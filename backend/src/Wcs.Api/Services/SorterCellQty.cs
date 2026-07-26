using Microsoft.EntityFrameworkCore;
using Wcs.Data;

namespace Wcs.Api;

// ════════════════════════════════════════════════════════════════════════════
// SorterCellQty — 소터 셀 현재 투입 수량·작업수량 full 판정·no-overflow 수용 가부 공유 로직.
//
// 이 스프린트(S-CELL-ACCUM)의 핵심 불변식:
//   "IF-05 OK ⟹ 적재 가능"이 성립하려면, IF-05 가부 판정(DestinationStatusService)과
//   IF-10 셀 배정(EfCellSelector.SelectCell)이 **동일한 셀 수량·용량·배정-분기 산출**을 써야 한다
//   (m4p4 교훈: IF-05 predicate ↔ SelectCell 동형 필수 — 서로 다른 로직이면 크로스-엔드포인트
//   비대칭으로 IF-05 OK인데 SelectCell이 full 셀을 골라 Capacity 초과 적재가 샌다).
//
// 따라서 (1)셀 현재수량 산출(LoadedQtyByCell)·용량 도달 판정(IsCellAtCapacity),
//   (2)배정 유무 분기(no-overflow) 술어(AssignedCellsForBarcode·HasFreeEnabledCell·
//   FirstAssignedCellWithRoom·CanAcceptBarcode)를 여기 한 곳에 두고 두 호출자가 공유한다
//   (byte-consistent). 둘 다 Wcs.Api 내부이므로 internal static로 노출.
//
// 셀 현재 투입 수량 = sorter_command(status=COMPLETED) 행이 가리키는 piece.qty 합(확정2) —
//   단, **현재 활성 cell_assignment 기간**으로 스코프(S-CELL-ACCUM):
//   COMPLETED sorter_command 중 그 셀의 활성 배정 AssignedAt **이후**(CWrittenAt >= AssignedAt)만
//   합산한다. release-on-complete로 셀이 다른 오더에 재사용될 때, 이전 오더가 그 셀에 쌓은 all-time
//   COMPLETED 적재량이 새 오더의 여유 계산을 오염시키지 않도록 — **새 오더는 0부터 카운트**한다.
//   - status=COMPLETED만 = 실제 적재 성공분(SENT/MISMATCH/TIMEOUT 제외 — 적재 안 됨).
//   - 재시도=새 sorter_command 행이므로 같은 piece가 여러 COMPLETED 행을 가질 수 있다(드묾) →
//     중복 합산 금지: 현재-기간 창(>= AssignedAt) 안에서 piece별 1건.
//   - 경계 등호는 >=로 포함(EC). DateTime 비교는 in-memory(provider-neutral — provider별 SQL 없음).
// 셀 작업 투입 수량 = cell.Capacity. NULL/≤0 = 무제한(수량-full 미적용 — 확정3).
//
// no-overflow(Q-a): 한 오더는 자기 배정 셀 하나에 국한. 오더가 이 소터에 활성 배정을 보유하면
//   그 배정 셀의 여유만 본다(빈 셀 폴백 금지 — full이면 NG). 배정이 없으면(진짜 신규) 빈 enabled 셀.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>그 오더(barcode)가 이 소터에 보유한 활성 cell_assignment 셀의 스냅샷(CellNo 오름차순 사용).</summary>
internal readonly record struct AssignedCell(long CellId, int CellNo, int? Capacity);

internal static class SorterCellQty
{
    /// <summary>
    /// 셀별 현재 투입 수량(현재 활성 배정 기간 스코프)을 한 시점 스냅샷으로 산출(읽기 전용).
    /// 각 셀의 현재 활성 cell_assignment AssignedAt 이후(CWrittenAt >= AssignedAt) COMPLETED sorter_command만
    /// piece별 1건(재시도 중복 제거) qty 합산. cellIdFilter가 주어지면 그 셀들로 한정.
    /// 활성 배정이 없는 셀은 결과에 넣지 않는다(현재 기간이 없으므로 0 — 호출자는 GetValueOrDefault(…,0)).
    /// 반환 Dictionary에 없는 cellId는 현재수량 0.
    ///
    /// 성능: DB 쿼리에 **전역 하한**(가장 이른 활성 배정 AssignedAt = minFrom)을 걸어 그 이후 COMPLETED만
    ///   materialize한다 — 셀 재사용이 반복되는 장기 운영에서 셀-일생 이력 전량 fetch(O(history))를 회피.
    ///   상수 `CWrittenAt >= minFrom`은 SQLite·SQL Server 모두 번역되므로 provider-neutral이다. **셀별 정밀
    ///   하한**(`>= from`)만 in-memory로 남긴다 — 셀마다 from이 달라 단일 WHERE로 표현할 수 없기 때문(불가피).
    /// </summary>
    public static Dictionary<long, int> LoadedQtyByCell(
        WcsDbContext db, long destinationId, IReadOnlyCollection<long>? cellIdFilter = null)
    {
        // 1) 각 셀의 현재 활성 배정 AssignedAt 하한(활성은 셀당 1건 전제 — 방어적으로 Max).
        var activeFrom = db.CellAssignments
            .Where(a => a.ReleasedAt == null
                     && a.Cell.DestinationId == destinationId
                     && (cellIdFilter == null || cellIdFilter.Contains(a.CellId)))
            .GroupBy(a => a.CellId)
            .Select(g => new { CellId = g.Key, From = g.Max(x => x.AssignedAt) })
            .ToList()
            .ToDictionary(x => x.CellId, x => x.From);

        if (activeFrom.Count == 0)
            return new Dictionary<long, int>();

        var cellIds = activeFrom.Keys.ToList();       // IN-list 번역 견고화(materialize).
        var minFrom = activeFrom.Values.Min();         // 전역 하한 = 가장 이른 활성 배정 AssignedAt.

        // 2) DB 측 전역 하한(>= minFrom)으로 fetch 범위를 좁힌 뒤(이력 전량 방지), 셀별 정밀 하한(>= from)과
        //    piece 중복 제거는 in-memory(셀별 from 차이 — 단일 WHERE 불가). minFrom 상수 비교는 provider-neutral.
        var cmds = db.SorterCommands
            .Where(sc => sc.Status == SorterCommandStatus.COMPLETED
                      && sc.ArchivedAt == null   // S-B2C-DATAGEN: 아카이브(재테스트 초기화) 제외 — 이중 카운트 차단.
                      && sc.Cell.DestinationId == destinationId
                      && cellIds.Contains(sc.CellId)
                      && sc.CWrittenAt >= minFrom)
            .Select(sc => new { sc.CellId, sc.PieceId, sc.Piece.Qty, sc.CWrittenAt })
            .ToList();

        var result = new Dictionary<long, int>();
        foreach (var cellGroup in cmds.GroupBy(x => x.CellId))
        {
            var from = activeFrom[cellGroup.Key];  // cellIds로 걸렀으므로 항상 존재.
            int sum = cellGroup
                .Where(x => x.CWrittenAt >= from)   // 셀별 정밀 하한(현재 배정 기간 이후만·EC: >= 포함).
                .GroupBy(x => x.PieceId)            // piece별 1건(재시도 COMPLETED 중복 제거).
                .Sum(pg => pg.First().Qty);
            if (sum != 0)
                result[cellGroup.Key] = sum;
        }
        return result;
    }

    /// <summary>
    /// 셀이 작업 투입 수량에 도달(full)했는가. Capacity NULL/≤0 = 무제한(절대 full 아님 — 확정3).
    /// 양수일 때만 현재수량 ≥ Capacity → full(경계 등호 포함 — EC-6).
    /// </summary>
    public static bool IsCellAtCapacity(int? capacity, int currentQty) =>
        capacity is int cap && cap > 0 && currentQty >= cap;

    // ── no-overflow(Q-a) 배정 유무 분기 공유 술어 — IF-05·IF-10 동형의 단일 진실 ─────────────

    /// <summary>
    /// 그 오더(barcode)가 이 소터에 보유한 활성 cell_assignment 셀 목록(CellNo 오름차순).
    /// released_at IS NULL 배정 중 그 배정 오더가 barcode 항목을 가진 것 = "그 오더 보유 셀".
    /// </summary>
    public static List<AssignedCell> AssignedCellsForBarcode(WcsDbContext db, long destinationId, string barcode) =>
        db.CellAssignments
            .Where(a => a.ReleasedAt == null
                     && a.Cell.DestinationId == destinationId
                     && a.Order.Items.Any(i => i.Barcode == barcode))
            .Select(a => new { a.CellId, a.Cell.CellNo, a.Cell.Capacity })
            .Distinct()   // ②-중복 배정 레이스(같은 셀 활성 배정 2건) 보험 — 셀당 1행.
            .ToList()
            .Select(x => new AssignedCell(x.CellId, x.CellNo, x.Capacity))
            .OrderBy(x => x.CellNo)
            .ToList();

    /// <summary>빈 enabled 셀(활성 cell_assignment 없는 셀) 존재 여부 — SelectCell ②분기와 동형.</summary>
    public static bool HasFreeEnabledCell(WcsDbContext db, long destinationId) =>
        db.Cells.Any(c => c.DestinationId == destinationId
                       && c.Enabled
                       && !db.CellAssignments.Any(a => a.CellId == c.Id && a.ReleasedAt == null));

    /// <summary>
    /// 배정 셀 중 여유(현재수량 &lt; Capacity, NULL/≤0=무제한) 있는 **최저 CellNo** 셀(없으면 null).
    /// 현재수량은 배정-기간 스코프 LoadedQtyByCell 공유(byte-consistent). 결정적 선택(CellNo 오름차순).
    /// </summary>
    public static AssignedCell? FirstAssignedCellWithRoom(
        WcsDbContext db, long destinationId, IReadOnlyList<AssignedCell> assignedCells)
    {
        if (assignedCells.Count == 0) return null;
        var loaded = LoadedQtyByCell(db, destinationId, assignedCells.Select(x => x.CellId).ToList());
        foreach (var c in assignedCells)   // 이미 CellNo 오름차순.
            if (!IsCellAtCapacity(c.Capacity, loaded.GetValueOrDefault(c.CellId, 0)))
                return c;
        return null;   // 보유 셀 전부 작업수량 도달 → 오버플로 금지(빈 셀 폴백 없음).
    }

    /// <summary>
    /// IF-05 piece 단위 수용 가부(소터) — 배정 유무 분기(no-overflow). SelectCell 비-null 조건과 **동형**.
    ///   · 오더가 이 소터에 활성 배정 보유 → 그 배정 셀 중 여유 셀 존재 여부만(빈 셀 폴백 금지).
    ///   · 배정 없음(진짜 신규)          → 빈 enabled 셀 존재 여부.
    /// "IF-05 OK ⟺ SelectCell 적재 가능"(계약 §88)을 크로스-엔드포인트로 보장한다.
    /// </summary>
    public static bool CanAcceptBarcode(WcsDbContext db, long destinationId, string barcode)
    {
        var assigned = AssignedCellsForBarcode(db, destinationId, barcode);
        if (assigned.Count > 0)
            return FirstAssignedCellWithRoom(db, destinationId, assigned) is not null;  // 폴백 금지.
        return HasFreeEnabledCell(db, destinationId);
    }
}
