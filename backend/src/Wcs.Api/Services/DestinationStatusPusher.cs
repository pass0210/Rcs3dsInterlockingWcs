using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wcs.Core;
using Wcs.Data;

namespace Wcs.Api;

// ════════════════════════════════════════════════════════════════════════════
// DestinationStatusPusher — 목적지 수용상태 전이 감지 + 전이당 1회 푸시 (S-IF08-READY-PUSH)
//
// 확정 와이어(UpdateChuteState) 단일 채널로 통합된 **목적지당 단일 발신 소스**:
//   목적지(슈트 + 3D 소터)의 "수용상태"가 전이할 때만 IChuteStatePushClient로 1회 푸시.
//   페이로드 = {chute_numbers:[ChuteNo], next_states:[3|2]} (snake_case, 전이당 단건).
//     · 3 = 받을 수 있음 / 2 = 받을 수 없음.
//
//   ★ 수용상태 합성(계약 SC-2) — DestinationStatusService.Compute를 재사용(새 판정 0):
//     accept = Compute().Ready ∧ !Compute().Paused.
//       - 슈트(CHUTE): Compute().Ready = 비만재 ∧ 비정지 이므로 accept = 비만재 ∧ 비정지(만재·정지가 발신에 반영).
//       - 소터(SORTER_3D): Compute().Ready = 운영 ready(online·정렬·Ready=1, paused·SorterFull 제외)이므로
//         accept = 운영 ready ∧ !paused. 즉 paused를 다시 접어 넣고 **셀 만재(SorterFull)는 제외**한다
//         (만재는 IF-05 dispatch에서만 차단 — 2단계 게이트 분리).
//     `Ready ∧ !Paused`는 슈트에도 정합하다(슈트의 Ready가 이미 !paused를 포함하므로 !Paused를 추가로
//      AND해도 동일) — 슈트/소터를 하나의 술어로 균일 발신한다.
//
//   변화원 셋이 공통 발신 경로(Observe→PumpAsync)로 수렴(단일 소스 — 이중/모순 발신 구조적 불가):
//     ① 슈트 ChuteCapacityService 상태 변화 — OnChuteStateChanged 콜백(IF-05 예약/IF-10 투입/비움/정지).
//     ② 소터 폴링 스냅샷 변화 — 관찰 타이머가 주기적으로 bundle.Latest를 diff(게이트웨이 본문 무변경).
//        소터 분류 사이클(Ready 1↔0)·정렬·오프라인 전이는 명시 이벤트가 없어 스냅샷 관찰이 유일 감지 수단.
//     ③ 운영자 PAUSED/RESUMED 전이 — DestinationControlService.OnTransition(실제 전이에서만 발화).
//        슈트·소터 모두 동일 훅으로 균일 처리 — 같은 chuteNo에 대해 accept 하나로 접혀 발신되므로
//        (한쪽 3·다른 쪽 2 같은) 모순 발신이 물리적으로 불가능하다.
//
//   전이 추적: chuteNo별 "직전 Computed accept"와 "RCS에 마지막으로 성공 알린 Acked accept"를
//   분리 보관. 푸시는 Computed != Acked일 때만 발생(값이 같은 관찰은 미발신 — 폴마다 폭주 0).
//   성공 시 Acked=Computed, 실패 시 Acked 불변(미알림 유지 → 다음 관찰/복구 시 재푸시).
//
//   동시성: 변화원 셋 + 재시도가 동시에 같은 destination을 갱신해도 per-destination 락 + in-flight
//   플래그로 "전이당 정확히 1회"(중복 0·누락 0)를 보장한다(비원자 check-then-act 금지).
//
//   관심사 분리: 이 서비스는 "전이 감지 → 1건 발신 요청"만 책임진다. "PUT 1건 전송 + 지수 백오프
//   재시도 + Fail-Loud"는 IChuteStatePushClient가 책임(ChuteStatePushClient).
//
//   부트스트랩: 기동 시 BaseUrl 설정되어 있으면 전 목적지 현재 수용상태를 목적지당 1회 발신 후
//   이후 전이만. BaseUrl 미설정(DORMANT)이면 경고 후 전체 비활성(구독 안 함·HTTP 시도 0·크래시 0).
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
/// 목적지 수용상태 전이 감지·발신 파이프(목적지당 단일 소스) — IHostedService.
/// 슈트 콜백 + 소터 스냅샷 관찰 타이머 + 운영자 전이 이벤트 세 변화원을 공통 Observe로 수렴.
/// </summary>
public sealed class DestinationStatusPusher : IDestinationChangeNotifier, IHostedService, IAsyncDisposable
{
    // 계약 상태값(UpdateChuteState next_state) — LOCKED.
    private const int NextStateOpen  = 3;   // 수용 가능(accept)  → Manual-open
    private const int NextStatePause = 2;   // 수용 불가          → Pause

    // ── destination별 전이 추적 상태 ─────────────────────────────────────────
    private sealed class DestState
    {
        public required long DestinationId { get; init; }
        public required int  ChuteNo       { get; init; }
        public required DestType DestType   { get; init; }

        /// <summary>per-destination 직렬화 락 — 관찰·발신 결정·Acked 갱신을 원자화.</summary>
        public readonly object Gate = new();

        /// <summary>마지막으로 Compute가 산출한 수용상태(accept — 전이 비교 기준).</summary>
        public bool Computed;

        /// <summary>마지막으로 RCS에 성공적으로 알린 accept. null=아직 한 번도 안 보냄.</summary>
        public bool? Acked;

        /// <summary>현재 이 destination에 대한 발신 루프가 진행 중인지(in-flight — 중복 발화 차단).</summary>
        public bool PushInFlight;
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
    // ConcurrentDictionary — 관찰 루프의 foreach 순회 중 RegisterDestination 동시 add 안전
    // (S-B2C-FACILITY: 설비 관리 페이지가 만든 슈트를 런타임 등록해 pause/resume push 를 가능케 함).
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
        // DORMANT: BaseUrl 미설정 → 경고 후 전체 비활성(전이 추적·관찰 타이머·구독 미기동).
        if (!_push.IsEnabled)
        {
            _log.LogWarning(
                "[IF-08푸시] RCS base URL 미설정(Wcs:ChuteStatePush:BaseUrl) — 아웃바운드 수용상태 푸시 DORMANT(비활성). " +
                "운영 배포 시 이 한 값만 설정하면 활성. pause/resume·인바운드(IF-05/09/10)는 정상 동작.");
            return;
        }

        // ── 전 목적지(슈트+소터) 전이 추적 상태 구성 ────────────────────────────
        await BuildStatesFromDbAsync(cancellationToken).ConfigureAwait(false);

        // ── 변화원 ①·③ 구독(부트스트랩 발신 전에 구독해 기동 직후 전이도 포착) ────
        _chuteCapacity.OnChuteStateChanged += NotifyChuteChanged;   // ① 슈트 capacity 변화
        _control.OnTransition             += OnControlTransition;   // ③ 운영자 PAUSED/RESUMED 전이
        _subscribed = true;

        // ── CTS 생성(부트스트랩 발신 앞으로 재배치 — S-HARDENING-1 라이드얼롱) ────────
        // 종전엔 부트스트랩 루프 뒤에서 생성돼 부트스트랩 push가 CancellationToken.None으로 나갔다.
        // 발신 앞에서 생성하면 부트스트랩 push도 _cts.Token으로 취소 가능(StopAsync 경쟁 시 즉시 종료).
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // ── 부트스트랩: 기동 시 전 목적지 현재 수용상태 1회 발신 ──────────────────
        // 각 destination의 현재 accept를 초기 전이로 간주(Acked=null → 무조건 1회 발신). 이후엔 전이만.
        foreach (var st in _states.Values)
        {
            var accept = ComputeAccept(st);
            lock (st.Gate) { st.Computed = accept; }
            _ = PumpAsync(st);
        }

        // ── 관찰 루프 시작(변화원 ② 소터 Latest diff + 슈트 복구 하트비트) ─────────
        _observeTask = Task.Run(() => RunSorterObserveLoopAsync(_cts.Token));

        _log.LogInformation(
            "[IF-08푸시] 활성 — 목적지 {Count}개 전이 추적 시작(기동 스냅샷 발신 + 관찰 주기 {Ms}ms[소터 스냅샷 diff + 슈트 미동기 복구 하트비트] + 운영자 전이 구독)",
            _states.Count, _opt.SorterObserveIntervalMs);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // 이중 호출(IHostedService.StopAsync 후 컨테이너 DisposeAsync 내부 StopAsync 재진입) 방어 —
        // Interlocked로 본문을 1회만 실행(disposed CTS 재접근 방지 — PlcPollingService 동형).
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
    // 설비 관리 페이지가 만든 CHUTE 를 전이 추적 대상에 편입한다. 기동 시 BuildStatesFromDb 는
    // 그 시점 DB 목적지만 등록하므로, 런타임 신설 목적지는 이 메서드로 등록하지 않으면 pause/resume
    // 전이(OnControlTransition)·capacity 변화(NotifyChuteChanged)가 "미등록 → 무시"로 드롭돼
    // IF-08 push 가 나가지 않는다.
    //
    // DORMANT(BaseUrl 미설정)면 no-op(추적 자체가 비활성). 이미 등록돼 있으면 no-op(멱등).
    // 등록 직후 Observe 로 현재 수용상태 부트스트랩 발신(신규 목적지를 RCS 에 1회 알림).
    // ⚠ 호출 전 capacity(ChuteCapacityService)에 먼저 등록돼 있어야 ComputeAccept 가 올바른 값을
    //   산출한다(GetHold 미등록 → Paused 오분류). 설비 서비스가 순서를 보장한다.
    public void RegisterDestination(long destinationId, int chuteNo, DestType destType)
    {
        if (!_push.IsEnabled) return;

        var st = new DestState
        {
            DestinationId = destinationId,
            ChuteNo       = chuteNo,
            DestType      = destType,
        };
        // 이미 있으면 기존 유지(멱등) — 신규 추가 시에만 부트스트랩 Observe.
        if (_states.TryAdd(destinationId, st))
        {
            _log.LogInformation("[IF-08푸시] 런타임 목적지 등록 destId={Id} chuteNo={ChuteNo} type={Type} — 수용상태 부트스트랩 발신",
                destinationId, chuteNo, destType);
            Observe(st);
        }
    }

    // ── 변화원 ① 슈트 콜백 ─────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void NotifyChuteChanged(long destinationId)
    {
        // 비활성(BaseUrl 미설정)이거나 미등록 destination이면 무시(no-op).
        if (!_push.IsEnabled) return;
        if (!_states.TryGetValue(destinationId, out var st)) return;

        Observe(st);
    }

    // ── 변화원 ③ 운영자 PAUSED/RESUMED 전이 ────────────────────────────────────
    //
    // DestinationControlService에서 동기 발화된다(운영자 O2/O3 요청 스레드). 절대 블로킹하지 않는다 —
    // Observe→PumpAsync가 fire-and-forget(클라이언트 내부 재시도). Compute가 DB Status를 직접 읽으므로
    // Target 값을 별도로 소비하지 않고 재평가만 트리거한다(슈트·소터 동일 경로).
    private void OnControlTransition(DestinationTransition t)
    {
        if (!_push.IsEnabled) return;
        if (!_states.TryGetValue(t.DestinationId, out var st)) return;

        Observe(st);
    }

    // ── 변화원 ② 소터 스냅샷 관찰 루프 + 슈트 복구 하트비트 ─────────────────────
    //
    // 게이트웨이 폴링 스냅샷(bundle.Latest)을 주기적으로 관찰해 수용상태 전이를 감지한다.
    // 게이트웨이 본문은 무변경(Latest 읽기만). 전이가 없으면 발신 0건
    // (Observe가 Computed==Acked면 발신 안 함 — 폴마다 폭주 금지).
    //
    // ★ 슈트 복구 하트비트(S-HARDENING-1 항목 1) — 관찰 주기를 재사용(신규 상수 0, 절대규칙 #7):
    //   소터는 분류 사이클(Ready 1↔0)·정렬·오프라인에 명시 이벤트가 없어 **매 주기** 스냅샷을 관찰해야
    //   한다. 슈트는 capacity 이벤트/운영자 전이가 있어 평시엔 관찰이 불필요하나, push가 재시도 소진으로
    //   실패(Acked ≠ Computed)한 슈트는 후속 이벤트 없이 stale로 남는다 — 특히 만재 슈트는 그 이벤트가
    //   오지 않아 RCS가 "받을 수 있음"으로 무기한 오인한다. 그래서 **미동기 슈트만** 주기적으로 재평가·
    //   재발신해 자율 복구한다.
    //   S-IF08 성질 보존:
    //     · 동기 완료(Acked == Computed) 슈트는 Observe를 호출하지 않는다 → 폴마다 재발신 0(폭주 금지).
    //     · Observe → PumpAsync 단일 경로로 수렴 → 별도 병렬 발신 경로/이중 소스 재도입 0.
    //     · 발신값은 여전히 단일 술어 ComputeAccept 산출 → 같은 chuteNo 모순 발신 불가.
    //     · per-dest Gate 락 + PushInFlight 클레임 그대로 경유 → 전이당 1회 멱등 불변.
    //   슈트 Compute는 인메모리 GetHold 기반이라 미동기 슈트 재평가 비용은 ~0.
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
                        // 소터: 매 주기 스냅샷 diff 관찰(명시 이벤트 없음 → 관찰이 유일 감지 수단).
                        Observe(st);
                        continue;
                    }

                    // 슈트: 미동기(Acked ≠ Computed)일 때만 재평가·재발신(복구 하트비트).
                    //   이미 동기된 슈트는 건드리지 않는다 — 무변화 폴 재발신 0.
                    //   (Acked==null = 부트스트랩 발신 실패분도 미동기로 간주해 복구.)
                    bool unsynced;
                    lock (st.Gate)
                    {
                        unsynced = !st.Acked.HasValue || st.Acked.Value != st.Computed;
                    }
                    if (unsynced)
                        Observe(st);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // 관찰 루프 예외가 루프를 죽이지 않도록 흡수(로깅은 유지 — Fail-Loud).
                // 다음 주기에 재시도(이벤트 영구 손실 방지).
                try { _log.LogError(ex, "[IF-08푸시] 관찰 루프 예외 — 다음 주기 재시도"); }
                catch { /* teardown 중 로거 disposed */ }
            }
        }
    }

    // ── 공통 전이 감지 진입점 ─────────────────────────────────────────────────
    //
    // 변화원 셋(슈트 콜백·소터 관찰·운영자 전이)이 모두 이 경로로 수렴.
    // Compute로 현재 accept 재산출 → per-dest 락에서 Computed 갱신 → 전이면 발신 루프 기동.
    private void Observe(DestState st)
    {
        bool accept;
        try
        {
            accept = ComputeAccept(st);
        }
        catch (Exception ex)
        {
            // Compute 예외(예: 일시적 DB/스냅샷 이상)는 관찰 1회를 건너뛴다(다음 관찰에서 재평가).
            try { _log.LogError(ex, "[IF-08푸시] 수용상태 산출 예외 destId={DestId} — 관찰 1회 건너뜀", st.DestinationId); }
            catch { }
            return;
        }

        lock (st.Gate)
        {
            st.Computed = accept;
        }
        _ = PumpAsync(st);
    }

    // ── 수용상태 합성(계약 SC-2): accept = Ready ∧ !Paused ─────────────────────
    //   슈트: Ready=비만재∧비정지 → accept=비만재∧비정지(만재·정지 반영).
    //   소터: Ready=운영 ready(paused·SorterFull 제외) → accept=운영 ready∧!paused(SorterFull 제외·paused 접음).
    private bool ComputeAccept(DestState st)
    {
        var r = _status.Compute(st.DestinationId, st.DestType);
        return r.Ready && !r.Paused;
    }

    // ── 전이당 1회 발신 루프 (동시성 멱등 핵심) ─────────────────────────────────
    //
    // per-destination 락 + PushInFlight 플래그로 "전이당 정확히 1회"를 원자 보장:
    //   - 진입 시 락에서 (Computed != Acked) && !PushInFlight 일 때만 in-flight 클레임.
    //     세 변화원이 동시에 같은 전이를 봐도 클레임은 한 쪽만 성공(중복 0).
    //   - 클레임 실패(이미 in-flight)면 즉시 반환 — 진행 중 루프가 완료 후 재평가하므로 누락 0.
    //   - 발신은 락 밖에서(I/O — 락 보유 중 await 금지). 성공 시 Acked=target, 실패 시 Acked 불변.
    //   - 완료 후 락에서 재평가: Computed != Acked면(발신 중 새 값 도착 OR 실패로 stale) 루프 계속.
    //     → 실패 시 Acked가 Computed와 달라 다음 클레임이 재시도(복구 재푸시).
    private async Task PumpAsync(DestState st)
    {
        while (true)
        {
            bool target;

            // ① 클레임 — 락에서 전이 여부 판정 + in-flight 원자 설정.
            lock (st.Gate)
            {
                if (st.PushInFlight)
                    return;  // 진행 중 루프가 완료 후 재평가 → 누락 없음.

                if (st.Acked.HasValue && st.Acked.Value == st.Computed)
                    return;  // 전이 없음(현재값을 이미 성공 알림) — 발신 안 함(폭주 금지).

                // 전이 클레임 — 이 시점의 Computed를 목표값으로 고정.
                st.PushInFlight = true;
                target = st.Computed;
            }

            int nextState = target ? NextStateOpen : NextStatePause;
            bool ok;
            try
            {
                // ② 발신(락 밖 I/O — 재시도 포함). CTS로 종료 시 취소 전파.
                //    전이당 길이-1 단건 배열(계약 구조 유지 · chute_numbers[i] ↔ next_states[i]).
                var token = _cts?.Token ?? CancellationToken.None;
                ok = await _push.PushAsync(
                    new ChuteStatePushPayload(new[] { st.ChuteNo }, new[] { nextState }), token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 종료 — in-flight 해제하고 종료(미알림 유지, 재시작 시 부트스트랩이 재푸시).
                lock (st.Gate) { st.PushInFlight = false; }
                return;
            }
            catch (Exception ex)
            {
                // PushAsync는 내부에서 false로 수렴하지만, 방어적으로 미관찰 예외 0 보장.
                try { _log.LogError(ex, "[IF-08푸시] 발신 루프 예외 destId={DestId}", st.DestinationId); }
                catch { }
                ok = false;
            }

            // ③ 완료 — 락에서 Acked 갱신 + 재평가.
            lock (st.Gate)
            {
                if (ok)
                    st.Acked = target;   // 성공 — 이 값으로 RCS 동기화 완료(성공만 Acked 갱신).
                // 실패면 Acked 불변 — 미알림 상태 유지(다음 관찰/복구 시 재푸시).

                st.PushInFlight = false;

                // 재평가: Computed가 Acked와 다르면(발신 중 새 전이 OR 실패로 stale) 계속.
                if (st.Acked.HasValue && st.Acked.Value == st.Computed)
                    return;  // 동기화 완료 — 루프 종료.

                // 실패 직후 즉시 재루프하면 백오프 없이 바쁜 재시도가 될 수 있다.
                // 실패 시(ok==false)는 다음 "관찰"(슈트 콜백·소터 관찰 주기·운영자 전이)이 재푸시를
                // 유도하도록 루프를 종료한다 — Computed가 Acked와 다른 상태로 남아 다음 Observe→Pump가 재시도.
                // (성공했는데 Computed가 또 바뀐 경우만 즉시 재루프 — 그건 진짜 새 전이.)
                if (!ok)
                    return;
                // ok==true && Computed!=Acked → 발신 중 새 전이 발생, 즉시 다음 전이 발신(continue while).
            }
        }
    }

    // ── 기동 시 DB로 전이 추적 상태 구성 ──────────────────────────────────────
    private async Task BuildStatesFromDbAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();

        // 활성 목적지(슈트 + 소터) 전부 — chuteNo가 RCS로 보내는 키(슈트·소터 동일).
        var dests = await db.Destinations
            .Where(d => d.IsActive)
            .Select(d => new { d.Id, d.ChuteNo, d.DestType })
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
            };
        }
    }
}
