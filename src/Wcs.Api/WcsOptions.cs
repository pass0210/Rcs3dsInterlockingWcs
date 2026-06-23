namespace Wcs.Api;

// ════════════════════════════════════════════════════════════════════════════
// WcsOptions — WCS 운영 설정(appsettings "Wcs" 섹션).
// 운영층 등 도메인 상수를 설정으로 외부화한다 — 하드코딩 금지(절대규칙 #7).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>appsettings.json "Wcs" 섹션.</summary>
public sealed record WcsOptions
{
    /// <summary>
    /// 3D 소터 운영층(고정 정렬 대상). AGV는 항상 이 층에서 수령하므로
    /// IF-09 도착 시 WCS가 소터를 이 층으로 정렬한다. 기본 2층.
    /// 절대규칙 #7: 하드코딩 금지 — 설정에서 읽는다.
    /// </summary>
    public int OperationalFloor { get; init; } = 2;
}
