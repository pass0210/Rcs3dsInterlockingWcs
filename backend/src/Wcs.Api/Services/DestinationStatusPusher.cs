using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wcs.Core;
using Wcs.Data;

namespace Wcs.Api;

// ════════════════════════════════════════════════════════════════════════════
// DestinationStatusPusher — 목적지 수용상태 전이 감지 + 전이당 1회 푸시
//   (S-IF08-READY-PUSH → S-TWO-FLOOR-CONTROL B: 층별 호스트 라우팅 · 소터 dual-host)
//
// 확정 와이어(UpdateChuteState) 단일 발신 소스 — 목적지(슈트+소터)의 "수용상태"가 전이할 때만
//   IChuteStatePushClient로 1회 푸시. payload = {chute_numbers:[ChuteNo], next_states:[3|2]}
//   (snake_case, 전이당 단건). 3 = 받을 수 있음 / 2 = 받을 수 없음.
//
//   ★ 수용상태 합성(계약 SC-2) — DestinationStatusService.Compute를 재사용(새 판정 0):
//     accept = Compute().Ready ∧ !Compute().Paused.
//       - 슈트(CHUTE): accept = 비만재 ∧ 비정지.
//       - 소터(SORTER_3D): accept = 운영 ready(online·정렬·Ready=1, paused·SorterFull 제외) ∧ !paused
//         (paused를 다시 접어 넣고 셀 만재 SorterFull은 제외 — 만재는 IF-05 dispatch만 차단).
//
//   ★ 층별 호스트 라우팅(B) — 층은 payload에 유입 0, **어느 호스트가 수신하느냐**로만 전달:
//     · 고정(비3D) 슈트(Destination.Floor 비-NULL): 자기 층 호스트 **한 곳**에 push
//       (accept?3:2). 그 층 호스트 미설정이면 no-op(층별 DORMANT).
//     · 고정 슈트인데 Floor==NULL(미할당 — 시드/레거시): 층 미상이므로 **설정된 전 층 호스트**에
//       같은 accept 값을 push(층-무관 슈트 — 모순 없음. 층별 배정되면 그 층 한 곳으로 좁혀짐).
//     · 3D 소터(Destination.Floor==NULL, 정렬로 두 층 겸용): **설정된 전 층 호스트 모두**에 push —
//       현재 CurFloor 층 호스트 = accept면 3/아니면 2, 다른 층 = 항상 2, 오프라인/CurFloor 불명이면 둘 다 2.
//     · [레거시 단일 호스트] FloorHosts 미설정·BaseUrl만 설정 시: 모든 목적지가 그 한 호스트로 발신
//       (accept?3:2, 층 무관 — 구 동작 보존). 소터도 단일 발신(CurFloor 비교 없음 — wildcard 층).
//
//   전이 추적을 **(목적지, 층 호스트) 단위**로 분리(B): 각 route(dest,floor-host)마다 "직전 Computed
//   next_state"와 "성공 알린 Acked next_state"를 분리 보관. 푸시는 route별 Computed != Acked일 때만
//   (값이 같은 관찰은 미발신 — 폴마다 폭주 0). 성공 시 Acked=Computed, 실패 시 Acked 불변(다음 관찰
//   재푸시). CurFloor 1→2 전이는 (dest,floor1)·(dest,floor2) 두 route가 각자 정확히 1회 전이한다
//   (1F→2 1건 + 2F→3 1건, 중복 0·누락 0). 층 호스트는 완전 독립(한쪽 다운이 다른 층 발신을 막지 않음).
//
//   변화원 셋이 공통 발신 경로(Observe→PumpAsync)로 수렴(단일 소스 — 이중/모순 발신 구조적 불가):
//     ① 슈트 ChuteCapacityService 상태 변화 — OnChuteStateChanged 콜백.
//     ② 소터 폴링 스냅샷 변화 — 관찰 타이머가 주기적으로 bundle.Latest를 diff(게이트웨이 본문 무변경).
//     ③ 운영자 PAUSED/RESUMED 전이 — DestinationControlService.OnTransition(실제 전이에서만 발화).
//
//   동시성: 변화원 셋 + 재시도가 동시에 같은 route를 갱신해도 per-route 락 + in-flight 플래그로
//   "전이당 정확히 1회"(중복 0·누락 0)를 보장한다(비원자 check-then-act 금지).
//
//   관심사 분리: 이 서비스는 "전이 감지 → route별 1건 발신 요청"만 책임진다. "PUT 1건 전송(지정 호스트)
//   + 지수 백오프 재시도 + Fail-Loud"는 IChuteStatePushClient가 책임(ChuteStatePushClient).
//
//   부트스트랩: 기동 시 층 호스트(또는 레거시 BaseUrl) 설정되어 있으면 전 활성 목적지의 현재 수용상태를
//   §라우팅 규칙대로 route별 1회 발신 후 이후 전이만. 전 층 미설정(DORMANT)이면 경고 후 전체 비활성.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 슈트 capacity 변화를 Pusher에 통지하는 콜백 인터페이스.
/// ChuteCapacityService(슈트 변화원)가 수용상태 경계를 넘길 수 있는 이벤트마다 호출.
/// Pusher가 Compute로 재산출해 전이만 발신(무변화면 0건 — 폴마다 폭주 금지).
/// </summary>
public interface IDestinationChangeNotifier
{
    /// <summary>슈트 destination의 상태가 바뀌었을 수 있음 — 재평가·전이 시 발신.</summary>
    void NotifyChuteChanged(long destinationId);
}

/// <summary>
/// 목적지 수용상태 전이 감지·발신 파이프(목적지당 단일 소스·층별 호스트 라우팅) — IHostedService.
/// 슈트 콜백 + 소터 스냅샷 관찰 타이머 + 운영자 전이 이벤트 세 변화원을 공통 Observe로 수렴.
/// </summary>
public sealed class DestinationStatusPusher : IDestinationChangeNotifier, IHostedService, IAsyncDisposable
{
    // 계약 상태값(UpdateChuteState next_state) — LOCKED.
    private const int NextStateOpen  = 3;   // 수용 가능(accept)  → Manual-open
    private const int NextStatePause = 2;   // 수용 불가          → Pause

    // 레거시 단일 호스트 모드의 route 키(실제 층 번호와 충돌하지 않는 센티넬 — 레거시 모드에선
    // FloorHosts가 비어 HostByFloor에 이 키가 존재할 수 없다). 층은 양수(1/2)라 음수 센티넬 사용.
    private const int LegacyRouteKey = int.MinValue;

    // ── (목적지, 층 호스트) 단위 전이 추적 상태 ────────────────────────────────
    //
    // S-IF08-PUSH-LOG-THROTTLE: 이 route 는 반복-실패 로그 억제 게이트(IPushFailureLogThrottle)이기도 하다
    //   — 억제 단위 = (route, next_state). 클라이언트가 재시도-소진 실패 확정 시 OnFailure 로 emit 여부를
    //   위임받고, 그 판정·상태 갱신은 이 route 의 Gate 락 안에서 원자적으로 수행한다(계약 스레드안전).
    private sealed class RouteState : IPushFailureLogThrottle
    {
        /// <summary>route 키 — 층 번호(1/2) 또는 <see cref="LegacyRouteKey"/>.</summary>
        public required int RouteKey { get; init; }

        /// <summary>이 route의 목적 호스트(설정값 — 절대규칙 #7). route 수명 동안 불변.</summary>
        public required string Host { get; init; }

        /// <summary>억제 on/off·요약 주기 설정(절대규칙 #7 — 하드코딩 0). route 수명 동안 불변.</summary>
        public required ChuteStatePushOptions Options { get; init; }

        /// <summary>per-route 직렬화 락 — 관찰·발신 결정·Acked 갱신·로그 억제 상태를 원자화.</summary>
        public readonly object Gate = new();

        /// <summary>마지막으로 Compute·라우팅이 산출한 next_state(2/3). null=아직 미산출.</summary>
        public int? Computed;

        /// <summary>마지막으로 이 호스트에 성공 알린 next_state. null=아직 한 번도 안 보냄(부트스트랩 대기).</summary>
        public int? Acked;

        /// <summary>이 route에 대한 발신 루프가 진행 중인지(in-flight — 중복 발화 차단).</summary>
        public bool PushInFlight;

        /// <summary>반복-실패 로그 억제 상태(직전 로깅한 실패 next_state + 요약 주기 기준 시각). Gate 락으로 보호.</summary>
        private readonly PushFailureLogThrottleState _failureLog = new();

        /// <summary>
        /// [IPushFailureLogThrottle] 재시도-소진 실패 확정 시 클라이언트가 호출 — emit/suppress/summary 위임.
        /// Gate 락 안에서 원자적 check-and-set(비원자 check-then-act 금지). 클라이언트는 락 밖(발신 I/O 중)에서
        /// 호출하므로 여기서 Gate 를 잡아도 데드락 없음(PumpAsync 의 발신은 락 밖 — 계약 in-flight 임계구역과 동일).
        /// </summary>
        public PushFailureLogAction OnFailure(int nextState)
        {
            lock (Gate)
                return _failureLog.Decide(
                    nextState, Options.SuppressRepeatedFailureLog, Options.FailureLogSummaryIntervalMs,
                    DateTimeOffset.UtcNow);
        }

        /// <summary>push 성공 복구 시 억제 리셋 — 다음 실패가 새 첫 실패로 로깅되도록 재무장.
        /// ★ M2: 스스로 <see cref="Gate"/> 락을 잡아 by-construction 안전(호출자 관례에 미의존). PumpAsync ③가
        /// 이미 Gate 보유 중 호출해도 동일 스레드 재진입(Monitor 재진입)으로 안전 — OnFailure 의 check-and-set 와 동일 임계구역.</summary>
        public void ResetFailureLogSuppression()
        {
            lock (Gate)
                _failureLog.Reset();
        }
    }

    // ── destination별 전이 추적 상태(route 집합 포함) ──────────────────────────
    private sealed class DestState
    {
        public required long     DestinationId { get; init; }
        public required int      ChuteNo       { get; init; }
        public required DestType DestType      { get; init; }

        /// <summary>목적지 층(3D 소터·미할당 슈트=NULL). 층별 라우팅 키.</summary>
        public int? Floor { get; init; }

        /// <summary>
        /// route 키(층/레거시) → 전이 추적 상태. 관찰마다 라우팅이 산출한 route에 대해 lazily 생성.
        /// 관찰 루프의 순회 중 동시 갱신 안전(ConcurrentDictionary).
        /// </summary>
        public readonly ConcurrentDictionary<int, RouteState> Routes = new();
    }

    private readonly IChuteStatePushClient      _push;
    private readonly IDestinationStatusService  _status;
    private readonly ISorterGatewayRegistry     _sorterRegistry;
    private readonly ChuteCapacityService       _chuteCapacity;
    private readonly IDestinationControlService _control;
    private readonly IServiceScopeFactory       _scopeFactory;
    private readonly ChuteStatePushOptions      _opt;
    private readonly ILogger<DestinationStatusPusher> _log;

    // destination.id → 전이 추적 상태(기동 시 DB로 구성 + 런타임 신설 목적지 등록).
    private readonly ConcurrentDictionary<long, DestState> _states = new();

    // 소터 스냅샷 관찰 타이머 수명.
    private CancellationTokenSource? _cts;
    private Task?                    _observeTask;

    // 변화원 구독 여부(StopAsync에서 해제 — 멱등).
    private bool _subscribed;

    // 멱등 StopAsync 플래그(Interlocked) — 이중 호출 시 본문 1회만 실행.
    private int _stopped;

    public DestinationStatusPusher(
        IChuteStatePushClient      push,
        IDestinationStatusService  status,
        ISorterGatewayRegistry     sorterRegistry,
        ChuteCapacityService       chuteCapacity,
        IDestinationControlService control,
        IServiceScopeFactory       scopeFactory,
        IOptions<WcsOptions>       options,
        ILogger<DestinationStatusPusher> log)
    {
        _push           = push;
        _status         = status;
        _sorterRegistry = sorterRegistry;
        _chuteCapacity  = chuteCapacity;
        _control        = control;
        _scopeFactory   = scopeFactory;
        _opt            = options.Value.ChuteStatePush;
        _log            = log;
    }

    // ── IHostedService ───────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // DORMANT: 전 층 호스트 미설정(& 레거시 BaseUrl도 미설정) → 경고 후 전체 비활성.
        if (!_push.IsEnabled)
        {
            _log.LogWarning(
                "[IF-08푸시] RCS 층 호스트 미설정(Wcs:ChuteStatePush:FloorHosts / 레거시 BaseUrl) — " +
                "아웃바운드 수용상태 푸시 DORMANT(비활성). 운영 배포 시 층별 호스트만 설정하면 활성. " +
                "pause/resume·인바운드(IF-05/09/10)는 정상 동작.");
            return;
        }

        // ── 전 목적지(슈트+소터) 전이 추적 상태 구성 ────────────────────────────
        await BuildStatesFromDbAsync(cancellationToken).ConfigureAwait(false);

        // ── 변화원 ①·③ 구독(부트스트랩 발신 전에 구독해 기동 직후 전이도 포착) ────
        _chuteCapacity.OnChuteStateChanged += NotifyChuteChanged;   // ① 슈트 capacity 변화
        _control.OnTransition             += OnControlTransition;   // ③ 운영자 PAUSED/RESUMED 전이
        _subscribed = true;

        // ── CTS 생성(부트스트랩 발신 앞으로 재배치 — S-HARDENING-1 라이드얼롱) ────────
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // ── S-TWO-FLOOR-CONTROL C2 §4-B: 소터 콜드스타트 레지스터 클리어(S1)를 부트스트랩보다 먼저 대기 ──
        //   기동 첫 폴이 투입한 StartupClear가 처리 완료(레지스터 0)될 때까지 기다린 뒤 아래 부트스트랩 push를
        //   낸다 — "클리어 before push"(계약 S3/CC3). 잔류 목표층 기반 상태를 push하는 것을 구조적으로 차단.
        //   소터가 끝내 Online이 안 되면 설정 상한(StartupClearWaitMs) 경과 후 경고와 함께 진행(무한 대기 금지).
        await WaitForSorterStartupClearsAsync(_cts.Token).ConfigureAwait(false);

        // ── 부트스트랩: 기동 시 전 활성 목적지 현재 수용상태 route별 1회 발신(§라우팅 규칙 동일) ──
        //   Observe가 라우팅으로 route를 만들고(Acked=null → 무조건 1회 발신) 각 route를 pump. 이후엔 전이만.
        foreach (var st in _states.Values)
            Observe(st);

        // ── 관찰 루프 시작(변화원 ② 소터 Latest diff + 슈트 복구 하트비트) ─────────
        _observeTask = Task.Run(() => RunSorterObserveLoopAsync(_cts.Token));

        _log.LogInformation(
            "[IF-08푸시] 활성 — 목적지 {Count}개 전이 추적 시작(층 호스트 {Hosts} · 기동 스냅샷 발신 + 관찰 주기 {Ms}ms + 운영자 전이 구독)",
            _states.Count,
            _opt.IsLegacySingleHost ? "레거시 단일" : string.Join(",", _opt.HostByFloor.Keys),
            _opt.SorterObserveIntervalMs);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // 이중 호출 방어 — Interlocked로 본문을 1회만 실행(disposed CTS 재접근 방지).
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;

        // 변화원 구독 해제 — 종료 후 콜백/이벤트 유입 차단.
        if (_subscribed)
        {
            _chuteCapacity.OnChuteStateChanged -= NotifyChuteChanged;
            _control.OnTransition             -= OnControlTransition;
            _subscribed = false;
        }

        if (_cts is not null)
        {
            try { await _cts.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { /* 이미 dispose됨(teardown 경쟁) */ }
        }

        if (_observeTask is not null)
        {
            try { await _observeTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception) { }  // teardown 경쟁 예외 흡수(폴 루프 동형)
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _cts?.Dispose();
    }

    // ── 런타임 신설 목적지 등록(S-B2C-FACILITY) ────────────────────────────────
    //
    // 설비 관리 페이지가 만든 CHUTE 를 전이 추적 대상에 편입한다. DORMANT면 no-op. 이미 등록돼 있으면
    // no-op(멱등). 등록 직후 Observe 로 현재 수용상태 부트스트랩 발신(신규 목적지를 RCS 에 1회 알림).
    // floor는 층별 라우팅 키(신설 슈트가 층을 가지면 그 층 호스트로, NULL이면 층-무관 전 층 발신).
    public void RegisterDestination(long destinationId, int chuteNo, DestType destType, int? floor = null)
    {
        if (!_push.IsEnabled) return;

        var st = new DestState
        {
            DestinationId = destinationId,
            ChuteNo       = chuteNo,
            DestType      = destType,
            Floor         = floor,
        };
        // 이미 있으면 기존 유지(멱등) — 신규 추가 시에만 부트스트랩 Observe.
        if (_states.TryAdd(destinationId, st))
        {
            _log.LogInformation("[IF-08푸시] 런타임 목적지 등록 destId={Id} chuteNo={ChuteNo} type={Type} floor={Floor} — 수용상태 부트스트랩 발신",
                destinationId, chuteNo, destType, floor);
            Observe(st);
        }
    }

    // ── 변화원 ① 슈트 콜백 ─────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void NotifyChuteChanged(long destinationId)
    {
        if (!_push.IsEnabled) return;
        if (!_states.TryGetValue(destinationId, out var st)) return;
        Observe(st);
    }

    // ── 변화원 ③ 운영자 PAUSED/RESUMED 전이 ────────────────────────────────────
    private void OnControlTransition(DestinationTransition t)
    {
        if (!_push.IsEnabled) return;
        if (!_states.TryGetValue(t.DestinationId, out var st)) return;
        Observe(st);
    }

    // ── 변화원 ② 소터 스냅샷 관찰 루프 + 슈트 복구 하트비트 ─────────────────────
    //
    // 게이트웨이 폴링 스냅샷(bundle.Latest)을 주기적으로 관찰해 수용상태·CurFloor 전이를 감지한다.
    // 소터는 매 주기 Observe(명시 이벤트 없음 → 관찰이 유일 감지 수단 · CurFloor 전이도 포착).
    // 슈트는 route가 하나라도 미동기(Acked ≠ Computed)일 때만 재평가·재발신(복구 하트비트) — 이미
    //   전 route 동기된 슈트는 폴마다 재발신 0(폭주 금지). 층별 독립: 한 층 호스트만 stale여도 그 route만 재푸시.
    private async Task RunSorterObserveLoopAsync(CancellationToken ct)
    {
        int intervalMs = Math.Max(1, _opt.SorterObserveIntervalMs);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(intervalMs, ct).ConfigureAwait(false);

                foreach (var st in _states.Values)
                {
                    if (st.DestType == DestType.SORTER_3D)
                    {
                        Observe(st);   // 소터: 매 주기 스냅샷·CurFloor diff.
                        continue;
                    }

                    // 슈트: route 하나라도 미동기면 재평가·재발신(복구 하트비트). 전 route 동기면 skip.
                    //   (Acked==null = 부트스트랩 발신 실패분도 미동기로 간주해 복구.)
                    bool unsynced = false;
                    foreach (var rs in st.Routes.Values)
                    {
                        lock (rs.Gate)
                        {
                            if (!rs.Acked.HasValue || rs.Acked != rs.Computed) { unsynced = true; break; }
                        }
                    }
                    if (unsynced)
                        Observe(st);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                try { _log.LogError(ex, "[IF-08푸시] 관찰 루프 예외 — 다음 주기 재시도"); }
                catch { /* teardown 중 로거 disposed */ }
            }
        }
    }

    // ── 공통 전이 감지 진입점 ─────────────────────────────────────────────────
    //
    // 변화원 셋이 모두 이 경로로 수렴. 라우팅으로 (route,host,next_state) 목록을 산출 →
    // route별 락에서 Computed 갱신 → route별 발신 루프 기동. 단일 발신 소스·전이당 1회 멱등 보존.
    private void Observe(DestState st)
    {
        List<(int routeKey, string host, int nextState)> routes;
        try
        {
            routes = ResolveRoutes(st);
        }
        catch (Exception ex)
        {
            try { _log.LogError(ex, "[IF-08푸시] 라우팅 산출 예외 destId={DestId} — 관찰 1회 건너뜀", st.DestinationId); }
            catch { }
            return;
        }

        foreach (var (routeKey, host, nextState) in routes)
        {
            var rs = st.Routes.GetOrAdd(routeKey,
                static (k, arg) => new RouteState { RouteKey = k, Host = arg.host, Options = arg.opt },
                (host, opt: _opt));
            lock (rs.Gate)
            {
                rs.Computed = nextState;
            }
            _ = PumpAsync(st, rs);
        }
    }

    // ── 층별 라우팅 산출 — (route 키, 호스트, next_state) 목록(§라우팅 규칙) ──────
    //
    // accept = ComputeAccept(st) (Ready ∧ !Paused — 슈트/소터 동일 술어). next_state는 route별 산출.
    //   레거시 단일 호스트: 목적지 무관 단일 route(accept?3:2).
    //   층 모드:
    //     · 고정 슈트(Floor 비-NULL): 그 층 호스트 1곳(있으면). accept?3:2.
    //     · Floor==NULL 슈트: 설정 전 층에 같은 accept 값(층-무관).
    //     · 소터(SORTER_3D): 설정 전 층 각각 — (accept ∧ CurFloor==그 층)?3:2 (다른 층·오프라인·CurFloor 불명=2).
    private List<(int routeKey, string host, int nextState)> ResolveRoutes(DestState st)
    {
        var result = new List<(int, string, int)>();
        bool accept = ComputeAccept(st);

        // 레거시 단일 호스트 — 층 무관 단일 발신(구 동작 보존).
        if (_opt.IsLegacySingleHost)
        {
            result.Add((LegacyRouteKey, _opt.BaseUrl!, accept ? NextStateOpen : NextStatePause));
            return result;
        }

        var hostByFloor = _opt.HostByFloor;

        if (st.DestType == DestType.SORTER_3D)
        {
            // 소터: 설정된 전 층 호스트 — CurFloor 층만 accept 시 3, 나머지·오프라인·CurFloor 불명은 2.
            int? curFloor = TryGetSorterCurFloor(st.DestinationId);
            foreach (var (floor, host) in hostByFloor)
            {
                bool atThisFloor = accept && curFloor.HasValue && curFloor.Value == floor;
                result.Add((floor, host, atThisFloor ? NextStateOpen : NextStatePause));
            }
        }
        else // CHUTE
        {
            int nextState = accept ? NextStateOpen : NextStatePause;
            if (st.Floor is int f)
            {
                // 고정 슈트: 자기 층 호스트 1곳(미설정이면 no-op — 층별 DORMANT).
                if (hostByFloor.TryGetValue(f, out var host))
                    result.Add((f, host, nextState));
            }
            else
            {
                // 층 미할당 슈트: 층-무관 → 설정 전 층에 같은 accept 값(모순 없음).
                foreach (var (floor, host) in hostByFloor)
                    result.Add((floor, host, nextState));
            }
        }

        return result;
    }

    // ── 소터 현재 CurFloor(라우팅 3/2 판단용) ──────────────────────────────────
    // 번들 없음·오프라인이면 null(→ 전 층 2). online이면 스냅샷 CurFloor.
    private int? TryGetSorterCurFloor(long destinationId)
    {
        var bundle = _sorterRegistry.GetBundle(destinationId);
        if (bundle is null) return null;
        var snap = bundle.Latest;
        return snap.Online ? snap.CurFloor : (int?)null;
    }

    // ── 수용상태 합성(계약 SC-2): accept = Ready ∧ !Paused ─────────────────────
    //
    // S-TWO-FLOOR-CONTROL C3 항목2 — 소터 경량화(I-2 동형): 소터에 대해 무거운 Compute(→ ComputeSorterFull:
    //   cell/cell_assignment/sorter_command/piece 다중 집계 쿼리)를 **매 관찰 틱마다 호출하지 않는다**. accept 에
    //   실제로 쓰이는 것은 Ready ∧ !Paused 뿐이고 Full 은 미사용이므로, 스냅샷 기반 Ready(bundle.Latest +
    //   DepositDecider(순수, CurFloor 목표) — DB 무접촉) ∧ !IsPaused(destination Status/IsActive 단일 조회)로
    //   대체한다. 발신 결과는 **완전히 동일**하고 셀 집계 비용만 절감된다.
    //     · 기존 Compute().Ready(= ComputeSorter 의 decision.Ready) == DepositDecider.Decide(snap, snap.CurFloor,
    //       None).Ready — 동일 산출(번들 없음/오프라인이면 둘 다 false).
    //     · 기존 Compute().Paused == IsPaused(destId) — 동일 로직(destination Status/IsActive).
    //   → accept = Ready ∧ !Paused 가 경량화 전후 byte-identical(next_state 3/2·전이당 1회 멱등 불변).
    //   슈트(비소터)는 현행 유지 — ChuteCapacityService.GetHold(인메모리 hold, DB 집계 없음)라 이미 경량이다.
    private bool ComputeAccept(DestState st)
    {
        if (st.DestType == DestType.SORTER_3D)
        {
            var bundle = _sorterRegistry.GetBundle(st.DestinationId);
            if (bundle is null) return false;   // 번들 없음 = OFFLINE(ComputeSorter 와 동일: Ready=false → accept=false).

            var snap  = bundle.Latest;
            bool ready = DepositDecider.Decide(snap, snap.CurFloor, WcsHold.None).Ready;   // = Compute().Ready(운영상태).
            return ready && !_status.IsPaused(st.DestinationId);                            // ComputeSorterFull 스킵.
        }

        // 슈트: 현행 유지(ComputeChute — 인메모리 hold 기반이라 이미 경량).
        var r = _status.Compute(st.DestinationId, st.DestType);
        return r.Ready && !r.Paused;
    }

    // ── route별 전이당 1회 발신 루프 (동시성 멱등 핵심 · 층 호스트 독립) ────────
    //
    // per-route 락 + PushInFlight 플래그로 "전이당 정확히 1회"를 원자 보장(S-IF08 구조를 (dest,floor)로 확장):
    //   - 진입 시 락에서 (Computed != Acked) && !PushInFlight 일 때만 in-flight 클레임.
    //   - 클레임 실패(이미 in-flight)면 즉시 반환 — 진행 중 루프가 완료 후 재평가하므로 누락 0.
    //   - 발신은 락 밖에서(I/O). 성공 시 Acked=target, 실패 시 Acked 불변.
    //   - 완료 후 재평가: Computed != Acked면(발신 중 새 값 OR 실패 stale) — 성공-후-새전이만 즉시 재루프,
    //     실패는 종료(다음 관찰이 재푸시 — 복구 하트비트). 이 route(층 호스트)만 재시도(타 층 무영향).
    private async Task PumpAsync(DestState st, RouteState rs)
    {
        while (true)
        {
            int target;

            // ① 클레임 — 락에서 전이 여부 판정 + in-flight 원자 설정.
            lock (rs.Gate)
            {
                if (rs.PushInFlight) return;
                if (!rs.Computed.HasValue) return;   // 아직 미산출(방어).
                if (rs.Acked.HasValue && rs.Acked.Value == rs.Computed.Value) return;   // 전이 없음(폭주 금지).

                rs.PushInFlight = true;
                target = rs.Computed.Value;
            }

            bool ok;
            try
            {
                // ② 발신(락 밖 I/O — 재시도 포함). 이 route의 호스트로만 PUT(층 독립).
                //   rs 를 억제 게이트(IPushFailureLogThrottle)로 넘겨 재시도-소진 실패 로깅을 (route,next_state)
                //   단위로 억제(로깅만 — 재시도/재발신/반환값은 무영향).
                var token = _cts?.Token ?? CancellationToken.None;
                ok = await _push.PushAsync(
                    new ChuteStatePushPayload(new[] { st.ChuteNo }, new[] { target }), rs.Host, rs, token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                lock (rs.Gate) { rs.PushInFlight = false; }
                return;
            }
            catch (Exception ex)
            {
                try { _log.LogError(ex, "[IF-08푸시] 발신 루프 예외 destId={DestId} host={Host}", st.DestinationId, rs.Host); }
                catch { }
                ok = false;
            }

            // ③ 완료 — 락에서 Acked 갱신 + 재평가.
            lock (rs.Gate)
            {
                if (ok)
                {
                    rs.Acked = target;   // 성공만 Acked 갱신(실패면 stale 유지 → 다음 관찰 재푸시).
                    // S-IF08-PUSH-LOG-THROTTLE: 성공(복구) 시 억제 리셋 — 이후 재실패는 새 첫 실패로 1건 로깅.
                    //   같은 Gate 락 안이므로 OnFailure 의 check-and-set 와 원자 직렬화(비원자 check-then-act 없음).
                    rs.ResetFailureLogSuppression();
                }

                rs.PushInFlight = false;

                if (rs.Acked.HasValue && rs.Acked.Value == rs.Computed!.Value)
                    return;  // 동기화 완료 — 루프 종료.

                if (!ok)
                    return;  // 실패 → 다음 관찰(콜백·소터 관찰·운영자 전이·하트비트)이 재푸시.
                // ok==true && Computed!=Acked → 발신 중 새 전이 발생, 즉시 다음 전이 발신(continue while).
            }
        }
    }

    // ── S-TWO-FLOOR-CONTROL C2 §4-B: 소터 콜드스타트 클리어 완료 대기(클리어 before push — S3/CC3) ──
    //
    // 전 소터 번들의 StartupClearCompleted(첫 폴 StartupClear 처리 완료 시 완료)를 상한 내 대기한다.
    // 온라인 기동이면 통상 폴 주기 수 배 이내에 완료 → 부트스트랩 push가 클리어 뒤에 나간다. 소터가 끝내
    // Online이 안 되면(오프라인 기동) 상한 경과 후 경고와 함께 진행한다(무한 대기 금지 — 잔류 클리어 대상도
    // 없으므로 순서 위반 아님). 소터 0대면 즉시 반환. 대기 자체는 호스트 기동 스레드에서 await하지만 폴/쓰기
    // 컨슈머는 별도 태스크라 데드락 없다.
    private async Task WaitForSorterStartupClearsAsync(CancellationToken ct)
    {
        var bundles = _sorterRegistry.AllBundles;
        if (bundles.Count == 0) return;

        int waitMs = Math.Max(1, _opt.StartupClearWaitMs);
        var all = Task.WhenAll(bundles.Select(b => b.StartupClearCompleted));
        Task done;
        try
        {
            done = await Task.WhenAny(all, Task.Delay(waitMs, ct)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;   // 호스트 종료 — 부트스트랩 자체가 무의미(상위 취소 전파).
        }

        if (done == all)
            _log.LogInformation(
                "[IF-08푸시] 소터 {Count}대 콜드스타트 클리어 완료 확인 — 부트스트랩 push 진행(클리어 before push).",
                bundles.Count);
        else
            _log.LogWarning(
                "[IF-08푸시] 소터 콜드스타트 클리어 대기 타임아웃({Ms}ms) — 일부 소터 미완료(오프라인 기동?). " +
                "부트스트랩 push 진행.", waitMs);
    }

    // ── 기동 시 DB로 전이 추적 상태 구성 ──────────────────────────────────────
    private async Task BuildStatesFromDbAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();

        // 활성 목적지(슈트 + 소터) 전부 — chuteNo가 RCS로 보내는 키, Floor가 층별 라우팅 키.
        var dests = await db.Destinations
            .Where(d => d.IsActive)
            .Select(d => new { d.Id, d.ChuteNo, d.DestType, d.Floor })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        _states.Clear();
        foreach (var d in dests)
        {
            _states[d.Id] = new DestState
            {
                DestinationId = d.Id,
                ChuteNo       = d.ChuteNo,
                DestType      = d.DestType,
                Floor         = d.Floor,
            };
        }
    }
}
