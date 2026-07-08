using System.ComponentModel.DataAnnotations;

namespace Wcs.Api.B2B;

// ════════════════════════════════════════════════════════════════════════════
// S-B2B-2a: test-data 관리 API DTO — docs/B2B-DATAGEN.md §1.1(GenerateRequest)·§2.
//
// · 검증 실패(HTTP 400) = DataAnnotations ErrorMessage(ModelState) → 경로분기 팩토리가
//   /api/test-data 접두를 allowlist 에 추가(additive) 해 B2BApiResponse.Fail(firstError) 형식으로 방출.
// · 문자열 필드 [StringLength] 전수 — SQL Server 과길이 500 차단(B2B-1 교훈).
// · 조회 응답(summary/detail)은 원시 배열(camelCase, System.Text.Json Web 기본).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>수동 생성 요청(라운드로빈) — §1.1 원본 그대로.</summary>
public sealed class GenerateRequest
{
    // BizDay — 형식 검증은 works 계약과 동일(#10 정본 재사용).
    [Required]
    [RegularExpression(ValidationRules.BizDayRegex, ErrorMessage = ValidationRules.BizDayError)]
    public string BizDay { get; set; } = string.Empty;

    [Required]
    [StringLength(10, MinimumLength = 1)]   // biz*.batch nvarchar(10)
    public string Batch { get; set; } = string.Empty;

    // 슈트 범위 문자열(예 "1-5,8") — 숫자·콤마·하이픈·공백만. 파싱 결과 0개면 서비스가 F.
    [Required]
    [StringLength(200)]
    [RegularExpression(TestDataValidationRules.ChuteNosRegex,
        ErrorMessage = TestDataValidationRules.ChuteNosError)]
    public string ChuteNos { get; set; } = string.Empty;

    // 생성 개수 상한 10000(§1.1 — RCS qty 9999와 별개).
    [Range(1, AppConstants.BarcodeCountMax,
        ErrorMessage = TestDataValidationRules.BarcodeCountError)]
    public int BarcodeCount { get; set; }
}

/// <summary>test-data 전용 검증 정규식·ErrorMessage(§1.1).</summary>
internal static class TestDataValidationRules
{
    // ChuteNos — 숫자·공백·콤마·하이픈만.
    public const string ChuteNosRegex = @"^[\d\s,\-]+$";
    public const string ChuteNosError = "ChuteNos may only contain digits, commas, hyphens, and spaces.";

    // BarcodeCount — 1~10000(Range 기본형식 대신 정본 메시지 명시).
    public const string BarcodeCountError = "BarcodeCount must be between 1 and 10000.";
}

// ── 조회 응답(원시 배열) ─────────────────────────────────────────────────────

/// <summary>summary 행 — 배치 단위 건수·수신시각(§2.3). ReceiveTime = MAX(receive_time).</summary>
public sealed record TestDataSummaryRow(
    string    BizDay,
    string    Batch,
    int       Count,
    DateTime? ReceiveTime);

/// <summary>detail 행 — 바코드 단위 + INPUT/SORT 로그 매핑(§2.4).</summary>
public sealed record TestDataDetailRow(
    long      Id,
    string    BizDay,
    string    Batch,
    string    Barcode,
    string?   Barcode2,
    string    ChuteNo,
    DateTime? ReceiveTime,
    DateTime  CreatedAt,
    string?   InputStatus,
    DateTime? InTime,
    string?   SortStatus,
    DateTime? SortTime);
