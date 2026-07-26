using Microsoft.AspNetCore.Mvc;
using Wcs.Api.B2C;

namespace Wcs.Api.Controllers.B2C;

// ════════════════════════════════════════════════════════════════════════════
// B2cTestDataController — B2C(3D 소터) 테스트 데이터 관리 API(라우트 api/b2c/test-data/*).
// 계약: docs/B2C-DATAGEN.md. 프론트 전용(RCS 계약 아님).
//
// HTTP 코드:
//   · 관리 액션(generate/reset) 비즈니스 실패 = 200 + status "F" / 파라미터 검증 실패 = 400.
//   · DataAnnotations 400 = 경로분기 팩토리(allowlist 에 /api/b2c/test-data 추가) → B2cManagementResponse.Fail.
//   · workDate ArgumentException(비존재 날짜) = 국소 try/catch → 400.
// 기존 /api/test-data·/api/v1·/api/monitor·/api/ops 무접촉 — 별도 라우트(무충돌·OQ7).
// ════════════════════════════════════════════════════════════════════════════

[ApiController]
[Route("api/b2c/test-data")]
public sealed class B2cTestDataController : ControllerBase
{
    private readonly IB2cTestDataService _svc;
    public B2cTestDataController(IB2cTestDataService svc) => _svc = svc;

    // 업로드 MIME 화이트리스트(.xlsx 전용 — 확정 결정 Q5 · .xls 거부). 대소문자 무시.
    private static readonly HashSet<string> AllowedExcelMimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", // .xlsx
        "application/octet-stream",                                          // 브라우저 미상정 시
    };

    // ── POST /api/b2c/test-data/generate — 멱등 생성(OQ4) ──────────────────────
    [HttpPost("generate")]
    public async Task<IActionResult> Generate([FromBody] B2cGenerateRequest req, CancellationToken ct)
    {
        try
        {
            return Ok(await _svc.GenerateAsync(req, ct));   // 200 + S/F (비즈니스 실패도 200)
        }
        catch (ArgumentException ex)
        {
            return BadRequest(B2cManagementResponse.Fail(ex.Message));   // 비존재 workDate 등
        }
    }

    // ── POST /api/b2c/test-data/upload — 엑셀 업로드(파일 3중 검증 + 행별 파싱·멱등 append) ──
    //   파일 레벨(없음/크기/확장자·MIME) = 400 선행. 구조/행오류·유효행0·팽창초과 = 200 F(+rowErrors).
    //   .xlsx 전용(확정 결정 Q5 — .xls 거부). 상한은 B2cConstants(하드코딩 금지·절대규칙 #7).
    [HttpPost("upload")]
    public async Task<IActionResult> Upload(IFormFile? file, CancellationToken ct)
    {
        // #1 파일 없음/0바이트 → 400.
        if (file is null || file.Length == 0)
            return BadRequest(B2cUploadResponse.Fail(B2cConstants.UploadNoFile));

        // #2 크기 > 상한 → 400.
        if (file.Length > B2cConstants.UploadMaxBytes)
            return BadRequest(B2cUploadResponse.Fail(B2cConstants.UploadFileTooBig));

        // #3 확장자(경로 제거 후) .xlsx 아님 → 400(.xls 거부).
        var ext = Path.GetExtension(Path.GetFileName(file.FileName)).ToLowerInvariant();
        if (ext != ".xlsx")
            return BadRequest(B2cUploadResponse.Fail(B2cConstants.UploadOnlyXlsx));

        // #4 MIME 화이트리스트 불일치 → 400.
        if (!AllowedExcelMimes.Contains(file.ContentType ?? string.Empty))
            return BadRequest(B2cUploadResponse.Fail(B2cConstants.UploadInvalidFormat));

        // 파싱/행검증 — 성공 200 S / 구조·행오류 200 F(+rowErrors).
        await using var stream = file.OpenReadStream();
        return Ok(await _svc.UploadExcelAsync(stream, ct));
    }

    // ── GET /api/b2c/test-data/batches?take= — 생성 결과 view(최근 배치·미할당 오더 수) ──
    [HttpGet("batches")]
    public async Task<IActionResult> Batches([FromQuery] int? take, CancellationToken ct)
    {
        // take clamp(1..100, 기본 20) — 하드코딩 상한 대신 국소 방어(관리 화면 소량 조회).
        int clamped = take is null or <= 0 ? 20 : Math.Min(take.Value, 100);
        return Ok(await _svc.GetBatchesAsync(clamped, ct));
    }

    // ── GET /api/b2c/test-data/summary?sorterChuteNo= — 소터별 요약 집계 ─────────
    [HttpGet("summary")]
    public async Task<IActionResult> Summary([FromQuery] int? sorterChuteNo, CancellationToken ct)
        => Ok(await _svc.GetSummaryAsync(sorterChuteNo, ct));   // 원시 배열(0건이면 [])

    // ── GET /api/b2c/test-data/detail?sorterChuteNo= — 셀 상세 ──────────────────
    [HttpGet("detail")]
    public async Task<IActionResult> Detail([FromQuery] int? sorterChuteNo, CancellationToken ct)
    {
        if (sorterChuteNo is null or <= 0)
            return BadRequest(B2cManagementResponse.Fail("sorterChuteNo query parameter is required."));
        return Ok(await _svc.GetDetailAsync(sorterChuteNo.Value, ct));   // 원시 배열
    }

    // ── POST /api/b2c/test-data/reset — 재테스트 초기화(OQ1·OQ2·OQ3) ────────────
    [HttpPost("reset")]
    public async Task<IActionResult> Reset([FromBody] B2cResetRequest req, CancellationToken ct)
        => Ok(await _svc.ResetAsync(req, ct));   // 200 + S/F (거부/실패도 200 F)
}
