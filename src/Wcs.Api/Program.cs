using Microsoft.Extensions.Options;
using Wcs.Api;
using Wcs.Core;
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

// ── 인메모리 리포지토리 + 시드 (M4 교체점 — Wcs.Data DbContext 없음) ─────────
builder.Services.AddSingleton<InMemoryDepositRecorder>(); // DestType 저장 기능 포함
builder.Services.AddSingleton<IDepositRecorder>(sp => sp.GetRequiredService<InMemoryDepositRecorder>());

builder.Services.AddSingleton<IOrderRepository>(sp =>
{
    // 시드: 슈트 기준정보
    var chutes = new List<ChuteInfo>
    {
        new() { ChuteNo = 1, IsEnabled = true, Capacity = 100, CurrentQty = 0 },
        new() { ChuteNo = 2, IsEnabled = true, Capacity = 100, CurrentQty = 0 },
        new() { ChuteNo = 3, IsEnabled = true, Capacity = 100, CurrentQty = 0 },
        new() { ChuteNo = 4, IsEnabled = true, Capacity = 100, CurrentQty = 0 },
        new() { ChuteNo = 5, IsEnabled = true, Capacity = 100, CurrentQty = 0 },
    };
    // 시드: 오더 기준정보
    // M3 테스트용 — 실제 오더는 M4에서 API·DB로 관리
    var orders = new List<OrderItem>
    {
        new() { Barcode = "TEST-BARCODE-1", OrderNo = "ORD-001", DestType = DestinationType.Chute,    ChuteNo = 1, TotalQty = 50 },
        new() { Barcode = "TEST-BARCODE-2", OrderNo = "ORD-002", DestType = DestinationType.Chute,    ChuteNo = 2, TotalQty = 30 },
        new() { Barcode = "TEST-BARCODE-3", OrderNo = "ORD-003", DestType = DestinationType.Sorter3D, ChuteNo = 3, TotalQty = 20 },
        new() { Barcode = "TEST-BARCODE-AUTO", OrderNo = "ORD-004", DestType = DestinationType.Chute, ChuteNo = null, TotalQty = 10 },  // AUTO 배정
        new() { Barcode = "TEST-BARCODE-PAUSED", OrderNo = "ORD-005", DestType = DestinationType.Chute, ChuteNo = 2, TotalQty = 10, IsPaused = true },
    };
    return new InMemoryOrderRepository(orders, chutes);
});

builder.Services.AddSingleton<ICellSelector>(sp =>
{
    // 시드: 3D Sorter 셀 기준정보 (슈트 3번 소속)
    var cells = new List<CellInfo>
    {
        new() { CellNo = 1, SorterChuteNo = 3, IsEnabled = true, IsOccupied = false },
        new() { CellNo = 2, SorterChuteNo = 3, IsEnabled = true, IsOccupied = false },
        new() { CellNo = 3, SorterChuteNo = 3, IsEnabled = true, IsOccupied = false },
    };
    return new InMemoryCellSelector(cells);
});

// ── agvNo→층 매핑 (M3 설정 기반, M4에서 agv.floor 단일 진실 전환) ────────────
builder.Services.AddSingleton<IAgvFloorResolver>(sp =>
{
    // appsettings Floors:AgvNoToFloor — 하드코딩 금지(절대규칙 7)
    var section = builder.Configuration.GetSection("Floors:AgvNoToFloor");
    var map     = section.Get<Dictionary<string, int>>()
                  ?? new Dictionary<string, int>();
    return new ConfigAgvFloorResolver(map);
});

// ── Wcs.Data DbContext는 M4 — 여기에 없어야 함 ────────────────────────────

var app = builder.Build();

// ════════════════════════════════════════════════════════════════════════════
// (C) 엔드포인트
// ════════════════════════════════════════════════════════════════════════════

// ── IF-05 목적지 조회 ────────────────────────────────────────────────────────
// 요청: pId(1~30000 필수)·agvNo·barcode·inductionNo·qty·timeStamp
// 응답: 200 {result,chuteNo,reason} / 400(검증 실패)
// 가부는 result 필드(200), 검증 실패만 400
app.MapPost("/api/v1/destination-query", (
    DestinationQueryRequest      req,
    IOrderRepository             orders,
    IDepositRecorder             recorder,
    IAgvFloorResolver            floorResolver,
    ILogger<Program>             log) =>
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
    var (result, chuteNo, reason, destType) =
        orders.QueryDestination(req.PId, req.AgvNo, req.Barcode, req.InductionNo, req.Qty);

    var status = result == "OK" ? DepositStatus.Ok : DepositStatus.Denied;

    // ── OK·NG 무관 투입 기록 (IF-16 통합) ────────────────────────────────────
    recorder.RecordDestinationQuery(
        req.PId, req.AgvNo, req.Barcode, req.InductionNo,
        chuteNo, req.Qty, status, reason);

    // DestType 저장 (IF-10에서 3D 목적지 여부 판단에 사용)
    if (destType.HasValue && recorder is InMemoryDepositRecorder imr)
        imr.SetDestType(req.PId, destType.Value);

    log.LogInformation("[IF-05] pId={PId} barcode={Barcode} → result={Result} chuteNo={ChuteNo} reason={Reason}",
        req.PId, req.Barcode, result, chuteNo, reason);

    return Results.Ok(new DestinationQueryResponse(result, chuteNo, reason));
});

// ── IF-08 투입 가부 ──────────────────────────────────────────────────────────
// 요청: pId·chuteNo·agvNo (timeStamp nullable 선택필드)
// 응답: 200 {allowed,reason} / 400(검증 실패)
// 가부는 allowed 필드(200), 검증 실패만 400
app.MapPost("/api/v1/deposit-permission", (
    DepositPermissionRequest req,
    IPlcGateway              gateway,
    IAgvFloorResolver        floorResolver,
    PlcWriteQueue            writeQueue,
    ILogger<Program>         log) =>
{
    // ── 검증 ────────────────────────────────────────────────────────────────
    if (req.PId is < 1 or > 30000)
        return Results.BadRequest(new { error = "pId는 1~30000 범위여야 합니다." });
    if (req.ChuteNo <= 0)
        return Results.BadRequest(new { error = "chuteNo는 양수여야 합니다." });

    // ── agvFloor 산출 (agvNo → 층) ──────────────────────────────────────────
    var agvFloor = floorResolver.Resolve(req.AgvNo);
    if (agvFloor is null)
        return Results.BadRequest(new
        {
            error = $"agvNo={req.AgvNo}에 대한 층 매핑이 없습니다. Floors:AgvNoToFloor 설정을 확인하세요."
        });

    // ── PLC 스냅샷(논블로킹) → DepositDecider.Decide ─────────────────────────
    var snap = gateway.Latest;

    // M3: WcsHold.None 기본 + 기준정보 PAUSED만(IOrderRepository 통해 판단하면 되나
    // Decide는 순수함수이므로 여기서 hold를 WcsHold.None으로 고정. FULL=M4)
    var hold     = WcsHold.None;
    var decision = DepositDecider.Decide(snap, agvFloor.Value, hold);

    // ── WriteTgtFloor면 큐 투입 (완료 대기 X — API 3s 한계 분리) ─────────────
    if (decision.WriteTgtFloor)
    {
        // ValueTask는 await 필요하나 "완료 대기 X" → fire-and-forget
        // ConfigureAwait(false) + 예외는 background 로그로
        _ = writeQueue.EnqueueAsync(new PlcWrite.SetTgtFloor(decision.TgtFloorValue))
                       .AsTask()
                       .ContinueWith(t =>
                       {
                           if (t.IsFaulted)
                               log.LogError(t.Exception, "[IF-08] SetTgtFloor 큐 투입 예외");
                       }, TaskScheduler.Default);
    }

    // ── 응답 생성 ────────────────────────────────────────────────────────────
    // allowed=true → reason="READY" API 계층 주입 (Core ToWire(None)=null 무변경)
    // allowed=false → reason=ToWire() 사유 문자열
    var reason = decision.Allowed ? "READY" : decision.Reason.ToWire();

    log.LogInformation(
        "[IF-08] pId={PId} agvNo={AgvNo} agvFloor={Floor} → allowed={Allowed} reason={Reason} WriteTgtFloor={Write}",
        req.PId, req.AgvNo, agvFloor, decision.Allowed, reason, decision.WriteTgtFloor);

    return Results.Ok(new DepositPermissionResponse(decision.Allowed, reason));
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
    HandshakeOrchestrator    handshake,
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
    // RecordDeposit이 check-then-act를 lock으로 원자화 — true=신규 기록, false=중복(멱등).
    // HasDepositRecord 선검사를 별도로 두지 않는다: 선검사 후 RecordDeposit 사이에 다른
    // 스레드가 삽입하면 둘 다 통과해 IF-11을 2회 트리거하는 경쟁이 발생하기 때문.
    var isNewRecord = recorder.RecordDeposit(req.PId, req.Barcode, req.ChuteNo, req.AgvNo, req.Qty);

    if (!isNewRecord)
    {
        log.LogInformation("[IF-10] pId={PId} 중복 보고 — 멱등 OK", req.PId);
        return Results.Ok(new DepositReportResponse("OK"));
    }

    // ── 3D 목적지면 IF-11 트리거 (백그라운드, 즉시 OK 반환) ─────────────────
    var destType = recorder is InMemoryDepositRecorder imr
                   ? imr.GetDestType(req.PId)
                   : null;

    if (destType == DestinationType.Sorter3D)
    {
        // 셀 선택
        var cellNo = cellSelector.SelectCell(req.ChuteNo, req.Barcode);

        if (cellNo.HasValue)
        {
            int selectedCell = cellNo.Value;
            // IF-11 핸드셰이크: 즉시 OK 반환, 핸드셰이크는 백그라운드
            // 완료 추적·결과 기록은 M4
            _ = handshake.ExecuteAsync(selectedCell, CancellationToken.None)
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
