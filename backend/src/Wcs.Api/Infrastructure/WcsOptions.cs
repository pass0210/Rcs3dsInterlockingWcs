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
    /// IF-05/IF-10 요청 qty 상한(입력 위생 — S-CLEANUP-FIELD D-4).
    /// RCS·스캐너·Postman 버그로 비정상 대량 qty(예: int.MaxValue)가 들어오면 DB 도달 전에 400으로 거부해
    /// OVER 우회(int 오버플로)·ReservedQty/DepositedQty 오염을 막는다. 하드코딩 금지(절대규칙 #7) — 설정값.
    /// 기본 100000(현장 단일 투입 수량을 크게 상회 — 정상 입력 경로 불변).
    /// </summary>
    public int MaxQtyPerRequest { get; init; } = 100000;

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

    /// <summary>
    /// 운영자 워드 쓰기(OpsController O4/O6) 유효 범위 상한(입력 위생 — S-F3a code review I-1).
    /// 하드코딩 금지(절대규칙 #7) — appsettings "Wcs:OpsLimits"에서 읽는다.
    /// </summary>
    public OpsWriteLimits OpsLimits { get; init; } = new();
}

// ════════════════════════════════════════════════════════════════════════════
// OpsWriteLimits — 운영자 워드 쓰기(O4 SetTgtFloor·O6 CellAssign) 유효 범위 상한.
//
// 배경(S-F3a code review I-1): 큐 컨슈머(PlcGateway.ProcessWriteAsync)는 floor/cellNo/seq를
//   무조건 (short)로 캐스트해 D6/D0/D1에 기입한다. int DTO에 short.MaxValue(32767) 초과값이
//   바인딩되면 검증을 통과해 **조용히 wrap(음수/오값)**되어 PLC에 잘못 기입된다 — 이 스프린트가
//   보호하는 operator→PLC 표면에서의 Fail-Loud 위반. 따라서 상한 검증으로 400 거부한다.
//
// 이중 상한(둘 중 낮은 값으로 캡):
//   ① 도메인 sane 상한(설정값 MaxTgtFloor/MaxCellNo/MaxCellSeq) — 현장 물리 규모 상회.
//   ② 하드 타입 상한 RegisterCeiling(=short.MaxValue) — 설정을 잘못 크게 잡아도 절대 wrap 불가.
// 절대규칙 #7: 도메인 상한은 하드코딩 리터럴이 아니라 설정값. 타입 상한은 언어 상수(리터럴 아님).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>appsettings.json "Wcs:OpsLimits" 섹션 — 운영자 워드 쓰기 유효 범위 상한.</summary>
public sealed record OpsWriteLimits
{
    /// <summary>
    /// PLC 16-bit 홀딩 레지스터 하드 상한. 이 값 초과는 컨슈머의 (short) 캐스트에서 wrap하므로
    /// 어떤 설정값도 이 상한을 넘겨 유효 처리될 수 없다(설정 오설정 방어 — Fail Loud). 언어 상수.
    /// </summary>
    public const int RegisterCeiling = short.MaxValue;   // 32767

    /// <summary>O4 SetTgtFloor(D6) 최대 유효 층 번호. 현장 물리 층수 상회(운영층 2). 기본 20.</summary>
    public int MaxTgtFloor { get; init; } = 20;

    /// <summary>O6 CellAssign 최대 셀 번호(D0). 현장 셀 수(SPEC §8 16셀) 크게 상회. 기본 1000.</summary>
    public int MaxCellNo { get; init; } = 1000;

    /// <summary>O6 CellAssign 최대 C_Seq(D1). 레지스터 상한 아래 헤드룸. 기본 30000.</summary>
    public int MaxCellSeq { get; init; } = 30000;

    /// <summary>floor 유효 상한(도메인 sane 상한과 하드 타입 상한 중 낮은 값).</summary>
    public int EffectiveMaxTgtFloor => Math.Min(MaxTgtFloor, RegisterCeiling);

    /// <summary>cellNo 유효 상한(도메인 sane 상한과 하드 타입 상한 중 낮은 값).</summary>
    public int EffectiveMaxCellNo => Math.Min(MaxCellNo, RegisterCeiling);

    /// <summary>seq 유효 상한(도메인 sane 상한과 하드 타입 상한 중 낮은 값).</summary>
    public int EffectiveMaxCellSeq => Math.Min(MaxCellSeq, RegisterCeiling);
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
