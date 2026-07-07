using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api;
using Wcs.Core;
using Wcs.Data;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// S-FIELD-20CELLS — 20셀·15가용 게이트 회귀 방지(A안: 백엔드 코드 무변경, 테스트만).
//
// 현장 3D Sorter는 물리 20셀(4×5)이지만 PLC 매핑이 15번까지만 완료 → 셀 16~20은 Enabled=0.
// 시드(seed-field-20cells.sql)가 만드는 상태를 **프로그래매틱 SQLite 더블**로 재구성해,
// 기존 Enabled 게이트가 셀 16~20을 어떤 경로로도 선택/수용하지 않음을 단정한다.
//
//   T1  1~15 만재(활성 배정 점유)·16~20 Enabled=0 미배정 → SelectCell 이 16~20 **미반환**(null).
//       (음성 대조: 셀 16 을 Enabled=1 로 올리면 SelectCell 이 16 을 반환 = 매핑 확장 데이터-온리 경로.)
//   T2  IF-05 0701-CELL-01/-15 → OK(chuteNo=1) · 0701-CELL-16(CANCELLED 오더) → NG(결정적).
//   T3  가용 15셀 전량 만재(작업수량 도달) → SorterFull=true · 신규 바코드 IF-05 NG(FULL).
//       (비활성 셀 16~20 은 물리적으로 비어 있어도 full 산출에서 제외됨을 단정 —
//        만약 포함됐다면 "빈 셀 있음"으로 SorterFull=false 가 되어 이 단정이 깨진다.)
//
// 게이트 메커니즘은 이미 존재하는 cell.Enabled 재사용(계약 A안) — production 코드 변경 0.
//   · SelectCell ②빈 셀 폴백 : c.Enabled 필터(DbRepositories.cs).
//   · IF-05 HasFreeEnabledCell / ComputeSorterFull : Enabled 셀만 집계(DestinationStatusService.cs).
// ════════════════════════════════════════════════════════════════════════════
public class Field20CellsGateTests
{
    private readonly ITestOutputHelper _out;
    public Field20CellsGateTests(ITestOutputHelper output) => _out = output;

    private const int CellCap  = 3;
    private const int AvailMax = 15;   // 가용(Enabled=1) 셀 상한
    private const int PhysMax  = 20;   // 물리 셀 수(4×5)

    // ── 시드(seed-field-20cells.sql)와 동형의 20셀·15가용 상태를 프로그래매틱 구성 ─────
    //   · 셀 1~20 존재(1~15 Enabled=1·Cap=3 / 16~20 Enabled=0·Cap=3).
    //   · 오더 0701-CELL-01~15 RUNNING(소터 매핑) + order_item(barcode=OrderNo) + N↔N 활성 배정.
    //   · 오더 0701-CELL-16 CANCELLED(order_item 보존, 활성 배정 없음).
    private static void SetupField20Cells(WcsDbContext db, long sorterId)
    {
        var now   = DateTime.UtcNow;
        var batch = db.WorkBatches.First();

        // 셀 1~20: 1~15 Enabled=1, 16~20 Enabled=0, 전부 Capacity=3.
        for (int cellNo = 1; cellNo <= PhysMax; cellNo++)
        {
            bool enabled = cellNo <= AvailMax;
            var cell = db.Cells.FirstOrDefault(c => c.DestinationId == sorterId && c.CellNo == cellNo);
            if (cell is null)
                db.Cells.Add(new Cell
                {
                    DestinationId = sorterId, CellNo = cellNo, Capacity = CellCap,
                    Enabled = enabled, CreatedAt = now,
                });
            else { cell.Capacity = CellCap; cell.Enabled = enabled; }
        }
        db.SaveChanges();

        // 오더/아이템/배정 1~15 (활성) + 오더 16 (CANCELLED).
        for (int n = 1; n <= 16; n++)
        {
            string orderNo = $"0701-CELL-{n:D2}";
            var order = db.Orders.FirstOrDefault(o => o.WorkBatchId == batch.Id && o.OrderNo == orderNo);
            if (order is null)
            {
                order = new WcsOrder
                {
                    WorkBatchId = batch.Id, OrderNo = orderNo, OrderType = OrderType.GENERAL,
                    DestinationId = sorterId, DestAssignType = DestAssignType.UPSTREAM, DestAssignedAt = now,
                    Status = n == 16 ? OrderStatus.CANCELLED : OrderStatus.RUNNING,
                    StartedAt = now, ClosedAt = n == 16 ? now : (DateTime?)null,
                    CreatedAt = now, UpdatedAt = now,
                };
                db.Orders.Add(order);
                db.SaveChanges();
            }

            if (!db.OrderItems.Any(i => i.OrderId == order.Id && i.Barcode == orderNo))
            {
                db.OrderItems.Add(new OrderItem
                {
                    OrderId = order.Id, Barcode = orderNo, PlannedQty = 100, ReservedQty = 0, SortedQty = 0,
                    CreatedAt = now, UpdatedAt = now,
                });
                db.SaveChanges();
            }

            // 활성 N↔N 배정은 1~15 만(셀 16 은 미배정 — 미매핑 완결 차단).
            if (n <= AvailMax)
            {
                var cell = db.Cells.First(c => c.DestinationId == sorterId && c.CellNo == n);
                if (!db.CellAssignments.Any(a => a.CellId == cell.Id && a.ReleasedAt == null))
                {
                    db.CellAssignments.Add(new CellAssignment
                    {
                        CellId = cell.Id, OrderId = order.Id, AssignedAt = now, ReleasedAt = null, CreatedAt = now,
                    });
                    db.SaveChanges();
                }
            }
        }
    }

    /// <summary>소터에 매핑된 신규 RUNNING 오더(배정 없음) — "빈 셀 필요" 경로 유발용.</summary>
    private static void AddRunningOrderNoAssignment(WcsDbContext db, long sorterId, string orderNo, string barcode)
    {
        var now   = DateTime.UtcNow;
        var batch = db.WorkBatches.First();
        var order = new WcsOrder
        {
            WorkBatchId = batch.Id, OrderNo = orderNo, OrderType = OrderType.GENERAL,
            DestinationId = sorterId, DestAssignType = DestAssignType.UPSTREAM, DestAssignedAt = now,
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
    }

    /// <summary>셀(cellNo)에 qty짜리 LOADED piece + COMPLETED sorter_command 1건 — 셀 현재수량 산출원.</summary>
    private static void LoadCellQty(WcsDbContext db, long sorterId, int cellNo, int qty, int pId, string barcode)
    {
        var now  = DateTime.UtcNow;
        var cell = db.Cells.First(c => c.DestinationId == sorterId && c.CellNo == cellNo);
        var piece = new Piece
        {
            PId = pId, IsActive = false, Barcode = barcode, Qty = qty, DepositedAt = now,
            DestinationId = sorterId, Status = PieceStatus.LOADED, CreatedAt = now, UpdatedAt = now,
        };
        db.Pieces.Add(piece);
        db.SaveChanges();
        db.SorterCommands.Add(new SorterCommand
        {
            PieceId = piece.Id, CellId = cell.Id, CSeq = 1, CellNo = cellNo, CWrittenAt = now,
            Status = SorterCommandStatus.COMPLETED, CreatedAt = now,
        });
        db.SaveChanges();
    }

    private static int UnoccupiedCellCount(WcsDbContext db, long sorterId, bool enabledOnly)
    {
        var occupied = db.CellAssignments
            .Where(a => a.Cell.DestinationId == sorterId && a.ReleasedAt == null)
            .Select(a => a.CellId).ToHashSet();
        return db.Cells.Count(c => c.DestinationId == sorterId
                                && (!enabledOnly || c.Enabled)
                                && !occupied.Contains(c.Id));
    }

    // ════════════════════════════════════════════════════════════════════════
    // T1: 1~15 만재(활성 배정 점유) + 16~20 Enabled=0 미배정 → SelectCell 이 16~20 미반환(null).
    //     음성 대조: 셀 16 을 Enabled=1 로 올리면 SelectCell 이 16 을 반환(매핑 확장 = 데이터-온리).
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task T1_SelectCell_NeverReturnsDisabledCells_16to20()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        _ = factory.CreateClient();

        long sorterId = factory.SorterDestinationId;
        int  chuteNo  = factory.SorterChuteNo;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            SetupField20Cells(db, sorterId);
            AddRunningOrderNoAssignment(db, sorterId, "ORD-NEW-A", "NEW-BC-A");

            // 가용(Enabled) 셀 전부 점유(1~15), 그러나 물리적으로 비어 있는 셀은 5개(16~20·비활성).
            Assert.Equal(0, UnoccupiedCellCount(db, sorterId, enabledOnly: true));
            Assert.Equal(5, UnoccupiedCellCount(db, sorterId, enabledOnly: false));
            Assert.Equal(20, db.Cells.Count(c => c.DestinationId == sorterId));
            Assert.Equal(15, db.Cells.Count(c => c.DestinationId == sorterId && c.Enabled));
        }

        using (var scope = factory.Services.CreateScope())
        {
            var selector = scope.ServiceProvider.GetRequiredService<ICellSelector>();

            // 빈 enabled 셀 0 → ②빈 셀 폴백이 Enabled 필터로 16~20 을 배제 → null.
            int? picked = selector.SelectCell(chuteNo, "NEW-BC-A");
            Assert.Null(picked);

            // 반복 호출해도 16~20 은 절대 나오지 않는다(null 유지).
            for (int i = 0; i < 5; i++)
            {
                int? p = selector.SelectCell(chuteNo, "NEW-BC-A");
                Assert.True(p is null || p.Value <= AvailMax, $"SelectCell 이 비활성 셀 반환: {p}");
            }
        }

        // 음성 대조 — 셀 16 을 Enabled=1 로 올리면 SelectCell 이 16 을 반환(게이트가 곧 Enabled).
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            db.Cells.First(c => c.DestinationId == sorterId && c.CellNo == 16).Enabled = true;
            db.SaveChanges();
        }
        using (var scope = factory.Services.CreateScope())
        {
            var selector = scope.ServiceProvider.GetRequiredService<ICellSelector>();
            int? picked = selector.SelectCell(chuteNo, "NEW-BC-A");
            Assert.Equal(16, picked);   // 매핑 확장(UPDATE Enabled=1)만으로 즉시 선택 가능.
        }
        _out.WriteLine("[T1] 1~15 만재·16~20 비활성 → SelectCell null(16~20 미반환). Enabled=1 승격 시 16 반환(음성 대조).");
    }

    // ════════════════════════════════════════════════════════════════════════
    // T2: IF-05 0701-CELL-01/-15 → OK(chuteNo=1) · 0701-CELL-16(CANCELLED) → NG(결정적).
    //     0701-CELL-16 은 오더 CANCELLED 라 QueryDestination 이 제외 → 1~15 점유와 무관하게 NG.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task T2_If05_Cells01And15_Ok_Cell16_Ng()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        var client = factory.CreateClient();

        long sorterId = factory.SorterDestinationId;

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            SetupField20Cells(db, sorterId);
        }

        // 0701-CELL-01 → OK (자기 배정 셀 1 여유: Cap=3, 현재 0). pId 는 1~30000 유효 범위.
        var ok01 = await PostIf05(client, pId: 20001, barcode: "0701-CELL-01");
        Assert.Equal("OK", ok01.Result);
        Assert.Equal(factory.SorterChuteNo, ok01.ChuteNo);

        // 0701-CELL-15 → OK (자기 배정 셀 15 여유).
        var ok15 = await PostIf05(client, pId: 20015, barcode: "0701-CELL-15");
        Assert.Equal("OK", ok15.Result);
        Assert.Equal(factory.SorterChuteNo, ok15.ChuteNo);

        // 0701-CELL-16 → NG (CANCELLED 오더 → 유효 목적지 없음).
        var ng16 = await PostIf05(client, pId: 20016, barcode: "0701-CELL-16");
        Assert.Equal("NG", ng16.Result);
        Assert.Null(ng16.ChuteNo);

        // 결정적 NG 확인: piece_event 사유 = NO_DEST(1~15 점유 여부와 무관).
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            var piece = db.Pieces.First(p => p.PId == 20016 && p.IsActive);
            var ev = db.PieceEvents.First(e => e.PieceId == piece.Id && e.EventType == PieceEventType.IF05_RES);
            Assert.Equal("NO_DEST", ev.Reason);
        }
        _out.WriteLine("[T2] IF-05 CELL-01 OK·CELL-15 OK·CELL-16 NG(NO_DEST, CANCELLED 오더).");
    }

    // ════════════════════════════════════════════════════════════════════════
    // T3: 가용 15셀 전량 만재(작업수량 도달) → SorterFull=true · 신규 바코드 IF-05 NG(FULL).
    //     비활성 셀 16~20 은 물리적으로 비어 있어도 full 산출에서 제외됨을 단정
    //     (포함됐다면 "빈 셀 있음"으로 SorterFull=false 가 되어야 하므로).
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task T3_All15EnabledCellsFull_SorterFull_And_If05_Ng()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        var client = factory.CreateClient();

        long sorterId = factory.SorterDestinationId;
        var status    = factory.Services.GetRequiredService<IDestinationStatusService>();

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            SetupField20Cells(db, sorterId);

            // 가용 15셀 전부 작업수량(Cap=3) 도달시킴 — 각 셀에 3개 적재.
            for (int cellNo = 1; cellNo <= AvailMax; cellNo++)
                LoadCellQty(db, sorterId, cellNo, qty: CellCap, pId: 41000 + cellNo, barcode: $"0701-CELL-{cellNo:D2}");

            AddRunningOrderNoAssignment(db, sorterId, "ORD-NEW-C", "NEW-BC-C");

            // 비활성 셀 16~20 은 비어 있으나(미점유) 가용 게이트 밖 — 빈 enabled 셀 0.
            Assert.Equal(0, UnoccupiedCellCount(db, sorterId, enabledOnly: true));
            Assert.Equal(5, UnoccupiedCellCount(db, sorterId, enabledOnly: false));
        }

        // SorterFull=true — 비활성 16~20 을 "빈 셀"로 세지 않음을 단정(세었다면 false).
        Assert.True(status.Compute(sorterId, DestType.SORTER_3D).Full,
            "가용 15셀 전량 만재 → SorterFull=true (비활성 16~20 은 full 산출 제외)");

        // 신규 바코드(배정 없음·빈 enabled 셀 없음) → IF-05 NG(FULL). pId 는 1~30000 유효 범위.
        var ng = await PostIf05(client, pId: 20100, barcode: "NEW-BC-C");
        Assert.Equal("NG", ng.Result);
        Assert.Null(ng.ChuteNo);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            var piece = db.Pieces.First(p => p.PId == 20100 && p.IsActive);
            var ev = db.PieceEvents.First(e => e.PieceId == piece.Id && e.EventType == PieceEventType.IF05_RES);
            Assert.Equal("FULL", ev.Reason);
        }
        _out.WriteLine("[T3] 가용 15셀 전량 만재 → SorterFull=true·신규 IF-05 NG(FULL). 비활성 16~20 full 산출 제외 단정.");
    }

    // ── 헬퍼: IF-05 destination-query POST ──────────────────────────────────────
    private static async Task<DestinationQueryResponse> PostIf05(HttpClient client, int pId, string barcode)
    {
        var req  = new { pId, agvNo = 1, barcode, inductionNo = 1, qty = 1, timeStamp = (string?)null };
        var resp = await client.PostAsJsonAsync("/api/v1/destination-query", req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);   // 도메인 거부도 200 + {result:"NG"}
        var body = await resp.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.NotNull(body);
        return body!;
    }
}
