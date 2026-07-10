using System.ComponentModel.DataAnnotations;

namespace Wcs.Api.B2B;

// ════════════════════════════════════════════════════════════════════════════
// B2B 요청/응답 DTO — docs/B2B-SCHEMA.md §3(와이어 계약)·§4(검증 message verbatim).
//
// 검증 실패(HTTP 400) message 는 DataAnnotations ErrorMessage 에 인라인(정본 §4 #10~15).
//   #10 BizDay 형식 / #11 Barcode 문자 / #12 Status / #13 Qty Range(기본형식) / #14 Items / #15 Required(기본형식)
// ModelState → 경로분기 InvalidModelStateResponseFactory 가 ApiResponse.Fail(firstError)로 400.
//
// pId·inductionNo = RCS 자체생성 정수 — 검증 없음(그대로 저장). Qty 기본 1.
// JSON(System.Text.Json Web 기본 camelCase): BizDay→bizDay, PId→pId, InductionNo→inductionNo, ...
// ════════════════════════════════════════════════════════════════════════════

/// <summary>공통 검증 정규식·ErrorMessage 상수(정본 §4).</summary>
internal static class ValidationRules
{
    // #10 — YYYYMMDD | YYYY-MM-DD
    public const string BizDayRegex = @"^(\d{8}|\d{4}-\d{2}-\d{2})$";
    public const string BizDayError = "BizDay must be in YYYYMMDD or YYYY-MM-DD format.";

    // #11 — 영문자·숫자·하이픈·언더스코어. 선택필드(빈 허용)는 '*', 필수필드는 '+'.
    public const string BarcodeOptionalRegex = @"^[A-Za-z0-9\-_]*$";
    public const string BarcodeRequiredRegex = @"^[A-Za-z0-9\-_]+$";
    public const string BarcodeError = "Barcode may only contain letters, digits, hyphen, and underscore.";

    // #12 — OK | NG
    public const string StatusRegex = "^(OK|NG)$";
    public const string StatusError = "Status must be 'OK' or 'NG'.";

    // #14 — results/box items 빈 배열
    public const string ItemsMinError = "Items must contain at least one entry.";

    // qty 범위(#13은 Range 기본형식 "The field Qty must be between 1 and 9999."이므로 ErrorMessage 미지정).
    public const int QtyMin = 1;
    public const int QtyMax = AppConstants.QtyMaxPerRequest;
}

// ── 2. 투입 (POST /api/v1/works/input) — INPUT 로그 ──────────────────────────
public sealed class InputRequest
{
    [Required]
    [RegularExpression(ValidationRules.BizDayRegex, ErrorMessage = ValidationRules.BizDayError)]
    public string BizDay { get; set; } = string.Empty;

    [Required]
    [StringLength(10, MinimumLength = 1)]   // batch 1~10(계약) = biz*.batch nvarchar(10)
    public string Batch { get; set; } = string.Empty;

    // RCS 자체생성 정수 — 검증 없음(그대로 저장)
    public int InductionNo { get; set; }

    [Required]
    [StringLength(20)]   // 원본 규칙 ≤20 (INPUT 은 equipment_no=inductionNo — chuteNo 미영속, 상한만)
    public string ChuteNo { get; set; } = string.Empty;

    // RCS 자체생성 정수 — 검증 없음(그대로 저장)
    public int PId { get; set; }

    // barcode 선택(N) — 빈 허용
    [StringLength(50)]   // test_data/test_log.barcode nvarchar(50)
    [RegularExpression(ValidationRules.BarcodeOptionalRegex, ErrorMessage = ValidationRules.BarcodeError)]
    public string? Barcode { get; set; }

    [Required]
    [RegularExpression(ValidationRules.StatusRegex, ErrorMessage = ValidationRules.StatusError)]
    public string Status { get; set; } = string.Empty;

    [StringLength(200)]   // test_log.reason nvarchar(200)
    public string? Reason { get; set; }

    [Required]
    public string InTime { get; set; } = string.Empty;

    [Range(ValidationRules.QtyMin, ValidationRules.QtyMax)]
    public int Qty { get; set; } = 1;
}

// ── 3. 분류 (POST /api/v1/works/classification) — SORT 로그 ───────────────────
public sealed class ClassificationRequest
{
    [Required]
    [RegularExpression(ValidationRules.BizDayRegex, ErrorMessage = ValidationRules.BizDayError)]
    public string BizDay { get; set; } = string.Empty;

    [Required]
    [StringLength(10, MinimumLength = 1)]   // batch 1~10(계약) = biz*.batch nvarchar(10)
    public string Batch { get; set; } = string.Empty;

    [Required]
    [StringLength(20)]   // SORT: equipment_no nvarchar(20) 에 정규화 chuteNo 저장 — 원본 규칙 ≤20
    public string ChuteNo { get; set; } = string.Empty;

    // RCS 자체생성 정수 — 검증 없음
    public int PId { get; set; }

    // barcode 필수(Y)
    [Required]
    [StringLength(50)]   // test_data/test_log.barcode nvarchar(50)
    [RegularExpression(ValidationRules.BarcodeRequiredRegex, ErrorMessage = ValidationRules.BarcodeError)]
    public string Barcode { get; set; } = string.Empty;

    [Required]
    [RegularExpression(ValidationRules.StatusRegex, ErrorMessage = ValidationRules.StatusError)]
    public string Status { get; set; } = string.Empty;

    [StringLength(200)]   // test_log.reason nvarchar(200)
    public string? Reason { get; set; }

    [Required]
    public string SortTime { get; set; } = string.Empty;

    [Range(ValidationRules.QtyMin, ValidationRules.QtyMax)]
    public int Qty { get; set; } = 1;
}

// ── 4. 결과 (POST /api/v1/works/results) — 최상위 JSON 배열 요소 ───────────────
public sealed class ResultRequestGroup
{
    [Required]
    [RegularExpression(ValidationRules.BizDayRegex, ErrorMessage = ValidationRules.BizDayError)]
    public string BizDay { get; set; } = string.Empty;

    [Required]
    [StringLength(10, MinimumLength = 1)]   // batch 1~10(계약) = biz*.batch nvarchar(10)
    public string Batch { get; set; } = string.Empty;

    [Required]
    [MinLength(1, ErrorMessage = ValidationRules.ItemsMinError)]
    public List<ResultItem> Items { get; set; } = [];
}

public sealed class ResultItem
{
    // barcode 빈/누락은 서비스가 skip(§6.4) — 형식만 공통 검증.
    [StringLength(50)]   // work_result.barcode nvarchar(50)
    [RegularExpression(ValidationRules.BarcodeOptionalRegex, ErrorMessage = ValidationRules.BarcodeError)]
    public string? Barcode { get; set; }

    // chuteNo 미검증(§3.4) — 3자리 정규화만. nullable(work_result.chute_no NULL 허용).
    [StringLength(20)]   // work_result.chute_no nvarchar(20) — 길이 상한(500 방지)
    public string? ChuteNo { get; set; }

    [Range(ValidationRules.QtyMin, ValidationRules.QtyMax)]
    public int Qty { get; set; } = 1;
}

// ── 5. 박스 (POST /api/v1/works/box) ─────────────────────────────────────────
public sealed class BoxRequest
{
    [Required]
    [RegularExpression(ValidationRules.BizDayRegex, ErrorMessage = ValidationRules.BizDayError)]
    public string BizDay { get; set; } = string.Empty;

    [Required]
    [StringLength(10, MinimumLength = 1)]   // batch 1~10(계약) = biz*.batch nvarchar(10)
    public string Batch { get; set; } = string.Empty;

    [Required]
    [StringLength(50)]   // box.box_no nvarchar(50)
    public string BoxNo { get; set; } = string.Empty;

    [Required]
    [StringLength(10)]   // box.chute_no nvarchar(10) (원본 규칙 ≤10)
    public string ChuteNo { get; set; } = string.Empty;

    [Required]
    [MinLength(1, ErrorMessage = ValidationRules.ItemsMinError)]
    public List<BoxItemDto> Items { get; set; } = [];

    [StringLength(50)]   // box.end_time nvarchar(50) — 길이 상한(500 방지)
    public string? EndTime { get; set; }
}

public sealed class BoxItemDto
{
    // 빈 barcode item 은 서비스가 필터(§6.5) — 형식만 공통 검증(barcode 미검증).
    [StringLength(100)]   // box_item.barcode nvarchar(100)
    [RegularExpression(ValidationRules.BarcodeOptionalRegex, ErrorMessage = ValidationRules.BarcodeError)]
    public string? Barcode { get; set; }

    [Range(ValidationRules.QtyMin, ValidationRules.QtyMax)]
    public int Qty { get; set; } = 1;
}

// ── 1. 미작업 조회 응답 (GET /api/v1/works/unprocessed) — 최상위 배열 ──────────
/// <summary>미작업 그룹(batch 1차 그룹) — items 는 (barcode, chuteNo) 2차 그룹.</summary>
public sealed record UnprocessedGroupResponse(
    string BizDay,
    string Batch,
    List<UnprocessedItem> Items);

/// <summary>미작업 항목 — 동일 (barcode, chuteNo) 묶음, qty = COUNT.</summary>
public sealed record UnprocessedItem(
    string Barcode,
    string ChuteNo,
    int Qty);
