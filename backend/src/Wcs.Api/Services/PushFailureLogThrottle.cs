namespace Wcs.Api;

// ════════════════════════════════════════════════════════════════════════════
// S-IF08-PUSH-LOG-THROTTLE — 반복-실패 push 로그 억제 게이트(로깅만 조절)
//
// 문제: RCS 호스트가 죽어 있으면 IF-08 chute-state push 재시도-소진 실패가 매 복구-하트비트 주기마다
//   세 sink(operation_log WARN "FAIL" + 트레이스 이벤트 8/10 result:"FAIL" + Serilog LogError)에 새 행으로
//   무한 누적한다. 이 게이트는 "반복되는 같은 실패"의 **로깅만** 억제한다 — push 재시도/재발신/delivery는
//   전혀 건드리지 않는다(RCS로의 재시도는 성공할 때까지 계속하되 그 실패를 매번 기록만 안 함).
//
// 억제 단위(OQ-2) = (route, next_state). route = (목적지, 층 호스트)이며 게이트 인스턴스 1개가 곧 한 route다
//   (DestinationStatusPusher.RouteState). 같은 목적지라도 BUSY(2)↔READY(3) 전이는 새 첫 실패로 로깅한다.
//
// 결과 시맨틱(계약):
//   · 첫 실패(전이)      → Emit(세 sink 각 1건)
//   · 반복되는 같은 실패 → Suppress(각 0건 — 하트비트 N주기 재발신에도 추가 0)
//   · 복구(성공)         → 억제 리셋(다음 실패가 새 첫 실패로 재무장)
//   · 저빈도 요약(OQ-1)  → Summary(설정 주기마다 1건 — 완전 무음 금지·Fail-Loud)
//
// 스레드안전(계약): 상태 갱신은 반드시 route별 락(RouteState.Gate) 안에서 원자적으로 수행한다
//   (비원자 check-then-act 금지). 판정에 필요한 컨텍스트(route별 직전 로깅 결과)는 Pusher가 보유하고
//   클라이언트(ChuteStatePushClient)는 호출 간 상태가 없으므로, 클라이언트가 실패 확정 시 이 게이트에
//   질의(OnFailure)해 emit 여부를 위임받는다("클라이언트가 Pusher로부터 억제 힌트를 받음").
// ════════════════════════════════════════════════════════════════════════════

/// <summary>반복-실패 push 로그 억제 판정 결과(억제 단위 = route, next_state).</summary>
public enum PushFailureLogAction
{
    /// <summary>첫 실패(전이) — 세 sink(operation_log WARN + 트레이스 8/10 + Serilog LogError) 각 1건 emit.</summary>
    Emit,

    /// <summary>반복되는 같은 실패 — 로그 0건(억제). push 재발신·delivery는 계속됨(로깅만 억제).</summary>
    Suppress,

    /// <summary>저빈도 "아직 실패 중" 요약 주기 도래 — 요약 1건(OQ-1 · 완전 무음 금지).</summary>
    Summary,
}

/// <summary>
/// 반복-실패 push 로그 억제 게이트. ChuteStatePushClient 가 재시도-소진(FAIL) 확정 직후 이 게이트에
/// 질의(<see cref="OnFailure"/>)해 세 FAIL sink 를 emit 할지/억제할지/요약할지 위임받는다.
/// 구현(DestinationStatusPusher.RouteState)은 route별 락 안에서 (route,next_state) 억제 단위로
/// 원자적 check-and-set 한다. 미주입(null)이면 억제 없음(현행 동작 — 클라이언트 단독 단위 테스트·레거시 호출).
/// </summary>
public interface IPushFailureLogThrottle
{
    /// <summary>
    /// next_state 실패가 방금 확정됐다(재시도 소진). 이 실패를 로깅할지 결정(원자 check-and-set).
    /// Emit=첫 실패(전이)이니 세 sink emit / Suppress=반복이니 0건 / Summary=요약 주기 도래.
    /// </summary>
    PushFailureLogAction OnFailure(int nextState);
}

/// <summary>
/// per-route 반복-실패 로그 억제 상태(직전 로깅한 실패 next_state + 마지막 실패-로그 시각).
/// 스레드안전은 소유자(RouteState)가 자신의 Gate 락으로 직렬화 보장 — 이 타입 자체는 락을 잡지 않는
/// 순수 상태기(deterministic). <see cref="Decide"/>는 clock 을 주입받아 시간 의존을 결정적으로 테스트할 수
/// 있게 한다(요약 주기 판정을 벽시계 없이 검증). 상태 전이:
///   · 직전 실패 next_state 와 다르면(첫 실패 or 전이) → Emit + 기록.
///   · 같은 실패가 요약 주기 이상 지속 → Summary + 시각 갱신.
///   · 그 외 반복 → Suppress.
///   · <see cref="Reset"/>(성공 복구) → 다음 실패가 새 첫 실패로 재무장.
/// </summary>
public sealed class PushFailureLogThrottleState
{
    /// <summary>직전 emit 한 실패의 next_state(null = 활성 억제 에피소드 없음).</summary>
    private int? _loggedFailureNextState;

    /// <summary>마지막으로 emit 한 실패-관련 로그(첫 실패 또는 요약) 시각 — 요약 주기 산정 기준.</summary>
    private DateTimeOffset _lastFailureLogAt;

    /// <summary>
    /// 실패 로그 결정(순수·결정적). 호출자가 락으로 직렬화 보장(check-and-set 원자성은 소유자 책임).
    /// </summary>
    /// <param name="nextState">이번 실패 push 의 next_state(2/3) — 억제 단위 키.</param>
    /// <param name="suppressEnabled">억제 on/off(설정값). false면 항상 Emit(구 동작).</param>
    /// <param name="summaryIntervalMs">저빈도 요약 주기(ms). ≤0이면 요약 비활성(반복은 Suppress).</param>
    /// <param name="now">현재 시각(clock 주입 — 결정적 테스트).</param>
    public PushFailureLogAction Decide(int nextState, bool suppressEnabled, int summaryIntervalMs, DateTimeOffset now)
    {
        // 억제 off → 매 실패 emit(현행 동작 보존, 상태 추적 없음).
        if (!suppressEnabled)
            return PushFailureLogAction.Emit;

        // 첫 실패 or next_state 전이(2↔3) → 새 에피소드로 emit + 기록.
        if (_loggedFailureNextState != nextState)
        {
            _loggedFailureNextState = nextState;
            _lastFailureLogAt       = now;
            return PushFailureLogAction.Emit;
        }

        // 같은 (route, next_state) 반복 실패 — 요약 주기 도래 시에만 저빈도 요약, 그 외 억제.
        if (summaryIntervalMs > 0 && (now - _lastFailureLogAt).TotalMilliseconds >= summaryIntervalMs)
        {
            _lastFailureLogAt = now;
            return PushFailureLogAction.Summary;
        }

        return PushFailureLogAction.Suppress;
    }

    /// <summary>push 성공 복구 시 억제 리셋 — 다음 실패가 새 첫 실패로 로깅되도록 재무장(신호 재무장).</summary>
    public void Reset() => _loggedFailureNextState = null;
}
