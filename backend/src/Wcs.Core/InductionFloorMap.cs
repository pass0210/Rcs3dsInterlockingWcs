namespace Wcs.Core;

/// <summary>
/// 인덕션 번호 → 목표 층(F) 파생 — 순수 함수(I/O·DI 의존 0). 테스트가 스펙이다.
///
/// 설계(인덕션 기반 2층 제어 — 2026-07-21):
///   IF-05 요청 inductionNo로 목표 층 F(1/2)를 파생한다. 맵(inductionNo→floor)은 **호출자가 주입**한다
///   (appsettings Wcs:InductionFloorMap 결선 — 하드코딩 금지·절대규칙 #7). Core는 맵을 소유하지 않는다.
///
/// Fail Loud(확정 결정 2026-07-22): 맵에 없는 inductionNo는 **null**을 반환한다 — 호출자(IF-05)가
///   NG 응답 + 경고 로그로 거부한다. 조용한 통과·기본층 폴백 금지.
/// </summary>
public static class InductionFloorMap
{
    /// <summary>
    /// inductionNo에 대응하는 목표 층 F. 맵에 없으면 <c>null</c>(미매핑 — 호출자 fail-loud).
    /// </summary>
    /// <param name="map">inductionNo → floor 맵(호출자 주입 — Core는 소유·조회만).</param>
    /// <param name="inductionNo">IF-05 요청의 인덕션 번호.</param>
    public static int? DeriveFloor(IReadOnlyDictionary<int, int> map, int inductionNo)
    {
        ArgumentNullException.ThrowIfNull(map);
        return map.TryGetValue(inductionNo, out var floor) ? floor : null;
    }
}
