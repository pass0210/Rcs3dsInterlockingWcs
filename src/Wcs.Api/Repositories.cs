using Wcs.PlcGateway;

namespace Wcs.Api;

// ════════════════════════════════════════════════════════════════════════════
// 리포지토리 인터페이스 (M4 EF Core 구현으로 교체 완료)
// ════════════════════════════════════════════════════════════════════════════

/// <summary>목적지 종류 — 3D Sorter 셀 vs 슈트.</summary>
public enum DestinationType { Chute, Sorter3D }

/// <summary>
/// 오더·목적지 조회 + 예약 차감.
/// M4에서 EfOrderRepository가 구현.
/// </summary>
public interface IOrderRepository
{
    /// <summary>
    /// 바코드로 오더 조회 + 목적지/상태 판정 + OK 시 예약 차감.
    /// IF05_REQ piece_event도 같은 트랜잭션 내 삽입(MINOR-6).
    /// 반환: (result:"OK"|"NG", chuteNo, reason, destType, destinationId).
    /// OK 시 chuteNo는 오더 지정 또는 AUTO 배정 슈트.
    /// NG 시 chuteNo=null.
    /// </summary>
    (string Result, int? ChuteNo, string Reason, DestinationType? DestType, long? DestinationId) QueryDestination(
        int pId, int agvNo, string barcode, int inductionNo, int qty, string? clientTs);
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
/// 3D Sorter 셀 선택 (IF-11 트리거 전 셀 지정).
/// 선택 우선순위: ①활성 셀 재사용 → ②소속 빈 셀 → ③없으면 null(FULL 요소).
/// M4에서 EfCellSelector가 구현.
/// </summary>
public interface ICellSelector
{
    /// <summary>
    /// 대상 슈트에 할당할 셀 선택.
    /// null이면 빈 셀 없음(→ 3DS FULL 경고).
    /// </summary>
    int? SelectCell(int chuteNo, string barcode);

    /// <summary>셀 점유 해제(핸드셰이크 완료·실패 시 호출 — cell_assignment 상태 전이).</summary>
    void ReleaseCell(int cellNo);
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
