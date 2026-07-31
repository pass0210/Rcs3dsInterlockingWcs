using System.Net;
using System.Net.Http.Json;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Wcs.Api;
using Wcs.Data;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// S-AUDIT-C-DATA-INTEGRITY — 감사 묶음 C 데이터 정합 3건(①②⑤) RED-first 검증.
//
//   ① 동시 IF-05 예약 차감 원자화 — RMW→원자 조건부 UPDATE
//        (reserved_qty += qty WHERE reserved_qty+qty <= planned_qty, 영향행 0=OVER).
//        SQL Server 미처리 500·SQLite lost-update 동시 해소 + DENIED 감사기록 계약 보존.
//   ② 같은 pId 잔존 다중 활성 piece 전건 비활성화 — FirstOrDefault 1행→전건.
//        IF-10 부분유니크 위반→'멱등 OK' 위장유실+IF-11 미트리거 차단.
//   ⑤ SelectCell 미매칭 fail-loud — 매칭 RUNNING 오더 없으면 셀 반환 거부(null)
//        +alarm(CELL_ORDER_UNMATCHED). 혼적 벡터(빈 셀로 조용히 틸트) 원천 차단.
//
// 검증 층위:
//   ②·⑤·①(happy/boundary) = 직접 SQLite 리포지토리 하네스(결정적).
//   ①(동시 IF-05 lost-update 부재) = 실 HTTP 병렬(RcsPushWebApplicationFactory·F4 패턴).
//   ★ 실 SQL Server 동시성 실증(rowversion 패자 500)은 SQLite 재현 불가(lessons 실 prod
//      provider) → Evaluator concurrency 차원이 실 SQL Server ≥5회로 수행. 여기선 provider
//      무관 원자 갱신 경로(reserved_qty 이중 가산 부재)를 SQLite 로 입증한다.
// ════════════════════════════════════════════════════════════════════════════

public sealed class DataIntegrityAuditTests
{
    private readonly ITestOutputHelper _out;
    public DataIntegrityAuditTests(ITestOutputHelper output) => _out = output;

    // ── 직접 SQLite 하네스(named in-memory·shared cache) ─────────────────────────
    private sealed class SeededDb : IDisposable
    {
        public WcsDbContext Db { get; }
        private readonly SqliteConnection _conn;
        public long SorterDestId { get; }
        public int  SorterChuteNo => 30;

        public SeededDb()
        {
            _conn = new SqliteConnection($"Data Source=DataIntegrity_{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            _conn.Open();
            var opts = new DbContextOptionsBuilder<WcsDbContext>()
                .UseSqlite(_conn)
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options;
            Db = new WcsDbContext(opts);
            Db.Database.EnsureCreated();
            DbSeeder.Seed(Db, new Dictionary<string, int> { ["1"] = 1, ["2"] = 2 });
            SorterDestId = Db.Destinations.First(d => d.DestType == DestType.SORTER_3D && d.IsActive).Id;
        }

        public void Dispose() { Db.Dispose(); _conn.Dispose(); }
    }

    // 소터/슈트 목적지 수용 가능(availability None) — QueryDestination 예약 경로만 검증.
    private static DestinationBlock AllowAll(long id, DestinationType dt) => DestinationBlock.None;

    // ⑤ 셀 선택기 생성 — 생성자 시그니처 변경 국소화(fail-loud alarm 결선 후 갱신).
    private static EfCellSelector MakeSelector(WcsDbContext db) =>
        new EfCellSelector(db, new EfAlarmSink(db), NullLogger<EfCellSelector>.Instance);

    // ════════════════════════════════════════════════════════════════════════
    // 항목 ① — 원자 예약 차감
    // ════════════════════════════════════════════════════════════════════════

    // [S-① happy] 단건 IF-05 OK → reserved_qty 가 정확히 +qty(원자 갱신·회귀 0).
    [Fact]
    public void S1_AtomicReservation_SingleOk_IncrementsExactly()
    {
        using var h = new SeededDb();
        var db = h.Db;
        Assert.Equal(0, db.OrderItems.Single(i => i.Barcode == "TEST-BARCODE-1").ReservedQty);

        var repo = new EfOrderRepository(db);
        var (result, chuteNo, reason, _, _) = repo.QueryDestination(
            14001, 1, "TEST-BARCODE-1", 1, qty: 3, clientTs: null, availability: AllowAll);

        Assert.Equal("OK", result);
        Assert.Equal(1, chuteNo);
        Assert.Equal("NORMAL", reason);

        db.ChangeTracker.Clear();
        Assert.Equal(3, db.OrderItems.Single(i => i.Barcode == "TEST-BARCODE-1").ReservedQty);
        Assert.Equal(1, db.Pieces.Count(p => p.PId == 14001 && p.IsActive && p.Status == PieceStatus.RESERVED));
        _out.WriteLine("[S-① happy] 단건 IF-05 OK → reserved_qty 정확히 +3(원자 갱신).");
    }

    // [S-①b] 동시 IF-05(같은 SKU·planned=1) → 원자 갱신 최종 권위:
    //   미처리 500 0건 + 정확히 1건 OK(초과예약 0) + 패자 전원 DENIED(OVER) 계약 보존.
    //   수정 전 RED: SQLite lost-update(전원 stale read=0 → RMW 전원 성공) → OK 다수·RESERVED 다수.
    [Fact]
    public async Task S1b_ConcurrentIf05_AtomicReservation_NoOverReserve_DeniedPreserved()
    {
        await using var rcs     = await FakeChuteStateServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        _ = factory.CreateClient();

        // CHUTE 오더(TEST-BARCODE-1)를 planned=1·reserved=0 으로 조여 동시 경합에서 1건만 수용.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            var item = db.OrderItems.Single(i => i.Barcode == "TEST-BARCODE-1");
            item.PlannedQty = 1; item.ReservedQty = 0; item.UpdatedAt = DateTime.UtcNow;
            db.SaveChanges();
        }

        const int concurrency = 8;
        using var barrier = new Barrier(concurrency);
        var tasks = Enumerable.Range(0, concurrency).Select(i => Task.Run(async () =>
        {
            using var client = factory.CreateClient();
            barrier.SignalAndWait();
            var resp = await client.PostAsJsonAsync("/api/v1/destination-query",
                new { pId = 14100 + i, agvNo = 1, barcode = "TEST-BARCODE-1", inductionNo = 1, qty = 1, timeStamp = (string?)null });
            var body = await resp.Content.ReadFromJsonAsync<DestinationQueryResponse>();
            return (resp.StatusCode, body!.Result);
        })).ToArray();
        var results = await Task.WhenAll(tasks);

        // 미처리 500 0건(전부 200) — 어느 동시 요청도 감사기록 없이 소실되지 않음.
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        int okCount = results.Count(r => r.Result == "OK");
        int ngCount = results.Count(r => r.Result == "NG");
        Assert.Equal(1, okCount);                 // 정확히 1건 수용(초과예약 0).
        Assert.Equal(concurrency - 1, ngCount);   // 나머지 전원 NG(OVER).

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            var item = db.OrderItems.Single(i => i.Barcode == "TEST-BARCODE-1");
            Assert.Equal(1, item.ReservedQty);                 // reserved_qty == planned(=1) — 이중 가산 0.
            Assert.True(item.ReservedQty <= item.PlannedQty);  // 초과예약 0.
            // RESERVED 활성 piece 정확히 1건(over-commit 0).
            Assert.Equal(1, db.Pieces.Count(p => p.Barcode == "TEST-BARCODE-1" && p.IsActive && p.Status == PieceStatus.RESERVED));
            // DENIED 계약 보존 — 패자 전원 DENIED piece + IF05_RES(OVER) piece_event.
            Assert.Equal(concurrency - 1, db.Pieces.Count(p => p.Barcode == "TEST-BARCODE-1" && p.Status == PieceStatus.DENIED));
            Assert.Equal(concurrency - 1, db.PieceEvents.Count(e => e.EventType == PieceEventType.IF05_RES && e.Reason == "OVER"));
        }
        _out.WriteLine($"[S-①b] 동시 IF-05 {concurrency} → OK {okCount}·NG {ngCount}·reserved 1·DENIED(OVER) {concurrency - 1}(계약 보존).");
    }

    // ════════════════════════════════════════════════════════════════════════
    // 항목 ② — 이전 활성 piece 전건 비활성화
    // ════════════════════════════════════════════════════════════════════════

    // [S-②] 같은 pId 잔존 다중 활성 2행 → IF-05 전건 비활성화(잔존 0) →
    //   정상 IF-10 이 위장유실(false) 없이 DEPOSITED 로 전이.
    //   구 코드(무정렬 FirstOrDefault 1행) RED: 잔존 활성 DEPOSITED → IF-10 부분유니크 위반 → false.
    [Fact]
    public void S2_MultiActivePieces_If05DeactivatesAll_If10NotFalselyIdempotent()
    {
        using var h = new SeededDb();
        var db = h.Db;
        const int pid = 15001;
        var now = DateTime.UtcNow;

        // 잔존 다중 활성 2행: RESERVED 를 먼저(낮은 Id) → DEPOSITED 를 나중(높은 Id) 삽입.
        //   구 무정렬 FirstOrDefault 는 낮은 Id(RESERVED)만 비활성화 → 활성 DEPOSITED 가 잔존.
        //   (부분 유니크 UQ_piece_pid_active_status 상 활성 DEPOSITED 는 정확히 1행만 가능.)
        db.Pieces.Add(new Piece { PId = pid, IsActive = true, Barcode = "TEST-BARCODE-3", Qty = 1,
            DestinationId = h.SorterDestId, Status = PieceStatus.RESERVED, CreatedAt = now, UpdatedAt = now });
        db.SaveChanges();
        db.Pieces.Add(new Piece { PId = pid, IsActive = true, Barcode = "TEST-BARCODE-3", Qty = 1,
            DepositedAt = now, DestinationId = h.SorterDestId, Status = PieceStatus.DEPOSITED, CreatedAt = now, UpdatedAt = now });
        db.SaveChanges();
        Assert.Equal(2, db.Pieces.Count(p => p.PId == pid && p.IsActive && p.ArchivedAt == null));

        // ── IF-05: 신규 목적지 조회 → 이전 활성 전건 비활성화 + 신규 RESERVED piece C ──
        var repo = new EfOrderRepository(db);
        var (result, chuteNo, _, _, _) = repo.QueryDestination(
            pid, 1, "TEST-BARCODE-3", 1, qty: 1, clientTs: null, availability: AllowAll);
        Assert.Equal("OK", result);
        Assert.Equal(30, chuteNo);

        db.ChangeTracker.Clear();
        var actives = db.Pieces.Where(p => p.PId == pid && p.IsActive && p.ArchivedAt == null).ToList();
        Assert.Single(actives);                                    // 전건 비활성화 → 잔존 0·신규 C 1건. [수정 전 RED: 2건]
        Assert.Equal(PieceStatus.RESERVED, actives[0].Status);
        long pieceCId = actives[0].Id;

        // ── IF-10: 투입 보고 → 신규 C 를 DEPOSITED 전이(잔존 활성 DEPOSITED 없음 → 부분유니크 위반 0). ──
        db.ChangeTracker.Clear();
        var recorder = new EfDepositRecorder(db);
        bool isNew = recorder.RecordDeposit(pid, "TEST-BARCODE-3", 30, 1, qty: 1, clientTs: null);
        Assert.True(isNew, "IF-10 이 위장유실(false) 없이 신규 투입 기록 — 전건 비활성화로 잔존 활성 DEPOSITED 제거");

        db.ChangeTracker.Clear();
        Assert.Equal(PieceStatus.DEPOSITED, db.Pieces.Single(p => p.Id == pieceCId).Status);
        _out.WriteLine("[S-②] 잔존 다중 활성 2행 → IF-05 전건 비활성화(잔존 0) → IF-10 위장유실 없이 DEPOSITED.");
    }

    // [S-② NG] RecordDenied(NG) 경로도 이전 활성 piece 전건 비활성화.
    [Fact]
    public void S2_MultiActivePieces_RecordDenied_DeactivatesAll()
    {
        using var h = new SeededDb();
        var db = h.Db;
        const int pid = 15002;
        var now = DateTime.UtcNow;

        db.Pieces.Add(new Piece { PId = pid, IsActive = true, Barcode = "TEST-BARCODE-3", Qty = 1,
            DestinationId = h.SorterDestId, Status = PieceStatus.RESERVED, CreatedAt = now, UpdatedAt = now });
        db.SaveChanges();
        db.Pieces.Add(new Piece { PId = pid, IsActive = true, Barcode = "TEST-BARCODE-3", Qty = 1,
            DepositedAt = now, DestinationId = h.SorterDestId, Status = PieceStatus.DEPOSITED, CreatedAt = now, UpdatedAt = now });
        db.SaveChanges();

        // 미매핑 바코드 → NO_DEST NG → RecordDenied 경로.
        var repo = new EfOrderRepository(db);
        var (result, _, reason, _, _) = repo.QueryDestination(
            pid, 1, "NO-SUCH-BARCODE", 1, qty: 1, clientTs: null, availability: AllowAll);
        Assert.Equal("NG", result);
        Assert.Equal("NO_DEST", reason);

        db.ChangeTracker.Clear();
        // 전건 비활성화 → 활성 piece 는 신규 DENIED 1건뿐(잔존 RESERVED·DEPOSITED 전부 비활성). [수정 전 RED: 3건]
        var actives = db.Pieces.Where(p => p.PId == pid && p.IsActive && p.ArchivedAt == null).ToList();
        Assert.Single(actives);
        Assert.Equal(PieceStatus.DENIED, actives[0].Status);
        _out.WriteLine("[S-② NG] RecordDenied 경로도 이전 활성 전건 비활성화(잔존 0).");
    }

    // ════════════════════════════════════════════════════════════════════════
    // 항목 ⑤ — SelectCell 미매칭 fail-loud
    // ════════════════════════════════════════════════════════════════════════

    // [S-⑤a] IF-10 빈 셀 분기에서 매칭 RUNNING 오더 없음 → 셀 반환 거부(null)+alarm(CELL_ORDER_UNMATCHED).
    //   구 코드 RED: 배정 없이 빈 셀 cellNo 를 조용히 반환(혼적 벡터).
    [Fact]
    public void S5a_SelectCell_UnmatchedOrder_ReturnsNull_RaisesAlarm()
    {
        using var h = new SeededDb();
        var db = h.Db;

        // 빈 셀 3개 존재(시드). 매칭 RUNNING 오더 없는 바코드(오타/REPORTED_DIRECT).
        Assert.Equal(3, db.Cells.Count(c => c.DestinationId == h.SorterDestId && c.Enabled));

        var selector = MakeSelector(db);
        int? cell = selector.SelectCell(h.SorterChuteNo, "TYPO-NO-MATCHING-ORDER");

        Assert.Null(cell);   // 셀 반환 거부(IF-11/틸트 미트리거). [수정 전 RED: cellNo 반환]
        // fail-loud alarm 1건.
        Assert.Equal(1, db.Alarms.Count(a => a.Code == "CELL_ORDER_UNMATCHED"));
        // 유령 점유 0 — cell_assignment 생성 안 함.
        Assert.Equal(0, db.CellAssignments.Count(a => a.Cell.DestinationId == h.SorterDestId && a.ReleasedAt == null));
        _out.WriteLine("[S-⑤a] 미매칭 → null+CELL_ORDER_UNMATCHED alarm 1건·유령 점유 0(혼적 차단).");
    }

    // [S-⑤b] 정상 매칭(RUNNING 오더 존재) → 기존대로 cell_assignment 생성 + 셀 반환·alarm 0(회귀 0).
    [Fact]
    public void S5b_SelectCell_MatchedOrder_ReturnsCell_NoAlarm_Regression()
    {
        using var h = new SeededDb();
        var db = h.Db;

        var selector = MakeSelector(db);
        int? cell = selector.SelectCell(h.SorterChuteNo, "TEST-BARCODE-3");   // ORD-003 RUNNING.

        Assert.NotNull(cell);
        Assert.Equal(0, db.Alarms.Count(a => a.Code == "CELL_ORDER_UNMATCHED"));
        Assert.Equal(1, db.CellAssignments.Count(a => a.Cell.DestinationId == h.SorterDestId && a.ReleasedAt == null));
        _out.WriteLine($"[S-⑤b] 정상 매칭 → cellNo={cell}·cell_assignment 1·alarm 0(회귀 0).");
    }
}
