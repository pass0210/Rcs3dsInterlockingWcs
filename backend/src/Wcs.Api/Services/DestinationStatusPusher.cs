using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wcs.Core;
using Wcs.Data;

namespace Wcs.Api;

// ════════════════════════════════════════════════════════════════════════════
// DestinationStatusPusher — IF-08 전이 감지 + 전이당 1회 푸시 파이프 (Scope C·D·F·G)
//
// RCS↔WCS 재설계 Phase 2:
//   목적지(슈트 + 3D 소터) ready가 전이(true↔false)할 때만 RcsPushClient로 1회 푸시.
//   ready 산출은 Phase 1 DestinationStatusService.Compute 재사용(새 판정 0 — Scope B).
//
//   변화원 둘이 공통 푸시 경로(ObserveAsync)로 수렴:
//     ① 슈트 ChuteCapacityService 상태 변화 — OnChuteChanged 콜백(IF-05 예약/IF-10 투입/비움).
//     ② 소터 폴링 스냅샷 변화 — 관찰 타이머가 주기적으로 bundle.Latest를 diff
//        (게이트웨이 본문 무변경 — Latest 관찰만, Scope D (a)).
//
//   전이 추적: chuteNo별 "직전 Computed ready"와 "RCS에 마지막으로 성공 알린 Acked ready"를
//   분리 보관(사용자 확정3). 푸시는 Computed != Acked일 때만 발생. 성공 시 Acked=Computed,
//   실패 시 Acked 불변(미알림 유지 → 다음 관찰/복구 시 재푸시).
//
//   동시성(Scope F·P3 교훈): 변화원 둘 + 재시도가 동시에 같은 destination을 갱신해도
//   per-destination 락 + in-flight 플래그로 "전이당 정확히 1회"(중복 0·누락 0)를 보장한다.
//   비원자 check-then-act 금지(P3 OFFLINE 전이당-1건 멱등 교훈).
//
//   부트스트랩(확정5): 기동 시 BaseUrl 설정되어 있으면 전 목적지 현재 ready 1회 스냅샷 푸시
//   후 이후 전이만. BaseUrl 미설정(확정4)이면 경고 후 전체 비활성(no-op).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 슈트 capacity 변화를 Pusher에 통지하는 콜백 인터페이스.
/// ChuteCapacityService(슈트 변화원)가 ready 경계를 넘길 수 있는 이벤트마다 호출.
/// Pusher가 Compute로 재산출해 전이만 푸시(무변화면 0건 — 폴마다 폭주 금지).
/// </summary>
public interface IDestinationChangeNotifier
{
    /// <summary>슈트 destination의 상태가 바뀌었을 수 있음 — 재평가·전이 시 푸시.</summary>
    void NotifyChuteChanged(long destinationId);
}

/// <summary>
/// IF-08 전이 감지·푸시 파이프 — IHostedService.
/// 슈트 콜백 + 소터 스냅샷 관찰 타이머 두 변화원을 공통 ObserveAsync로 수렴.
/// </summary>
public sealed class DestinationStatusPusher : IDestinationChangeNotifier, IHostedService, IAsyncDisposable
{
    // ── destination별 전이 추적 상태 ─────────────────────────────────────────
    private sealed class DestState
    {
        public required long DestinationId { get; init; }
        public required int  ChuteNo       { get; init; }
        public required DestType DestType   { get; init; }

        /// <summary>per-destination 직렬화 락 — 관찰·푸시 결정·Acked 갱신을 원자화(P3 교훈).</summary>
        public readonly object Gate = new();

        /// <summary>마지막으로 Compute가 산출한 ready(전이 비교 기준).</summary>
        public bool Computed;

        /// <summary>마지막으로 RCS에 성공적으로 알린 ready. null=아직 한 번도 안 보냄.</summary>
        public bool? Acked;

        /// <summary>현재 이 destination에 대한 푸시 루프가 진행 중인지(in-flight — 중복 발화 차단).</summary>
        public bool PushInFlight;
    }

    private readonly IRcsPushClient            _push;
    private readonly IDestinationStatusService _status;
    private readonly ISorterGatewayRegistry    _sorterRegistry;
    private readonly ChuteCapacityService      _chuteCapacity;
    private readonly IServiceScopeFactory      _scopeFactory;
    private readonly RcsPushOptions            _opt;
    private readonly ILogger<DestinationStatusPusher> _log;

    // destination.id → 전이 추적 상태(기동 시 DB로 구성, 이후 불변 키셋).
    private readonly Dictionary<long, DestState> _states = new();

    // 소터 스냅샷 관찰 타이머 수명.
    private CancellationTokenSource? _cts;
    private Task?                    _observeTask;

    // 슈트 변화원 구독 여부(StopAsync에서 해제 — 멱등).
    private bool _subscribed;

    // 멱등 StopAsync 플래그(Interlocked) — 이중 호출 시 본문 1회만 실행.
    private int _stopped;

    public DestinationStatusPusher(
        IRcsPushClient            push,
        IDestinationStatusService status,
        ISorterGatewayRegistry    sorterRegistry,
        ChuteCapacityService      chuteCapacity,
        IServiceScopeFactory      scopeFactory,
        IOptions<WcsOptions>      options,
        ILogger<DestinationStatusPusher> log)
    {
        _push           = push;
        _status         = status;
        _sorterRegistry = sorterRegistry;
        _chuteCapacity  = chuteCapacity;
        _scopeFactory   = scopeFactory;
        _opt            = options.Value.RcsPush;
        _log            = log;
    }

    // ── IHostedService ───────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // 사용자 확정4: BaseUrl 미설정 → 경고 후 전체 비활성(전이 추적·관찰 타이머 미기동).
        if (!_push.IsEnabled)
        {
            _log.LogWarning(
                "[IF-08푸시] RCS base URL 미설정(Wcs:RcsPush:BaseUrl) — 아웃바운드 푸시 비활성. " +
                "운영 배포 시 필수 설정. 인바운드(IF-05/09/10)는 정상 동작.");
            return;
        }

        // ── 전 목적지(슈트+소터) 전이 추적 상태 구성 ────────────────────────────
        await BuildStatesFromDbAsync(cancellationToken).ConfigureAwait(false);

        // ── 변화원 ① 슈트 콜백 구독(ChuteCapacityService 이벤트) ─────────────────
        // 부트스트랩 푸시 전에 구독해 기동 직후 발생하는 슈트 전이도 포착.
        _chuteCapacity.OnChuteStateChanged += NotifyChuteChanged;
        _subscribed = true;

        // ── 부트스트랩(확정5): 기동 시 전 목적지 현재 ready 1회 스냅샷 푸시 ───────
        // 각 destination의 현재 Compute 산출을 초기 전이로 간주(Acked=null → 무조건 1회 푸시).
        // 이후엔 ObserveAsync 전이만.
        foreach (var st in _states.Values)
        {
            var ready = _status.Compute(st.DestinationId, st.DestType).Ready;
            lock (st.Gate) { st.Computed = ready; }
            _ = PumpAsync(st);  // 비동기 푸시 루프 시작(전이당 1회·복구 재푸시 포함).
        }

        // ── 소터 스냅샷 관찰 타이머 시작(변화원 ② — Latest diff) ─────────────────
        _cts         = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _observeTask = Task.Run(() => RunSorterObserveLoopAsync(_cts.Token));

        _log.LogInformation(
            "[IF-08푸시] 활성 — 목적지 {Count}개 전이 추적 시작(기동 스냅샷 푸시 + 소터 관찰 주기 {Ms}ms)",
            _states.Count, _opt.SorterObserveIntervalMs);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // 이중 호출(IHostedService.StopAsync 후 컨테이너 DisposeAsync 내부 StopAsync 재진입) 방어 —
        // Interlocked로 본문을 1회만 실행(disposed CTS 재접근 방지 — PlcPollingService 동형).
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;

        // 슈트 변화원 구독 해제 — 종료 후 콜백 유입 차단.
        if (_subscribed)
        {
            _chuteCapacity.OnChuteStateChanged -= NotifyChuteChanged;
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

    // ── 변화원 ① 슈트 콜백 ─────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void NotifyChuteChanged(long destinationId)
    {
        // 비활성(BaseUrl 미설정)이거나 미등록 destination이면 무시(no-op).
        if (!_push.IsEnabled) return;
        if (!_states.TryGetValue(destinationId, out var st)) return;

        Observe(st);
    }

    // ── 변화원 ② 소터 스냅샷 관찰 루프 ────────────────────────────────────────
    //
    // 게이트웨이 폴링 스냅샷(bundle.Latest)을 주기적으로 관찰해 ready 전이를 감지한다.
    // 게이트웨이 본문은 무변경(Latest 읽기만 — Scope D (a)). ready 전이가 없으면 푸시 0건
    // (Observe가 Computed==Acked면 푸시 안 함 — 폴마다 폭주 금지).
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
                    if (st.DestType != DestType.SORTER_3D) continue;
                    Observe(st);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // 관찰 루프 예외가 루프를 죽이지 않도록 흡수(로깅은 유지 — Fail-Loud).
                // 다음 주기에 재시도(이벤트 영구 손실 방지 — P3 핸들러 교훈과 동형).
                try { _log.LogError(ex, "[IF-08푸시] 소터 관찰 루프 예외 — 다음 주기 재시도"); }
                catch { /* teardown 중 로거 disposed */ }
            }
        }
    }

    // ── 공통 전이 감지 진입점 ─────────────────────────────────────────────────
    //
    // 변화원 둘(슈트 콜백·소터 관찰)이 모두 이 경로로 수렴.
    // Compute로 현재 ready 재산출 → per-dest 락에서 Computed 갱신 → 전이면 푸시 루프 기동.
    private void Observe(DestState st)
    {
        bool ready;
        try
        {
            ready = _status.Compute(st.DestinationId, st.DestType).Ready;
        }
        catch (Exception ex)
        {
            // Compute 예외(예: 일시적 DB/스냅샷 이상)는 관찰 1회를 건너뛴다(다음 관찰에서 재평가).
            try { _log.LogError(ex, "[IF-08푸시] ready 산출 예외 destId={DestId} — 관찰 1회 건너뜀", st.DestinationId); }
            catch { }
            return;
        }

        lock (st.Gate)
        {
            st.Computed = ready;
        }
        _ = PumpAsync(st);
    }

    // ── 전이당 1회 푸시 루프 (동시성 멱등 핵심 — P3 교훈) ───────────────────────
    //
    // per-destination 락 + PushInFlight 플래그로 "전이당 정확히 1회"를 원자 보장:
    //   - 진입 시 락에서 (Computed != Acked) && !PushInFlight 일 때만 in-flight 클레임.
    //     두 변화원이 동시에 같은 전이를 봐도 클레임은 한 쪽만 성공(중복 0).
    //   - 클레임 실패(이미 in-flight)면 즉시 반환 — 진행 중 루프가 완료 후 재평가하므로 누락 0.
    //   - 푸시는 락 밖에서(I/O — 락 보유 중 await 금지). 성공 시 Acked=target, 실패 시 Acked 불변.
    //   - 완료 후 락에서 재평가: Computed != Acked면(푸시 중 새 값 도착 OR 실패로 stale) 루프 계속.
    //     → 실패 시 Acked가 Computed와 달라 다음 클레임이 재시도(복구 재푸시·확정3).
    private async Task PumpAsync(DestState st)
    {
        while (true)
        {
            bool target;
            string ts;

            // ① 클레임 — 락에서 전이 여부 판정 + in-flight 원자 설정.
            lock (st.Gate)
            {
                if (st.PushInFlight)
                    return;  // 진행 중 루프가 완료 후 재평가 → 누락 없음.

                if (st.Acked.HasValue && st.Acked.Value == st.Computed)
                    return;  // 전이 없음(현재값을 이미 성공 알림) — 푸시 안 함(폭주 금지).

                // 전이 클레임 — 이 시점의 Computed를 목표값으로 고정.
                st.PushInFlight = true;
                target = st.Computed;
            }

            ts = FormatTimeStamp(DateTimeOffset.Now);
            bool ok;
            try
            {
                // ② 푸시(락 밖 I/O — 재시도 포함). CTS로 종료 시 취소 전파.
                var token = _cts?.Token ?? CancellationToken.None;
                ok = await _push.PushAsync(
                    new DestinationStatusPushPayload(st.ChuteNo, target, ts), token)
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
                try { _log.LogError(ex, "[IF-08푸시] 푸시 루프 예외 destId={DestId}", st.DestinationId); }
                catch { }
                ok = false;
            }

            // ③ 완료 — 락에서 Acked 갱신 + 재평가.
            lock (st.Gate)
            {
                if (ok)
                    st.Acked = target;   // 성공 — 이 값으로 RCS 동기화 완료(확정3: 성공만 Acked 갱신).
                // 실패면 Acked 불변 — 미알림 상태 유지(다음 관찰/복구 시 재푸시).

                st.PushInFlight = false;

                // 재평가: Computed가 Acked와 다르면(푸시 중 새 전이 OR 실패로 stale) 계속.
                if (st.Acked.HasValue && st.Acked.Value == st.Computed)
                    return;  // 동기화 완료 — 루프 종료.

                // 실패 직후 즉시 재루프하면 백오프 없이 바쁜 재시도가 될 수 있다.
                // 실패 시(ok==false)는 다음 "관찰"(슈트 콜백·소터 관찰 주기)이 재푸시를 유도하도록
                // 루프를 종료한다 — Computed가 Acked와 다른 상태로 남아 다음 Observe→Pump가 재시도.
                // (성공했는데 Computed가 또 바뀐 경우만 즉시 재루프 — 그건 진짜 새 전이.)
                if (!ok)
                    return;
                // ok==true && Computed!=Acked → 푸시 중 새 전이 발생, 즉시 다음 전이 푸시(continue while).
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

    // ── 와이어 시간 포맷(기존 와이어 포맷과 일관 — "yyyy-MM-dd HH:mm:ss") ───────
    private static string FormatTimeStamp(DateTimeOffset at) =>
        at.ToString("yyyy-MM-dd HH:mm:ss");
}
