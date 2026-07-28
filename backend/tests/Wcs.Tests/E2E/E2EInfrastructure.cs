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
//   발신(IF-08)은 비활성이고, RcsPushWebApplicationFactory(RcsPushTests)는 fake 번들이라
//   핸드셰이크 ground-truth가 없다. 이 팩토리는 둘의 능력을 합친다:
//     ① 실 Sim3ds N대(동적 포트) — 실 Modbus TCP 핸드셰이크·정렬·고장주입(SimServer).
//     ② production SorterRegistryFactory 그대로 — DB 주도 소터 판별 + 소터별 번들 N대.
//        (Fake/Nop 레지스트리 교체 안 함 — 진짜 핸드셰이크 ground-truth.)
//     ③ Wcs:ChuteStatePush:BaseUrl → FakeChuteStateServer 결선 — DestinationStatusPusher 활성(실 발신 수신).
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
    /// <summary>Modbus 슬레이브 주소. 멀티 포트(기본)=1 / 공유 버스=슬롯별 상이(같은 포트에 unitId로 구분).</summary>
    public byte UnitId { get; init; } = 1;
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
    private readonly string?  _rcsBaseUrl;            // 가짜 RCS base URL(레거시 단일 호스트). null이면 발신 비활성(DORMANT).
    private readonly IReadOnlyDictionary<int, string>? _floorHosts;  // S-TWO-FLOOR-CONTROL B — 층→호스트 맵(제공 시 층별 라우팅).
    private readonly int      _initialCurFloor;       // Sim 초기 CurFloor(2=즉시 정렬 / 1=미정렬)
    private readonly int      _rFlagTimeoutMs;
    private readonly int      _sorterObserveIntervalMs;
    private readonly int      _settleDelayMs;         // S-IF10-CWRITE-SETTLE-DELAY — 안착 지연(0=미주입·현행).
    private readonly string?  _traceLogDir;           // S-TRACE-LOG-VIEWER — 전용 추적 로그 per-test scratch 디렉터리(null=env 기본).
    private readonly int      _simLoopMs;             // Sim 상태기계 루프 주기(ms). 기본 10. 크게 주면 C_Flag=1 dwell↑
                                                      //   (현장 PLC 처럼 C_Flag 유지 → WCS 폴이 1→0 델타를 관측 — 이벤트 6 결정성).

    // 인덕션 기반 2층 제어: inductionNo→floor 맵. 기본은 1→2·2→2(기존 E2E는 induction=1로 층2 정렬 기대).
    // 폐루프 1↔2 테스트는 커스텀 맵({1:1,2:2})을 주입해 induction 1→층1·2→층2를 구동한다.
    private readonly IReadOnlyDictionary<int, int> _inductionFloorMap;

    // C2 S2(I-3): 표준 시드 + 슬롯 DestinationId 확정 후 추가 시드 훅(호스트 기동 전 실행 — 재파생 복원 대상
    // 미완료 piece 시드용). db·슬롯을 받아 임의 행을 삽입하고 SaveChanges 한다(같은 in-memory DB).
    private readonly Action<WcsDbContext, IReadOnlyList<SorterSimSlot>>? _seedExtra;

    // S-MULTISORTER-SHARED-BUS Phase 2 — 공유 버스 모드(한 포트·멀티유닛 Sim 1대에 여러 SORTER_3D를 unitId로 구분).
    private readonly (int ChuteNo, byte UnitId)[] _sharedBusUnits;
    private readonly bool _induceSerialMismatch;       // fail-loud 재현(OQ4): 같은 버스 멤버 BaudRate 불일치.
    private readonly bool _induceDuplicateUnitId;      // fail-loud 재현(OQ11): 같은 버스 UnitId 중복.
    private readonly bool _inducePollIntervalMismatch; // fail-loud 재현(OQ9-i): 같은 버스 PollIntervalMs 불일치.
    private readonly bool _induceTimeoutMismatch;      // fail-loud 재현(CR-I1): 같은 버스 Read/WriteTimeoutMs 불일치.

    // C4 fix 테스트 — 실 레지스트리 경로에 read-timeout 주입 데코레이터를 끼운다(솔로 재연결 복구 검증).
    private readonly bool _injectTimeoutConnection;
    private readonly object _connLock = new();
    private readonly List<TimeoutInjectingSharedConnection> _injectedConns = [];
    /// <summary>주입된 timeout 데코레이터(솔로 재연결 테스트 — 실 레지스트리가 생성).</summary>
    public IReadOnlyList<TimeoutInjectingSharedConnection> InjectedConnections
    { get { lock (_connLock) { return _injectedConns.ToList(); } } }

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
        int       sorterObserveIntervalMs = 30,
        (int ChuteNo, byte UnitId)[]? sharedBusUnits = null,
        bool      induceSerialMismatch     = false,
        bool      induceDuplicateUnitId    = false,
        bool      inducePollIntervalMismatch = false,
        bool      induceTimeoutMismatch    = false,
        bool      injectTimeoutConnection  = false,
        IReadOnlyDictionary<int, int>? inductionFloorMap = null,
        IReadOnlyDictionary<int, string>? floorHosts = null,
        Action<WcsDbContext, IReadOnlyList<SorterSimSlot>>? seedExtra = null,
        int       settleDelayMs           = 0,
        string?   traceLogDir             = null,
        int       simLoopMs               = 10)
    {
        _simLoopMs                  = simLoopMs;
        _traceLogDir                = traceLogDir;
        _rcsBaseUrl                 = rcsBaseUrl;
        _floorHosts                 = floorHosts;
        _seedExtra                  = seedExtra;
        _extraSorterChuteNos        = extraSorterChuteNos ?? [];
        _initialCurFloor            = initialCurFloor;
        _rFlagTimeoutMs             = rFlagTimeoutMs;
        _sorterObserveIntervalMs    = sorterObserveIntervalMs;
        _settleDelayMs              = settleDelayMs;
        _inductionFloorMap          = inductionFloorMap ?? new Dictionary<int, int> { [1] = 2, [2] = 2 };
        _sharedBusUnits             = sharedBusUnits ?? [];
        _induceSerialMismatch       = induceSerialMismatch;
        _induceDuplicateUnitId      = induceDuplicateUnitId;
        _inducePollIntervalMismatch = inducePollIntervalMismatch;
        _induceTimeoutMismatch      = induceTimeoutMismatch;
        _injectTimeoutConnection    = injectTimeoutConnection;

        _anchor = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={_dbName};Mode=Memory;Cache=Shared");
        _anchor.Open();
    }

    // ── 실 Sim3ds N대 기동 (ConfigureWebHost 전에 호출 — 포트 확정 필요) ──────────
    /// <summary>실 Sim3ds를 소터 수만큼 기동(동적 포트). 호스트 기동(CreateClient) 전에 호출.</summary>
    public async Task StartSimsAsync()
    {
        // ── 공유 버스 모드 (S-MULTISORTER-SHARED-BUS Phase 2) ────────────────────
        // 한 포트에 멀티유닛 SimServer 1대를 세우고, 그 포트를 공유하는 여러 SORTER_3D를 서로 다른
        // unitId로 배선한다(같은 Host:Port → 같은 TCP 버스 키 → production SorterRegistryFactory가
        // ModbusBus 1개로 그룹핑). 전송=Tcp(테스트 vehicle — 실 COM1/RTU 금지).
        if (_sharedBusUnits.Length > 0)
        {
            int port = GetFreePort();
            var unitIds = _sharedBusUnits.Select(u => u.UnitId).ToArray();
            var simOpt = new SimServer.Options
            {
                Host            = "127.0.0.1",
                Port            = port,
                TiltDelayMs     = 50,
                SortDurationMs  = 100,
                MoveDurationMs  = 80,
                InitialCurFloor = _initialCurFloor,
                SimLoopMs       = _simLoopMs,
            };
            var sim = new SimServer(simOpt, unitIds, timelineLog: line =>
            {
                lock (_tlLock) { _timeline.Add($"[port{port}] {line}"); }
            });
            await sim.StartAsync();
            foreach (var (chuteNo, unitId) in _sharedBusUnits)
                _slots.Add(new SorterSimSlot { ChuteNo = chuteNo, Port = port, Sim = sim, UnitId = unitId });
            return;
        }

        // ── 멀티 포트 모드(기존 기본) — 소터당 별도 포트·UnitId=1(서로 다른 버스 키 = 독립 병렬) ──
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
                SimLoopMs       = _simLoopMs,
            };
            var sim = new SimServer(simOpt, timelineLog: line =>
            {
                lock (_tlLock) { _timeline.Add($"[chute{chuteNo}] {line}"); }
            });
            await sim.StartAsync();
            _slots.Add(new SorterSimSlot { ChuteNo = chuteNo, Port = port, Sim = sim, UnitId = 1 });
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
            SimLoopMs       = _simLoopMs,
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
        // base appsettings=SqlServer → 테스트 더블은 인메모리 SQLite. host setting으로 Provider=Sqlite를
        // Program의 builder.Configuration 읽기 전에 주입(provider 충돌 회피). connection은 아래
        // ConfigureServices가 named in-memory(anchor)로 재등록(provider 결선만).
        builder.UseSetting("Database:Provider", "Sqlite");

        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var dict = new Dictionary<string, string?>
            {
                ["Timing:RFlagPollMs"]    = "20",
                ["Timing:RFlagTimeoutMs"] = _rFlagTimeoutMs.ToString(),
                ["Timing:CFlagTimeoutMs"] = "2000",
                // 운영층 — 레거시 참조값(정렬은 인덕션 파생 큐 구동 — 절대규칙 #7)
                ["Wcs:OperationalFloor"]  = "2",
                // 소터 pending-floor 큐 관측 주기(폐루프 트리거) — 폴 주기 동급으로 빠르게.
                ["Wcs:SorterFloorReturn:ObserveIntervalMs"] = "30",
            };

            // S-TRACE-LOG-VIEWER — 전용 추적 로그 per-test scratch 디렉터리(실경로 D:\ 무접촉). 지정 시 env 기본 override.
            if (_traceLogDir is not null)
                dict["TraceLog:Directory"] = _traceLogDir;

            // S-IF10-CWRITE-SETTLE-DELAY — 안착 지연 주입(테스트 opt-in). 0(기본)이면 미주입 → 현행 동작
            // 바이트 동일(기존 E2E 회귀 0). 양수면 공통 Timing:SettleDelayMs로 결선(소터 전체 적용).
            if (_settleDelayMs > 0)
                dict["Timing:SettleDelayMs"] = _settleDelayMs.ToString();

            // 인덕션 기반 2층 제어: inductionNo→floor 맵 결선(테스트별 커스텀 — 폐루프 1↔2 구동).
            foreach (var (induction, floorNo) in _inductionFloorMap)
                dict[$"Wcs:InductionFloorMap:{induction}"] = floorNo.ToString();

            // Sorters[] — 실 Sim 포트별 config 항목(production SorterRegistryFactory가 소비).
            // 공유 버스 모드: 여러 슬롯이 같은 Host:Port + 서로 다른 UnitId(같은 TCP 버스 키로 그룹핑).
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

                // UnitId 바인딩(배경 5 — 기존 인프라는 미바인딩이라 전부 기본 1). 멀티 포트=1(바이트 동일),
                // 공유 버스=슬롯별 상이. fail-loud(OQ11): 중복 유도 시 전부 1로 덮어 같은 버스 UnitId 충돌.
                byte unitId = _induceDuplicateUnitId ? (byte)1 : slot.UnitId;
                dict[$"Sorters:{i}:UnitId"] = unitId.ToString();

                // fail-loud(OQ4): 둘째 멤버 BaudRate만 불일치시켜 같은 버스 시리얼 파라미터 충돌 유발.
                // (TCP에선 실제로 미사용이나 레지스트리 정합 검사 대상 — 실 COM 무접촉으로 재현.)
                if (_induceSerialMismatch && i > 0)
                    dict[$"Sorters:{i}:BaudRate"] = "19200";

                // fail-loud(OQ9-i): 둘째 멤버 PollIntervalMs만 불일치시켜 같은 버스 폴 주기 충돌 유발.
                if (_inducePollIntervalMismatch && i > 0)
                    dict[$"Sorters:{i}:PollIntervalMs"] = "77";

                // fail-loud(CR-I1): 둘째 멤버 ReadTimeoutMs만 불일치시켜 같은 버스 연결 타임아웃 충돌 유발.
                // (WriteTimeoutMs는 위에서 전 슬롯 500 고정 → ReadTimeoutMs 기본 1000 vs 2000 불일치로 재현.)
                if (_induceTimeoutMismatch && i > 0)
                    dict[$"Sorters:{i}:ReadTimeoutMs"] = "2000";
            }

            // ChuteStatePush(확정 와이어 UpdateChuteState) — 가짜 RCS로 결선(발신 활성). 아무것도 없으면 DORMANT.
            //   S-TWO-FLOOR-CONTROL B: floorHosts 제공 시 층→호스트 맵으로(층별 라우팅). 아니면 레거시 BaseUrl.
            if (_floorHosts is not null || _rcsBaseUrl is not null)
            {
                if (_floorHosts is not null)
                    foreach (var (floor, host) in _floorHosts)
                        dict[$"Wcs:ChuteStatePush:FloorHosts:{floor}"] = host;
                else
                    dict["Wcs:ChuteStatePush:BaseUrl"] = _rcsBaseUrl;

                dict["Wcs:ChuteStatePush:RetryCount"]              = "3";
                dict["Wcs:ChuteStatePush:RetryBaseDelayMs"]        = "30";
                dict["Wcs:ChuteStatePush:RetryMaxDelayMs"]         = "120";
                dict["Wcs:ChuteStatePush:HttpTimeoutMs"]           = "2000";
                dict["Wcs:ChuteStatePush:SorterObserveIntervalMs"] = _sorterObserveIntervalMs.ToString();
            }

            cfg.AddInMemoryCollection(dict);
        });

        builder.ConfigureServices(services =>
        {
            // C4 fix 테스트 — 실 레지스트리가 만드는 공유 연결을 read-timeout 주입 데코레이터로 감싼다.
            // SorterRegistryFactory는 ISharedModbusConnectionFactory(DI 등록 시)를 사용하므로 실 경로 그대로.
            if (_injectTimeoutConnection)
                services.AddSingleton<Wcs.PlcGateway.ISharedModbusConnectionFactory>(
                    new TimeoutInjectingConnectionFactory(this));

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

            // 공유 버스 모드의 비-기본(≠30) chuteNo도 테스트 시드(SeedExtraSorter는 idempotent).
            foreach (var (chuteNo, _) in _sharedBusUnits)
                if (chuteNo != DefaultSorterChuteNo)
                    SeedExtraSorter(db, chuteNo);

            // 각 슬롯의 DB destination.id를 채운다(테스트가 destId로 단언).
            foreach (var slot in _slots)
            {
                var dest = db.Destinations
                    .First(d => d.ChuteNo == slot.ChuteNo && d.DestType == DestType.SORTER_3D && d.IsActive);
                slot.DestinationId = dest.Id;
            }

            // C2 S2(I-3): 추가 시드 훅 — 호스트 기동(hosted service StartAsync) 전에 미완료 piece 등을 주입해
            // 재파생 복원(RestoreAsync)이 그 piece 를 관측 루프 소비 전에 큐로 복원함을 실증한다.
            _seedExtra?.Invoke(db, _slots);

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
        // 공유 버스 모드는 여러 슬롯이 같은 SimServer를 참조 → Distinct(참조 동일성)로 이중 dispose 방지.
        foreach (var sim in _slots.Select(s => s.Sim).Distinct())
            await sim.DisposeAsync().ConfigureAwait(false);
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

    /// <summary>chuteNo로 슬롯 조회(공유 버스 unit 고장주입·라우팅용).</summary>
    public SorterSimSlot Sorter(int chuteNo) => _slots.First(s => s.ChuteNo == chuteNo);

    /// <summary>
    /// 구성된 물리 버스 진단(공유 연결/포트/마스터 1개 = 버스 1개).
    /// 공유 버스 = 버스 1개·멤버 N / 멀티 포트 = 버스 N개·각 멤버 1. "공유 연결 1개" 구조 입증.
    /// </summary>
    public IReadOnlyList<SorterRegistryFactory.BusInfo> Buses =>
        Services.GetRequiredService<SorterRegistryFactory>().Buses;

    public static int GetFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    // ── C4 fix 테스트 지원: read-timeout 주입 공유 연결 데코레이터 + 팩토리 ──────────
    // MultiSorterSameBus C3(N=2, soft·무-churn)의 대칭 — 솔로(1-멤버 버스)는 read 타임아웃을 HARD로
    // 처리해 재연결(Connect/Disconnect 증가·reopen)로 복구함을 실 레지스트리 경로에서 입증한다.
    private sealed class TimeoutInjectingConnectionFactory(E2EWebApplicationFactory owner)
        : ISharedModbusConnectionFactory
    {
        public ISharedModbusConnection Create(PlcTransportOptions opt, Microsoft.Extensions.Logging.ILogger? log = null)
        {
            var inner = SharedModbusConnectionFactory.Create(opt, log);
            var deco  = new TimeoutInjectingSharedConnection(inner);
            lock (owner._connLock) { owner._injectedConns.Add(deco); }
            return deco;
        }
    }
}

/// <summary>
/// 지정 unitId의 read/write를 즉시 TimeoutException으로 만드는 ISharedModbusConnection 데코레이터
/// (MultiSorterSameBus C3의 공유버스판 — 솔로 재연결 복구 검증용). Connect/Disconnect 호출 수를 계측해
/// "솔로 타임아웃 → 재연결(reopen)"을 입증한다. 실 소켓은 inner에 위임.
/// </summary>
public sealed class TimeoutInjectingSharedConnection(ISharedModbusConnection inner) : ISharedModbusConnection
{
    private volatile int _timeoutUnit = -1;   // -1 = 없음
    private int _connectCalls;
    private int _disconnectCalls;

    public int ConnectCalls    => Volatile.Read(ref _connectCalls);
    public int DisconnectCalls => Volatile.Read(ref _disconnectCalls);

    /// <summary>이 unitId의 read/write를 타임아웃시킨다(-1=해제).</summary>
    public void SetTimeoutUnit(int unitId) => _timeoutUnit = unitId;

    public string BusKey      => inner.BusKey;
    public bool   IsConnected => inner.IsConnected;

    public void Connect()    { Interlocked.Increment(ref _connectCalls);    inner.Connect(); }
    public void Disconnect() { Interlocked.Increment(ref _disconnectCalls); inner.Disconnect(); }

    public Task<ushort[]> ReadHoldingRegistersAsync(byte unitId, ushort startAddress, ushort count, CancellationToken ct)
        => unitId == _timeoutUnit
            ? throw new TimeoutException($"[test] injected read timeout for unit {unitId}")
            : inner.ReadHoldingRegistersAsync(unitId, startAddress, count, ct);

    public Task WriteSingleRegisterAsync(byte unitId, ushort address, short value, CancellationToken ct)
        => unitId == _timeoutUnit
            ? throw new TimeoutException($"[test] injected write timeout for unit {unitId}")
            : inner.WriteSingleRegisterAsync(unitId, address, value, ct);

    public Task WriteMultipleRegistersAsync(byte unitId, ushort startAddress, short[] data, CancellationToken ct)
        => unitId == _timeoutUnit
            ? throw new TimeoutException($"[test] injected write timeout for unit {unitId}")
            : inner.WriteMultipleRegistersAsync(unitId, startAddress, data, ct);

    public void Dispose() => inner.Dispose();
}
