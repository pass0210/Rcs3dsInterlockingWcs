using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wcs.Core;
using Wcs.Data;

namespace Wcs.Api.Controllers;

// ════════════════════════════════════════════════════════════════════════════
// RcsController — RCS → WCS 인바운드 인터페이스 (재설계 Phase 1)
//
//   IF-05  POST /api/v1/destination-query  목적지 조회 ({result, chuteNo}) + 소터 pending-floor 큐 enqueue
//   IF-09  POST /api/v1/arrival-report      도착 보고 ({result:"OK"}) — 기록만(정렬 트리거 제거, 2026-07-21)
//   IF-10  POST /api/v1/deposit-report       투입 보고 ({result:"OK"}) + IF-11 트리거
//
// IF-08(투입 가부 폴링 deposit-permission)은 폐지 — Phase 2에서 WCS→RCS 푸시로 대체.
//
// 가부는 200 + result로 응답, 검증 실패만 400. fire-and-forget(TgtFloor·핸드셰이크)은
// 응답 완료 대기 없이 큐 투입하되 예외를 삼키지 않는다(.ContinueWith IsFaulted 로깅).
// ════════════════════════════════════════════════════════════════════════════

[ApiController]
[Route("api/v1")]
public sealed class RcsController : ControllerBase
{
    private readonly ILogger<RcsController> _log;
    private readonly IOperationLogger       _opLog;

    // ── D-4 입력 상한 상수 (S-CLEANUP-FIELD) ──────────────────────────────────
    // WcsDbContext 스키마와 정합(단일 진실). 이 값 변경 시 반드시 WcsDbContext.HasMaxLength(...)도 함께
    // 변경한다(초과 입력이 DB 컬럼 길이를 넘으면 SaveChanges에서 500 유발 — 그 전에 400으로 거부).
    private const int BarcodeMaxLength   = 200;  // piece.Barcode / order_item.Barcode = nvarchar(200)
    private const int TimeStampMaxLength = 30;   // piece.ClientTs / piece_event.ClientTs = nvarchar(30)

    public RcsController(ILogger<RcsController> log, IOperationLogger opLog)
    {
        _log   = log;
        _opLog = opLog;
    }

    // ── IF-05 목적지 조회 ─────────────────────────────────────────────────────
    // 요청: pId(1~30000 필수)·agvNo·barcode·inductionNo·qty·timeStamp
    // 응답: 200 {result, chuteNo} / 400(검증 실패)
    // 사유(NORMAL·BUSY·FULL·PAUSED·…)는 piece_event(IF05_REQ/RES)에 내부 기록 — RCS 미전송.
    [HttpPost("destination-query")]
    public IActionResult DestinationQuery(
        [FromBody] DestinationQueryRequest    req,
        [FromServices] IOrderRepository       orders,
        [FromServices] IChuteCapacityService  capacity,
        [FromServices] IDestinationStatusService status,
        [FromServices] SorterPendingFloorQueues floorQueues,
        [FromServices] IOptions<WcsOptions>   wcsOptions)
    {
        // ── 검증 (D-4: 입력 상한 — DB 도달 전 거부, 위반 시 400. 정상 입력 경로 불변) ─────
        if (req.PId is < 1 or > 30000)
            return BadRequest(new { error = "pId는 1~30000 범위여야 합니다." });
        if (string.IsNullOrWhiteSpace(req.Barcode))
            return BadRequest(new { error = "barcode는 필수입니다." });
        if (req.Barcode.Length > BarcodeMaxLength)
            return BadRequest(new { error = $"barcode 길이는 {BarcodeMaxLength}자 이하여야 합니다." });
        if (req.Qty <= 0)
            return BadRequest(new { error = "qty는 1 이상이어야 합니다." });
        // qty 상한(설정값) — int.MaxValue 등 비정상 대량 qty를 DB 도달 전 거부(OVER int 오버플로 우회 차단).
        if (req.Qty > wcsOptions.Value.MaxQtyPerRequest)
            return BadRequest(new { error = $"qty는 {wcsOptions.Value.MaxQtyPerRequest} 이하여야 합니다." });
        if (!string.IsNullOrEmpty(req.TimeStamp) && req.TimeStamp.Length > TimeStampMaxLength)
            return BadRequest(new { error = $"timeStamp 길이는 {TimeStampMaxLength}자 이하여야 합니다." });

        // ── operation_log: IF-05 요청 원문 전수 기록(부수 — 응답 형상 0 변경) ───────
        _opLog.Log(OperationLogCategory.API, "IF05_REQ", barcode: req.Barcode, pId: req.PId,
            detail: $"{{\"agvNo\":{req.AgvNo},\"inductionNo\":{req.InductionNo},\"qty\":{req.Qty}}}");

        // ── 인덕션 파생 목표 층 F (§2-A·§3 — 인덕션 기반 2층 제어) ─────────────────
        // 요청 inductionNo → InductionFloorMap(순수) → F(1/2). 미매핑이면 null → 소터 목적지는
        // fail-loud(NG + 경고) — 조용한 통과·기본층 폴백 금지(확정 결정 2026-07-22). 슈트는 층 무관.
        int? floor = InductionFloorMap.DeriveFloor(wcsOptions.Value.FloorByInduction, req.InductionNo);

        // ── 오더 매칭 → 목적지·상태 판정 (+ FULL/PAUSED 상류 필터) → OK 시 예약 차감 ─
        // FULL/PAUSED 차단은 배정 시점(IF-05)으로 상류 이동. 산출원은 DestinationStatusService(슈트·소터 공용).
        // BUSY(분류·이동 중)는 차단하지 않는다 — OK·이동시킴(도착 후 Phase 2 푸시 ready 시 투입).
        //
        // 슈트(CHUTE) vs 소터(SORTER_3D) 분기(확정4):
        //   - 슈트: full/paused여도 IF-05 OK(보냄) — 슈트는 곧 비워지니 보내고 대기. availability는
        //     슈트에 대해 항상 None(통과). 슈트 readiness는 푸시(IF-08)로 별도 전달(IF-05와 분리 채널).
        //   - 소터: Paused는 예외 없이 차단(곧 안 풀림). 그 외엔 **이 piece(이 오더)를 지금 받을 수 있나**로
        //     판정 — SorterCanAcceptBarcode = (빈 셀 ≥1) OR (그 오더 배정 셀 중 여유 있는 셀 보유).
        //     이 술어는 IF-10 SelectCell의 비-null 조건과 동형이라 "IF-05 OK ⟹ 적재 가능"(§88)이 성립.
        //     받을 수 없으면 NG(FULL). (목적지-단위 SorterFull(푸시 ready)은 "아무 piece라도 받나"라
        //     다른 오더의 여유 셀까지 포함하므로, piece 단위 dispatch엔 SorterCanAcceptBarcode를 쓴다.)
        var (result, chuteNo, reason, destType, destId) =
            orders.QueryDestination(req.PId, req.AgvNo, req.Barcode, req.InductionNo, req.Qty, req.TimeStamp,
                availability: (id, dt) =>
                {
                    // 슈트는 full/paused 통과(OK) — IF-05 dispatch에서 차단하지 않는다(확정4). 층 무관.
                    if (dt != DestinationType.Sorter3D)
                        return DestinationBlock.None;

                    // 미매핑 inductionNo → 소터 목적지 fail-loud(NG + 경고 로그). 소터로 보내지 않음
                    // (확정 결정 2026-07-22 — 조용한 통과·기본층 폴백 금지).
                    if (floor is null)
                    {
                        _log.LogWarning(
                            "[IF-05] pId={PId} inductionNo={Ind} 미매핑(InductionFloorMap 없음) — 소터 목적지 NG(fail-loud)",
                            req.PId, req.InductionNo);
                        _opLog.Log(OperationLogCategory.API, "IF05_NO_FLOOR", level: OperationLogLevel.WARN,
                            destinationId: id, barcode: req.Barcode, pId: req.PId,
                            detail: $"{{\"inductionNo\":{req.InductionNo}}}");
                        return DestinationBlock.Unmapped;
                    }

                    var r = status.Compute(id, DestType.SORTER_3D);
                    if (r.Paused) return DestinationBlock.Paused;  // 소터 정지는 예외 없이 차단(우선).

                    // 이 piece(이 오더)를 지금 받을 수 있으면 OK, 아니면 FULL(NG) — SelectCell과 동형.
                    return status.SorterCanAcceptBarcode(id, req.Barcode)
                        ? DestinationBlock.None
                        : DestinationBlock.Full;
                });

        // ── FULL/PAUSED 인메모리 집계: IF-05 OK 예약 반영 (슈트만) ───────────────
        if (result == "OK" && destId.HasValue && destType == DestinationType.Chute)
            capacity.OnReserved(destId.Value, req.Qty);

        // ── 인덕션 기반 2층 제어: 소터 목적지 OK → 파생 층 F를 그 소터 큐에 IF-05 순서대로 enqueue ─
        // TgtFloor 쓰기는 IF-05 순간이 아니라 관측 루프(SorterFloorReturnService)가 TgtFloor==0 관측 시
        // 큐 머리 층을 게이트로 기입한다(§2-C). FULL/PAUSED/OFFLINE이면 위 availability가 NG로 차단해
        // enqueue에 도달하지 않는다. floor는 소터 OK 경로에선 항상 non-null(위 Unmapped 게이트 통과).
        if (result == "OK" && destId.HasValue && destType == DestinationType.Sorter3D && floor is int fFloor)
        {
            floorQueues.Enqueue(destId.Value, fFloor);
            _log.LogInformation(
                "[IF-05] pId={PId} 소터 destId={DestId} pending-floor 큐 enqueue F={Floor}(inductionNo={Ind})",
                req.PId, destId.Value, fFloor, req.InductionNo);
        }

        _log.LogInformation("[IF-05] pId={PId} barcode={Barcode} → result={Result} chuteNo={ChuteNo} reason(내부)={Reason}",
            req.PId, req.Barcode, result, chuteNo, reason);

        // ── operation_log: IF-05 응답 전수 기록(result·chuteNo·내부 reason) ─────────
        _opLog.Log(OperationLogCategory.API, "IF05_RES",
            level: result == "OK" ? OperationLogLevel.INFO : OperationLogLevel.WARN,
            sorterChuteNo: destType == DestinationType.Sorter3D ? chuteNo : null,
            destinationId: destId, barcode: req.Barcode, pId: req.PId,
            detail: $"{{\"result\":\"{result}\",\"chuteNo\":{(chuteNo.HasValue ? chuteNo.Value.ToString() : "null")},\"reason\":{(reason is null ? "null" : $"\"{reason}\"")}}}");

        // 응답은 {result, chuteNo} — reason 제거.
        return Ok(new DestinationQueryResponse(result, chuteNo));
    }

    // ── IF-09 도착 보고 ───────────────────────────────────────────────────────
    // 요청: pId·chuteNo·agvNo·timeStamp
    // 응답: 200 {result:"OK"} / 400(검증 실패)
    // 도착을 piece_event(IF09_ARRIVAL)로 **기록만** 한다(상태 전이 없음).
    //
    // 인덕션 기반 2층 제어(2026-07-21): IF-09는 더 이상 정렬(TgtFloor 쓰기)을 트리거하지 않는다.
    //   정렬은 IF-05 시점의 소터별 pending-floor 큐 enqueue + 관측 루프(SorterFloorReturnService)의
    //   TgtFloor==0 관측 기입으로 이동했다(§2-C). IF-09에 정렬 트리거를 남기면 이중 기입(경합)이
    //   발생하므로 제거(A의 핵심 전환). 미존재/비활성 chuteNo도 200 + 기록만(500 금지).
    [HttpPost("arrival-report")]
    public IActionResult ArrivalReport(
        [FromBody] ArrivalReportRequest         req,
        [FromServices] IArrivalRecorder          arrival)
    {
        // ── 검증 (D-4: timeStamp 상한 포함 — ClientTs 절단 500 방지) ─────────────────
        if (req.PId is < 1 or > 30000)
            return BadRequest(new { error = "pId는 1~30000 범위여야 합니다." });
        if (req.ChuteNo <= 0)
            return BadRequest(new { error = "chuteNo는 양수여야 합니다." });
        if (!string.IsNullOrEmpty(req.TimeStamp) && req.TimeStamp.Length > TimeStampMaxLength)
            return BadRequest(new { error = $"timeStamp 길이는 {TimeStampMaxLength}자 이하여야 합니다." });

        // ── operation_log: IF-09 요청 원문 전수 기록 ──────────────────────────────
        _opLog.Log(OperationLogCategory.API, "IF09", sorterChuteNo: req.ChuteNo, pId: req.PId,
            detail: $"{{\"chuteNo\":{req.ChuteNo},\"agvNo\":{req.AgvNo}}}");

        // ── 도착 기록 (piece_event IF09_ARRIVAL — 상태 전이·정렬 트리거 없음) ────────
        var recorded = arrival.RecordArrival(req.PId, req.ChuteNo, req.AgvNo, req.TimeStamp);
        if (!recorded)
            _log.LogWarning("[IF-09] pId={PId} 활성 piece 없음 — 도착 기록 생략(IF-05 선행 없음?)", req.PId);

        // 정렬은 IF-05 enqueue + 관측 루프가 담당(IF-09는 도착 기록만). 목적지 조회·정렬 없음.
        return Ok(new ArrivalReportResponse("OK"));
    }

    // ── IF-10 투입 보고 ───────────────────────────────────────────────────────
    // 요청: pId·barcode·chuteNo·agvNo (qty·timeStamp nullable 선택필드)
    // 응답: 200 {result:"OK"} / 400(검증 실패)
    // 3D 목적지면 번들 핸들의 핸드셰이크를 트리거 (소터별 독립 — 인스턴스별 _cSeq 보존)
    [HttpPost("deposit-report")]
    public IActionResult DepositReport(
        [FromBody] DepositReportRequest          req,
        [FromServices] IDepositRecorder           recorder,
        [FromServices] ICellSelector              cellSelector,
        [FromServices] IChuteCapacityService      capacity,
        [FromServices] WcsDbContext               db,
        [FromServices] ISorterGatewayRegistry     sorterRegistry,
        [FromServices] IHostApplicationLifetime   lifetime,
        [FromServices] IServiceScopeFactory       scopeFactory,
        [FromServices] IOptions<WcsOptions>       wcsOptions)
    {
        // S-IF10-CWRITE-SETTLE-DELAY — 안착 지연(D2)의 기준 시각(anchor). IF-10 HTTP 수신 시점(≈AGV 틸트
        // 시점)을 컨트롤러 진입 즉시 UTC로 캡처해 백그라운드 핸드셰이크로 넘긴다. 이 캡처는 응답 경로를
        // 전혀 지연시키지 않는다(값 하나 읽기) — IF-10은 아래에서 즉시 200 ack(fire-and-forget) 불변.
        var if10ReceivedAtUtc = DateTime.UtcNow;

        // ── 검증 (D-4: 입력 상한 — 음수/과대 qty·과길이 barcode/timeStamp를 DB 도달 전 거부) ─────
        if (req.PId is < 1 or > 30000)
            return BadRequest(new { error = "pId는 1~30000 범위여야 합니다." });
        if (string.IsNullOrWhiteSpace(req.Barcode))
            return BadRequest(new { error = "barcode는 필수입니다." });
        if (req.Barcode.Length > BarcodeMaxLength)
            return BadRequest(new { error = $"barcode 길이는 {BarcodeMaxLength}자 이하여야 합니다." });
        if (req.ChuteNo <= 0)
            return BadRequest(new { error = "chuteNo는 양수여야 합니다." });
        // qty는 선택필드(nullable) — 제공 시 음수 거부(DepositedQty 왜곡 방지) + 상한(설정값) 초과 거부.
        if (req.Qty is int q && (q < 0 || q > wcsOptions.Value.MaxQtyPerRequest))
            return BadRequest(new { error = $"qty는 0 이상 {wcsOptions.Value.MaxQtyPerRequest} 이하여야 합니다." });
        if (!string.IsNullOrEmpty(req.TimeStamp) && req.TimeStamp.Length > TimeStampMaxLength)
            return BadRequest(new { error = $"timeStamp 길이는 {TimeStampMaxLength}자 이하여야 합니다." });

        // ── operation_log: IF-10 요청 원문 전수 기록 ──────────────────────────────
        _opLog.Log(OperationLogCategory.API, "IF10", sorterChuteNo: req.ChuteNo,
            barcode: req.Barcode, pId: req.PId,
            detail: $"{{\"chuteNo\":{req.ChuteNo},\"agvNo\":{req.AgvNo}}}");

        // ── 투입 기록 + 멱등 ──────────────────────────────────────────────────────
        var isNewRecord = recorder.RecordDeposit(
            req.PId, req.Barcode, req.ChuteNo, req.AgvNo, req.Qty, req.TimeStamp);

        if (!isNewRecord)
        {
            _log.LogInformation("[IF-10] pId={PId} 중복 보고 — 멱등 OK", req.PId);
            return Ok(new DepositReportResponse("OK"));
        }

        // ── 목적지 타입 조회 → FULL 집계 반영 + IF-11 트리거 ─────────────────────
        var dest = db.Destinations
            .FirstOrDefault(d => d.ChuteNo == req.ChuteNo && d.IsActive);

        var destType = dest?.DestType switch
        {
            DestType.CHUTE     => DestinationType.Chute,
            DestType.SORTER_3D => DestinationType.Sorter3D,
            _                  => (DestinationType?)null,
        };

        // ── FULL/PAUSED 인메모리 집계: IF-10 투입 반영 ───────────────────────────
        if (dest is not null && destType == DestinationType.Chute)
        {
            // S-B2C-DATAGEN: 아카이브(재테스트 초기화) 행 제외.
            var piece = db.Pieces.FirstOrDefault(p => p.PId == req.PId && p.IsActive && p.ArchivedAt == null);
            var qty   = piece?.Qty ?? req.Qty ?? 1;
            capacity.OnDeposited(dest.Id, qty);
        }

        if (destType == DestinationType.Sorter3D && dest is not null)
            TriggerSorterHandshake(req, dest, cellSelector, db, sorterRegistry, lifetime, scopeFactory, if10ReceivedAtUtc);
        else
            _log.LogInformation("[IF-10] pId={PId} 슈트 보고 → IF-11 트리거 없음", req.PId);

        return Ok(new DepositReportResponse("OK"));
    }

    /// <summary>
    /// 3D 소터 IF-11 핸드셰이크 트리거 — 셀 선택 → 번들 핸드셰이크 → sorter_command·alarm·piece 영속화.
    /// 게이트웨이 본문 무변경(번들 핸들 경유). 영속화는 별도 스코프(요청 스코프 종료 후 안전).
    /// </summary>
    private void TriggerSorterHandshake(
        DepositReportRequest      req,
        Destination               dest,
        ICellSelector             cellSelector,
        WcsDbContext              db,
        ISorterGatewayRegistry    sorterRegistry,
        IHostApplicationLifetime  lifetime,
        IServiceScopeFactory      scopeFactory,
        DateTime                  if10ReceivedAtUtc)
    {
        var cellNo = cellSelector.SelectCell(req.ChuteNo, req.Barcode);

        if (!cellNo.HasValue)
        {
            _log.LogWarning("[IF-10] pId={PId} 3D 보고 → 빈 셀 없음 (3DS FULL 조건). IF-11 트리거 생략", req.PId);
            return;
        }

        int selectedCell = cellNo.Value;
        var bundle = sorterRegistry.GetBundle(dest.Id);

        if (bundle is null)
        {
            // 번들 없음(OFFLINE) — 핸드셰이크 불가. 방금 만든 **신규(빈) 배정만** 롤백(orphan 잔존 0).
            //   누적 진행 중(적재≥1) 배정은 유지 → 다음 piece가 같은 셀 누적(S-CELL-ACCUM Scope 5).
            cellSelector.ReleaseEmptyAssignment(req.ChuteNo, req.Barcode, selectedCell);
            _log.LogWarning("[IF-10] pId={PId} 3D 번들 없음(OFFLINE) — 핸드셰이크 생략(신규 배정만 롤백)", req.PId);
            return;
        }

        // piece.id / cell.id 조회 (sorter_command 연결 키 — 백그라운드 콜백에서 사용)
        //   C1: depositedAt(IF-10 투입 보고 시각 = piece.DepositedAt)를 함께 조회해 저널에 유입(계약 (e)).
        //   단일 진실(piece.DepositedAt) 재사용 — 요청 스코프 db로 지금 읽어 클로저로 넘긴다(백그라운드는 스코프 종료).
        var pieceRow = db.Pieces
            .Where(p => p.PId == req.PId && p.IsActive && p.ArchivedAt == null)   // S-B2C-DATAGEN: 아카이브 제외.
            .Select(p => new { p.Id, p.DepositedAt })
            .FirstOrDefault();
        long pieceId = pieceRow?.Id ?? 0;
        DateTime? depositedAt = pieceRow?.DepositedAt;
        long cellId = db.Cells
            .Where(c => c.DestinationId == dest.Id && c.CellNo == selectedCell)
            .Select(c => c.Id)
            .FirstOrDefault();

        int    pId      = req.PId;
        long   destId   = dest.Id;

        // IF-11 핸드셰이크: 번들 핸들 경유 — 소터별 독립 _cSeq·RFlag 채널
        // ContinueWith에서 sorter_command + alarm 영속화 (P3 결선 — 별도 스코프).
        var stopping = lifetime.ApplicationStopping;
        // S-IF10-CWRITE-SETTLE-DELAY — anchor(IF-10 수신 시각)를 백그라운드 핸드셰이크로 전달(D2). 핸드셰이크
        //   내부에서 arming 이후·C 기입 이전에 SettleDelayMs 안착 지연을 둔다(응답은 이미 fire-and-forget·불변).
        _ = bundle.ExecuteHandshakeAsync(selectedCell, stopping, if10ReceivedAtUtc)
            .ContinueWith((Task<Wcs.PlcGateway.HandshakeResult> t) =>
            {
                // 백그라운드 콜백은 HTTP 응답 완료 후 실행 → 요청 스코프는 이미 dispose.
                // 영속화·셀 해제는 새 스코프(scopeFactory)로 수행한다.
                //
                // 종료 단계 방어(절대규칙: 예외 삼킴 금지를 로깅으로 충족):
                //   호스트 종료(ApplicationStopping)가 신호되면 DB가 닫히는 중이라 영속화·셀 해제는
                //   무의미하고, 닫히는 SQLite/연결에 트랜잭션을 거는 순간 블로킹·예외가 발생한다.
                //   그 예외가 콜백 밖으로 새면 관찰되지 않은 Task → 파이널라이저 재던지기로 프로세스가 종료된다.
                //   따라서 종료 신호 시 전체 영속화를 건너뛴다. 콜백 전체를 try로 감싸 어떤 경로로도
                //   예외가 새지 않게 하고, 로깅 자체도 teardown 중 throw할 수 있으므로 SafeLog로 보호한다.
                void SafeLog(Action logAction)
                {
                    try { logAction(); } catch { /* 종료 중 로거 자체가 throw — 무시 */ }
                }

                if (stopping.IsCancellationRequested)
                    return;

                IServiceScope scope;
                try
                {
                    scope = scopeFactory.CreateScope();
                }
                catch (ObjectDisposedException)
                {
                    // 호스트 종료 후 콜백 — 영속화 생략(teardown 경쟁 조건 방어)
                    return;
                }

                try
                {
                    using var _ = scope;
                    var journal   = scope.ServiceProvider.GetRequiredService<ISorterCommandJournal>();
                    var alarmSink = scope.ServiceProvider.GetRequiredService<IAlarmSink>();

                    if (t.IsCompletedSuccessfully)
                    {
                        var result = t.Result;
                        SafeLog(() => _log.LogInformation(
                            "[IF-11] 핸드셰이크 완료: pId={PId} cellNo={CellNo} outcome={Outcome} destId={DestId}",
                            pId, selectedCell, result.Outcome, destId));

                        if (pieceId > 0 && cellId > 0)
                        {
                            try
                            {
                                var cmdId = journal.CreateSent(pieceId, cellId, result.SentCSeq, selectedCell, depositedAt);
                                journal.Finalize(cmdId, result);
                            }
                            catch (Exception ex)
                            {
                                SafeLog(() => _log.LogError(ex, "[IF-11] sorter_command 영속화 예외: pId={PId}", pId));
                            }

                            if (!result.IsSuccess)
                            {
                                try
                                {
                                    var code = result.Outcome switch
                                    {
                                        Wcs.PlcGateway.HandshakeOutcome.RSeqMismatch => "R_SEQ_MISMATCH",
                                        Wcs.PlcGateway.HandshakeOutcome.RFlagTimeout => "RFLAG_TIMEOUT",
                                        Wcs.PlcGateway.HandshakeOutcome.Offline      => "OFFLINE",
                                        Wcs.PlcGateway.HandshakeOutcome.CFlagTimeout => "CFLAG_TIMEOUT",
                                        _                                             => "RFLAG_TIMEOUT",
                                    };
                                    alarmSink.Append(code, Wcs.Data.AlarmSeverity.ERROR, pieceId,
                                        $"pId={pId} cellNo={selectedCell} detail={result.Detail}");
                                }
                                catch (Exception ex)
                                {
                                    SafeLog(() => _log.LogError(ex, "[IF-11] alarm 영속화 예외: pId={PId}", pId));
                                }
                            }
                            // C1: 성공했으나 복귀(Ready==1)를 상한 내 관측하지 못한 경우(returnedAt=NULL) — 소터 정체
                            //   경보. 분류 자체는 완료(status=COMPLETED)라 !IsSuccess 게이트 밖의 별도 분기.
                            //   즉시-clear 성공(무-이동)은 returnedAt non-NULL이므로 여기 미해당.
                            else if (result.ReturnedAt is null)
                            {
                                try
                                {
                                    alarmSink.Append("RETURN_TIMEOUT", Wcs.Data.AlarmSeverity.WARN, pieceId,
                                        $"pId={pId} cellNo={selectedCell} detail={result.Detail}");
                                }
                                catch (Exception ex)
                                {
                                    SafeLog(() => _log.LogError(ex, "[IF-11] RETURN_TIMEOUT alarm 영속화 예외: pId={PId}", pId));
                                }
                            }
                        }
                    }
                    else if (t.IsFaulted)
                    {
                        SafeLog(() => _log.LogError(t.Exception,
                            "[IF-11] 핸드셰이크 예외: pId={PId} cellNo={CellNo} destId={DestId}",
                            pId, selectedCell, destId));
                    }

                    // S-CELL-ACCUM: 매 투입 무조건 ReleaseCell 제거 — 셀 해제는 오더 완료 시점에만
                    //   (journal.Finalize가 Success 시 SortedQty 가산 + 오더 완료면 오더 스코프 release).
                    //   실패(MISMATCH/TIMEOUT)·미완료 오더는 배정 유지 → 다음 piece가 같은 셀 누적.
                }
                catch (Exception ex)
                {
                    // 어떤 경로(영속화·셀 해제·스코프 해제)의 예외도 콜백 밖으로 새지 않게 흡수.
                    SafeLog(() => _log.LogError(ex,
                        "[IF-11] 백그라운드 영속화 예외(흡수): pId={PId} cellNo={CellNo}", pId, selectedCell));
                }
            }, TaskScheduler.Default);

        _log.LogInformation("[IF-10] pId={PId} 3D 보고 → IF-11 트리거: cellNo={CellNo} destId={DestId}",
            req.PId, selectedCell, dest.Id);
    }
}
