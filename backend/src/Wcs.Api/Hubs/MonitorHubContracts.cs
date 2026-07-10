using Wcs.Data;

namespace Wcs.Api.Hubs;

// ════════════════════════════════════════════════════════════════════════════
// F2 SignalR 허브 메시지 계약 (WcsMonitorHub) — 서버→클라이언트 push payload 형상.
//
// 프론트(frontend/src/lib/signalr.ts)의 TS 타입과 1:1(카멜케이스 JSON — AddJsonProtocol에서
// PropertyNamingPolicy=CamelCase 강제). 전부 읽기 전용 관측 payload(쓰기/제어 없음 — F3).
//
// 메서드명(문자열)은 클라이언트 `.on(name)`과 대소문자까지 정확히 일치해야 한다:
//   Bootstrap / Heartbeat  → SorterWordDto[]
//   RegisterDelta          → RegisterDeltaDto
//   SorterTransition       → SorterTransitionDto
//   OpLog                  → OpLogEntryDto
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 소터 1대의 전체 워드 스냅샷(D0~D6 + 비트 + Online). 부트스트랩·하트비트에서 전량 전송.
/// PlcSnapshot(순수 모델)을 관측용 평면 DTO로 투영 — 게이트웨이 모델 무변경(소비만).
/// </summary>
public sealed record SorterWordDto(
    long           DestId,
    int            ChuteNo,
    bool           Online,
    int            CCellNo,   // D0
    int            CSeq,      // D1
    int            RCellNo,   // D2
    int            RSeq,      // D3
    bool           CFlag,     // D4.0
    bool           RFlag,     // D4.1
    bool           Ready,     // D4.2
    int            CurFloor,  // D5
    int            TgtFloor,  // D6
    DateTimeOffset At);

/// <summary>
/// 레지스터 변화분 1건(변화분만 push — 무변화 0). reg 문자열은 PlcGateway.EmitRegisterChanges와
/// 동일: C_CellNo/C_Seq/R_CellNo/R_Seq/C_Flag/R_Flag/Ready/CurFloor/TgtFloor.
/// 비트(C_Flag/R_Flag/Ready)는 0/1 정수로 표현.
/// </summary>
public sealed record RegisterDeltaDto(
    long           DestId,
    int            ChuteNo,
    string         Reg,
    int            OldValue,
    int            NewValue,
    DateTimeOffset At);

/// <summary>소터 Online/Offline 전이(전이당 1회).</summary>
public sealed record SorterTransitionDto(
    long           DestId,
    int            ChuteNo,
    bool           Online,
    DateTimeOffset At);

/// <summary>
/// operation_log 엔트리 1건(테일 스트림). DB 영속화와 별개 경로로 브로드캐스트 —
/// enum은 문자열로 노출(프론트 필터·표시용). Id는 스트림 시점 미할당일 수 있어 nullable.
/// </summary>
public sealed record OpLogEntryDto(
    long?     Id,
    DateTime  At,
    string    Category,
    string    Action,
    string    Level,
    int?      SorterChuteNo,
    long?     DestinationId,
    string?   Barcode,
    int?      PId,
    string?   Detail)
{
    public static OpLogEntryDto From(OperationLog e) => new(
        e.Id == 0 ? null : e.Id,
        e.At,
        e.Category.ToString(),
        e.Action,
        e.Level.ToString(),
        e.SorterChuteNo,
        e.DestinationId,
        e.Barcode,
        e.PId,
        e.Detail);
}
