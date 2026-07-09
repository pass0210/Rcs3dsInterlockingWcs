using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Wcs.Data;

namespace Wcs.Api.Controllers;

// ════════════════════════════════════════════════════════════════════════════
// OpsController — B2C 운영자 제어 표면 (/api/ops/*, S-F3a 신설).
//
// RcsController(/api/v1, RCS 계약)·MonitoringController(/api/monitor, 읽기)와 완전 분리된 신규 라우트.
// 인증 없음(FRONTEND.md §4.5 LOCK, 사내망 신뢰) — 단 모든 조작 body에 작업자 이름(operatorName, 자유 입력)
// 필수. 감사 귀속: 정규 감사 = destination_event.operator_id(clear/pause/resume) + 운영자 발원
// operation_log STATE 1행(operatorName detail). 워드 쓰기 자동 감사 = 컨슈머 OnWrite → PLC_WRITE(무료).
//
// ★ 안전 경계(계약 SAFETY BOUNDARY):
//   · 모든 PLC 쓰기(O4~O6)는 컨트롤러가 Modbus를 직접 만지지 않고 소터별 단일 쓰기 큐로만 enqueue한다
//     (절대규칙 #1). SetTgtFloor는 컨슈머의 TgtFloor==0 재확인 가드를 그대로 탄다(#2) — 컨트롤러가
//     그 가드를 우회/삭제하지 않는다. WCS는 TgtFloor를 클리어하지 않는다(#3) — floor>=1만 수락.
//   · 허용 워드 쓰기는 SetTgtFloor·ClearR·CellAssign 3종뿐(Q2 LOCK) — 임의 레지스터/D4 비트 편집 없음.
//   · clear/pause/resume은 PLC 쓰기가 아니라 DB + 인메모리 서비스 경유(소터 PAUSE = 순수 WCS 게이트).
//
// 라우팅:
//   O1 clear   → CHUTE 대상(비-CHUTE·미존재 → 404).
//   O2/O3 pause/resume → CHUTE·SORTER_3D 공용(미존재 → 404, 동시성 충돌 → 409).
//   O4~O6 워드 쓰기 → ISorterGatewayRegistry.GetBundle(destId)(null=미등록/비-SORTER_3D → 404).
// ════════════════════════════════════════════════════════════════════════════

[ApiController]
[Route("api/ops")]
public sealed class OpsController : ControllerBase
{
    private readonly ILogger<OpsController> _log;
    private readonly IOperationLogger       _opLog;

    // operatorName 최소 검증(감사 귀속 보장 — 공백/누락 시 400). detail JSON 폭주 방지용 상한.
    private const int OperatorNameMaxLength = 100;   // destination_event.OperatorId = nvarchar(100)

    public OpsController(ILogger<OpsController> log, IOperationLogger opLog)
    {
        _log   = log;
        _opLog = opLog;
    }

    // ── O1 슈트 비움 (A-8 해소: OnCleared production 호출자 신설) ─────────────────
    // POST /api/ops/chutes/{destId}/clear  {operatorName}
    [HttpPost("chutes/{destId:long}/clear")]
    public async Task<IActionResult> ClearChute(
        [FromRoute] long                     destId,
        [FromBody]  OpsOperatorRequest        req,
        [FromServices] WcsDbContext           db,
        [FromServices] IChuteCapacityService  capacity)
    {
        if (!TryValidateOperator(req?.OperatorName, out var op, out var bad))
            return bad!;

        // CHUTE 대상만 — 미존재/비-CHUTE → 404.
        var dest = db.Destinations.FirstOrDefault(d => d.Id == destId);
        if (dest is null || dest.DestType != DestType.CHUTE)
            return NotFound(new { error = $"CHUTE destination(id={destId})을 찾을 수 없습니다." });

        // A-8: OnCleared(operatorId) — last_cleared_at 갱신 + 인메모리 리셋 + destination_event(CLEARED, op).
        await capacity.OnCleared(destId, op);

        LogOpsAction("OPS_CLEAR", destId, sorterChuteNo: null, op,
            detail: $"{{\"op\":\"{Esc(op)}\",\"chuteNo\":{dest.ChuteNo}}}");

        _log.LogInformation("[Ops] chute clear destId={DestId} op={Op}", destId, op);
        return Ok(new { status = "cleared", destId, operatorName = op });
    }

    // ── O2 목적지 정지 (런타임 PAUSED 전이 신규) ─────────────────────────────────
    // POST /api/ops/destinations/{destId}/pause  {operatorName}
    [HttpPost("destinations/{destId:long}/pause")]
    public Task<IActionResult> Pause(
        [FromRoute] long                        destId,
        [FromBody]  OpsOperatorRequest           req,
        [FromServices] IDestinationControlService control)
        => TransitionAsync(destId, req, control, pause: true);

    // ── O3 목적지 재개 (런타임 RESUMED 전이 신규) ────────────────────────────────
    // POST /api/ops/destinations/{destId}/resume  {operatorName}
    [HttpPost("destinations/{destId:long}/resume")]
    public Task<IActionResult> Resume(
        [FromRoute] long                        destId,
        [FromBody]  OpsOperatorRequest           req,
        [FromServices] IDestinationControlService control)
        => TransitionAsync(destId, req, control, pause: false);

    private async Task<IActionResult> TransitionAsync(
        long destId, OpsOperatorRequest? req, IDestinationControlService control, bool pause)
    {
        if (!TryValidateOperator(req?.OperatorName, out var op, out var bad))
            return bad!;

        var result = pause
            ? await control.PauseAsync(destId, op, HttpContext.RequestAborted)
            : await control.ResumeAsync(destId, op, HttpContext.RequestAborted);

        var action = pause ? "OPS_PAUSE" : "OPS_RESUME";

        switch (result.Outcome)
        {
            case DestControlOutcome.NotFound:
                return NotFound(new { error = $"destination(id={destId})을 찾을 수 없습니다." });

            case DestControlOutcome.Conflict:
                // 동시 전이 충돌 — 정직히 409(거짓 성공 응답 금지). 감사에도 WARN 1행.
                LogOpsAction(action, destId, sorterChuteNo: null, op,
                    detail: $"{{\"op\":\"{Esc(op)}\",\"outcome\":\"Conflict\"}}",
                    level: OperationLogLevel.WARN);
                return Conflict(new { error = "동시 상태 전이 충돌 — 재시도하세요.", destId });

            default:  // Transitioned or AlreadyInState — 둘 다 200(멱등).
                LogOpsAction(action, destId, sorterChuteNo: null, op,
                    detail: $"{{\"op\":\"{Esc(op)}\",\"outcome\":\"{result.Outcome}\",\"type\":\"{result.DestType}\"}}");
                _log.LogInformation("[Ops] {Action} destId={DestId} op={Op} outcome={Outcome}",
                    action, destId, op, result.Outcome);
                return Ok(new
                {
                    status       = pause ? "paused" : "resumed",
                    destId,
                    outcome      = result.Outcome.ToString(),
                    operatorName = op,
                });
        }
    }

    // ── O4 소터 TgtFloor 쓰기 (단일 큐 enqueue — 컨슈머 TgtFloor==0 가드 그대로) ────
    // POST /api/ops/sorters/{destId}/tgtfloor  {floor, operatorName}
    [HttpPost("sorters/{destId:long}/tgtfloor")]
    public async Task<IActionResult> SetTgtFloor(
        [FromRoute] long                     destId,
        [FromBody]  OpsTgtFloorRequest        req,
        [FromServices] ISorterGatewayRegistry registry,
        [FromServices] IOptions<WcsOptions>   wcsOptions)
    {
        if (!TryValidateOperator(req?.OperatorName, out var op, out var bad))
            return bad!;
        // #3: WCS는 TgtFloor를 클리어하지 않는다 — floor==0 수동 리셋은 F3a 미노출(floor>=1만 수락).
        if (req!.Floor < 1)
            return BadRequest(new { error = "floor는 1 이상이어야 합니다(수동 클리어 floor=0은 F3a 미노출)." });
        // I-1: 상한 검증 — 컨슈머 (short) 캐스트 조용한 wrap 방지(Fail Loud). 설정값·하드 타입 상한 이중.
        int maxFloor = wcsOptions.Value.OpsLimits.EffectiveMaxTgtFloor;
        if (req.Floor > maxFloor)
            return BadRequest(new { error = $"floor는 {maxFloor} 이하여야 합니다(PLC 레지스터 상한)." });

        var bundle = registry.GetBundle(destId);
        if (bundle is null)
            return NotFound(new { error = $"SORTER_3D destination(id={destId}) 소터 번들이 없습니다." });

        // 현재 TgtFloor 스냅샷 — 응답에 정직히 반영(핑퐁 차단 가능성을 거짓 성공으로 숨기지 않음, #2).
        var currentTgt = bundle.Latest.TgtFloor;

        // 단일 큐 enqueue(절대규칙 #1). 컨슈머가 쓰기 직전 TgtFloor==0 재확인 후에만 실기입(핑퐁 차단).
        await bundle.EnqueueSetTgtFloorAsync(req.Floor, HttpContext.RequestAborted);

        LogOpsAction("OPS_SET_TGTFLOOR", destId, bundle.ChuteNo, op,
            detail: $"{{\"op\":\"{Esc(op)}\",\"floor\":{req.Floor},\"currentTgtFloor\":{currentTgt}}}");

        _log.LogInformation("[Ops] SetTgtFloor enqueue destId={DestId} floor={Floor} currentTgt={Cur} op={Op}",
            destId, req.Floor, currentTgt, op);

        // 정직한 응답: enqueue 수락됨. currentTgtFloor≠0이면 컨슈머가 핑퐁 차단으로 스킵할 수 있음.
        return Ok(new
        {
            status           = "enqueued",
            destId,
            floor            = req.Floor,
            currentTgtFloor  = currentTgt,
            pingPongGuard    = currentTgt != 0,   // true면 컨슈머가 이 쓰기를 스킵(진행 중)할 수 있음.
            operatorName     = op,
        });
    }

    // ── O5 소터 R 영역 강제 클리어 (진단 — 단일 큐 enqueue) ───────────────────────
    // POST /api/ops/sorters/{destId}/clear-r  {operatorName}
    [HttpPost("sorters/{destId:long}/clear-r")]
    public async Task<IActionResult> ClearR(
        [FromRoute] long                     destId,
        [FromBody]  OpsOperatorRequest        req,
        [FromServices] ISorterGatewayRegistry registry)
    {
        if (!TryValidateOperator(req?.OperatorName, out var op, out var bad))
            return bad!;

        var bundle = registry.GetBundle(destId);
        if (bundle is null)
            return NotFound(new { error = $"SORTER_3D destination(id={destId}) 소터 번들이 없습니다." });

        await bundle.EnqueueClearRAsync(HttpContext.RequestAborted);

        LogOpsAction("OPS_CLEAR_R", destId, bundle.ChuteNo, op,
            detail: $"{{\"op\":\"{Esc(op)}\"}}", level: OperationLogLevel.WARN);

        _log.LogInformation("[Ops] ClearR enqueue destId={DestId} op={Op}", destId, op);
        return Ok(new { status = "enqueued", destId, operatorName = op });
    }

    // ── O6 소터 셀 지정 (고위험 진단 — 단일 큐 enqueue) ──────────────────────────
    // POST /api/ops/sorters/{destId}/cell-assign  {cellNo, seq, operatorName}
    [HttpPost("sorters/{destId:long}/cell-assign")]
    public async Task<IActionResult> CellAssign(
        [FromRoute] long                     destId,
        [FromBody]  OpsCellAssignRequest      req,
        [FromServices] ISorterGatewayRegistry registry,
        [FromServices] IOptions<WcsOptions>   wcsOptions)
    {
        if (!TryValidateOperator(req?.OperatorName, out var op, out var bad))
            return bad!;
        if (req!.CellNo < 1)
            return BadRequest(new { error = "cellNo는 1 이상이어야 합니다." });
        if (req.Seq < 1)
            return BadRequest(new { error = "seq는 1 이상이어야 합니다." });
        // I-1: 상한 검증 — 컨슈머 (short) 캐스트 조용한 wrap 방지(Fail Loud). 설정값·하드 타입 상한 이중.
        var limits = wcsOptions.Value.OpsLimits;
        if (req.CellNo > limits.EffectiveMaxCellNo)
            return BadRequest(new { error = $"cellNo는 {limits.EffectiveMaxCellNo} 이하여야 합니다(PLC 레지스터 상한)." });
        if (req.Seq > limits.EffectiveMaxCellSeq)
            return BadRequest(new { error = $"seq는 {limits.EffectiveMaxCellSeq} 이하여야 합니다(PLC 레지스터 상한)." });

        var bundle = registry.GetBundle(destId);
        if (bundle is null)
            return NotFound(new { error = $"SORTER_3D destination(id={destId}) 소터 번들이 없습니다." });

        await bundle.EnqueueCellAssignAsync(req.CellNo, req.Seq, HttpContext.RequestAborted);

        LogOpsAction("OPS_CELL_ASSIGN", destId, bundle.ChuteNo, op,
            detail: $"{{\"op\":\"{Esc(op)}\",\"cellNo\":{req.CellNo},\"seq\":{req.Seq}}}",
            level: OperationLogLevel.WARN);

        _log.LogInformation("[Ops] CellAssign enqueue destId={DestId} cellNo={CellNo} seq={Seq} op={Op}",
            destId, req.CellNo, req.Seq, op);
        return Ok(new { status = "enqueued", destId, cellNo = req.CellNo, seq = req.Seq, operatorName = op });
    }

    // ── 공통: operatorName 검증(누락/공백/과길이 → 400) ──────────────────────────
    private bool TryValidateOperator(string? operatorName, out string op, out IActionResult? bad)
    {
        op = (operatorName ?? string.Empty).Trim();
        if (op.Length == 0)
        {
            bad = BadRequest(new { error = "operatorName은 필수입니다(감사 귀속)." });
            return false;
        }
        if (op.Length > OperatorNameMaxLength)
        {
            bad = BadRequest(new { error = $"operatorName 길이는 {OperatorNameMaxLength}자 이하여야 합니다." });
            return false;
        }
        bad = null;
        return true;
    }

    // ── 공통: 운영자 발원 operation_log STATE 1행(감사 귀속 — 경량 재사용, 마이그레이션 0) ──
    // 정규 감사는 destination_event(operator_id 컬럼); 워드 쓰기 자동 감사는 컨슈머 PLC_WRITE.
    // operation_log엔 operator_id 컬럼이 없어 operatorName은 detail JSON으로 귀속(FRONTEND.md §3.4).
    private void LogOpsAction(string action, long destId, int? sorterChuteNo, string op,
        string detail, OperationLogLevel level = OperationLogLevel.INFO)
    {
        _opLog.Log(OperationLogCategory.STATE, action, level: level,
            sorterChuteNo: sorterChuteNo, destinationId: destId, detail: detail);
    }

    // detail JSON 문자열 안전 삽입(operatorName 자유 입력의 따옴표/역슬래시 이스케이프).
    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}

// ── Ops 요청 DTO (작업자 이름 필수 — 감사 귀속) ─────────────────────────────────

/// <summary>clear/pause/resume/clear-r 공통 — 작업자 이름만.</summary>
public sealed record OpsOperatorRequest(string? OperatorName);

/// <summary>O4 SetTgtFloor — floor(>=1) + 작업자 이름.</summary>
public sealed record OpsTgtFloorRequest(int Floor, string? OperatorName);

/// <summary>O6 CellAssign — cellNo·seq(>=1) + 작업자 이름.</summary>
public sealed record OpsCellAssignRequest(int CellNo, int Seq, string? OperatorName);
