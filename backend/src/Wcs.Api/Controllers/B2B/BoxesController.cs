using Microsoft.AspNetCore.Mvc;
using Wcs.Api.B2B;

namespace Wcs.Api.Controllers.B2B;

// ════════════════════════════════════════════════════════════════════════════
// BoxesController — 프론트 전용 박스 목록 조회(라우트 api/boxes, 읽기 전용).
//   E6 GET /api/boxes?bizDay=&batch= — 박스 헤더 + 내품 원시 배열(§2.3).
//
// HTTP 코드: 조회 성공 = 200(0건이면 []) / bizDay 누락 = 400 + Fail / 비존재 날짜 = 400(#17).
// bizDay 는 DataAnnotations 미적용(원본 §2.3 — POST 대비 비대칭) → 컨트롤러 수동 검증.
// 기존 /api/v1/works(박스 마감 POST)·/api/monitor 무접촉 — 별도 라우트(충돌 0).
// ════════════════════════════════════════════════════════════════════════════

[ApiController]
[Route("api/boxes")]
public sealed class BoxesController : ControllerBase
{
    private readonly IBoxService _box;

    public BoxesController(IBoxService box) => _box = box;

    // ── E6 GET /api/boxes?bizDay=&batch= ─────────────────────────────────────────
    [HttpGet]
    public async Task<IActionResult> Get(
        [FromQuery] string? bizDay, [FromQuery] string? batch, CancellationToken ct)
    {
        // bizDay 필수(§2.3) — 누락 시 400 + Fail.
        if (string.IsNullOrWhiteSpace(bizDay))
            return BadRequest(B2BApiResponse.Fail(FailMessages.BizDayParameterRequired));

        try
        {
            return Ok(await _box.GetBoxesAsync(bizDay, batch, ct));   // 원시 배열(0건이면 [])
        }
        catch (ArgumentException ex)
        {
            return BadRequest(B2BApiResponse.Fail(ex.Message));   // #17 Invalid date
        }
    }
}
