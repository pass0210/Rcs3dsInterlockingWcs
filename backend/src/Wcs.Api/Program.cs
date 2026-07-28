using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;
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

// ── Serilog 구조화 로깅 (M5-P2 / S-OBSERVABILITY) ────────────────────────────
// 레벨·싱크(Console/File)·파일 경로·롤링 주기·보존·outputTemplate 전부 appsettings의
// "Serilog" 섹션에서 읽는다(하드코딩 금지·절대규칙 #7). ReadFrom.Configuration이 그 섹션을 소비.
// 기존 ILogger<T> 호출부는 Serilog 백엔드로 자동 라우팅(구조화 메시지 템플릿 속성 보존) —
// 호출 코드 대량 변경 불요. ReadFrom.Services로 등록된 enricher/sink도 흡수.
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services));

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

// ── B2B(S-B2B-1): 검증실패 400 형식 경로분기 (사용자 확정 Q5 — 무접촉) ──────────
// /api/v1/works/ 요청의 ModelState 실패만 B2B ApiResponse.Fail(firstError)로 400 응답한다.
// 그 외 경로는 프레임워크 기본 팩토리(ProblemDetails)에 그대로 위임 → 기존 엔드포인트 400 형식 불변.
// (이 Configure 는 AddControllers 의 ApiBehaviorOptionsSetup 등록 뒤에 실행되므로 builtInFactory 유효.)
builder.Services.Configure<Microsoft.AspNetCore.Mvc.ApiBehaviorOptions>(options =>
{
    var builtInFactory = options.InvalidModelStateResponseFactory;
    options.InvalidModelStateResponseFactory = context =>
    {
        // S-B2B-2a: allowlist 에 /api/test-data 추가(additive) — /api/v1/works· B2C ProblemDetails 불변.
        // S-B2C-DATAGEN: /api/b2c/test-data 추가(additive) — 검증실패 400 을 B2cManagementResponse.Fail 형식으로.
        var path = context.HttpContext.Request.Path;
        if (path.StartsWithSegments(Wcs.Api.B2B.AppConstants.WorksRoutePrefix, StringComparison.OrdinalIgnoreCase)
         || path.StartsWithSegments(Wcs.Api.B2B.AppConstants.TestDataRoutePrefix, StringComparison.OrdinalIgnoreCase)
         || path.StartsWithSegments(Wcs.Api.B2C.B2cConstants.RoutePrefix, StringComparison.OrdinalIgnoreCase)
         || path.StartsWithSegments(Wcs.Api.B2C.B2cConstants.FacilityRoutePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var firstError = context.ModelState
                .Where(kv => kv.Value is not null && kv.Value.Errors.Count > 0)
                .SelectMany(kv => kv.Value!.Errors)
                .Select(e => e.ErrorMessage)
                .FirstOrDefault(msg => !string.IsNullOrEmpty(msg))
                ?? Wcs.Api.B2B.FailMessages.InvalidRequestBody;   // #18 fallback
            return new Microsoft.AspNetCore.Mvc.BadRequestObjectResult(
                Wcs.Api.B2B.B2BApiResponse.Fail(firstError));
        }
        // /api/v1/works/ 밖 경로 — 기존 기본 동작 보존(무접촉).
        return builtInFactory(context);
    };
});

// ── SignalR 허브 (F2 실시간 관측 — WcsMonitorHub) ────────────────────────────
// payload는 프론트 TS 타입과 1:1 카멜케이스로 직렬화(명시 — 기본값에 의존하지 않음).
// 인증 없음(사용자 확정 — F3). MapHub 결선은 미들웨어 순서 섹션에서 수행.
builder.Services.AddSignalR()
    .AddJsonProtocol(o =>
        o.PayloadSerializerOptions.PropertyNamingPolicy =
            System.Text.Json.JsonNamingPolicy.CamelCase);

// ── F2 실시간 relay 타이밍 바인딩 (Wcs:Monitor — 하드코딩 금지·절대규칙 #7) ────
builder.Services.Configure<MonitorOptions>(builder.Configuration.GetSection("Wcs:Monitor"));

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

// ── operation_log 비동기 싱크 (S-OBSERVABILITY) ─────────────────────────────
// IOperationLogger(논블로킹 enqueue) + IHostedService(백그라운드 컨슈머)를 한 싱글톤으로.
// 본 처리(폴·핸드셰이크·API) 비지연 + fail-safe. in-memory SQLite 테스트 더블에서도 무해.
builder.Services.AddSingleton<OperationLogService>();
builder.Services.AddSingleton<Wcs.Data.IOperationLogger>(sp =>
    sp.GetRequiredService<OperationLogService>());
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<OperationLogService>());

// ── 전용 추적 로그 sink (S-TRACE-LOG-VIEWER — additive·관측/로깅 전용) ────────────
// 6개 이벤트(1~6)를 전용 파일 + SignalR 로 발신하는 논블로킹 채널 sink. 기존 operation_log/Serilog 무영향.
// 경로·롤링·크기·보존·파일명·백로그 take = appsettings "TraceLog"(하드코딩 금지·절대규칙 #7). 기본 D:\Rcs3dsInterlockingWcsLogs.
builder.Services.Configure<TraceLogOptions>(builder.Configuration.GetSection("TraceLog"));
builder.Services.AddSingleton<TraceLogService>();
builder.Services.AddSingleton<ITraceLogger>(sp => sp.GetRequiredService<TraceLogService>());
builder.Services.AddSingleton<ITraceBacklog>(sp => sp.GetRequiredService<TraceLogService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<TraceLogService>());
// C 흐름(이벤트 4·5·6) pId 실시간 상관(소터별 컨텍스트) — RcsController 등록·TraceWiring 해소.
builder.Services.AddSingleton<TraceCorrelator>();

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

// ── 인덕션 기반 2층 제어 (S-TWO-FLOOR-CONTROL A) — 소터별 pending-floor 큐 + 관측 루프 ──
// 큐(상태): IF-05가 목표 층 F를 소터별 FIFO에 enqueue. 관측 루프(트리거): TgtFloor==0 관측 시
//   큐 머리 F를 DepositDecider(순수) 게이트로 소터별 단일 쓰기 큐(SetTgtFloor)에 기입, CurFloor==F
//   도착 시 pop(폐루프). 절대규칙 #1(단일 쓰기 큐)·#3(WCS 클리어 금지) 준수.
// 하스티드 등록은 람다(ImplementationType=null) — 테스트 팩토리의 "null-hosted 제거" 패턴과 정합.
builder.Services.AddSingleton<SorterPendingFloorQueues>();
// C2 S2(I-3): 재시작 시 미완료 SORTER_3D piece 에서 pending-floor 큐를 재파생(읽기 전용). SorterFloorReturnService
// 가 관측 루프 기동 전에 RestoreAsync 를 await 한다(복원 before 관측 — 스키마 변경·마이그레이션 0).
builder.Services.AddSingleton<IPendingFloorQueueRestorer, PendingFloorQueueRestorer>();
builder.Services.AddSingleton<SorterFloorReturnService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SorterFloorReturnService>());

// ── IDestinationControlService — B2C 운영자 런타임 PAUSED/RESUMED 전이 (S-F3a) ──
// OpsController(/api/ops/destinations/{id}/pause|resume)가 소비. DB Status 전이 + destination_event
// (operatorId) + CHUTE 인메모리 반영(ChuteCapacityService)을 한 단위로 수행. PLC 쓰기 없음(Q3 LOCK).
// 싱글톤 — scoped WcsDbContext는 IServiceScopeFactory로 취득(DestinationStatusService와 동형).
builder.Services.AddSingleton<IDestinationControlService, DestinationControlService>();

// ── S-IF08-READY-PUSH: 목적지 수용상태 아웃바운드 푸시 (WCS → RCS PUT /api/UpdateChuteState) ──
// 확정 와이어(UpdateChuteState) 단일 채널. 목적지당 단일 발신 소스(DestinationStatusPusher)가
// 슈트 capacity·소터 스냅샷·운영자 전이 세 변화원을 수렴해 {chute_numbers:[n], next_states:[3|2]}로 발신.
// ① named HttpClient(IHttpClientFactory 경유 — 직접 new HttpClient() 금지, 소켓 고갈 방지).
//    타임아웃은 설정값(Wcs:ChuteStatePush:HttpTimeoutMs). 하드코딩 0(절대규칙 #7).
{
    var chuteStatePush = builder.Configuration.GetSection("Wcs:ChuteStatePush").Get<ChuteStatePushOptions>()
                         ?? new ChuteStatePushOptions();
    builder.Services.AddHttpClient(ChuteStatePushClient.HttpClientName, c =>
    {
        c.Timeout = TimeSpan.FromMilliseconds(Math.Max(1, chuteStatePush.HttpTimeoutMs));
    });
}

// ② 푸시 클라이언트(PUT 1건 전송 + 설정 경유 지수 백오프 재시도 + Fail-Loud + DORMANT no-op).
builder.Services.AddSingleton<IChuteStatePushClient, ChuteStatePushClient>();

// ③ 전이 감지·전이당 1회 발신 파이프(변화원 셋 수렴 + 부트스트랩 + 복구 재푸시 + 동시성 멱등).
//    싱글톤 + IHostedService로 공급.
//    변화원: ChuteCapacityService.OnChuteStateChanged(슈트) + 소터 스냅샷 관찰 타이머 +
//    DestinationControlService.OnTransition(운영자 PAUSED/RESUMED) — StartAsync에서 구독.
//    BaseUrl 미설정이면 StartAsync가 경고 후 비활성(구독 안 함 — 크래시 0).
//    ※ IDestinationChangeNotifier는 DI로 resolve하는 소비처가 0(슈트 변화원은 ChuteCapacityService의
//      OnChuteStateChanged 이벤트를 StartAsync에서 직접 구독) — S-HARDENING-1에서 사장 DI 등록 제거.
builder.Services.AddSingleton<DestinationStatusPusher>();
builder.Services.AddHostedService(sp =>
    sp.GetRequiredService<DestinationStatusPusher>());

// ── F1-CR-M1 해소: IMonitoringQueries DI 등록 (요청당 손조립 제거) ─────────────
// MonitoringController가 생성자에서 new MonitoringQueries(...)로 조립하던 것을 폐지하고
// AddScoped로 주입한다. 수명: WcsDbContext(scoped) + 싱글톤(레지스트리·상태서비스) → scoped 정상 해석.
builder.Services.AddScoped<Wcs.Api.Monitoring.IMonitoringQueries, Wcs.Api.Monitoring.MonitoringQueries>();

// ── F2 실시간 relay (MonitorRelayService) ────────────────────────────────────
// ⚠ 구독 시점(함정6): AllBundles는 SorterRegistryFactory.StartAsync 완료 후 채워진다.
// IHostedService는 등록 순서로 순차 기동되므로, 이 relay를 SorterRegistryFactory 등록(위)
// **이후**에 등록해 relay.StartAsync가 나중에 돌게 한다 → 구독 시점에 AllBundles 유효.
builder.Services.AddSingleton<MonitorRelayService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MonitorRelayService>());

// ════════════════════════════════════════════════════════════════════════════
// (D-B2B) S-B2B-1 배선 — B2B(작업 테스트 데이터) RCS 5개 API. 기존 배선 무접촉(append).
//   IWorkService/WorkService · IBoxService/BoxService (Scoped — WcsDbContext 동일 수명).
//   ApiCallLogQueue(Singleton) + ApiCallLogBackgroundWriter(HostedService) — /api/v1/works/ 한정 감사.
//   큐 헬스체크 미이식(우리 /health 유지). api_call_log 미들웨어는 파이프라인 섹션에서 경로 한정 결선.
// ════════════════════════════════════════════════════════════════════════════
builder.Services.AddScoped<Wcs.Api.B2B.IWorkService, Wcs.Api.B2B.WorkService>();
builder.Services.AddScoped<Wcs.Api.B2B.IBoxService,  Wcs.Api.B2B.BoxService>();
builder.Services.AddSingleton<Wcs.Api.B2B.ApiCallLogQueue>();
builder.Services.AddHostedService<Wcs.Api.B2B.ApiCallLogBackgroundWriter>();

// ── S-B2B-2a: test-data 관리 서비스(수동생성·엑셀·조회·초기화·삭제+아카이브) — additive ──
builder.Services.AddScoped<Wcs.Api.B2B.ITestDataService, Wcs.Api.B2B.TestDataService>();

// ── S-B2B-3a: 조회 전용 서비스(로그·API호출이력·3-way 비교 / 투입+분류 Excel) — additive ──
builder.Services.AddScoped<Wcs.Api.B2B.ILogService,       Wcs.Api.B2B.LogService>();
builder.Services.AddScoped<Wcs.Api.B2B.ILogExportService, Wcs.Api.B2B.LogExportService>();

// ── S-B2C-DATAGEN: B2C(3D 소터) 테스트 데이터 관리 서비스(생성·요약·초기화) — additive ──
// 컨트롤러가 판정/PLC 를 직접 호출하지 않음(절대규칙 #1·#8). WcsDbContext(scoped)+IOperationLogger 만 사용.
builder.Services.AddScoped<Wcs.Api.B2C.IB2cTestDataService, Wcs.Api.B2C.B2cTestDataService>();

// ── S-B2C-FACILITY: 설비 관리 서비스(목적지 구성·셀 설정·오더 할당) — additive ──
// WcsDbContext(scoped) + IOperationLogger + IChuteCapacityService + DestinationStatusPusher(런타임 슈트
// 등록 — 신설 슈트가 GetHold·pause/resume·IF-08 push 에서 즉시 동작). 판정/Modbus 직접 호출 0.
builder.Services.AddScoped<Wcs.Api.B2C.IB2cFacilityService, Wcs.Api.B2C.B2cFacilityService>();

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
// S-OBSERVABILITY: 슈트 FULL/PAUSED 상태 전이 → operation_log STATE 기록(전이당 1회)
// ChuteCapacityService.OnChuteStateChanged(매 reserve/deposit/clear 발화)를 구독해
// GetHold 전이(None↔Full↔Paused)만 1행 기록. 무변화는 0행(전이 정책). 부수 기록 — 도메인 의미 0 변경.
// (소터 FULL은 DestinationStatusService.Compute 산출 — 본 스프린트는 슈트 capacity 전이를 STATE로 관측.)
// ════════════════════════════════════════════════════════════════════════════
{
    // OnChuteStateChanged 이벤트는 구체 타입(ChuteCapacityService)에 정의됨 — 동일 싱글톤 인스턴스.
    var chuteCap = app.Services.GetRequiredService<ChuteCapacityService>();
    var opLogSink = app.Services.GetService<Wcs.Data.IOperationLogger>();
    var lastHold = new System.Collections.Concurrent.ConcurrentDictionary<long, Wcs.Core.WcsHold>();
    if (opLogSink is not null)
    chuteCap.OnChuteStateChanged += destId =>
    {
        try
        {
            var hold = chuteCap.GetHold(destId);
            var prev = lastHold.GetOrAdd(destId, Wcs.Core.WcsHold.None);
            if (hold == prev) return;  // 무변화 — 기록 0(전이 정책).
            lastHold[destId] = hold;

            var (action, level) = hold switch
            {
                Wcs.Core.WcsHold.Full   => ("FULL",   Wcs.Data.OperationLogLevel.WARN),
                Wcs.Core.WcsHold.Paused => ("PAUSED", Wcs.Data.OperationLogLevel.WARN),
                _                       => ("NORMAL", Wcs.Data.OperationLogLevel.INFO),
            };
            opLogSink.Log(Wcs.Data.OperationLogCategory.STATE, action, level: level,
                destinationId: destId,
                detail: $"{{\"destId\":{destId},\"hold\":\"{hold}\",\"prev\":\"{prev}\"}}");
        }
        catch { /* 관측 훅 예외 격리 — 본 동작 보존(fail-safe) */ }
    };
}

// ════════════════════════════════════════════════════════════════════════════
// (F1) SPA 정적 서빙 — UseStaticFiles는 라우팅/엔드포인트 이전 미들웨어여야 한다.
// npm run build 산출물을 src/Wcs.Api/wwwroot(기본 WebRootPath)에서 서빙.
// wwwroot 부재(테스트 호스트·fresh clone·CI)면 WebRootFileProvider가 NullFileProvider라
// 무해 — 기존 146 테스트는 wwwroot 없이 GREEN 유지(함정 #6). Windows Service ContentRoot
// (=AppContext.BaseDirectory)에서도 WebRootPath=ContentRoot/wwwroot 기준으로 정상 서빙(함정 #5).
// ════════════════════════════════════════════════════════════════════════════
app.UseStaticFiles();

// ════════════════════════════════════════════════════════════════════════════
// (C) 엔드포인트 — Controller 이관 (RcsController: IF-05/IF-09/IF-10, MonitoringController: /api/monitor)
// IF-08 투입 가부 폴링(deposit-permission)은 폐지 — Phase 2 WCS→RCS 푸시로 대체.
// ════════════════════════════════════════════════════════════════════════════
// ── B2B(S-B2B-1): api_call_log 감사 미들웨어 — /api/v1/works/ 한정(경로 밖 즉시 통과) ──
// Q1(사용자 확정): 기존 /api/v1/ RCS 엔드포인트(destination-query 등) 미기록 → 무접촉.
// 논블로킹 enqueue만(응답 비지연). 경로 밖 요청엔 무영향이라 기존 미들웨어 순서·동작 보존.
app.UseMiddleware<Wcs.Api.B2B.RcsApiLoggingMiddleware>();

app.MapControllers();

// ════════════════════════════════════════════════════════════════════════════
// (D-3) /health — liveness 프로브 (S-CLEANUP-FIELD, 현장 관측성)
// 프로세스 생존을 외부(감시/로드밸런서/현장 점검)에서 확인할 최소 HTTP 표면.
// 정책(Q2 확정): liveness=프로세스가 응답하면 **항상 200**. 소터 OFFLINE·DB 저하는 본문 플래그로만
//   노출한다(readiness 503은 B2B-1로 이연 — 과설계 금지). 전용 HealthChecks 프레임워크 미도입(단일 엔드포인트).
// 부수효과 0: 소터 스냅샷(Latest.Online/At)·DB CanConnect(읽기 전용) 스냅샷만 조회. 쓰기·상태전이 없음.
// /api catch-all 밖 경로라 아래 Map("/api/{**rest}")가 삼키지 않고, MapGet이 fallback보다 우선.
// ════════════════════════════════════════════════════════════════════════════
app.MapGet("/health", (ISorterGatewayRegistry sorters, WcsDbContext db) =>
{
    bool dbOk;
    try { dbOk = db.Database.CanConnect(); }
    catch { dbOk = false; }  // 연결 실패도 liveness는 200 — 본문 db=false로만 저하 표시(예외 삼킴 아님).

    var sorterStates = sorters.AllBundles
        .Select(b =>
        {
            var snap = b.Latest;   // 폴링 스냅샷(논블로킹) — 부수효과 0.
            return new
            {
                chuteNo    = b.ChuteNo,
                online     = snap.Online,
                lastPollAt = snap.At,
            };
        })
        .ToArray();

    return Results.Json(new
    {
        status  = "ok",   // liveness — 프로세스 생존(항상 200). 저하는 db·sorters 필드로 판단.
        db      = dbOk,
        sorters = sorterStates,
    });
});

// ════════════════════════════════════════════════════════════════════════════
// (F2) SignalR 허브 매핑 — MapControllers 뒤·catch-all/fallback 앞.
// /api/{**rest} catch-all은 /api/**만 매치하므로 /hubs/monitor를 삼키지 않고,
// MapFallbackToFile(index.html)보다 앞서 매핑돼 fallback이 허브 negotiate를 가로채지 않는다
// (함정 #1·#2 — 검증 결선: negotiate가 404 아님을 통합 테스트로 입증).
// ════════════════════════════════════════════════════════════════════════════
app.MapHub<Wcs.Api.Hubs.WcsMonitorHub>("/hubs/monitor");

// ════════════════════════════════════════════════════════════════════════════
// (F1) SPA fallback — API 우선·fallback이 /api/**를 삼키지 않게(함정 #1·음성 대조 C2).
// /api/** 미매치 요청은 여기서 404로 확정한다(이 catch-all은 MapFallbackToFile보다 우선순위가
// 높고, 리터럴 컨트롤러 라우트보다는 낮아 실 API는 그대로 컨트롤러가 처리). 이렇게 하지 않으면
// /api/monitor/오타 같은 요청이 index.html(HTML 200)로 떨어져 프론트 fetch가 JSON 파싱에
// 조용히 실패한다. 그 외 경로(SPA 딥링크)만 index.html로 폴백 — wwwroot/index.html 부재 시 404(무해).
// ════════════════════════════════════════════════════════════════════════════
app.Map("/api/{**rest}", () => Results.NotFound());
app.MapFallbackToFile("index.html");

app.Run();

// ════════════════════════════════════════════════════════════════════════════
// OperationLogClassifier — 핸드셰이크 단계 action → operation_log 레벨 분류 (A-1)
// 단일 진실: 위 SubscribeHandshakeStage 구독부와 테스트(CleanupFieldM1Tests)가 이 함수를 공유해
// 분류 로직 중복·표류를 막는다.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>핸드셰이크 단계 로그 레벨 분류기 — operation_log HANDSHAKE 레벨 산출(A-1).</summary>
public static class OperationLogClassifier
{
    /// <summary>
    /// 핸드셰이크 단계 action 문자열 → operation_log 레벨.
    ///   · MISMATCH/TIMEOUT/OFFLINE 포함 → ERROR (실패·이상. HS_R_RESIDUE_TIMEOUT은 TIMEOUT 포함 → ERROR).
    ///   · RESIDUE 포함(그 외)          → WARN  (A-1: 잔류 감지 HS_R_RESIDUE — 현장 추적 핵심. INFO 매몰 방지).
    ///   · 그 외                        → INFO  (정상 진행 단계 HS_C_SENT/HS_R_RECV/HS_RSEQ_MATCH 등).
    /// </summary>
    public static Wcs.Data.OperationLogLevel ForHandshakeStage(string action)
    {
        if (action.Contains("MISMATCH") || action.Contains("TIMEOUT") || action.Contains("OFFLINE"))
            return Wcs.Data.OperationLogLevel.ERROR;
        if (action.Contains("RESIDUE"))
            return Wcs.Data.OperationLogLevel.WARN;
        return Wcs.Data.OperationLogLevel.INFO;
    }
}

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

    // S-MULTISORTER-SHARED-BUS Phase 2 — 생명주기 = 버스 단위(배경 3).
    // 버스 목록: StopAsync가 버스마다 ModbusBus.StopAsync를 1회 호출(멤버 번들 개별 teardown 금지).
    private List<Wcs.PlcGateway.ModbusBus>? _buses;
    private IReadOnlyList<BusInfo>?         _busInfos;

    /// <summary>공유 버스 진단 정보(버스 1개 = SharedModbusConnection 1개 = 포트/마스터 1개).</summary>
    public sealed record BusInfo(string BusKey, IReadOnlyList<byte> UnitIds, int MemberCount);

    /// <summary>기동 후 구성된 버스 목록(진단·테스트용 — StartAsync 완료 후 유효). 각 항목 = 물리 버스 1개.</summary>
    public IReadOnlyList<BusInfo> Buses => _busInfos ?? Array.Empty<BusInfo>();

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

        // ── 버스 키 그룹핑 구성 (S-MULTISORTER-SHARED-BUS Phase 2 — 계약 A1) ────────
        // 같은 버스 키(RTU=PortName 대소문자무시 / TCP=Host:Port 입력그대로 — OQ8)를 가진 소터들을
        // 하나의 ISharedModbusConnection + ModbusBus 위에 unitId로 구분해 공유 운영한다.
        // 다른 버스 키는 각자 독립 ModbusBus로 병렬(멀티 포트 회귀 0 — A2). 멤버 1개(N=1)는 현행 단일
        // 소터와 동작 동치(A3): 소켓/IO 하드오류 재연결·OFFLINE·arming·teardown 의미 보존.
        var bundles  = new Dictionary<long, SorterBundleHandle>();
        var logFac   = _sp.GetRequiredService<ILoggerFactory>();
        var timing   = _config.GetSection("Timing").Get<PlcGatewayOptions>() ?? new PlcGatewayOptions();

        // ① destination → (cfg, per-member gwOpt) 매핑 (ChuteNo 매칭 fail-loud 보존)
        var members = new List<(Wcs.Data.Destination dest, SorterConfig cfg, PlcGatewayOptions gwOpt)>();
        foreach (var dest in sorterDests)
        {
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
            // 공통 Timing + 소터별 오버라이드 → per-member 게이트웨이 옵션(멤버별 핸드셰이크 Timing 보존 — OQ9-ii).
            members.Add((dest, cfg, BuildGatewayOptions(timing, cfg)));
        }

        // ② 버스 키로 그룹핑 → 그룹당 공유 연결 1개 + ModbusBus 1개 → 멤버 AddSlave(cfg.UnitId, per-member opt).
        // 연결 생성은 seam(ISharedModbusConnectionFactory) 경유 — DI 미등록이면 기본 팩토리(정적 위임).
        // 테스트가 데코레이터(예: read timeout 주입)를 끼워 실 레지스트리 경로를 검증할 수 있게 함(C4 fix 테스트).
        var connFactory = _sp.GetService<Wcs.PlcGateway.ISharedModbusConnectionFactory>()
                          ?? new Wcs.PlcGateway.DefaultSharedModbusConnectionFactory();
        var buses     = new List<Wcs.PlcGateway.ModbusBus>();
        var busInfos  = new List<BusInfo>();
        try
        {
            foreach (var group in members.GroupBy(m => BusKeyOf(m.cfg)))
            {
                var busKey  = group.Key;
                var groupMs = group.ToList();

                // fail-loud: 같은 버스 멤버의 시리얼 파라미터·폴 주기 불일치(OQ4/OQ9-i/OQ10).
                ValidateBusGroupConsistency(busKey, groupMs);

                // 그룹 대표 전송 설정으로 공유 연결 1개(포트/마스터 1개) — 버스 키 규칙과 일치(TCP host:port / RTU portName).
                var rep  = groupMs[0];
                var conn = connFactory.Create(
                    rep.cfg.ToTransportOptions(),
                    logFac.CreateLogger("Wcs.PlcGateway.SharedModbusConnection"));
                // 폴 cadence(PollIntervalMs)는 버스 단위 1개 — 그룹의 (일치 강제된) 대표 gwOpt를 버스 opt로.
                var bus = new Wcs.PlcGateway.ModbusBus(conn, rep.gwOpt, logFac.CreateLogger("Wcs.PlcGateway.ModbusBus"));
                buses.Add(bus);   // 예외 시에도 정리되도록 즉시 추적.

                foreach (var m in groupMs)
                {
                    PlcPollingService polling;
                    try
                    {
                        // per-member opt 오버로드 — 멤버별 handshake Timing/OfflineAfterFailures/WriteTimeoutMs 보존.
                        polling = bus.AddSlave(m.cfg.UnitId, m.gwOpt, logFac.CreateLogger<PlcPollingService>());
                    }
                    catch (InvalidOperationException ex)
                    {
                        // OQ11 — 같은 버스 중복 UnitId(ModbusBus.AddSlave가 throw). 명확 메시지 + LogCritical로 표면화.
                        _log.LogCritical(ex,
                            "[SorterRegistry] 버스 {Bus} UnitId {Unit} 중복 — 기동 불가(fail-loud). " +
                            "한 물리 버스의 슬레이브 주소(UnitId)는 유일해야 합니다.", busKey, m.cfg.UnitId);
                        throw new InvalidOperationException(
                            $"버스 '{busKey}' UnitId {m.cfg.UnitId} 중복 — 한 물리 버스의 슬레이브 주소는 유일해야 합니다. fail-loud.", ex);
                    }

                    var handshake = new HandshakeOrchestrator(
                        polling, m.gwOpt, logFac.CreateLogger<HandshakeOrchestrator>());
                    var bundle = new SorterBundleHandle(
                        m.dest.Id, m.dest.ChuteNo, polling, handshake,
                        // 생명주기 = 버스 단위(배경 3): 버스 멤버 번들은 공유 큐를 소유하지 않는다(writeQueue=null).
                        // ⚠ writeQueue=null은 StopPollingAsync의 '큐 완료(TryComplete)' 벡터만 무력화한다. 그 안의
                        //   _polling.StopAsync()→_master.Disconnect()(BusSlaveMaster→공유 연결 Disconnect)는 여전히
                        //   형제를 끊으므로, 버스 멤버 번들은 절대 개별 StopPollingAsync로 teardown하지 않는다
                        //   (teardown은 버스 단위 ModbusBus.StopAsync 1회만 — StopAsync 참고). 향후 per-소터 재시작도
                        //   버스 단위 제어로 가야 한다. (Disconnect를 no-op화하지 않는다 — solo 하드 재연결이 이에 의존.)
                        writeQueue: null);
                    bundles[m.dest.Id] = bundle;
                }

                busInfos.Add(new BusInfo(
                    bus.BusKey, bus.Slaves.Select(s => s.UnitId).ToArray(), bus.Slaves.Count));
                _log.LogInformation(
                    "[SorterRegistry] 공유 버스 구성: busKey={Bus} 멤버 {Count}대 (unitIds=[{Units}]) transport={Transport}",
                    bus.BusKey, bus.Slaves.Count, string.Join(",", bus.Slaves.Select(s => s.UnitId)), rep.cfg.Transport);
            }
        }
        catch
        {
            // fail-loud/부분 구성 실패 — 이미 만든 버스(공유 연결 포함) 결정 종료 후 재던짐(포트 누수 0).
            foreach (var b in buses) { try { await b.DisposeAsync(); } catch { /* teardown 경쟁 흡수 */ } }
            throw;
        }

        // ── 관측/OFFLINE 구독 (버스 StartAsync 이전 — 첫 폴 포착) ─────────────────
        // P3: OFFLINE 전이당 1건 alarm 영속화 — IServiceScopeFactory 경유 별도 스코프.
        // 폴링 시작 전에 구독하면 첫 폴 실패도 포착 가능(이벤트는 true→false 전이만 발화).
        // S-OBSERVABILITY: operation_log 싱크(논블로킹 enqueue) — 관측 훅이 직접 호출.
        // IOperationLogger는 싱글톤(OperationLogService)이라 폴/쓰기 스레드에서 직접 취득 안전.
        // 소프트 의존: 미등록(IOperationLogger 없는 최소 호스트 — 일부 단위 테스트)이면 관측 훅 구독을
        // 건너뛴다(게이트웨이 동작은 그대로 — 부수 기록만 비활성). 강제 의존을 만들지 않는다(fail-safe).
        var opLog = _sp.GetService<Wcs.Data.IOperationLogger>();

        // S-TRACE-LOG-VIEWER: 전용 추적 로그 sink(이벤트 4·5·6) — operation_log 구독과 나란히 "추가 구독".
        //   HandshakeOrchestrator/PlcGateway 무접촉 — 기존 콜백(HS_C_SENT·CELL_ASSIGN·C_Flag 델타)만 소비.
        //   소프트 의존: 미등록(최소 호스트)이면 건너뜀(부수 관측만 비활성 — 게이트웨이 동작 불변).
        var trace       = _sp.GetService<ITraceLogger>();
        var correlator  = _sp.GetService<TraceCorrelator>();

        foreach (var bundle in bundles.Values)
        {
            // OFFLINE 이벤트 구독: 전이당 1회만 발화 — API 계층에서 alarm 기록
            var capturedDestId  = bundle.DestinationId;
            var capturedChuteNo = bundle.ChuteNo;

            // ── S-OBSERVABILITY 관측 훅 구독(부수 기록 — 게이트웨이 의미·타이밍 0 변경) ──
            // 모든 핸들러는 IOperationLogger.Log(논블로킹·fail-safe)만 호출 → 폴/쓰기 핫패스 비지연.
            if (opLog is not null)
            {
                bundle.SubscribeWrite((action, detail) =>
                    opLog.Log(Wcs.Data.OperationLogCategory.PLC_WRITE, action,
                        sorterChuteNo: capturedChuteNo, destinationId: capturedDestId, detail: detail));
                bundle.SubscribeRegisterChange((reg, oldV, newV) =>
                    opLog.Log(Wcs.Data.OperationLogCategory.POLL_CHANGE, "REG_CHANGE",
                        level: Wcs.Data.OperationLogLevel.INFO,
                        sorterChuteNo: capturedChuteNo, destinationId: capturedDestId,
                        detail: $"{{\"reg\":\"{reg}\",\"old\":{oldV},\"new\":{newV}}}"));
                bundle.SubscribeHandshakeStage((action, detail) =>
                    opLog.Log(Wcs.Data.OperationLogCategory.HANDSHAKE, action,
                        // A-1: 레벨 분류를 단일 진실(OperationLogClassifier)로 위임 — RESIDUE는 WARN 승격.
                        level: OperationLogClassifier.ForHandshakeStage(action),
                        sorterChuteNo: capturedChuteNo, destinationId: capturedDestId, detail: detail));
                bundle.SubscribeOnline(_ =>
                    opLog.Log(Wcs.Data.OperationLogCategory.STATE, "ONLINE",
                        sorterChuteNo: capturedChuteNo, destinationId: capturedDestId,
                        detail: $"{{\"destId\":{capturedDestId},\"chuteNo\":{capturedChuteNo}}}"));
                // STATE/OFFLINE operation_log(전이당 1회) — 기존 alarm 기록(아래 별도 구독)과 비중복(다른 테이블).
                bundle.SubscribeOffline(_ =>
                    opLog.Log(Wcs.Data.OperationLogCategory.STATE, "OFFLINE",
                        level: Wcs.Data.OperationLogLevel.ERROR,
                        sorterChuteNo: capturedChuteNo, destinationId: capturedDestId,
                        detail: $"{{\"destId\":{capturedDestId},\"chuteNo\":{capturedChuteNo}}}"));
            }

            // ── S-TRACE-LOG-VIEWER: 전용 추적 이벤트 4·5·6 추가 구독(관측/로깅 전용·기존 구독 무변경) ──
            if (trace is not null && correlator is not null)
                TraceWiring.Wire(bundle, trace, correlator);

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
        }

        // ── 버스 단위 폴링 기동 (버스당 ModbusBus.StartAsync 1회 — 계약 A1/A5) ────────
        // 멤버별 StartPollingAsync 아님: ModbusBus가 단일 폴 루프(주기당 1회 대기 후 멤버 순회) +
        // 단일 쓰기 컨슈머를 구동한다(절대규칙 #1 — 쓰기는 버스당 단일 큐 컨슈머로만 직렬화).
        // M2(부분 기동 누수 방지): 폴 루프 기동 '이전'에 _buses를 게시한다. 시작 안 된 버스의 StopAsync는
        // 안전(무동작에 가깝고 결정적)하므로, bus[1].StartAsync가 던져도 이미 시작된 bus[0]가 StopAsync로
        // 결정 종료돼 폴 태스크·열린 포트가 새지 않는다(그 다음 예외는 상위로 전파).
        _buses    = buses;
        _busInfos = busInfos;

        foreach (var bus in buses)
        {
            await bus.StartAsync(cancellationToken);
            _log.LogInformation("[SorterRegistry] 공유 버스 폴링 시작: busKey={Bus} 멤버 {Count}대",
                bus.BusKey, bus.Slaves.Count);
        }

        _registry = new MultiSorterGatewayRegistry(bundles);
        _log.LogInformation("[SorterRegistry] 초기화 완료 — 소터 {Count}대 / 물리 버스 {BusCount}개",
            bundles.Count, buses.Count);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_buses is null) return;

        // ── 생명주기 = 버스 단위(배경 3·계약 A5) ──────────────────────────────────
        // 버스마다 ModbusBus.StopAsync를 1회 호출한다. 그 내부가 (1) 쓰기 큐 Writer.TryComplete로
        // parked ReadAllAsync를 결정적으로 깨우고(교훈: testhost-teardown-channel-race) (2) 폴/쓰기 태스크
        // await (3) 멤버 정지 (4) 공유 연결 Disconnect를 순서대로 수행해 결정 종료한다.
        // ⚠ 버스 멤버 번들에 대해 개별 bundle.StopPollingAsync()로 공유 큐/연결을 조기 teardown하지 않는다
        //   (형제 슬레이브 보호 — writeQueue=null이라 TryComplete/Disconnect가 형제를 죽이지 않음).
        // 호스트 종료 시 로거가 먼저 dispose될 수 있어 로깅도 try로 보호(ObjectDisposedException 방어).
        foreach (var bus in _buses)
        {
            try
            {
                await bus.StopAsync();
            }
            catch (Exception ex)
            {
                try { _log.LogError(ex, "[SorterRegistry] 공유 버스 종료 예외: busKey={Bus}", bus.BusKey); }
                catch { /* 호스트 종료 중 로거 disposed — 로깅 실패 무시 */ }
            }
        }
    }

    // ── 헬퍼: 버스 키 도출 (OQ8) ──────────────────────────────────────────────
    // RTU → PortName(대소문자 무시) / TCP → Host:Port(입력 그대로). host 별칭 정규화 안 함.
    // 전송을 키에 포함해 교차 전송 충돌(예: RTU "COM3" vs TCP host "COM3") 방지.
    private static string BusKeyOf(SorterConfig cfg)
    {
        var t = (cfg.Transport ?? "").Trim().ToUpperInvariant();
        return t switch
        {
            "RTU" => "RTU|" + (cfg.PortName ?? "").ToUpperInvariant(),
            "TCP" => "TCP|" + cfg.Host + ":" + cfg.Port,
            // 알 수 없는 전송 — 연결 생성(SharedModbusConnectionFactory)에서 fail-loud. 그룹핑만 유일화.
            _     => "?|" + cfg.Transport + "|" + cfg.Host + ":" + cfg.Port + "|" + cfg.PortName,
        };
    }

    // ── 헬퍼: 같은 버스 멤버 설정 정합 검사 (fail-loud — OQ4/OQ9-i/OQ10) ─────────
    // 같은 물리 버스의 모든 슬레이브가 공유하는 "버스(연결) 단위" 값의 불일치를 기동 시 거부한다.
    // 대상(버스 단위 = 공유 연결/폴 루프가 1개만 가질 수 있는 값):
    //   · 시리얼 파라미터 BaudRate/Parity/StopBits (OQ10 — DataBits/endianness는 설정 표면에 없어 검사 제외)
    //   · PollIntervalMs (OQ9-i — 버스 폴 루프 cadence 1개)
    //   · ReadTimeoutMs/WriteTimeoutMs (CR-I1 — 공유 클라이언트의 연결 타임아웃은 1개. 그룹 대표값이 실효이고
    //     멤버별로 다르게 줘도 대표만 적용되므로, 조용히 대표가 이기는 대신 fail-loud로 표면화).
    // 진짜 per-member(멤버 PlcPollingService/HandshakeOrchestrator가 인스턴스별로 실효) 값은 검사 안 함:
    //   RFlagTimeoutMs/CFlagTimeoutMs/RFlagClearConfirmTimeoutMs/OfflineAfterFailures(+OfflineLogSummaryEveryPolls).
    private void ValidateBusGroupConsistency(
        string busKey,
        IReadOnlyList<(Wcs.Data.Destination dest, SorterConfig cfg, PlcGatewayOptions gwOpt)> members)
    {
        if (members.Count <= 1) return;   // N=1 버스는 자명하게 정합(하위호환).
        var first = members[0].cfg;
        foreach (var (_, cfg, _) in members.Skip(1))
        {
            if (cfg.BaudRate != first.BaudRate
                || !string.Equals(cfg.Parity,   first.Parity,   StringComparison.OrdinalIgnoreCase)
                || !string.Equals(cfg.StopBits, first.StopBits, StringComparison.OrdinalIgnoreCase))
            {
                _log.LogCritical(
                    "[SorterRegistry] 버스 {Bus} 시리얼 파라미터 불일치 — 기동 불가(fail-loud). " +
                    "기준(Baud={B0} Parity={P0} StopBits={S0}) vs (Baud={B1} Parity={P1} StopBits={S1}).",
                    busKey, first.BaudRate, first.Parity, first.StopBits, cfg.BaudRate, cfg.Parity, cfg.StopBits);
                throw new InvalidOperationException(
                    $"버스 '{busKey}' 멤버의 시리얼 파라미터(BaudRate/Parity/StopBits)가 불일치합니다 — " +
                    $"한 물리 버스의 모든 슬레이브는 동일해야 합니다. fail-loud.");
            }
            if (cfg.PollIntervalMs != first.PollIntervalMs)
            {
                _log.LogCritical(
                    "[SorterRegistry] 버스 {Bus} PollIntervalMs 불일치({A}≠{B}) — 기동 불가(fail-loud). " +
                    "한 물리 버스의 폴 주기는 하나여야 합니다.", busKey, first.PollIntervalMs, cfg.PollIntervalMs);
                throw new InvalidOperationException(
                    $"버스 '{busKey}' 멤버의 PollIntervalMs가 불일치합니다({first.PollIntervalMs}≠{cfg.PollIntervalMs}) — " +
                    $"한 물리 버스의 폴 주기는 하나여야 합니다. fail-loud.");
            }
            if (cfg.ReadTimeoutMs != first.ReadTimeoutMs || cfg.WriteTimeoutMs != first.WriteTimeoutMs)
            {
                _log.LogCritical(
                    "[SorterRegistry] 버스 {Bus} 연결 타임아웃 불일치 — 기동 불가(fail-loud). " +
                    "기준(ReadTimeoutMs={R0} WriteTimeoutMs={W0}) vs (ReadTimeoutMs={R1} WriteTimeoutMs={W1}). " +
                    "공유 연결(클라이언트)의 Read/Write 타임아웃은 버스 단위 1개입니다.",
                    busKey, first.ReadTimeoutMs, first.WriteTimeoutMs, cfg.ReadTimeoutMs, cfg.WriteTimeoutMs);
                throw new InvalidOperationException(
                    $"버스 '{busKey}' 멤버의 연결 타임아웃(ReadTimeoutMs/WriteTimeoutMs)이 불일치합니다 — " +
                    $"공유 연결의 Read/Write 타임아웃은 버스 단위 1개여야 합니다. fail-loud.");
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
            RFlagClearConfirmTimeoutMs =
                t?.RFlagClearConfirmTimeoutMs ?? commonTiming.RFlagClearConfirmTimeoutMs,
            // C1: 복귀 대기(Ready==1) 상한(소터별 오버라이드 or 공통).
            ReturnReadyTimeoutMs =
                t?.ReturnReadyTimeoutMs ?? commonTiming.ReturnReadyTimeoutMs,
            // S-IF10-CWRITE-SETTLE-DELAY: 안착 지연(소터별 오버라이드 or 공통). 소터마다 낙하 높이·기구가
            //   달라 안착 시간이 다를 수 있어 소터별 오버라이드를 둔다(기존 Timing 키와 동형).
            SettleDelayMs =
                t?.SettleDelayMs ?? commonTiming.SettleDelayMs,
            // D-1: OFFLINE 지속 로그 요약 주기(소터별 오버라이드 or 공통).
            OfflineLogSummaryEveryPolls =
                t?.OfflineLogSummaryEveryPolls ?? commonTiming.OfflineLogSummaryEveryPolls,
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

    // S-HANDSHAKE-RESIDUE — 소터별 잔류 대사 확인 타임아웃 오버라이드(null=공통 상속).
    public int? RFlagClearConfirmTimeoutMs { get; init; }

    // S-TWO-FLOOR-CONTROL C1 — 소터별 복귀 대기(Ready==1) 상한 오버라이드(null=공통 상속).
    public int? ReturnReadyTimeoutMs { get; init; }

    // S-IF10-CWRITE-SETTLE-DELAY — 소터별 안착 지연(ms) 오버라이드(null=공통 상속).
    public int? SettleDelayMs { get; init; }

    // S-CLEANUP-FIELD D-1 — 소터별 OFFLINE 지속 로그 요약 주기 오버라이드(null=공통 상속).
    public int? OfflineLogSummaryEveryPolls { get; init; }
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

    // S-HANDSHAKE-RESIDUE — 잔류 대사 ClearR 후 R_Flag==0 확인 대기 상한(ms).
    public int RFlagClearConfirmTimeoutMs { get; init; } = 2000;

    // S-TWO-FLOOR-CONTROL C1 — R_Seq 대사 성공 후 Ready==1(복귀 완료) 관측 대기 상한(ms).
    public int ReturnReadyTimeoutMs { get; init; } = 30000;

    // S-IF10-CWRITE-SETTLE-DELAY — IF-10 수신 후 C 기입 전 안착 지연(ms). 코드 기본 0(비활성/현행).
    public int SettleDelayMs { get; init; } = 0;
}
