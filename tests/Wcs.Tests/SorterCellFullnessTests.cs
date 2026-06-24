using System.Net;
using System.Net.Http.Json;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api;
using Wcs.Core;
using Wcs.Data;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// 소터 셀 만재 판정 (m4p4) — DestinationStatusService.ComputeSorter 실산출 검증
//
// 검증 대상: Phase 1이 하드코딩하던 소터 Full:false/Paused:false를 실제 산출로 대체.
//   - SorterFull = 그 소터 enabled 셀 중 미점유(활성 cell_assignment 없는) 셀 0개.
//   - paused     = destination.Status==PAUSED || IsActive==false.
//   - 두 소비자: ① IF-05 NG 상류 필터(piece-aware 오더 재사용 예외) ② 푸시 ready.
//
// 메타교훈(인메모리 GREEN ≠ 결함 없음): 인메모리 카운터가 아니라
//   "실 cell_assignment DB 상태"와 "가짜 RCS가 수신한 실제 JSON 본문"을 ground-truth로 단언.
//
// 시나리오(계약 §Verification Scenarios):
//   HP-1 빈셀 있음 + 정렬 → IF-05 OK·푸시 ready=true
//   HP-2 빈셀 0 + 오더 활성 assignment 보유 → IF-05 OK(재사용 예외)
//   HP-3 full→!full(셀 해제) → 푸시 ready=true 재푸시(전이당 1회)
//   EC-1 빈셀 0 + 오더 재사용 불가 → IF-05 NG(FULL)
//   EC-2 PAUSED / 비활성 → IF-05 NG(PAUSED)
//   EC-3 !full→full(마지막 빈셀 점유) → 푸시 ready=false 1건
//   EC-4 paused 단독 전이 → 푸시 ready=false 1건
//   EC-5 동시성 원자성 — 빈셀 배정/해제 중 모순 응답 0건
//   EC-6 회귀 — 빈셀 충분 + 미정렬 → ready=false(full/paused 아님, decision.Reason)
// ════════════════════════════════════════════════════════════════════════════

public class SorterCellFullnessTests
{
    private readonly ITestOutputHelper _out;
    public SorterCellFullnessTests(ITestOutputHelper output) => _out = output;

    // ── 헬퍼: 조건 폴링(RcsPushTests와 동형) ───────────────────────────────────
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs, string msg, int pollMs = 20)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTimeOffset.Now > deadline)
                Assert.Fail($"WaitUntil 타임아웃({timeoutMs}ms): {msg}");
            await Task.Delay(pollMs);
        }
    }

    private static async Task WaitUntilExactAsync(
        Func<int> countFunc, int expected, int stableCount,
        int timeoutMs, string msg, int pollMs = 30)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        int consecutive = 0;
        while (consecutive < stableCount)
        {
            if (DateTimeOffset.Now > deadline)
                Assert.Fail($"WaitUntilExact 타임아웃({timeoutMs}ms): {msg} (현재={countFunc()}, 기대={expected})");
            if (countFunc() == expected) consecutive++;
            else                         consecutive = 0;
            await Task.Delay(pollMs);
        }
    }

    private static async Task WaitForSnapshotAsync(
        PlcSnapshotSource src, Func<PlcSnapshot, bool> condition, int timeoutMs, int pollMs = 20)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        while (!condition(src()))
        {
            if (DateTimeOffset.Now > deadline) Assert.Fail($"WaitForSnapshot 타임아웃({timeoutMs}ms)");
            await Task.Delay(pollMs);
        }
    }
    private delegate PlcSnapshot PlcSnapshotSource();

    // ── 헬퍼: 셀 배정/해제(실 cell_assignment DB 조작) ─────────────────────────

    /// <summary>그 소터의 enabled 셀을 cnt개 점유(활성 cell_assignment 삽입). ORD-003 사용.</summary>
    private static void OccupyCells(WcsDbContext db, long sorterDestId, int cnt)
    {
        var order = db.Orders.First(o => o.DestinationId == sorterDestId && o.Status == OrderStatus.RUNNING);
        var occupied = db.CellAssignments
            .Where(a => a.Cell.DestinationId == sorterDestId && a.ReleasedAt == null)
            .Select(a => a.CellId)
            .ToHashSet();
        var freeCells = db.Cells
            .Where(c => c.DestinationId == sorterDestId && c.Enabled && !occupied.Contains(c.Id))
            .OrderBy(c => c.CellNo)
            .Take(cnt)
            .ToList();
        var now = DateTime.UtcNow;
        foreach (var cell in freeCells)
            db.CellAssignments.Add(new CellAssignment
            {
                CellId = cell.Id, OrderId = order.Id, AssignedAt = now, ReleasedAt = null, CreatedAt = now,
            });
        db.SaveChanges();
    }

    /// <summary>그 소터의 활성 cell_assignment 1건 해제(빈셀 1개 생성).</summary>
    private static void ReleaseOneCell(WcsDbContext db, long sorterDestId)
    {
        var assign = db.CellAssignments
            .Where(a => a.Cell.DestinationId == sorterDestId && a.ReleasedAt == null)
            .OrderBy(a => a.Id)
            .First();
        assign.ReleasedAt = DateTime.UtcNow;
        db.SaveChanges();
    }

    private static int FreeCellCount(WcsDbContext db, long sorterDestId)
    {
        var occupied = db.CellAssignments
            .Where(a => a.Cell.DestinationId == sorterDestId && a.ReleasedAt == null)
            .Select(a => a.CellId)
            .ToHashSet();
        return db.Cells.Count(c => c.DestinationId == sorterDestId && c.Enabled && !occupied.Contains(c.Id));
    }

    // ════════════════════════════════════════════════════════════════════════
    // HP-1 + EC-6: 빈셀 있음 + 정렬 → ready=true / 빈셀 충분 + 미정렬 → ready=false
    // Compute 직접 호출로 산출 정확성 검증(셀 3개 전부 미점유 → full=false).
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task HP1_EC6_Sorter_FreeCells_Compute_FullFalse_ReadyByAlignment()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        _ = factory.CreateClient();

        var status = factory.Services.GetRequiredService<IDestinationStatusService>();
        long sorterId = factory.SorterDestinationId;

        // 빈셀 3개(시드) — full=false 확인
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            Assert.Equal(3, FreeCellCount(db, sorterId));
        }

        // EC-6: 미정렬(CurFloor=1≠운영층2) → full/paused 아님, decision.Reason → ready=false
        var r0 = status.Compute(sorterId, DestType.SORTER_3D);
        Assert.False(r0.Full,   "빈셀 3개 → full=false");
        Assert.False(r0.Paused, "NORMAL·활성 → paused=false");
        Assert.False(r0.Ready,  "미정렬 → ready=false (decision.Reason)");
        Assert.NotEqual(DenyReason.Full, r0.Reason);
        Assert.NotEqual(DenyReason.Paused, r0.Reason);

        // HP-1: 운영층 정렬(CurFloor=2·Ready=1) → ready=true
        factory.FakeMaster.SetReady(true);
        factory.FakeMaster.SetCurFloor(2);
        factory.FakeMaster.SetTgtFloor(0);
        await WaitForSnapshotAsync(() => factory.FakePolling!.Latest,
            s => s.Online && s.CurFloor == 2 && s.Ready, 5000);

        var r1 = status.Compute(sorterId, DestType.SORTER_3D);
        Assert.False(r1.Full);
        Assert.False(r1.Paused);
        Assert.True(r1.Ready, "빈셀 있음 + 정렬 + Ready=1 → ready=true");
        _out.WriteLine("[HP-1/EC-6] 빈셀3 미정렬 ready=false → 정렬 후 ready=true, full=false 유지");
    }

    // ════════════════════════════════════════════════════════════════════════
    // EC-1: 소터 빈셀 0(전부 점유) + 오더 재사용 불가 → IF-05 NG(FULL), chuteNo=null
    // 그리고 Compute full=true·ready=false 동반 단언.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task EC1_Sorter_AllCellsOccupied_NoReuse_If05_Ng_Full()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        var client = factory.CreateClient();

        long sorterId = factory.SorterDestinationId;
        int  sorterChute = factory.SorterChuteNo;

        // 정렬 상태로(ready 재료) 만들되, 셀 3개 전부 점유(ORD-003)로 SorterFull.
        factory.FakeMaster.SetReady(true);
        factory.FakeMaster.SetCurFloor(2);
        await WaitForSnapshotAsync(() => factory.FakePolling!.Latest, s => s.Online && s.CurFloor == 2 && s.Ready, 5000);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            OccupyCells(db, sorterId, 3);
            Assert.Equal(0, FreeCellCount(db, sorterId));
        }

        var status = factory.Services.GetRequiredService<IDestinationStatusService>();
        var r = status.Compute(sorterId, DestType.SORTER_3D);
        Assert.True(r.Full,   "빈셀 0 → full=true");
        Assert.False(r.Ready, "full → ready=false");
        Assert.Equal(DenyReason.Full, r.Reason);

        // IF-05: ORD-003(TEST-BARCODE-3)은 점유 오더지만, 그 활성 assignment는 ORD-003 보유.
        // 재사용 예외를 정확히 치려면 "재사용 불가" 케이스가 필요 → 다른 바코드를 같은 소터에 임시 오더로.
        // 여기선 TEST-BARCODE-3가 점유 오더이므로 재사용 예외로 OK가 될 수 있다(HP-2에서 별도 검증).
        // EC-1의 "재사용 불가"는 그 소터에 활성 assignment가 없는 새 바코드여야 한다.
        // → 소터에 매핑된 별도 오더(다른 바코드)를 삽입해 재사용 불가 piece를 만든다.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            var batch = db.WorkBatches.First();
            var sorterDest = db.Destinations.First(d => d.Id == sorterId);
            var order = new WcsOrder
            {
                WorkBatchId = batch.Id, OrderNo = "ORD-SORTER-OTHER", OrderType = OrderType.GENERAL,
                DestinationId = sorterId, DestAssignType = DestAssignType.UPSTREAM, DestAssignedAt = DateTime.UtcNow,
                Status = OrderStatus.RUNNING, StartedAt = DateTime.UtcNow,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.Orders.Add(order);
            db.SaveChanges();
            db.OrderItems.Add(new OrderItem
            {
                OrderId = order.Id, Barcode = "SORTER-OTHER-BC", PlannedQty = 10, ReservedQty = 0, SortedQty = 0,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();

            // 그 새 오더는 활성 assignment 없음 → 재사용 불가 확인
            var st = factory.Services.GetRequiredService<IDestinationStatusService>();
            Assert.False(st.SorterHasActiveAssignmentForBarcode(sorterId, "SORTER-OTHER-BC"),
                "새 오더는 활성 cell_assignment 없음 → 재사용 불가");
        }

        var if05Req = new { pId = 21001, agvNo = 1, barcode = "SORTER-OTHER-BC", inductionNo = 1, qty = 1, timeStamp = (string?)null };
        var resp = await client.PostAsJsonAsync("/api/v1/destination-query", if05Req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);  // 도메인 거부 = 200 + NG
        var body = await resp.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.NotNull(body);
        Assert.Equal("NG", body.Result);
        Assert.Null(body.ChuteNo);

        // piece_event 내부 reason=FULL 단언(ground-truth)
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            var piece = db.Pieces.First(p => p.PId == 21001 && p.IsActive);
            var ev = db.PieceEvents.Where(e => e.PieceId == piece.Id && e.EventType == PieceEventType.IF05_RES).First();
            Assert.Equal("FULL", ev.Reason);
        }
        _out.WriteLine($"[EC-1] 소터(chute={sorterChute}) 빈셀0 + 재사용불가 → IF-05 NG·chuteNo=null·reason(내부)=FULL");
    }

    // ════════════════════════════════════════════════════════════════════════
    // HP-2: 소터 빈셀 0이지만 그 piece의 오더가 활성 cell_assignment 보유 → IF-05 OK
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task HP2_Sorter_Full_ButOrderHasActiveAssignment_If05_Ok()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        var client = factory.CreateClient();

        long sorterId = factory.SorterDestinationId;

        // ORD-003(TEST-BARCODE-3)이 3셀 전부 점유(빈셀 0). 그러나 그 오더가 활성 assignment 보유.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            OccupyCells(db, sorterId, 3);
            Assert.Equal(0, FreeCellCount(db, sorterId));

            var st = factory.Services.GetRequiredService<IDestinationStatusService>();
            Assert.True(st.SorterHasActiveAssignmentForBarcode(sorterId, "TEST-BARCODE-3"),
                "ORD-003은 활성 cell_assignment 보유 → 재사용 가능");
        }

        // Compute(목적지 단위)는 여전히 Full=true(새 오더 수용 불가) — IF-05만 예외 적용.
        var status = factory.Services.GetRequiredService<IDestinationStatusService>();
        Assert.True(status.Compute(sorterId, DestType.SORTER_3D).Full, "목적지 단위는 여전히 full=true");

        // IF-05: TEST-BARCODE-3 → 재사용 예외로 OK·chuteNo=30
        var if05Req = new { pId = 22001, agvNo = 1, barcode = "TEST-BARCODE-3", inductionNo = 1, qty = 1, timeStamp = (string?)null };
        var resp = await client.PostAsJsonAsync("/api/v1/destination-query", if05Req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.NotNull(body);
        Assert.Equal("OK", body.Result);
        Assert.Equal(factory.SorterChuteNo, body.ChuteNo);

        // piece_event reason=NORMAL(OK 경로)
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            var piece = db.Pieces.First(p => p.PId == 22001 && p.IsActive);
            var ev = db.PieceEvents.Where(e => e.PieceId == piece.Id && e.EventType == PieceEventType.IF05_RES).First();
            Assert.Equal("NORMAL", ev.Reason);
            Assert.Equal(PieceStatus.RESERVED, piece.Status);
        }
        _out.WriteLine("[HP-2] 빈셀0 + ORD-003 활성 assignment 보유 → IF-05 OK(재사용 예외)·reason=NORMAL");
    }

    // ════════════════════════════════════════════════════════════════════════
    // EC-2: 소터 PAUSED / 비활성 → IF-05 NG(PAUSED). 두 케이스 각각 단언.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task EC2_Sorter_Paused_And_Inactive_If05_Ng_Paused()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        var client = factory.CreateClient();

        long sorterId = factory.SorterDestinationId;
        var status = factory.Services.GetRequiredService<IDestinationStatusService>();

        // 소터 Online 확보 — Offline(최우선)이 Paused를 가리지 않도록(DenyReason 우선순위 Offline>Paused).
        await WaitForSnapshotAsync(() => factory.FakePolling!.Latest, s => s.Online, 5000);

        // ── 케이스 A: Status=PAUSED ──
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            var dest = db.Destinations.First(d => d.Id == sorterId);
            dest.Status = DestStatus.PAUSED;
            db.SaveChanges();
        }
        var rPaused = status.Compute(sorterId, DestType.SORTER_3D);
        Assert.True(rPaused.Paused, "Status=PAUSED → paused=true");
        Assert.False(rPaused.Ready);
        Assert.Equal(DenyReason.Paused, rPaused.Reason);

        var respA = await client.PostAsJsonAsync("/api/v1/destination-query",
            new { pId = 23001, agvNo = 1, barcode = "TEST-BARCODE-3", inductionNo = 1, qty = 1, timeStamp = (string?)null });
        Assert.Equal(HttpStatusCode.OK, respA.StatusCode);
        var bodyA = await respA.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.Equal("NG", bodyA!.Result);
        Assert.Null(bodyA.ChuteNo);

        // ── 케이스 B: IsActive=false (PAUSED 해제 후 비활성) ──
        // 단, IF-05 QueryDestination은 dest.IsActive==false면 availability 이전에 NO_DEST로 차단한다.
        // 따라서 ComputeSorter의 비활성→paused 매핑은 Compute 직접 호출로 단언한다(availability 산출원 정확성).
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            var dest = db.Destinations.First(d => d.Id == sorterId);
            dest.Status   = DestStatus.NORMAL;
            dest.IsActive = false;
            db.SaveChanges();
        }
        var rInactive = status.Compute(sorterId, DestType.SORTER_3D);
        Assert.True(rInactive.Paused, "IsActive=false → paused=true (ComputeSorter)");
        Assert.False(rInactive.Ready);
        Assert.Equal(DenyReason.Paused, rInactive.Reason);

        _out.WriteLine("[EC-2] PAUSED → IF-05 NG·Compute paused=true / 비활성 → Compute paused=true");
    }

    // ════════════════════════════════════════════════════════════════════════
    // HP-3 + EC-3: 푸시 전이 — !full→full(ready=false 1건) → full→!full(ready=true 1건)
    // 정렬 상태에서 셀 점유/해제로 full↔!full 전이 → 가짜 RCS가 전이당 정확히 1건 수신.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task EC3_HP3_Sorter_FullTransition_PushReadyFalse_ThenReleaseTrue()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        _ = factory.CreateClient();

        long sorterId    = factory.SorterDestinationId;
        int  sorterChute = factory.SorterChuteNo;

        // 정렬 완료 + 빈셀 있음 → ready=true. 부트스트랩 ready=false(미정렬)에서 정렬로 1전이.
        factory.FakeMaster.SetReady(true);
        factory.FakeMaster.SetCurFloor(2);
        factory.FakeMaster.SetTgtFloor(0);
        await WaitForSnapshotAsync(() => factory.FakePolling!.Latest, s => s.Online && s.CurFloor == 2 && s.Ready, 5000);

        // 정렬 후 ready=true 도달(최신 수신이 ready=true가 될 때까지) + 안정.
        await WaitUntilAsync(() => rcs.LastFor(sorterChute) is { Ready: true }, 6000, "정렬 후 소터 ready=true 푸시");
        int baseAligned = rcs.CountFor(sorterChute);
        await WaitUntilExactAsync(() => rcs.CountFor(sorterChute), baseAligned, stableCount: 6, timeoutMs: 4000,
            "정렬 후 무변화 안정(폭주 0)");

        // ── EC-3: !full→full — 셀 3개 전부 점유(빈셀 0) → 관찰 타이머가 full 전이 감지 → ready=false 1건 ──
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            OccupyCells(db, sorterId, 3);
            Assert.Equal(0, FreeCellCount(db, sorterId));
        }
        await WaitUntilAsync(() => rcs.LastFor(sorterChute) is { Ready: false }, 5000, "full 전이 → ready=false 푸시");
        int afterFull = baseAligned + 1;
        await WaitUntilExactAsync(() => rcs.CountFor(sorterChute), afterFull, stableCount: 6, timeoutMs: 4000,
            "full 전이 후 정확히 1건(중복 0·무변화 폴 폭주 0)");
        Assert.False(rcs.LastFor(sorterChute)!.Ready, "빈셀 0 → ready=false");

        // ── HP-3: full→!full — 셀 1개 해제 → 관찰 타이머가 !full 전이 감지 → ready=true 1건 ──
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            ReleaseOneCell(db, sorterId);
            Assert.Equal(1, FreeCellCount(db, sorterId));
        }
        await WaitUntilAsync(() => rcs.LastFor(sorterChute) is { Ready: true }, 5000, "!full 전이 → ready=true 재푸시");
        int afterRelease = afterFull + 1;
        await WaitUntilExactAsync(() => rcs.CountFor(sorterChute), afterRelease, stableCount: 6, timeoutMs: 4000,
            "!full 전이 후 정확히 1건(전이당 1회)");
        Assert.True(rcs.LastFor(sorterChute)!.Ready, "빈셀 1 생김 → ready=true");

        _out.WriteLine($"[EC-3/HP-3] 정렬 ready=true({baseAligned}) → full ready=false({afterFull}) → 해제 ready=true({afterRelease}) — 전이당 1건");
    }

    // ════════════════════════════════════════════════════════════════════════
    // EC-4: paused 단독 전이 — NORMAL(ready=true) → Status PAUSED → ready=false 1건
    // full과 독립적으로 paused 단독 전이를 검증.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task EC4_Sorter_PausedTransition_PushReadyFalse()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        _ = factory.CreateClient();

        long sorterId    = factory.SorterDestinationId;
        int  sorterChute = factory.SorterChuteNo;

        // 정렬 + 빈셀 있음 → ready=true.
        factory.FakeMaster.SetReady(true);
        factory.FakeMaster.SetCurFloor(2);
        factory.FakeMaster.SetTgtFloor(0);
        await WaitForSnapshotAsync(() => factory.FakePolling!.Latest, s => s.Online && s.CurFloor == 2 && s.Ready, 5000);
        await WaitUntilAsync(() => rcs.LastFor(sorterChute) is { Ready: true }, 6000, "정렬 후 ready=true");
        int baseAligned = rcs.CountFor(sorterChute);
        await WaitUntilExactAsync(() => rcs.CountFor(sorterChute), baseAligned, stableCount: 6, timeoutMs: 4000, "정렬 안정");

        // paused 전이: Status=PAUSED → ready=false 1건(셀 변화 없음 — full과 독립).
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            db.Destinations.First(d => d.Id == sorterId).Status = DestStatus.PAUSED;
            db.SaveChanges();
        }
        await WaitUntilAsync(() => rcs.LastFor(sorterChute) is { Ready: false }, 5000, "paused 전이 → ready=false 푸시");
        int afterPaused = baseAligned + 1;
        await WaitUntilExactAsync(() => rcs.CountFor(sorterChute), afterPaused, stableCount: 6, timeoutMs: 4000,
            "paused 단독 전이 정확히 1건");
        Assert.False(rcs.LastFor(sorterChute)!.Ready, "PAUSED → ready=false");
        _out.WriteLine($"[EC-4] paused 단독 전이 ready=false 1건(총 {afterPaused}건)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // EC-5: 동시성 원자성 — 셀 배정/해제 동시 다수 + Compute 호출 동안
    //   "빈셀 0인데 ready=true" 또는 "빈셀 ≥1인데 full=true(ready 모순)" 응답 단 한 건도 없음.
    // 단일 원자 쿼리(check-then-act 분리 없음) 검증. 전이 푸시는 최종 상태로 수렴(누락 0).
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task EC5_Sorter_ConcurrentAssignRelease_AtomicReadCompute_NoContradiction()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        _ = factory.CreateClient();

        long sorterId = factory.SorterDestinationId;
        var status    = factory.Services.GetRequiredService<IDestinationStatusService>();
        var scopeFactory = factory.Services.GetRequiredService<IServiceScopeFactory>();

        // 정렬 상태(ready 재료) — full만이 ready를 좌우하도록.
        factory.FakeMaster.SetReady(true);
        factory.FakeMaster.SetCurFloor(2);
        await WaitForSnapshotAsync(() => factory.FakePolling!.Latest, s => s.Online && s.CurFloor == 2 && s.Ready, 5000);

        long orderId;
        List<long> cellIds;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            orderId = db.Orders.First(o => o.DestinationId == sorterId && o.Status == OrderStatus.RUNNING).Id;
            cellIds = db.Cells.Where(c => c.DestinationId == sorterId && c.Enabled).OrderBy(c => c.CellNo)
                              .Select(c => c.Id).ToList();
        }
        Assert.Equal(3, cellIds.Count);

        using var cts = new CancellationTokenSource();
        int contradictions = 0;
        int observations  = 0;

        // 관찰자: Compute를 빠르게 반복 호출하며 **단일 Compute 결과 내부 불변식**을 검사.
        //   - full ⟹ !ready  (만재면 절대 ready=true가 새면 안 됨 — 계약 "빈셀0인데 ready=true" 금지)
        //   - ready ⟹ !full && !paused && online  (ready 합성 정합)
        //   이 불변식들은 한 Compute 호출이 산출한 record 내부에서 성립해야 한다. 만약 full을
        //   check-then-act(셀 조회 후 별도 시점 ready 결정)로 합성하면 동시 배정/해제 churn 중
        //   "빈셀0 관측 → 그사이 해제 → ready=true 합성"이 새어 full&&ready가 발생할 수 있다.
        //   단일 원자 쿼리 + record 동시 산출이면 0건. (별도 free-count 재조회와의 비교는
        //   읽기 시점 차로 인한 위양성이므로 쓰지 않는다 — 메타교훈: 진성 불변식만.)
        var observer = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                DestinationReadiness r;
                try
                {
                    r = status.Compute(sorterId, DestType.SORTER_3D);
                }
                catch
                {
                    // in-memory SQLite 단일 writer 경합(SQLITE_BUSY) — 다음 관찰에서 재평가
                    // (Pusher.Observe 흡수 패턴과 동형). 불변식 검사는 성공한 산출에만 적용.
                    await Task.Delay(1);
                    continue;
                }
                Interlocked.Increment(ref observations);

                bool bad = (r.Full && r.Ready)                        // 만재인데 ready (금지·핵심)
                        || (r.Ready && (r.Full || r.Paused || !r.Online)); // ready 합성 모순
                if (bad) Interlocked.Increment(ref contradictions);
                await Task.Delay(1);
            }
        });

        // 배정/해제를 동시 다수 스레드가 토글 — released_at IS NULL 부분유니크 일관성.
        const int writers = 6;
        var writerTasks = Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            var rnd = new Random(w * 17 + 1);
            for (int i = 0; i < 40; i++)
            {
                int cellIdx = rnd.Next(cellIds.Count);
                long cellId = cellIds[cellIdx];
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
                    // 토글: 활성이면 해제, 아니면 배정(부분유니크 위반 시 흡수).
                    var active = db.CellAssignments.FirstOrDefault(a => a.CellId == cellId && a.ReleasedAt == null);
                    if (active is not null)
                    {
                        active.ReleasedAt = DateTime.UtcNow;
                        db.SaveChanges();
                    }
                    else
                    {
                        db.CellAssignments.Add(new CellAssignment
                        {
                            CellId = cellId, OrderId = orderId, AssignedAt = DateTime.UtcNow,
                            ReleasedAt = null, CreatedAt = DateTime.UtcNow,
                        });
                        db.SaveChanges();
                    }
                }
                catch { /* 동시 토글 경합(유니크/업데이트 충돌)은 다음 라운드에서 재평가 — eventually consistent */ }
                await Task.Delay(rnd.Next(1, 4));
            }
        })).ToArray();

        await Task.WhenAll(writerTasks);
        cts.Cancel();
        await observer;

        Assert.True(observations > 0, "관찰자가 Compute를 최소 1회 실행");
        Assert.Equal(0, contradictions);

        // 정지(quiesce) 후 결정적 검증: full ⟺ 빈셀0 등가성(누락 0 — 최종 일관성).
        // ① 전부 점유 → 빈셀0 → full=true·ready=false
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            OccupyCells(db, sorterId, 3);
            Assert.Equal(0, FreeCellCount(db, sorterId));
        }
        var rFull = status.Compute(sorterId, DestType.SORTER_3D);
        Assert.True(rFull.Full,   "전부 점유(빈셀0) → full=true 수렴");
        Assert.False(rFull.Ready, "full → ready=false");

        // ② 전부 해제 → 빈셀3 → full=false
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            foreach (var a in db.CellAssignments.Where(a => a.ReleasedAt == null).ToList())
                a.ReleasedAt = DateTime.UtcNow;
            db.SaveChanges();
            Assert.Equal(3, FreeCellCount(db, sorterId));
        }
        var rFree = status.Compute(sorterId, DestType.SORTER_3D);
        Assert.False(rFree.Full, "전부 해제(빈셀3) → full=false 수렴");

        _out.WriteLine($"[EC-5] {writers}스레드 동시 배정/해제 + Compute {observations}회 — 내부 모순 {contradictions}건. quiesce full⟺빈셀0 등가성 확인");
    }
}
