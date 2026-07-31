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
    IReadOnlyList<DestinationDto>    GetDestinations();
    IReadOnlyList<CellStatusDto>     GetCells(long destId);
    PagedResult<SorterCommandDto>    GetSorterCommands(long? destId, int take, long? cursor);
    PagedResult<OperationLogDto>     GetOperationLog(
        string? category, string? level, int? sorterChuteNo, int take, long? cursor);
    CycleTimeAvgDto                  GetCycleTimeAvg();
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
                     && p.ArchivedAt == null   // S-B2C-DATAGEN: 아카이브(재테스트 초기화) piece 제외.
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

    // ── A2 destinations (전 목적지 열거 — 설비 관리 페이지·슈트 제어 destId 소스) ───
    // 읽기 전용·부수효과 0. readiness 는 DestinationStatusService.Compute 재사용(새 판정 0).
    //   · CHUTE: workFullQty/lastClearedAt(chute_detail) + Compute(GetHold 기반) full/paused/ready.
    //   · SORTER_3D: cellTotal/cellEnabled(cell 집계) + Compute(ComputeSorter — 게이트웨이 스냅샷).
    // chute_detail·cell 은 materialize 후 C#에서 조립(EF 번역 의존 0 — provider-agnostic).
    public IReadOnlyList<DestinationDto> GetDestinations()
    {
        var dests = _db.Destinations.AsNoTracking()
            .OrderBy(d => d.ChuteNo)
            .Select(d => new { d.Id, d.ChuteNo, d.DestType, d.Floor, d.Status, d.IsActive })
            .ToList();

        // CHUTE chute_detail(workFullQty/lastClearedAt) — 한 번에 조회 후 맵.
        var chuteDetails = _db.ChuteDetails.AsNoTracking()
            .Select(cd => new { cd.DestinationId, cd.WorkFullQty, cd.LastClearedAt })
            .ToList()
            .ToDictionary(x => x.DestinationId, x => (x.WorkFullQty, x.LastClearedAt));

        // SORTER_3D 셀 집계(total/enabled) — destination_id 별 그룹.
        var sorterIds = dests.Where(d => d.DestType == DestType.SORTER_3D).Select(d => d.Id).ToHashSet();
        var cellAgg = _db.Cells.AsNoTracking()
            .Where(c => sorterIds.Contains(c.DestinationId))
            .Select(c => new { c.DestinationId, c.Enabled })
            .ToList()
            .GroupBy(c => c.DestinationId)
            .ToDictionary(g => g.Key, g => (Total: g.Count(), Enabled: g.Count(x => x.Enabled)));

        var result = new List<DestinationDto>(dests.Count);
        foreach (var d in dests)
        {
            var r = _status.Compute(d.Id, d.DestType);   // readiness 단일 산출(재사용).

            int? workFullQty = null; DateTime? lastClearedAt = null;
            int? cellTotal = null; int? cellEnabled = null;

            if (d.DestType == DestType.CHUTE && chuteDetails.TryGetValue(d.Id, out var cd))
            {
                workFullQty   = cd.WorkFullQty;
                lastClearedAt = cd.LastClearedAt;
            }
            else if (d.DestType == DestType.SORTER_3D)
            {
                var agg = cellAgg.GetValueOrDefault(d.Id, (Total: 0, Enabled: 0));
                cellTotal   = agg.Total;
                cellEnabled = agg.Enabled;
            }

            result.Add(new DestinationDto(
                d.Id, d.ChuteNo, d.DestType.ToString(), d.Floor, d.Status.ToString(), d.IsActive,
                r.Online, r.Ready, r.Full, r.Paused,
                workFullQty, lastClearedAt, cellTotal, cellEnabled));
        }
        return result;
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
        // S-B2C-DATAGEN: 아카이브(재테스트 초기화) sorter_command 제외.
        var q = _db.SorterCommands.AsNoTracking().Where(sc => sc.ArchivedAt == null);

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
                sc.DepositedAt,
                sc.TiltedAt,
                sc.ReturnedAt,
            })
            .ToList();

        bool hasMore = rows.Count > take;
        var page = (hasMore ? rows.Take(take) : rows)
            .Select(sc => new SorterCommandDto(
                sc.Id, sc.PId, sc.Barcode, sc.CellNo, sc.CSeq, sc.RSeq,
                sc.Status.ToString(), sc.CWrittenAt, sc.DepositedAt, sc.TiltedAt, sc.ReturnedAt))
            .ToList();

        long? next = hasMore && page.Count > 0 ? page[^1].Id : null;
        return new PagedResult<SorterCommandDto>(page, next);
    }

    // ── F2 operation_log (테일 백로그 — 키셋 커서·필터·POLL_CHANGE 기본 제외) ─────
    // 선두 인덱스(at/id) 활용: Id 내림차순 키셋(최신순). take clamp(E7 패턴 재사용).
    // category 미지정 → 고빈도 POLL_CHANGE 기본 제외(테일 폭주 방지·스트림 정책과 동형).
    // 명시 category=POLL_CHANGE면 그 카테고리만(옵트인). AsNoTracking·기존 리포지토리 무변경·스키마 0.
    public PagedResult<OperationLogDto> GetOperationLog(
        string? category, string? level, int? sorterChuteNo, int take, long? cursor)
    {
        var q = _db.OperationLogs.AsNoTracking().AsQueryable();

        // category: 유효 enum이면 그 카테고리만, 미지정이면 POLL_CHANGE 제외, 잘못된 값이면 빈 결과(일관·500 아님).
        if (!string.IsNullOrWhiteSpace(category))
        {
            if (Enum.TryParse<OperationLogCategory>(category, ignoreCase: true, out var cat))
                q = q.Where(x => x.Category == cat);
            else
                return new PagedResult<OperationLogDto>(Array.Empty<OperationLogDto>(), null);
        }
        else
        {
            q = q.Where(x => x.Category != OperationLogCategory.POLL_CHANGE);  // 기본 제외(옵트인).
        }

        // level 필터(선택): 유효 enum이면 적용, 잘못된 값이면 빈 결과(일관).
        if (!string.IsNullOrWhiteSpace(level))
        {
            if (Enum.TryParse<OperationLogLevel>(level, ignoreCase: true, out var lvl))
                q = q.Where(x => x.Level == lvl);
            else
                return new PagedResult<OperationLogDto>(Array.Empty<OperationLogDto>(), null);
        }

        if (sorterChuteNo.HasValue)
            q = q.Where(x => x.SorterChuteNo == sorterChuteNo.Value);

        if (cursor.HasValue)
            q = q.Where(x => x.Id < cursor.Value);

        var rows = q
            .OrderByDescending(x => x.Id)
            .Take(take + 1)
            .Select(x => new
            {
                x.Id, x.At, x.Category, x.Action, x.Level,
                x.SorterChuteNo, x.DestinationId, x.Barcode, x.PId, x.Detail,
            })
            .ToList();

        bool hasMore = rows.Count > take;
        var page = (hasMore ? rows.Take(take) : rows)
            .Select(x => new OperationLogDto(
                x.Id, x.At, x.Category.ToString(), x.Action, x.Level.ToString(),
                x.SorterChuteNo, x.DestinationId, x.Barcode, x.PId, x.Detail))
            .ToList();

        long? next = hasMore && page.Count > 0 ? page[^1].Id : null;
        return new PagedResult<OperationLogDto>(page, next);
    }

    // ── 평균 사이클 시간(분류시작~복귀) — 읽기 전용 집계(S-SORT-CYCLE-TIME-METRIC) ──────────────
    // sorter_command 전 행(★ ArchivedAt 무필터 — 초기화/아카이브 이전 행 전부 포함) 중 SortStartedAt·
    //   ReturnedAt 둘 다 non-NULL인 행에 대해 Σ(ReturnedAt − SortStartedAt).TotalSeconds / n.
    // provider-neutral: (SortStartedAt, ReturnedAt) 2컬럼만 materialize 후 C# TimeSpan 계산(SqlServer/Sqlite
    //   동일 수치 — provider 고유 date/datediff SQL 0). AsNoTracking·핫패스 무접촉(이 조회 요청 시에만 실행).
    // n=0 → AvgSeconds=null·N=0(0 나눗셈 없이 200 반환·500 금지). 단조 불변식상 음수 미발생(방어 클램프만).
    public CycleTimeAvgDto GetCycleTimeAvg()
    {
        var pairs = _db.SorterCommands.AsNoTracking()
            .Where(sc => sc.SortStartedAt != null && sc.ReturnedAt != null)
            .Select(sc => new { sc.SortStartedAt, sc.ReturnedAt })
            .ToList();

        int n = pairs.Count;
        if (n == 0)
            return new CycleTimeAvgDto(null, 0);   // 측정 데이터 없음 — null·200(비크래시).

        double totalSeconds = 0;
        foreach (var p in pairs)
        {
            double sec = (p.ReturnedAt!.Value - p.SortStartedAt!.Value).TotalSeconds;
            if (sec < 0) sec = 0;   // 단조상 미발생 — 시계 비단조 방어 클램프(음수 제외 로직 불요).
            totalSeconds += sec;
        }

        return new CycleTimeAvgDto(totalSeconds / n, n);   // raw double(초) — 소수 표기는 프론트(#7).
    }
}
