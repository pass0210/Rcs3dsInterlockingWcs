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

    /// <summary>
    /// IF-08 아웃바운드 푸시(WCS→RCS destination-status) 설정.
    /// Phase 2 — RCS base URL·재시도·타이밍 전부 설정화(하드코딩 금지, 절대규칙 #7).
    /// </summary>
    public RcsPushOptions RcsPush { get; init; } = new();

    /// <summary>
    /// F2 실시간 모니터링(SignalR relay) 타이밍 설정(appsettings "Wcs:Monitor").
    /// 하트비트 주기 등 신규 타이밍은 전부 여기서 읽는다(하드코딩 금지, 절대규칙 #7).
    /// </summary>
    public MonitorOptions Monitor { get; init; } = new();
}

// ════════════════════════════════════════════════════════════════════════════
// MonitorOptions — F2 실시간 relay 타이밍 (appsettings "Wcs:Monitor" 섹션).
// 하트비트(저빈도 전체 스냅샷 재전송)로 델타 유실·재연결 갭을 보정한다.
// 절대규칙 #7: 주기 하드코딩 금지 — 설정에서 읽는다.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>appsettings.json "Wcs:Monitor" 섹션 — SignalR relay 타이밍.</summary>
public sealed record MonitorOptions
{
    /// <summary>
    /// 하트비트 주기(ms). 이 주기마다 전체 소터 워드 스냅샷을 sorters 그룹에 1회 push해
    /// 델타 유실·재연결 갭을 보정한다. ≤0이면 하트비트 비활성. 기본 5000ms(저빈도).
    /// </summary>
    public int HeartbeatMs { get; init; } = 5000;
}

// ════════════════════════════════════════════════════════════════════════════
// RcsPushOptions — IF-08 아웃바운드 푸시 설정 (appsettings "Wcs:RcsPush" 섹션).
//
// RCS↔WCS 재설계 Phase 2:
//   목적지(슈트/소터) ready 전이 시 WCS가 POST {BaseUrl}/api/v1/destination-status 로 푸시.
//   base URL·재시도 횟수·백오프·간격·HTTP 타임아웃을 전부 설정으로 외부화한다.
//   - 사용자 확정2: 기본 3회 지수 백오프(1s/2s/4s). 고정 sleep·하드코딩 금지.
//   - 사용자 확정4: BaseUrl 미설정 시 푸시 비활성(경고 후 no-op) — 크래시 X.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>appsettings.json "Wcs:RcsPush" 섹션 — IF-08 아웃바운드 푸시 설정.</summary>
public sealed record RcsPushOptions
{
    /// <summary>
    /// RCS base URL(예: "http://10.0.0.5:8080"). 엔드포인트는 RCS가 제공 —
    /// WCS는 "{BaseUrl}/api/v1/destination-status"로 POST한다.
    /// 미설정(null/공백)이면 푸시 비활성(경고 로그 후 no-op — 사용자 확정4).
    /// 운영 배포에선 필수 설정.
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// 아웃바운드 경로(BaseUrl에 이어붙이는 상대 경로). 스펙 IF-08 정의.
    /// RCS가 다른 경로를 제공하면 설정으로 교체 가능(하드코딩 금지).
    /// </summary>
    public string Path { get; init; } = "/api/v1/destination-status";

    /// <summary>
    /// 푸시 실패(연결 거부·타임아웃·5xx) 시 재시도 횟수(최초 시도 제외 — 총 시도 = 1+RetryCount).
    /// 사용자 확정2: 기본 3회.
    /// </summary>
    public int RetryCount { get; init; } = 3;

    /// <summary>
    /// 지수 백오프 초기 지연(ms). 시도 n회차 지연 = RetryBaseDelayMs × 2^(n-1)(상한 RetryMaxDelayMs).
    /// 사용자 확정2: 기본 1000ms(→ 1s/2s/4s). 고정 sleep 아님(설정값).
    /// </summary>
    public int RetryBaseDelayMs { get; init; } = 1000;

    /// <summary>지수 백오프 지연 상한(ms). 기본 4000ms.</summary>
    public int RetryMaxDelayMs { get; init; } = 4000;

    /// <summary>HTTP 요청 타임아웃(ms). 기본 3000ms.</summary>
    public int HttpTimeoutMs { get; init; } = 3000;

    /// <summary>
    /// 소터 ready 전이 감지를 위한 스냅샷 관찰 주기(ms).
    /// 게이트웨이 폴링 스냅샷(bundle.Latest)을 이 주기로 diff해 ready 전이를 감지한다
    /// (게이트웨이 본문 무변경 — Latest 관찰만, Scope D (a)). 폴 간격과 동급 권장.
    /// </summary>
    public int SorterObserveIntervalMs { get; init; } = 150;

    /// <summary>BaseUrl이 설정되어 푸시가 활성인지.</summary>
    public bool IsEnabled => !string.IsNullOrWhiteSpace(BaseUrl);
}
