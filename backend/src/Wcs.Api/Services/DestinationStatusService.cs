using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wcs.Core;
using Wcs.Data;

namespace Wcs.Api;

// ════════════════════════════════════════════════════════════════════════════
// DestinationStatusService — 목적지(슈트/소터) 상태 단일 산출 경로.
//
// RCS↔WCS 재설계 Phase 1, Scope E + 소터 셀 만재 판정(m4p4) + 셀 작업수량 full
// + 소터 push ready를 운영상태로 좁힘(S-소터push운영상태):
//   목적지별 full / paused / online / ready 를 한 함수로 접는다.
//   ① IF-05 dispatch 게이트가 이를 소비 — 단 소터는 ready(운영상태)가 아니라
//      Paused + SorterCanAcceptBarcode(셀 기준)만 본다(RcsController.cs IF-05).
//   ② 아웃바운드 수용상태 푸시(WCS→RCS UpdateChuteState)가 ready(운영상태)를 소비.
//
//   - 일반 슈트(CHUTE): ready = 만재 아님 && 정지 아님(비활성 포함).
//   - 3D 소터 슈트(SORTER_3D): ready = **운영상태만** = online && CurFloor==운영층 && Ready==1.
//        ★ 셀 만재(SorterFull)·정지(PAUSED)는 push ready 합성에서 **제외**한다. 이 둘은
//          push가 아니라 IF-05 dispatch 게이트에서만 차단된다(2단계 게이트 분리):
//            · "받을 수 있는 운영 상태인가"(online·정렬·비분류) = push ready.
//            · "지금 이 piece를 보낼 목적지가 있는가"(셀 여유·미정지) = IF-05 dispatch.
//        ★ Full/Paused/Online/Reason 필드는 계속 산출한다(IF-05·내부 사유 소비) — ready 합성에서만 뺀다.
//
// m4p4 — 소터 full/paused 실산출(Phase 1의 Full:false/Paused:false 하드코딩 대체).
// 셀 작업수량 full — 소터 셀 full을 "빈 셀 없음"에서 **셀 작업 투입 수량 기반**으로 정밀화(확정1~4):
//   - 셀 현재 투입 수량 = 그 셀에 적재(LOADED)된 piece.qty 합.
//     산출원(확정2) = sorter_command(status=COMPLETED) JOIN piece.qty. piece별 1건만(재시도=새 행 →
//     중복 합산 금지: DISTINCT piece로 카운트). cell_assignment는 핸드셰이크마다 released라 비사용.
//   - 셀 작업 투입 수량 = cell.Capacity(재활용). NULL/≤0 = 무제한(그 셀 수량-full 미적용 — 확정3).
//     양수일 때만 현재수량 ≥ Capacity → 그 셀 full.
//   - SorterFull(목적지 단위·확정1) = 빈 enabled 셀 없음 AND 모든 활성 배정 셀이 작업수량 도달.
//     빈 셀 ≥1 또는 작업수량 미달 배정 셀 ≥1이면 SorterFull=false(그 채널로 수용 가능).
//     같은 스코프(동일 DbContext) 순차 읽기로 평가. 읽기 전용(배정 부수효과 없음).
//     (참고: push ready는 더 이상 SorterFull에 의존하지 않으므로 "꽉 찼는데 ready=true"는
//      이제 모순이 아니다 — Full은 IF-05 dispatch만 차단. SorterFull은 IF-05·내부 사유용.)
//   - IF-05 piece-aware(확정1)는 같은 셀 수량 산출을 재사용하되 "그 piece 오더의 배정 셀"만 별도
//     체크 — 오더 셀 보유 AND 그 셀 여유(현재 < 작업수량)면 OK. 셀 꽉 차면 그 piece도 full(NG).
//   - paused    = destination.Status==PAUSED 또는 IsActive==false(슈트 ComputeChute와 동형).
//   - DB 접근(cell/cell_assignment/sorter_command/piece/destination)은 IServiceScopeFactory로 scoped
//     WcsDbContext 취득(확정3 — 싱글톤 captive dependency 회피). DepositDecider(순수) 무변경.
//
// Wcs.Core(DepositDecider)는 순수 유지 — 여기서 스냅샷+hold를 모아 Decide를 호출.
// 개별 full/paused 필드를 외부(API 응답)로 내보내지 않는다(Phase 1) — IF-05 NG 필터로만 소비.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 목적지 상태 산출 결과(내부 표현 — RCS로 직접 전송하지 않음).
/// Ready 하나로 접되, 산출 근거(Full/Paused/Online/내부 사유)를 함께 보존해
/// IF-05 dispatch·Phase 2 푸시가 같은 산출을 분기 소비한다.
///   - 푸시(IF-08)는 Ready만 소비(소터 Ready=운영상태, 슈트 Ready=만재·정지).
///   - IF-05 dispatch는 소터에 대해 Ready가 아니라 Paused+SorterCanAcceptBarcode를 소비.
/// </summary>
public sealed record DestinationReadiness(
    bool       Ready,        // 푸시 페이로드의 ready. 슈트=만재 아님&&정지 아님 / 소터=운영상태(online·정렬·Ready==1).
    bool       Full,         // 만재(소터: SorterFull). 소터 push ready 합성에는 미반영 — IF-05·내부 사유 전용.
    bool       Paused,       // 정지(비활성 포함). 소터 push ready 합성에는 미반영 — IF-05·내부 사유 전용.
    bool       Online,       // 소터 온라인(슈트는 항상 true)
    DenyReason Reason);      // 받을 수 없는 내부 사유(Ready=true면 None). 소터는 운영상태 사유(Offline·NotAligned·Busy)만.

/// <summary>
/// 목적지 상태(full/ready) 단일 산출 인터페이스.
/// Phase 2 푸시가 Compute().Ready를 소비. IF-05 dispatch는 소터에 대해 Full/Paused·셀 술어를 소비.
/// </summary>
public interface IDestinationStatusService
{
    /// <summary>
    /// destination(슈트/소터)의 현재 상태를 단일 산출.
    /// 슈트면 ChuteCapacityService hold만, 소터면 게이트웨이 스냅샷 + DepositDecider + 셀 만재까지 접는다.
    /// 소터 Ready는 **운영상태만**(online·정렬·Ready==1) — Full·Paused는 Ready 합성에서 제외하되 필드로 보존.
    /// </summary>
    DestinationReadiness Compute(long destinationId, DestType destType);

    /// <summary>
    /// IF-05 piece-aware 예외(사용자 확정1) — 그 소터에 barcode의 오더가 활성 cell_assignment를
    /// 이미 보유하고 그 배정 셀 중 **여유 있는 셀**(현재 투입 수량 &lt; cell.Capacity, NULL/≤0=무제한)이
    /// 하나라도 있는지(읽기 전용). 그런 셀이 있으면 SorterFull이어도 그 piece는 자기 셀에 누적 가능(OK).
    /// EfCellSelector의 ①분기(같은 오더 활성 assignment 재사용)와 동형 — 단 배정 부수효과 없음,
    /// 그리고 m4p4 "오더 셀 보유=무조건 OK"에서 "오더 셀 보유 AND 그 셀 여유"로 좁혔다(이 스프린트).
    /// 푸시 ready(목적지 단위)는 이 예외를 쓰지 않는다(SorterFull은 빈셀+전 배정셀 작업수량으로 산출).
    /// 셀 현재 투입 수량 산출원은 SorterFull과 동일(sorter_command COMPLETED JOIN piece.qty — 공유).
    /// </summary>
    bool SorterHasAssignedCellWithRoomForBarcode(long destinationId, string barcode);

    /// <summary>
    /// IF-05 piece 단위 수용 가부(소터) — 그 piece(barcode 오더)를 이 소터가 지금 받을 수 있는가.
    /// = (빈 enabled 셀 ≥1) OR (그 오더의 활성 배정 셀 중 여유 있는 셀 보유).
    /// EfCellSelector.SelectCell의 비-null 조건(①여유 배정 셀 재사용 → ②빈 셀 할당)과 **동형** —
    ///   "IF-05 OK ⟹ 적재 가능"(계약 §88) 불변식을 크로스-엔드포인트로 보장한다(MAJOR-1 정합).
    /// 목적지-단위 SorterFull(푸시 ready)과 다르다: SorterFull은 "아무 piece라도 받을 수 있나"이고,
    ///   이것은 "이 piece(이 오더)를 받을 수 있나"이다(다른 오더의 여유 셀은 이 piece에 무용).
    /// 셀 수량 산출원은 SorterCellQty 공유(IF-10 SelectCell·SorterFull과 byte-consistent).
    /// </summary>
    bool SorterCanAcceptBarcode(long destinationId, string barcode);
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
    public bool SorterHasAssignedCellWithRoomForBarcode(long destinationId, string barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode)) return false;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        // 그 오더의 활성 배정 셀 중 여유(현재수량 < Capacity, NULL/≤0=무제한) 있는 셀 보유 여부(읽기 전용).
        // SorterCellQty 공유 — IF-10 SelectCell ①분기·SorterFull과 byte-consistent(현재수량 배정-기간 스코프).
        return SorterCellQty.FirstAssignedCellWithRoom(
            db, destinationId, SorterCellQty.AssignedCellsForBarcode(db, destinationId, barcode)) is not null;
    }

    /// <inheritdoc/>
    public bool SorterCanAcceptBarcode(long destinationId, string barcode)
    {
        if (string.IsNullOrWhiteSpace(barcode)) return false;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();

        // SelectCell 비-null 조건과 **동형**(배정 유무 분기·no-overflow) — 단일 진실 SorterCellQty 공유:
        //   ①오더 배정 보유 → 그 배정 셀 여유만(빈 셀 폴백 금지) / ②배정 없음 → 빈 enabled 셀.
        //   "IF-05 OK ⟺ SelectCell 적재 가능"(§88)을 크로스-엔드포인트로 보장.
        return SorterCellQty.CanAcceptBarcode(db, destinationId, barcode);
    }

    // ── 소터 목적지-단위 full(SorterFull) 산출 — 확정1 ───────────────────────────
    //
    // SorterFull = 빈 enabled 셀 없음 AND 모든 활성 배정 셀이 작업수량 도달.
    //   빈 셀(점유 없음·새 오더 수용 가능)이 ≥1 → Full=false.
    //   배정 셀(활성 cell_assignment 보유) 중 작업수량 미달(현재 < Capacity, 무제한 포함)이 ≥1 →
    //     기존 오더가 그 셀로 수용 가능 → Full=false.
    //   둘 다 없으면(빈 셀 0 AND 전 배정 셀 작업수량 도달) → 아무것도 못 받음 → Full=true.
    //   (enabled 셀 자체가 0개여도 빈 셀 0·배정 셀 0 → 두 채널 모두 없음 → Full=true. 미구성 소터.)
    //
    // 산출은 같은 스코프(동일 DbContext)에서 2개 쿼리를 순차 실행한다 — ①enabled 셀+점유여부+Capacity,
    // ②점유 셀들의 현재수량(LoadedQtyByCell). 단일 SQL 트랜잭션은 아니나 읽기 전용 보수적 스냅샷이며,
    // 두 쿼리 사이 race가 있어도 다음 관찰/IF-05에서 재평가(eventually consistent).
    // (이 스프린트 전엔 ready=!full&&…이라 "full ⟹ !ready" 불변식이 있었으나, 이제 push ready는
    //  운영상태(decision.Ready)만 보므로 full은 ready와 독립이다 — Full은 IF-05 dispatch·내부 사유 전용.)
    // 셀 현재수량 산출원(SorterCellQty)은 IF-05 piece-aware·IF-10 SelectCell과 공유.
    private static bool ComputeSorterFull(WcsDbContext db, long destinationId)
    {
        // enabled 셀 목록 + 각 셀의 점유 여부(활성 cell_assignment) + Capacity 를 한 시점으로 수집.
        var cells = db.Cells
            .Where(c => c.DestinationId == destinationId && c.Enabled)
            .Select(c => new
            {
                c.Id,
                c.Capacity,
                Occupied = db.CellAssignments.Any(a => a.CellId == c.Id && a.ReleasedAt == null),
            })
            .ToList();

        // 빈 enabled 셀(점유 안 됨)이 하나라도 있으면 새 오더 수용 가능 → Full 아님.
        if (cells.Any(c => !c.Occupied))
            return false;

        // 여기 도달 = enabled 셀 전부 점유(또는 enabled 셀 0개). 배정 셀의 작업수량 도달 여부 확인.
        var occupiedCellIds = cells.Select(c => c.Id).ToList();
        if (occupiedCellIds.Count == 0)
            return true;  // enabled 셀 0개 — 받을 채널 없음(미구성 소터 = Full).

        var loaded = SorterCellQty.LoadedQtyByCell(db, destinationId, occupiedCellIds);

        // 배정(점유) 셀 중 하나라도 작업수량 미달(여유)이면 기존 오더 수용 가능 → Full 아님.
        foreach (var c in cells)
        {
            int current = loaded.GetValueOrDefault(c.Id, 0);
            if (!SorterCellQty.IsCellAtCapacity(c.Capacity, current))
                return false;
        }

        // 빈 셀 0 AND 전 배정 셀 작업수량 도달 → 아무것도 못 받음 → Full.
        return true;
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

    // ── 3D 소터 슈트: push ready=운영상태(online+정렬+비분류) / Full·Paused는 산출만(ready 제외) ──
    //
    // 산출(목적지 단위):
    //   - Online : snap.Online (번들 없음 → false=OFFLINE).
    //   - Paused : destination.Status==PAUSED || IsActive==false(슈트 ComputeChute와 동형).
    //              [push ready 합성 제외] IF-05 dispatch(RcsController)·내부 사유 전용.
    //   - Full(SorterFull, 확정1) : 빈 enabled 셀 없음 AND 모든 활성 배정 셀이 작업수량 도달.
    //              즉 새 오더도(빈 셀로) 기존 오더도(여유 배정 셀로) 아무것도 못 받는 상태.
    //              빈 셀 ≥1 또는 작업수량 미달 배정 셀 ≥1이면 Full=false(그 채널로 수용 가능).
    //              셀 현재수량 = sorter_command(COMPLETED) JOIN piece.qty(piece별 1건) — IF-05 공유.
    //              [push ready 합성 제외] IF-05 dispatch(SorterCanAcceptBarcode)·내부 사유 전용.
    //   - Ready  : **운영상태만** = online && CurFloor==운영층 && Ready==1 (= decision.Ready).
    //              ★ Full·Paused를 ready 합성에서 뺀다(S-소터push운영상태). 만재·정지여도 운영상태가
    //                OK면 push ready=true. push 소비자(DestinationStatusPusher)가 이 ready를 발화.
    //              ★ 2단계 게이트 분리: "받을 수 있는 운영상태인가"=push ready,
    //                "이 piece를 보낼 셀/미정지인가"=IF-05 dispatch(Paused+SorterCanAcceptBarcode).
    //
    // DenyReason(내부 — 외부 미노출): ready 산출과 정합하게 **운영상태 사유**만 보존한다.
    //   ready=true(운영 OK)면 None. ready=false면 Offline(번들/오프라인) 또는 decision.Reason
    //   (NotAligned·Busy). Full/Paused는 ready를 좌우하지 않으므로 ready-deny 사유에 넣지 않는다
    //   (그 둘은 Full/Paused 필드로 별도 보존 — IF-05·로깅이 필드를 직접 본다).
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

        // ── DB 조회: paused(destination 상태) + full(셀 작업수량 산출) ─────────────
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

            full = ComputeSorterFull(db, destinationId);
        }

        // push ready = 운영상태(online && 정렬 && Ready==1)만. Full·Paused 제외(S-소터push운영상태).
        // decision.Ready는 DepositDecider가 !online이면 false이므로 offline도 자동 false로 접힌다.
        bool ready = decision.Ready;

        // DenyReason(내부) — ready와 정합하는 운영상태 사유만. Full/Paused는 ready를 좌우하지 않으므로
        // ready-deny 사유에서 제외(각자 Full/Paused 필드로 별도 보존 — IF-05·로깅이 필드를 직접 본다).
        //   ready=true → None. !online → Offline. 그 외 → decision.Reason(NotAligned·Busy).
        DenyReason reason =
            ready     ? DenyReason.None
          : !online   ? DenyReason.Offline
          :             decision.Reason;  // NotAligned·Busy(또는 Offline — decision이 이미 판정).

        return new DestinationReadiness(ready, full, paused, online, reason);
    }
}
