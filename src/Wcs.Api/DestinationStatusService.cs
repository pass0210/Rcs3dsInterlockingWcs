using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wcs.Core;
using Wcs.Data;

namespace Wcs.Api;

// ════════════════════════════════════════════════════════════════════════════
// DestinationStatusService — 목적지(슈트/소터) 상태 단일 산출 경로.
//
// RCS↔WCS 재설계 Phase 1, Scope E + 소터 셀 만재 판정(m4p4):
//   목적지별 full / paused / online / ready 를 한 함수로 접는다.
//   ① IF-05 NG 필터(FULL/PAUSED→NG)가 이를 소비.
//   ② Phase 2 아웃바운드 푸시(WCS→RCS destination-status)가 동일 산출을 재사용.
//
//   - 일반 슈트(CHUTE): ready = 만재 아님 && 정지 아님(비활성 포함).
//   - 3D 소터 슈트(SORTER_3D): ready = online && CurFloor==운영층 && Ready==1
//                              && 만재 아님(빈 셀 있음) && 정지 아님.
//
// m4p4 — 소터 full/paused 실산출(Phase 1의 Full:false/Paused:false 하드코딩 대체):
//   - SorterFull = 그 소터 enabled 셀 중 미점유(활성 cell_assignment 없는) 셀 0개.
//                  단일 원자 쿼리로 평가(check-then-act 분리 금지 — "빈셀0인데 ready=true"가
//                  한 순간도 새지 않도록). 읽기 전용(배정 부수효과 없음 — EfCellSelector 재활용).
//   - paused    = destination.Status==PAUSED 또는 IsActive==false(슈트 ComputeChute와 동형).
//   - DB 접근(cell/cell_assignment/destination)은 IServiceScopeFactory로 scoped WcsDbContext
//     취득(확정3 — 싱글톤 captive dependency 회피). DepositDecider(순수) 무변경.
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
    /// 슈트면 ChuteCapacityService hold만, 소터면 게이트웨이 스냅샷 + DepositDecider + 셀 만재까지 접는다.
    /// </summary>
    DestinationReadiness Compute(long destinationId, DestType destType);

    /// <summary>
    /// IF-05 piece-aware 예외(사용자 확정1) — 그 소터에 barcode의 오더가 활성 cell_assignment를
    /// 이미 보유하는지(읽기 전용). 보유하면 SorterFull이어도 그 piece는 자기 셀에 누적 가능(OK).
    /// EfCellSelector의 ①분기(같은 오더 활성 assignment 재사용)와 동형 — 단 배정 부수효과 없음.
    /// 푸시 ready(목적지 단위)는 이 예외를 쓰지 않는다(새 오더 수용 가부는 빈셀 유무).
    /// </summary>
    bool SorterHasActiveAssignmentForBarcode(long destinationId, string barcode);
}

/// <summary>
/// IDestinationStatusService 구현 — full/ready 산출의 단일 지점.
/// 운영층(OperationalFloor)은 설정값 주입(하드코딩 금지 — 절대규칙 #7).
/// </summary>
public sealed class DestinationStatusService : IDestinationStatusService
{
    private readonly IChuteCapacityService  _capacity;
    private readonly ISorterGatewayRegistry _sorterRegistry;
    private readonly IServiceScopeFactory   _scopeFactory;
    private readonly int                    _operationalFloor;

    public DestinationStatusService(
        IChuteCapacityService    capacity,
        ISorterGatewayRegistry   sorterRegistry,
        IServiceScopeFactory     scopeFactory,
        IOptions<WcsOptions>     options)
    {
        _capacity         = capacity;
        _sorterRegistry   = sorterRegistry;
        _scopeFactory     = scopeFactory;
        _operationalFloor = options.Value.OperationalFloor;
    }

    /// <inheritdoc/>
    public DestinationReadiness Compute(long destinationId, DestType destType)
    {
        if (destType == DestType.SORTER_3D)
            return ComputeSorter(destinationId);

        return ComputeChute(destinationId);
    }

    /// <inheritdoc/>
    public bool SorterHasActiveAssignmentForBarcode(long destinationId, string barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode)) return false;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();

        // EfCellSelector ①분기와 동형(읽기 전용): 그 소터 셀에 released_at IS NULL인 배정이
        // 있고, 그 배정 오더의 항목 중 barcode 매칭이 있으면 재사용 가능.
        return db.CellAssignments.Any(a =>
            a.ReleasedAt == null
            && a.Cell.DestinationId == destinationId
            && a.Order.Items.Any(i => i.Barcode == barcode));
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

    // ── 3D 소터 슈트: online + 정렬 ready + 셀 만재(full) + 정지(paused) ──────────
    //
    // m4p4 산출(목적지 단위 — 푸시 ready·IF-05 NG 공통 소비):
    //   - Online : snap.Online (번들 없음 → false=OFFLINE).
    //   - Paused : destination.Status==PAUSED || IsActive==false(슈트 ComputeChute와 동형).
    //   - Full   : 그 소터 enabled 셀 중 미점유(활성 cell_assignment 없는) 셀 0개.
    //              단일 원자 쿼리로 평가 — "빈셀0인데 ready=true" 모순이 한 순간도 새지 않게.
    //   - Ready  : !Full && !Paused && online && CurFloor==운영층 && Ready==1.
    //              (decision.Ready = online && CurFloor==운영층 && Ready==1 — 정렬·BUSY 판정.)
    //
    // DenyReason 우선순위: Offline > Paused > Full > decision.Reason(정렬·BUSY).
    // IF-05는 Ready=false(미정렬·BUSY)여도 차단하지 않는다(availability는 Full/Paused만 차단) —
    //   여기서 산출한 Full/Paused만 IF-05 NG 필터가 소비하고, 미정렬·BUSY는 OK·이동 후 정렬.
    private DestinationReadiness ComputeSorter(long destinationId)
    {
        var bundle = _sorterRegistry.GetBundle(destinationId);

        // 번들 없음 → 미구성/OFFLINE (최우선 — DB 조회 불요).
        if (bundle is null)
            return new DestinationReadiness(Ready: false, Full: false, Paused: false, Online: false, DenyReason.Offline);

        var snap = bundle.Latest;
        // DepositDecider(순수)로 정렬·준비 산출 — full/paused와 합성 전 재료.
        var decision = DepositDecider.Decide(snap, _operationalFloor, WcsHold.None);
        bool online = snap.Online;

        // ── DB 조회: paused(destination 상태) + full(빈 셀 산출) ───────────────────
        // 확정3: 싱글톤이 scoped WcsDbContext를 직접 못 받으므로 IServiceScopeFactory로 스코프 생성.
        bool paused;
        bool full;
        using (var scope = _scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();

            // paused: destination.Status==PAUSED 또는 비활성(IsActive==false). 미존재도 paused로 취급.
            // 한 번의 조회로 두 플래그(존재·정지)를 가져온다.
            var destInfo = db.Destinations
                .Where(d => d.Id == destinationId)
                .Select(d => new { d.Status, d.IsActive })
                .FirstOrDefault();

            paused = destInfo is null
                  || !destInfo.IsActive
                  || destInfo.Status == DestStatus.PAUSED;

            // full: 그 소터 enabled 셀 중 활성 cell_assignment가 없는(미점유) 셀이 하나도 없으면 Full.
            // 단일 원자 쿼리(check-then-act 분리 없음) — released_at IS NULL을 점유 기준으로.
            // !Any(빈셀) == Full. (셀 자체가 0개여도 빈셀 0 → Full — 미구성 소터는 받을 수 없음.)
            bool hasFreeCell = db.Cells.Any(c =>
                c.DestinationId == destinationId
                && c.Enabled
                && !db.CellAssignments.Any(a => a.CellId == c.Id && a.ReleasedAt == null));
            full = !hasFreeCell;
        }

        bool ready = !full && !paused && decision.Ready;

        // DenyReason 우선순위: Offline > Paused > Full > decision.Reason.
        DenyReason reason =
            !online ? DenyReason.Offline
          : paused  ? DenyReason.Paused
          : full    ? DenyReason.Full
          : decision.Reason;  // None(ready) 또는 정렬·BUSY 사유.

        return new DestinationReadiness(ready, full, paused, online, ready ? DenyReason.None : reason);
    }
}
