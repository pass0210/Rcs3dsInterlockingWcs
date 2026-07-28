using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wcs.Api;
using Wcs.Data;
using Xunit;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// S-TWO-FLOOR-CONTROL C2 S2 — I-3 pending-floor 큐 재파생 복원 (읽기 전용) 검증
//
//   미완료 SORTER_3D piece 에서 소터별 큐를 재구성함을 실 WcsDbContext(named in-memory SQLite)로 검증한다.
//   커버: CC4(정확 집합·IF-05 순서) · CC6(종료/아카이브 제외·소터 교차 0) · VS-5/6/7 · piece 상태 변경 0.
// ════════════════════════════════════════════════════════════════════════════

public sealed class PendingFloorQueueRestorerTests : IAsyncLifetime
{
    private readonly string _dbName = $"RestoreTest_{Guid.NewGuid():N}";
    private SqliteConnection _anchor = null!;
    private ServiceProvider  _sp     = null!;
    private SorterPendingFloorQueues _queues = null!;
    private CapturingOperationLogger _opLog = null!;

    // 시드 destination.id (SeedAsync가 채움).
    private long _sorterA, _sorterB, _chuteC;

    public Task InitializeAsync()
    {
        _anchor = new SqliteConnection($"Data Source={_dbName};Mode=Memory;Cache=Shared");
        _anchor.Open();

        var services = new ServiceCollection();
        services.AddLogging();
        var connStr = $"Data Source={_dbName};Mode=Memory;Cache=Shared";
        services.AddDbContext<WcsDbContext>(o =>
            o.UseSqlite(connStr)
             .ConfigureWarnings(w => w.Ignore(
                 Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)),
            ServiceLifetime.Scoped);

        _opLog  = new CapturingOperationLogger();
        services.AddSingleton<IOperationLogger>(_opLog);
        _sp     = services.BuildServiceProvider();
        _queues = new SorterPendingFloorQueues();

        Seed();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _sp.Dispose();
        _anchor.Dispose();
        return Task.CompletedTask;
    }

    // 인덕션 맵 {1:1, 2:2} — 인덕션 99는 미매핑(Fail Loud 대상).
    private PendingFloorQueueRestorer NewRestorer() => new(
        _sp.GetRequiredService<IServiceScopeFactory>(),
        _queues,
        new NopTraceLogger(),
        Options.Create(new WcsOptions
        {
            InductionFloorMap = new Dictionary<string, int> { ["1"] = 1, ["2"] = 2 },
        }),
        NullLogger<PendingFloorQueueRestorer>.Instance);

    private void Seed()
    {
        var opts = new DbContextOptionsBuilder<WcsDbContext>()
            .UseSqlite(_anchor)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;
        using var db = new WcsDbContext(opts);
        db.Database.EnsureCreated();

        var now = DateTime.UtcNow;

        var a = new Destination { ChuteNo = 30, DestType = DestType.SORTER_3D, Status = DestStatus.NORMAL, IsActive = true, CreatedAt = now, UpdatedAt = now };
        var b = new Destination { ChuteNo = 31, DestType = DestType.SORTER_3D, Status = DestStatus.NORMAL, IsActive = true, CreatedAt = now, UpdatedAt = now };
        var c = new Destination { ChuteNo = 1,  DestType = DestType.CHUTE,     Status = DestStatus.NORMAL, IsActive = true, CreatedAt = now, UpdatedAt = now };
        db.Destinations.AddRange(a, b, c);
        db.SaveChanges();
        _sorterA = a.Id; _sorterB = b.Id; _chuteC = c.Id;

        var ind1  = new Induction { InductionNo = 1,  Floor = 1, Enabled = true, CreatedAt = now };
        var ind2  = new Induction { InductionNo = 2,  Floor = 2, Enabled = true, CreatedAt = now };
        var ind99 = new Induction { InductionNo = 99, Floor = 2, Enabled = true, CreatedAt = now };
        db.Inductions.AddRange(ind1, ind2, ind99);
        db.SaveChanges();

        int pid = 1;
        Piece P(long destId, long? indId, PieceStatus st, int order, bool active = true, DateTime? archived = null) => new()
        {
            PId = pid++, IsActive = active, Barcode = $"BC-{destId}-{st}-{order}", Qty = 1,
            DestinationId = destId, InductionId = indId, Status = st,
            CreatedAt = now.AddSeconds(order), UpdatedAt = now.AddSeconds(order), ArchivedAt = archived,
        };

        // 소터 A — 수용 확정·미LOADED (IF-05 순서: ind1→F1, ind1→F1, ind2→F2) → 큐 A = [1,1,2].
        db.Pieces.Add(P(_sorterA, ind1.Id,  PieceStatus.RESERVED,      1));
        db.Pieces.Add(P(_sorterA, ind1.Id,  PieceStatus.DEPOSITED,     2));
        db.Pieces.Add(P(_sorterA, ind2.Id,  PieceStatus.CELL_ASSIGNED, 3));
        // 소터 A — 종료/제외 상태(재편입 0): DENIED·LOADED·MISMATCH·TIMEOUT·CANCELLED·QUERIED.
        db.Pieces.Add(P(_sorterA, ind1.Id,  PieceStatus.DENIED,    10));
        db.Pieces.Add(P(_sorterA, ind1.Id,  PieceStatus.LOADED,    11));
        db.Pieces.Add(P(_sorterA, ind1.Id,  PieceStatus.MISMATCH,  12));
        db.Pieces.Add(P(_sorterA, ind1.Id,  PieceStatus.TIMEOUT,   13));
        db.Pieces.Add(P(_sorterA, ind1.Id,  PieceStatus.CANCELLED, 14));
        db.Pieces.Add(P(_sorterA, ind1.Id,  PieceStatus.QUERIED,   15));
        // 소터 A — 아카이브(재테스트 초기화)분 RESERVED → 제외(ArchivedAt≠null). PERMITTED 활성분은 포함.
        db.Pieces.Add(P(_sorterA, ind2.Id,  PieceStatus.RESERVED,  16, archived: now));
        db.Pieces.Add(P(_sorterA, ind1.Id,  PieceStatus.PERMITTED, 4));   // 큐 A 꼬리에 F1 추가 → [1,1,2,1].

        // 소터 B — RESERVED(ind2→F2) → 큐 B = [2] (소터 교차 0).
        db.Pieces.Add(P(_sorterB, ind2.Id,  PieceStatus.RESERVED, 1));

        // CHUTE C — RESERVED(SORTER_3D 아님) → 재편입 0.
        db.Pieces.Add(P(_chuteC, ind1.Id,   PieceStatus.RESERVED, 1));

        db.SaveChanges();
    }

    // ── VS-5/CC4: 정확 집합·IF-05 순서로 소터별 큐 복원 ─────────────────────────────
    [Fact]
    public async Task VS5_RestoresQueues_InIf05Order_ByDestination()
    {
        int restored = await NewRestorer().RestoreAsync();

        // 소터 A: RESERVED(F1)·DEPOSITED(F1)·CELL_ASSIGNED(F2)·PERMITTED(F1) → CreatedAt 순 [1,1,2,1].
        Assert.Equal(new[] { 1, 1, 2, 1 }, _queues.Snapshot(_sorterA));
        // 소터 B: RESERVED(F2) → [2]. 소터 교차 0.
        Assert.Equal(new[] { 2 }, _queues.Snapshot(_sorterB));
        // 재편입 총계 = A 4건 + B 1건 = 5.
        Assert.Equal(5, restored);
    }

    // ── VS-6/CC6: 종료·아카이브·CHUTE piece 재편입 0(음성 대조) ────────────────────────
    [Fact]
    public async Task VS6_ExcludesTerminalArchivedAndChute()
    {
        await NewRestorer().RestoreAsync();

        // CHUTE 목적지는 큐 대상 아님.
        Assert.Empty(_queues.Snapshot(_chuteC));
        // 소터 A 큐 길이 4 — 종료(6건)·아카이브(1건)는 제외됐다(포함됐다면 11).
        Assert.Equal(4, _queues.Count(_sorterA));
    }

    // ── VS-7/CC6: 미매핑 inductionNo piece → skip + 경보(Fail Loud) ──────────────────
    [Fact]
    public async Task VS7_UnmappedInduction_SkippedAndAlarmed()
    {
        // 소터 A에 미매핑 인덕션(99) RESERVED piece 추가.
        var opts = new DbContextOptionsBuilder<WcsDbContext>()
            .UseSqlite(_anchor)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;
        using (var db = new WcsDbContext(opts))
        {
            var ind99 = db.Inductions.First(i => i.InductionNo == 99);
            db.Pieces.Add(new Piece
            {
                PId = 500, IsActive = true, Barcode = "BC-UNMAPPED", Qty = 1,
                DestinationId = _sorterA, InductionId = ind99.Id, Status = PieceStatus.RESERVED,
                CreatedAt = DateTime.UtcNow.AddSeconds(50), UpdatedAt = DateTime.UtcNow.AddSeconds(50),
            });
            db.SaveChanges();
        }

        await NewRestorer().RestoreAsync();

        // 미매핑분은 재편입되지 않는다 → 큐 A 길이는 여전히 4(매핑분만).
        Assert.Equal(4, _queues.Count(_sorterA));
        // 경보(Fail Loud): I3_RESTORE_NO_FLOOR WARN 1건 이상.
        Assert.Contains(_opLog.Entries, e =>
            e.action == "I3_RESTORE_NO_FLOOR" && e.level == OperationLogLevel.WARN);
    }

    // ── piece 상태 변경 0(순수 읽기 재구성 — 단일 진실 보존) ─────────────────────────
    [Fact]
    public async Task Restore_DoesNotMutatePieceState()
    {
        await NewRestorer().RestoreAsync();

        var opts = new DbContextOptionsBuilder<WcsDbContext>()
            .UseSqlite(_anchor)
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;
        using var db = new WcsDbContext(opts);
        // 재파생 대상이던 A의 RESERVED/DEPOSITED/CELL_ASSIGNED/PERMITTED 상태가 그대로 유지됨.
        var statuses = db.Pieces
            .Where(p => p.DestinationId == _sorterA && p.IsActive && p.ArchivedAt == null)
            .Select(p => p.Status).ToList();
        Assert.Contains(PieceStatus.RESERVED, statuses);
        Assert.Contains(PieceStatus.DEPOSITED, statuses);
        Assert.Contains(PieceStatus.CELL_ASSIGNED, statuses);
        Assert.Contains(PieceStatus.PERMITTED, statuses);
    }
}

// ── 캡처형 IOperationLogger(테스트) — Fail Loud 경보 기록 검증용 ─────────────────────
public sealed class CapturingOperationLogger : IOperationLogger
{
    private readonly object _lock = new();
    private readonly List<(OperationLogCategory category, string action, OperationLogLevel level)> _entries = new();

    public IReadOnlyList<(OperationLogCategory category, string action, OperationLogLevel level)> Entries
    { get { lock (_lock) return _entries.ToList(); } }

    public void Log(OperationLog entry)
    {
        lock (_lock) _entries.Add((entry.Category, entry.Action, entry.Level));
    }

    public void Log(OperationLogCategory category, string action, OperationLogLevel level = OperationLogLevel.INFO,
        int? sorterChuteNo = null, long? destinationId = null, string? barcode = null, int? pId = null, string? detail = null)
    {
        lock (_lock) _entries.Add((category, action, level));
    }
}
