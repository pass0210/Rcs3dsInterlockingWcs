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

    // ── S-B2B-2a: test-data 관리 유틸(§2.1) — 슈트 파서·바코드 채번 ─────────────

    /// <summary>
    /// 슈트 범위 문자열 파싱(§2.1·§3.2.10) — 콤마 구분에서 <c>"a-b"</c> 범위 전개·단일 숫자,
    /// <see cref="HashSet{T}"/> 중복 제거 후 오름차순 정렬 반환. 예: <c>"1-5,8"</c> → <c>[1,2,3,4,5,8]</c>.
    /// · 공백 무시. 파싱 불가 토큰(비숫자)·역순 범위(b&lt;a)·음수는 무시(skip).
    /// · 결과 0개면 호출측이 F(Invalid chute numbers) 처리(§2.1).
    /// 수동 생성·(자동생성 preview는 미이식)이 공유하는 단일 파서.
    /// </summary>
    public static List<int> ParseChuteNos(string? input)
    {
        var result = new HashSet<int>();
        if (string.IsNullOrWhiteSpace(input))
            return new List<int>();

        foreach (var rawToken in input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dash = rawToken.IndexOf('-');
            if (dash > 0)
            {
                // 범위 "a-b" — a·b 모두 정수여야 전개(a<=b). 첫 글자가 '-'(음수)면 dash>0 아님.
                var left  = rawToken[..dash].Trim();
                var right = rawToken[(dash + 1)..].Trim();
                if (int.TryParse(left, NumberStyles.Integer, CultureInfo.InvariantCulture, out var a) &&
                    int.TryParse(right, NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) &&
                    a <= b)
                {
                    for (var n = a; n <= b; n++)
                        result.Add(n);
                }
            }
            else if (int.TryParse(rawToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out var single))
            {
                result.Add(single);
            }
        }

        var list = result.ToList();
        list.Sort();
        return list;
    }

    /// <summary>
    /// 테스트 바코드 자동 채번(§2.1) — <c>BC{yyyyMMddHHmmssfff}{랜덤4자리}</c>.
    /// 밀리초 3자리 + 1000~9998 랜덤으로 대량 생성 시 충돌 최소화(유니크 제약 없음 — 원본 동일).
    /// </summary>
    public static string GenerateBarcode() =>
        $"BC{DateTime.Now:yyyyMMddHHmmssfff}{Random.Shared.Next(1000, 9999)}";
}
