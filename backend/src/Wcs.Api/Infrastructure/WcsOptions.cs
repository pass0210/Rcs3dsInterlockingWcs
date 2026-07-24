namespace Wcs.Api;

// ════════════════════════════════════════════════════════════════════════════
// WcsOptions — WCS 운영 설정(appsettings "Wcs" 섹션).
// 운영층 등 도메인 상수를 설정으로 외부화한다 — 하드코딩 금지(절대규칙 #7).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>appsettings.json "Wcs" 섹션.</summary>
public sealed record WcsOptions
{
    /// <summary>
    /// 3D 소터 운영층(레거시 단일 층). 인덕션 기반 2층 제어(2026-07-21)로 대체됐다 — 목표 층 F는
    /// 이제 <see cref="InductionFloorMap"/>에서 파생하며, 이 값은 더 이상 정렬 트리거에 쓰이지 않는다
    /// (하위호환·문서 참조용으로 남김). 절대규칙 #7: 하드코딩 금지 — 설정에서 읽는다.
    /// </summary>
    public int OperationalFloor { get; init; } = 2;

    /// <summary>
    /// 인덕션 번호 → 목표 층(F) 맵 (appsettings "Wcs:InductionFloorMap", 예 {"1":1,"2":1,"3":2}).
    /// IF-05가 요청 inductionNo로 목표 층 F(1/2)를 파생하는 근거(§2-A·§3·§5-A). 하드코딩 금지(절대규칙 #7).
    /// 키는 JSON 문자열(설정 바인딩 제약) — <see cref="FloorByInduction"/>가 int 키 맵으로 변환한다.
    /// 미매핑 inductionNo는 fail-loud(NG 응답 + 경고 로그) — 조용한 통과·기본층 폴백 금지(확정 결정 2026-07-22).
    /// </summary>
    public Dictionary<string, int> InductionFloorMap { get; init; } = new();

    /// <summary>
    /// <see cref="InductionFloorMap"/>(문자열 키)을 int 키 맵으로 변환한 읽기 전용 뷰.
    /// Wcs.Core.InductionFloorMap.DeriveFloor(순수)에 주입한다. 파싱 불가 키는 건너뛴다(무시).
    /// </summary>
    public IReadOnlyDictionary<int, int> FloorByInduction
    {
        get
        {
            var result = new Dictionary<int, int>(InductionFloorMap.Count);
            foreach (var (key, floor) in InductionFloorMap)
                if (int.TryParse(key, out var inductionNo))
                    result[inductionNo] = floor;
            return result;
        }
    }

    /// <summary>
    /// IF-05/IF-10 요청 qty 상한(입력 위생 — S-CLEANUP-FIELD D-4).
    /// RCS·스캐너·Postman 버그로 비정상 대량 qty(예: int.MaxValue)가 들어오면 DB 도달 전에 400으로 거부해
    /// OVER 우회(int 오버플로)·ReservedQty/DepositedQty 오염을 막는다. 하드코딩 금지(절대규칙 #7) — 설정값.
    /// 기본 100000(현장 단일 투입 수량을 크게 상회 — 정상 입력 경로 불변).
    /// </summary>
    public int MaxQtyPerRequest { get; init; } = 100000;

    /// <summary>
    /// 목적지 수용상태 아웃바운드 푸시(WCS → RCS PUT /api/UpdateChuteState) 설정.
    /// S-IF08-READY-PUSH — 확정 와이어(UpdateChuteState) 단일 채널. base URL·경로·재시도·타이밍·
    /// 소터 관찰 주기 전부 설정화(하드코딩 금지, 절대규칙 #7).
    /// BaseUrl 미설정(호스트 미정) 시 DORMANT(no-op)로 출하 — RCS가 추후 이 한 값만 설정하면 활성.
    /// </summary>
    public ChuteStatePushOptions ChuteStatePush { get; init; } = new();

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

    /// <summary>
    /// 소터별 pending-floor 큐 관측 루프(SorterFloorReturnService) 타이밍 — 인덕션 기반 2층 제어.
    /// TgtFloor==0 관측 주기 등. 하드코딩 금지(절대규칙 #7) — appsettings "Wcs:SorterFloorReturn".
    /// </summary>
    public SorterFloorReturnOptions SorterFloorReturn { get; init; } = new();
}

// ════════════════════════════════════════════════════════════════════════════
// SorterFloorReturnOptions — 소터별 pending-floor 큐 관측 루프 타이밍 (Wcs:SorterFloorReturn).
//
// 인덕션 기반 2층 제어(2026-07-21): IF-05가 소터별 큐에 목표 층 F를 enqueue하면, 이 루프가
//   각 소터 스냅샷의 TgtFloor==0을 이 주기로 관측해 큐 머리 층 F를 게이트 통과 시 기입(SetTgtFloor).
//   폴 주기와 동급 권장. 하드코딩 금지(절대규칙 #7).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>appsettings.json "Wcs:SorterFloorReturn" 섹션 — 큐 관측 루프 타이밍.</summary>
public sealed record SorterFloorReturnOptions
{
    /// <summary>
    /// 큐 관측 주기(ms). 각 소터의 TgtFloor==0을 이 주기로 관측해 큐 머리 층 F 기입/pop을 수행한다.
    /// ≤0이면 최소 1로 클램프. 기본 150ms(폴 주기 동급).
    /// </summary>
    public int ObserveIntervalMs { get; init; } = 150;
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
// ChuteStatePushOptions — 목적지 수용상태 아웃바운드 푸시 설정 (appsettings "Wcs:ChuteStatePush").
//
// S-IF08-READY-PUSH (확정 와이어 단일 채널) + S-TWO-FLOOR-CONTROL B (층별 호스트 라우팅):
//   목적지(슈트+소터) 수용상태 전이 시 WCS가 PUT {host}{Path}로
//   {chute_numbers:[dest.ChuteNo], next_states:[2|3]} 를 RCS로 푸시한다(3=수용가능/2=불가).
//   ★ 호스트는 목적지의 **층**으로 라우팅한다 — 층은 payload에 유입되지 않고 **어느 호스트가
//     수신하느냐**로만 전달된다(경로·payload·next_state 의미는 층 무관 동일 — wire 계약 불변).
//   전이원: 운영자 PAUSED/RESUMED · 슈트 만재/비움 · 소터 분류 사이클·정렬(스냅샷 관찰).
//   호스트 맵·경로·재시도·백오프·HTTP 타임아웃·소터 관찰 주기를 전부 설정으로 외부화한다
//   (하드코딩 금지·절대규칙 #7 — 호스트 리터럴은 코드에 없다. 실 IP는 appsettings·docs에만).
//   - 지수 백오프(기본 3회 1s/2s/4s).
//   - 층별 DORMANT: 어떤 층 호스트가 미설정이면 그 층 push는 no-op(HTTP 시도 0). 전 층 미설정이면
//     서브시스템 전체 DORMANT(경고 후 관찰/구독 미기동·크래시 0).
//   - chute_number 매핑 config 없음(Q-b LOCKED = Destination.ChuteNo 직접 1:1).
//   - SorterObserveIntervalMs: 소터 분류 사이클(3↔2)·정렬 전이는 명시 이벤트가 없어 스냅샷 관찰이
//     유일한 감지 수단이므로 반드시 유지.
//
// 하위호환(레거시 단일 호스트): 구 단일 키 `BaseUrl`은 **FloorHosts가 비었을 때만** 쓰이는
//   fallback으로 유지한다(제거 대신 alias — 이연 결정). FloorHosts가 하나라도 설정되면 BaseUrl은
//   무시된다(FloorHosts 우선). 레거시 모드에선 모든 목적지가 그 단일 호스트로 발신된다(층 무관 —
//   구 동작 보존). 출하 기본은 FloorHosts={} + BaseUrl=null → DORMANT(실 운영 파괴 0).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>appsettings.json "Wcs:ChuteStatePush" 섹션 — 목적지 수용상태 아웃바운드 푸시 설정.</summary>
public sealed record ChuteStatePushOptions
{
    /// <summary>
    /// [레거시 alias] RCS 단일 base URL. **FloorHosts가 비었을 때만** fallback으로 쓰인다(층별 라우팅으로
    /// 대체됨 — 하위호환용). FloorHosts가 설정되면 무시. 미설정(null/공백)이고 FloorHosts도 비면 DORMANT.
    /// </summary>
    public string? BaseUrl { get; init; }

    /// <summary>
    /// 층 → RCS 호스트 맵 (appsettings "Wcs:ChuteStatePush:FloorHosts", 예 {"1":"http://rcs-1f-host:3000","2":"http://rcs-2f-host:3000"}. 실 IP는 appsettings·docs).
    /// 목적지의 층에 해당하는 호스트로 push를 라우팅한다. 키는 JSON 문자열(설정 바인딩 제약) —
    /// <see cref="HostByFloor"/>가 int 키 맵으로 변환한다. 값이 null/공백인 층은 미설정(그 층 DORMANT)으로 간주.
    /// 호스트 리터럴은 여기(설정)에만 존재하고 코드엔 없다(절대규칙 #7).
    /// </summary>
    public Dictionary<string, string> FloorHosts { get; init; } = new();

    /// <summary>
    /// <see cref="FloorHosts"/>(문자열 키)를 int 키 맵으로 변환한 읽기 전용 뷰. 파싱 불가 키·null/공백
    /// 호스트는 건너뛴다(그 층은 미설정=DORMANT). 층별 라우팅·부트스트랩이 이 뷰를 소비한다.
    /// </summary>
    public IReadOnlyDictionary<int, string> HostByFloor
    {
        get
        {
            var result = new Dictionary<int, string>(FloorHosts.Count);
            foreach (var (key, host) in FloorHosts)
                if (int.TryParse(key, out var floor) && !string.IsNullOrWhiteSpace(host))
                    result[floor] = host.Trim();
            return result;
        }
    }

    /// <summary>
    /// 아웃바운드 경로(호스트에 이어붙이는 상대 경로). RCS 계약(UpdateChuteState) 기본값.
    /// RCS가 다른 경로를 제공하면 설정으로 교체 가능(하드코딩 금지 — 외부화된 기본값). 층 공통(호스트만 다름).
    /// </summary>
    public string Path { get; init; } = "/api/UpdateChuteState";

    /// <summary>
    /// 푸시 실패(연결 거부·타임아웃·5xx·flag≠1) 시 재시도 횟수(최초 시도 제외 — 총 시도 = 1+RetryCount).
    /// 기본 3회.
    /// </summary>
    public int RetryCount { get; init; } = 3;

    /// <summary>
    /// 지수 백오프 초기 지연(ms). 시도 n회차 지연 = RetryBaseDelayMs × 2^(n-1)(상한 RetryMaxDelayMs).
    /// 기본 1000ms(→ 1s/2s/4s). 고정 sleep 아님(설정값).
    /// </summary>
    public int RetryBaseDelayMs { get; init; } = 1000;

    /// <summary>지수 백오프 지연 상한(ms). 기본 4000ms.</summary>
    public int RetryMaxDelayMs { get; init; } = 4000;

    /// <summary>HTTP 요청 타임아웃(ms). 기본 3000ms.</summary>
    public int HttpTimeoutMs { get; init; } = 3000;

    /// <summary>
    /// S-TWO-FLOOR-CONTROL C2 — 부트스트랩 push 전에 소터 콜드스타트 레지스터 클리어(StartupClear) 완료를
    /// 기다리는 상한(ms). "클리어 before push" 순서(계약 S3/CC3)를 보장하되, 소터가 끝내 Online이 안 되면
    /// (오프라인 기동) 이 상한 경과 후 경고와 함께 부트스트랩을 진행한다(무한 대기 금지). 하드코딩 금지(절대규칙 #7).
    /// 기본 5000ms(첫 폴+클리어 처리는 통상 폴 주기 수 배 이내 — 온라인 기동은 이 값보다 훨씬 빨리 완료).
    /// </summary>
    public int StartupClearWaitMs { get; init; } = 5000;

    /// <summary>
    /// 소터 수용상태 전이 감지를 위한 스냅샷 관찰 주기(ms). 폐지된 목적지-상태 와이어 옵션에서 이전.
    /// 소터 분류 사이클(Ready 1↔0)·정렬 전이는 명시 이벤트가 없으므로 게이트웨이 폴링 스냅샷
    /// (bundle.Latest)을 이 주기로 diff해 수용상태 전이를 감지한다(게이트웨이 본문 무변경 — Latest
    /// 관찰만). 폴 간격과 동급 권장. 하드코딩 금지(절대규칙 #7).
    /// </summary>
    public int SorterObserveIntervalMs { get; init; } = 150;

    /// <summary>
    /// 푸시 서브시스템이 활성인지 — 층 호스트가 하나라도 설정됐거나(신규) 레거시 BaseUrl이 설정됨.
    /// 둘 다 미설정이면 서브시스템 전체 DORMANT(관찰/구독 미기동).
    /// </summary>
    public bool IsEnabled => HostByFloor.Count > 0 || !string.IsNullOrWhiteSpace(BaseUrl);

    /// <summary>
    /// 레거시 단일 호스트 모드인지 — 층 호스트 맵이 비어 있고 BaseUrl만 설정됨(하위호환).
    /// 이 모드에선 모든 목적지가 층 무관하게 BaseUrl 한 곳으로 발신된다(구 동작 보존).
    /// </summary>
    public bool IsLegacySingleHost => HostByFloor.Count == 0 && !string.IsNullOrWhiteSpace(BaseUrl);

    /// <summary>지정 층의 호스트(미설정이면 null=그 층 DORMANT). 층별 라우팅 진입점.</summary>
    public string? HostForFloor(int floor) =>
        HostByFloor.TryGetValue(floor, out var host) ? host : null;
}
