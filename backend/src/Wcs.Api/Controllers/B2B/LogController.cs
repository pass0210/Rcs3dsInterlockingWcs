using Microsoft.AspNetCore.Mvc;
using Wcs.Api.B2B;

namespace Wcs.Api.Controllers.B2B;

// ════════════════════════════════════════════════════════════════════════════
// LogController — 프론트 전용 조회 API(라우트 api/logs/*, 읽기 전용).
//   E1 GET input      — 투입(INPUT) 로그(원시 배열)
//   E2 GET sort       — 분류(SORT) 로그(원시 배열)
//   E3 GET api-calls  — RCS API 호출 이력(원시 배열, 최대 500건)
//   E4 GET export     — 투입+분류 통합 Excel(.xlsx 바이너리 + Content-Disposition)
//
// HTTP 코드: 조회 성공 = 200(0건이면 []) / bizDay·date 비존재 날짜 = 400(#17 국소 catch) /
//            export bizDay 누락 = 400 + Fail / export 생성 오류 = 400 + Fail.
// 아카이브 필터: archived=active|all|archivedOnly(미인식→active) — TestDataService.ParseArchiveFilter 공용.
// 기존 /api/v1/works·/api/test-data·/api/monitor 무접촉 — 별도 라우트(라우트 충돌 0, 계약 §108).
// ════════════════════════════════════════════════════════════════════════════

[ApiController]
[Route("api/logs")]
public sealed class LogController : ControllerBase
{
    private readonly ILogService       _log;
    private readonly ILogExportService _export;
    private readonly ILogger<LogController> _logger;

    public LogController(ILogService log, ILogExportService export, ILogger<LogController> logger)
    {
        _log    = log;
        _export = export;
        _logger = logger;
    }

    // ── E1 GET /api/logs/input?bizDay=&archived= ─────────────────────────────────
    [HttpGet("input")]
    public async Task<IActionResult> Input(
        [FromQuery] string? bizDay, [FromQuery] string? archived, CancellationToken ct)
    {
        try
        {
            var filter = TestDataService.ParseArchiveFilter(archived);
            return Ok(await _log.GetInputLogsAsync(bizDay, filter, ct));   // 원시 배열(0건이면 [])
        }
        catch (ArgumentException ex)
        {
            return BadRequest(B2BApiResponse.Fail(ex.Message));   // #17 Invalid date
        }
    }

    // ── E2 GET /api/logs/sort?bizDay=&archived= ──────────────────────────────────
    [HttpGet("sort")]
    public async Task<IActionResult> Sort(
        [FromQuery] string? bizDay, [FromQuery] string? archived, CancellationToken ct)
    {
        try
        {
            var filter = TestDataService.ParseArchiveFilter(archived);
            return Ok(await _log.GetSortLogsAsync(bizDay, filter, ct));
        }
        catch (ArgumentException ex)
        {
            return BadRequest(B2BApiResponse.Fail(ex.Message));   // #17
        }
    }

    // ── E3 GET /api/logs/api-calls?date= (최대 500건) ────────────────────────────
    [HttpGet("api-calls")]
    public async Task<IActionResult> ApiCalls([FromQuery] string? date, CancellationToken ct)
    {
        try
        {
            return Ok(await _log.GetApiCallLogsAsync(date, ct));   // 원시 배열(0건이면 [])
        }
        catch (ArgumentException ex)
        {
            return BadRequest(B2BApiResponse.Fail(ex.Message));   // #17
        }
    }

    // ── E4 GET /api/logs/export?bizDay=&batch= (.xlsx 다운로드) ───────────────────
    [HttpGet("export")]
    public async Task<IActionResult> Export(
        [FromQuery] string? bizDay, [FromQuery] string? batch, CancellationToken ct)
    {
        // bizDay 필수(§1 표) — 누락 시 400 + Fail.
        if (string.IsNullOrWhiteSpace(bizDay))
            return BadRequest(B2BApiResponse.Fail(FailMessages.BizDayParameterRequired));

        try
        {
            var (content, fileName) = await _export.ExportAsync(bizDay, batch, ct);
            return File(content,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                fileName);   // Content-Disposition: attachment; filename=...
        }
        catch (ArgumentException ex)
        {
            return BadRequest(B2BApiResponse.Fail(ex.Message));   // #17 Invalid date
        }
        catch (OperationCanceledException)
        {
            // ★ 코드리뷰 #4: 클라이언트 취소는 400 이 아님 — 상위(GlobalException/프레임워크)에 위임(재던짐).
            throw;
        }
        catch (Exception ex)
        {
            // ★ 코드리뷰 #4: export 생성 오류 → 400 + Fail. 상세는 서버 로그에만, 클라이언트엔 원문 미노출.
            _logger.LogError(ex, "[B2B export] Excel 생성 실패 bizDay={BizDay} batch={Batch}", bizDay, batch);
            return BadRequest(B2BApiResponse.Fail("Export failed."));
        }
    }
}
