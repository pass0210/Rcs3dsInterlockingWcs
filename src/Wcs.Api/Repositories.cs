using System.Collections.Concurrent;

namespace Wcs.Api;

// ════════════════════════════════════════════════════════════════════════════
// 기준정보 도메인 모델 (M3 인메모리, M4 EF Core 엔티티로 교체)
// ════════════════════════════════════════════════════════════════════════════

/// <summary>목적지 종류 — 3D Sorter 셀 vs 슈트.</summary>
public enum DestinationType { Chute, Sorter3D }

/// <summary>
/// 오더 항목 — 바코드 기준정보.
/// M4에서 order_item 테이블로 교체.
/// </summary>
public sealed class OrderItem
{
    public int             PId          { get; set; }   // 예약 차감 후 연결됨 (투입 후 기록)
    public required string Barcode      { get; set; }
    public required string OrderNo      { get; set; }
    public DestinationType DestType     { get; set; }   // Chute=슈트, Sorter3D=3D소터
    public int?            ChuteNo      { get; set; }   // 오더에 목적지 고정 시 설정, NULL=AUTO 배정
    public int             TotalQty     { get; set; }
    public int             ReservedQty  { get; set; }   // 예약 차감된 수량 (투입 중 물량 반영)
    public bool            IsCompleted  { get; set; }   // 오더 완료 (COMPLETED NG)
    public bool            IsOver       { get; set; }   // 수량 초과 (OVER NG)
    /// <summary>WcsHold PAUSED 상태(기준정보에서 일시정지). FULL은 M4.</summary>
    public bool            IsPaused     { get; set; }
}

/// <summary>
/// 슈트 기준정보.
/// M4에서 destination 테이블로 교체.
/// </summary>
public sealed class ChuteInfo
{
    public int  ChuteNo     { get; set; }
    public bool IsEnabled   { get; set; }   // 운영 중(true)·비활성(false)
    public int  Capacity    { get; set; }   // 최대 수량 (M4 FULL 계산용)
    public int  CurrentQty  { get; set; }   // 현재 적재 수량 (M4 FULL 계산용)
}

/// <summary>
/// 3DS 셀 기준정보.
/// M4에서 cell, cell_assignment 테이블로 교체.
/// </summary>
public sealed class CellInfo
{
    public int  CellNo        { get; set; }
    public int  SorterChuteNo { get; set; }  // 소속 소터 대상 슈트번호 (=destination.chute_no)
    public bool IsEnabled     { get; set; }  // 운영 중
    public bool IsOccupied    { get; set; }  // 현재 점유 중(핸드셰이크 진행 중)
    /// <summary>활성 오더 바코드 — 같은 오더 재사용 식별.</summary>
    public string? ActiveOrderBarcode { get; set; }
}

// ════════════════════════════════════════════════════════════════════════════
// 투입 기록 모델 (M4에서 piece + piece_event로 교체)
// ════════════════════════════════════════════════════════════════════════════

public enum DepositStatus { Ok, Denied, Reported }

/// <summary>
/// 투입 기록 — IF-05 OK/NG 및 IF-10 보고를 통합 기록(IF-16 역할).
/// M4에서 piece·piece_event 테이블로 교체.
/// </summary>
public sealed class DepositRecord
{
    public int            PId          { get; set; }
    public int            AgvNo        { get; set; }
    public required string Barcode     { get; set; }
    public int            InductionNo  { get; set; }
    public int?           ChuteNo      { get; set; }    // NG이면 null
    public int            Qty          { get; set; }
    public DepositStatus  Status       { get; set; }    // Ok·Denied·Reported
    public required string Reason      { get; set; }    // IF-05 reason
    public DateTimeOffset RecordedAt   { get; set; }
    /// <summary>IF-10 보고 수신 여부.</summary>
    public bool           IsReported   { get; set; }
}

// ════════════════════════════════════════════════════════════════════════════
// 리포지토리 인터페이스 (M4 교체점 — 구현체 교체 1지점)
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 오더·목적지 조회 + 예약 차감.
/// M4에서 EF Core 구현으로 교체.
/// </summary>
public interface IOrderRepository
{
    /// <summary>
    /// 바코드로 오더 조회 + 목적지/상태 판정 + OK 시 예약 차감.
    /// 반환: (result:"OK"|"NG", chuteNo, reason).
    /// OK 시 chuteNo는 오더 지정 또는 AUTO 배정 슈트.
    /// NG 시 chuteNo=null.
    /// </summary>
    (string Result, int? ChuteNo, string Reason, DestinationType? DestType) QueryDestination(
        int pId, int agvNo, string barcode, int inductionNo, int qty);

    /// <summary>IF-10 보고 시 목적지 종류 조회(3D면 IF-11 트리거).</summary>
    DestinationType? GetDestType(int pId);
}

/// <summary>
/// 투입 기록 (IF-16 통합 — IF-05 OK/NG·IF-10 보고 모두 기록).
/// M4에서 EF Core 구현으로 교체.
/// </summary>
public interface IDepositRecorder
{
    /// <summary>IF-05 결과(OK·NG 무관) 투입 기록.</summary>
    void RecordDestinationQuery(
        int pId, int agvNo, string barcode, int inductionNo,
        int? chuteNo, int qty, DepositStatus status, string reason);

    /// <summary>IF-10 투입 보고 기록 — 멱등(pId 중복 무해).</summary>
    /// <returns>이미 기록된(중복) 경우 false, 신규 기록 true.</returns>
    bool RecordDeposit(int pId, string barcode, int chuteNo, int agvNo, int? qty);

    /// <summary>pId 기록 존재 여부 (IF-10 멱등 확인).</summary>
    bool HasDepositRecord(int pId);
}

/// <summary>
/// 3D Sorter 셀 선택 (IF-11 트리거 전 셀 지정).
/// 선택 우선순위: ①활성 셀 재사용 → ②소속 빈 셀 → ③없으면 null(FULL 요소).
/// M4에서 EF Core + cell_assignment로 교체.
/// </summary>
public interface ICellSelector
{
    /// <summary>
    /// 대상 슈트에 할당할 셀 선택.
    /// null이면 빈 셀 없음(→ 3DS FULL 경고 — M4 FULL 계산 연동).
    /// </summary>
    int? SelectCell(int chuteNo, string barcode);

    /// <summary>셀 점유 해제(핸드셰이크 완료·실패 시 호출 — M4에서 cell_assignment 상태 전이).</summary>
    void ReleaseCell(int cellNo);
}

/// <summary>
/// agvNo → 층 번호 산출.
/// M3: appsettings Floors:AgvNoToFloor 설정값.
/// M4: agv.floor 단일 진실 전환.
/// 매핑 없는 agvNo는 명시적 거부(추측 금지 — 절대규칙 8).
/// </summary>
public interface IAgvFloorResolver
{
    /// <summary>
    /// agvNo에 해당하는 층 번호.
    /// 매핑 없으면 null (호출자가 400 Bad Request 처리).
    /// </summary>
    int? Resolve(int agvNo);
}

// ════════════════════════════════════════════════════════════════════════════
// 인메모리 구현체 (M4 교체 전 임시 — DB 의존성 0)
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 인메모리 오더 리포지토리.
/// 시드 데이터로 테스트 가능. thread-safe(lock).
/// M4에서 EF Core InMemoryOrderRepository로 교체.
/// </summary>
public sealed class InMemoryOrderRepository : IOrderRepository
{
    private readonly List<ChuteInfo>  _chutes;
    private readonly List<OrderItem>  _orders;
    private readonly object _lock = new();

    public InMemoryOrderRepository(List<OrderItem> orders, List<ChuteInfo> chutes)
    {
        _orders = orders;
        _chutes = chutes;
    }

    public (string Result, int? ChuteNo, string Reason, DestinationType? DestType) QueryDestination(
        int pId, int agvNo, string barcode, int inductionNo, int qty)
    {
        lock (_lock)
        {
            // 바코드로 오더 조회
            var order = _orders.FirstOrDefault(o => o.Barcode == barcode);

            if (order is null)
                return ("NG", null, "NO_DEST", null);

            // 상태 판정 (우선순위 순)
            if (order.IsOver)
                return ("NG", null, "OVER", order.DestType);
            if (order.IsCompleted)
                return ("NG", null, "COMPLETED", order.DestType);
            if (order.IsPaused)
                return ("NG", null, "PAUSED", order.DestType);  // WcsHold.Paused 기준정보

            // 목적지 결정
            int? chuteNo = order.ChuteNo;

            if (chuteNo is null)
            {
                // AUTO 배정: 빈 슈트 할당 (같은 트랜잭션)
                var freeChute = _chutes.FirstOrDefault(c => c.IsEnabled && c.CurrentQty < c.Capacity);
                if (freeChute is null)
                    return ("NG", null, "NO_DEST", order.DestType);

                chuteNo = freeChute.ChuteNo;
                // AUTO 배정 결과를 오더에 반영 (M4에서 dest_assign_type=AUTO 컬럼)
                order.ChuteNo = chuteNo;
            }
            else
            {
                // 오더 지정 슈트 활성화 확인
                var chute = _chutes.FirstOrDefault(c => c.ChuteNo == chuteNo && c.IsEnabled);
                if (chute is null)
                    return ("NG", null, "NO_DEST", order.DestType);
            }

            // 예약 차감 (중복 배정 방지 — 이동 중 물량 반영)
            order.ReservedQty += qty;

            // OK reason: NORMAL(기본)  — BUSY·FULL·PAUSED는 3DS 상태에서 판단하나
            // M3에서는 기준정보 PAUSED만 판단하고 3DS 점유 상태는 M4에서 계산
            // 여기서는 항상 NORMAL 반환
            return ("OK", chuteNo, "NORMAL", order.DestType);
        }
    }

    public DestinationType? GetDestType(int pId)
    {
        // M3에서는 pId→바코드→오더 역추적 없이, DepositRecorder에서 chuteNo 기반으로 판단
        // 실제 구현은 DepositRecorder가 기록한 정보를 이용
        return null; // 기본 — DepositRecorder.GetDestType으로 대체
    }
}

/// <summary>
/// 인메모리 투입 기록.
/// thread-safe(ConcurrentDictionary). M4에서 EF Core로 교체.
/// </summary>
public sealed class InMemoryDepositRecorder : IDepositRecorder
{
    // pId → DepositRecord (IF-05 기록, IF-10에서 업데이트)
    private readonly ConcurrentDictionary<int, DepositRecord> _records = new();

    // pId → DestinationType (IF-05에서 기록, IF-10에서 조회)
    private readonly ConcurrentDictionary<int, DestinationType> _destTypes = new();

    // RecordDeposit의 IsReported RMW 및 TryAdd 원자성 보호용 lock
    // (ConcurrentDictionary는 개별 연산을 원자화하지만, 읽기+쓰기 복합 연산은 아님)
    private readonly object _lock = new();

    public void RecordDestinationQuery(
        int pId, int agvNo, string barcode, int inductionNo,
        int? chuteNo, int qty, DepositStatus status, string reason)
    {
        var record = new DepositRecord
        {
            PId        = pId,
            AgvNo      = agvNo,
            Barcode    = barcode,
            InductionNo = inductionNo,
            ChuteNo    = chuteNo,
            Qty        = qty,
            Status     = status,
            Reason     = reason,
            RecordedAt = DateTimeOffset.Now,
        };
        // M3 인메모리: 같은 pId면 덮어씀 (M4에서 piece.status 전이 로직으로 교체)
        _records[pId] = record;
    }

    public bool RecordDeposit(int pId, string barcode, int chuteNo, int agvNo, int? qty)
    {
        // ── 기존 레코드 경로 (IF-05 선행, 가장 일반적인 경로) ──────────────────
        // TryGetValue+RMW는 lock으로 원자화: ConcurrentDictionary.TryGetValue는 스냅샷을
        // 가져오지만 IsReported set은 별도 write라 경쟁 가능 → lock으로 묶어 보호.
        lock (_lock)
        {
            if (_records.TryGetValue(pId, out var existing))
            {
                if (existing.IsReported)
                    return false; // 중복 — 멱등

                existing.IsReported = true;
                existing.Status     = DepositStatus.Reported;
                return true;
            }

            // ── 신규 pId 경로 (IF-05 없이 IF-10이 먼저 도착한 경우·비정상이나 멱등 허용) ──
            // TryAdd로 원자화: 같은 새 pId로 동시에 다수 IF-10이 들어와도 정확히 1건만 추가.
            // TryAdd 실패(=다른 스레드가 먼저 삽입) → false 반환(멱등 처리됨).
            var record = new DepositRecord
            {
                PId         = pId,
                AgvNo       = agvNo,
                Barcode     = barcode,
                InductionNo = 0,
                ChuteNo     = chuteNo,
                Qty         = qty ?? 0,
                Status      = DepositStatus.Reported,
                Reason      = "REPORTED_DIRECT",
                RecordedAt  = DateTimeOffset.Now,
                IsReported  = true,
            };
            // lock 내부에서 실행 → 신규 pId 경로도 단일 스레드에서 처리됨
            if (_records.TryAdd(pId, record))
                return true;

            // 극히 드문 경로: lock 진입 전 다른 스레드가 IF-05를 실행해 레코드를 삽입한 경우
            // (RecordDestinationQuery가 lock 밖에서 _records[pId]=record 함) → 재확인
            var late = _records[pId];
            if (late.IsReported)
                return false;
            late.IsReported = true;
            late.Status     = DepositStatus.Reported;
            return true;
        }
    }

    public bool HasDepositRecord(int pId) =>
        _records.TryGetValue(pId, out var r) && r.IsReported;

    /// <summary>IF-05에서 기록한 DestType을 저장 (IF-10 트리거 판단용).</summary>
    public void SetDestType(int pId, DestinationType destType) =>
        _destTypes[pId] = destType;

    /// <summary>pId에 해당하는 목적지 종류 반환.</summary>
    public DestinationType? GetDestType(int pId) =>
        _destTypes.TryGetValue(pId, out var t) ? t : null;
}

/// <summary>
/// 인메모리 셀 선택기.
/// 선택 우선순위: ①활성 셀 재사용(같은 오더) → ②소속 빈 셀 → ③없으면 null.
/// thread-safe(lock). M4에서 EF Core + cell_assignment로 교체.
/// </summary>
public sealed class InMemoryCellSelector : ICellSelector
{
    private readonly List<CellInfo> _cells;
    private readonly object _lock = new();

    public InMemoryCellSelector(List<CellInfo> cells)
    {
        _cells = cells;
    }

    public int? SelectCell(int chuteNo, string barcode)
    {
        lock (_lock)
        {
            // ① 활성 셀 재사용: 같은 오더 바코드가 이미 점유 중인 셀
            var active = _cells.FirstOrDefault(c =>
                c.SorterChuteNo == chuteNo &&
                c.IsEnabled &&
                c.IsOccupied &&
                c.ActiveOrderBarcode == barcode);

            if (active is not null)
                return active.CellNo;

            // ② 소속 빈 셀: 해당 소터 슈트에 속하는 활성·비점유 셀
            var free = _cells.FirstOrDefault(c =>
                c.SorterChuteNo == chuteNo &&
                c.IsEnabled &&
                !c.IsOccupied);

            if (free is null)
                return null; // ③ 빈 셀 없음 — 3DS FULL 요소

            // 점유 등록
            free.IsOccupied          = true;
            free.ActiveOrderBarcode  = barcode;
            return free.CellNo;
        }
    }

    public void ReleaseCell(int cellNo)
    {
        lock (_lock)
        {
            var cell = _cells.FirstOrDefault(c => c.CellNo == cellNo);
            if (cell is not null)
            {
                cell.IsOccupied         = false;
                cell.ActiveOrderBarcode = null;
            }
        }
    }
}

/// <summary>
/// appsettings Floors:AgvNoToFloor 설정에서 agvNo→층 산출.
/// 매핑 없는 agvNo는 null 반환(명시적 거부 — 절대규칙 8 추측 금지).
/// M4에서 agv.floor DB 단일 진실 전환.
/// </summary>
public sealed class ConfigAgvFloorResolver : IAgvFloorResolver
{
    private readonly IReadOnlyDictionary<string, int> _map;

    /// <param name="agvNoToFloor">appsettings Floors:AgvNoToFloor (key=agvNo 문자열, value=층번호).</param>
    public ConfigAgvFloorResolver(IReadOnlyDictionary<string, int> agvNoToFloor)
    {
        _map = agvNoToFloor;
    }

    /// <summary>매핑 없으면 null — 호출자가 400 Bad Request 처리.</summary>
    public int? Resolve(int agvNo) =>
        _map.TryGetValue(agvNo.ToString(), out var floor) ? floor : null;
}
