using Microsoft.EntityFrameworkCore;
using Wcs.Data;

namespace Wcs.Api;

// ════════════════════════════════════════════════════════════════════════════
// IDestinationControlService — B2C 운영자 제어의 런타임 PAUSED/RESUMED 전이 백엔드(S-F3a 신설).
//
// 배경(FRONTEND.md §3.3 / 감사 A-8 인접): ChuteCapacityService.IsPaused는 기동 시
//   InitializeFromDbAsync에서만 세팅되고 런타임 전이 메서드가 없었다. 이 서비스가 그 갭을 닫는다 —
//   운영자 조작(OpsController)이 목적지를 실시간으로 PAUSED/NORMAL 전환한다.
//
// 전이 단위(한 트랜잭션 + 커밋 후 인메모리 반영):
//   ① DB: destination.Status ← PAUSED/NORMAL, updated_at 갱신.
//   ② destination_event(PAUSED|RESUMED, OperatorId, DetailJson=prev/new) append(정규 감사).
//   ③ 커밋 후: CHUTE면 ChuteCapacityService.ApplyPauseStateInMemory로 인메모리 IsPaused 반영 +
//      OnChuteStateChanged 발화(GetHold 게이트·DestinationStatusPusher·STATE 훅 재평가).
//      SORTER_3D면 인메모리 불요 — DestinationStatusService.ComputeSorter가 DB Status를 직접 read하고
//      DestinationStatusPusher의 소터 관찰 타이머가 재평가한다(계약 "소터면 DB만").
//
// 멱등: 이미 목표 상태면 event 중복 append 없이 no-op(AlreadyInState) — 감사 잡음/중복 방지.
// 동시성: Destination.RowVersion/XminRowVersion 토큰을 존중 — 동시 전이 충돌은 Conflict로 정직 보고
//   (예외 삼킴 금지, CLAUDE.md Fail Loud). PLC 쓰기는 없음(소터 PAUSE = 순수 WCS-측 게이트, Q3 LOCK).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>런타임 전이 결과 — OpsController가 HTTP 상태로 매핑.</summary>
public enum DestControlOutcome
{
    /// <summary>상태가 실제로 전이됨(event 1행 append).</summary>
    Transitioned,
    /// <summary>이미 목표 상태 — 멱등 no-op(event append 없음).</summary>
    AlreadyInState,
    /// <summary>대상 destination 없음 → 404.</summary>
    NotFound,
    /// <summary>동시성 토큰 충돌(동시 전이) → 409.</summary>
    Conflict,
}

/// <summary>런타임 전이 결과 + 대상 목적지 타입(널=NotFound).</summary>
public readonly record struct DestControlResult(DestControlOutcome Outcome, DestType? DestType);

/// <summary>
/// 목적지 런타임 전이 알림(관찰 전용 side-channel) — 실제 PAUSED/RESUMED 전이(Transitioned) 시 발화.
/// ChuteStatePusher(S-CHUTESTATE-PUSH)가 구독해 고객 UpdateChuteState로 푸시한다.
/// AlreadyInState(멱등 no-op)에서는 발화하지 않는다(실제 상태 변화만).
/// ChuteNo를 함께 실어 관찰자가 별도 DB 조회 없이 chute_numbers를 직송(1:1)할 수 있게 한다.
/// </summary>
public readonly record struct DestinationTransition(
    long DestinationId, int ChuteNo, DestStatus Target, DestType DestType);

/// <summary>목적지 런타임 PAUSED/RESUMED 전이 서비스(운영자 제어 백엔드).</summary>
public interface IDestinationControlService
{
    /// <summary>목적지를 PAUSED로 전이(운영자 귀속). 이미 PAUSED면 멱등 no-op.</summary>
    Task<DestControlResult> PauseAsync(long destinationId, string operatorId, CancellationToken ct = default);

    /// <summary>목적지를 NORMAL로 전이(운영자 귀속). 이미 NORMAL이면 멱등 no-op.</summary>
    Task<DestControlResult> ResumeAsync(long destinationId, string operatorId, CancellationToken ct = default);

    /// <summary>
    /// 실제 PAUSED/RESUMED 전이(Transitioned) 발생 시 발화하는 관찰 전용 이벤트.
    /// AlreadyInState(멱등 no-op)·NotFound·Conflict에서는 발화하지 않는다.
    /// 구독자는 fire-and-forget으로 처리해 pause/resume 코어 경로를 막지 않아야 한다(비블로킹).
    /// </summary>
    event Action<DestinationTransition>? OnTransition;
}

/// <summary>
/// IDestinationControlService 구현 — 싱글톤. scoped WcsDbContext는 IServiceScopeFactory로 취득
/// (DestinationStatusService·ChuteCapacityService와 동형 — captive dependency 회피).
/// </summary>
public sealed class DestinationControlService : IDestinationControlService
{
    private readonly IServiceScopeFactory       _scopeFactory;
    private readonly IChuteCapacityService      _chuteCapacity;
    private readonly ILogger<DestinationControlService> _log;

    public DestinationControlService(
        IServiceScopeFactory                scopeFactory,
        IChuteCapacityService               chuteCapacity,
        ILogger<DestinationControlService>  log)
    {
        _scopeFactory  = scopeFactory;
        _chuteCapacity = chuteCapacity;
        _log           = log;
    }

    /// <inheritdoc/>
    // 관찰 전용 side-channel(S-CHUTESTATE-PUSH) — 실제 전이(Transitioned) 시에만 발화.
    // pause/resume 코어(전이·감사·인메모리·멱등)는 이 이벤트와 무관하게 동작한다.
    public event Action<DestinationTransition>? OnTransition;

    /// <inheritdoc/>
    public Task<DestControlResult> PauseAsync(long destinationId, string operatorId, CancellationToken ct = default) =>
        TransitionAsync(destinationId, operatorId, DestStatus.PAUSED, DestinationEventType.PAUSED, ct);

    /// <inheritdoc/>
    public Task<DestControlResult> ResumeAsync(long destinationId, string operatorId, CancellationToken ct = default) =>
        TransitionAsync(destinationId, operatorId, DestStatus.NORMAL, DestinationEventType.RESUMED, ct);

    private async Task<DestControlResult> TransitionAsync(
        long destinationId, string operatorId, DestStatus target, DestinationEventType evt, CancellationToken ct)
    {
        DestType destType;
        int  chuteNo = 0;   // 관찰 이벤트에 실을 ChuteNo(1:1 직송) — 스코프 안에서 캡처.
        bool alreadyInState;

        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();

            var dest = await db.Destinations
                .FirstOrDefaultAsync(d => d.Id == destinationId, ct)
                .ConfigureAwait(false);

            if (dest is null)
                return new DestControlResult(DestControlOutcome.NotFound, null);

            destType = dest.DestType;
            chuteNo  = dest.ChuteNo;

            // 멱등: 이미 목표 상태면 event 중복 없이 no-op(정책 명시 — 감사 잡음 방지).
            // 단, 인메모리 반영(아래)은 건너뛰지 않는다 — DB Status ↔ 인메모리 IsPaused divergence 교정(I-2).
            alreadyInState = dest.Status == target;

            if (!alreadyInState)
            {
                var now  = DateTime.UtcNow;
                var prev = dest.Status;
                dest.Status    = target;
                dest.UpdatedAt = now;

                // destination_event(PAUSED|RESUMED, operatorId) append — 정규 감사(operator_id 컬럼 보유).
                db.DestinationEvents.Add(new DestinationEvent
                {
                    DestinationId = destinationId,
                    EventType     = evt,
                    OperatorId    = operatorId,
                    DetailJson    = $"{{\"prev\":\"{prev}\",\"new\":\"{target}\"}}",
                    At            = now,
                });

                try
                {
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                }
                catch (DbUpdateConcurrencyException ex)
                {
                    // RowVersion/XminRowVersion 충돌(동시 전이) — 삼키지 않고 명시 로깅 후 Conflict 보고.
                    _log.LogWarning(ex, "[DestControl] destId={Id} 동시성 충돌({Target}) — 재시도 필요(op={Op})",
                        destinationId, target, operatorId);
                    return new DestControlResult(DestControlOutcome.Conflict, destType);
                }
            }
        }

        // DB 커밋(또는 멱등 확인) 후 인메모리 반영 — CHUTE만(소터는 DB Status로 산출 → 인메모리 불요).
        // I-2: Transitioned·AlreadyInState **공통** 경로에서 반영한다. 멱등 호출도 인메모리 IsPaused를
        // 목표 상태로 강제 동기화해, DB Status와 ChuteState.IsPaused가 어긋난 경우(게이트가 열린 채
        // "이미 PAUSED" 응답 등)를 idempotent 재요청 한 번으로 self-heal한다(#GetHold 게이트 일관성 보존).
        if (destType == DestType.CHUTE)
            _chuteCapacity.ApplyPauseStateInMemory(destinationId, target == DestStatus.PAUSED);

        if (alreadyInState)
        {
            _log.LogInformation("[DestControl] destId={Id} 이미 {Target} — 멱등 no-op(인메모리 재동기, op={Op})",
                destinationId, target, operatorId);
            return new DestControlResult(DestControlOutcome.AlreadyInState, destType);
        }

        _log.LogInformation("[DestControl] destId={Id} {Target} 전이 완료(op={Op}, type={Type})",
            destinationId, target, operatorId, destType);

        // ── 관찰 전용 side-channel 발화(S-CHUTESTATE-PUSH) — 실제 Transitioned에서만 ──
        // AlreadyInState는 위에서 이미 반환됐으므로 여기 도달 = 실제 전이 1건. 구독자(ChuteStatePusher)는
        // fire-and-forget으로 처리해 운영자 O2/O3 응답을 막지 않는다. 구독자 예외가 코어(이 반환)를 죽이지
        // 않도록 이벤트 발화 자체를 방어적으로 감싼다(관찰 훅이 pause/resume 결과를 바꾸지 않음).
        try
        {
            OnTransition?.Invoke(new DestinationTransition(destinationId, chuteNo, target, destType));
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[DestControl] OnTransition 관찰 훅 예외 격리 destId={Id}(코어 전이는 정상)", destinationId);
        }

        return new DestControlResult(DestControlOutcome.Transitioned, destType);
    }
}
