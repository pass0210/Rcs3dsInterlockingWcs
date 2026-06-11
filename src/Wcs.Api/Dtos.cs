namespace Wcs.Api;

// 필드명 고정: pId, agvNo, barcode, inductionNo, chuteNo, qty, timeStamp (docs/wcs_rcs_interface_kr.html §3)
// timeStamp 형식: "yyyy-MM-dd HH:mm:ss" (로컬)

public sealed record DestinationQueryRequest(int PId, string Barcode, int InductionNo, int Qty, string TimeStamp);
public sealed record DestinationQueryResponse(string Result, int? ChuteNo, string? Reason); // OK+chuteNo / NG+OVER·COMPLETED·NO_DEST·OFFLINE

public sealed record DepositPermissionRequest(int PId, int ChuteNo, int AgvNo, string TimeStamp);
public sealed record DepositPermissionResponse(bool Allowed, string? Reason); // WRONG_FLOOR·BUSY·FULL·PAUSED·OFFLINE

public sealed record DepositReportRequest(int PId, string Barcode, int ChuteNo, int AgvNo, int Qty, string TimeStamp);
public sealed record DepositReportResponse(string Result); // "OK" — 멱등
