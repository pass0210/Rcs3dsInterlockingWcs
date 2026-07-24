using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Wcs.Core;
using Wcs.Data;

namespace Wcs.Api;

// ════════════════════════════════════════════════════════════════════════════
// 인덕션 기반 2층 제어 (S-TWO-FLOOR-CONTROL 서브 스프린트 A) — 관심사 분리:
//   ① 큐(SorterPendingFloorQueues)     = 상태(소터별 pending-floor FIFO).
//   ② 판정(Wcs.Core.DepositDecider)     = 순수 게이트(층 F 파라미터화).
//   ③ 관측 루프(SorterFloorReturnService) = 트리거(TgtFloor==0 관측 → 큐 머리 F 기입, 도착 시 pop).
//
// 절대규칙:
//   #1 TgtFloor 기입은 소터별 단일 쓰기 큐(bundle.EnqueueSetTgtFloorAsync)로만 — 직접 Modbus 0.
//   #2 TgtFloor 게이트(TgtFloor==0 && (CurFloor!=F||Ready==0)), 진행중(≠0)엔 미기입(핑퐁 차단).
//   #3 WCS는 TgtFloor를 클리어하지 않는다(PLC가 분류 시작 시 클리어).
//   #7 관측 주기는 appsettings(Wcs:SorterFloorReturn:ObserveIntervalMs).
//   #8 층 파생·게이트는 순수 함수(Wcs.Core) — 이 서비스는 I/O·상태 트리거만.
// ════════════════════════════════════════════════════════════════════════════

// ── 소터별 pending-floor FIFO 큐 (상태 컴포넌트 — 싱글톤) ─────────────────────

/// <summary>
/// 소터(destination.id)별 pending-floor FIFO 큐. IF-05가 목표 층 F를 순서대로 enqueue하고,
/// 관측 루프(SorterFloorReturnService)가 유일 소비자로 머리(head)를 관측·소비한다.
///
/// 동시성: 다중 AGV IF-05가 동시에 Enqueue(생산자 다수)하고, 관측 루프 1개가 TryPeek/TryPop(소비자 1개).
///   <see cref="ConcurrentQueue{T}"/>가 이 다대일 패턴에 안전하며, 소비자가 하나뿐이므로
///   TryPeek→TryPop 사이 다른 소비자의 개입이 없다(관측 루프 단일 스레드 전제).
/// </summary>
public sealed class SorterPendingFloorQueues
{
    private readonly ConcurrentDictionary<long, ConcurrentQueue<int>> _queues = new();

    /// <summary>소터 destId 큐 꼬리에 목표 층 F를 추가(IF-05 순서 보존). 연속 동일층도 매 피스 1건씩 stack.</summary>
    public void Enqueue(long destinationId, int floor)
    {
        var q = _queues.GetOrAdd(destinationId, static _ => new ConcurrentQueue<int>());
        q.Enqueue(floor);
    }

    /// <summary>큐 머리(head) 층을 제거하지 않고 조회. 비어 있으면 false.</summary>
    public bool TryPeek(long destinationId, out int floor)
    {
        if (_queues.TryGetValue(destinationId, out var q))
            return q.TryPeek(out floor);
        floor = 0;
        return false;
    }

    /// <summary>큐 머리(head) 층을 제거하며 반환(소비). 비어 있으면 false. 관측 루프 단일 소비자 전용.</summary>
    public bool TryPop(long destinationId, out int floor)
    {
        if (_queues.TryGetValue(destinationId, out var q))
            return q.TryDequeue(out floor);
        floor = 0;
        return false;
    }

    /// <summary>소터 destId 큐 길이(테스트·진단).</summary>
    public int Count(long destinationId) =>
        _queues.TryGetValue(destinationId, out var q) ? q.Count : 0;

    /// <summary>소터 destId 큐의 현재 층 순서 스냅샷(테스트·진단 — FIFO 순서 검증).</summary>
    public IReadOnlyList<int> Snapshot(long destinationId) =>
        _queues.TryGetValue(destinationId, out var q) ? q.ToArray() : Array.Empty<int>();
}

// ── 관측 루프 (트리거 — IHostedService) ──────────────────────────────────────

/// <summary>
/// 각 소터 스냅샷의 <c>TgtFloor==0</c>을 주기적으로 관측해, 소터별 pending-floor 큐 머리 층 F를
/// DepositDecider 게이트로 판정하고 통과 시 소터별 단일 쓰기 큐(SetTgtFloor)로 기입한다.
///
/// 폐루프 소비(I-1 재설계 2026-07-23 — 도착-pop → 분류사이클-pop):
///   · pop 단위 = **분류 사이클 완료**(Ready 1→0→1, 그 피스가 실제 분류됨)마다 큐 머리 1건. 도착 즉시가 아님.
///     큐 [A,A,B]에서 A 2건이 모두 분류 완료되기 전엔 소터가 B로 이동하지 않는다(2번째 A-AGV 고립 방지).
///   · 분류-제자리(Ready 1→0 시점 CurFloor == 완료 시점 CurFloor)만 pop. 정렬 이동(CurFloor 변화)에 의한
///     Ready 0→1은 피스 소비가 아니므로 pop하지 않는다.
///   · 미정렬(머리 F != CurFloor)이면 **유휴(Ready=1)일 때만** 게이트로 F 기입해 그 층으로 정렬(분류 중
///     선기입 금지 — 분류+이동 융합으로 사이클 감지가 깨지는 것 방지).
///   · OFFLINE(스냅샷 불신)이면 pop·기입 모두 생략. PAUSED면 기입 생략(hold — DepositDecider 차단).
///     FULL(셀 만재)은 정렬 기입을 막지 않는다(I-2/Q5 — 만재는 IF-05 dispatch만 차단, 물리 정렬은 진행).
/// </summary>
public sealed class SorterFloorReturnService : IHostedService, IAsyncDisposable
{
    private readonly ISorterGatewayRegistry     _registry;
    private readonly SorterPendingFloorQueues   _queues;
    private readonly IDestinationStatusService  _status;
    private readonly IHostApplicationLifetime   _lifetime;
    private readonly IPendingFloorQueueRestorer _restorer;
    private readonly ILogger<SorterFloorReturnService> _log;
    private readonly int _intervalMs;

    private CancellationTokenSource? _cts;
    private Task?                    _loopTask;
    private int                      _stopped;   // 멱등 StopAsync(Interlocked)

    // ── 소터별 관측 상태(관측 루프 단일 스레드 전용 — 락 불요) ─────────────────
    // 분류 사이클(Ready 1→0→1) 단위 pop을 위해 소터별 직전 Ready·사이클 시작 CurFloor를 추적.
    private sealed class ObserveState
    {
        public bool PrevReady = true;        // 소터 기동 Ready=1 전제(첫 전이 정상 감지).
        public int  CycleStartFloor;         // Ready 1→0 시점 CurFloor(분류-제자리 vs 이동 구분).
    }
    private readonly ConcurrentDictionary<long, ObserveState> _observeState = new();

    public SorterFloorReturnService(
        ISorterGatewayRegistry    registry,
        SorterPendingFloorQueues  queues,
        IDestinationStatusService status,
        IHostApplicationLifetime  lifetime,
        IPendingFloorQueueRestorer restorer,
        IOptions<WcsOptions>      options,
        ILogger<SorterFloorReturnService> log)
    {
        _registry   = registry;
        _queues     = queues;
        _status     = status;
        _lifetime   = lifetime;
        _restorer   = restorer;
        _log        = log;
        _intervalMs = Math.Max(1, options.Value.SorterFloorReturn.ObserveIntervalMs);
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // ── S-TWO-FLOOR-CONTROL C2 S2/S3: I-3 큐 재파생 복원 (관측 루프 소비 전 1회 — 복원 before 관측) ──
        // 미완료 SORTER_3D piece 에서 소터별 pending-floor 큐를 재구성한 뒤 관측 루프를 띄운다. 이 순서로
        // "복원이 관측 첫 소비보다 먼저"(계약 S3/CC5)가 구조적으로 보장된다. 재파생 예외는 격리(빈 큐로 진행)하되
        // 로깅으로 Fail Loud — 복원 실패가 관측 루프 기동을 막지 않는다(부트스트랩 이후 신규 IF-05 는 정상 enqueue).
        try
        {
            await _restorer.RestoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogError(ex, "[층복귀] I-3 큐 재파생 예외 — 빈 큐로 관측 시작(Fail-Loud)");
        }

        _cts      = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loopTask = Task.Run(() => RunObserveLoopAsync(_cts.Token));
        _log.LogInformation("[층복귀] 소터 pending-floor 큐 관측 루프 시작(주기 {Ms}ms)", _intervalMs);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // 이중 호출(StopAsync 후 DisposeAsync 내부 재진입) 방어 — 본문 1회만(disposed CTS 재접근 방지).
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;

        if (_cts is not null)
        {
            try { await _cts.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { /* 이미 dispose됨(teardown 경쟁) */ }
        }

        if (_loopTask is not null)
        {
            try { await _loopTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception) { }   // teardown 경쟁 예외 흡수(폴 루프 동형)
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _cts?.Dispose();
    }

    private async Task RunObserveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_intervalMs, ct).ConfigureAwait(false);

                foreach (var bundle in _registry.AllBundles)
                {
                    try { ObserveSorter(bundle, ct); }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        // 소터 1대의 관측 예외가 루프·형제 소터를 죽이지 않게 격리(Fail-Loud 로깅).
                        try { _log.LogError(ex, "[층복귀] 소터 destId={DestId} 관측 예외 — 다음 주기 재시도", bundle.DestinationId); }
                        catch { /* teardown 중 로거 disposed */ }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                try { _log.LogError(ex, "[층복귀] 관측 루프 예외 — 다음 주기 재시도"); }
                catch { /* teardown 중 로거 disposed */ }
            }
        }
    }

    /// <summary>
    /// 소터 1대 관측 — 분류 사이클(Ready 1→0→1) 단위로 큐 머리 pop, 미정렬이면 유휴(Ready=1) 시 F 기입.
    ///
    /// pop 기준 재설계(I-1 — 2026-07-23): 도착(CurFloor==F) 즉시 pop이 아니라 **분류 사이클 완료**(그 피스가
    /// 실제 분류됨)마다 큐 머리 1건 pop. 큐 [A,A,B]에서 A 2건이 모두 분류 완료되기 전엔 소터가 B로 이동하지
    /// 않아 2번째 A-AGV 고립을 막는다. 분류-제자리(CurFloor 불변)와 정렬 이동(CurFloor 변화)을 Ready 1→0
    /// 시점 CurFloor로 구별해, 이동에 의한 Ready 0→1은 pop하지 않는다(정렬 이동은 피스 소비가 아님).
    ///
    /// stall 재조정: 구 "머리 F==CurFloor 즉시 소비" 엣지는 제거([A,A,B] 조기 pop 버그의 원인). 대신 머리
    /// F==CurFloor면 그 피스의 분류를 기다렸다 pop한다 — 각 enqueue 피스에는 대응 분류(IF-10)가 반드시
    /// 오므로 정상 흐름은 무-stall(미투하 abandonment는 파킹존 D 스코프). 기입은 유휴(Ready=1)·CurFloor!=F
    /// 에서만 하여 분류 중(Ready=0) 선기입으로 분류+이동이 융합돼 사이클 감지를 깨는 것을 방지.
    ///
    /// 사이클 감지는 관측 루프(단일 스레드) 샘플링 기반 — Ready=0 구간(분류·이동 소요)이 관측 주기보다
    /// 충분히 길어 최소 1회 샘플됨(현장 분류=초 단위 ≫ 주기 150ms). 연속 분류 사이 Ready=1 간격도 AGV
    /// 도착 cadence(초 단위) ≫ 주기라 각 0→1 에지를 놓치지 않는다.
    /// </summary>
    private void ObserveSorter(SorterBundleHandle bundle, CancellationToken ct)
    {
        long destId = bundle.DestinationId;
        var  snap   = bundle.Latest;
        var  st     = _observeState.GetOrAdd(destId, static _ => new ObserveState());

        // OFFLINE(스냅샷 불신) — pop·기입 생략. PrevReady만 동기(오프라인 스냅샷은 직전 Ready 유지)해
        // 복구 시 스퓨리어스 에지 방지.
        if (!snap.Online)
        {
            st.PrevReady = snap.Ready;
            return;
        }

        // ── 분류 사이클 단위 pop (Ready 1→0→1) ────────────────────────────────────
        bool ready = snap.Ready;
        if (st.PrevReady && !ready)
        {
            // Ready 1→0: 사이클 시작 — 시작 시점 CurFloor 기록(분류-제자리 vs 정렬 이동 구분).
            st.CycleStartFloor = snap.CurFloor;
        }
        else if (!st.PrevReady && ready)
        {
            // Ready 0→1: 사이클 완료. 시작·완료 CurFloor 동일(제자리 분류 — 이동 아님) && 머리층==그 층이면
            // 그 피스가 분류 완료된 것 → 큐 머리 1건 pop. 정렬 이동(CurFloor 변화)에 의한 0→1은 pop 안 함.
            if (st.CycleStartFloor == snap.CurFloor
                && _queues.TryPeek(destId, out int head) && head == snap.CurFloor)
            {
                _queues.TryPop(destId, out _);
            }
        }
        st.PrevReady = ready;

        // ── 정렬 기입 — 유휴(Ready=1)·CurFloor!=머리층에서만(분류/이동 중 선기입 금지) ──────────
        if (!ready || !_queues.TryPeek(destId, out int f) || snap.CurFloor == f)
            return;   // 분류/이동 중이거나 큐 빔이거나 이미 머리층 → 기입 불요.

        // hold(PAUSED만) 산출 후 게이트. PAUSED/OFFLINE·핑퐁(TgtFloor≠0)이면 미기입(DepositDecider 차단).
        //
        // I-2(S-TWO-FLOOR-CONTROL B · Q5 승인): 게이트에 **Paused만** 넘긴다(Online은 위 snap.Online 조기
        //   반환으로 이미 선판정). **매 유휴 틱마다 무거운 Compute(→ ComputeSorterFull: 셀/배정/명령/piece
        //   다중 집계 쿼리)를 호출하지 않는다** — 저비용 IsPaused(destination Status/IsActive 단일 조회)로 대체.
        //   ★ FULL(셀 만재)은 정렬 기입을 **차단하지 않는다**: 큐에 든 피스는 이미 IF-05 dispatch에서 수용
        //     확정분이므로, 만재로 물리 정렬(이동)까지 막으면 확정 피스가 고립된다. FULL은 IF-05 dispatch에서만
        //     차단(2단계 게이트 분리). Paused/Offline은 여전히 차단(절대규칙 #2 — 오케스트레이터 정정 문언).
        bool paused = _status.IsPaused(destId);
        var hold = paused ? WcsHold.Paused : WcsHold.None;

        var decision = DepositDecider.Decide(snap, f, hold);
        if (!decision.WriteTgtFloor)
            return;   // 핑퐁 차단(TgtFloor≠0)·hold·정렬완료 — 미기입.

        // 소터별 단일 쓰기 큐 경유(절대규칙 #1). fire-and-forget — 예외는 삼키지 않고 로깅.
        var stopping = _lifetime.ApplicationStopping;
        _ = bundle.EnqueueSetTgtFloorAsync(decision.TgtFloorValue, stopping)
                  .AsTask()
                  .ContinueWith(t =>
                  {
                      if (t.IsFaulted)
                          try { _log.LogError(t.Exception, "[층복귀] SetTgtFloor 큐 투입 예외 destId={DestId} floor={Floor}", destId, f); }
                          catch { }
                  }, TaskScheduler.Default);
    }
}
