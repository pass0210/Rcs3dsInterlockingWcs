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
// S-B2C-DATAGEN 서비스 단위 테스트 — 생성 멱등(OQ4)·초기화 의미(OQ1·OQ2·OQ3)·아카이브 이중카운트 차단.
// TestDb(in-memory SQLite·EnsureCreated) 재사용. 서비스 직접 구동(HTTP 무관) + 캡처 IOperationLogger.
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
    private static B2cGenerateRequest Gen(int chute = 1, int cells = 5, string prefix = "CELL",
        string batch = "B1", int cap = 3, int planned = 3) => new()
    {
        SorterChuteNo = chute, WorkDate = "2026-07-13", BatchNo = batch, WaveNo = 1,
        CellCount = cells, CellCapacity = cap, PlannedQty = planned, OrderPrefix = prefix,
    };

    // ── BuildPlan 순수·결정성 ────────────────────────────────────────────────
    [Fact]
    public void BuildPlan_Deterministic_NxN_ZeroPadded()
    {
        var plan = B2cTestDataService.BuildPlan(15, "0701-CELL");
        Assert.Equal(15, plan.Count);
        Assert.Equal((1, "0701-CELL-01"), plan[0]);
        Assert.Equal((15, "0701-CELL-15"), plan[14]);

        // 100셀 → 폭 3(정렬 안정).
        var plan100 = B2cTestDataService.BuildPlan(100, "X");
        Assert.Equal("X-001", plan100[0].OrderNo);
        Assert.Equal("X-100", plan100[99].OrderNo);
    }

    // ── 생성: 소터·셀·오더·항목·배정 생성 + 요약 반영 ────────────────────────────
    [Fact]
    public async Task Generate_CreatesSorterCellsOrdersItemsAssignments()
    {
        await using var tdb = new TestDb();
        var log = new CapturingOperationLogger();
        await using (var db = tdb.Create())
        {
            var svc = new B2cTestDataService(db, log);
            var res = await svc.GenerateAsync(Gen(cells: 5));
            Assert.Equal("S", res.Status);
            Assert.Equal(1, res.Counts!["destinationCreated"]);
            Assert.Equal(5, res.Counts["cellsCreated"]);
            Assert.Equal(5, res.Counts["ordersCreated"]);
            Assert.Equal(5, res.Counts["orderItemsCreated"]);
            Assert.Equal(5, res.Counts["cellAssignmentsCreated"]);
        }
        await using (var db = tdb.Create())
        {
            var svc = new B2cTestDataService(db, log);
            var summary = await svc.GetSummaryAsync(1);
            var s = Assert.Single(summary);
            Assert.Equal(1, s.ChuteNo);
            Assert.Equal(5, s.CellTotal);
            Assert.Equal(5, s.CellEnabled);
            Assert.Equal(5, s.CellAssigned);
            Assert.Equal(5, s.OrderRunning);
            Assert.Equal(15, s.PlannedSum);   // 5 × 3
            Assert.Equal(0, s.InFlightPieces);
        }
        // 감사(OQ8): STATE·B2C_GENERATE 기록.
        Assert.Contains(log.Entries, e => e.Category == OperationLogCategory.STATE && e.Action == "B2C_GENERATE");
    }

    // ── 생성 멱등(OQ4): 재실행 시 신규 카운트 0 + reserved/sorted 보존 ──────────────
    [Fact]
    public async Task Generate_Idempotent_SecondRunZeroCounts_PreservesQty()
    {
        await using var tdb = new TestDb();
        var log = new CapturingOperationLogger();

        await using (var db = tdb.Create())
            await new B2cTestDataService(db, log).GenerateAsync(Gen(cells: 4));

        // 한 항목의 reserved/sorted 를 사람이 채운 상태로 둔다(재테스트 실적 시뮬레이션).
        await using (var db = tdb.Create())
        {
            var item = db.OrderItems.First();
            item.ReservedQty = 2; item.SortedQty = 1;
            await db.SaveChangesAsync();
        }

        // 같은 파라미터 재실행 → 신규 0.
        await using (var db = tdb.Create())
        {
            var res = await new B2cTestDataService(db, log).GenerateAsync(Gen(cells: 4));
            Assert.Equal("S", res.Status);
            Assert.Equal(0, res.Counts!["destinationCreated"]);
            Assert.Equal(0, res.Counts["cellsCreated"]);
            Assert.Equal(0, res.Counts["ordersCreated"]);
            Assert.Equal(0, res.Counts["orderItemsCreated"]);
            Assert.Equal(0, res.Counts["cellAssignmentsCreated"]);
        }

        // reserved/sorted 는 재생성이 클로버하지 않음(멱등 — 실적 보존).
        await using (var db = tdb.Create())
        {
            var item = db.OrderItems.OrderBy(i => i.Id).First();
            Assert.Equal(2, item.ReservedQty);
            Assert.Equal(1, item.SortedQty);
        }
    }

    // ── 생성 F: chuteNo 가 CHUTE 타입으로 점유됨 ─────────────────────────────────
    [Fact]
    public async Task Generate_ChuteTypeOccupied_ReturnsF()
    {
        await using var tdb = new TestDb();
        await using (var db = tdb.Create())
        {
            db.Destinations.Add(new Destination
            {
                ChuteNo = 7, DestType = DestType.CHUTE, Status = DestStatus.NORMAL, IsActive = true,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        await using (var db = tdb.Create())
        {
            var res = await new B2cTestDataService(db, new CapturingOperationLogger()).GenerateAsync(Gen(chute: 7));
            Assert.Equal("F", res.Status);
            Assert.Contains("SORTER_3D", res.Message);
        }
    }

    // ── 초기화(OQ1=B 아카이브·OQ2 보존): 소프트삭제 + 수량 리셋 + 오더 재개 ──────────
    [Fact]
    public async Task Reset_SoftDeletesPieces_ResetsQty_ReopensCompleted_PreservesOrdersAndAssignments()
    {
        await using var tdb = new TestDb();
        var log = new CapturingOperationLogger();
        await using (var db = tdb.Create())
            await new B2cTestDataService(db, log).GenerateAsync(Gen(cells: 3));

        long sorterId, pieceId, cellId;
        await using (var db = tdb.Create())
        {
            sorterId = db.Destinations.Single(d => d.ChuteNo == 1).Id;
            cellId   = db.Cells.First(c => c.DestinationId == sorterId).Id;
            var order = db.Orders.First(o => o.DestinationId == sorterId);
            var item  = db.OrderItems.First(i => i.OrderId == order.Id);
            // 완료된 테스트 시뮬레이션: 오더 COMPLETED, 수량 채움, LOADED piece + COMPLETED sorter_command.
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

        // 초기화(force 불필요 — LOADED 는 in-flight 이므로 실제론 force 필요. 여기선 force=true 로 확정 검증).
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

            // 수량 0 리셋 + 오더 재개(RUNNING) + 오더·배정 보존(OQ2).
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
        await using (var db = tdb.Create())
            await new B2cTestDataService(db, new CapturingOperationLogger()).GenerateAsync(Gen(cells: 2));

        long sorterId, cellId;
        await using (var db = tdb.Create())
        {
            sorterId = db.Destinations.Single(d => d.ChuteNo == 1).Id;
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
        await using (var db = tdb.Create())
            await new B2cTestDataService(db, new CapturingOperationLogger()).GenerateAsync(Gen(cells: 2));

        long sorterId, pieceId;
        await using (var db = tdb.Create())
        {
            sorterId = db.Destinations.Single(d => d.ChuteNo == 1).Id;
            var piece = new Piece
            {
                PId = 7000, IsActive = true, Barcode = "Y", Qty = 1, DestinationId = sorterId,
                Status = PieceStatus.RESERVED, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            };
            db.Pieces.Add(piece);
            await db.SaveChangesAsync();
            pieceId = piece.Id;
        }

        // force=false → 거부(F) + piece 무접촉(archived_at NULL 유지).
        await using (var db = tdb.Create())
        {
            var res = await new B2cTestDataService(db, new CapturingOperationLogger())
                .ResetAsync(new B2cResetRequest { SorterChuteNo = 1, Force = false });
            Assert.Equal("F", res.Status);
            Assert.Equal(1, res.Counts!["inFlight"]);
        }
        await using (var db = tdb.Create())
            Assert.Null(db.Pieces.Single(p => p.Id == pieceId).ArchivedAt);   // 무접촉.

        // force=true → 진행 중 포함 아카이브.
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

    // ── FIX ITER 3(코드리뷰 TOCTOU): in-flight 가드 COUNT 가 **트랜잭션 안**에서 실행되는지 구조 단언 ──
    //   가드가 트랜잭션 밖이면 "체크 → 아카이브 UPDATE" 사이에 IF-05 삽입 창(TOCTOU)이 생겨
    //   비강제 reset 이 진행 중 piece 를 조용히 아카이브(OQ3 위반). 커맨드 인터셉터로 거부 경로의
    //   piece COUNT(이 경로 유일의 COUNT)가 DbTransaction 참여 상태로 실행됐음을 단언한다 —
    //   가드가 트랜잭션 밖으로 되돌아가는 회귀 시 즉시 RED.
    [Fact]
    public async Task Reset_InFlightGuard_CountExecutesInsideTransaction()
    {
        // TestDb 는 인터셉터 주입 불가 → 자체 anchor(공유 in-memory SQLite) 하네스.
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

        // 스키마 + 시드(소터 + in-flight RESERVED piece) — 인터셉터 없는 컨텍스트로(DDL 노이즈 제외).
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

        // 거부 경로 실행(인터셉터 부착) — force=false + in-flight 1 → F.
        var capture = new TxCaptureInterceptor();
        await using (var db = new WcsDbContext(Opts(capture)))
        {
            var res = await new B2cTestDataService(db, new CapturingOperationLogger())
                .ResetAsync(new B2cResetRequest { SorterChuteNo = 1, Force = false });
            Assert.Equal("F", res.Status);
            Assert.Equal(1, res.Counts!["inFlight"]);
        }

        // 거부 경로의 piece COUNT(가드) 전부가 트랜잭션 참여 상태였는지 — TOCTOU 협착의 구조 증거.
        var guardCounts = capture.Commands
            .Where(c => c.Sql.Contains("COUNT", StringComparison.OrdinalIgnoreCase)
                     && c.Sql.Contains("piece", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.NotEmpty(guardCounts);   // 가드 COUNT 실행됨(공허 통과 차단).
        Assert.All(guardCounts, c =>
            Assert.True(c.InTransaction, "in-flight 가드 COUNT 가 트랜잭션 밖에서 실행됨(TOCTOU 창 회귀)"));

        // 무접촉 재확인: 거부 후 piece 는 여전히 활성(archived_at NULL).
        await using (var db = new WcsDbContext(Opts(null)))
            Assert.Null(db.Pieces.Single(p => p.PId == 8000).ArchivedAt);
    }

    /// <summary>커맨드별 (SQL, 트랜잭션 참여 여부) 캡처 인터셉터 — 가드 위치(트랜잭션 안/밖) 구조 단언용.</summary>
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
