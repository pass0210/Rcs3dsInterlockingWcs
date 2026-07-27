using Microsoft.AspNetCore.Mvc;
using Wcs.Api.B2C;

namespace Wcs.Api.Controllers.B2C;

// ════════════════════════════════════════════════════════════════════════════
// B2cFacilityController — B2C 설비 관리 API(라우트 api/b2c/facility/*). 계약: docs/B2C-FACILITY.md.
// 프론트 전용(RCS 계약 아님). 기존 /api/b2c/test-data·/api/v1·/api/monitor·/api/ops 무충돌.
//
// HTTP 코드:
//   · 관리 액션 비즈니스 실패(중복·미존재·가드 거부) = 200 + {status:"F", counts}.
//   · 파라미터 검증 실패(Range/Required) = 400(경로분기 팩토리 allowlist → B2cManagementResponse.Fail).
//   · 조회(orders) = 200 + 원시 배열(camelCase).
// ════════════════════════════════════════════════════════════════════════════

[ApiController]
[Route("api/b2c/facility")]
public sealed class B2cFacilityController : ControllerBase
{
    private readonly IB2cFacilityService _svc;
    public B2cFacilityController(IB2cFacilityService svc) => _svc = svc;

    // ── POST /destinations — 목적지 생성(소터/슈트) ─────────────────────────────
    [HttpPost("destinations")]
    public async Task<IActionResult> Create([FromBody] B2cCreateDestinationRequest req, CancellationToken ct)
        => Ok(await _svc.CreateDestinationAsync(req, ct));

    // ── POST /destinations/{id}/activate — 활성/비활성 토글(OQ-2 가드) ──────────
    [HttpPost("destinations/{id:long}/activate")]
    public async Task<IActionResult> Activate([FromRoute] long id, [FromBody] B2cActivateRequest req, CancellationToken ct)
        => Ok(await _svc.SetActiveAsync(id, req, ct));

    // ── POST /destinations/{id} — 수정(status/floor/workFullQty) ────────────────
    [HttpPost("destinations/{id:long}")]
    public async Task<IActionResult> Update([FromRoute] long id, [FromBody] B2cUpdateDestinationRequest req, CancellationToken ct)
        => Ok(await _svc.UpdateDestinationAsync(id, req, ct));

    // ── POST /sorters/{id}/cells — 소터 셀 벌크 설정(행×열 · OQ-1) ──────────────
    [HttpPost("sorters/{id:long}/cells")]
    public async Task<IActionResult> ConfigureCells([FromRoute] long id, [FromBody] B2cCellBulkRequest req, CancellationToken ct)
        => Ok(await _svc.ConfigureCellsAsync(id, req, ct));

    // ── GET /orders?assigned=&batchId=&take= — 오더 목록(할당 UI 소스) ──────────
    [HttpGet("orders")]
    public async Task<IActionResult> GetOrders(
        [FromQuery] bool? assigned,
        [FromQuery] long? batchId,
        [FromQuery] int? take,
        CancellationToken ct)
    {
        // ★ S-B2C-UX FIX ITER 2 (Fail-Loud · 침묵 절단 제거): 상한을 GenerateCountMax(=한 배치 생성 상한)로
        //   올린다. 이전 상한 200/500 은 배치가 최대 1000 오더를 가질 수 있어(생성 폼 plannedQty≤1000·같은
        //   배치키에 다중 생성 누적) 배치 디테일·미할당/할당 집계·슈트 단위 해제를 조용히 절단했다.
        //   스케일-세이프: 무한 조회 금지 — GenerateCountMax 로 캡. 프론트가 반환수==상한이면 절단 힌트 표면화.
        int max = B2cConstants.GenerateCountMax;
        int clamped = take is null or <= 0 ? max : Math.Min(take.Value, max);
        return Ok(await _svc.GetOrdersAsync(assigned, batchId, clamped, ct));
    }

    // ── GET /batch-items?batchId=&take= — 배치 상세 per-item(order_item 단위) ─────
    //   Fix 1(S-B2C-BARCODE-MULTI-FIX): 데이터 생성 페이지 하단 "배치 상세" 그리드 전용 — 바코드(order_item)당 1행.
    //   take 상한·기본 = GenerateCountMax(orders 엔드포인트와 동형 — 침묵 절단 방지, 프론트가 절단 힌트 표면화).
    [HttpGet("batch-items")]
    public async Task<IActionResult> GetBatchItems([FromQuery] long batchId, [FromQuery] int? take, CancellationToken ct)
    {
        int max     = B2cConstants.GenerateCountMax;
        int clamped = take is null or <= 0 ? max : Math.Min(take.Value, max);
        return Ok(await _svc.GetBatchItemsAsync(batchId, clamped, ct));
    }

    // ── POST /orders/assign — 오더→목적지(+셀) 할당/재배정(OQ-3) ────────────────
    [HttpPost("orders/assign")]
    public async Task<IActionResult> Assign([FromBody] B2cAssignOrderRequest req, CancellationToken ct)
        => Ok(await _svc.AssignOrderAsync(req, ct));

    // ── POST /orders/unassign — 오더 목적지 할당 해제(OQ-3) ─────────────────────
    [HttpPost("orders/unassign")]
    public async Task<IActionResult> Unassign([FromBody] B2cUnassignOrderRequest req, CancellationToken ct)
        => Ok(await _svc.UnassignOrderAsync(req, ct));
}
