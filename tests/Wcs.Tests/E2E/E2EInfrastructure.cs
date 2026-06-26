using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api;
using Wcs.Core;
using Wcs.Data;
using Wcs.PlcGateway;
using Wcs.Sim3ds;
using Xunit;

namespace Wcs.Tests.E2E;

// ════════════════════════════════════════════════════════════════════════════
// S-E2E-MULTI-AGV — 다중 AGV 전(全) 플로우 E2E 공용 인프라
//
// 계약 §3.1 + §9 재사용 갭 해소:
//   기존 SimWebApplicationFactory(ScenarioTests)는 실 Sim + 실 핸드셰이크를 제공하나
//   푸시(IF-08)는 비활성이고, RcsPushWebApplicationFactory(RcsPushTests)는 fake 번들이라
//   핸드셰이크 ground-truth가 없다. 이 팩토리는 둘의 능력을 합친다:
//     ① 실 Sim3ds N대(동적 포트) — 실 Modbus TCP 핸드셰이크·정렬·고장주입(SimServer).
//     ② production SorterRegistryFactory 그대로 — DB 주도 소터 판별 + 소터별 번들 N대.
//        (Fake/Nop 레지스트리 교체 안 함 — 진짜 핸드셰이크 ground-truth.)
//     ③ Wcs:RcsPush:BaseUrl → FakeRcsServer 결선 — DestinationStatusPusher 활성(실 push 수신).
//     ④ 실 EF DB(named in-memory SQLite, 인스턴스별 Guid) — sorter_command·cell_assignment·
//        piece·alarm·셀수량 ground-truth.
//
// production 변경 0(계약 §3.2): RegisterMap·DepositDecider·DestinationStatusService·
//   DbSeeder 토폴로지·핸드셰이크 무변경. 다중 소터(A6·F5)는 **테스트 측 추가 시드**로 둘째
//   SORTER_3D destination·셀·order를 더하고 config Sorters[]를 2항목으로 구성한다.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 한 소터(=SORTER_3D destination 1개)에 대응하는 실 Sim3ds + config 1세트.
/// 다중 소터(A6·F5)면 둘 이상.
/// </summary>
public sealed class SorterSimSlot
{
    public required int       ChuteNo { get; init; }
    public required int       Port    { get; init; }
    public required SimServer Sim     { get; init; }
    /// <summary>이 소터의 DB destination.id (DB 시드 후 채워짐).</summary>
    public long DestinationId { get; set; }
}

/// <summary>
/// 다중 AGV 전 플로우 E2E 팩토리.
/// 실 Sim3ds N대 + production SorterRegistryFactory + FakeRcs push 수신 + 실 EF DB.
///
/// 단일 소터(기본): chuteNo=30(시드 그대로) Sim 1대.
/// 다중 소터: extraSorterChuteNos에 둘째 chuteNo(예: 31)를 주면 그 SORTER_3D destination·
///   셀·order를 테스트 시드로 추가하고 Sim 2대·config Sorters[] 2항목을 배선한다.
/// </summary>
public sealed class E2EWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly int[]    _extraSorterChuteNos;   // 시드 chuteNo=30 외에 추가할 SORTER_3D chuteNo들
    private readonly string?  _rcsBaseUrl;            // FakeRcs base URL(푸시 수신). null이면 푸시 비활성.
    private readonly int      _initialCurFloor;       // Sim 초기 CurFloor(2=즉시 정렬 / 1=미정렬)
    private readonly int      _rFlagTimeoutMs;
    private readonly int      _sorterObserveIntervalMs;

    private readonly List<SorterSimSlot> _slots = [];
    private readonly object              _tlLock = new();
    private readonly List<string>        _timeline = [];

    private readonly string _dbName = $"E2ETest_{Guid.NewGuid():N}";
    private readonly Microsoft.Data.Sqlite.SqliteConnection _anchor;

    // 기본 소터(시드 chuteNo=30)
    public const int DefaultSorterChuteNo = 30;

    /// <summary>전 소터 슬롯(실 Sim + DB destination.id).</summary>
    public IReadOnlyList<SorterSimSlot> Slots => _slots;

    /// <summary>첫 소터(기본 chuteNo=30) 슬롯.</summary>
    public SorterSimSlot PrimarySorter => _slots[0];

    public IReadOnlyList<string> Timeline { get { lock (_tlLock) { return _timeline.ToList(); } } }

    public E2EWebApplicationFactory(
        string?   rcsBaseUrl              = null,
        int[]?    extraSorterChuteNos     = null,
        int       initialCurFloor         = 2,
        int       rFlagTimeoutMs          = 3000,
        int       sorterObserveIntervalMs = 30)
    {
        _rcsBaseUrl              = rcsBaseUrl;
        _extraSorterChuteNos     = extraSorterChuteNos ?? [];
        _initialCurFloor         = initialCurFloor;
        _rFlagTimeoutMs          = rFlagTimeoutMs;
        _sorterObserveIntervalMs = sorterObserveIntervalMs;

        _anchor = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={_dbName};Mode=Memory;Cache=Shared");
        _anchor.Open();
    }

    // ── 실 Sim3ds N대 기동 (ConfigureWebHost 전에 호출 — 포트 확정 필요) ──────────
    /// <summary>실 Sim3ds를 소터 수만큼 기동(동적 포트). 호스트 기동(CreateClient) 전에 호출.</summary>
    public async Task StartSimsAsync()
    {
        var chuteNos = new List<int> { DefaultSorterChuteNo };
        chuteNos.AddRange(_extraSorterChuteNos);

        foreach (var chuteNo in chuteNos)
        {
            int port = GetFreePort();
            var simOpt = new SimServer.Options
            {
                Host            = "127.0.0.1",
                Port            = port,
                TiltDelayMs     = 50,
                SortDurationMs  = 100,
                MoveDurationMs  = 80,
                InitialCurFloor = _initialCurFloor,
                SimLoopMs       = 10,
            };
            var sim = new SimServer(simOpt, timelineLog: line =>
            {
                lock (_tlLock) { _timeline.Add($"[chute{chuteNo}] {line}"); }
            });
            await sim.StartAsync();
            _slots.Add(new SorterSimSlot { ChuteNo = chuteNo, Port = port, Sim = sim });
        }
    }

    /// <summary>슬롯 인덱스의 Sim 재기동(OFFLINE→ONLINE 복구 — G2).</summary>
    public async Task RestartSimAsync(int slotIndex)
    {
        var slot = _slots[slotIndex];
        await slot.Sim.DisposeAsync();
        var simOpt = new SimServer.Options
        {
            Host            = "127.0.0.1",
            Port            = slot.Port,
            TiltDelayMs     = 50,
            SortDurationMs  = 100,
            MoveDurationMs  = 80,
            InitialCurFloor = _initialCurFloor,
            SimLoopMs       = 10,
        };
        var sim = new SimServer(simOpt, timelineLog: line =>
        {
            lock (_tlLock) { _timeline.Add($"[chute{slot.ChuteNo}] {line}"); }
        });
        await sim.StartAsync();
        // 슬롯 교체(record-like 갱신).
        _slots[slotIndex] = new SorterSimSlot
        {
            ChuteNo = slot.ChuteNo, Port = slot.Port, Sim = sim, DestinationId = slot.DestinationId,
        };
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var dict = new Dictionary<string, string?>
            {
                ["Timing:RFlagPollMs"]    = "20",
                ["Timing:RFlagTimeoutMs"] = _rFlagTimeoutMs.ToString(),
                ["Timing:CFlagTimeoutMs"] = "2000",
                // 운영층 — 2층 고정 정렬(설정 경유, 하드코딩 금지 — 절대규칙 #7)
                ["Wcs:OperationalFloor"]  = "2",
            };

            // Sorters[] — 실 Sim 포트별 config 항목(production SorterRegistryFactory가 소비).
            for (int i = 0; i < _slots.Count; i++)
            {
                var slot = _slots[i];
                dict[$"Sorters:{i}:ChuteNo"]              = slot.ChuteNo.ToString();
                dict[$"Sorters:{i}:Transport"]            = "Tcp";
                dict[$"Sorters:{i}:Host"]                 = "127.0.0.1";
                dict[$"Sorters:{i}:Port"]                 = slot.Port.ToString();
                dict[$"Sorters:{i}:PollIntervalMs"]       = "30";
                dict[$"Sorters:{i}:OfflineAfterFailures"] = "3";
                dict[$"Sorters:{i}:WriteTimeoutMs"]       = "500";
            }

            // RcsPush — FakeRcs로 결선(푸시 활성). null이면 비활성(BaseUrl 미설정).
            if (_rcsBaseUrl is not null)
            {
                dict["Wcs:RcsPush:BaseUrl"]                 = _rcsBaseUrl;
                dict["Wcs:RcsPush:RetryCount"]              = "3";
                dict["Wcs:RcsPush:RetryBaseDelayMs"]        = "30";
                dict["Wcs:RcsPush:RetryMaxDelayMs"]         = "120";
                dict["Wcs:RcsPush:HttpTimeoutMs"]           = "2000";
                dict["Wcs:RcsPush:SorterObserveIntervalMs"] = _sorterObserveIntervalMs.ToString();
            }

            cfg.AddInMemoryCollection(dict);
        });

        builder.ConfigureServices(services =>
        {
            // WcsDbContext → named in-memory SQLite(인스턴스별 Guid).
            var dbDescriptors = services
                .Where(d => d.ServiceType == typeof(DbContextOptions<WcsDbContext>)
                         || d.ServiceType == typeof(WcsDbContext))
                .ToList();
            foreach (var d in dbDescriptors)
                services.Remove(d);

            var connStr = $"Data Source={_dbName};Mode=Memory;Cache=Shared";
            services.AddDbContext<WcsDbContext>(opts =>
                opts.UseSqlite(connStr, sqlite => sqlite.CommandTimeout(30))
                    .ConfigureWarnings(w => w.Ignore(
                        Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)),
                ServiceLifetime.Scoped);

            // 스키마 + 시드(production DbSeeder — 토폴로지 무변경) + 다중 소터 추가 시드.
            var dbOpts = new DbContextOptionsBuilder<WcsDbContext>()
                .UseSqlite(_anchor)
                .ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
                .Options;
            using var db = new WcsDbContext(dbOpts);
            db.Database.EnsureCreated();
            DbSeeder.Seed(db, new Dictionary<string, int> { ["1"] = 1, ["2"] = 2 });

            // 추가 SORTER_3D(다중 소터) — production DbSeeder 변경 금지 → 테스트 측 시드.
            foreach (var chuteNo in _extraSorterChuteNos)
                SeedExtraSorter(db, chuteNo);

            // 각 슬롯의 DB destination.id를 채운다(테스트가 destId로 단언).
            foreach (var slot in _slots)
            {
                var dest = db.Destinations
                    .First(d => d.ChuteNo == slot.ChuteNo && d.DestType == DestType.SORTER_3D && d.IsActive);
                slot.DestinationId = dest.Id;
            }
            // production SorterRegistryFactory·DestinationStatusPusher·ChuteCapacityService는 교체하지 않는다.
            // (Fake/Nop 미사용 — 실 핸드셰이크 + 실 push ground-truth.)
        });
    }

    /// <summary>
    /// 둘째(이후) SORTER_3D destination + 셀 3개 + 매핑 오더(barcode)를 테스트 시드로 추가.
    /// production DbSeeder는 단일 소터(chuteNo=30)만 시드하므로 다중 소터(A6·F5) 배선용.
    /// barcode 규칙: "SORTER-{chuteNo}-BC" — 그 소터로 라우팅되는 바코드.
    /// </summary>
    private static void SeedExtraSorter(WcsDbContext db, int chuteNo)
    {
        if (db.Destinations.Any(d => d.ChuteNo == chuteNo)) return;

        var now = DateTime.UtcNow;
        var dest = new Destination
        {
            ChuteNo = chuteNo, DestType = DestType.SORTER_3D,
            Status = DestStatus.NORMAL, IsActive = true, CreatedAt = now, UpdatedAt = now,
        };
        db.Destinations.Add(dest);
        db.SaveChanges();

        for (int cellNo = 1; cellNo <= 3; cellNo++)
            db.Cells.Add(new Cell
            {
                DestinationId = dest.Id, CellNo = cellNo, Capacity = null, Enabled = true, CreatedAt = now,
            });
        db.SaveChanges();

        var batch = db.WorkBatches.First();
        var order = new WcsOrder
        {
            WorkBatchId = batch.Id, OrderNo = $"ORD-SORTER-{chuteNo}", OrderType = OrderType.GENERAL,
            DestinationId = dest.Id, DestAssignType = DestAssignType.UPSTREAM, DestAssignedAt = now,
            Status = OrderStatus.RUNNING, StartedAt = now, CreatedAt = now, UpdatedAt = now,
        };
        db.Orders.Add(order);
        db.SaveChanges();
        db.OrderItems.Add(new OrderItem
        {
            OrderId = order.Id, Barcode = BarcodeForSorter(chuteNo), PlannedQty = 100,
            ReservedQty = 0, SortedQty = 0, CreatedAt = now, UpdatedAt = now,
        });
        db.SaveChanges();
    }

    /// <summary>그 소터(chuteNo)로 라우팅되는 바코드. 기본 소터(30)는 시드 TEST-BARCODE-3.</summary>
    public static string BarcodeForSorter(int chuteNo) =>
        chuteNo == DefaultSorterChuteNo ? "TEST-BARCODE-3" : $"SORTER-{chuteNo}-BC";

    // ── 비동기 종료(teardown 채널 경쟁 회피 — 기존 팩토리 패턴) ──────────────────
    public override async ValueTask DisposeAsync()
    {
        // 실 Sim 먼저 종료(소켓 accept 루프 정리) → 베이스(IHost) 종료(소터 폴링 쓰기큐 완료) → 앵커.
        foreach (var slot in _slots)
            await slot.Sim.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
        _anchor.Dispose();
        GC.SuppressFinalize(this);
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    Task IAsyncLifetime.DisposeAsync()    => DisposeAsync().AsTask();

    protected override void Dispose(bool disposing)
    {
        // 동기 Dispose 경로(파이널라이저)에서도 sync-over-async 회피 — 앵커만 정리.
        if (disposing)
            _anchor.Dispose();
        base.Dispose(disposing);
    }

    // ── DB 조회 헬퍼 ────────────────────────────────────────────────────────────

    /// <summary>새 WcsDbContext 스코프(테스트가 DB 행 직접 조회 — ground-truth).</summary>
    public WcsDbContext CreateDbScope()
    {
        var connStr = $"Data Source={_dbName};Mode=Memory;Cache=Shared";
        var opts = new DbContextOptionsBuilder<WcsDbContext>()
            .UseSqlite(connStr, sqlite => sqlite.CommandTimeout(30))
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new WcsDbContext(opts);
    }

    /// <summary>해당 소터(destId)의 최신 PLC 스냅샷(정렬 관찰용).</summary>
    public PlcSnapshot? SorterSnapshot(long destId)
    {
        var registry = Services.GetService<ISorterGatewayRegistry>();
        return registry?.GetLatest(destId);
    }

    /// <summary>해당 소터(destId)가 Online인지.</summary>
    public bool IsSorterOnline(long destId) => SorterSnapshot(destId)?.Online ?? false;

    public static int GetFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
