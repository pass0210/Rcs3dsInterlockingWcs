using Wcs.PlcGateway;

namespace Wcs.Api;

// ════════════════════════════════════════════════════════════════════════════
// 리포지토리 인터페이스 (M4 EF Core 구현으로 교체 완료)
// ════════════════════════════════════════════════════════════════════════════

/// <summary>목적지 종류 — 3D Sorter 셀 vs 슈트.</summary>
public enum DestinationType { Chute, Sorter3D }

/// <summary>
/// 목적지 가용성 차단 사유(IF-05 상류 필터).
/// None=배정 가능 / Full·Paused=배정 안 함(NG). FULL/PAUSED 차단을 도착 시점→배정 시점으로 상류 이동.
/// Unmapped=미매핑 inductionNo(층 파생 불가) → 소터 목적지 NG(fail-loud — 조용한 통과 금지, 확정 결정 2026-07-22).
/// </summary>
public enum DestinationBlock { None, Full, Paused, Unmapped }

/// <summary>
/// 오더·목적지 조회 + 예약 차감.
/// M4에서 EfOrderRepository가 구현.
/// </summary>
public interface IOrderRepository
{
    /// <summary>
    /// 바코드로 오더 조회 + 목적지/상태 판정 + OK 시 예약 차감.
    /// IF05_REQ/RES piece_event도 같은 트랜잭션 내 삽입(MINOR-6).
    ///
    /// 재설계 Phase 1: 목적지 결정 후 예약 직전에 <paramref name="availability"/>를 호출해
    ///   FULL/PAUSED면 배정하지 않고 NG로 기록한다(상류 필터 — 산출원은 DestinationStatusService).
    ///   BUSY(분류·이동 중)는 OK(이동시킴) — availability는 FULL/PAUSED만 차단한다.
    ///
    /// 반환: (result:"OK"|"NG", chuteNo, reason, destType, destinationId).
    /// reason은 내부 기록·로깅용 — RCS 응답에는 포함하지 않는다(IF-05 응답은 {result, chuteNo}).
    /// </summary>
    /// <param name="availability">
    /// 결정된 목적지(id, destType)의 차단 사유 산출 — None이면 배정, Full/Paused면 NG.
    /// </param>
    (string Result, int? ChuteNo, string Reason, DestinationType? DestType, long? DestinationId) QueryDestination(
        int pId, int agvNo, string barcode, int inductionNo, int qty, string? clientTs,
        Func<long, DestinationType, DestinationBlock> availability);
}

/// <summary>
/// 투입 기록 (IF-16 통합 — IF-05 OK/NG·IF-10 보고 모두 기록).
/// M4에서 EfDepositRecorder가 구현.
/// </summary>
public interface IDepositRecorder
{
    /// <summary>
    /// IF-10 투입 보고 기록 — 멱등(pId 중복 무해).
    /// piece 부분 유니크 위반 시 멱등 false 반환(static lock 불필요).
    /// </summary>
    /// <returns>이미 기록된(중복) 경우 false, 신규 기록 true.</returns>
    bool RecordDeposit(int pId, string barcode, int chuteNo, int agvNo, int? qty, string? clientTs);

    /// <summary>pId 기록 존재 여부 (IF-10 멱등 확인).</summary>
    bool HasDepositRecord(int pId);
}

/// <summary>
/// IF-09 도착 보고 기록 — piece_event(IF09_ARRIVAL) append-only.
/// 사용자 확정: piece 상태 전이 없음(기록만). RESERVED/PERMITTED 그대로 유지.
/// M4 재설계에서 EfArrivalRecorder가 구현.
/// </summary>
public interface IArrivalRecorder
{
    /// <summary>
    /// IF-09 도착을 piece_event(IF09_ARRIVAL)로 기록한다.
    /// 활성 piece가 있으면 그 piece에, 없으면(IF-05 없이 도착) 기록 생략(false 반환).
    /// piece.status는 변경하지 않는다(기록 전용).
    /// </summary>
    /// <returns>도착 이벤트를 기록한 piece가 있으면 true, 없으면 false.</returns>
    bool RecordArrival(int pId, int chuteNo, int agvNo, string? clientTs);
}

/// <summary>
/// 3D Sorter 셀 선택 (IF-11 트리거 전 셀 지정) — one-order-one-cell, no overflow (S-CELL-ACCUM).
/// 배정 유무 분기: ①오더 활성 배정 보유 → 그 배정 셀 여유 시 재사용·전부 full이면 null(빈 셀 폴백 금지)
///   → ②배정 없음(진짜 신규) → 빈 셀 신규 할당 → ③없으면 null(FULL 요소).
/// 배정은 오더 완료(SortedQty==PlannedQty) 시에만 release(EfSorterCommandJournal.Finalize) — 매 투입 해제 아님.
/// M4에서 EfCellSelector가 구현.
/// </summary>
public interface ICellSelector
{
    /// <summary>
    /// 대상 소터에 barcode 오더의 셀 선택.
    /// null이면 적재 불가(배정 셀 full — 오버플로 금지 / 빈 셀 없음 → 3DS FULL). IF-05 SorterCanAcceptBarcode와 동형.
    /// </summary>
    int? SelectCell(int chuteNo, string barcode);

    /// <summary>
    /// OFFLINE 등 물리 적재 불가 시 방금 만든 **신규(빈) 배정만** 롤백 — 그 오더(barcode)가 cellNo 셀에
    /// 보유한 활성 배정을 현재-기간 적재가 0일 때만 release(orphan 잔존 0). 누적 진행 중(적재≥1) 배정은
    /// 파기하지 않는다. destination(chuteNo) 스코프 — CellNo만으로 전 소터 해제하던 회귀(A-7) 차단.
    /// (오더 완료 시 정상 release는 EfSorterCommandJournal.Finalize가 오더 스코프로 수행 — 이 메서드 아님.)
    /// </summary>
    void ReleaseEmptyAssignment(int chuteNo, string barcode, int cellNo);
}

/// <summary>
/// agvNo → 층 번호 산출.
/// M4: agv.floor DB 단일 진실(EfAgvFloorResolver).
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

// ────────────────────────────────────────────────────────────────────────────
// S-M4-P3 갭 결선: alarm · sorter_command 영속화 인터페이스
// API 계층 한정 — Wcs.PlcGateway는 이 인터페이스를 절대 참조하지 않는다(단방향 경계).
// ────────────────────────────────────────────────────────────────────────────

/// <summary>
/// 알람 기록 인터페이스 — code별 alarm 행 삽입.
/// OFFLINE 전이당 1건, 핸드셰이크 실패(MISMATCH/TIMEOUT) 시 1건 기록.
/// 구현(EfAlarmSink)은 별도 WcsDbContext 스코프에서 트랜잭션으로 삽입.
/// </summary>
public interface IAlarmSink
{
    /// <summary>alarm 행 1건 기록 (code, severity, pieceId nullable, message).</summary>
    void Append(string code, Wcs.Data.AlarmSeverity severity, long? pieceId, string message);
}

/// <summary>
/// sorter_command 저널 인터페이스 — SENT 생성 + 상태 전이.
/// IF-10 핸드셰이크 시작 시 SENT 행 생성, 결과에 따라 COMPLETED/MISMATCH/TIMEOUT 전이.
/// 구현(EfSorterCommandJournal)은 WcsDbContext 스코프로 트랜잭션 처리.
/// </summary>
public interface ISorterCommandJournal
{
    /// <summary>
    /// 핸드셰이크 전송 시작 — sorter_command SENT 행 생성.
    /// 반환값: 생성된 sorter_command.id (이후 Finalize에 사용).
    /// </summary>
    long CreateSent(long pieceId, long cellId, int cSeq, int cellNo);

    /// <summary>
    /// 핸드셰이크 완료 — sorter_command 상태 전이 (COMPLETED/MISMATCH/TIMEOUT).
    /// result: HandshakeResult 전체, commandId: CreateSent가 반환한 id.
    /// </summary>
    void Finalize(long commandId, HandshakeResult result);
}
