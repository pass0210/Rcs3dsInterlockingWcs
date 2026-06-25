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
        Assert.True(r.Full,   "빈셀0 + 전 배정 셀 작업수량 도달 → SorterFull=true");
        Assert.False(r.Ready, "full → ready=false");
        Assert.Equal(DenyReason.Full, r.Reason);

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
    // EC-3: 소터 PAUSED → IF-05 NG (소터 불변 — (B) 슈트 정정이 소터를 깨지 않음)
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task EC3_Sorter_Paused_If05_Ng_Unchanged()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        var client = factory.CreateClient();

        long sorterId = factory.SorterDestinationId;
        var status    = factory.Services.GetRequiredService<IDestinationStatusService>();

        await WaitForSnapshotAsync(() => factory.FakePolling!.Latest, s => s.Online, 5000);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            db.Destinations.First(d => d.Id == sorterId).Status = DestStatus.PAUSED;
            db.SaveChanges();
        }
        var rPaused = status.Compute(sorterId, DestType.SORTER_3D);
        Assert.True(rPaused.Paused);
        Assert.Equal(DenyReason.Paused, rPaused.Reason);

        var resp = await client.PostAsJsonAsync("/api/v1/destination-query",
            new { pId = 25001, agvNo = 1, barcode = "TEST-BARCODE-3", inductionNo = 1, qty = 1, timeStamp = (string?)null });
        var body = await resp.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.Equal("NG", body!.Result);
        Assert.Null(body.ChuteNo);
        _out.WriteLine("[EC-3] 소터 PAUSED → IF-05 NG (소터 불변)");
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
    // HP-5: 빈셀0 + 일부 배정 셀 여유(셀A=도달, 셀B<도달) → push ready=true 유지·Full=false.
    //   3셀 전부 점유, cellNo 1·2는 작업수량 도달, cellNo 3은 미달 → 그 여유 셀로 기존 오더 수용 가능
    //   → SorterFull=false → 정렬·online 충족 시 push ready=true 유지.
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
    // EC-7: push ready=false 전이 — 마지막 여유 배정 셀이 작업수량 도달 → SorterFull=true →
    //   관찰 타이머가 ready=true→false 전이 감지(정확히 1건) → 복귀(빈 셀 1개 생성) → ready=true 재푸시 1건.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task EC7_Sorter_LastRoomConsumed_PushReadyFalse_ThenRecover()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        _ = factory.CreateClient();

        long sorterId    = factory.SorterDestinationId;
        int  sorterChute = factory.SorterChuteNo;

        factory.FakeMaster.SetReady(true);
        factory.FakeMaster.SetCurFloor(2);
        factory.FakeMaster.SetTgtFloor(0);
        await WaitForSnapshotAsync(() => factory.FakePolling!.Latest, s => s.Online && s.CurFloor == 2 && s.Ready, 5000);

        // 사전: 빈셀0 + cell1·2 도달, cell3 미달(여유) → ready=true 유지(HP-5 상태).
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

        await WaitUntilAsync(() => rcs.LastFor(sorterChute) is { Ready: true }, 6000, "여유 셀 존재 → ready=true");
        int baseReady = rcs.CountFor(sorterChute);
        await WaitUntilExactAsync(() => rcs.CountFor(sorterChute), baseReady, stableCount: 6, timeoutMs: 4000, "ready=true 안정");

        // ── EC-7: 마지막 여유 셀(cell3)이 작업수량 도달(현재 3→5, 추가 적재) → SorterFull=true ──
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            LoadCellQty(db, sorterId, cellNo: 3, qty: 2, pId: 29013, barcode: "TEST-BARCODE-3");  // +2 → 현재 5(도달)
        }
        await WaitUntilAsync(() => rcs.LastFor(sorterChute) is { Ready: false }, 5000, "마지막 여유 소진 → ready=false");
        int afterFull = baseReady + 1;
        await WaitUntilExactAsync(() => rcs.CountFor(sorterChute), afterFull, stableCount: 6, timeoutMs: 4000,
            "SorterFull 전이 정확히 1건(중복 0·무변화 폴 폭주 0)");
        Assert.False(rcs.LastFor(sorterChute)!.Ready);

        // ── 복귀: 빈 셀 1개 생성(cell_assignment 해제) → SorterFull=false → ready=true 재푸시 1건 ──
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
        await WaitUntilAsync(() => rcs.LastFor(sorterChute) is { Ready: true }, 5000, "빈 셀 생성 → ready=true 재푸시");
        int afterRecover = afterFull + 1;
        await WaitUntilExactAsync(() => rcs.CountFor(sorterChute), afterRecover, stableCount: 6, timeoutMs: 4000,
            "복귀 재푸시 정확히 1건(전이당 1회)");
        Assert.True(rcs.LastFor(sorterChute)!.Ready);
        _out.WriteLine($"[EC-7] 마지막 여유 소진 ready=false({afterFull}) → 빈 셀 복귀 ready=true({afterRecover}) — 전이당 1건");
    }

    // ════════════════════════════════════════════════════════════════════════
    // EC-5: 동시성 원자성 — 셀 적재(sorter_command COMPLETED)·배정/해제를 동시 다수 churn 중
    //   "셀 현재 ≥ Capacity인데 그 piece OK" 또는 "셀 여유 있는데 NG", "SorterFull인데 ready=true"
    //   모순 응답 0건(단일 원자 쿼리). 최종 상태로 수렴(누락 0).
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
        //   - full ⟹ !ready  (SorterFull이면 절대 ready=true가 새면 안 됨)
        //   - ready ⟹ !full && !paused && online  (ready 합성 정합)
        //   단일 원자 쿼리 + record 동시 산출이면 0건. (셀 적재·배정/해제가 churn하는 동안에도
        //   한 Compute 호출 내부에서 "빈셀0·전 배정 셀 도달"의 합성 결과가 일관해야 한다.)
        var observer = Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                DestinationReadiness r;
                try { r = status.Compute(sorterId, DestType.SORTER_3D); }
                catch { await Task.Delay(1); continue; }  // SQLITE_BUSY — 다음 관찰에서 재평가
                Interlocked.Increment(ref observations);

                bool bad = (r.Full && r.Ready)
                        || (r.Ready && (r.Full || r.Paused || !r.Online));
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
        Assert.True(rFull.Full,   "전부 점유 + 전 셀 작업수량 도달 → SorterFull=true 수렴");
        Assert.False(rFull.Ready, "full → ready=false");

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
}
