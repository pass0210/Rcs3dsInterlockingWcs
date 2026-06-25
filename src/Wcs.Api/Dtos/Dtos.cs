namespace Wcs.Api;

// 필드명 고정: pId, agvNo, barcode, inductionNo, chuteNo, qty, timeStamp (docs/wcs_rcs_interface.html §3)
// timeStamp 형식: "yyyy-MM-dd HH:mm:ss" (로컬)
//
// RCS↔WCS 재설계 Phase 1:
//   IF-05/IF-09/IF-10 = RCS → WCS 요청(WCS 응답).
//   IF-08 = WCS → RCS 푸시(Phase 2) — 투입 가부 폴링(deposit-permission) 폐지.

// IF-05 목적지 조회 요청 — pId(1~30000 필수), agvNo, barcode, inductionNo, qty, timeStamp
public sealed record DestinationQueryRequest(
    int     PId,
    int     AgvNo,        // 도착 기록·감사용(층 비교는 더 이상 정렬에 쓰지 않음 — 2층 고정 운영)
    string  Barcode,
    int     InductionNo,
    int     Qty,
    string  TimeStamp);

// IF-05 응답 — {result, chuteNo} (reason 제거 — RCS로 전송하지 않음).
// OK: result="OK" + chuteNo / NG: result="NG" + chuteNo=null.
// 내부 사유(NORMAL·BUSY·FULL·PAUSED·OVER·…)는 piece_event(IF05_REQ/RES)에만 기록.
public sealed record DestinationQueryResponse(
    string  Result,       // "OK" | "NG"
    int?    ChuteNo);     // OK=슈트번호, NG=null

// IF-09 도착 보고 요청 — pId, chuteNo, agvNo, timeStamp.
// AGV가 목적지 슈트에 도착 시 투입 직전 1회 호출.
public sealed record ArrivalReportRequest(
    int     PId,
    int     ChuteNo,
    int     AgvNo,
    string? TimeStamp = null);  // 로컬 timeStamp (nullable — RCS 미전송 허용)

// IF-09 응답 — {result:"OK"}.
public sealed record ArrivalReportResponse(string Result); // "OK"

// IF-10 투입 보고 요청 — pId, barcode, chuteNo, agvNo. qty·timeStamp는 nullable 선택필드(RCS 미전송 허용)
// qty: IF-05 등록값(전량 틸트)이 진실의 원천 — RCS가 재전송하지 않아도 무방
public sealed record DepositReportRequest(
    int     PId,
    string  Barcode,
    int     ChuteNo,
    int     AgvNo,
    int?    Qty       = null,   // nullable 선택필드 (RCS 미전송 허용)
    string? TimeStamp = null);  // nullable 선택필드 (RCS 미전송 허용)

// IF-10 응답 — 멱등(pId 중복 보고 무해)
public sealed record DepositReportResponse(string Result); // "OK"
