using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Wcs.Api.Hubs;
using Wcs.Data;

namespace Wcs.Api;

// ════════════════════════════════════════════════════════════════════════════
// MonitorRelayService — F2 실시간 relay (S-FRONTEND-F2). 신규 폴 루프 0.
//
// 두 소스를 IHubContext<WcsMonitorHub>로 fire-and-forget 브로드캐스트한다:
//   ① 소터 워드 스트림: 기존 관측 훅(SorterBundleHandle.SubscribeRegisterChange/Online/Offline)을
//      relay가 **추가 구독**(operation_log 구독과 나란히 — 멀티캐스트 이벤트, 시그니처 무변경).
//      변화분만 push(무변화 0) + 저빈도 하트비트(전체 스냅샷 재전송 — 델타 유실·재연결 갭 보정).
//   ② operation_log 테일: OperationLogService.OnEntry(단일 컨슈머 발화)를 구독해 그룹으로 브로드캐스트.
//      POLL_CHANGE는 기본 스트림(oplog)에서 제외하고 옵트인 그룹(oplog-poll)으로만 보낸다(폭주 방지).
//
// 불변식(S-OBSERVABILITY 계약 동형 · 절대규칙·함정7):
//   · 모든 콜백은 폴/쓰기/핸드셰이크 스레드 또는 로그 컨슈머 스레드에서 **직접** 호출된다 →
//     동기 I/O·블로킹 금지. IHubContext.SendAsync는 fire-and-forget(await 안 함)하고,
//     faulted task는 관찰만 하고 삼킨다(예외 격리 — 본 동작 비지연·루프 사망 방지).
//   · 구독 시점: AllBundles는 SorterRegistryFactory.StartAsync 완료 후 채워진다 →
//     이 relay는 그 IHostedService **이후에 등록**돼 StartAsync가 나중에 돌게 한다(Program.cs 결선).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>기존 관측 훅 + operation_log를 SignalR로 중계하는 relay(IHostedService). 신규 폴 0.</summary>
public sealed class MonitorRelayService : IHostedService, IAsyncDisposable
{
    private readonly IHubContext<WcsMonitorHub> _hub;
    private readonly ISorterGatewayRegistry     _registry;
    private readonly OperationLogService        _opLog;
    private readonly ITraceLogger               _trace;
    private readonly MonitorOptions             _opt;
    private readonly ILogger<MonitorRelayService> _log;

    private CancellationTokenSource? _cts;
    private Task?                    _heartbeatTask;
    // 전용 추적 sink OnEntry 핸들러(재구독 방지·StopAsync 해제용).
    private Action<TraceRecord>?     _traceHandler;

    // StopAsync에서 해제하는 것은 **opLog(OnEntry) 핸들러 한정**(재구독 방지용 저장) — F2-CR-M1.
    // PLC 관측 훅(SorterBundleHandle.Subscribe*) 구독은 의도적으로 해제하지 않는다:
    //   · 번들/레지스트리는 구독 해제 API를 노출하지 않고(+= 구독만), 기존 관측 구독자
    //     (SorterRegistryFactory의 operation_log 훅·OFFLINE alarm 훅)와 동일한
    //     host-lifetime 싱글톤 convention — 번들과 relay는 호스트와 함께 소멸한다.
    //   · 해제 없이도 안전: 호스트 종료 시 폴 루프가 먼저 멎어 훅 발화가 중단되고,
    //     잔여 발화는 Broadcast의 예외 흡수로 무해(수명 누수 없음 — 둘 다 싱글톤).
    private Action<OperationLog>? _opLogHandler;

    public MonitorRelayService(
        IHubContext<WcsMonitorHub>    hub,
        ISorterGatewayRegistry        registry,
        OperationLogService           opLog,
        ITraceLogger                  trace,
        IOptions<MonitorOptions>      opt,
        ILogger<MonitorRelayService>  log)
    {
        _hub      = hub;
        _registry = registry;
        _opLog    = opLog;
        _trace    = trace;
        _opt      = opt.Value;
        _log      = log;
    }

    // ── IHostedService ───────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // ① 소터 워드 스트림 — 각 번들의 관측 훅에 추가 구독(operation_log 구독과 나란히).
        //    AllBundles는 SorterRegistryFactory.StartAsync 이후이므로(등록 순서 보장) 이 시점 유효.
        foreach (var bundle in _registry.AllBundles)
        {
            var destId  = bundle.DestinationId;
            var chuteNo = bundle.ChuteNo;

            bundle.SubscribeRegisterChange((reg, oldV, newV) =>
                Broadcast(WcsMonitorHub.GroupSorters, "RegisterDelta",
                    new RegisterDeltaDto(destId, chuteNo, reg, oldV, newV, DateTimeOffset.UtcNow)));

            bundle.SubscribeOnline(snap =>
                Broadcast(WcsMonitorHub.GroupSorters, "SorterTransition",
                    new SorterTransitionDto(destId, chuteNo, true, snap.At)));

            bundle.SubscribeOffline(snap =>
                Broadcast(WcsMonitorHub.GroupSorters, "SorterTransition",
                    new SorterTransitionDto(destId, chuteNo, false, snap.At)));
        }

        // ② operation_log 테일 — 단일 컨슈머 발화를 구독. POLL_CHANGE는 옵트인 그룹으로만.
        _opLogHandler = OnOperationLogEntry;
        _opLog.OnEntry += _opLogHandler;

        // ③ 전용 추적 로그(S-TRACE-LOG-VIEWER) — 전용 sink OnEntry 를 구독해 trace 그룹으로 fire-and-forget.
        //    옵트인 그룹이라 뷰어가 없으면 no-op(빈 그룹 push). operation_log 구독과 동형(예외 격리·논블로킹).
        _traceHandler = OnTraceEntry;
        _trace.OnEntry += _traceHandler;

        // 저빈도 하트비트 — 전체 스냅샷 재전송(델타 유실·재연결 갭 보정). ≤0이면 비활성.
        if (_opt.HeartbeatMs > 0)
            _heartbeatTask = Task.Run(() => RunHeartbeatLoopAsync(_cts.Token));

        _log.LogInformation("[MonitorRelay] 시작 — 소터 {Count}대 구독, 하트비트 {Hb}ms",
            _registry.AllBundles.Count, _opt.HeartbeatMs);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_opLogHandler is not null)
        {
            _opLog.OnEntry -= _opLogHandler;
            _opLogHandler = null;
        }

        if (_traceHandler is not null)
        {
            _trace.OnEntry -= _traceHandler;
            _traceHandler = null;
        }

        if (_cts is not null)
        {
            try { await _cts.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
        }

        if (_heartbeatTask is not null)
        {
            try { await _heartbeatTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception) { /* teardown 경쟁 흡수 */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _cts?.Dispose();
    }

    // ── operation_log 엔트리 핸들러 ─────────────────────────────────────────────
    // 이 콜백은 OperationLogService 컨슈머 스레드에서 직접 호출된다 — 논블로킹·예외 격리.
    private void OnOperationLogEntry(OperationLog e)
    {
        // POLL_CHANGE는 옵트인 그룹(oplog-poll)으로만 — 기본 스트림 폭주 방지.
        var group = e.Category == OperationLogCategory.POLL_CHANGE
            ? WcsMonitorHub.GroupOpLogPoll
            : WcsMonitorHub.GroupOpLog;
        Broadcast(group, "OpLog", OpLogEntryDto.From(e));
    }

    // ── 전용 추적 엔트리 핸들러 ─────────────────────────────────────────────────
    // TraceLogService 컨슈머 스레드에서 직접 호출된다 — 논블로킹·예외 격리. trace 그룹(옵트인)으로만 push.
    private void OnTraceEntry(TraceRecord rec)
        => Broadcast(WcsMonitorHub.GroupTrace, "Trace", rec);

    // ── 하트비트 루프 — 전체 소터 스냅샷 저빈도 재전송 ──────────────────────────
    private async Task RunHeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_opt.HeartbeatMs));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var snapshot = SnapshotAll();
                if (snapshot.Length > 0)
                    Broadcast(WcsMonitorHub.GroupSorters, "Heartbeat", snapshot);
            }
        }
        catch (OperationCanceledException) { /* 종료 — 정상 */ }
        catch (Exception ex)
        {
            try { _log.LogWarning(ex, "[MonitorRelay] 하트비트 루프 예외 — 종료(관측만 영향)"); }
            catch { }
        }
    }

    private SorterWordDto[] SnapshotAll() =>
        _registry.AllBundles
            .OrderBy(b => b.ChuteNo)
            .Select(b =>
            {
                var s = b.Latest;
                return new SorterWordDto(
                    b.DestinationId, b.ChuteNo, s.Online,
                    s.CCellNo, s.CSeq, s.RCellNo, s.RSeq,
                    s.CFlag, s.RFlag, s.Ready,
                    s.CurFloor, s.TgtFloor, s.At);
            })
            .ToArray();

    // ── fire-and-forget 브로드캐스트 — 본 동작 비지연·예외 격리 ─────────────────
    private void Broadcast(string group, string method, object payload)
    {
        try
        {
            var task = _hub.Clients.Group(group).SendAsync(method, payload);
            // faulted task 관찰(unobserved 예외 방지) — 실패는 삼킨다(관측이 본 동작을 막지 않음).
            task.ContinueWith(t =>
            {
                if (t.Exception is not null)
                {
                    try { _log.LogDebug(t.Exception, "[MonitorRelay] push 실패(무시): {Method}", method); }
                    catch { }
                }
            }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        catch
        {
            // SendAsync 동기 예외(허브 컨텍스트 종료 등) — 삼킨다(fail-safe).
        }
    }
}
