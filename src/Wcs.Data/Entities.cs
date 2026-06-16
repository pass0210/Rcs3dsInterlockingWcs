using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Wcs.Data;

// ════════════════════════════════════════════════════════════════════════════
// ERD.md §테이블(16) 그대로 구현
// 설계 원칙 (ERD.md):
//   1. PK = 대리키 bigint identity (전부)
//   2. 자연키는 UNIQUE 인덱스
//   3. p_id 필터드 유니크 (is_active=1) — SQLite는 일반 UNIQUE(p_id, is_active)
//   4. 상태 enum → HasConversion<string>() + CHECK
//   5. 이력 테이블(piece_event, plc_event, destination_event) append-only
//   6. 공통: created_at datetime2(UTC). 상태 테이블 추가: updated_at, row_version
// ════════════════════════════════════════════════════════════════════════════

// ─────────────────────────────────────────────
// 기준정보 테이블 enum
// ─────────────────────────────────────────────

/// <summary>목적지 종류 — 슈트 vs 3D Sorter.</summary>
public enum DestType { CHUTE, SORTER_3D }

/// <summary>목적지 운영 상태.</summary>
public enum DestStatus { NORMAL, PAUSED }

/// <summary>작업 배치 상태.</summary>
public enum WorkBatchStatus { WAITING, RUNNING, CLOSED }

/// <summary>오더 타입.</summary>
public enum OrderType { GENERAL, INVOICE, STORE }

/// <summary>오더 목적지 자동 배정 타입.</summary>
public enum DestAssignType { UPSTREAM, AUTO, MANUAL }

/// <summary>오더 상태.</summary>
public enum OrderStatus { WAITING, RUNNING, COMPLETED, CANCELLED }

/// <summary>피스 상태 — 슈트는 DEPOSITED 종료, 3D는 CELL_ASSIGNED→LOADED 진행.</summary>
public enum PieceStatus
{
    QUERIED, RESERVED, DENIED, PERMITTED,
    DEPOSITED, CELL_ASSIGNED, LOADED,
    MISMATCH, TIMEOUT, CANCELLED
}

/// <summary>피스 이벤트 타입.</summary>
public enum PieceEventType
{
    IF05_REQ, IF05_RES, IF08_REQ, IF08_RES,
    IF10_REQ, IF10_RES, DECISION
}

/// <summary>소터 명령 상태.</summary>
public enum SorterCommandStatus { SENT, COMPLETED, MISMATCH, TIMEOUT }

/// <summary>PLC 이벤트 종류.</summary>
public enum PlcEventKind { REG_CHANGE, WRITE, ONLINE, OFFLINE }

/// <summary>알람 심각도.</summary>
public enum AlarmSeverity { INFO, WARN, ERROR }

/// <summary>목적지 이벤트 타입 — append-only 감사.</summary>
public enum DestinationEventType
{
    CLEARED, FULL_QTY_CHANGED, CLOSED, PAUSED, RESUMED
}

// ─────────────────────────────────────────────
// 기준정보 엔티티
// ─────────────────────────────────────────────

/// <summary>목적지 (슈트 또는 3D Sorter). SORTER_3D 행이 소터 식별자.</summary>
public sealed class Destination
{
    public long       Id        { get; set; }  // PK 대리키
    public int        ChuteNo   { get; set; }  // UNIQUE
    public DestType   DestType  { get; set; }  // CHECK('CHUTE','SORTER_3D')
    public int?       Floor     { get; set; }  // 3D=NULL
    public DestStatus Status    { get; set; }  // CHECK('NORMAL','PAUSED')
    public bool       IsActive  { get; set; }

    public DateTime   CreatedAt { get; set; }
    public DateTime   UpdatedAt { get; set; }

    // 동시성 토큰 — SQL Server: rowversion / SQLite: int 버전 컬럼
    [Timestamp]
    public byte[]? RowVersion { get; set; }
    // SQLite용 정수 동시성 토큰 (provider 분기로 한 쪽만 매핑)
    public int XminRowVersion { get; set; }

    // 네비게이션
    public ChuteDetail?           ChuteDetail    { get; set; }
    public ICollection<Cell>      Cells          { get; set; } = [];
    public ICollection<WcsOrder>  Orders         { get; set; } = [];
    public ICollection<Piece>     Pieces         { get; set; } = [];
    public ICollection<DestinationEvent> Events  { get; set; } = [];
}

/// <summary>3D Sorter 셀 (SORTER_3D 목적지 소속).</summary>
public sealed class Cell
{
    public long    Id            { get; set; }  // PK 대리키
    public long    DestinationId { get; set; }  // FK → destination (SORTER_3D)
    public int     CellNo        { get; set; }
    public int?    Capacity      { get; set; }
    public bool    Enabled       { get; set; }

    public DateTime CreatedAt { get; set; }

    // 네비게이션
    public Destination              Destination  { get; set; } = null!;
    public ICollection<CellAssignment> Assignments { get; set; } = [];
    public ICollection<SorterCommand>  Commands    { get; set; } = [];
}

/// <summary>셀 배정 현황 — released_at IS NULL이면 점유 중.</summary>
public sealed class CellAssignment
{
    public long     Id         { get; set; }  // PK 대리키
    public long     CellId     { get; set; }  // FK → cell
    public long     OrderId    { get; set; }  // FK → wcs_order
    public DateTime AssignedAt { get; set; }
    public DateTime? ReleasedAt { get; set; } // NULL = 점유 중

    public DateTime CreatedAt { get; set; }

    // 네비게이션
    public Cell     Cell  { get; set; } = null!;
    public WcsOrder Order { get; set; } = null!;
}

/// <summary>AGV — agvNo→층 단일 진실. agv.floor가 런타임 소스.</summary>
public sealed class Agv
{
    public long   Id      { get; set; }  // PK 대리키
    public int    AgvNo   { get; set; }  // UNIQUE
    public int    Floor   { get; set; }
    public bool   Enabled { get; set; }

    public DateTime CreatedAt { get; set; }
}

/// <summary>프린터 기준정보.</summary>
public sealed class Printer
{
    public long    Id        { get; set; }  // PK 대리키
    public int     PrinterNo { get; set; }  // UNIQUE
    public string  Name      { get; set; } = string.Empty;
    public string? ConnInfo  { get; set; }  // IP:PORT 등
    public bool    Enabled   { get; set; }

    public DateTime CreatedAt { get; set; }

    // 네비게이션
    public ICollection<ChuteDetail> ChuteDetails { get; set; } = [];
}

/// <summary>슈트 상세 — CHUTE 전용, destination_id PK=FK (1:1).</summary>
public sealed class ChuteDetail
{
    public long     DestinationId   { get; set; }  // PK = FK(destination)
    public int      DefaultFullQty  { get; set; }  // 기본 풀: 마감 시 적용
    public int      WorkFullQty     { get; set; }  // 작업 풀: 현재 적용
    public long?    PrinterId       { get; set; }  // FK → printer NULL
    public DateTime? LastClearedAt  { get; set; }  // 마지막 비움
    public string?  Zone            { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    // 네비게이션
    public Destination Destination { get; set; } = null!;
    public Printer?    Printer     { get; set; }
}

/// <summary>인덕션 기준정보.</summary>
public sealed class Induction
{
    public long   Id          { get; set; }  // PK 대리키
    public int    InductionNo { get; set; }  // UNIQUE
    public int    Floor       { get; set; }
    public bool   Enabled     { get; set; }

    public DateTime CreatedAt { get; set; }
}

// ─────────────────────────────────────────────
// 운영 축 · 오더
// ─────────────────────────────────────────────

/// <summary>작업 배치 — 작업일자·배치·차수 3컬럼 흡수. UQ(work_date,batch_no,wave_no).</summary>
public sealed class WorkBatch
{
    public long            Id        { get; set; }  // PK 대리키
    public DateOnly        WorkDate  { get; set; }
    public string          BatchNo   { get; set; } = string.Empty;
    public int             WaveNo    { get; set; }
    public WorkBatchStatus Status    { get; set; }
    public DateTime?       OpenedAt  { get; set; }
    public DateTime?       ClosedAt  { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    [Timestamp]
    public byte[]? RowVersion { get; set; }
    public int XminRowVersion { get; set; }

    // 네비게이션
    public ICollection<WcsOrder> Orders { get; set; } = [];
}

/// <summary>
/// WCS 오더. UQ(work_batch_id, order_no). destination NULL=미할당(지연 배정).
/// dest_assign_type NULL=아직 미배정.
/// </summary>
public sealed class WcsOrder
{
    public long          Id             { get; set; }  // PK 대리키
    public long          WorkBatchId    { get; set; }  // FK → work_batch
    public string        OrderNo        { get; set; } = string.Empty;
    public OrderType     OrderType      { get; set; }
    public string?       RefNo         { get; set; }   // 송장번호/매장코드
    public string?       RefName       { get; set; }
    public long?         DestinationId  { get; set; }  // FK NULL(미할당)
    public DestAssignType? DestAssignType { get; set; } // NULL=미배정
    public DateTime?     DestAssignedAt { get; set; }
    public OrderStatus   Status         { get; set; }
    public DateTime?     StartedAt      { get; set; }
    public DateTime?     ClosedAt       { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    [Timestamp]
    public byte[]? RowVersion { get; set; }
    public int XminRowVersion { get; set; }

    // 네비게이션
    public WorkBatch               WorkBatch   { get; set; } = null!;
    public Destination?            Destination { get; set; }
    public ICollection<OrderItem>  Items       { get; set; } = [];
    public ICollection<CellAssignment> CellAssignments { get; set; } = [];
}

/// <summary>오더 항목 — 바코드 기준. UQ(order_id, barcode).</summary>
public sealed class OrderItem
{
    public long   Id          { get; set; }  // PK 대리키
    public long   OrderId     { get; set; }  // FK → wcs_order
    public string Barcode     { get; set; } = string.Empty;
    public int    PlannedQty  { get; set; }
    public int    ReservedQty { get; set; }  // IF-05 OK 시 += qty
    public int    SortedQty   { get; set; }  // IF-10·12 확정 시 += qty

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    [Timestamp]
    public byte[]? RowVersion { get; set; }
    public int XminRowVersion { get; set; }

    // 네비게이션
    public WcsOrder          Order  { get; set; } = null!;
    public ICollection<Piece> Pieces { get; set; } = [];
}

// ─────────────────────────────────────────────
// 실행·이력 엔티티
// ─────────────────────────────────────────────

/// <summary>
/// 투입 피스. p_id 1~30000 순환 — 필터드 유니크 (p_id) WHERE is_active=1.
/// 슈트: DEPOSITED 종료 / 3D: CELL_ASSIGNED→LOADED 진행.
/// </summary>
public sealed class Piece
{
    public long        Id           { get; set; }  // PK 대리키
    public int         PId          { get; set; }  // 순환 키 (1~30000)
    public bool        IsActive     { get; set; }  // 필터드 유니크 기준
    public string      Barcode      { get; set; } = string.Empty;
    public int         Qty          { get; set; }
    public DateTime?   DepositedAt  { get; set; }  // IF-10 시점(사실)
    public long        DestinationId { get; set; } // FK → destination
    public long?       OrderItemId  { get; set; }  // FK → order_item NULL(예약 라인)
    public long?       AgvId        { get; set; }  // FK → agv NULL
    public long?       InductionId  { get; set; }  // FK → induction NULL
    public PieceStatus Status       { get; set; }

    // ERD: piece/piece_event에 client_ts·created_at 컬럼은 P1에서 생성(백필 로직은 P2)
    public string?   ClientTs   { get; set; }  // RCS 원문 timeStamp 보존
    public DateTime  CreatedAt  { get; set; }
    public DateTime  UpdatedAt  { get; set; }

    [Timestamp]
    public byte[]? RowVersion { get; set; }
    public int XminRowVersion { get; set; }

    // 네비게이션
    public Destination         Destination { get; set; } = null!;
    public OrderItem?          OrderItem   { get; set; }
    public Agv?                Agv         { get; set; }
    public Induction?          Induction   { get; set; }
    public ICollection<PieceEvent>     Events   { get; set; } = [];
    public ICollection<SorterCommand>  Commands { get; set; } = [];
    public ICollection<Alarm>          Alarms   { get; set; } = [];
}

/// <summary>피스 이벤트 — append-only 이력. (piece_id, at) 보조 인덱스.</summary>
public sealed class PieceEvent
{
    public long           Id          { get; set; }  // PK 대리키
    public long           PieceId     { get; set; }  // FK → piece
    public PieceEventType EventType   { get; set; }
    public string?        Reason      { get; set; }
    public string?        PayloadJson { get; set; }  // nvarchar(max)
    public string?        ClientTs    { get; set; }  // RCS 원문 timeStamp (varchar NULL)
    public DateTime       At          { get; set; }  // UTC, 선두 인덱스

    // 네비게이션
    public Piece Piece { get; set; } = null!;
}

/// <summary>소터 명령 — 재시도=새 행 삽입.</summary>
public sealed class SorterCommand
{
    public long               Id          { get; set; }  // PK 대리키
    public long               PieceId     { get; set; }  // FK → piece
    public long               CellId      { get; set; }  // FK → cell
    public int                CSeq        { get; set; }
    public int                CellNo      { get; set; }  // 스냅샷
    public DateTime           CWrittenAt  { get; set; }
    public int?               RSeq        { get; set; }  // NULL=미수신
    public int?               RCellNo     { get; set; }
    public DateTime?          RFlagAt     { get; set; }
    public SorterCommandStatus Status     { get; set; }

    public DateTime CreatedAt { get; set; }

    // 네비게이션
    public Piece Piece { get; set; } = null!;
    public Cell  Cell  { get; set; } = null!;
}

/// <summary>PLC 이벤트 — append-only 이력. (at) 선두 인덱스.</summary>
public sealed class PlcEvent
{
    public long         Id       { get; set; }  // PK 대리키
    public PlcEventKind Kind     { get; set; }
    public string       Register { get; set; } = string.Empty; // 'D0'~'D6','D4.0'…
    public int?         OldVal   { get; set; }
    public int?         NewVal   { get; set; }
    public DateTime     At       { get; set; }  // UTC, 선두 인덱스
}

/// <summary>알람 — acked_at WHERE IS NULL 부분 인덱스.</summary>
public sealed class Alarm
{
    public long          Id        { get; set; }  // PK 대리키
    public string        Code      { get; set; } = string.Empty; // R_SEQ_MISMATCH·RFLAG_TIMEOUT·OFFLINE…
    public AlarmSeverity Severity  { get; set; }
    public long?         PieceId   { get; set; }  // FK → piece NULL
    public string        Message   { get; set; } = string.Empty;
    public DateTime      RaisedAt  { get; set; }
    public DateTime?     AckedAt   { get; set; }

    public DateTime CreatedAt { get; set; }

    // 네비게이션
    public Piece? Piece { get; set; }
}

/// <summary>목적지 이벤트 — append-only 감사. (destination_id, at) 인덱스.</summary>
public sealed class DestinationEvent
{
    public long                Id            { get; set; }  // PK 대리키
    public long                DestinationId { get; set; }  // FK → destination
    public DestinationEventType EventType    { get; set; }
    public string?             DetailJson    { get; set; }  // old/new 값
    public string?             OperatorId    { get; set; }
    public DateTime            At            { get; set; }  // UTC, (destination_id, at) 인덱스

    // 네비게이션
    public Destination Destination { get; set; } = null!;
}
