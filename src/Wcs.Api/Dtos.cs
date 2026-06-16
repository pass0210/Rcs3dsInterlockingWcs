namespace Wcs.Api;

// 필드명 고정: pId, agvNo, barcode, inductionNo, chuteNo, qty, timeStamp (docs/wcs_rcs_interface_kr.html §3)
// timeStamp 형식: "yyyy-MM-dd HH:mm:ss" (로컬)

// IF-05 목적지 조회 요청 — pId(1~30000 필수), agvNo, barcode, inductionNo, qty, timeStamp
public sealed record DestinationQueryRequest(
    int     PId,
    int     AgvNo,        // agvNo→층 매핑으로 agvFloor 산출에도 사용 (원본 HTML §3 + 절대규칙 6)
    string  Barcode,
    int     InductionNo,
    int     Qty,
    string  TimeStamp);

// IF-05 응답
// OK:   result="OK" + chuteNo + reason ∈ {NORMAL, BUSY, FULL, PAUSED}
// NG:   result="NG" + chuteNo=null + reason ∈ {OVER, COMPLETED, NO_DEST, OFFLINE}
// NG여도 투입 기록은 남긴다 (DENIED).
public sealed record DestinationQueryResponse(
    string  Result,       // "OK" | "NG"
    int?    ChuteNo,      // OK=슈트번호, NG=null
    string? Reason);      // OK: NORMAL·BUSY·FULL·PAUSED / NG: OVER·COMPLETED·NO_DEST·OFFLINE

// IF-08 투입 가부 요청 — pId, chuteNo, agvNo. timeStamp는 WCS 감사용 nullable 선택필드(RCS 미전송 허용, §7-B)
public sealed record DepositPermissionRequest(
    int     PId,
    int     ChuteNo,
    int     AgvNo,
    string? TimeStamp = null);  // nullable 선택필드 (RCS 미전송 허용)

// IF-08 응답
// allowed=true  → reason="READY"  (원본 §6 사유코드 — API 계층 주입, Core ToWire(None)=null 무변경)
// allowed=false → reason ∈ {WRONG_FLOOR, BUSY, FULL, PAUSED, OFFLINE}
public sealed record DepositPermissionResponse(
    bool    Allowed,
    string? Reason);      // true=READY / false=WRONG_FLOOR·BUSY·FULL·PAUSED·OFFLINE

// IF-10 투입 보고 요청 — pId, barcode, chuteNo, agvNo. qty·timeStamp는 nullable 선택필드(RCS 미전송 허용, §7-B)
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
