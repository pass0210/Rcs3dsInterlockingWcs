using Microsoft.AspNetCore.Mvc;
using Wcs.Api.B2B;

namespace Wcs.Api.Controllers.B2B;

// ════════════════════════════════════════════════════════════════════════════
// TestDataController — 프론트 전용 test-data 관리 API(라우트 api/test-data/*).
// 계약: docs/B2B-DATAGEN.md §1. 알고리즘: §2·§3(ITestDataService). 실패 message: §1.2(FailMessages).
//
// HTTP 코드: 비즈니스 실패 = 200 + status "F" / 검증 실패 = 400.
//   · GenerateRequest DataAnnotations 400 = 경로분기 팩토리(allowlist 에 /api/test-data 추가) → B2BApiResponse.Fail.
//   · upload 3중 검증(파일/크기/확장자·MIME)은 컨트롤러가 400 + B2BApiResponse.Fail 로 선행.
//   · NormalizeBizDay ArgumentException(비존재 날짜 #17) = 국소 try/catch → 400.
// 기존 /api/v1/works·/api/monitor·/api/ops 무접촉 — 별도 라우트·별도 응답(무충돌, §6).
// ════════════════════════════════════════════════════════════════════════════

[ApiController]
[Route("api/test-data")]
public sealed class TestDataController : ControllerBase
{
    private readonly ITestDataService _svc;
    public TestDataController(ITestDataService svc) => _svc = svc;

    // 업로드 MIME 화이트리스트(§1.2 #4) — 대소문자 무시 비교.
    private static readonly HashSet<string> AllowedExcelMimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", // .xlsx
        "application/vnd.ms-excel",                                          // .xls
        "application/octet-stream",                                          // 브라우저가 미상정 시
    };

    // ── POST /api/test-data/generate — 수동 라운드로빈 생성(§2.1) ────────────────
    [HttpPost("generate")]
    public async Task<IActionResult> Generate([FromBody] GenerateRequest req, CancellationToken ct)
    {
        try
        {
            return Ok(await _svc.GenerateAsync(req, ct));   // 200 + S/F (비즈니스 실패도 200)
        }
        catch (ArgumentException ex)
        {
            return BadRequest(B2BApiResponse.Fail(ex.Message));   // #17 Invalid date
        }
    }

    // ── GET /api/test-data/summary?bizDay= — 배치 요약(§2.3) ─────────────────────
    [HttpGet("summary")]
    public async Task<IActionResult> Summary([FromQuery] string? bizDay, CancellationToken ct)
    {
        try
        {
            return Ok(await _svc.GetSummaryAsync(bizDay, ct));   // 원시 배열(0건이면 [])
        }
        catch (ArgumentException ex)
        {
            return BadRequest(B2BApiResponse.Fail(ex.Message));   // #17
        }
    }

    // ── GET /api/test-data/detail?bizDay=&batch=&archived= — 상세(§2.4·§3.4) ─────
    [HttpGet("detail")]
    public async Task<IActionResult> Detail(
        [FromQuery] string? bizDay, [FromQuery] string? batch,
        [FromQuery] string? archived, CancellationToken ct)
    {
        // bizDay·batch 둘 다 필수(§1 표).
        if (string.IsNullOrWhiteSpace(bizDay) || string.IsNullOrWhiteSpace(batch))
            return BadRequest(B2BApiResponse.Fail("bizDay and batch parameters are required."));

        try
        {
            var filter = TestDataService.ParseArchiveFilter(archived);
            return Ok(await _svc.GetDetailAsync(bizDay, batch, filter, ct));   // 원시 배열
        }
        catch (ArgumentException ex)
        {
            return BadRequest(B2BApiResponse.Fail(ex.Message));   // #17
        }
    }

    // ── E5 GET /api/test-data/comparison?bizDay=&archived= — 투입/분류/결과 3-way 비교 ──────
    // 원본이 comparison 을 TestDataController 에 둔 것과 정합(additive). 로직은 ILogService(§3.2.7).
    // ILogService 는 [FromServices] 메서드 주입 — 기존 생성자(ITestDataService 만) 무접촉.
    [HttpGet("comparison")]
    public async Task<IActionResult> Comparison(
        [FromQuery] string? bizDay, [FromQuery] string? archived,
        [FromServices] ILogService logSvc, CancellationToken ct)
    {
        try
        {
            var filter = TestDataService.ParseArchiveFilter(archived);
            return Ok(await logSvc.GetResultComparisonAsync(bizDay, filter, ct));   // 원시 배열(0건이면 [])
        }
        catch (ArgumentException ex)
        {
            return BadRequest(B2BApiResponse.Fail(ex.Message));   // #17 Invalid date
        }
    }

    // ── POST /api/test-data/reset — 수신시간 초기화 + 연관 아카이브(§3.3) ──────────
    [HttpPost("reset")]
    public async Task<IActionResult> Reset([FromBody] List<long>? ids, CancellationToken ct)
        => Ok(await _svc.ResetReceiveTimeAsync(ids, ct));

    // ── DELETE /api/test-data — 선택 삭제(하드) + 연관 아카이브(소프트, §3.3) ───────
    [HttpDelete]
    public async Task<IActionResult> Delete([FromBody] List<long>? ids, CancellationToken ct)
        => Ok(await _svc.DeleteAsync(ids, ct));

    // ── POST /api/test-data/upload — 엑셀 업로드(§1.2 3중 검증 + §2.2 파싱) ────────
    [HttpPost("upload")]
    public async Task<IActionResult> Upload(IFormFile? file, CancellationToken ct)
    {
        // #1 파일 없음/0바이트 → 400.
        if (file is null || file.Length == 0)
            return BadRequest(B2BApiResponse.Fail(FailMessages.PleaseSelectFile));

        // #2 크기 > 10MB → 400. (RequestSizeLimit(정확히 10MB) 는 멀티파트 오버헤드로 유효 10MB 파일도
        //    413 로 선점해 이 정밀 400 message 를 가로막으므로 도입하지 않고 수동 검사를 정본으로 둔다.
        //    Kestrel 기본 요청 본문 한도(~28MB)가 하드 백스톱.)
        if (file.Length > AppConstants.UploadMaxBytes)
            return BadRequest(B2BApiResponse.Fail(FailMessages.FileSizeExceeded));

        // #3 확장자(경로 제거 후) .xlsx/.xls 아님 → 400.
        var ext = Path.GetExtension(Path.GetFileName(file.FileName)).ToLowerInvariant();
        if (ext != ".xlsx" && ext != ".xls")
            return BadRequest(B2BApiResponse.Fail(FailMessages.OnlyExcelAllowed));

        // #4 MIME 화이트리스트 불일치 → 400.
        if (!AllowedExcelMimes.Contains(file.ContentType ?? string.Empty))
            return BadRequest(B2BApiResponse.Fail(FailMessages.InvalidFileFormat));

        // 파싱(§2.2) — 성공 200 S "{n}건 업로드 완료", 파싱 실패 200 F.
        await using var stream = file.OpenReadStream();
        return Ok(await _svc.UploadExcelAsync(stream, ct));
    }
}
