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
/// <remarks>
/// S-TWO-FLOOR-CONTROL C1 — 처리 3시각 노출(계측 가시화). RFlagAt → TiltedAt 개명 + DepositedAt·ReturnedAt
/// 신설(append-only — 기존 필드 제거 0). 프론트 결선은 본 스프린트 스코프 밖.
/// </remarks>
public sealed record SorterCommandDto(
    long      Id,
    int?      PId,
    string?   Barcode,
    int       CellNo,
    int       CSeq,
    int?      RSeq,
    string    Status,       // SorterCommandStatus(SENT/COMPLETED/MISMATCH/TIMEOUT)
    DateTime  CWrittenAt,
    DateTime? DepositedAt,   // 3DS 투입(IF-10 보고) — 항상 non-NULL
    DateTime? TiltedAt,      // 셀 틸트(R_Flag==1 관측) — 성공·불일치 non-NULL (구 RFlagAt)
    DateTime? ReturnedAt);   // 복귀 완료(Ready 0→1) — 성공만 non-NULL

/// <summary>
/// F2 operation_log 조회 항목 — 테일 초기 백로그(읽기 전용·커서 페이징). enum→문자열.
/// SignalR OpLog push payload(OpLogEntryDto)와 형상 호환(프론트 통합 소비).
/// </summary>
public sealed record OperationLogDto(
    long     Id,
    DateTime At,
    string   Category,        // OperationLogCategory(API/PLC_WRITE/POLL_CHANGE/HANDSHAKE/STATE)
    string   Action,
    string   Level,           // OperationLogLevel(INFO/WARN/ERROR)
    int?     SorterChuteNo,
    long?    DestinationId,
    string?  Barcode,
    int?     PId,
    string?  Detail);

/// <summary>
/// 키셋(커서) 페이징 결과. NextCursor가 null이면 마지막 페이지.
/// 커서는 마지막 항목의 Id(내림차순 정렬 → 다음 페이지는 Id &lt; cursor).
/// </summary>
public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    long?            NextCursor);

/// <summary>
/// 전 목적지(CHUTE + SORTER_3D) 열거 항목 — GET /api/monitor/destinations(S-B2C-FACILITY A2).
/// 설비 관리 페이지의 목적지 목록·슈트 제어 destId 소스. readiness 는 DestinationStatusService 재사용.
/// CHUTE 는 workFullQty/lastClearedAt(chute_detail), SORTER_3D 는 cellTotal/cellEnabled 를 채운다.
/// </summary>
public sealed record DestinationDto(
    long      Id,
    int       ChuteNo,
    string    DestType,        // "CHUTE" | "SORTER_3D"
    int?      Floor,
    string    Status,          // "NORMAL" | "PAUSED"
    bool      IsActive,
    // readiness(DestinationStatusService.Compute 산출 — 슈트=capacity/paused, 소터=운영상태+full/paused)
    bool      Online,
    bool      Ready,
    bool      Full,
    bool      Paused,
    // CHUTE 전용(chute_detail) — SORTER_3D 는 null.
    int?      WorkFullQty,
    DateTime? LastClearedAt,
    // SORTER_3D 전용(cell 집계) — CHUTE 는 null.
    int?      CellTotal,
    int?      CellEnabled);
