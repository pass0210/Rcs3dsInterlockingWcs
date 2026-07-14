using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wcs.Api.B2C;
using Wcs.Data;
using Wcs.Tests.B2B;   // TestDb(in-memory SQLite 격리 harness) 재사용(같은 어셈블리 internal).
using Xunit;

namespace Wcs.Tests.B2C;

// ════════════════════════════════════════════════════════════════════════════
// S-B2C-FACILITY 서비스 단위 테스트 — 생성 슬림(미할당 오더·OQ-4)·초기화 의미(불변)·아카이브 이중카운트 차단.
// TestDb(in-memory SQLite·EnsureCreated) 재사용. 서비스 직접 구동(HTTP 무관) + 캡처 IOperationLogger.
//
// ★ 슬림 계약(S-B2C-FACILITY): 생성은 오더/바코드만 만든다(목적지 미할당). 소터/셀/배정 자동 생성 제거 —
//   설비 관리(B2cFacilityService)로 이관. 초기화(reset) 의미는 불변(아카이브·수량 리셋·오더 재개·가드).
//   reset 테스트의 소터/셀/오더 셋업은 SeedSorter 헬퍼로 직접 조성(옛 generate 자동생성 대체).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>operation_log 감사(OQ8) 검증용 캡처 로거.</summary>
internal sealed class CapturingOperationLogger : IOperationLogger
{
    public readonly List<OperationLog> Entries = new();

    public void Log(OperationLog entry) => Entries.Add(entry);

    public void Log(OperationLogCategory category, string action,
        OperationLogLevel level = OperationLogLevel.INFO,
        int? sorterChuteNo = null, long? destinationId = null,
        string? barcode = null, int? pId = null, string? detail = null)
        => Entries.Add(new OperationLog
        {
            Category = category, Action = action, Level = level,
            SorterChuteNo = sorterChuteNo, DestinationId = destinationId,
            Barcode = barcode, PId = pId, Detail = detail, At = DateTime.UtcNow,
        });
}

public class B2cTestDataServiceTests
{
    private static B2cGenerateRequest Gen(int count = 5, string prefix = "CELL", string batch = "B1", int wave = 1) => new()
    {
        WorkDate = "2026-07-13", BatchNo = batch, WaveNo = wave, PlannedQty = count, BarcodePrefix = prefix,
    };

    // reset 테스트용: 소터 + 셀 N + 배치 + 셀당 오더(MANUAL 배정) + order_item + cell_assignment 직접 조성.
    // (옛 generate 의 소터/셀/N↔N 자동생성 대체 — 이제 그 책임은 설비 관리로 이관됨.)
    private static long SeedSorter(WcsDbContext db, int chuteNo, int cellCount, int cap = 3, int planned = 3)
    {
        var now = DateTime.UtcNow;
        var dest = new Destination
        {
            ChuteNo = chuteNo, DestType = DestType.SORTER_3D, Status = DestStatus.NORMAL, IsActive = true,
            CreatedAt = now, UpdatedAt = now,
        };
        db.Destinations.Add(dest);
        db.SaveChanges();

        var batch = new WorkBatch
        {
            WorkDate = DateOnly.FromDateTime(now), BatchNo = $"SEED-{chuteNo}", WaveNo = 1,
            Status = WorkBatchStatus.RUNNING, OpenedAt = now, CreatedAt = now, UpdatedAt = now,
        };
        db.WorkBatches.Add(batch);
        db.SaveChanges();

        for (int n = 1; n <= cellCount; n++)
        {
            var cell = new Cell { DestinationId = dest.Id, CellNo = n, Capacity = cap, Enabled = true, CreatedAt = now };
            db.Cells.Add(cell);
            db.SaveChanges();

            var order = new WcsOrder
            {
                WorkBatchId = batch.Id, OrderNo = $"S{chuteNo}-{n:D2}", OrderType = OrderType.GENERAL,
                DestinationId = dest.Id, DestAssignType = DestAssignType.MANUAL, DestAssignedAt = now,
                Status = OrderStatus.RUNNING, StartedAt = now, CreatedAt = now, UpdatedAt = now,
            };
            db.Orders.Add(order);
            db.SaveChanges();

            db.OrderItems.Add(new OrderItem
            {
                OrderId = order.Id, Barcode = order.OrderNo, PlannedQty = planned,
                ReservedQty = 0, SortedQty = 0, CreatedAt = now, UpdatedAt = now,
            });
            db.CellAssignments.Add(new CellAssignment
            {
                CellId = cell.Id, OrderId = order.Id, AssignedAt = now, ReleasedAt = null, CreatedAt = now,
            });
            db.SaveChanges();
        }
        return dest.Id;
    }

    // ── BuildOrderNumbers 순수·결정성 ─────────────────────────────────────────────
    [Fact]
    public void BuildOrderNumbers_Deterministic_ZeroPadded()
    {
        var plan = B2cTestDataService.BuildOrderNumbers(15, "0701-CELL");
        Assert.Equal(15, plan.Count);
        Assert.Equal("0701-CELL-01", plan[0]);
        Assert.Equal("0701-CELL-15", plan[14]);

        // 100개 → 폭 3(정렬 안정).
        var plan100 = B2cTestDataService.BuildOrderNumbers(100, "X");
        Assert.Equal("X-001", plan100[0]);
        Assert.Equal("X-100", plan100[99]);
    }

    // ── 생성(슬림): 미할당 오더 N + order_item(planned_qty=1) 만 — 소터/셀/배정 0 ────────
    [Fact]
    public async Task Generate_CreatesUnassignedOrdersOnly()
    {
        await using var tdb = new TestDb();
        var log = new CapturingOperationLogger();
        await using (var db = tdb.Create())
        {
            var svc = new B2cTestDataService(db, log);
            var res = await svc.GenerateAsync(Gen(count: 5, prefix: "A"));
            Assert.Equal("S", res.Status);
            Assert.Equal(5, res.Counts!["ordersCreated"]);
            Assert.Equal(5, res.Counts["orderItemsCreated"]);
            Assert.Equal(5, res.Counts["requestedCount"]);
        }
        await using (var db = tdb.Create())
        {
            // 오더 5건 전부 미할당(DestinationId=null·DestAssignType=null).
            var orders = db.Orders.Where(o => o.OrderNo.StartsWith("A-")).ToList();
            Assert.Equal(5, orders.Count);
            Assert.All(orders, o =>
            {
                Assert.Null(o.DestinationId);
                Assert.Null(o.DestAssignType);
                Assert.Equal(OrderStatus.RUNNING, o.Status);
            });
            // order_item planned_qty=1 고정(OQ-4), barcode==orderNo.
            var items = db.OrderItems.Where(i => i.Barcode.StartsWith("A-")).ToList();
            Assert.Equal(5, items.Count);
            Assert.All(items, i => Assert.Equal(1, i.PlannedQty));
            Assert.Contains(items, i => i.Barcode == "A-01");
            Assert.Contains(items, i => i.Barcode == "A-05");
            // 소터/셀/배정 자동 생성 0.
            Assert.Equal(0, db.Destinations.Count());
            Assert.Equal(0, db.Cells.Count());
            Assert.Equal(0, db.CellAssignments.Count());
        }
        Assert.Contains(log.Entries, e => e.Category == OperationLogCategory.STATE && e.Action == "B2C_GENERATE");
    }

    // ── 생성 멱등: 재실행 시 신규 카운트 0 + reserved/sorted 보존 ──────────────────────
    [Fact]
    public async Task Generate_Idempotent_SecondRunZeroCounts_PreservesQty()
    {
        await using var tdb = new TestDb();
        var log = new CapturingOperationLogger();

        await using (var db = tdb.Create())
            await new B2cTestDataService(db, log).GenerateAsync(Gen(count: 4, prefix: "B"));

        // 한 항목의 reserved/sorted 를 채운 상태로 둔다(재테스트 실적 시뮬레이션).
        await using (var db = tdb.Create())
        {
            var item = db.OrderItems.First();
            item.ReservedQty = 1; item.SortedQty = 1;
            await db.SaveChangesAsync();
        }

        // 같은 파라미터 재실행 → 신규 0.
        await using (var db = tdb.Create())
        {
            var res = await new B2cTestDataService(db, log).GenerateAsync(Gen(count: 4, prefix: "B"));
            Assert.Equal("S", res.Status);
            Assert.Equal(0, res.Counts!["ordersCreated"]);
            Assert.Equal(0, res.Counts["orderItemsCreated"]);
        }

        // reserved/sorted 는 재생성이 클로버하지 않음(멱등 — 실적 보존).
        await using (var db = tdb.Create())
        {
            var item = db.OrderItems.OrderBy(i => i.Id).First();
            Assert.Equal(1, item.ReservedQty);
            Assert.Equal(1, item.SortedQty);
        }
    }

    // ── 생성 결과 view: 배치 요약(미할당 오더 수 포함) ────────────────────────────────
    [Fact]
    public async Task GetBatches_ReportsUnassignedOrderCount()
    {
        await using var tdb = new TestDb();
        await using (var db = tdb.Create())
            await new B2cTestDataService(db, new CapturingOperationLogger()).GenerateAsync(Gen(count: 3, prefix: "C", batch: "BATCH-C"));

        await using (var db = tdb.Create())
        {
            var batches = await new B2cTestDataService(db, new CapturingOperationLogger()).GetBatchesAsync(20);
            var b = Assert.Single(batches, x => x.BatchNo == "BATCH-C");
            Assert.Equal(3, b.OrderTotal);
            Assert.Equal(3, b.OrderUnassigned);   // 슬림 생성 → 전부 미할당.
            Assert.Equal(3, b.ItemTotal);
        }
    }

    // ── 초기화(아카이브·OQ2 보존): 소프트삭제 + 수량 리셋 + 오더 재개(의미 불변) ──────────
    [Fact]
    public async Task Reset_SoftDeletesPieces_ResetsQty_ReopensCompleted_PreservesOrdersAndAssignments()
    {
        await using var tdb = new TestDb();
        var log = new CapturingOperationLogger();
        long sorterId, pieceId, cellId;
        await using (var db = tdb.Create())
            sorterId = SeedSorter(db, chuteNo: 1, cellCount: 3);

        await using (var db = tdb.Create())
        {
            cellId   = db.Cells.First(c => c.DestinationId == sorterId).Id;
            var order = db.Orders.First(o => o.DestinationId == sorterId);
            var item  = db.OrderItems.First(i => i.OrderId == order.Id);
            item.ReservedQty = 3; item.SortedQty = 3;
            order.Status = OrderStatus.COMPLETED; order.ClosedAt = DateTime.UtcNow;
            var piece = new Piece
            {
                PId = 5000, IsActive = true, Barcode = order.OrderNo, Qty = 3,
                DestinationId = sorterId, OrderItemId = item.Id, Status = PieceStatus.LOADED,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.Pieces.Add(piece);
            await db.SaveChangesAsync();
            pieceId = piece.Id;
            db.PieceEvents.Add(new PieceEvent { PieceId = pieceId, EventType = PieceEventType.IF10_RES, At = DateTime.UtcNow });
            db.SorterCommands.Add(new SorterCommand
            {
                PieceId = pieceId, CellId = cellId, CSeq = 1, CellNo = 1,
                CWrittenAt = DateTime.UtcNow.AddSeconds(1), Status = SorterCommandStatus.COMPLETED,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        await using (var db = tdb.Create())
        {
            var res = await new B2cTestDataService(db, log).ResetAsync(new B2cResetRequest { SorterChuteNo = 1, Force = true });
            Assert.Equal("S", res.Status);
            Assert.Equal(1, res.Counts!["archivedPieces"]);
            Assert.Equal(1, res.Counts["archivedPieceEvents"]);
            Assert.Equal(1, res.Counts["archivedSorterCommands"]);
            Assert.Equal(1, res.Counts["reopenedOrders"]);
        }

        await using (var db = tdb.Create())
        {
            // 하드삭제 0 — 행은 그대로 존재하되 archived_at 세팅(OQ1=B 핵심).
            Assert.Equal(1, db.Pieces.Count(p => p.Id == pieceId));
            Assert.NotNull(db.Pieces.Single(p => p.Id == pieceId).ArchivedAt);
            Assert.NotNull(db.PieceEvents.Single(e => e.PieceId == pieceId).ArchivedAt);
            Assert.NotNull(db.SorterCommands.Single(c => c.PieceId == pieceId).ArchivedAt);

            var order = db.Orders.First(o => o.DestinationId == sorterId);
            Assert.Equal(OrderStatus.RUNNING, order.Status);
            Assert.All(db.OrderItems.Where(i => i.OrderId == order.Id), i =>
            {
                Assert.Equal(0, i.ReservedQty);
                Assert.Equal(0, i.SortedQty);
            });
            Assert.Equal(3, db.Orders.Count(o => o.DestinationId == sorterId));           // 오더 보존
            Assert.Equal(3, db.CellAssignments.Count(a => a.Cell.DestinationId == sorterId)); // 배정 보존
        }
        Assert.Contains(log.Entries, e => e.Category == OperationLogCategory.STATE && e.Action == "B2C_RESET");
    }

    // ── ★ 이중카운트 차단(HIGHEST-STAKES): 아카이브 후 셀 currentQty=0 ──────────────
    [Fact]
    public async Task Reset_ArchivedSorterCommand_ExcludedFromCellCurrentQty()
    {
        await using var tdb = new TestDb();
        long sorterId, cellId;
        await using (var db = tdb.Create())
            sorterId = SeedSorter(db, chuteNo: 1, cellCount: 2);

        await using (var db = tdb.Create())
        {
            var assign = db.CellAssignments.First(a => a.Cell.DestinationId == sorterId);
            cellId = assign.CellId;
            var piece = new Piece
            {
                PId = 6000, IsActive = true, Barcode = "X", Qty = 2, DestinationId = sorterId,
                Status = PieceStatus.LOADED, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.Pieces.Add(piece);
            await db.SaveChangesAsync();
            db.SorterCommands.Add(new SorterCommand
            {
                PieceId = piece.Id, CellId = cellId, CSeq = 1, CellNo = 1,
                CWrittenAt = assign.AssignedAt.AddSeconds(1), Status = SorterCommandStatus.COMPLETED,
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        // 아카이브 전: 셀 currentQty = 2 (detail 은 SorterCellQty 재사용 — 블랙박스 검증).
        await using (var db = tdb.Create())
        {
            var detail = await new B2cTestDataService(db, new CapturingOperationLogger()).GetDetailAsync(1);
            Assert.Equal(2, detail.Single(c => c.CellNo == 1).CurrentQty);
        }

        await using (var db = tdb.Create())
            await new B2cTestDataService(db, new CapturingOperationLogger()).ResetAsync(new B2cResetRequest { SorterChuteNo = 1, Force = true });

        // 아카이브 후: 셀 currentQty = 0(이중 카운트 차단 — 재테스트 시 0부터).
        await using (var db = tdb.Create())
        {
            var detail = await new B2cTestDataService(db, new CapturingOperationLogger()).GetDetailAsync(1);
            Assert.Equal(0, detail.Single(c => c.CellNo == 1).CurrentQty);
        }
    }

    // ── OQ3 가드: in-flight piece 존재 시 force 없이는 거부(데이터 무접촉) ────────────
    [Fact]
    public async Task Reset_InFlightGuard_RefusesWithoutForce_DataUntouched()
    {
        await using var tdb = new TestDb();
        long sorterId, pieceId;
        await using (var db = tdb.Create())
            sorterId = SeedSorter(db, chuteNo: 1, cellCount: 2);

        await using (var db = tdb.Create())
        {
            var piece = new Piece
            {
                PId = 7000, IsActive = true, Barcode = "Y", Qty = 1, DestinationId = sorterId,
                Status = PieceStatus.RESERVED, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.Pieces.Add(piece);
            await db.SaveChangesAsync();
            pieceId = piece.Id;
        }

        await using (var db = tdb.Create())
        {
            var res = await new B2cTestDataService(db, new CapturingOperationLogger())
                .ResetAsync(new B2cResetRequest { SorterChuteNo = 1, Force = false });
            Assert.Equal("F", res.Status);
            Assert.Equal(1, res.Counts!["inFlight"]);
        }
        await using (var db = tdb.Create())
            Assert.Null(db.Pieces.Single(p => p.Id == pieceId).ArchivedAt);   // 무접촉.

        await using (var db = tdb.Create())
        {
            var res = await new B2cTestDataService(db, new CapturingOperationLogger())
                .ResetAsync(new B2cResetRequest { SorterChuteNo = 1, Force = true });
            Assert.Equal("S", res.Status);
        }
        await using (var db = tdb.Create())
            Assert.NotNull(db.Pieces.Single(p => p.Id == pieceId).ArchivedAt);
    }

    // ── 초기화 F: 대상 소터 미존재 ────────────────────────────────────────────────
    [Fact]
    public async Task Reset_UnknownSorter_ReturnsF()
    {
        await using var tdb = new TestDb();
        await using var db = tdb.Create();
        var res = await new B2cTestDataService(db, new CapturingOperationLogger())
            .ResetAsync(new B2cResetRequest { SorterChuteNo = 999, Force = false });
        Assert.Equal("F", res.Status);
    }

    // ── 코드리뷰 TOCTOU 회귀잠금: in-flight 가드 COUNT 가 트랜잭션 안에서 실행되는지 구조 단언 ──
    [Fact]
    public async Task Reset_InFlightGuard_CountExecutesInsideTransaction()
    {
        var connStr = $"Data Source=b2c_tx_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        await using var anchor = new SqliteConnection(connStr);
        anchor.Open();

        DbContextOptions<WcsDbContext> Opts(TxCaptureInterceptor? icpt)
        {
            var b = new DbContextOptionsBuilder<WcsDbContext>()
                .UseSqlite(connStr)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
            if (icpt is not null) b.AddInterceptors(icpt);
            return b.Options;
        }

        long destId;
        await using (var db = new WcsDbContext(Opts(null)))
        {
            db.Database.EnsureCreated();
            var dest = new Destination
            {
                ChuteNo = 1, DestType = DestType.SORTER_3D, Status = DestStatus.NORMAL, IsActive = true,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.Destinations.Add(dest);
            await db.SaveChangesAsync();
            destId = dest.Id;
            db.Pieces.Add(new Piece
            {
                PId = 8000, IsActive = true, Barcode = "TX", Qty = 1, DestinationId = destId,
                Status = PieceStatus.RESERVED, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var capture = new TxCaptureInterceptor();
        await using (var db = new WcsDbContext(Opts(capture)))
        {
            var res = await new B2cTestDataService(db, new CapturingOperationLogger())
                .ResetAsync(new B2cResetRequest { SorterChuteNo = 1, Force = false });
            Assert.Equal("F", res.Status);
            Assert.Equal(1, res.Counts!["inFlight"]);
        }

        var guardCounts = capture.Commands
            .Where(c => c.Sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase)
                     && c.Sql.Contains("piece", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(guardCounts);
        Assert.All(guardCounts, c =>
            Assert.True(c.InTransaction, "in-flight 가드 COUNT 가 트랜잭션 밖에서 실행됨(TOCTOU 창 회귀)"));

        await using (var db = new WcsDbContext(Opts(null)))
            Assert.Null(db.Pieces.Single(p => p.PId == 8000).ArchivedAt);
    }

    /// <summary>커맨드별 (SQL, 트랜잭션 참여 여부) 캡처 인터셉터 — 가드 위치 구조 단언용.</summary>
    private sealed class TxCaptureInterceptor : DbCommandInterceptor
    {
        public readonly List<(string Sql, bool InTransaction)> Commands = new();

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Commands.Add((command.CommandText, command.Transaction is not null));
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Commands.Add((command.CommandText, command.Transaction is not null));
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
