using Microsoft.Extensions.Options;
using Wcs.Core;
using Wcs.Data;

namespace Wcs.Api;

// ════════════════════════════════════════════════════════════════════════════
// DestinationStatusService — 목적지(슈트/소터) 상태 단일 산출 경로.
//
// RCS↔WCS 재설계 Phase 1, Scope E:
//   목적지별 full / paused / online / ready 를 한 함수로 접는다.
//   ① IF-05 NG 필터(FULL/PAUSED→NG)가 이를 소비.
//   ② Phase 2 아웃바운드 푸시(WCS→RCS destination-status)가 동일 산출을 재사용할 확장점.
//
//   - 일반 슈트(CHUTE): ready = 만재 아님 && 정지 아님(비활성 포함).
//   - 3D 소터 슈트(SORTER_3D): ready = online && CurFloor==운영층 && Ready==1
//                              && 만재 아님(빈 셀 있음) && 정지 아님.
//
// Wcs.Core(DepositDecider)는 순수 유지 — 여기서 스냅샷+hold를 모아 Decide를 호출.
// 개별 full/paused 필드를 외부(API 응답)로 내보내지 않는다(Phase 1) — IF-05 NG 필터로만 소비.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 목적지 상태 산출 결과(내부 표현 — RCS로 직접 전송하지 않음).
/// Ready 하나로 접되, 산출 근거(Full/Paused/Online/내부 사유)를 함께 보존해
/// IF-05 NG 필터·Phase 2 푸시가 같은 산출을 재사용한다.
/// </summary>
public sealed record DestinationReadiness(
    bool       Ready,        // 지금 받을 수 있는가(접힌 단일 플래그 — Phase 2 푸시 페이로드의 ready)
    bool       Full,         // 만재
    bool       Paused,       // 정지(비활성 포함)
    bool       Online,       // 소터 온라인(슈트는 항상 true)
    DenyReason Reason);      // 받을 수 없는 내부 사유(Ready=true면 None)

/// <summary>
/// 목적지 상태(full/ready) 단일 산출 인터페이스.
/// IF-05 NG 필터가 Compute로 소비. Phase 2 푸시가 동일 Compute 재사용.
/// </summary>
public interface IDestinationStatusService
{
    /// <summary>
    /// destination(슈트/소터)의 현재 수용 가능 여부를 단일 산출.
    /// 슈트면 ChuteCapacityService hold만, 소터면 게이트웨이 스냅샷 + DepositDecider까지 접는다.
    /// </summary>
    DestinationReadiness Compute(long destinationId, DestType destType);
}

/// <summary>
/// IDestinationStatusService 구현 — full/ready 산출의 단일 지점.
/// 운영층(OperationalFloor)은 설정값 주입(하드코딩 금지 — 절대규칙 #7).
/// </summary>
public sealed class DestinationStatusService : IDestinationStatusService
{
    private readonly IChuteCapacityService  _capacity;
    private readonly ISorterGatewayRegistry _sorterRegistry;
    private readonly int                    _operationalFloor;

    public DestinationStatusService(
        IChuteCapacityService    capacity,
        ISorterGatewayRegistry   sorterRegistry,
        IOptions<WcsOptions>     options)
    {
        _capacity         = capacity;
        _sorterRegistry   = sorterRegistry;
        _operationalFloor = options.Value.OperationalFloor;
    }

    /// <inheritdoc/>
    public DestinationReadiness Compute(long destinationId, DestType destType)
    {
        if (destType == DestType.SORTER_3D)
            return ComputeSorter(destinationId);

        return ComputeChute(destinationId);
    }

    // ── 일반 슈트: full / paused (ChuteCapacityService hold) ───────────────────
    private DestinationReadiness ComputeChute(long destinationId)
    {
        var hold = _capacity.GetHold(destinationId);

        return hold switch
        {
            WcsHold.Full   => new DestinationReadiness(Ready: false, Full: true,  Paused: false, Online: true, DenyReason.Full),
            WcsHold.Paused => new DestinationReadiness(Ready: false, Full: false, Paused: true,  Online: true, DenyReason.Paused),
            _              => new DestinationReadiness(Ready: true,  Full: false, Paused: false, Online: true, DenyReason.None),
        };
    }

    // ── 3D 소터 슈트: online + 정렬 ready (full/paused는 상류에서 처리) ────────
    //
    // Phase 1 범위:
    //   - PAUSED(소터 정지): destination.status==PAUSED는 IF-05의 QueryDestination이
    //     이미 상류에서 차단(NG PAUSED)하므로 여기서 Paused로 다시 막지 않는다
    //     (소터는 ChuteCapacityService 집계 대상이 아님 — GetHold 호출 금지).
    //   - FULL(셀 만재): 빈 셀 산출은 IF-10 셀 선택 시점 판단(현 동작 보존) — Phase 1 IF-05
    //     필터는 셀 만재로 배정을 막지 않는다(Full=false). Phase 2에서 셀 가용성을 ready에 접을 확장점.
    //   - Ready(접힌 플래그): online && CurFloor==운영층 && Ready==1 — Phase 2 푸시 재사용 재료.
    //     IF-05는 Ready=false(미정렬·BUSY)여도 차단하지 않는다(BUSY→OK·이동, 도착 후 정렬).
    private DestinationReadiness ComputeSorter(long destinationId)
    {
        var bundle = _sorterRegistry.GetBundle(destinationId);

        // 번들 없음 → 미구성/OFFLINE.
        if (bundle is null)
            return new DestinationReadiness(Ready: false, Full: false, Paused: false, Online: false, DenyReason.Offline);

        var snap = bundle.Latest;
        // DepositDecider(순수)로 정렬·준비 산출 — Phase 2 푸시 ready의 재료.
        var decision = DepositDecider.Decide(snap, _operationalFloor, WcsHold.None);

        bool online = snap.Online;
        bool ready  = decision.Ready; // online && CurFloor==운영층 && Ready==1

        // Phase 1: 소터 full/paused는 상류(QueryDestination status·IF-10 셀선택)에서 처리 → IF-05 필터는 미차단.
        return new DestinationReadiness(ready, Full: false, Paused: false, online, ready ? DenyReason.None : decision.Reason);
    }
}
