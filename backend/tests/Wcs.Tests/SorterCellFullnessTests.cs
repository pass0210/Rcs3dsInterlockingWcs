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
// 소터 셀 작업수량 full 판정 — DestinationStatusService 실산출 검증(이 스프린트)
//
// m4p4 "빈 셀 없음=full"을 **셀 작업 투입 수량 기반**으로 정밀화한 결과를 검증한다.
//   - 셀 현재 투입 수량 = sorter_command(status=COMPLETED) JOIN piece.qty 합(piece별 1건·확정2).
//   - 셀 작업 투입 수량 = cell.Capacity(재활용). NULL/≤0 = 무제한(수량-full 미적용·확정3).
//   - SorterFull(목적지 단위·확정1) = 빈 enabled 셀 없음 AND 모든 활성 배정 셀이 작업수량 도달.
//   - IF-05 piece-aware(확정1) = 오더 배정 셀 보유 AND 그 셀 여유면 OK / 전부 도달이면 NG(FULL).
//
// 메타교훈(인메모리 GREEN ≠ 결함 없음): 인메모리 카운터가 아니라
//   "실 sorter_command/cell.Capacity DB 상태"와 "가짜 RCS가 수신한 실제 JSON 본문"을
//   ground-truth로 단언한다.
//
// 시나리오(계약 §Verification Scenarios HP-1~5 · EC-1~7):
//   HP-1 오더 배정 셀 여유(현재<작업) → IF-05 OK·reason=NORMAL
//   HP-2 새 오더 빈 셀 → IF-05 OK (m4p4 free-cell 회귀 가드)
//   HP-5 빈셀0 + 일부 배정 셀 여유 → push ready=true 유지·Compute Full=false
//   EC-1 오더 배정 셀 작업수량 도달 + 빈셀0 → IF-05 NG(FULL)·reason=FULL
//   EC-2 새 오더 빈셀0 → IF-05 NG(FULL) (m4p4 회귀 가드)
//   EC-3 소터 PAUSED → IF-05 NG (소터 불변 — (B) 정정이 소터를 깨지 않음)
//   EC-4 cell.Capacity NULL=무제한 → 현재수량 아무리 많아도 수량-full 미적용
//   EC-5 동시성 원자성 — 적재/배정 churn 중 모순 응답(셀full⟹OK / 여유⟹NG) 0건
//   EC-6 셀 경계값 — Capacity-1=OK / Capacity=NG / Capacity+1=NG (≥ 등호)
//   EC-7 push ready=false 전이 — 마지막 여유 셀이 작업수량 도달 → ready=false 1건 → 복귀 재푸시
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

    private static int FreeCellCount(WcsDbContext db, long sorterDestId)
    {
        var occupied = db.CellAssignments
            .Where(a => a.Cell.DestinationId == sorterDestId && a.ReleasedAt == null)
            .Select(a => a.CellId)
            .ToHashSet();
        return db.Cells.Count(c => c.DestinationId == sorterDestId && c.Enabled && !occupied.Contains(c.Id));
    }

    // ── 헬퍼: 셀 작업수량(cell.Capacity) 설정 + 셀 현재수량(sorter_command COMPLETED) 적재 ──

    /// <summary>그 소터의 모든 enabled 셀 작업수량(cell.Capacity)을 cap으로 설정(양수=수량-full 활성).</summary>
    private static void SetAllCapacities(WcsDbContext db, long sorterDestId, int? cap)
    {
        foreach (var c in db.Cells.Where(c => c.DestinationId == sorterDestId).ToList())
            c.Capacity = cap;
        db.SaveChanges();
    }

    /// <summary>
    /// 그 셀(cellNo)에 qty짜리 piece를 적재 — piece + COMPLETED sorter_command 1건 삽입.
    /// 셀 현재 투입 수량은 sorter_command(COMPLETED) JOIN piece.qty 합으로 산출되므로
    /// 이 헬퍼가 "실 sorter_command/piece DB 상태" ground-truth를 구성한다(인메모리 카운터 아님).
    /// </summary>
    private static void LoadCellQty(WcsDbContext db, long sorterDestId, int cellNo, int qty, int pId, string barcode)
    {
        var now  = DateTime.UtcNow;
        var cell = db.Cells.First(c => c.DestinationId == sorterDestId && c.CellNo == cellNo);

        var piece = new Piece
        {
            PId           = pId,
            IsActive      = true,
            Barcode       = barcode,
            Qty           = qty,
            DepositedAt   = now,
            DestinationId = sorterDestId,
            Status        = PieceStatus.LOADED,
            CreatedAt     = now,
            UpdatedAt     = now,
        };
        db.Pieces.Add(piece);
        db.SaveChanges();

        db.SorterCommands.Add(new SorterCommand
        {
            PieceId    = piece.Id,
            CellId     = cell.Id,
            CSeq       = 1,
            CellNo     = cellNo,
            CWrittenAt = now,
            RSeq       = 1,
            RCellNo    = cellNo,
            RFlagAt    = now,
            Status     = SorterCommandStatus.COMPLETED,
            CreatedAt  = now,
        });
        db.SaveChanges();
    }

    /// <summary>그 소터에 매핑된 별도 오더(새 바코드) + 빈 배정 셀 1개를 만든다. EC-1·HP-1용.</summary>
    private static (long orderId, long cellId) AddSorterOrderWithAssignedCell(
        WcsDbContext db, long sorterDestId, string orderNo, string barcode, int cellNo)
    {
        var now   = DateTime.UtcNow;
        var batch = db.WorkBatches.First();
        var order = new WcsOrder
        {
            WorkBatchId = batch.Id, OrderNo = orderNo, OrderType = OrderType.GENERAL,
            DestinationId = sorterDestId, DestAssignType = DestAssignType.UPSTREAM, DestAssignedAt = now,
            Status = OrderStatus.RUNNING, StartedAt = now, CreatedAt = now, UpdatedAt = now,
        };
        db.Orders.Add(order);
        db.SaveChanges();
        db.OrderItems.Add(new OrderItem
        {
            OrderId = order.Id, Barcode = barcode, PlannedQty = 100, ReservedQty = 0, SortedQty = 0,
            CreatedAt = now, UpdatedAt = now,
        });
        db.SaveChanges();

        var cell = db.Cells.First(c => c.DestinationId == sorterDestId && c.CellNo == cellNo);
        db.CellAssignments.Add(new CellAssignment
        {
            CellId = cell.Id, OrderId = order.Id, AssignedAt = now, ReleasedAt = null, CreatedAt = now,
        });
        db.SaveChanges();
        return (order.Id, cell.Id);
    }

    // ════════════════════════════════════════════════════════════════════════
    // HP-1: 소터 오더 배정 셀 여유(현재<작업) → IF-05 OK·reason=NORMAL
    //   Capacity=10, 현재=3(LOADED) → 그 오더 piece OK. 빈셀 0(전부 점유) 상황에서도 자기 셀 누적.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task HP1_Sorter_AssignedCellHasRoom_If05_Ok_Normal()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        var client = factory.CreateClient();

        long sorterId = factory.SorterDestinationId;
        var status    = factory.Services.GetRequiredService<IDestinationStatusService>();

        // ORD-003(TEST-BARCODE-3)이 3셀 전부 점유(빈셀 0). 그 배정 셀 중 cellNo=1에 현재=3 적재, Capacity=10.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            SetAllCapacities(db, sorterId, 10);
            OccupyCells(db, sorterId, 3);
            Assert.Equal(0, FreeCellCount(db, sorterId));
            LoadCellQty(db, sorterId, cellNo: 1, qty: 3, pId: 21100, barcode: "TEST-BARCODE-3");

            Assert.True(status.SorterHasAssignedCellWithRoomForBarcode(sorterId, "TEST-BARCODE-3"),
                "현재=3 < Capacity=10 → 여유 셀 보유 → 재사용 예외 적용 가능");
        }

        // 빈셀0이지만 배정 셀(전부 Capacity=10, 현재 0~3) 작업수량 미달 → 목적지 Full=false.
        Assert.False(status.Compute(sorterId, DestType.SORTER_3D).Full,
            "배정 셀 작업수량 미달 → SorterFull=false");

        var if05Req = new { pId = 21101, agvNo = 1, barcode = "TEST-BARCODE-3", inductionNo = 1, qty = 1, timeStamp = (string?)null };
        var resp = await client.PostAsJsonAsync("/api/v1/destination-query", if05Req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.NotNull(body);
        Assert.Equal("OK", body.Result);
        Assert.Equal(factory.SorterChuteNo, body.ChuteNo);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            var piece = db.Pieces.First(p => p.PId == 21101 && p.IsActive);
            var ev = db.PieceEvents.First(e => e.PieceId == piece.Id && e.EventType == PieceEventType.IF05_RES);
            Assert.Equal("NORMAL", ev.Reason);
        }
        _out.WriteLine("[HP-1] 배정 셀 여유(현재3<작업10) → IF-05 OK·reason=NORMAL");
    }

    // ════════════════════════════════════════════════════════════════════════
    // HP-2: 새 오더(셀 미보유) + 빈 enabled 셀 ≥1 → IF-05 OK (m4p4 free-cell 회귀 가드)
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task HP2_Sorter_NewOrder_FreeCellAvailable_If05_Ok()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        var client = factory.CreateClient();

        long sorterId = factory.SorterDestinationId;

        // 빈 셀 3개(시드) — Capacity 양수로 설정해도 빈 셀이면 새 오더 수용.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            SetAllCapacities(db, sorterId, 10);
            Assert.Equal(3, FreeCellCount(db, sorterId));
        }

        var status = factory.Services.GetRequiredService<IDestinationStatusService>();
        Assert.False(status.Compute(sorterId, DestType.SORTER_3D).Full, "빈 셀 ≥1 → SorterFull=false");

        // TEST-BARCODE-3(ORD-003)은 셀 미보유 — 빈 셀로 수용.
        var if05Req = new { pId = 22001, agvNo = 1, barcode = "TEST-BARCODE-3", inductionNo = 1, qty = 1, timeStamp = (string?)null };
        var resp = await client.PostAsJsonAsync("/api/v1/destination-query", if05Req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.Equal("OK", body!.Result);
        Assert.Equal(factory.SorterChuteNo, body.ChuteNo);
        _out.WriteLine("[HP-2] 새 오더 + 빈 셀 ≥1 → IF-05 OK(free-cell 회귀 가드)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // EC-1: 오더 배정 셀 작업수량 도달(현재≥작업) + 빈셀0 → IF-05 NG(FULL)·reason=FULL
    //   Capacity=5, 현재=5(LOADED), 빈셀0. 그 오더(SORTER-FULL-BC)의 유일 배정 셀이 도달 → NG.
    //   [S-소터push운영상태 정정] 만재(SorterFull)는 더 이상 push ready를 false로 만들지 않는다.
    //   운영상태가 OK(online·정렬·Ready=1)이면 r.Ready=true·Reason=None이고, full은 r.Full로만 보존.
    //   IF-05 dispatch는 r.Ready가 아니라 SorterCanAcceptBarcode를 보므로 NG·reason=FULL은 불변(회귀 가드).
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task EC1_Sorter_AssignedCellAtCapacity_If05_Ng_Full()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        var client = factory.CreateClient();

        long sorterId = factory.SorterDestinationId;
        var status    = factory.Services.GetRequiredService<IDestinationStatusService>();

        // 셋업: 별도 오더(SORTER-FULL-BC)를 cellNo=1에 배정. ORD-003이 나머지 2셀 점유 → 빈셀 0.
        //   cellNo=1 Capacity=5, 현재=5(작업수량 도달). 나머지 셀들도 도달시켜 SorterFull.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            SetAllCapacities(db, sorterId, 5);

            // cellNo=1 → 별도 오더(SORTER-FULL-BC) 배정 + 현재=5 적재(도달)
            AddSorterOrderWithAssignedCell(db, sorterId, "ORD-SORTER-FULL", "SORTER-FULL-BC", cellNo: 1);
            LoadCellQty(db, sorterId, cellNo: 1, qty: 5, pId: 23000, barcode: "SORTER-FULL-BC");

            // cellNo 2·3 → ORD-003 점유 + 각 현재=5 적재(도달) → 빈 셀 0 AND 전 배정 셀 작업수량 도달.
            OccupyCells(db, sorterId, 2);
            LoadCellQty(db, sorterId, cellNo: 2, qty: 5, pId: 23001, barcode: "TEST-BARCODE-3");
            LoadCellQty(db, sorterId, cellNo: 3, qty: 5, pId: 23002, barcode: "TEST-BARCODE-3");
            Assert.Equal(0, FreeCellCount(db, sorterId));

            Assert.False(status.SorterHasAssignedCellWithRoomForBarcode(sorterId, "SORTER-FULL-BC"),
                "SORTER-FULL-BC의 배정 셀(cellNo=1) 현재5 ≥ 작업5 → 여유 없음");
        }

        // 정렬 상태로(ready 재료) — full만이 ready를 좌우.
        factory.FakeMaster.SetReady(true);
        factory.FakeMaster.SetCurFloor(2);
        await WaitForSnapshotAsync(() => factory.FakePolling!.Latest, s => s.Online && s.CurFloor == 2 && s.Ready, 5000);

        var r = status.Compute(sorterId, DestType.SORTER_3D);
        Assert.True(r.Full,   "빈셀0 + 전 배정 셀 작업수량 도달 → SorterFull=true(산출 유지)");
        // [정정] 만재는 push ready에 영향 없음 — 운영상태 OK(online·정렬·Ready=1)이므로 ready=true·Reason=None.
        Assert.True(r.Ready,  "만재여도 운영상태 OK → push ready=true(S-소터push운영상태)");
        Assert.Equal(DenyReason.None, r.Reason);

        var if05Req = new { pId = 23100, agvNo = 1, barcode = "SORTER-FULL-BC", inductionNo = 1, qty = 1, timeStamp = (string?)null };
        var resp = await client.PostAsJsonAsync("/api/v1/destination-query", if05Req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);  // 도메인 거부 = 200 + NG
        var body = await resp.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.Equal("NG", body!.Result);
        Assert.Null(body.ChuteNo);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            var piece = db.Pieces.First(p => p.PId == 23100 && p.IsActive);
            var ev = db.PieceEvents.First(e => e.PieceId == piece.Id && e.EventType == PieceEventType.IF05_RES);
            Assert.Equal("FULL", ev.Reason);
        }
        _out.WriteLine("[EC-1] 배정 셀 작업수량 도달(현재5≥작업5)+빈셀0 → IF-05 NG·reason=FULL");
    }

    // ════════════════════════════════════════════════════════════════════════
    // EC-2: 새 오더 + 빈셀 0 → IF-05 NG(FULL) (m4p4 회귀 가드)
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task EC2_Sorter_NewOrder_NoFreeCell_If05_Ng_Full()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        var client = factory.CreateClient();

        long sorterId = factory.SorterDestinationId;
        var status    = factory.Services.GetRequiredService<IDestinationStatusService>();

        // 셀 3개 전부 점유(ORD-003) + 각 작업수량 도달 → 빈셀0·전 배정 셀 도달 → SorterFull.
        // 새 바코드(셀 미보유, 그러나 이 소터에 매핑된 별도 오더) → 재사용 예외 불가(배정 셀 없음) → NG.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            SetAllCapacities(db, sorterId, 5);
            OccupyCells(db, sorterId, 3);
            LoadCellQty(db, sorterId, cellNo: 1, qty: 5, pId: 24001, barcode: "TEST-BARCODE-3");
            LoadCellQty(db, sorterId, cellNo: 2, qty: 5, pId: 24002, barcode: "TEST-BARCODE-3");
            LoadCellQty(db, sorterId, cellNo: 3, qty: 5, pId: 24003, barcode: "TEST-BARCODE-3");
            Assert.Equal(0, FreeCellCount(db, sorterId));

            // 별도 오더(셀 미배정) — 새 오더로서 빈 셀이 필요하나 빈 셀 0 → NG.
            var batch = db.WorkBatches.First();
            var order = new WcsOrder
            {
                WorkBatchId = batch.Id, OrderNo = "ORD-SORTER-NEW", OrderType = OrderType.GENERAL,
                DestinationId = sorterId, DestAssignType = DestAssignType.UPSTREAM, DestAssignedAt = DateTime.UtcNow,
                Status = OrderStatus.RUNNING, StartedAt = DateTime.UtcNow, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.Orders.Add(order);
            db.SaveChanges();
            db.OrderItems.Add(new OrderItem
            {
                OrderId = order.Id, Barcode = "SORTER-NEW-BC", PlannedQty = 100, ReservedQty = 0, SortedQty = 0,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }

        // 새 오더 — 배정 셀 없음 → 재사용 예외 불가.
        Assert.False(status.SorterHasAssignedCellWithRoomForBarcode(sorterId, "SORTER-NEW-BC"),
            "SORTER-NEW-BC는 배정 셀 없음 → 재사용 예외 불가");
        Assert.True(status.Compute(sorterId, DestType.SORTER_3D).Full, "빈셀0 + 전 배정 셀 도달 → Full");

        var if05Req = new { pId = 24100, agvNo = 1, barcode = "SORTER-NEW-BC", inductionNo = 1, qty = 1, timeStamp = (string?)null };
        var resp = await client.PostAsJsonAsync("/api/v1/destination-query", if05Req);
        var body = await resp.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.Equal("NG", body!.Result);
        Assert.Null(body.ChuteNo);
        _out.WriteLine("[EC-2] 빈셀0 + 전 배정 셀 도달 → 새 오더 IF-05 NG(FULL) — m4p4 회귀 가드");
    }

    // ════════════════════════════════════════════════════════════════════════
    // EC-3: 소터 PAUSED → IF-05 NG (IF-05 dispatch는 r.Paused를 소비 — 소터 불변).
    //   [S-소터push운영상태 정정] Paused는 산출(r.Paused 필드)로 유지되지만 push ready 합성에선 제외.
    //   운영상태 OK(정렬)로 만들어 r.Ready=true·Reason=None이어도 IF-05는 r.Paused로 NG 차단함을 입증
    //   (Reason은 이제 운영상태 사유만 보존 — 이전 DenyReason.Paused 단언은 새 모델로 정정).
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task EC3_Sorter_Paused_If05_Ng_Unchanged()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        var client = factory.CreateClient();

        long sorterId = factory.SorterDestinationId;
        var status    = factory.Services.GetRequiredService<IDestinationStatusService>();

        // 운영상태 정렬(online·CurFloor=2·Ready=1) — paused여도 push ready=true임을 함께 확인.
        factory.FakeMaster.SetReady(true);
        factory.FakeMaster.SetCurFloor(2);
        factory.FakeMaster.SetTgtFloor(0);
        await WaitForSnapshotAsync(() => factory.FakePolling!.Latest, s => s.Online && s.CurFloor == 2 && s.Ready, 5000);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            db.Destinations.First(d => d.Id == sorterId).Status = DestStatus.PAUSED;
            db.SaveChanges();
        }
        var rPaused = status.Compute(sorterId, DestType.SORTER_3D);
        Assert.True(rPaused.Paused, "PAUSED → Paused=true(산출 유지)");
        // [정정] push ready는 운영상태만 보므로 paused여도 ready=true·Reason=None(운영상태 OK).
        Assert.True(rPaused.Ready, "paused여도 운영상태 OK → push ready=true");
        Assert.Equal(DenyReason.None, rPaused.Reason);

        // 그러나 IF-05 dispatch는 r.Paused를 소비해 NG로 차단(소터 paused는 예외 없이 차단).
        var resp = await client.PostAsJsonAsync("/api/v1/destination-query",
            new { pId = 25001, agvNo = 1, barcode = "TEST-BARCODE-3", inductionNo = 1, qty = 1, timeStamp = (string?)null });
        var body = await resp.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.Equal("NG", body!.Result);
        Assert.Null(body.ChuteNo);
        _out.WriteLine("[EC-3] 소터 PAUSED → push ready=true(운영상태) BUT IF-05 NG(r.Paused 소비) — 크로스-엔드포인트 분리");
    }

    // ════════════════════════════════════════════════════════════════════════
    // EC-4: cell.Capacity NULL=무제한 — 현재수량이 아무리 많아도 수량-full 미적용.
    //   3셀 전부 점유 + 막대한 현재수량 적재(예: 100) + Capacity=NULL → 무제한이므로
    //   배정 셀이 "여유 있음"으로 취급 → SorterFull=false(빈셀0이어도). IF-05도 OK.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task EC4_Sorter_CapacityNull_Unlimited_NoQtyFull()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        var client = factory.CreateClient();

        long sorterId = factory.SorterDestinationId;
        var status    = factory.Services.GetRequiredService<IDestinationStatusService>();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            // 시드 기본값 Capacity=NULL 유지. ORD-003이 3셀 전부 점유.
            Assert.All(db.Cells.Where(c => c.DestinationId == sorterId), c => Assert.Null(c.Capacity));
            OccupyCells(db, sorterId, 3);
            Assert.Equal(0, FreeCellCount(db, sorterId));

            // 막대한 현재수량 적재 — Capacity=NULL이므로 절대 도달 안 함.
            LoadCellQty(db, sorterId, cellNo: 1, qty: 100, pId: 26001, barcode: "TEST-BARCODE-3");
            LoadCellQty(db, sorterId, cellNo: 2, qty: 100, pId: 26002, barcode: "TEST-BARCODE-3");
            LoadCellQty(db, sorterId, cellNo: 3, qty: 100, pId: 26003, barcode: "TEST-BARCODE-3");

            Assert.True(status.SorterHasAssignedCellWithRoomForBarcode(sorterId, "TEST-BARCODE-3"),
                "Capacity=NULL=무제한 → 현재 100이어도 여유 있음");
        }

        // 빈셀0이지만 Capacity=NULL(무제한) → 배정 셀이 항상 여유 → SorterFull=false.
        Assert.False(status.Compute(sorterId, DestType.SORTER_3D).Full,
            "Capacity NULL=무제한 → 수량-full 미적용 → SorterFull=false");

        var if05Req = new { pId = 26100, agvNo = 1, barcode = "TEST-BARCODE-3", inductionNo = 1, qty = 1, timeStamp = (string?)null };
        var resp = await client.PostAsJsonAsync("/api/v1/destination-query", if05Req);
        var body = await resp.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.Equal("OK", body!.Result);
        Assert.Equal(factory.SorterChuteNo, body.ChuteNo);
        _out.WriteLine("[EC-4] Capacity NULL=무제한 → 현재 100이어도 수량-full 미적용 → IF-05 OK");
    }

    // ════════════════════════════════════════════════════════════════════════
    // EC-6: 셀 경계값 — Capacity-1=OK(미달) / Capacity=NG(도달) / Capacity+1=NG(초과).
    //   ≥ 등호 정확성. 한 셀(cellNo=1)만 그 오더 보유, 빈셀0으로 만들어 그 셀 여유가 OK/NG를 좌우.
    // ════════════════════════════════════════════════════════════════════════
    [Theory]
    [InlineData(4, "OK")]   // Capacity-1=4 < 5 → 미달 → OK
    [InlineData(5, "NG")]   // Capacity=5 == 5 → 도달(≥) → NG
    [InlineData(6, "NG")]   // Capacity+1=6 > 5 → 초과 → NG
    public async Task EC6_Sorter_CellBoundary_Equality(int currentQty, string expected)
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        var client = factory.CreateClient();

        long sorterId = factory.SorterDestinationId;
        var status    = factory.Services.GetRequiredService<IDestinationStatusService>();
        const int capacity = 5;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            SetAllCapacities(db, sorterId, capacity);

            // 별도 오더(BC) → cellNo=1 배정 + 현재=currentQty 적재.
            AddSorterOrderWithAssignedCell(db, sorterId, "ORD-BOUNDARY", "BOUNDARY-BC", cellNo: 1);
            LoadCellQty(db, sorterId, cellNo: 1, qty: currentQty, pId: 27000 + currentQty, barcode: "BOUNDARY-BC");

            // 나머지 셀 2·3은 ORD-003 점유 + 작업수량 도달 → 빈셀0(그 셀 여유 유무만이 OK/NG 좌우).
            OccupyCells(db, sorterId, 2);
            LoadCellQty(db, sorterId, cellNo: 2, qty: capacity, pId: 27020 + currentQty, barcode: "TEST-BARCODE-3");
            LoadCellQty(db, sorterId, cellNo: 3, qty: capacity, pId: 27040 + currentQty, barcode: "TEST-BARCODE-3");
            Assert.Equal(0, FreeCellCount(db, sorterId));

            bool hasRoom = status.SorterHasAssignedCellWithRoomForBarcode(sorterId, "BOUNDARY-BC");
            Assert.Equal(currentQty < capacity, hasRoom);
        }

        var if05Req = new { pId = 27100 + currentQty, agvNo = 1, barcode = "BOUNDARY-BC", inductionNo = 1, qty = 1, timeStamp = (string?)null };
        var resp = await client.PostAsJsonAsync("/api/v1/destination-query", if05Req);
        var body = await resp.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.Equal(expected, body!.Result);
        _out.WriteLine($"[EC-6] 현재={currentQty} vs 작업={capacity} → IF-05 {body.Result} (기대 {expected})");
    }

    // ════════════════════════════════════════════════════════════════════════
    // HP-5: 빈셀0 + 일부 배정 셀 여유(셀A=도달, 셀B<도달) → SorterFull=false + push ready=true 유지.
    //   3셀 전부 점유, cellNo 1·2는 작업수량 도달, cellNo 3은 미달 → 그 여유 셀로 기존 오더 수용 가능
    //   → SorterFull=false. push ready는 운영상태(online·정렬·Ready=1)만 보므로 정렬·online 충족 시 true.
    //   (S-소터push운영상태: Full=false든 true든 push ready는 운영상태로만 판정 — 여기선 Full=false·운영OK.)
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task HP5_Sorter_SomeAssignedCellHasRoom_PushReadyTrue()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        _ = factory.CreateClient();

        long sorterId    = factory.SorterDestinationId;
        int  sorterChute = factory.SorterChuteNo;
        var status       = factory.Services.GetRequiredService<IDestinationStatusService>();

        // 정렬 완료(ready 재료).
        factory.FakeMaster.SetReady(true);
        factory.FakeMaster.SetCurFloor(2);
        factory.FakeMaster.SetTgtFloor(0);
        await WaitForSnapshotAsync(() => factory.FakePolling!.Latest, s => s.Online && s.CurFloor == 2 && s.Ready, 5000);

        // 정렬 후 ready=true 도달(빈셀3) + 안정.
        await WaitUntilAsync(() => rcs.LastFor(sorterChute) is { Ready: true }, 6000, "정렬 후 소터 ready=true");
        int baseAligned = rcs.CountFor(sorterChute);
        await WaitUntilExactAsync(() => rcs.CountFor(sorterChute), baseAligned, stableCount: 6, timeoutMs: 4000, "정렬 안정");

        // 빈셀0 + cell1·2 도달, cell3 미달(현재3<작업5) → 여유 배정 셀 ≥1 → SorterFull=false → ready 유지.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            SetAllCapacities(db, sorterId, 5);
            OccupyCells(db, sorterId, 3);
            LoadCellQty(db, sorterId, cellNo: 1, qty: 5, pId: 28001, barcode: "TEST-BARCODE-3");  // 도달
            LoadCellQty(db, sorterId, cellNo: 2, qty: 5, pId: 28002, barcode: "TEST-BARCODE-3");  // 도달
            LoadCellQty(db, sorterId, cellNo: 3, qty: 3, pId: 28003, barcode: "TEST-BARCODE-3");  // 미달(여유)
            Assert.Equal(0, FreeCellCount(db, sorterId));
        }

        Assert.False(status.Compute(sorterId, DestType.SORTER_3D).Full,
            "일부 배정 셀 작업수량 미달 → SorterFull=false");

        // 관찰 타이머가 ready 무변화로 폭주하지 않음 — 여전히 1건(전이 없음).
        await WaitUntilExactAsync(() => rcs.CountFor(sorterChute), baseAligned, stableCount: 8, timeoutMs: 4000,
            "여유 배정 셀 존재 → ready=true 유지(전이 없음·폭주 0)");
        Assert.True(rcs.LastFor(sorterChute)!.Ready, "여유 셀 ≥1 → push ready=true 유지");
        _out.WriteLine($"[HP-5] 빈셀0 + 일부 배정 셀 여유 → SorterFull=false·push ready=true 유지(총 {baseAligned}건·폭주 0)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // EC-7 [정정 — 만재 전이 무발화]: 운영상태가 ready=true인 채 SorterFull이 false→true→false로
    //   churn해도 **소터 push 0건**(운영상태 불변 → push ready 불변 → 전이 없음). S-소터push운영상태.
    //   (이전 모델: 마지막 여유 셀 소진→ready false→true 전이 푸시. 만재가 push ready에서 빠지면서 반전 —
    //    삭제가 아니라 단언 정정. 메타교훈: 셀 만재 전이만으로는 소터 push 무발화 — no-flood 가드.)
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task EC7_Sorter_CellFullnessTransition_NoPush_OperationalReadyUnchanged()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        _ = factory.CreateClient();

        long sorterId    = factory.SorterDestinationId;
        int  sorterChute = factory.SorterChuteNo;
        var  status      = factory.Services.GetRequiredService<IDestinationStatusService>();

        factory.FakeMaster.SetReady(true);
        factory.FakeMaster.SetCurFloor(2);
        factory.FakeMaster.SetTgtFloor(0);
        await WaitForSnapshotAsync(() => factory.FakePolling!.Latest, s => s.Online && s.CurFloor == 2 && s.Ready, 5000);

        // 사전: 빈셀0 + cell1·2 도달, cell3 미달(여유) → SorterFull=false. 운영상태 OK → push ready=true.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            SetAllCapacities(db, sorterId, 5);
            OccupyCells(db, sorterId, 3);
            LoadCellQty(db, sorterId, cellNo: 1, qty: 5, pId: 29001, barcode: "TEST-BARCODE-3");
            LoadCellQty(db, sorterId, cellNo: 2, qty: 5, pId: 29002, barcode: "TEST-BARCODE-3");
            LoadCellQty(db, sorterId, cellNo: 3, qty: 3, pId: 29003, barcode: "TEST-BARCODE-3");  // 마지막 여유
            Assert.Equal(0, FreeCellCount(db, sorterId));
        }

        await WaitUntilAsync(() => rcs.LastFor(sorterChute) is { Ready: true }, 6000, "운영상태 OK → ready=true");
        int baseReady = rcs.CountFor(sorterChute);
        await WaitUntilExactAsync(() => rcs.CountFor(sorterChute), baseReady, stableCount: 6, timeoutMs: 4000, "ready=true 안정");

        // ── 만재 전이: 마지막 여유 셀(cell3) 작업수량 도달(현재 3→5) → SorterFull=true ──
        // 운영상태(online·정렬·Ready=1)는 불변이므로 push ready는 여전히 true → 푸시 0건(무발화).
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            LoadCellQty(db, sorterId, cellNo: 3, qty: 2, pId: 29013, barcode: "TEST-BARCODE-3");  // +2 → 현재 5(도달)
        }
        // ground-truth: 산출은 Full=true·Ready=true(만재여도 운영상태 ready).
        await WaitUntilAsync(() => status.Compute(sorterId, DestType.SORTER_3D).Full, 5000, "마지막 여유 소진 → SorterFull=true");
        Assert.True(status.Compute(sorterId, DestType.SORTER_3D).Ready, "만재여도 운영상태 OK → ready=true");
        // 관찰 주기 다수에도 push 무발화(폭주 0·전이 0) — 만재 전이는 push에 무영향.
        await WaitUntilExactAsync(() => rcs.CountFor(sorterChute), baseReady, stableCount: 8, timeoutMs: 4000,
            "SorterFull 전이만으로는 소터 push 0건(무발화·no-flood)");
        Assert.True(rcs.LastFor(sorterChute)!.Ready, "마지막 푸시는 여전히 ready=true(만재 전이가 false로 안 바꿈)");

        // ── 만재 해소: 빈 셀 1개 생성 → SorterFull=false. 운영상태 여전히 불변 → push 0건 ──
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            var assign = db.CellAssignments
                .Where(a => a.Cell.DestinationId == sorterId && a.ReleasedAt == null)
                .OrderBy(a => a.Id).First();
            assign.ReleasedAt = DateTime.UtcNow;
            db.SaveChanges();
            Assert.Equal(1, FreeCellCount(db, sorterId));
        }
        await WaitUntilAsync(() => !status.Compute(sorterId, DestType.SORTER_3D).Full, 5000, "빈 셀 생성 → SorterFull=false");
        await WaitUntilExactAsync(() => rcs.CountFor(sorterChute), baseReady, stableCount: 8, timeoutMs: 4000,
            "만재 해소 전이만으로도 소터 push 0건(운영상태 불변)");
        Assert.True(rcs.LastFor(sorterChute)!.Ready);
        _out.WriteLine($"[EC-7] SorterFull false→true→false churn 중 소터 push 0건(총 {rcs.CountFor(sorterChute)}건=부트1) — 만재 전이 무발화");
    }

    // ════════════════════════════════════════════════════════════════════════
    // EC-5: 동시성 원자성 — 셀 적재(sorter_command COMPLETED)·배정/해제를 동시 다수 churn 중
    //   한 Compute 결과 내부 불변식이 깨지지 않음(누락 0·최종 상태 수렴).
    //   [S-소터push운영상태 정정] 소터 ready=운영상태(decision.Ready)이므로 "Full ⟹ !ready"는
    //   더 이상 불변식이 아니다(만재여도 운영상태 OK면 ready=true). 새 불변식:
    //     - ready ⟹ online (운영상태 ready는 온라인 전제 — Full/Paused는 ready와 독립).
    //   Full·Paused는 ready를 좌우하지 않으므로 (Full && Ready)·(Paused && Ready)는 모순이 아니다.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task EC5_Sorter_ConcurrentLoadAssign_AtomicCompute_NoContradiction()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        _ = factory.CreateClient();

        long sorterId = factory.SorterDestinationId;
        var status    = factory.Services.GetRequiredService<IDestinationStatusService>();
        var scopeFactory = factory.Services.GetRequiredService<IServiceScopeFactory>();

        // 정렬 상태(ready 재료) — full만이 ready를 좌우.
        factory.FakeMaster.SetReady(true);
        factory.FakeMaster.SetCurFloor(2);
        await WaitForSnapshotAsync(() => factory.FakePolling!.Latest, s => s.Online && s.CurFloor == 2 && s.Ready, 5000);

        const int capacity = 5;
        long orderId;
        List<long> cellIds;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            SetAllCapacities(db, sorterId, capacity);
            orderId = db.Orders.First(o => o.DestinationId == sorterId && o.Status == OrderStatus.RUNNING).Id;
            cellIds = db.Cells.Where(c => c.DestinationId == sorterId && c.Enabled).OrderBy(c => c.CellNo)
                              .Select(c => c.Id).ToList();
        }
        Assert.Equal(3, cellIds.Count);

        using var cts = new CancellationTokenSource();
        int contradictions = 0;
        int observations   = 0;

        // 관찰자: Compute를 빠르게 반복 호출하며 **단일 Compute 결과 내부 불변식**을 검사.
        //   [정정] 소터 ready=운영상태(decision.Ready)이므로 Full/Paused는 ready와 독립이다.
        //   - ready ⟹ online  (운영상태 ready는 온라인 전제 — DepositDecider가 !online이면 Deny).
        //   (셀 적재·배정/해제가 churn해도 운영상태(SetReady(true)·CurFloor=2)는 불변이므로 ready=true 유지.
        //    Full/Paused churn은 ready에 무영향 — Full&&Ready는 이제 정당.)
        var observer = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                DestinationReadiness r;
                try { r = status.Compute(sorterId, DestType.SORTER_3D); }
                catch { await Task.Delay(1); continue; }  // SQLITE_BUSY — 다음 관찰에서 재평가
                Interlocked.Increment(ref observations);

                bool bad = r.Ready && !r.Online;  // 운영상태 ready는 온라인 전제(유일 불변식).
                if (bad) Interlocked.Increment(ref contradictions);
                await Task.Delay(1);
            }
        });

        // 배정/해제 토글 + 셀 적재(sorter_command COMPLETED 추가)를 동시 다수 스레드가 churn.
        const int writers = 6;
        int pidSeq = 50000;
        var writerTasks = Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            var rnd = new Random(w * 31 + 7);
            for (int i = 0; i < 30; i++)
            {
                int cellIdx = rnd.Next(cellIds.Count);
                long cellId = cellIds[cellIdx];
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
                    int action = rnd.Next(3);
                    if (action == 0)
                    {
                        // 배정(점유) — 이미 활성이면 흡수.
                        var active = db.CellAssignments.FirstOrDefault(a => a.CellId == cellId && a.ReleasedAt == null);
                        if (active is null)
                            db.CellAssignments.Add(new CellAssignment
                            {
                                CellId = cellId, OrderId = orderId, AssignedAt = DateTime.UtcNow,
                                ReleasedAt = null, CreatedAt = DateTime.UtcNow,
                            });
                    }
                    else if (action == 1)
                    {
                        // 해제(빈 셀 생성).
                        var active = db.CellAssignments.FirstOrDefault(a => a.CellId == cellId && a.ReleasedAt == null);
                        if (active is not null) active.ReleasedAt = DateTime.UtcNow;
                    }
                    else
                    {
                        // 적재(sorter_command COMPLETED + piece) — 셀 현재수량 증가.
                        int pid = Interlocked.Increment(ref pidSeq);
                        var now = DateTime.UtcNow;
                        var cellNo = db.Cells.Where(c => c.Id == cellId).Select(c => c.CellNo).First();
                        var piece = new Piece
                        {
                            PId = pid, IsActive = false, Barcode = "CHURN", Qty = rnd.Next(1, 4),
                            DepositedAt = now, DestinationId = sorterId, Status = PieceStatus.LOADED,
                            CreatedAt = now, UpdatedAt = now,
                        };
                        db.Pieces.Add(piece);
                        db.SaveChanges();
                        db.SorterCommands.Add(new SorterCommand
                        {
                            PieceId = piece.Id, CellId = cellId, CSeq = 1, CellNo = cellNo,
                            CWrittenAt = now, Status = SorterCommandStatus.COMPLETED, CreatedAt = now,
                        });
                    }
                    db.SaveChanges();
                }
                catch { /* 동시 토글/적재 경합 — 다음 라운드에서 재평가(eventually consistent) */ }
                await Task.Delay(rnd.Next(1, 4));
            }
        })).ToArray();

        await Task.WhenAll(writerTasks);
        cts.Cancel();
        await observer;

        Assert.True(observations > 0, "관찰자가 Compute를 최소 1회 실행");
        Assert.Equal(0, contradictions);

        // quiesce 후 결정적 검증: 전부 점유 + 전 셀 작업수량 도달 → SorterFull=true·ready=false.
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            foreach (var a in db.CellAssignments.Where(a => a.ReleasedAt == null).ToList())
                a.ReleasedAt = DateTime.UtcNow;
            db.SaveChanges();
            OccupyCells(db, sorterId, 3);
            // 모든 셀을 작업수량 도달시킴(현재 churn 적재 + 추가 적재로 ≥ capacity 보장).
            for (int cellNo = 1; cellNo <= 3; cellNo++)
                LoadCellQty(db, sorterId, cellNo, capacity, 51000 + cellNo, "TEST-BARCODE-3");
            Assert.Equal(0, FreeCellCount(db, sorterId));
        }
        var rFull = status.Compute(sorterId, DestType.SORTER_3D);
        Assert.True(rFull.Full,  "전부 점유 + 전 셀 작업수량 도달 → SorterFull=true 수렴(산출 유지)");
        // [정정] 만재는 push ready를 좌우하지 않음 — 운영상태 OK(SetReady·CurFloor=2)이므로 ready=true.
        Assert.True(rFull.Ready, "만재여도 운영상태 OK → ready=true(S-소터push운영상태)");

        // 빈 셀 1개 생성 → SorterFull=false 수렴.
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            var a = db.CellAssignments.Where(a => a.Cell.DestinationId == sorterId && a.ReleasedAt == null)
                      .OrderBy(a => a.Id).First();
            a.ReleasedAt = DateTime.UtcNow;
            db.SaveChanges();
            Assert.Equal(1, FreeCellCount(db, sorterId));
        }
        Assert.False(status.Compute(sorterId, DestType.SORTER_3D).Full, "빈 셀 1개 → SorterFull=false 수렴");

        _out.WriteLine($"[EC-5] {writers}스레드 동시 적재/배정/해제 + Compute {observations}회 — 내부 모순 {contradictions}건. quiesce SorterFull 등가성 확인");
    }

    // ════════════════════════════════════════════════════════════════════════
    // EC-8 (MAJOR-1 크로스-엔드포인트 정합): 오더가 full 셀 + 여유 셀 동시 보유 시
    //   IF-10 SelectCell이 **여유 셀**을 골라 Capacity 초과 적재 0 (IF-05 OK ⟹ 적재 가능).
    //   기존 버그: SelectCell ①분기가 FirstOrDefault로 임의(full) 셀을 골라 초과 적재.
    //   EC-5류 단일-Compute 모순만으론 이 비대칭을 못 잡으므로 명시 시나리오로 단언.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task EC8_SelectCell_PicksRoomCell_NotFullCell_If05OkImpliesLoadable()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        var client = factory.CreateClient();

        long sorterId = factory.SorterDestinationId;
        var status    = factory.Services.GetRequiredService<IDestinationStatusService>();
        const int capacity = 5;

        int fullCellNo, roomCellNo;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            SetAllCapacities(db, sorterId, capacity);

            // 오더 X(ORDX-BC)를 cellNo=1·2 둘 다 배정. cell1=full(현재5), cell2=여유(현재2).
            var (orderId, _) = AddSorterOrderWithAssignedCell(db, sorterId, "ORD-X-MULTI", "ORDX-BC", cellNo: 1);
            var now = DateTime.UtcNow;
            var cell2 = db.Cells.First(c => c.DestinationId == sorterId && c.CellNo == 2);
            db.CellAssignments.Add(new CellAssignment
            {
                CellId = cell2.Id, OrderId = orderId, AssignedAt = now, ReleasedAt = null, CreatedAt = now,
            });
            db.SaveChanges();

            fullCellNo = 1; roomCellNo = 2;
            LoadCellQty(db, sorterId, cellNo: 1, qty: capacity, pId: 29101, barcode: "ORDX-BC");  // full(5)
            LoadCellQty(db, sorterId, cellNo: 2, qty: 2,        pId: 29102, barcode: "ORDX-BC");  // 여유(2<5)

            // cell3 → ORD-003 점유 + 작업수량 도달 → 빈 셀 0(SelectCell이 ②빈셀 폴백 못 하게).
            OccupyCells(db, sorterId, 1);
            LoadCellQty(db, sorterId, cellNo: 3, qty: capacity, pId: 29103, barcode: "TEST-BARCODE-3");
            Assert.Equal(0, FreeCellCount(db, sorterId));
        }

        // IF-05: ORDX-BC는 여유 배정 셀(cell2) 보유 → OK(IF-05 OK ⟹ 적재 가능 전제).
        var if05Req = new { pId = 29110, agvNo = 1, barcode = "ORDX-BC", inductionNo = 1, qty = 1, timeStamp = (string?)null };
        var resp = await client.PostAsJsonAsync("/api/v1/destination-query", if05Req);
        var body = await resp.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.Equal("OK", body!.Result);

        // IF-10 SelectCell: full 셀(1)이 아니라 **여유 셀(2)**을 골라야 한다(임의 FirstOrDefault 금지).
        using (var scope = factory.Services.CreateScope())
        {
            var selector = scope.ServiceProvider.GetRequiredService<ICellSelector>();
            int? picked = selector.SelectCell(factory.SorterChuteNo, "ORDX-BC");
            Assert.NotNull(picked);
            Assert.Equal(roomCellNo, picked!.Value);
            Assert.NotEqual(fullCellNo, picked.Value);
        }

        // 그 여유 셀(2)에 적재해도 Capacity 초과 0 — 현재 2 → +qty(≤3)면 ≤5.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            // 적재 후 셀2 현재수량 산출(공유 로직과 동일 ground-truth) — Capacity 이내 확인.
            LoadCellQty(db, sorterId, cellNo: 2, qty: 2, pId: 29120, barcode: "ORDX-BC");  // 2→4 (<5)
            var cell2Id = db.Cells.First(c => c.DestinationId == sorterId && c.CellNo == 2).Id;
            var loaded = db.SorterCommands
                .Where(sc => sc.Status == SorterCommandStatus.COMPLETED && sc.CellId == cell2Id)
                .Select(sc => new { sc.PieceId, sc.Piece.Qty }).Distinct().Sum(x => x.Qty);
            Assert.True(loaded <= capacity, $"여유 셀 적재 후 현재수량({loaded}) ≤ Capacity({capacity}) — 초과 적재 0");
        }
        _out.WriteLine($"[EC-8] 오더 full셀{fullCellNo}+여유셀{roomCellNo} 보유 → SelectCell 여유셀{roomCellNo} 선택·초과 적재 0(IF-05 OK⟹적재 가능)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // EC-9 (MAJOR-1 정합 — 다른 오더의 여유 셀은 무용): 오더 A 셀 전부 full + 빈 셀 0,
    //   다른 오더 B의 셀에만 여유가 있을 때 → 오더 A piece는 IF-05 **NG**(B의 여유 셀은 A에 무용)
    //   AND SelectCell(A) = null. "IF-05 OK ⟺ SelectCell 적재 가능" 동형 — 목적지-단위 SorterFull
    //   (B 여유로 false)에 끌려가 A가 OK로 새지 않음(SorterCanAcceptBarcode는 piece 단위).
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task EC9_OtherOrderRoomCell_DoesNotMakeThisPieceOk()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        var client = factory.CreateClient();

        long sorterId = factory.SorterDestinationId;
        var status    = factory.Services.GetRequiredService<IDestinationStatusService>();
        const int capacity = 5;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            SetAllCapacities(db, sorterId, capacity);

            // 오더 A(ORDA-BC) → cell1 배정 + full(5). 빈 셀 0이 되도록 cell2·3도 점유.
            AddSorterOrderWithAssignedCell(db, sorterId, "ORD-A", "ORDA-BC", cellNo: 1);
            LoadCellQty(db, sorterId, cellNo: 1, qty: capacity, pId: 29201, barcode: "ORDA-BC");  // A full

            // 오더 B(ORDB-BC) → cell2 배정 + 여유(2<5). cell3 → ORD-003 점유 + full.
            AddSorterOrderWithAssignedCell(db, sorterId, "ORD-B", "ORDB-BC", cellNo: 2);
            LoadCellQty(db, sorterId, cellNo: 2, qty: 2, pId: 29202, barcode: "ORDB-BC");          // B 여유
            OccupyCells(db, sorterId, 1);                                                          // cell3 점유
            LoadCellQty(db, sorterId, cellNo: 3, qty: capacity, pId: 29203, barcode: "TEST-BARCODE-3");
            Assert.Equal(0, FreeCellCount(db, sorterId));
        }

        // 목적지-단위 SorterFull은 B 여유로 false(소터는 B piece를 받을 수 있음).
        Assert.False(status.Compute(sorterId, DestType.SORTER_3D).Full,
            "B 여유 셀 존재 → 목적지-단위 SorterFull=false");
        // 그러나 A piece는 받을 수 없다(A 셀 full·빈 셀 0·B 셀은 A에 무용).
        Assert.False(status.SorterCanAcceptBarcode(sorterId, "ORDA-BC"),
            "오더 A는 받을 수 없음(B 여유 셀은 A piece에 무용)");
        Assert.True(status.SorterCanAcceptBarcode(sorterId, "ORDB-BC"),
            "오더 B는 자기 여유 셀로 받을 수 있음");

        // IF-05(A) → NG. (목적지 SorterFull=false에 끌려가 OK로 새지 않음.)
        var respA = await client.PostAsJsonAsync("/api/v1/destination-query",
            new { pId = 29210, agvNo = 1, barcode = "ORDA-BC", inductionNo = 1, qty = 1, timeStamp = (string?)null });
        var bodyA = await respA.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.Equal("NG", bodyA!.Result);
        Assert.Null(bodyA.ChuteNo);

        // SelectCell(A) = null (적재 불가) — IF-05 NG와 동형.
        using (var scope = factory.Services.CreateScope())
        {
            var selector = scope.ServiceProvider.GetRequiredService<ICellSelector>();
            Assert.Null(selector.SelectCell(factory.SorterChuteNo, "ORDA-BC"));
            // 반면 B는 자기 여유 셀(2)로 적재 가능.
            Assert.Equal(2, selector.SelectCell(factory.SorterChuteNo, "ORDB-BC"));
        }

        // IF-05(B) → OK.
        var respB = await client.PostAsJsonAsync("/api/v1/destination-query",
            new { pId = 29211, agvNo = 1, barcode = "ORDB-BC", inductionNo = 1, qty = 1, timeStamp = (string?)null });
        var bodyB = await respB.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.Equal("OK", bodyB!.Result);
        _out.WriteLine("[EC-9] A full+빈셀0, B만 여유 → IF-05(A) NG·SelectCell(A) null / IF-05(B) OK·SelectCell(B)=2 (동형)");
    }

    // ── 헬퍼: 명시 타임스탬프 오더/배정/적재(배정-기간 스코프 결정적 검증용) ─────────────────
    private static long AddOrderWithAssignmentAt(
        WcsDbContext db, long destId, string orderNo, string barcode, int cellNo,
        DateTime assignedAt, DateTime? releasedAt)
    {
        var now   = DateTime.UtcNow;
        var batch = db.WorkBatches.First();
        var order = new WcsOrder
        {
            WorkBatchId = batch.Id, OrderNo = orderNo, OrderType = OrderType.GENERAL,
            DestinationId = destId, DestAssignType = DestAssignType.UPSTREAM, DestAssignedAt = now,
            Status = OrderStatus.RUNNING, StartedAt = now, CreatedAt = now, UpdatedAt = now,
        };
        db.Orders.Add(order);
        db.SaveChanges();
        db.OrderItems.Add(new OrderItem
        {
            OrderId = order.Id, Barcode = barcode, PlannedQty = 100, ReservedQty = 0, SortedQty = 0,
            CreatedAt = now, UpdatedAt = now,
        });
        var cell = db.Cells.First(c => c.DestinationId == destId && c.CellNo == cellNo);
        db.CellAssignments.Add(new CellAssignment
        {
            CellId = cell.Id, OrderId = order.Id, AssignedAt = assignedAt, ReleasedAt = releasedAt, CreatedAt = assignedAt,
        });
        db.SaveChanges();
        return order.Id;
    }

    private static void LoadCellQtyAt(
        WcsDbContext db, long destId, int cellNo, int qty, int pId, string barcode, DateTime cWrittenAt)
    {
        var cell = db.Cells.First(c => c.DestinationId == destId && c.CellNo == cellNo);
        var piece = new Piece
        {
            PId = pId, IsActive = false, Barcode = barcode, Qty = qty, DepositedAt = cWrittenAt,
            DestinationId = destId, Status = PieceStatus.LOADED, CreatedAt = cWrittenAt, UpdatedAt = cWrittenAt,
        };
        db.Pieces.Add(piece);
        db.SaveChanges();
        db.SorterCommands.Add(new SorterCommand
        {
            PieceId = piece.Id, CellId = cell.Id, CSeq = 1, CellNo = cellNo, CWrittenAt = cWrittenAt,
            RSeq = 1, RCellNo = cellNo, RFlagAt = cWrittenAt, Status = SorterCommandStatus.COMPLETED, CreatedAt = cWrittenAt,
        });
        db.SaveChanges();
    }

    // ════════════════════════════════════════════════════════════════════════
    // EC-10 [S-CELL-ACCUM no-overflow + 동형 핵심]: 오더 배정 셀 full인데 **빈 enabled 셀이 남아 있어도**
    //   그 오더는 두 번째 셀로 오버플로하지 않는다(자기 셀 국한). 구조 결함(HasAssignedCellWithRoom OR
    //   HasFreeEnabledCell)이었다면 빈 셀 존재로 OK가 새어 오버플로했을 오버플로-가능 지점을 명시 단언.
    //     · SorterCanAcceptBarcode(ORDX) == false  AND  SelectCell(ORDX) == null  (동형).
    //     · 대조: 배정 없는 새 오더(TEST-BARCODE-3)는 그 빈 셀로 OK — 빈 셀은 실제 사용 가능(폴백만 금지).
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task EC10_AssignedCellFull_FreeCellsExist_NoOverflow_Isomorphic()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        var client = factory.CreateClient();

        long sorterId = factory.SorterDestinationId;
        var status    = factory.Services.GetRequiredService<IDestinationStatusService>();
        const int capacity = 2;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            SetAllCapacities(db, sorterId, capacity);
            // 오더 X → cell1 배정 + full(현재=2). cell2·3은 **점유하지 않음**(빈 enabled 셀 유지).
            AddSorterOrderWithAssignedCell(db, sorterId, "ORD-X-CONFINED", "ORDX-BC", cellNo: 1);
            LoadCellQty(db, sorterId, cellNo: 1, qty: capacity, pId: 30001, barcode: "ORDX-BC");
            Assert.True(FreeCellCount(db, sorterId) >= 1, "오버플로 가능 지점: 빈 enabled 셀 ≥1");
        }

        // 오더 X: 배정 셀 full + 빈 셀 존재 → no-overflow → NG·null (동형).
        Assert.False(status.SorterCanAcceptBarcode(sorterId, "ORDX-BC"),
            "배정 셀 full → 빈 셀로 오버플로 금지(SorterCanAcceptBarcode=false)");
        using (var scope = factory.Services.CreateScope())
        {
            var selector = scope.ServiceProvider.GetRequiredService<ICellSelector>();
            Assert.Null(selector.SelectCell(factory.SorterChuteNo, "ORDX-BC"));  // ②빈셀 폴백 안 함.
        }
        var respX = await client.PostAsJsonAsync("/api/v1/destination-query",
            new { pId = 29910, agvNo = 1, barcode = "ORDX-BC", inductionNo = 1, qty = 1, timeStamp = (string?)null });
        Assert.Equal(HttpStatusCode.OK, respX.StatusCode);   // 도메인 거부 = 200 + NG(400 아님).
        var bodyX = await respX.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.Equal("NG", bodyX!.Result);

        // 대조: 배정 없는 새 오더(TEST-BARCODE-3)는 그 빈 셀로 수용 가능(빈 셀은 실사용 — 폴백만 금지).
        Assert.True(status.SorterCanAcceptBarcode(sorterId, "TEST-BARCODE-3"),
            "배정 없는 새 오더는 빈 enabled 셀로 OK(빈 셀 자체는 사용 가능)");
        _out.WriteLine("[EC-10] 배정 셀 full+빈셀 존재 → ORDX NG·SelectCell null(오버플로 0). 새 오더는 빈 셀 OK(대조).");
    }

    // ════════════════════════════════════════════════════════════════════════
    // EC-11 [S-CELL-ACCUM 배정-기간 스코프 핵심]: 재사용된 셀의 적재량은 **현재 활성 배정 기간부터** 카운트.
    //   이전 오더 A가 그 셀에 쌓은 all-time COMPLETED 적재량이 새 오더 B의 여유 계산을 오염시키지 않음.
    //   명시 타임스탬프로 결정적 검증(same-tick 경계 회피):
    //     A: 배정 cell1[t0, released t0+10], COMPLETED qty=2 @ t0+1.  (셀 all-time 적재=2)
    //     B: 배정 cell1[t0+20, active].  → B 적재량=0(A의 t0+1 < t0+20 배제) → 여유(0<Capacity=2)=OK.
    //   B에 qty 1·1 추가(@t0+21·+22) → B 적재 1→2(오직 B 것) → Capacity 2 도달 시 NG. 오염됐다면 처음부터 NG였을 것.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task EC11_ReusedCell_LoadedScopedToCurrentAssignment_NotAllTime()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        _ = factory.CreateClient();

        long sorterId = factory.SorterDestinationId;
        var status    = factory.Services.GetRequiredService<IDestinationStatusService>();
        const int capacity = 2;
        var t0 = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            SetAllCapacities(db, sorterId, capacity);

            // 오더 A: cell1 배정(t0)·release(t0+10) + A의 COMPLETED 적재 2(@t0+1). 셀 all-time 적재=2.
            AddOrderWithAssignmentAt(db, sorterId, "ORD-A11", "A11-BC", cellNo: 1,
                assignedAt: t0, releasedAt: t0.AddSeconds(10));
            LoadCellQtyAt(db, sorterId, cellNo: 1, qty: 2, pId: 31001, barcode: "A11-BC", cWrittenAt: t0.AddSeconds(1));

            // 오더 B: cell1 재배정(t0+20, active). B 적재 0.
            AddOrderWithAssignmentAt(db, sorterId, "ORD-B11", "B11-BC", cellNo: 1,
                assignedAt: t0.AddSeconds(20), releasedAt: null);
        }

        // B 재사용 셀 적재량 0부터 → 여유(0<2). 오염됐다면(A의 2 합산) 2>=2로 이미 full=false였을 것.
        Assert.True(status.SorterHasAssignedCellWithRoomForBarcode(sorterId, "B11-BC"),
            "재사용 셀 적재 0부터 카운트(A의 옛 적재 2 미오염) → B 여유");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            LoadCellQtyAt(db, sorterId, cellNo: 1, qty: 1, pId: 31002, barcode: "B11-BC", cWrittenAt: t0.AddSeconds(21));
        }
        Assert.True(status.SorterHasAssignedCellWithRoomForBarcode(sorterId, "B11-BC"),
            "B 적재 1<2 → 여전히 여유(B 것만 카운트 = 1, 오염 시 3이었을 것)");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            LoadCellQtyAt(db, sorterId, cellNo: 1, qty: 1, pId: 31003, barcode: "B11-BC", cWrittenAt: t0.AddSeconds(22));
        }
        Assert.False(status.SorterHasAssignedCellWithRoomForBarcode(sorterId, "B11-BC"),
            "B 적재 2>=Capacity 2 → 도달(B 것만 카운트 — A의 2 미합산, 아니면 4로 진작 도달)");
        _out.WriteLine("[EC-11] 재사용 셀 적재 배정-기간 스코프 — A 옛 적재 2 배제·B 0부터 카운트(1→2에서 full).");
    }

    // ════════════════════════════════════════════════════════════════════════
    // EC-12 [S-CELL-ACCUM Scope 5 — OFFLINE orphan 롤백]: ReleaseEmptyAssignment는 **적재 0인 신규 배정만**
    //   release하고, 적재≥1(누적 진행) 배정은 유지한다. (OFFLINE 시 orphan 잔존 0·누적 바인딩 조기 파기 0.)
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task EC12_ReleaseEmptyAssignment_RollsBackEmptyOrphan_KeepsLoaded()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        _ = factory.CreateClient();

        long sorterId = factory.SorterDestinationId;
        int  chute    = factory.SorterChuteNo;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            SetAllCapacities(db, sorterId, 5);
            // 오더 O: cell1 배정, 적재 0(빈 orphan).
            AddSorterOrderWithAssignedCell(db, sorterId, "ORD-O12", "O12-BC", cellNo: 1);
            // 오더 P: cell2 배정 + 적재 1(누적 진행).
            AddSorterOrderWithAssignedCell(db, sorterId, "ORD-P12", "P12-BC", cellNo: 2);
            LoadCellQty(db, sorterId, cellNo: 2, qty: 1, pId: 32002, barcode: "P12-BC");
        }

        using (var scope = factory.Services.CreateScope())
        {
            var selector = scope.ServiceProvider.GetRequiredService<ICellSelector>();
            selector.ReleaseEmptyAssignment(chute, "O12-BC", 1);   // 빈 orphan → release.
            selector.ReleaseEmptyAssignment(chute, "P12-BC", 2);   // 적재≥1 → 유지.
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            Assert.Equal(0, db.CellAssignments.Count(a => a.Order.OrderNo == "ORD-O12" && a.ReleasedAt == null));  // 롤백됨.
            Assert.Equal(1, db.CellAssignments.Count(a => a.Order.OrderNo == "ORD-P12" && a.ReleasedAt == null));  // 유지됨.
        }
        _out.WriteLine("[EC-12] ReleaseEmptyAssignment: 빈 신규 배정(O) 롤백·적재 진행 배정(P) 유지 — OFFLINE orphan 0.");
    }

    // ════════════════════════════════════════════════════════════════════════
    // EC-13 [동형 명시 스위프]: 배정-분기 전 케이스에서 SorterCanAcceptBarcode ⟺ (SelectCell != null),
    //   같은 셀. no-overflow(배정 full → NG·null)·재사용(배정 room → 같은 셀)·신규(빈 셀 → ②)를 한 상태에서.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task EC13_If05_SelectCell_Isomorphism_Sweep()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        _ = factory.CreateClient();

        long sorterId = factory.SorterDestinationId;
        int  chute    = factory.SorterChuteNo;
        var status    = factory.Services.GetRequiredService<IDestinationStatusService>();
        const int capacity = 2;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            SetAllCapacities(db, sorterId, capacity);
            AddSorterOrderWithAssignedCell(db, sorterId, "ORD-F13", "F13-BC", cellNo: 1); // 배정-full 케이스
            LoadCellQty(db, sorterId, cellNo: 1, qty: capacity, pId: 33001, barcode: "F13-BC");
            AddSorterOrderWithAssignedCell(db, sorterId, "ORD-R13", "R13-BC", cellNo: 2); // 배정-room 케이스
            LoadCellQty(db, sorterId, cellNo: 2, qty: 1, pId: 33002, barcode: "R13-BC");
            // cell3은 빈 enabled 유지(배정 없는 새 오더 TEST-BARCODE-3용).
        }

        // 배정-full: NG ⟺ null.
        AssertIsomorphic(factory, status, chute, sorterId, "F13-BC", expectAccept: false, expectCell: null);
        // 배정-room: OK ⟺ 같은 셀(2).
        AssertIsomorphic(factory, status, chute, sorterId, "R13-BC", expectAccept: true, expectCell: 2);
        // 배정 없는 새 오더 + 빈 셀(3): OK ⟺ 비-null(② 신규 할당 = cell3). (마지막 — SelectCell 부수효과.)
        Assert.True(status.SorterCanAcceptBarcode(sorterId, "TEST-BARCODE-3"));
        using (var scope = factory.Services.CreateScope())
        {
            var selector = scope.ServiceProvider.GetRequiredService<ICellSelector>();
            int? picked = selector.SelectCell(chute, "TEST-BARCODE-3");
            Assert.NotNull(picked);
            Assert.Equal(3, picked!.Value);  // 유일한 빈 셀.
        }
        _out.WriteLine("[EC-13] 동형 스위프: 배정-full NG⟺null·배정-room OK⟺셀2·신규 OK⟺셀3(②).");
    }

    private static void AssertIsomorphic(
        RcsPushWebApplicationFactory factory, IDestinationStatusService status, int chute, long sorterId,
        string barcode, bool expectAccept, int? expectCell)
    {
        bool canAccept = status.SorterCanAcceptBarcode(sorterId, barcode);
        using var scope = factory.Services.CreateScope();
        var selector = scope.ServiceProvider.GetRequiredService<ICellSelector>();
        int? picked = selector.SelectCell(chute, barcode);   // ①/NG 케이스는 부수효과 없음(재사용/실패).
        Assert.Equal(expectAccept, canAccept);
        Assert.Equal(expectAccept, picked is not null);       // 동형: OK ⟺ 비-null.
        Assert.Equal(expectCell, picked);
    }
}
