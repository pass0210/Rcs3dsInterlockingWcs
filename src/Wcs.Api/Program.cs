using Wcs.Api;

var builder = WebApplication.CreateBuilder(args);
// TODO(M5): builder.Host.UseWindowsService(); + Serilog
// TODO(M3): PlcPollingService/PlcWriteQueue/IPlcGateway DI 등록, Wcs.Data DbContext
var app = builder.Build();

// IF-05 목적지 조회 — TODO(M3): 오더 매칭 → 목적지 상태 판단(OVER/COMPLETED/NO_DEST/OFFLINE=NG) → OK 시 예약 차감
app.MapPost("/api/v1/destination-query", (DestinationQueryRequest req) =>
    Results.StatusCode(StatusCodes.Status501NotImplemented));

// IF-08 투입 가부 — TODO(M3): 스냅샷 캐시 + agvFloor 산출(설정 매핑) → DepositDecider.Decide
//   → decision.WriteTgtFloor면 쓰기 큐 투입(완료 대기 X) → {allowed, reason(ToWire)}
app.MapPost("/api/v1/deposit-permission", (DepositPermissionRequest req) =>
    Results.StatusCode(StatusCodes.Status501NotImplemented));

// IF-10 투입 보고 — TODO(M3): 멱등(pId 중복 무해), 3D 목적지면 IF-11 셀 지정 트리거
app.MapPost("/api/v1/deposit-report", (DepositReportRequest req) =>
    Results.StatusCode(StatusCodes.Status501NotImplemented));

app.Run();
