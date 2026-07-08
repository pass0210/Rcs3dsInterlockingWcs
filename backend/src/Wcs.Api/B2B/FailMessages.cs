namespace Wcs.Api.B2B;

// ════════════════════════════════════════════════════════════════════════════
// FailMessages — B2B API 실패/성공 message 정본 (verbatim — 한 글자도 변경 금지).
//
// 정본: docs/B2B-SCHEMA.md §4 ( = docs/api-spec-ko.html §6 ).
// 서비스가 방출하는 문자열을 여기에 단일 진실로 고정 — 리팩터로 인한 문자열 표류 차단.
// 테스트(B2B 서비스/통합)가 이 상수를 참조해 byte-for-byte 일치를 검증한다.
//
// {..} 자리표시자는 런타임 값 — 포맷 메서드로 조립한다.
// DataAnnotations ErrorMessage(#10~14)는 DTO에 인라인(ModelState 경로), 여기엔 서비스 방출분만.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>B2B API 실패/성공 message 정본(§4).</summary>
public static class FailMessages
{
    // ── 성공 (status "S") ──────────────────────────────────────────────────
    public const string Success = "Success";                                        // #9

    // ── 비즈니스 실패 (HTTP 200 + status "F") ────────────────────────────────
    /// <summary>#1 — input; classification(후보 0).</summary>
    public const string BarcodeNotFound =
        "Barcode not found, or bizDay/batch does not match the registered data.";
    /// <summary>#5 — results(null/빈 배열).</summary>
    public const string NoDataToProcess = "No data to process.";
    /// <summary>#7 — results(유효 item 0).</summary>
    public const string NoValidDataToProcess = "No valid data to process.";
    /// <summary>#8 — box(중복 재전송).</summary>
    public const string BoxAlreadyExists = "Box already exists for the given bizDay/batch/boxNo.";

    // ── 검증 실패(컨트롤러/유틸 방출 — HTTP 400) ──────────────────────────────
    /// <summary>#16 — unprocessed GET 컨트롤러 bizDay 쿼리 누락.</summary>
    public const string BizDayParameterRequired = "bizDay parameter is required.";
    /// <summary>#18 — ModelState firstError 없음 fallback.</summary>
    public const string InvalidRequestBody = "Invalid request body.";

    // ── 포맷 메서드(런타임 자리표시자 치환) ───────────────────────────────────

    /// <summary>#2 — 미처리/미분류 행이 qty 보다 적음(input &amp; classification).</summary>
    public static string NotEnoughRows(int requested, int available) =>
        $"Not enough unprocessed rows: requested {requested}, available {available}.";

    /// <summary>#3 — 요청 슈트가 바코드의 등록 슈트와 불일치(classification).</summary>
    public static string ChuteMismatch(string barcode, string chuteList, string receivedChuteNo) =>
        $"Chute mismatch: barcode {barcode} expected chute(s) [{chuteList}], received {receivedChuteNo}.";

    /// <summary>#4 — 해당 바코드+슈트의 모든 행이 이미 분류됨(classification).</summary>
    public static string AlreadyClassified(string barcode, string chuteNo) =>
        $"Barcode {barcode} in chute {chuteNo} has already been fully classified.";

    /// <summary>#6 — results 개별 barcode 미등록(작은따옴표 포함).</summary>
    public static string ResultBarcodeNotFound(string barcode) =>
        $"Barcode '{barcode}' not found, or bizDay/batch does not match the registered data.";

    /// <summary>#17 — NormalizeBizDay(형식통과·비존재 날짜).</summary>
    public static string InvalidDate(string value) => $"Invalid date: {value}";
}
