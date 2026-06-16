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

// ── Plc / Timing 설정 바인딩 ────────────────────────────────────────────────
builder.Services.Configure<PlcTransportOptions>(builder.Configuration.GetSection("Plc"));
builder.Services.Configure<PlcGatewayOptions>(builder.Configuration.GetSection("Plc"));
builder.Services.Configure<TimingOptions>(builder.Configuration.GetSection("Timing"));

// ── IModbusMaster: 팩토리를 통해 Transport 설정에 따라 생성 ──────────────────
builder.Services.AddSingleton<IModbusMaster>(sp =>
{
    var transportOpt = sp.GetRequiredService<IOptions<PlcTransportOptions>>().Value;
    var logFac       = sp.GetRequiredService<ILoggerFactory>();
    return ModbusMasterFactory.Create(transportOpt, logFac);
});

// ── PlcWriteQueue 싱글톤 (절대규칙 #1 — 단일 큐) ────────────────────────────
builder.Services.AddSingleton<PlcWriteQueue>();

// ── PlcPollingService: IPlcGateway + IHostedService (M3 전환) ─────────────
// M2 수동 Start/Stop 경로 회귀 0 — PlcPollingService는 public StartAsync/StopAsync 유지.
// M3에서는 IHostedService로도 등록해 앱 기동 시 자동 시작.
builder.Services.AddSingleton<PlcPollingService>(sp =>
{
    var gwOpt    = sp.GetRequiredService<IOptions<PlcGatewayOptions>>().Value;
    var queue    = sp.GetRequiredService<PlcWriteQueue>();
    var master   = sp.GetRequiredService<IModbusMaster>();
    var log      = sp.GetRequiredService<ILogger<PlcPollingService>>();
    return new PlcPollingService(gwOpt, queue, master, log);
});
builder.Services.AddSingleton<IPlcGateway>(sp => sp.GetRequiredService<PlcPollingService>());
// IHostedService 등록 — 앱 시작/종료 시 PlcPollingService.StartAsync/StopAsync 자동 호출
builder.Services.AddHostedService<PlcPollingHostedAdapter>();

// ── HandshakeOrchestrator ────────────────────────────────────────────────────
builder.Services.AddSingleton<HandshakeOrchestrator>(sp =>
{
    var gw  = sp.GetRequiredService<PlcPollingService>();
    var opt = sp.GetRequiredService<IOptions<PlcGatewayOptions>>().Value;
    var log = sp.GetRequiredService<ILogger<HandshakeOrchestrator>>();
    return new HandshakeOrchestrator(gw, opt, log);
});

// ── ISorterGatewayRegistry — destination.id 단일 진입점 (P2a: 단일 소터) ────
builder.Services.AddSingleton<ISorterGatewayRegistry>(sp =>
    new SingleSorterGatewayRegistry(sp.GetRequiredService<IPlcGateway>()));

// ── ChuteCapacityService 싱글톤 (FULL/PAUSED 인메모리 집계) ──────────────────
builder.Services.AddSingleton<ChuteCapacityService>();
builder.Services.AddSingleton<IChuteCapacityService>(sp =>
    sp.GetRequiredService<ChuteCapacityService>());
builder.Services.AddHostedService<ChuteCapacityService>(sp =>
    sp.GetRequiredService<ChuteCapacityService>());

// ── WcsDbContext 등록 (M4 — appsettings에서 provider·연결문자열 선택) ────────
// 절대규칙 8: 연결문자열·provider 하드코딩 금지.
var dbProvider         = builder.Configuration["Database:Provider"] ?? "Sqlite";
var connectionString   = builder.Configuration.GetConnectionString("WcsDb")
                         ?? "Data Source=wcs.db";

builder.Services.AddDbContext<WcsDbContext>(opts =>
{
    if (dbProvider.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
        // 마이그레이션 어셈블리: Wcs.Migrations.SqlServer (독립 ModelSnapshot — provider별 분리)
        opts.UseSqlServer(connectionString,
            sql => sql.MigrationsAssembly("Wcs.Migrations.SqlServer")
                      .MigrationsHistoryTable("__EFMigrationHistory"));
    else
        // 마이그레이션 어셈블리: Wcs.Migrations.Sqlite (독립 ModelSnapshot — provider별 분리)
        opts.UseSqlite(connectionString,
            sqlite => sqlite.MigrationsAssembly("Wcs.Migrations.Sqlite")
                            .MigrationsHistoryTable("__EFMigrationHistory"));
}, ServiceLifetime.Scoped);

// ── 4 인터페이스 EF Core 구현 바인딩 (M4 교체점 1지점) ───────────────────────
// InMemory* 프로덕션 경로 제거 — DB 구현으로 교체.
builder.Services.AddScoped<EfDepositRecorder>();
builder.Services.AddScoped<IDepositRecorder>(sp => sp.GetRequiredService<EfDepositRecorder>());
builder.Services.AddScoped<IOrderRepository, EfOrderRepository>();
builder.Services.AddScoped<ICellSelector,    EfCellSelector>();
builder.Services.AddScoped<IAgvFloorResolver, EfAgvFloorResolver>();

// ── agvNo→층 설정은 시드 전용으로 강등 — 런타임 조회는 agv.floor DB ─────────
// IAgvFloorResolver는 EfAgvFloorResolver(DB)로 교체. appsettings 매핑은 시드에서만 사용.

var app = builder.Build();

// ── 개발/테스트: 마이그레이션 + 시드 자동 적용 (테스트 배선용) ───────────────
// 운영 기동 자동 Migrate()는 M5 — P1은 테스트에서만 적용(WebApplicationFactory 주입).
// 프로덕션에서는 이 블록이 호출되지 않음(테스트 WebApplicationFactory가 재정의).

// ════════════════════════════════════════════════════════════════════════════
// (C) 엔드포인트
// ════════════════════════════════════════════════════════════════════════════

// ── IF-05 목적지 조회 ────────────────────────────────────────────────────────
// 요청: pId(1~30000 필수)·agvNo·barcode·inductionNo·qty·timeStamp
// 응답: 200 {result,chuteNo,reason} / 400(검증 실패)
// 가부는 result 필드(200), 검증 실패만 400
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
    // qty <= 0이면 예약 차감 음수→ReservedQty 손상(fail-loud 원칙 — 절대규칙 참조)
    if (req.Qty <= 0)
        return Results.BadRequest(new { error = "qty는 1 이상이어야 합니다." });

    // ── 오더 매칭 → 목적지·상태 판정 → OK 시 예약 차감 ─────────────────────
    // timeStamp 백필: clientTs 원문 전달 (EfOrderRepository 내부에서 파싱)
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
// 가부는 allowed 필드(200), 검증 실패만 400
//
// Scope-1: chuteNo → destination → dest_type 분기
//   SORTER_3D: 게이트웨이 스냅샷 + WcsHold → Decide(snap, agvFloor, hold)
//   CHUTE: hold만 판정(NORMAL/Full/Paused/비활성→PAUSED) — TgtFloor 쓰기 없음
app.MapPost("/api/v1/deposit-permission", (
    DepositPermissionRequest req,
    ISorterGatewayRegistry   sorterRegistry,
    IChuteCapacityService    capacity,
    IAgvFloorResolver        floorResolver,
    PlcWriteQueue            writeQueue,
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
        // 비활성·미존재 슈트 → PAUSED 매핑(SPEC §2 비활성→PAUSED)
        log.LogWarning("[IF-08] pId={PId} chuteNo={ChuteNo} — 목적지 없음(비활성/미존재) → PAUSED",
            req.PId, req.ChuteNo);
        return Results.Ok(new DepositPermissionResponse(false, "PAUSED"));
    }

    if (dest.DestType == DestType.SORTER_3D)
    {
        // ── SORTER_3D 경로: 게이트웨이 스냅샷 + WcsHold → Decide ────────────
        // agvFloor 산출 (agvNo → 층)
        var agvFloor = floorResolver.Resolve(req.AgvNo);
        if (agvFloor is null)
            return Results.BadRequest(new
            {
                error = $"agvNo={req.AgvNo}에 대한 층 매핑이 없습니다. agv 테이블을 확인하세요."
            });

        // 게이트웨이 스냅샷(논블로킹) — destination.id 단일 진입점(P2a 단일 소터, P2b N대 확장점)
        var snap = sorterRegistry.GetLatest(dest.Id);
        if (snap is null)
        {
            log.LogWarning("[IF-08] destinationId={Id} 소터 게이트웨이 없음 → OFFLINE", dest.Id);
            return Results.Ok(new DepositPermissionResponse(false, "OFFLINE"));
        }

        // FULL/PAUSED 인메모리 집계 → WcsHold 산출
        // 소터는 capacity 서비스 대신 빈 셀 유무로 FULL 판정(ChuteCapacityService는 CHUTE 전용)
        // P2a: WcsHold.None — 소터 FULL은 셀 선택기에서 판단(핸드셰이크 후)
        var hold = WcsHold.None;

        var decision = DepositDecider.Decide(snap, agvFloor.Value, hold);

        // ── WriteTgtFloor면 큐 투입 (완료 대기 X — API 3s 한계 분리) ─────────
        if (decision.WriteTgtFloor)
        {
            _ = writeQueue.EnqueueAsync(new PlcWrite.SetTgtFloor(decision.TgtFloorValue))
                           .AsTask()
                           .ContinueWith(t =>
                           {
                               if (t.IsFaulted)
                                   log.LogError(t.Exception, "[IF-08] SetTgtFloor 큐 투입 예외");
                           }, TaskScheduler.Default);
        }

        // 응답 생성: allowed=true → reason="READY" (API 계층 주입)
        var reason3d = decision.Allowed ? "READY" : decision.Reason.ToWire();

        log.LogInformation(
            "[IF-08/SORTER] pId={PId} agvNo={AgvNo} agvFloor={Floor} → allowed={Allowed} reason={Reason} WriteTgtFloor={Write}",
            req.PId, req.AgvNo, agvFloor, decision.Allowed, reason3d, decision.WriteTgtFloor);

        return Results.Ok(new DepositPermissionResponse(decision.Allowed, reason3d));
    }
    else
    {
        // ── CHUTE 경로: hold만 판정 — agvFloor·TgtFloor 쓰기 없음 ────────────
        // SPEC §2/§3: 슈트는 층 이동·Ready 판정 없음.
        // 비활성 슈트 → PAUSED, FULL 조건 → FULL, PAUSED status → PAUSED, NORMAL → READY
        WcsHold hold;

        if (!dest.IsActive)
        {
            // 비활성 슈트 → PAUSED 매핑
            hold = WcsHold.Paused;
        }
        else
        {
            // FULL/PAUSED 인메모리 집계에서 hold 산출
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
// 멱등: pId 중복 보고 무해
// 3D 목적지면 IF-11 트리거 (핸드셰이크 백그라운드 — 즉시 OK 반환)
app.MapPost("/api/v1/deposit-report", (
    DepositReportRequest     req,
    IDepositRecorder         recorder,
    ICellSelector            cellSelector,
    IChuteCapacityService    capacity,
    IOrderRepository         orders,
    WcsDbContext             db,
    HandshakeOrchestrator    handshake,
    IHostApplicationLifetime lifetime,
    ILogger<Program>         log) =>
{
    // ── 검증 ────────────────────────────────────────────────────────────────
    if (req.PId is < 1 or > 30000)
        return Results.BadRequest(new { error = "pId는 1~30000 범위여야 합니다." });
    if (string.IsNullOrWhiteSpace(req.Barcode))
        return Results.BadRequest(new { error = "barcode는 필수입니다." });
    if (req.ChuteNo <= 0)
        return Results.BadRequest(new { error = "chuteNo는 양수여야 합니다." });

    // ── 투입 기록 + 멱등 (원자 결합) ─────────────────────────────────────────
    // RecordDeposit이 check-then-act를 트랜잭션으로 원자화 — true=신규 기록, false=중복(멱등).
    // static _recordLock 제거 — piece 부분 유니크 + catch로 진성 멱등 보장(MAJOR-1).
    var isNewRecord = recorder.RecordDeposit(
        req.PId, req.Barcode, req.ChuteNo, req.AgvNo, req.Qty, req.TimeStamp);

    if (!isNewRecord)
    {
        log.LogInformation("[IF-10] pId={PId} 중복 보고 — 멱등 OK", req.PId);
        return Results.Ok(new DepositReportResponse("OK"));
    }

    // ── 목적지 타입 조회 → FULL 집계 반영 + IF-11 트리거 ─────────────────────
    // Scope-9: EfDepositRecorder 다운캐스트 제거 — DB 직접 조회로 대체
    var dest = db.Destinations
        .FirstOrDefault(d => d.ChuteNo == req.ChuteNo && d.IsActive);

    var destType = dest?.DestType switch
    {
        DestType.CHUTE     => DestinationType.Chute,
        DestType.SORTER_3D => DestinationType.Sorter3D,
        _                  => (DestinationType?)null,
    };

    // ── FULL/PAUSED 인메모리 집계: IF-10 투입 반영 ────────────────────────────
    if (dest is not null && destType == DestinationType.Chute)
    {
        // piece.qty는 IF-05 등록값 — DB에서 조회 (req.Qty가 없을 수 있음)
        var piece = db.Pieces.FirstOrDefault(p => p.PId == req.PId && p.IsActive);
        var qty   = piece?.Qty ?? req.Qty ?? 1;
        capacity.OnDeposited(dest.Id, qty);
    }

    if (destType == DestinationType.Sorter3D)
    {
        // 셀 선택
        var cellNo = cellSelector.SelectCell(req.ChuteNo, req.Barcode);

        if (cellNo.HasValue)
        {
            int selectedCell = cellNo.Value;
            // IF-11 핸드셰이크: 즉시 OK 반환, 핸드셰이크는 백그라운드
            // Scope-9: CancellationToken.None → IHostApplicationLifetime.ApplicationStopping
            _ = handshake.ExecuteAsync(selectedCell, lifetime.ApplicationStopping)
                .ContinueWith(t =>
                {
                    if (t.IsCompletedSuccessfully)
                        log.LogInformation(
                            "[IF-11] 핸드셰이크 완료: pId={PId} cellNo={CellNo} outcome={Outcome}",
                            req.PId, selectedCell, t.Result.Outcome);
                    else if (t.IsFaulted)
                        log.LogError(t.Exception,
                            "[IF-11] 핸드셰이크 예외: pId={PId} cellNo={CellNo}",
                            req.PId, selectedCell);
                    // 셀 해제 (핸드셰이크 완료·실패 모두)
                    cellSelector.ReleaseCell(selectedCell);
                }, TaskScheduler.Default);

            log.LogInformation("[IF-10] pId={PId} 3D 보고 → IF-11 트리거: cellNo={CellNo}", req.PId, selectedCell);
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
// PlcPollingHostedAdapter — IHostedService 어댑터
// PlcPollingService.StartAsync/StopAsync(수동 경로·M2 회귀 0) 유지하면서
// ASP.NET Core IHostedService(자동 기동)로도 동작하게 브리지.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// PlcPollingService를 IHostedService에 연결하는 어댑터.
/// PlcPollingService의 수동 StartAsync/StopAsync 경로(M2 통합 테스트)를 그대로 보존하면서
/// ASP.NET Core가 앱 시작 시 자동으로 PLC 폴링을 기동하도록 브리지한다.
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
