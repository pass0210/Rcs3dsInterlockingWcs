namespace Wcs.Api.Monitoring;

// ════════════════════════════════════════════════════════════════════════════
// F1 모니터링 읽기 표면 DTO — /api/monitor/* 반환 형상(카멜케이스 JSON).
//
// 전부 읽기 전용 조회 결과. 도메인 엔티티를 그대로 노출하지 않고(순환 참조·과다 노출 방지)
// 페이지 ①(작업데이터/이동중/분류)이 소비하는 필드만 평면 DTO로 투영한다.
// enum은 문자열로 노출(프론트 표시·필터용).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>E1 work_batch 목록 항목.</summary>
public sealed record BatchDto(
    long      Id,
    DateOnly  WorkDate,
    string    BatchNo,
    int       WaveNo,
    string    Status,      // WorkBatchStatus(WAITING/RUNNING/CLOSED)
    DateTime? OpenedAt,
    DateTime? ClosedAt);

/// <summary>E2 오더 진행 — order_item 합계(planned/reserved/sorted) 집계.</summary>
public sealed record OrderProgressDto(
    long   Id,
    string OrderNo,
    string OrderType,             // OrderType(GENERAL/INVOICE/STORE)
    int?   DestinationChuteNo,    // 미배정(AUTO) 오더는 null
    string Status,                // OrderStatus(WAITING/RUNNING/COMPLETED/CANCELLED)
    int    PlannedQty,
    int    ReservedQty,
    int    SortedQty);

/// <summary>E3 오더아이템(바코드 단위 진행).</summary>
public sealed record OrderItemDto(
    long   Id,
    string Barcode,
    int    PlannedQty,
    int    ReservedQty,
    int    SortedQty);

/// <summary>E4 이동중(in-flight) piece — status ∈ QUERIED/RESERVED/PERMITTED.</summary>
public sealed record InFlightPieceDto(
    long      Id,
    int       PId,
    string    Barcode,
    int       Qty,
    int?      DestinationChuteNo,
    int?      AgvNo,
    int?      InductionNo,
    string    Status,             // PieceStatus
    DateTime? DepositedAt,
    DateTime  CreatedAt);

/// <summary>E5 소터 목록 + readiness(DestinationStatusService.Compute 산출).</summary>
public sealed record SorterStatusDto(
    long DestId,
    int  ChuteNo,
    bool Online,
    bool Ready,
    bool Full,
    bool Paused);

/// <summary>E6 셀 현황 — currentQty는 SorterCellQty(재사용) 산출.</summary>
public sealed record CellStatusDto(
    int     CellNo,
    int?    Capacity,       // NULL/≤0 = 무제한
    int     CurrentQty,     // LOADED(COMPLETED) piece.qty 합
    bool    Occupied,       // 활성 cell_assignment 보유
    bool    Enabled,
    string? AssignedOrderNo);

/// <summary>E7 소터 명령(적재 이력) — piece·cell JOIN.</summary>
public sealed record SorterCommandDto(
    long      Id,
    int?      PId,
    string?   Barcode,
    int       CellNo,
    int       CSeq,
    int?      RSeq,
    string    Status,       // SorterCommandStatus(SENT/COMPLETED/MISMATCH/TIMEOUT)
    DateTime  CWrittenAt,
    DateTime? RFlagAt);

/// <summary>
/// 키셋(커서) 페이징 결과. NextCursor가 null이면 마지막 페이지.
/// 커서는 마지막 항목의 Id(내림차순 정렬 → 다음 페이지는 Id &lt; cursor).
/// </summary>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    long?            NextCursor);
