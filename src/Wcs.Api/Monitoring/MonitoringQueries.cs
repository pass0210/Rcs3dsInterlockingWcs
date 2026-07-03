using Microsoft.EntityFrameworkCore;
using Wcs.Data;

namespace Wcs.Api.Monitoring;

// ════════════════════════════════════════════════════════════════════════════
// IMonitoringQueries — F1 읽기 전용 조회 서비스(EF AsNoTracking).
//
// 기존 리포지토리(DbRepositories·Ef*Repository)를 건드리지 않는 신규 인터페이스.
// 소터 readiness는 IDestinationStatusService.Compute(재사용), 셀 현재수량은
// SorterCellQty(재사용)를 그대로 호출한다 — F1은 신규 산출 로직 0(조회 투영만).
//
// 페이징(A-3 풀스캔 방어):
//   · piece(E4)·sorter_command(E7)는 인덱스 없는 대량 테이블 → 키셋 커서(Id 내림차순) +
//     take 상한(TakeMax)으로 범위 강제. 무한/전건 로드 금지.
//   · batches(E1)·orders(E2)는 take 상한 + 필터로 범위 강제.
//
// provider-agnostic LINQ만 사용(raw SQL·SqlServer 고유 함수 금지) — in-memory SQLite
// 테스트 더블에서도 동작. enum→string 변환은 materialize 후 C#에서 수행(EF 번역 의존 0).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>F1 모니터링 페이지 ①이 소비하는 읽기 전용 조회.</summary>
public interface IMonitoringQueries
{
    IReadOnlyList<BatchDto>          GetBatches(int take);
    IReadOnlyList<OrderProgressDto>  GetOrders(long? batchId, string? status, int take);
    IReadOnlyList<OrderItemDto>      GetOrderItems(long orderId);
    PagedResult<InFlightPieceDto>    GetInFlightPieces(int take, long? cursor);
    IReadOnlyList<SorterStatusDto>   GetSorters();
    IReadOnlyList<CellStatusDto>     GetCells(long destId);
    PagedResult<SorterCommandDto>    GetSorterCommands(long? destId, int take, long? cursor);
}

/// <summary>
/// IMonitoringQueries EF 구현. 요청 스코프 WcsDbContext + 소터 상태 산출원(싱글톤) 조합.
/// MonitoringController가 요청당 조립(Program.cs DI 배선 무변경 — 정적 서빙 삽입만 유지).
/// </summary>
public sealed class MonitoringQueries : IMonitoringQueries
{
    /// <summary>take 상한(A-3 방어) — 초과 요청은 이 값으로 clamp.</summary>
    public const int TakeMax     = 200;
    /// <summary>take 기본값(미지정 시).</summary>
    public const int TakeDefault = 50;

    private readonly WcsDbContext             _db;
    private readonly ISorterGatewayRegistry   _registry;
    private readonly IDestinationStatusService _status;

    public MonitoringQueries(
        WcsDbContext              db,
        ISorterGatewayRegistry    registry,
        IDestinationStatusService status)
    {
        _db       = db;
        _registry = registry;
        _status   = status;
    }

    /// <summary>take를 [1, TakeMax]로 clamp. null/≤0 → TakeDefault.</summary>
    public static int ClampTake(int? take) =>
        take is null or <= 0 ? TakeDefault : Math.Min(take.Value, TakeMax);

    // ── E1 batches ────────────────────────────────────────────────────────────
    public IReadOnlyList<BatchDto> GetBatches(int take)
    {
        var rows = _db.WorkBatches.AsNoTracking()
            .OrderByDescending(b => b.Id)
            .Take(take)
            .ToList();

        return rows
            .Select(b => new BatchDto(
                b.Id, b.WorkDate, b.BatchNo, b.WaveNo, b.Status.ToString(), b.OpenedAt, b.ClosedAt))
            .ToList();
    }

    // ── E2 orders (order_item 합계 집계) ────────────────────────────────────────
    public IReadOnlyList<OrderProgressDto> GetOrders(long? batchId, string? status, int take)
    {
        var q = _db.Orders.AsNoTracking().AsQueryable();

        if (batchId.HasValue)
            q = q.Where(o => o.WorkBatchId == batchId.Value);

        // status 필터: 유효 enum이면 적용, 미지정이면 무필터, 잘못된 값이면 빈 결과(500 아님·일관).
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (Enum.TryParse<OrderStatus>(status, ignoreCase: true, out var parsed))
                q = q.Where(o => o.Status == parsed);
            else
                return Array.Empty<OrderProgressDto>();
        }

        var rows = q
            .OrderByDescending(o => o.Id)
            .Take(take)
            .Select(o => new
            {
                o.Id,
                o.OrderNo,
                o.OrderType,
                o.Status,
                ChuteNo  = o.Destination != null ? (int?)o.Destination.ChuteNo : null,
                Planned  = o.Items.Sum(i => (int?)i.PlannedQty)  ?? 0,
                Reserved = o.Items.Sum(i => (int?)i.ReservedQty) ?? 0,
                Sorted   = o.Items.Sum(i => (int?)i.SortedQty)   ?? 0,
            })
            .ToList();

        return rows
            .Select(o => new OrderProgressDto(
                o.Id, o.OrderNo, o.OrderType.ToString(), o.ChuteNo, o.Status.ToString(),
                o.Planned, o.Reserved, o.Sorted))
            .ToList();
    }

    // ── E3 order items ──────────────────────────────────────────────────────────
    public IReadOnlyList<OrderItemDto> GetOrderItems(long orderId)
    {
        var rows = _db.OrderItems.AsNoTracking()
            .Where(i => i.OrderId == orderId)
            .OrderBy(i => i.Id)
            .Take(TakeMax)
            .ToList();

        return rows
            .Select(i => new OrderItemDto(i.Id, i.Barcode, i.PlannedQty, i.ReservedQty, i.SortedQty))
            .ToList();
    }

    // ── E4 in-flight pieces (키셋 커서 페이징) ──────────────────────────────────
    // in-flight = IsActive && status ∈ {QUERIED, RESERVED, PERMITTED}.
    // 명시적 OR로 표현(정적 배열 Contains는 값변환 enum에서 EF 파라미터 평가가 깨짐 —
    // ReadOnlySpan 제약 위반. OR는 값 변환기를 거쳐 provider-agnostic하게 번역된다).
    public PagedResult<InFlightPieceDto> GetInFlightPieces(int take, long? cursor)
    {
        var q = _db.Pieces.AsNoTracking()
            .Where(p => p.IsActive
                     && (p.Status == PieceStatus.QUERIED
                      || p.Status == PieceStatus.RESERVED
                      || p.Status == PieceStatus.PERMITTED));

        if (cursor.HasValue)
            q = q.Where(p => p.Id < cursor.Value);

        // take+1 조회 → 다음 페이지 존재 여부 판별(추가 조회 없이 커서 산출).
        var rows = q
            .OrderByDescending(p => p.Id)
            .Take(take + 1)
            .Select(p => new
            {
                p.Id,
                p.PId,
                p.Barcode,
                p.Qty,
                ChuteNo     = p.Destination != null ? (int?)p.Destination.ChuteNo : null,
                AgvNo       = p.Agv != null ? (int?)p.Agv.AgvNo : null,
                InductionNo = p.Induction != null ? (int?)p.Induction.InductionNo : null,
                p.Status,
                p.DepositedAt,
                p.CreatedAt,
            })
            .ToList();

        bool hasMore = rows.Count > take;
        var page = (hasMore ? rows.Take(take) : rows)
            .Select(p => new InFlightPieceDto(
                p.Id, p.PId, p.Barcode, p.Qty, p.ChuteNo, p.AgvNo, p.InductionNo,
                p.Status.ToString(), p.DepositedAt, p.CreatedAt))
            .ToList();

        long? next = hasMore && page.Count > 0 ? page[^1].Id : null;
        return new PagedResult<InFlightPieceDto>(page, next);
    }

    // ── E5 sorters + readiness (registry + DestinationStatusService 재사용) ──────
    public IReadOnlyList<SorterStatusDto> GetSorters()
    {
        return _registry.AllBundles
            .OrderBy(b => b.ChuteNo)
            .Select(b =>
            {
                var r = _status.Compute(b.DestinationId, DestType.SORTER_3D);
                return new SorterStatusDto(b.DestinationId, b.ChuteNo, r.Online, r.Ready, r.Full, r.Paused);
            })
            .ToList();
    }

    // ── E6 cells (SorterCellQty 재사용 — currentQty byte-consistent) ─────────────
    public IReadOnlyList<CellStatusDto> GetCells(long destId)
    {
        var cells = _db.Cells.AsNoTracking()
            .Where(c => c.DestinationId == destId)
            .OrderBy(c => c.CellNo)
            .ToList();

        if (cells.Count == 0)
            return Array.Empty<CellStatusDto>();  // 미존재 destId → 빈 목록(일관 정책·500 아님).

        var cellIds = cells.Select(c => c.Id).ToList();

        // 현재 투입 수량 — SorterCellQty(재사용, IF-05·IF-10과 동일 산출).
        var loaded = SorterCellQty.LoadedQtyByCell(_db, destId, cellIds);

        // 활성 cell_assignment → 배정 오더번호(점유 여부 판단 겸용).
        var assigned = _db.CellAssignments.AsNoTracking()
            .Where(a => a.ReleasedAt == null && a.Cell.DestinationId == destId)
            .Select(a => new { a.CellId, a.Order.OrderNo })
            .ToList()
            .GroupBy(a => a.CellId)
            .ToDictionary(g => g.Key, g => g.First().OrderNo);

        return cells
            .Select(c => new CellStatusDto(
                c.CellNo,
                c.Capacity,
                loaded.GetValueOrDefault(c.Id, 0),
                assigned.ContainsKey(c.Id),
                c.Enabled,
                assigned.GetValueOrDefault(c.Id)))
            .ToList();
    }

    // ── E7 sorter commands (키셋 커서 페이징) ──────────────────────────────────
    public PagedResult<SorterCommandDto> GetSorterCommands(long? destId, int take, long? cursor)
    {
        var q = _db.SorterCommands.AsNoTracking().AsQueryable();

        if (destId.HasValue)
            q = q.Where(sc => sc.Cell.DestinationId == destId.Value);

        if (cursor.HasValue)
            q = q.Where(sc => sc.Id < cursor.Value);

        var rows = q
            .OrderByDescending(sc => sc.Id)
            .Take(take + 1)
            .Select(sc => new
            {
                sc.Id,
                PId     = (int?)sc.Piece.PId,
                Barcode = (string?)sc.Piece.Barcode,
                sc.CellNo,
                sc.CSeq,
                sc.RSeq,
                sc.Status,
                sc.CWrittenAt,
                sc.RFlagAt,
            })
            .ToList();

        bool hasMore = rows.Count > take;
        var page = (hasMore ? rows.Take(take) : rows)
            .Select(sc => new SorterCommandDto(
                sc.Id, sc.PId, sc.Barcode, sc.CellNo, sc.CSeq, sc.RSeq,
                sc.Status.ToString(), sc.CWrittenAt, sc.RFlagAt))
            .ToList();

        long? next = hasMore && page.Count > 0 ? page[^1].Id : null;
        return new PagedResult<SorterCommandDto>(page, next);
    }
}
