using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wcs.Api;
using Wcs.Api.Startup;
using Wcs.Core;
using Wcs.Data;
using Wcs.PlcGateway;

// ── 종료 단계 관찰되지 않은 Task 예외 가드 (프로세스 자기 종료 방지) ──────────
// 배경: 종속 라이브러리(FluentModbus)의 내부 루프 Task가 종료 시점에 폴트한다 —
//   ModbusTcpServer accept 루프는 Stop() 시 TcpListener가 대기 중 AcceptTcpClientAsync를
//   끊으며 SocketException(995, "I/O 작업이 취소되었습니다")으로 폴트하고,
//   RTU 읽기 루프는 포트 종료 시 IOException/InvalidOperationException으로 폴트한다.
//   이 Task들은 라이브러리가 await하지 않으므로 관찰되지 않고, GC 시 파이널라이저 스레드가
//   재던져 프로세스(Windows 서비스 호스트·테스트호스트)를 종료시킨다.
// 정책: 종료 신호로 명백한 양성 예외(소켓 취소·I/O 취소·dispose 경쟁)만 관찰 처리(SetObserved)하고
//   반드시 로깅한다. 그 외 예외는 관찰하지 않아 기존 동작을 보존한다(진성 버그는 그대로 노출).
WcsTeardownGuard.Install();

var builder = WebApplication.CreateBuilder(args);

// ── Windows Service 호스팅 (M5) ──────────────────────────────────────────────
// 서비스 컨텍스트(SCM이 기동)에서는 Windows Service 호스트로, 콘솔/테스트에서는 no-op.
// UseWindowsService()는 WindowsServiceHelpers.IsWindowsService()가 false면(콘솔·
// WebApplicationFactory 테스트 호스트) 아무것도 하지 않으므로 콘솔·테스트 무파손.
// 서비스 등록 스크립트: scripts/install-service.ps1 / uninstall-service.ps1.
builder.Host.UseWindowsService();
// TODO(M5-P2): Serilog 구조화 로깅

// ════════════════════════════════════════════════════════════════════════════
// (D) DI 배선
// ════════════════════════════════════════════════════════════════════════════

// ── 공통 Timing 설정 바인딩 ─────────────────────────────────────────────────
builder.Services.Configure<TimingOptions>(builder.Configuration.GetSection("Timing"));

// ── WcsOptions 바인딩 (운영층 등 도메인 설정 — 하드코딩 금지, 절대규칙 #7) ─────
builder.Services.Configure<WcsOptions>(builder.Configuration.GetSection("Wcs"));

// ── MVC Controllers (Minimal API → Controller 이관) ──────────────────────────
// 검증은 컨트롤러 핸들러가 명시적으로 수행(가부는 200+result, 검증 실패만 400) —
// non-nullable 참조 타입에 대한 [ApiController] 자동 [Required] 추론을 끈다(Minimal API 동작 보존).
// nullable 선택필드(timeStamp 등)가 누락돼도 자동 400이 발생하지 않게 한다.
builder.Services.AddControllers(o =>
    o.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true);

// ── WcsDbContext 등록 (appsettings에서 provider·연결문자열 선택) ──────────────
// 절대규칙 8: 연결문자열·provider 하드코딩 금지.
var dbProvider       = builder.Configuration["Database:Provider"] ?? "Sqlite";
var connectionString = builder.Configuration.GetConnectionString("WcsDb")
                       ?? "Data Source=wcs.db";

builder.Services.AddDbContext<WcsDbContext>(opts =>
{
    if (dbProvider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
        opts.UseSqlServer(connectionString,
            sql => sql.MigrationsAssembly("Wcs.Migrations.SqlServer")
                      .MigrationsHistoryTable("__EFMigrationHistory"));
    else
        opts.UseSqlite(connectionString,
            sqlite => sqlite.MigrationsAssembly("Wcs.Migrations.Sqlite")
                            .MigrationsHistoryTable("__EFMigrationHistory"));
}, ServiceLifetime.Scoped);

// ── 4 인터페이스 EF Core 구현 바인딩 ─────────────────────────────────────────
builder.Services.AddScoped<EfDepositRecorder>();
builder.Services.AddScoped<IDepositRecorder>(sp => sp.GetRequiredService<EfDepositRecorder>());
builder.Services.AddScoped<IOrderRepository,  EfOrderRepository>();
builder.Services.AddScoped<ICellSelector,     EfCellSelector>();
builder.Services.AddScoped<IAgvFloorResolver, EfAgvFloorResolver>();
// IF-09 도착 보고 기록 (piece_event IF09_ARRIVAL — 상태 전이 없음)
builder.Services.AddScoped<IArrivalRecorder,  EfArrivalRecorder>();

// ── P3 갭 결선: alarm·sorter_command 영속화 (API 계층 한정) ───────────────────
// Scoped: 각 HTTP 핸들러 스코프와 WcsDbContext 동일 수명주기.
// 백그라운드 ContinueWith에서는 IServiceScopeFactory로 별도 스코프 생성해 사용.
builder.Services.AddScoped<EfAlarmSink>();
builder.Services.AddScoped<IAlarmSink>(sp => sp.GetRequiredService<EfAlarmSink>());
builder.Services.AddScoped<EfSorterCommandJournal>();
builder.Services.AddScoped<ISorterCommandJournal>(sp => sp.GetRequiredService<EfSorterCommandJournal>());

// ── ChuteCapacityService 싱글톤 (FULL/PAUSED 인메모리 집계) ──────────────────
builder.Services.AddSingleton<ChuteCapacityService>();
builder.Services.AddSingleton<IChuteCapacityService>(sp =>
    sp.GetRequiredService<ChuteCapacityService>());
builder.Services.AddHostedService<ChuteCapacityService>(sp =>
    sp.GetRequiredService<ChuteCapacityService>());

// ── ISorterGatewayRegistry — DB 주도 소터 판별 + 소터별 번들 N대 구성 ─────────
// 기동 시 IHostedService.StartAsync에서 수행.
// SorterRegistryFactory가 IHostedService + ISorterGatewayRegistry 양쪽 구현.
// 단일 싱글톤으로 양쪽 인터페이스에 같은 인스턴스를 공급.
// 테스트에서는 ISorterGatewayRegistry + IHostedService 등록을 모두 교체해 DB 조회 우회.
builder.Services.AddSingleton<SorterRegistryFactory>();
builder.Services.AddSingleton<ISorterGatewayRegistry>(sp =>
    sp.GetRequiredService<SorterRegistryFactory>());
// IHostedService 등록: ImplementationFactory 기반(람다) — ImplementationType은 null이지만
// ServiceType=IHostedService + factory가 SorterRegistryFactory를 반환함.
// 테스트에서 이 등록을 제거하려면: ServiceType=IHostedService 중 ISorterGatewayRegistry를
// 공급하는 타입(SorterRegistryFactory)와 같은 인스턴스를 가리키는 것을 제거.
// → 테스트 배선에서 IHostedService(SorterRegistryFactory) 제거 시
//   SorterRegistryFactory 싱글톤 자체도 제거해야 람다 resolve 실패를 피할 수 있음.
builder.Services.AddSingleton<IHostedService>(sp =>
    sp.GetRequiredService<SorterRegistryFactory>());

// ── IDestinationStatusService — full/ready 단일 산출 경로 (Scope E + m4p4 셀 만재) ──
// IF-05 NG 필터가 소비 + Phase 2 아웃바운드 푸시 재사용.
// 의존(ChuteCapacityService·SorterRegistry·WcsOptions)은 싱글톤이고, 소터 셀/정지 조회용
// scoped WcsDbContext는 IServiceScopeFactory(싱글톤)로 스코프 생성해 취득(확정3 — captive 회피).
builder.Services.AddSingleton<IDestinationStatusService, DestinationStatusService>();

// ── Phase 2: IF-08 아웃바운드 푸시 (WCS → RCS destination-status) ──────────────
// ① named HttpClient(IHttpClientFactory 경유 — 직접 new HttpClient() 금지, 소켓 고갈 방지).
//    타임아웃은 설정값(Wcs:RcsPush:HttpTimeoutMs). 하드코딩 0(절대규칙 #7).
{
    var rcsPush = builder.Configuration.GetSection("Wcs:RcsPush").Get<RcsPushOptions>()
                  ?? new RcsPushOptions();
    builder.Services.AddHttpClient(RcsPushClient.HttpClientName, c =>
    {
        c.Timeout = TimeSpan.FromMilliseconds(Math.Max(1, rcsPush.HttpTimeoutMs));
    });
}

// ② 푸시 클라이언트(1건 전송 + 설정 경유 지수 백오프 재시도 + Fail-Loud).
builder.Services.AddSingleton<IRcsPushClient, RcsPushClient>();

// ③ 전이 감지·전이당 1회 푸시 파이프(변화원 둘 수렴 + 부트스트랩 + 복구 재푸시).
//    IHostedService + IDestinationChangeNotifier 양쪽을 같은 싱글톤으로 공급.
//    슈트 변화원은 ChuteCapacityService.OnChuteStateChanged 이벤트 구독(StartAsync).
builder.Services.AddSingleton<DestinationStatusPusher>();
builder.Services.AddSingleton<IDestinationChangeNotifier>(sp =>
    sp.GetRequiredService<DestinationStatusPusher>());
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<DestinationStatusPusher>());

var app = builder.Build();

// ════════════════════════════════════════════════════════════════════════════
// 콜드스타트 자동 프로비저닝 (M5-P1)
// IHostedService(ChuteCapacityService·SorterRegistryFactory)가 DB를 조회하기 전
// (app.Run() 이전)에 스키마를 보장하고 dev/빈-DB 한정 시드를 적용한다.
// 테스트 호스트(in-memory SQLite)에서는 DbInitializer가 자동으로 no-op
// (기존 5개 테스트 팩토리의 EnsureCreated+DbSeeder.Seed 경로 무파손).
// ════════════════════════════════════════════════════════════════════════════
await DbInitializer.ProvisionAsync(app);

// ════════════════════════════════════════════════════════════════════════════
// (C) 엔드포인트 — Controller 이관 (RcsController: IF-05/IF-09/IF-10)
// IF-08 투입 가부 폴링(deposit-permission)은 폐지 — Phase 2 WCS→RCS 푸시로 대체.
// ════════════════════════════════════════════════════════════════════════════
app.MapControllers();

app.Run();

// ════════════════════════════════════════════════════════════════════════════
// SorterRegistryFactory — IHostedService
// StartAsync에서 DB 조회 → 소터별 번들 N대 구성 → ISorterGatewayRegistry 제공.
// 소터 0대: 빈 레지스트리(기동·종료 정상). 1대: P2a 동등. N대: 소터별 독립 버스.
// SORTER_3D destination인데 appsettings Sorters[]에 ChuteNo 누락 → fail-loud(기동 에러).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 기동 시 DB에서 SORTER_3D destination을 조회해 소터별 번들을 N대 구성하는 팩토리.
/// IHostedService + ISorterGatewayRegistry 둘 다 구현 — 단일 싱글톤으로 양쪽 인터페이스 제공.
/// StartAsync에서 초기화, StopAsync에서 모든 번들 종료.
/// 소터 0대: 빈 레지스트리(기동·종료 정상). 1대: P2a 동등. N대: 소터별 독립 버스.
/// SORTER_3D destination인데 appsettings Sorters[]에 ChuteNo 누락 → fail-loud(기동 에러).
/// </summary>
public sealed class SorterRegistryFactory : IHostedService, ISorterGatewayRegistry
{
    private readonly IServiceProvider               _sp;
    private readonly IConfiguration                 _config;
    private readonly ILogger<SorterRegistryFactory> _log;
    private MultiSorterGatewayRegistry?             _registry;

    public SorterRegistryFactory(
        IServiceProvider               sp,
        IConfiguration                 config,
        ILogger<SorterRegistryFactory> log)
    {
        _sp     = sp;
        _config = config;
        _log    = log;
    }

    // ── ISorterGatewayRegistry 구현 — StartAsync 완료 후 유효 ─────────────────

    public Wcs.Core.PlcSnapshot? GetLatest(long destinationId) =>
        _registry?.GetLatest(destinationId);

    public SorterBundleHandle? GetBundle(long destinationId) =>
        _registry?.GetBundle(destinationId);

    public IReadOnlyCollection<SorterBundleHandle> AllBundles =>
        _registry?.AllBundles ?? Array.Empty<SorterBundleHandle>();

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // ── appsettings Sorters[] 로드 ────────────────────────────────────────
        var sorterConfigs = _config.GetSection("Sorters").Get<List<SorterConfig>>()
                            ?? new List<SorterConfig>();

        // ChuteNo → SorterConfig 딕셔너리
        var configByChuteNo = sorterConfigs.ToDictionary(c => c.ChuteNo, c => c);

        // ── DB 주도 SORTER_3D 조회 ─────────────────────────────────────────────
        using var scope = _sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();

        List<Wcs.Data.Destination> sorterDests;
        try
        {
            sorterDests = await db.Destinations
                .Where(d => d.DestType == DestType.SORTER_3D && d.IsActive)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // DB 미가용 → fail-loud(기동 에러)
            _log.LogCritical(ex, "[SorterRegistry] DB 조회 실패 — 기동 불가");
            throw;
        }

        _log.LogInformation("[SorterRegistry] SORTER_3D destination {Count}대 조회됨", sorterDests.Count);

        // ── 소터별 번들 구성 ──────────────────────────────────────────────────
        var bundles = new Dictionary<long, SorterBundleHandle>();
        var logFac  = _sp.GetRequiredService<ILoggerFactory>();
        var timing  = _config.GetSection("Timing").Get<PlcGatewayOptions>() ?? new PlcGatewayOptions();

        foreach (var dest in sorterDests)
        {
            // ChuteNo로 설정 매칭 — 미스매치 fail-loud
            if (!configByChuteNo.TryGetValue(dest.ChuteNo, out var cfg))
            {
                _log.LogCritical(
                    "[SorterRegistry] SORTER_3D destination(id={Id} chuteNo={ChuteNo})에 대한 " +
                    "appsettings Sorters[] 항목 없음 — 기동 불가(fail-loud). " +
                    "appsettings.json의 Sorters 배열에 ChuteNo={ChuteNo} 항목을 추가하세요.",
                    dest.Id, dest.ChuteNo, dest.ChuteNo);
                throw new InvalidOperationException(
                    $"SORTER_3D destination(id={dest.Id} chuteNo={dest.ChuteNo})에 대한 " +
                    $"appsettings Sorters[] 항목이 없습니다. fail-loud.");
            }

            // ── 소터별 번들 구성 (인스턴스별 독립) ──────────────────────────────
            // 공통 Timing + 소터별 오버라이드 적용
            var gwOpt = BuildGatewayOptions(timing, cfg);

            // 소터별 전송 설정으로 IModbusMaster 생성
            var transportOpt = cfg.ToTransportOptions();
            var master = ModbusMasterFactory.Create(transportOpt, logFac);

            // 소터별 독립 PlcWriteQueue (단일 공유 큐 제거 — 절대규칙 #1 소터별 보존)
            var writeQueue = new PlcWriteQueue();

            // 소터별 독립 PlcPollingService (인스턴스별 _clientLock·_cSeq·RFlag 채널)
            var polling = new PlcPollingService(
                gwOpt,
                writeQueue,
                master,
                logFac.CreateLogger<PlcPollingService>());

            // 소터별 독립 HandshakeOrchestrator (인스턴스별 _cSeq)
            var handshake = new HandshakeOrchestrator(
                polling,
                gwOpt,
                logFac.CreateLogger<HandshakeOrchestrator>());

            var bundle = new SorterBundleHandle(dest.Id, dest.ChuteNo, polling, handshake, writeQueue);
            bundles[dest.Id] = bundle;

            _log.LogInformation(
                "[SorterRegistry] 소터 번들 구성: destId={DestId} chuteNo={ChuteNo} transport={Transport} host={Host}:{Port}",
                dest.Id, dest.ChuteNo, cfg.Transport, cfg.Host, cfg.Port);
        }

        // ── 소터별 폴링 서비스 시작 + OFFLINE 전이 이벤트 구독 ────────────────
        // P3: OFFLINE 전이당 1건 alarm 영속화 — IServiceScopeFactory 경유 별도 스코프.
        // 폴링 시작 전에 구독하면 첫 폴 실패도 포착 가능(이벤트는 true→false 전이만 발화).
        foreach (var bundle in bundles.Values)
        {
            // OFFLINE 이벤트 구독: 전이당 1회만 발화 — API 계층에서 alarm 기록
            var capturedDestId  = bundle.DestinationId;
            var capturedChuteNo = bundle.ChuteNo;
            bundle.SubscribeOffline(offlineSnap =>
            {
                // 이 핸들러는 폴 스레드(RunPollLoopAsync)에서 직접 호출된다.
                // 어떤 예외도 폴 스레드 밖으로 새어나가면 폴링 루프가 영구 종료되므로
                // scope 생성·DI 해석·Append 전 구간을 단일 try/catch로 감쌈.
                try
                {
                    // IServiceScopeFactory는 싱글톤 — IServiceProvider에서 직접 취득 안전.
                    var sf = _sp.GetRequiredService<IServiceScopeFactory>();
                    using var offlineScope = sf.CreateScope();
                    var sink = offlineScope.ServiceProvider.GetRequiredService<IAlarmSink>();
                    sink.Append("OFFLINE", Wcs.Data.AlarmSeverity.ERROR, pieceId: null,
                        $"소터 OFFLINE 전이 — destId={capturedDestId} chuteNo={capturedChuteNo}");
                    _log.LogWarning("[SorterRegistry] OFFLINE alarm 기록: destId={DestId} chuteNo={ChuteNo}",
                        capturedDestId, capturedChuteNo);
                }
                catch (ObjectDisposedException)
                {
                    // 호스트 종료 후 OFFLINE 이벤트 — 영속화 생략(teardown 경쟁 조건 방어)
                }
                catch (Exception ex)
                {
                    // DB/DI 예외가 폴 스레드를 죽이지 않도록 삼킴 (로깅은 유지)
                    _log.LogError(ex, "[SorterRegistry] OFFLINE alarm 영속화 예외: destId={DestId}", capturedDestId);
                }
            });

            await bundle.StartPollingAsync(cancellationToken);
            _log.LogInformation("[SorterRegistry] 소터 폴링 시작: destId={DestId} chuteNo={ChuteNo}",
                bundle.DestinationId, bundle.ChuteNo);
        }

        _registry = new MultiSorterGatewayRegistry(bundles);
        _log.LogInformation("[SorterRegistry] 초기화 완료 — 소터 {Count}대", bundles.Count);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_registry is null) return;

        // 모든 소터 폴링 서비스 종료 (ApplicationStopping)
        // 호스트 종료 시 로거(EventLogInternal 등)가 먼저 dispose될 수 있어
        // LogInformation/LogError 자체가 ObjectDisposedException을 던질 수 있음.
        // teardown 중 로깅 실패가 StopAsync를 중단시키지 않도록 로깅도 try로 보호.
        foreach (var bundle in _registry.AllBundles)
        {
            try
            {
                await bundle.StopPollingAsync();
            }
            catch (Exception ex)
            {
                try { _log.LogError(ex, "[SorterRegistry] 소터 폴링 종료 예외: destId={DestId}", bundle.DestinationId); }
                catch { /* 호스트 종료 중 로거 disposed — 로깅 실패 무시 */ }
            }
        }
    }

    // ── 헬퍼: 공통 Timing + 소터별 오버라이드 → PlcGatewayOptions 합성 ────────

    private static PlcGatewayOptions BuildGatewayOptions(PlcGatewayOptions commonTiming, SorterConfig cfg)
    {
        // 소터별 Timing 오버라이드(없으면 공통 상속)
        var t = cfg.Timing;
        return new PlcGatewayOptions
        {
            // 전송 파라미터 (소터별)
            Host                 = cfg.Host,
            Port                 = cfg.Port,
            PollIntervalMs       = cfg.PollIntervalMs,
            OfflineAfterFailures = cfg.OfflineAfterFailures,
            WriteTimeoutMs       = cfg.WriteTimeoutMs,
            // Timing (소터별 오버라이드 or 공통)
            RFlagPollMs    = t?.RFlagPollMs    ?? commonTiming.RFlagPollMs,
            RFlagTimeoutMs = t?.RFlagTimeoutMs ?? commonTiming.RFlagTimeoutMs,
            CFlagTimeoutMs = t?.CFlagTimeoutMs ?? commonTiming.CFlagTimeoutMs,
        };
    }
}

// ════════════════════════════════════════════════════════════════════════════
// SorterConfig — appsettings Sorters[] 항목 1개 바인딩
// PlcTransportOptions + 소터별 Timing 오버라이드
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// appsettings.json Sorters[] 배열 항목 1개.
/// ChuteNo로 DB destination과 매칭(키).
/// 전송 파라미터(Transport·Host·Port·RTU 파라미터)와 소터별 Timing 오버라이드 포함.
/// </summary>
public sealed record SorterConfig
{
    // ── 키: DB destination.chute_no 매칭 ─────────────────────────────────────
    /// <summary>소터 슬롯 번호 — DB destination.chute_no와 1:1 대응(매칭 키).</summary>
    public int ChuteNo { get; init; }

    // ── 전송 파라미터 ─────────────────────────────────────────────────────────
    /// <summary>전송 선택: "Tcp" 또는 "Rtu". 미지정 기본값 = Rtu.</summary>
    public string Transport { get; init; } = "Rtu";

    // TCP
    public string Host { get; init; } = "127.0.0.1";
    public int    Port { get; init; } = 502;

    // RTU
    public string PortName       { get; init; } = "COM1";
    public int    BaudRate       { get; init; } = 9600;
    public string Parity         { get; init; } = "Even";
    public string StopBits       { get; init; } = "One";
    public int    ReadTimeoutMs  { get; init; } = 1000;
    public int    WriteTimeoutMs { get; init; } = 1000;
    public byte   UnitId         { get; init; } = 1;

    // 폴링
    public int PollIntervalMs       { get; init; } = 150;
    public int OfflineAfterFailures { get; init; } = 3;

    // ── 소터별 Timing 오버라이드 (null이면 공통 Timing 상속) ─────────────────
    /// <summary>소터별 Timing 오버라이드. null 항목은 공통 Timing 값 사용.</summary>
    public SorterTimingOverride? Timing { get; init; }

    // ── PlcTransportOptions 변환 ──────────────────────────────────────────────
    public PlcTransportOptions ToTransportOptions() => new()
    {
        Transport     = Transport,
        Host          = Host,
        Port          = Port,
        PortName      = PortName,
        BaudRate      = BaudRate,
        Parity        = Parity,
        StopBits      = StopBits,
        ReadTimeoutMs = ReadTimeoutMs,
        WriteTimeoutMs = WriteTimeoutMs,
        UnitId        = UnitId,
    };
}

/// <summary>소터별 Timing 오버라이드. null 필드 = 공통 Timing 상속.</summary>
public sealed record SorterTimingOverride
{
    public int? RFlagPollMs    { get; init; }
    public int? RFlagTimeoutMs { get; init; }
    public int? CFlagTimeoutMs { get; init; }
}

// ════════════════════════════════════════════════════════════════════════════
// PlcPollingHostedAdapter — 단일 소터(P2a 호환) IHostedService 어댑터
// M2 통합 테스트 회귀 보존 — 단일 PlcPollingService를 직접 감쌀 때 사용.
// P2b에서는 SorterRegistryFactory가 소터별 Start/Stop을 직접 수행하므로
// 이 어댑터는 사용되지 않지만 M2 테스트 코드 참조를 위해 유지.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// PlcPollingService를 IHostedService에 연결하는 어댑터.
/// M2 통합 테스트의 수동 StartAsync/StopAsync 경로를 그대로 보존.
/// P2b에서는 SorterRegistryFactory가 소터별 Start/Stop을 직접 처리.
/// </summary>
public sealed class PlcPollingHostedAdapter : IHostedService
{
    private readonly PlcPollingService _service;
    private readonly ILogger<PlcPollingHostedAdapter> _log;

    public PlcPollingHostedAdapter(
        PlcPollingService service,
        ILogger<PlcPollingHostedAdapter> log)
    {
        _service = service;
        _log     = log;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _log.LogInformation("[PlcPollingAdapter] PLC 폴링 서비스 시작");
        await _service.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _log.LogInformation("[PlcPollingAdapter] PLC 폴링 서비스 종료");
        await _service.StopAsync().ConfigureAwait(false);
    }
}

// ════════════════════════════════════════════════════════════════════════════
// TimingOptions — appsettings Timing 섹션 바인딩
// ════════════════════════════════════════════════════════════════════════════

/// <summary>appsettings.json Timing 섹션 설정값.</summary>
public sealed record TimingOptions
{
    public int If08RetryMsHint  { get; init; } = 500;
    public int RFlagPollMs      { get; init; } = 100;
    public int RFlagTimeoutMs   { get; init; } = 30000;
    public int CFlagTimeoutMs   { get; init; } = 5000;
}
