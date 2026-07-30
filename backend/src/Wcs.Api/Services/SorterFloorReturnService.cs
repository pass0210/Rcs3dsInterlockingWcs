using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Wcs.Core;
using Wcs.Data;

namespace Wcs.Api;

// ════════════════════════════════════════════════════════════════════════════
// 인덕션 기반 2층 제어 (S-TWO-FLOOR-CONTROL A / write-on-clear 개정 S-TWO-FLOOR-WRITE-ON-CLEAR):
//   ① 큐(SorterPendingFloorQueues)     = 상태(소터별 pending-floor FIFO).
//   ② 판정(Wcs.Core.DepositDecider)     = 순수 게이트(층 F 파라미터화·write-on-clear).
//   ③ 관측 루프(SorterFloorReturnService) = 트리거(TgtFloor==0 관측 → 큐 머리 F 기입, 분류 시작 클리어 시 pop).
//
// 절대규칙:
//   #1 TgtFloor 기입은 소터별 단일 쓰기 큐(bundle.EnqueueSetTgtFloorAsync)로만 — 직접 Modbus 0.
//   #2 TgtFloor 게이트(TgtFloor==0에서만 기입), 진행중(≠0)엔 미기입(핑퐁 차단).
//   #3 WCS는 TgtFloor를 클리어하지 않는다(PLC가 분류 시작 시 클리어). WCS는 D6에 0을 절대 안 씀.
//   #7 관측 주기·스톨 임계는 appsettings(Wcs:SorterFloorReturn:ObserveIntervalMs·StallSuspectTicks).
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
/// write-on-clear 개정(S-TWO-FLOOR-WRITE-ON-CLEAR 2026-07-29):
///   · pop 단위 = **분류 시작 클리어 에지**(TgtFloor 비영→0)마다 큐 머리 1건. PLC는 분류 시작 시에만 TgtFloor를
///     0으로 클리어하고(도착 시엔 유지·벤더 확정 OQ3), WCS가 비영값의 유일한 기입자이므로 이 전이는 "그 피스의
///     분류가 실제 시작됨"의 명확한 신호다 → 에지당 정확히 1 pop(over-pop 불가·early-pop 불가). 큐 [A,A,B]에서
///     A 2건이 모두 분류 시작되기 전엔 소터가 B로 이동하지 않는다(2번째 A-AGV 고립 방지 — I-1 불변식 보존).
///   · pop 후 새 머리(다음 피스 층)를 같은 틱에 기입한다(OQ1). 큐가 비면 미기입 — TgtFloor 0 유지(디폴트층 park·OQ2).
///   · 기입 트리거 = TgtFloor==0 관측 && 큐 비지 않음 && Online && !Paused. **Ready==0(분류/이동 중)에도 기입**
///     (write-during-busy — PLC가 진행 중 분류를 마친 뒤 F로 이동)하고, **CurFloor==F(이미 그 층)에도 기입**
///     (같은 층 hold — 방치 시 TgtFloor==0이 디폴트층 이동 명령이라 드리프트). 실제 기입 여부는 DepositDecider
///     (순수 #8)가 결정: TgtFloor==0이면 F 기입·≠0이면 미기입(핑퐁 #2). WCS는 D6에 0을 절대 안 씀(#3).
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
    private readonly IOperationLogger           _opLog;
    private readonly ITraceLogger               _trace;
    private readonly ILogger<SorterFloorReturnService> _log;
    private readonly int _intervalMs;
    private readonly int _stallSuspectTicks;   // ≤0이면 스톨 감지 비활성(C3).

    private CancellationTokenSource? _cts;
    private Task?                    _loopTask;
    private int                      _stopped;   // 멱등 StopAsync(Interlocked)

    // ── 소터별 관측 상태(관측 루프 단일 스레드 전용 — 락 불요) ─────────────────
    // 분류 시작 클리어 에지(TgtFloor 비영→0) 단위 pop을 위해 소터별 무장 여부·직전 TgtFloor를 추적.
    private sealed class ObserveState
    {
        // ── 분류-시작 pop 에지 감지 ───────────────────────────────────────────
        public bool Armed;                   // TgtFloor==0을 최초 1회 관측한 뒤부터 에지 감지 켜짐(무장).
        public int  PrevTgtFloor;            // 직전 관측 TgtFloor(무장 후 비영→0 전이 = 분류 시작 = pop 에지).

        // ── fail-loud 스톨 의심 감지 상태(관측 전용) ──────────────────────────
        public int  StallTicks;              // AND 조건 연속 지속 틱수.
        public bool StallWarned;             // 이 에피소드에서 WARN 이미 발화(스팸 억제 — 에피소드당 1회).
        public int  StallHeadFloor;          // 직전 틱 큐 머리 층(머리 불변 판정 — pop 진행 여부).
    }
    private readonly ConcurrentDictionary<long, ObserveState> _observeState = new();

    public SorterFloorReturnService(
        ISorterGatewayRegistry    registry,
        SorterPendingFloorQueues  queues,
        IDestinationStatusService status,
        IHostApplicationLifetime  lifetime,
        IPendingFloorQueueRestorer restorer,
        IOperationLogger          opLog,
        ITraceLogger              trace,
        IOptions<WcsOptions>      options,
        ILogger<SorterFloorReturnService> log)
    {
        _registry          = registry;
        _queues            = queues;
        _status            = status;
        _lifetime          = lifetime;
        _restorer          = restorer;
        _opLog             = opLog;
        _trace             = trace;
        _log               = log;
        _intervalMs        = Math.Max(1, options.Value.SorterFloorReturn.ObserveIntervalMs);
        _stallSuspectTicks = options.Value.SorterFloorReturn.StallSuspectTicks;   // ≤0=비활성(클램프 안 함).
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
    /// 소터 1대 관측 — 분류 시작 클리어 에지(TgtFloor 비영→0)에서 큐 머리 1건 pop, TgtFloor==0이면 큐 머리 F 기입.
    ///
    /// pop 기준(write-on-clear — 2026-07-29): 분류 사이클(Ready 에지) 대신 **TgtFloor 비영→0 전이**에서 pop한다.
    /// PLC는 분류 시작 시에만 TgtFloor를 0으로 클리어하고(도착 시엔 유지 — 벤더 확정 OQ3), WCS가 비영값의 유일한
    /// 기입자이므로(관측된 0 이후에만 비영 기입) 관측된 비영→0 전이는 "그 피스의 분류가 실제 시작됨"의 명확한
    /// 신호다 → 에지당 정확히 1 pop. 큐 [A,A,B]에서 A 2건이 모두 분류 시작되기 전엔 B로 이동하지 않는다(I-1 보존).
    ///
    /// 무장(Armed): TgtFloor==0을 최초 1회 관측한 뒤부터만 에지 감지를 켠다. 콜드스타트 StartupClear가 잔류
    /// TgtFloor(예: 2)를 0으로 지우는 전이(2→0)를 분류 시작으로 오인해 복원 큐 머리를 조기 pop하는 것을 원천
    /// 차단한다 — StartupClear는 첫 Online 스냅샷 게시 전에 큐 투입되나 처리는 비동기라 첫 관측이 잔류를 볼 수
    /// 있다. WCS는 0을 관측한 뒤에야 비영을 기입하므로, 무장 후 관측하는 비영은 항상 WCS 자신의 기입 → 그 뒤의
    /// 0은 진짜 PLC 클리어다. OFFLINE 복구 시에도 재무장(Armed=false)해 fabricated 에지를 방지한다.
    ///
    /// 기입은 TgtFloor==0 관측 시 큐 머리 F로(Ready==0/CurFloor==F 무관 — write-during-busy·same-floor hold).
    /// 실제 기입 여부는 DepositDecider(순수)가 결정: TgtFloor==0이면 F 기입·≠0이면 미기입(핑퐁 #2). 큐가 비면
    /// 미기입(TgtFloor 0 유지 = 디폴트층 park·OQ2). 에지·기입 모두 관측 루프(단일 스레드) 샘플링 기반이며,
    /// 분류(Ready=0)·클리어(0) 창이 관측 주기보다 충분히 길어(현장 초 단위 ≫ 주기 150ms) 최소 1회 샘플된다.
    /// </summary>
    private void ObserveSorter(SorterBundleHandle bundle, CancellationToken ct)
    {
        long destId = bundle.DestinationId;
        var  snap   = bundle.Latest;
        var  st     = _observeState.GetOrAdd(destId, static _ => new ObserveState());

        // OFFLINE(스냅샷 불신) — pop·기입 생략. 에지 감지 무장 해제(복구 시 재무장 → fabricated 에지 방지).
        // 오프라인은 정당한 미기입 상태이므로 스톨 감지 리셋(발화 제외).
        if (!snap.Online)
        {
            st.Armed = false;
            ResetStall(st);
            return;
        }

        int tgt = snap.TgtFloor;

        // ── 분류-시작 pop (TgtFloor 비영→0 클리어 에지) ────────────────────────────
        if (!st.Armed)
        {
            // 무장 전 — TgtFloor==0을 관측해야 무장(콜드스타트 잔류 2→0을 pop 에지로 오인 방지). 0을 볼 때까지 보류.
            if (tgt == 0) { st.Armed = true; st.PrevTgtFloor = 0; }
        }
        else
        {
            if (st.PrevTgtFloor != 0 && tgt == 0)
            {
                // 비영→0 에지 = 분류 시작 → 큐 머리 1건 pop(그 피스 소비). 에지당 정확히 1회.
                if (_queues.TryPop(destId, out int popped))
                {
                    ResetStall(st);   // pop = 진행 → 스톨 카운터 리셋(에지 직후 재무장).
                    // ── [트레이스 이벤트 2] TgtFloor 펜딩큐 디큐(분류 시작 클리어) — 관측/로깅 전용(S-TRACE-LOG-VIEWER) ──
                    // 층-큐 흐름(소터+층 scope). 큐가 floor(int)만 저장하므로 pId 미포함(수용된 경계). timing은
                    // 분류 시작(구 사이클 완료보다 이름) — event 번호·이름·피스당 1회 의미는 불변.
                    _trace.Log(new TraceRecord(
                        EventNo: 2, Event: "TGTFLOOR_DEQUEUE", At: DateTimeOffset.Now,
                        PId: null, CSeq: null, ChuteNo: bundle.ChuteNo, DestId: destId,
                        CellNo: null, Floor: popped, InductionNo: null, Trigger: "SORT_START_CLEAR",
                        Detail: $"{{\"curFloor\":{snap.CurFloor},\"remainingDepth\":{_queues.Count(destId)}}}"));
                }
            }
            st.PrevTgtFloor = tgt;
        }

        // ── fail-loud 스톨 의심 감지(관측 전용·WARN + operation_log 1회/에피소드) ──────────
        // 새 write 모델에선 큐 머리가 있으면 WCS가 즉시 F를 기입하므로 TgtFloor==0에 머물지 않는다. under-pop
        // (정렬·유휴인데 투하가 안 와 분류 시작(클리어)이 안 일어나 pop이 정체 — AGV abandonment)만 발화한다.
        // 쓰기/pop/재dispatch 같은 교정 동작은 하지 않는다(그건 Sub-Sprint D).
        DetectStall(destId, bundle.ChuteNo, snap, st);

        // ── 정렬/드리프트-방지 기입 — TgtFloor==0 관측 시 큐 머리 F 기입(Ready==0/CurFloor==F 무관) ──────
        // write-on-clear: 구 "!ready 조기반환·CurFloor==F 스킵"을 제거 — 분류 중(write-during-busy)에도, 이미
        //   그 층(same-floor hold)에도 기입한다. TgtFloor==0(디폴트층 이동 명령)을 방치하면 캐리지가 드리프트하기
        //   때문이다. 실제 기입 여부는 DepositDecider(순수)가 판정: TgtFloor==0이면 F 기입·≠0이면 미기입(핑퐁 #2).
        if (!_queues.TryPeek(destId, out int f))
            return;   // 큐 빔 → 기입 불요(TgtFloor 0 유지 = 디폴트층 park·OQ2).

        // hold(PAUSED만) 산출 후 게이트. Online은 위 snap.Online 조기반환으로 이미 선판정.
        // I-2(Q5): 게이트에 **Paused만** 넘긴다 — 매 틱 무거운 Compute(→ ComputeSorterFull 셀 집계)를 호출하지
        //   않고 저비용 IsPaused(destination Status/IsActive 단일 조회)만 쓴다. ★ FULL(셀 만재)은 정렬 기입을
        //   차단하지 않는다(큐 피스는 IF-05 수용 확정분 — 만재로 물리 정렬까지 막으면 고립). FULL은 IF-05
        //   dispatch만 차단(2단계 게이트 분리). Paused/Offline은 여전히 차단(절대규칙 #2).
        bool paused = _status.IsPaused(destId);
        var hold = paused ? WcsHold.Paused : WcsHold.None;

        var decision = DepositDecider.Decide(snap, f, hold);
        if (!decision.WriteTgtFloor)
            return;   // TgtFloor≠0(핑퐁 #2)·hold(Paused) — 미기입. WCS는 D6에 0을 안 씀(#3).

        // 소터별 단일 쓰기 큐 경유(절대규칙 #1). fire-and-forget — 예외는 삼키지 않고 로깅.
        // 컨슈머(PlcGateway)가 쓰기 직전 D6를 fresh FC03로 재읽어 !=0이면 스킵(멱등 dedup — 매 0-틱 같은 F
        //   재천명이 스팸되지 않게). WCS는 0을 어디서도 안 씀(#3 — 이 경로는 항상 1/2 등 비영값만 투입).
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

    // ── fail-loud 스톨 의심 감지기(관측 전용) — write-on-clear 재조정 ─────────────
    //
    // 재조정 배경: 구 조건(유휴 ∧ TgtFloor==0 ∧ 머리 존재)은 새 write 모델에선 발화 불가다 — 큐 머리가 있으면
    //   WCS가 즉시 F를 기입해 TgtFloor≠0 이 되므로 "머리 있는데 TgtFloor==0" 상태가 성립하지 않는다. 새 under-pop
    //   시그니처는 **정렬됐는데 투하가 안 와 분류 시작(클리어)이 안 일어나 pop이 정체**하는 것 = AGV abandonment.
    //
    // AND 조건(Online은 호출 전 보장): 유휴(Ready==1) ∧ 정렬(CurFloor==큐 머리 층) ∧ 큐 머리 존재 ∧ 정지 아님
    //   (!IsPaused) ∧ 큐 머리 층이 직전 틱과 동일(pop 무진행). 연속 StallSuspectTicks 틱 지속되면 에피소드당
    //   1회 WARN + operation_log 발화. 조건이 하나라도 깨지면 리셋(다음 에피소드 재감지).
    //
    // 왜 오탐 0인가:
    //   · busy(Ready==0) 구간(분류·이동)은 매 틱 리셋 — 정상 투하는 Ready 1→0(분류 시작)을 동반하므로 정상
    //     사이클링은 그 busy 창에서 카운터가 리셋돼 임계에 못 미친다(정상 투하 cadence < 임계 지속시간 전제).
    //   · 미정렬(CurFloor≠머리)이면 아직 정렬 이동 중 — 리셋(발화 대상 아님).
    //   · pop 진행 시 pop 브랜치가 ResetStall + 다음 틱 머리 층 변경으로 리셋.
    //   · 오프라인/PAUSED 는 정당한 미소비 — 각각 상위 조기반환·IsPaused 로 제외(발화 안 함).
    //   · 큐 빔은 머리 없음 → 발화 대상 아님.
    // 즉 실제 지속 under-pop(정렬·유휴·머리 불변이 임계 이상 — 미투하 abandonment)에서만 발화한다.
    //
    // IsPaused 는 저비용 단일 조회(I-2 — Compute/ComputeSorterFull 셀 집계 미호출)이며, 값싼 조건이 모두
    //   통과했을 때만 호출한다(불필요한 DB 조회 최소화). 예외는 관측 루프 try/catch(형제 소터 격리)가 흡수.
    private void DetectStall(long destId, int chuteNo, PlcSnapshot snap, ObserveState st)
    {
        // 스톨 감지 비활성(설정 ≤0) → 상태 리셋 후 no-op.
        if (_stallSuspectTicks <= 0) { ResetStall(st); return; }

        // 값싼 조건 먼저 — 유휴 ∧ 큐 머리 존재 ∧ 정렬(CurFloor==머리 층)(Online 은 호출 전 보장).
        if (!snap.Ready || !_queues.TryPeek(destId, out int head) || snap.CurFloor != head)
        {
            ResetStall(st);
            return;
        }

        // 정지(PAUSED/비활성)는 정당한 미소비 — 발화 제외(저비용 IsPaused 만·Compute 미호출).
        if (_status.IsPaused(destId))
        {
            ResetStall(st);
            return;
        }

        // 큐 머리 층이 직전 틱과 다르면(pop 으로 진행됨) 카운터 리셋·재무장 후 이 틱부터 재계수.
        if (st.StallTicks > 0 && head != st.StallHeadFloor)
            ResetStall(st);

        st.StallHeadFloor = head;
        st.StallTicks++;

        // 임계 도달 → 에피소드당 1회만 발화(지속 중 매 틱 반복 발화 금지 — 로그 스팸 0).
        if (st.StallTicks >= _stallSuspectTicks && !st.StallWarned)
        {
            st.StallWarned = true;
            FireStallWarning(destId, chuteNo, snap, head, st.StallTicks);
        }
    }

    private static void ResetStall(ObserveState st)
    {
        st.StallTicks     = 0;
        st.StallWarned    = false;
        st.StallHeadFloor = 0;
    }

    // 스톨 의심 발화 — Serilog WARN + operation_log 1건(구조화 detail). 관측 전용(교정 동작 0).
    private void FireStallWarning(long destId, int chuteNo, PlcSnapshot snap, int headFloor, int ticks)
    {
        try
        {
            _log.LogWarning(
                "[층복귀] 스톨 의심 — 소터 destId={DestId} chuteNo={ChuteNo}: 큐 머리 층 {Head} 존재·유휴(Ready=1)·" +
                "TgtFloor=0·머리 불변이 {Ticks}틱(≈{Ms}ms) 지속. under-pop 가능(관측 전용 — 자동 조치 없음).",
                destId, chuteNo, headFloor, ticks, ticks * _intervalMs);
        }
        catch { /* teardown 중 로거 disposed */ }

        try
        {
            _opLog.Log(
                OperationLogCategory.STATE,
                action:        "SORTER_STALL_SUSPECT",
                level:         OperationLogLevel.WARN,
                sorterChuteNo: chuteNo,
                destinationId: destId,
                detail:        JsonSerializer.Serialize(new
                {
                    headFloor,
                    curFloor     = snap.CurFloor,
                    ready        = snap.Ready,
                    tgtFloor     = snap.TgtFloor,
                    stallTicks   = ticks,
                    intervalMs   = _intervalMs,
                    observedOnly = true,   // 관측 전용 — 교정 동작 없음(파킹/복구는 Sub-Sprint D).
                }));
        }
        catch { /* operation_log fail-safe — 본 관측 비차단 */ }
    }
}
