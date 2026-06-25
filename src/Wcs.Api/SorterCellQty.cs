using Wcs.Data;

namespace Wcs.Api;

// ════════════════════════════════════════════════════════════════════════════
// SorterCellQty — 소터 셀 현재 투입 수량·작업수량 full 판정 공유 로직.
//
// 이 스프린트(소터 셀 작업수량 full)의 핵심 불변식:
//   "IF-05 OK ⟹ 적재 가능"이 성립하려면, IF-05 가부 판정(DestinationStatusService)과
//   IF-10 셀 배정(EfCellSelector.SelectCell)이 **동일한 셀 수량·용량 산출**을 써야 한다
//   (m4p4 교훈: IF-05 predicate ↔ SelectCell 동형 필수 — 서로 다른 로직이면 크로스-엔드포인트
//   비대칭으로 IF-05 OK인데 SelectCell이 full 셀을 골라 Capacity 초과 적재가 샌다).
//
// 따라서 셀 현재수량 산출(LoadedQtyByCell)과 용량 도달 판정(IsCellAtCapacity)을 여기 한 곳에 두고
// 두 호출자가 공유한다(byte-consistent). 둘 다 Wcs.Api 내부이므로 internal static로 노출.
//
// 셀 현재 투입 수량 = sorter_command(status=COMPLETED) 행이 가리키는 piece.qty 합(확정2).
//   - status=COMPLETED만 = 실제 적재 성공분(SENT/MISMATCH/TIMEOUT 제외 — 적재 안 됨).
//   - 재시도=새 sorter_command 행이므로 같은 piece가 여러 COMPLETED 행을 가질 수 있다(드묾) →
//     중복 합산 금지: (cellId, pieceId) DISTINCT 후 합산(piece별 1건).
// 셀 작업 투입 수량 = cell.Capacity. NULL/≤0 = 무제한(수량-full 미적용 — 확정3).
// ════════════════════════════════════════════════════════════════════════════

internal static class SorterCellQty
{
    /// <summary>
    /// 셀별 현재 투입 수량(LOADED qty 합)을 한 시점 스냅샷으로 산출(읽기 전용).
    /// sorter_command(COMPLETED) JOIN piece.qty를 (cellId, pieceId, qty) DISTINCT 후 cellId로 합산
    /// (piece 재시도 COMPLETED 행 중복 합산 0). cellIdFilter가 주어지면 그 셀들로 한정.
    /// 반환 Dictionary에 없는 cellId는 현재수량 0(적재 이력 없음).
    /// </summary>
    public static Dictionary<long, int> LoadedQtyByCell(
        WcsDbContext db, long destinationId, IReadOnlyCollection<long>? cellIdFilter = null)
    {
        var perPiece = db.SorterCommands
            .Where(sc => sc.Status == SorterCommandStatus.COMPLETED
                      && sc.Cell.DestinationId == destinationId
                      && (cellIdFilter == null || cellIdFilter.Contains(sc.CellId)))
            .Select(sc => new { sc.CellId, sc.PieceId, sc.Piece.Qty })
            .Distinct()   // (cellId, pieceId, qty) 단위 — 같은 piece 재시도 COMPLETED 행 중복 제거.
            .ToList();

        return perPiece
            .GroupBy(x => x.CellId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Qty));
    }

    /// <summary>
    /// 셀이 작업 투입 수량에 도달(full)했는가. Capacity NULL/≤0 = 무제한(절대 full 아님 — 확정3).
    /// 양수일 때만 현재수량 ≥ Capacity → full(경계 등호 포함 — EC-6).
    /// </summary>
    public static bool IsCellAtCapacity(int? capacity, int currentQty) =>
        capacity is int cap && cap > 0 && currentQty >= cap;
}
