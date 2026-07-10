using Microsoft.EntityFrameworkCore;
using Wcs.Data;

namespace Wcs.Tests.E2E;

// ════════════════════════════════════════════════════════════════════════════
// E2ESeed — 셀 만재/배정 ground-truth 시드 헬퍼(SorterCellFullnessTests 패턴 재사용).
//
// 셀 현재수량은 sorter_command(COMPLETED) JOIN piece.qty(확정2)로 산출되므로,
// LoadCellQty가 "실 sorter_command/piece DB 상태" ground-truth를 구성한다(인메모리 카운터 0).
// production DbSeeder 변경 금지(계약 §3.2) — 테스트가 추가 시드로 만재/경계를 만든다.
// ════════════════════════════════════════════════════════════════════════════
public static class E2ESeed
{
    /// <summary>그 소터의 enabled 셀을 cnt개 점유(활성 cell_assignment). 그 소터의 RUNNING 오더에 귀속.</summary>
    public static void OccupyCells(WcsDbContext db, long sorterDestId, int cnt)
    {
        var order = db.Orders.First(o => o.DestinationId == sorterDestId && o.Status == OrderStatus.RUNNING);
        var occupied = db.CellAssignments
            .Where(a => a.Cell.DestinationId == sorterDestId && a.ReleasedAt == null)
            .Select(a => a.CellId).ToHashSet();
        var freeCells = db.Cells
            .Where(c => c.DestinationId == sorterDestId && c.Enabled && !occupied.Contains(c.Id))
            .OrderBy(c => c.CellNo).Take(cnt).ToList();
        var now = DateTime.UtcNow;
        foreach (var cell in freeCells)
            db.CellAssignments.Add(new CellAssignment
            {
                CellId = cell.Id, OrderId = order.Id, AssignedAt = now, ReleasedAt = null, CreatedAt = now,
            });
        db.SaveChanges();
    }

    public static int FreeCellCount(WcsDbContext db, long sorterDestId)
    {
        var occupied = db.CellAssignments
            .Where(a => a.Cell.DestinationId == sorterDestId && a.ReleasedAt == null)
            .Select(a => a.CellId).ToHashSet();
        return db.Cells.Count(c => c.DestinationId == sorterDestId && c.Enabled && !occupied.Contains(c.Id));
    }

    /// <summary>그 소터의 모든 enabled 셀 작업수량(cell.Capacity)을 cap으로 설정(양수=수량-full 활성).</summary>
    public static void SetAllCapacities(WcsDbContext db, long sorterDestId, int? cap)
    {
        foreach (var c in db.Cells.Where(c => c.DestinationId == sorterDestId).ToList())
            c.Capacity = cap;
        db.SaveChanges();
    }

    /// <summary>
    /// 그 셀(cellNo)에 qty짜리 piece를 적재 — piece + COMPLETED sorter_command 1건 삽입.
    /// 셀 현재 투입 수량 = sorter_command(COMPLETED) JOIN piece.qty 합 → 실 DB ground-truth.
    /// </summary>
    public static void LoadCellQty(WcsDbContext db, long sorterDestId, int cellNo, int qty, int pId, string barcode)
    {
        var now  = DateTime.UtcNow;
        var cell = db.Cells.First(c => c.DestinationId == sorterDestId && c.CellNo == cellNo);

        var piece = new Piece
        {
            PId = pId, IsActive = true, Barcode = barcode, Qty = qty, DepositedAt = now,
            DestinationId = sorterDestId, Status = PieceStatus.LOADED, CreatedAt = now, UpdatedAt = now,
        };
        db.Pieces.Add(piece);
        db.SaveChanges();

        db.SorterCommands.Add(new SorterCommand
        {
            PieceId = piece.Id, CellId = cell.Id, CSeq = 1, CellNo = cellNo, CWrittenAt = now,
            RSeq = 1, RCellNo = cellNo, RFlagAt = now, Status = SorterCommandStatus.COMPLETED, CreatedAt = now,
        });
        db.SaveChanges();
    }

    /// <summary>그 소터에 매핑된 별도 오더(새 바코드) + 빈 배정 셀 1개. EC-1/HP-1 동형.</summary>
    public static (long orderId, long cellId) AddSorterOrderWithAssignedCell(
        WcsDbContext db, long sorterDestId, string orderNo, string barcode, int cellNo)
    {
        var now   = DateTime.UtcNow;
        var batch = db.WorkBatches.First();
        var order = new WcsOrder
        {
            WorkBatchId = batch.Id, OrderNo = orderNo, OrderType = OrderType.GENERAL,
            DestinationId = sorterDestId, DestAssignType = DestAssignType.UPSTREAM, DestAssignedAt = now,
            Status = OrderStatus.RUNNING, StartedAt = now, CreatedAt = now, UpdatedAt = now,
        };
        db.Orders.Add(order);
        db.SaveChanges();
        db.OrderItems.Add(new OrderItem
        {
            OrderId = order.Id, Barcode = barcode, PlannedQty = 100, ReservedQty = 0, SortedQty = 0,
            CreatedAt = now, UpdatedAt = now,
        });
        db.SaveChanges();

        var cell = db.Cells.First(c => c.DestinationId == sorterDestId && c.CellNo == cellNo);
        db.CellAssignments.Add(new CellAssignment
        {
            CellId = cell.Id, OrderId = order.Id, AssignedAt = now, ReleasedAt = null, CreatedAt = now,
        });
        db.SaveChanges();
        return (order.Id, cell.Id);
    }

    /// <summary>
    /// 그 소터에 매핑된 오더(새 바코드) — **셀 배정 없음**(SelectCell ②가 자연 배정). 누적/완료/재사용 시나리오용.
    /// PlannedQty를 작게 주면 그만큼 분류 시 오더가 완료(SortedQty==PlannedQty)되어 셀이 release된다.
    /// </summary>
    public static long AddSorterOrder(
        WcsDbContext db, long sorterDestId, string orderNo, string barcode, int plannedQty)
    {
        var now   = DateTime.UtcNow;
        var batch = db.WorkBatches.First();
        var order = new WcsOrder
        {
            WorkBatchId = batch.Id, OrderNo = orderNo, OrderType = OrderType.GENERAL,
            DestinationId = sorterDestId, DestAssignType = DestAssignType.UPSTREAM, DestAssignedAt = now,
            Status = OrderStatus.RUNNING, StartedAt = now, CreatedAt = now, UpdatedAt = now,
        };
        db.Orders.Add(order);
        db.SaveChanges();
        db.OrderItems.Add(new OrderItem
        {
            OrderId = order.Id, Barcode = barcode, PlannedQty = plannedQty, ReservedQty = 0, SortedQty = 0,
            CreatedAt = now, UpdatedAt = now,
        });
        db.SaveChanges();
        return order.Id;
    }

    /// <summary>그 오더(orderNo)의 활성 cell_assignment(ReleasedAt IS NULL) 수.</summary>
    public static int ActiveAssignmentsForOrder(WcsDbContext db, string orderNo) =>
        db.CellAssignments.Count(a => a.Order.OrderNo == orderNo && a.ReleasedAt == null);

    /// <summary>그 오더(orderNo)의 released cell_assignment 수.</summary>
    public static int ReleasedAssignmentsForOrder(WcsDbContext db, string orderNo) =>
        db.CellAssignments.Count(a => a.Order.OrderNo == orderNo && a.ReleasedAt != null);

    /// <summary>그 소터의 COMPLETED sorter_command 기준 셀별 적재 수량 합(piece별 1건 DISTINCT).</summary>
    public static int LoadedQtyForDestination(WcsDbContext db, long sorterDestId) =>
        db.SorterCommands
            .Where(c => c.Status == SorterCommandStatus.COMPLETED && c.Cell.DestinationId == sorterDestId)
            .Select(c => new { c.CellId, c.PieceId, c.Piece.Qty })
            .Distinct().ToList().Sum(x => x.Qty);
}
