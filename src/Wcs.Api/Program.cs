using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wcs.Api;
using Wcs.Core;
using Wcs.Data;
using Wcs.PlcGateway;

var builder = WebApplication.CreateBuilder(args);
// TODO(M5): builder.Host.UseWindowsService(); + Serilog

// ════════════════════════════════════════════════════════════════════════════
// (D) DI 배선
// ════════════════════════════════════════════════════════════════════════════

// ── 공통 Timing 설정 바인딩 ─────────────────────────────────────────────────
builder.Services.Configure<TimingOptions>(builder.Configuration.GetSection("Timing"));

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

var app = builder.Build();

// ── 개발/테스트: 마이그레이션 + 시드 자동 적용 (테스트 배선용) ───────────────
// 운영 기동 자동 Migrate()는 M5 — P1은 테스트에서만 적용(WebApplicationFactory 주입).

// ════════════════════════════════════════════════════════════════════════════
// (C) 엔드포인트
// ════════════════════════════════════════════════════════════════════════════

// ── IF-05 목적지 조회 ────────────────────────────────────────────────────────
// 요청: pId(1~30000 필수)·agvNo·barcode·inductionNo·qty·timeStamp
// 응답: 200 {result,chuteNo,reason} / 400(검증 실패)
app.MapPost("/api/v1/destination-query", (
    DestinationQueryRequest  req,
    IOrderRepository         orders,
    IChuteCapacityService    capacity,
    ILogger<Program>         log) =>
{
    // ── 검증 ────────────────────────────────────────────────────────────────
    if (req.PId is < 1 or > 30000)
        return Results.BadRequest(new { error = "pId는 1~30000 범위여야 합니다." });
    if (string.IsNullOrWhiteSpace(req.Barcode))
        return Results.BadRequest(new { error = "barcode는 필수입니다." });
    if (req.Qty <= 0)
        return Results.BadRequest(new { error = "qty는 1 이상이어야 합니다." });

    // ── 오더 매칭 → 목적지·상태 판정 → OK 시 예약 차감 ─────────────────────
    var (result, chuteNo, reason, destType, destId) =
        orders.QueryDestination(req.PId, req.AgvNo, req.Barcode, req.InductionNo, req.Qty, req.TimeStamp);

    // ── FULL/PAUSED 인메모리 집계: IF-05 OK 예약 반영 ──────────────────────
    if (result == "OK" && destId.HasValue && destType == DestinationType.Chute)
        capacity.OnReserved(destId.Value, req.Qty);

    log.LogInformation("[IF-05] pId={PId} barcode={Barcode} → result={Result} chuteNo={ChuteNo} reason={Reason}",
        req.PId, req.Barcode, result, chuteNo, reason);

    return Results.Ok(new DestinationQueryResponse(result, chuteNo, reason));
});

// ── IF-08 투입 가부 ──────────────────────────────────────────────────────────
// 요청: pId·chuteNo·agvNo (timeStamp nullable 선택필드)
// 응답: 200 {allowed,reason} / 400(검증 실패)
//
// P2b 라우팅: chuteNo → destination.id → ISorterGatewayRegistry.GetBundle(id)
//   SORTER_3D: 번들 핸들 스냅샷 + WcsHold → Decide(snap, agvFloor, hold)
//              WriteTgtFloor면 번들 큐 투입(단일 공유 큐 제거 — 소터별 큐 경유)
//   CHUTE: hold만 판정 — TgtFloor 쓰기 없음
app.MapPost("/api/v1/deposit-permission", (
    DepositPermissionRequest req,
    ISorterGatewayRegistry   sorterRegistry,
    IChuteCapacityService    capacity,
    IAgvFloorResolver        floorResolver,
    WcsDbContext             db,
    ILogger<Program>         log) =>
{
    // ── 검증 ────────────────────────────────────────────────────────────────
    if (req.PId is < 1 or > 30000)
        return Results.BadRequest(new { error = "pId는 1~30000 범위여야 합니다." });
    if (req.ChuteNo <= 0)
        return Results.BadRequest(new { error = "chuteNo는 양수여야 합니다." });

    // ── chuteNo → destination 조회 ─────────────────────────────────────────
    var dest = db.Destinations
        .FirstOrDefault(d => d.ChuteNo == req.ChuteNo && d.IsActive);

    if (dest is null)
    {
        log.LogWarning("[IF-08] pId={PId} chuteNo={ChuteNo} — 목적지 없음(비활성/미존재) → PAUSED",
            req.PId, req.ChuteNo);
        return Results.Ok(new DepositPermissionResponse(false, "PAUSED"));
    }

    if (dest.DestType == DestType.SORTER_3D)
    {
        // ── SORTER_3D 경로: 번들 핸들 스냅샷 + WcsHold → Decide ─────────────
        // agvFloor 산출 (agvNo → 층)
        var agvFloor = floorResolver.Resolve(req.AgvNo);
        if (agvFloor is null)
            return Results.BadRequest(new
            {
                error = $"agvNo={req.AgvNo}에 대한 층 매핑이 없습니다. agv 테이블을 확인하세요."
            });

        // destination.id → 번들 핸들 조회 (P2b 라우팅: chuteNo→dest.Id→번들)
        var bundle = sorterRegistry.GetBundle(dest.Id);
        if (bundle is null)
        {
            log.LogWarning("[IF-08] destinationId={Id} 소터 번들 없음 → OFFLINE", dest.Id);
            return Results.Ok(new DepositPermissionResponse(false, "OFFLINE"));
        }

        // 번들 스냅샷 조회(논블로킹)
        var snap = bundle.Latest;
        if (!snap.Online)
        {
            log.LogWarning("[IF-08] destinationId={Id} 소터 OFFLINE", dest.Id);
            return Results.Ok(new DepositPermissionResponse(false, "OFFLINE"));
        }

        // P2a: WcsHold.None — 소터 FULL은 셀 선택기에서 판단
        var hold = WcsHold.None;

        var decision = DepositDecider.Decide(snap, agvFloor.Value, hold);

        // ── WriteTgtFloor면 번들 전용 큐 투입 (단일 공유 큐 제거 — 소터별) ───
        if (decision.WriteTgtFloor)
        {
            _ = bundle.EnqueueSetTgtFloorAsync(decision.TgtFloorValue)
                       .AsTask()
                       .ContinueWith(t =>
                       {
                           if (t.IsFaulted)
                               log.LogError(t.Exception, "[IF-08] SetTgtFloor 번들 큐 투입 예외 destinationId={Id}", dest.Id);
                       }, TaskScheduler.Default);
        }

        var reason3d = decision.Allowed ? "READY" : decision.Reason.ToWire();

        log.LogInformation(
            "[IF-08/SORTER] pId={PId} agvNo={AgvNo} agvFloor={Floor} destId={DestId} → allowed={Allowed} reason={Reason} WriteTgtFloor={Write}",
            req.PId, req.AgvNo, agvFloor, dest.Id, decision.Allowed, reason3d, decision.WriteTgtFloor);

        return Results.Ok(new DepositPermissionResponse(decision.Allowed, reason3d));
    }
    else
    {
        // ── CHUTE 경로: hold만 판정 — agvFloor·TgtFloor 쓰기 없음 ────────────
        WcsHold hold;

        if (!dest.IsActive)
        {
            hold = WcsHold.Paused;
        }
        else
        {
            hold = capacity.GetHold(dest.Id);
        }

        bool   allowed;
        string reason;

        switch (hold)
        {
            case WcsHold.Full:
                allowed = false;
                reason  = "FULL";
                break;
            case WcsHold.Paused:
                allowed = false;
                reason  = "PAUSED";
                break;
            default: // WcsHold.None
                allowed = true;
                reason  = "READY";
                break;
        }

        log.LogInformation(
            "[IF-08/CHUTE] pId={PId} chuteNo={ChuteNo} hold={Hold} → allowed={Allowed} reason={Reason}",
            req.PId, req.ChuteNo, hold, allowed, reason);

        return Results.Ok(new DepositPermissionResponse(allowed, reason));
    }
});

// ── IF-10 투입 보고 ──────────────────────────────────────────────────────────
// 요청: pId·barcode·chuteNo·agvNo (qty·timeStamp nullable 선택필드)
// 응답: 200 {result:"OK"} / 400(검증 실패)
// 3D 목적지면 번들 핸들의 핸드셰이크를 트리거 (소터별 독립 — 인스턴스별 _cSeq 보존)
app.MapPost("/api/v1/deposit-report", (
    DepositReportRequest     req,
    IDepositRecorder         recorder,
    ICellSelector            cellSelector,
    IChuteCapacityService    capacity,
    IOrderRepository         orders,
    WcsDbContext             db,
    ISorterGatewayRegistry   sorterRegistry,
    IHostApplicationLifetime lifetime,
    IServiceScopeFactory     scopeFactory,
    ILogger<Program>         log) =>
{
    // ── 검증 ────────────────────────────────────────────────────────────────
    if (req.PId is < 1 or > 30000)
        return Results.BadRequest(new { error = "pId는 1~30000 범위여야 합니다." });
    if (string.IsNullOrWhiteSpace(req.Barcode))
        return Results.BadRequest(new { error = "barcode는 필수입니다." });
    if (req.ChuteNo <= 0)
        return Results.BadRequest(new { error = "chuteNo는 양수여야 합니다." });

    // ── 투입 기록 + 멱등 ──────────────────────────────────────────────────────
    var isNewRecord = recorder.RecordDeposit(
        req.PId, req.Barcode, req.ChuteNo, req.AgvNo, req.Qty, req.TimeStamp);

    if (!isNewRecord)
    {
        log.LogInformation("[IF-10] pId={PId} 중복 보고 — 멱등 OK", req.PId);
        return Results.Ok(new DepositReportResponse("OK"));
    }

    // ── 목적지 타입 조회 → FULL 집계 반영 + IF-11 트리거 ─────────────────────
    var dest = db.Destinations
        .FirstOrDefault(d => d.ChuteNo == req.ChuteNo && d.IsActive);

    var destType = dest?.DestType switch
    {
        DestType.CHUTE     => DestinationType.Chute,
        DestType.SORTER_3D => DestinationType.Sorter3D,
        _                  => (DestinationType?)null,
    };

    // ── FULL/PAUSED 인메모리 집계: IF-10 투입 반영 ───────────────────────────
    if (dest is not null && destType == DestinationType.Chute)
    {
        var piece = db.Pieces.FirstOrDefault(p => p.PId == req.PId && p.IsActive);
        var qty   = piece?.Qty ?? req.Qty ?? 1;
        capacity.OnDeposited(dest.Id, qty);
    }

    if (destType == DestinationType.Sorter3D && dest is not null)
    {
        // 셀 선택
        var cellNo = cellSelector.SelectCell(req.ChuteNo, req.Barcode);

        if (cellNo.HasValue)
        {
            int selectedCell = cellNo.Value;

            // destination.id → 번들 핸들 조회 (P2b: 소터별 독립 핸드셰이크)
            var bundle = sorterRegistry.GetBundle(dest.Id);

            if (bundle is not null)
            {
                // piece.id 조회 (sorter_command 연결 키 — 백그라운드 콜백에서 사용)
                long pieceId = db.Pieces
                    .Where(p => p.PId == req.PId && p.IsActive)
                    .Select(p => p.Id)
                    .FirstOrDefault();

                // cell.id 조회 (sorter_command.cell_id FK — cellNo로 매핑)
                long cellId = db.Cells
                    .Where(c => c.DestinationId == dest.Id && c.CellNo == selectedCell)
                    .Select(c => c.Id)
                    .FirstOrDefault();

                // IF-11 핸드셰이크: 번들 핸들 경유 — 소터별 독립 _cSeq·RFlag 채널
                // P3 결선: ContinueWith에서 sorter_command + alarm 영속화
                _ = bundle.ExecuteHandshakeAsync(selectedCell, lifetime.ApplicationStopping)
                    .ContinueWith((Task<Wcs.PlcGateway.HandshakeResult> t) =>
                    {
                        // ── sorter_command·alarm·piece 상태 영속화 ─────────────────
                        // 스코프 분리 필수 — 원본 WcsDbContext는 이미 HTTP 요청 스코프 종료.
                        // ObjectDisposedException 방어: 호스트 종료 후 콜백 실행 시 스코프 생성 불가.
                        IServiceScope scope;
                        try
                        {
                            scope = scopeFactory.CreateScope();
                        }
                        catch (ObjectDisposedException)
                        {
                            // 호스트 종료 후 콜백 — 영속화 생략(테스트 teardown 경쟁 조건 방어)
                            return;
                        }
                        using var _ = scope;
                        var journal   = scope.ServiceProvider.GetRequiredService<ISorterCommandJournal>();
                        var alarmSink = scope.ServiceProvider.GetRequiredService<IAlarmSink>();

                        if (t.IsCompletedSuccessfully)
                        {
                            var result = t.Result;
                            log.LogInformation(
                                "[IF-11] 핸드셰이크 완료: pId={PId} cellNo={CellNo} outcome={Outcome} destId={DestId}",
                                req.PId, selectedCell, result.Outcome, dest.Id);

                            // sorter_command SENT → 상태 전이 (한 트랜잭션으로)
                            if (pieceId > 0 && cellId > 0)
                            {
                                try
                                {
                                    var cmdId = journal.CreateSent(pieceId, cellId, result.SentCSeq, selectedCell);
                                    journal.Finalize(cmdId, result);
                                }
                                catch (Exception ex)
                                {
                                    log.LogError(ex, "[IF-11] sorter_command 영속화 예외: pId={PId}", req.PId);
                                }

                                // 실패 시 alarm 기록
                                if (!result.IsSuccess)
                                {
                                    try
                                    {
                                        var code = result.Outcome switch
                                        {
                                            Wcs.PlcGateway.HandshakeOutcome.RSeqMismatch => "R_SEQ_MISMATCH",
                                            Wcs.PlcGateway.HandshakeOutcome.RFlagTimeout => "RFLAG_TIMEOUT",
                                            Wcs.PlcGateway.HandshakeOutcome.Offline      => "OFFLINE",
                                            Wcs.PlcGateway.HandshakeOutcome.CFlagTimeout => "CFLAG_TIMEOUT",
                                            _                                             => "RFLAG_TIMEOUT",
                                        };
                                        alarmSink.Append(code, Wcs.Data.AlarmSeverity.ERROR, pieceId,
                                            $"pId={req.PId} cellNo={selectedCell} detail={result.Detail}");
                                    }
                                    catch (Exception ex)
                                    {
                                        log.LogError(ex, "[IF-11] alarm 영속화 예외: pId={PId}", req.PId);
                                    }
                                }
                            }
                        }
                        else if (t.IsFaulted)
                        {
                            log.LogError(t.Exception,
                                "[IF-11] 핸드셰이크 예외: pId={PId} cellNo={CellNo} destId={DestId}",
                                req.PId, selectedCell, dest.Id);
                        }

                        cellSelector.ReleaseCell(selectedCell);
                    }, TaskScheduler.Default);

                log.LogInformation("[IF-10] pId={PId} 3D 보고 → IF-11 트리거: cellNo={CellNo} destId={DestId}",
                    req.PId, selectedCell, dest.Id);
            }
            else
            {
                // 번들 없음(OFFLINE) — 셀 즉시 해제
                cellSelector.ReleaseCell(selectedCell);
                log.LogWarning("[IF-10] pId={PId} 3D 번들 없음(OFFLINE) — 핸드셰이크 생략", req.PId);
            }
        }
        else
        {
            log.LogWarning("[IF-10] pId={PId} 3D 보고 → 빈 셀 없음 (3DS FULL 조건). IF-11 트리거 생략", req.PId);
        }
    }
    else
    {
        log.LogInformation("[IF-10] pId={PId} 슈트 보고 → IF-11 트리거 없음", req.PId);
    }

    return Results.Ok(new DepositReportResponse("OK"));
});

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

            var bundle = new SorterBundleHandle(dest.Id, dest.ChuteNo, polling, handshake);
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
