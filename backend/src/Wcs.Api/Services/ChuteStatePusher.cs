using Wcs.Data;

namespace Wcs.Api;

// ════════════════════════════════════════════════════════════════════════════
// ChuteStatePusher — PAUSED/RESUMED 전이 관찰 → 고객 UpdateChuteState 푸시 (S-CHUTESTATE-PUSH)
//
// RcsPush의 DestinationStatusPusher와 구조적 형제(관찰 → 클라이언트 1건 전송, 관심사 분리).
// 단, RcsPush(복합 ready 폴 관찰)와 달리 이 관찰은 **명시 전이 이벤트** 기반이라 훨씬 단순하다:
//   - 발원: DestinationControlService.OnTransition(실제 PAUSED/RESUMED 전이에서만 발화).
//   - 매핑(LOCKED): PAUSED → next_state 2(Pause), NORMAL(RESUMED) → next_state 3(Manual-open).
//   - chute_numbers = dest.ChuteNo 그대로(1:1, 이벤트가 실어 옴 — 매핑 테이블/DB 조회 없음).
//   - 스코프 게이트 = 전이 종류. FULL(capacity)·O6(cell-assign)은 이 이벤트로 들어오지 않으므로
//     자동 제외(별도 목적지 필터 불요). AlreadyInState(멱등)도 발화 안 됨 → 스퓨리어스 재푸시 0.
//   - CHUTE·SORTER_3D 둘 다 동일 훅으로 균일 처리(DestType 필터 없음).
//
// 관찰-전용: pause/resume 코어 동작을 바꾸지 않는다(전이 이벤트 구독만). 푸시는 fire-and-forget +
//   클라이언트 내부 재시도 — 운영자 O2/O3 HTTP 응답을 막지 않는다(비블로킹). 예외는 삼키지 않되(로깅)
//   코어를 죽이지 않는다(관찰 루프 예외 격리 — RcsPush observe 루프 미러).
//
// DORMANT(확정): BaseUrl 미설정이면 StartAsync가 경고 후 구독하지 않는다(HTTP 시도 0·크래시 0).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 운영자 PAUSED/RESUMED 전이를 관찰해 고객 UpdateChuteState로 푸시하는 IHostedService.
/// DestinationControlService.OnTransition 구독(StartAsync) → 해제(StopAsync).
/// </summary>
public sealed class ChuteStatePusher : IHostedService, IAsyncDisposable
{
    // 계약 상태값(UpdateChuteState) — 매핑 LOCKED(Q-a).
    private const int NextStatePause      = 2;   // PAUSED 전이 → Pause chute
    private const int NextStateManualOpen = 3;   // RESUMED(NORMAL) 전이 → Manual-open

    private readonly IChuteStatePushClient       _client;
    private readonly IDestinationControlService  _control;
    private readonly ILogger<ChuteStatePusher>   _log;

    // 진행 중 푸시 취소용(호스트 종료 시 재시도 루프 취소 전파).
    private CancellationTokenSource? _cts;

    // 구독 여부(StopAsync에서 해제 — 멱등).
    private bool _subscribed;

    // 멱등 StopAsync 플래그(Interlocked) — 이중 호출 시 본문 1회만 실행.
    private int _stopped;

    public ChuteStatePusher(
        IChuteStatePushClient      client,
        IDestinationControlService control,
        ILogger<ChuteStatePusher>  log)
    {
        _client  = client;
        _control = control;
        _log     = log;
    }

    // ── IHostedService ───────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // DORMANT: BaseUrl 미설정 → 경고 후 비활성(구독 안 함·HTTP 시도 0·크래시 0).
        if (!_client.IsEnabled)
        {
            _log.LogWarning(
                "[CHUTESTATE푸시] 고객 base URL 미설정(Wcs:ChuteStatePush:BaseUrl) — 아웃바운드 푸시 DORMANT(비활성). " +
                "고객이 호스트 제공 시 이 한 값만 설정하면 활성. pause/resume·인바운드는 정상 동작.");
            return Task.CompletedTask;
        }

        // 서비스 수명 CTS(StopAsync에서 취소) — 진행 중 재시도 루프에 종료 전파.
        // StartAsync의 cancellationToken은 기동 단계 전용이므로 링크하지 않는다.
        _cts = new CancellationTokenSource();

        _control.OnTransition += OnTransition;
        _subscribed = true;

        _log.LogInformation(
            "[CHUTESTATE푸시] 활성 — 운영자 PAUSED/RESUMED 전이 관찰 시작(PAUSED→{Pause}, RESUMED→{Open})",
            NextStatePause, NextStateManualOpen);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // 이중 호출(StopAsync 후 컨테이너 Dispose 내부 재진입) 방어 — 본문 1회만.
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return Task.CompletedTask;

        if (_subscribed)
        {
            _control.OnTransition -= OnTransition;   // 종료 후 이벤트 유입 차단.
            _subscribed = false;
        }

        if (_cts is not null)
        {
            try { _cts.Cancel(); }                    // 진행 중 재시도 루프 취소 전파.
            catch (ObjectDisposedException) { /* teardown 경쟁 */ }
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        _cts?.Dispose();
        return ValueTask.CompletedTask;
    }

    // ── 전이 관찰 핸들러 ───────────────────────────────────────────────────────
    //
    // DestinationControlService에서 동기 호출된다(운영자 O2/O3 요청 스레드).
    // 절대 블로킹하지 않는다 — 푸시는 fire-and-forget(클라이언트 내부 재시도). 예외는 격리.
    private void OnTransition(DestinationTransition t)
    {
        // 방어적 재확인 — 비활성이면 무시(no-op).
        if (!_client.IsEnabled) return;

        // 스코프 게이트 = 전이 종류. PAUSED → 2, NORMAL(RESUMED) → 3(Q-a LOCKED).
        int nextState = t.Target switch
        {
            DestStatus.PAUSED => NextStatePause,
            DestStatus.NORMAL => NextStateManualOpen,
            _                 => 0,   // 알 수 없는 상태 — 방어적 무시(계약 외 값 미발신).
        };
        if (nextState == 0)
        {
            _log.LogWarning("[CHUTESTATE푸시] 미지원 전이 상태 {Target} destId={Id} — 미발신",
                t.Target, t.DestinationId);
            return;
        }

        // chute_numbers = ChuteNo 직송(1:1). 전이당 길이-1 단건 배열(계약 구조 유지).
        var payload = new ChuteStatePushPayload(
            ChuteNumbers: new[] { t.ChuteNo },
            NextStates:   new[] { nextState });

        // fire-and-forget — 운영자 O2/O3 응답을 막지 않음. 예외는 PushSafeAsync 내부에서 처리(Fail-Loud).
        _ = PushSafeAsync(payload, t);
    }

    private async Task PushSafeAsync(ChuteStatePushPayload payload, DestinationTransition t)
    {
        try
        {
            var token = _cts?.Token ?? CancellationToken.None;
            // 클라이언트가 재시도·Fail-Loud·operation_log를 책임진다(false 반환은 이미 로깅됨).
            await _client.PushAsync(payload, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 호스트 종료 — 미발신 유지(다음 전이 시 재발신).
        }
        catch (Exception ex)
        {
            // PushAsync는 내부에서 false로 수렴하지만, 방어적으로 미관찰 예외 0 보장(Fail-Loud).
            try
            {
                _log.LogError(ex,
                    "[CHUTESTATE푸시] 푸시 루프 예외 destId={Id} chuteNo={ChuteNo} target={Target}",
                    t.DestinationId, t.ChuteNo, t.Target);
            }
            catch { /* teardown 중 로거 disposed */ }
        }
    }
}
