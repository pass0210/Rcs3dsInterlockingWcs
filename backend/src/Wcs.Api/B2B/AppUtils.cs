using System.Globalization;

namespace Wcs.Api.B2B;

// ════════════════════════════════════════════════════════════════════════════
// AppUtils (B2B) — 원본 유틸 이식(§5). B2B 전용 네임스페이스.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>B2B 공통 유틸.</summary>
public static class AppUtils
{
    private static readonly string[] BizDayFormats = { "yyyy-MM-dd", "yyyyMMdd" };

    /// <summary>
    /// bizDay 정규화 — 허용 입력 <c>YYYYMMDD</c> | <c>YYYY-MM-DD</c>.
    /// · 빈/null 문자열은 그대로 반환.
    /// · 성공 시 항상 <c>"YYYY-MM-DD"</c> 반환(저장·비교 통일).
    /// · 형식은 통과하나 존재하지 않는 날짜(예: 20261332) → <see cref="ArgumentException"/>("Invalid date: ...").
    ///   (POST 는 DataAnnotation 형식검증 #10 이 선행하므로, 여기 도달 = 형식통과·달력 무효.)
    /// </summary>
    public static string NormalizeBizDay(string? bizDay)
    {
        if (string.IsNullOrEmpty(bizDay))
            return bizDay ?? string.Empty;   // 빈 문자열 그대로 반환

        var trimmed = bizDay.Trim();
        if (DateTime.TryParseExact(trimmed, BizDayFormats,
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        // 형식통과·비존재 날짜(또는 형식불일치 — GET 경로) → 400 유도(#17)
        throw new ArgumentException(FailMessages.InvalidDate(bizDay));
    }

    /// <summary>
    /// ChuteNo 3자리 zero-pad 정규화 — int 파싱 성공 시 <c>ToString("D3")</c>("1"→"001"),
    /// 실패 시 원문 유지(비교 깨짐 방지). test_data·test_log(SORT)·work_result·box 공통 규칙(§5).
    /// </summary>
    public static string NormalizeChuteNo(string? chuteNo)
    {
        if (string.IsNullOrWhiteSpace(chuteNo))
            return chuteNo ?? string.Empty;

        var trimmed = chuteNo.Trim();
        return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n.ToString(AppConstants.ChuteNoFormat, CultureInfo.InvariantCulture)
            : trimmed;
    }
}
