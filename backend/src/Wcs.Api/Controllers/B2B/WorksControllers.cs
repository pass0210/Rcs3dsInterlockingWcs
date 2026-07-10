using Microsoft.AspNetCore.Mvc;
using Wcs.Api.B2B;

namespace Wcs.Api.Controllers.B2B;

// ════════════════════════════════════════════════════════════════════════════
// B2B RCS 5개 API 컨트롤러 — 라우트 api/v1/works/*.
// 와이어 계약: docs/B2B-SCHEMA.md §3 · api-spec-ko.html. 실패 message: §4(FailMessages).
//
// HTTP 코드: 비즈니스 실패 = 200 + status "F" / 검증 실패 = 400 / 예외 = 500.
//   · DataAnnotations ModelState 400 = 경로분기 InvalidModelStateResponseFactory(ApiResponse.Fail).
//   · NormalizeBizDay 의 ArgumentException(비존재 날짜 #17) = 아래 국소 try/catch → 400(전역 미들웨어 미도입).
// 기존 RcsController(api/v1/destination-query 등) 무접촉 — 별도 라우트·별도 응답 형식.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>1. 미작업 조회 (부수효과: receive_time 마킹). 응답 = 최상위 배열(0건이면 []).</summary>
[ApiController]
[Route("api/v1/works")]
public sealed class UnprocessedController : ControllerBase
{
    private readonly IWorkService _work;
    public UnprocessedController(IWorkService work) => _work = work;

    [HttpGet("unprocessed")]
    public async Task<IActionResult> Get([FromQuery] string? bizDay, CancellationToken ct)
    {
        // bizDay 쿼리 필수(수동 검증) → 없으면 400(#16).
        if (string.IsNullOrWhiteSpace(bizDay))
            return BadRequest(B2BApiResponse.Fail(FailMessages.BizDayParameterRequired));

        try
        {
            var groups = await _work.GetUnprocessedAsync(bizDay, ct);
            return Ok(groups);   // 최상위 배열 — 0건이면 [] (F 아님).
        }
        catch (ArgumentException ex)
        {
            return BadRequest(B2BApiResponse.Fail(ex.Message));   // #17 Invalid date
        }
    }
}

/// <summary>2. 투입 (INPUT).</summary>
[ApiController]
[Route("api/v1/works")]
public sealed class InputController : ControllerBase
{
    private readonly IWorkService _work;
    public InputController(IWorkService work) => _work = work;

    [HttpPost("input")]
    public async Task<IActionResult> Post([FromBody] InputRequest req, CancellationToken ct)
    {
        try
        {
            var res = await _work.ProcessInputAsync(req, ct);
            return Ok(res);   // 200 + S/F (비즈니스 실패도 200)
        }
        catch (ArgumentException ex)
        {
            return BadRequest(B2BApiResponse.Fail(ex.Message));   // #17
        }
    }
}

/// <summary>3. 분류 (SORT).</summary>
[ApiController]
[Route("api/v1/works")]
public sealed class ClassificationController : ControllerBase
{
    private readonly IWorkService _work;
    public ClassificationController(IWorkService work) => _work = work;

    [HttpPost("classification")]
    public async Task<IActionResult> Post([FromBody] ClassificationRequest req, CancellationToken ct)
    {
        try
        {
            var res = await _work.ProcessClassificationAsync(req, ct);
            return Ok(res);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(B2BApiResponse.Fail(ex.Message));   // #17
        }
    }
}

/// <summary>4. 전체 작업 결과 — 요청 본문은 최상위 JSON 배열.</summary>
[ApiController]
[Route("api/v1/works")]
public sealed class ResultController : ControllerBase
{
    private readonly IWorkService _work;
    public ResultController(IWorkService work) => _work = work;

    [HttpPost("results")]
    public async Task<IActionResult> Post([FromBody] List<ResultRequestGroup>? groups, CancellationToken ct)
    {
        try
        {
            var res = await _work.ProcessResultsAsync(groups, ct);
            return Ok(res);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(B2BApiResponse.Fail(ex.Message));   // #17
        }
    }
}

/// <summary>5. 박스 마감.</summary>
[ApiController]
[Route("api/v1/works")]
public sealed class BoxController : ControllerBase
{
    private readonly IBoxService _box;
    public BoxController(IBoxService box) => _box = box;

    [HttpPost("box")]
    public async Task<IActionResult> Post([FromBody] BoxRequest req, CancellationToken ct)
    {
        try
        {
            var res = await _box.ProcessBoxAsync(req, ct);
            return Ok(res);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(B2BApiResponse.Fail(ex.Message));   // #17
        }
    }
}
