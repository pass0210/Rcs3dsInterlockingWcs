using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Wcs.Api.B2B;   // AppUtils.NormalizeBizDay 재사용(DRY — 작업일자 정규화 단일 소스)
using Wcs.Data;

namespace Wcs.Api.B2C;

// ════════════════════════════════════════════════════════════════════════════
// B2cTestDataService — B2C(3D 소터) 테스트 데이터 생성·요약·초기화(재테스트 준비).
// 계약 정본: docs/B2C-DATAGEN.md. 게이트 확정(OQ1~OQ8) 준수.
//
// 무접촉 경계: Wcs.PlcGateway·Wcs.Core·HandshakeOrchestrator 무의존. 판정/Modbus 직접 호출 0
//   (절대규칙 #1·#8). WcsDbContext(Scoped) + IOperationLogger 만 주입.
//
// 생성(2a 슬림 · 멱등 upsert): 같은 파라미터 재실행 시 카운트 불변.
//   ★ S-B2C-FACILITY: 생성은 **오더/바코드만** 만든다(목적지 미할당 — DestinationId=null·
//     DestAssignType=null). 소터/셀/cell_assignment 자동 생성·N↔N 배정 제거(설비 관리 2b 로 이관).
//   orderNo == barcode == "{prefix}-{NN}"(zero-pad). 계획수량 = 생성 개수 N(각 order_item.planned_qty=1·OQ-4).
//   기존 order_item reserved/sorted 는 보존(멱등 — 재생성이 실적 클로버 금지).
//
// 초기화(OQ1=B 아카이브 · S-B2C-UX = **배치 스코프**): 대상 배치(work_batch)에 속한 오더의
//   piece/piece_event/sorter_command 를 하드삭제하지 않고 archived_at 로 소프트삭제(보존).
//   order_item reserved/sorted=0 리셋. wcs_order/cell_assignment 보존(OQ2). COMPLETED 오더는
//   RUNNING 으로 재개(재테스트 가능 — QueryDestination 이 COMPLETED 제외하므로 재개하지 않으면 같은
//   바코드 재투입이 NG). in-flight piece 존재 시 기본 거부·force 로만 허용(OQ3). 소터 스코프는 폐지.
//
// 감사(OQ8): generate/reset 을 operation_log 카테고리 STATE 로 1행 기록(성공·실패·거부 전수).
// ════════════════════════════════════════════════════════════════════════════

public interface IB2cTestDataService
{
    /// <summary>생성(멱등 upsert · 슬림). 미할당 오더 N건 생성. workDate 비존재 날짜 → ArgumentException → 400.</summary>
    Task<B2cManagementResponse> GenerateAsync(B2cGenerateRequest req, CancellationToken ct = default);

    /// <summary>
    /// 엑셀 업로드(S-B2C-DATAGEN-UPLOAD) — 6열(작업일자·배치명·차수·오더번호·바코드·수량). ClosedXML 파싱
    /// → 행별 검증 → 멱등 append + 오류 시 전체 거부(atomic). 미할당 유지(DestinationId=null).
    /// ★ 1 오더:N 바코드 — 같은 (배치·오더번호) 행들을 하나의 WcsOrder 로 묶고, 각 행의 바코드를
    ///   그 오더의 order_item(planned_qty=행 수량)으로 만든다(오더번호 ≠ 바코드). 배치 내 바코드 유일.
    /// 파일 레벨 검증(없음/크기/확장자·MIME)은 컨트롤러가 400 으로 선행. 여기선 구조/행오류를 200 F 로.
    /// </summary>
    Task<B2cUploadResponse> UploadExcelAsync(Stream excelStream, CancellationToken ct = default);

    /// <summary>최근 work_batch 요약(생성 결과 view) — 미할당 오더 수 포함.</summary>
    Task<List<B2cBatchSummary>> GetBatchesAsync(int take, CancellationToken ct = default);

    /// <summary>요약 집계(소터별) — sorterChuteNo 지정 시 그 소터만, 미지정 시 전체 SORTER_3D.</summary>
    Task<List<B2cSorterSummary>> GetSummaryAsync(int? sorterChuteNo, CancellationToken ct = default);

    /// <summary>셀 상세(그리드용) — 지정 소터의 셀별 현재수량·배정 오더.</summary>
    Task<List<B2cCellDetail>> GetDetailAsync(int sorterChuteNo, CancellationToken ct = default);

    /// <summary>초기화(재테스트 준비 · 배치 스코프) — 배치 오더의 piece 아카이브 + 수량 리셋 + 오더 재개. in-flight 가드(force).</summary>
    Task<B2cManagementResponse> ResetAsync(B2cResetRequest req, CancellationToken ct = default);
}

public sealed class B2cTestDataService : IB2cTestDataService
{
    private readonly WcsDbContext    _db;
    private readonly IOperationLogger _opLog;

    public B2cTestDataService(WcsDbContext db, IOperationLogger opLog)
    {
        _db    = db;
        _opLog = opLog;
    }

    /// <summary>
    /// 진행 중(in-flight) 판정 술어(OQ3 확정 근사) — reset 가드·요약 집계 공용.
    /// QUERIED/RESERVED/PERMITTED/CELL_ASSIGNED/LOADED + IsActive + archived 제외.
    /// ⚠ 값변환 enum(HasConversion&lt;string&gt;)은 정적 배열 <c>Contains</c> 번역이 깨지므로(EF 파라미터
    ///   평가·ReadOnlySpan 제약) 명시 OR 로 표현한다(MonitoringQueries 와 동일 패턴).
    /// </summary>
    private static readonly System.Linq.Expressions.Expression<Func<Piece, bool>> IsInFlight =
        p => p.IsActive
          && p.ArchivedAt == null
          && (p.Status == PieceStatus.QUERIED
           || p.Status == PieceStatus.RESERVED
           || p.Status == PieceStatus.PERMITTED
           || p.Status == PieceStatus.CELL_ASSIGNED
           || p.Status == PieceStatus.LOADED);

    // ── 순수 함수: 생성 계획(결정적) — I/O 무의존(절대규칙 #8 정신·테스트 가능) ──────────
    /// <summary>
    /// 생성할 오더번호(=바코드) 목록을 결정적으로 산출한다. n=1..count, "{prefix}-{NN}"(zero-pad).
    /// zero-pad 폭 = max(2, count 자릿수) — 정렬 안정(0714-A-01 …). 순수(부수효과·I/O 0) —
    /// 같은 입력 → 같은 출력(멱등의 기반). barcode == orderNo.
    /// </summary>
    public static IReadOnlyList<string> BuildOrderNumbers(int count, string prefix)
    {
        int width = Math.Max(2, count.ToString(System.Globalization.CultureInfo.InvariantCulture).Length);
        var list = new List<string>(count);
        for (int n = 1; n <= count; n++)
            list.Add($"{prefix}-{n.ToString("D" + width, System.Globalization.CultureInfo.InvariantCulture)}");
        return list;
    }

    // ── 생성(2a 슬림 · 멱등 upsert) — 미할당 오더 N건만 ───────────────────────────
    public async Task<B2cManagementResponse> GenerateAsync(B2cGenerateRequest req, CancellationToken ct = default)
    {
        // 작업일자 정규화(비존재 날짜 → ArgumentException → 컨트롤러 400). DateOnly 로 변환.
        var normalized = AppUtils.NormalizeBizDay(req.WorkDate);   // "yyyy-MM-dd"
        var workDate   = DateOnly.ParseExact(normalized, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        // OQ-4: 계획수량 = 생성 개수 N. 오더번호=바코드="{prefix}-{NN}".
        var orderNos = BuildOrderNumbers(req.PlannedQty, req.BarcodePrefix);
        var now = DateTime.UtcNow;

        int ordersCreated = 0, itemsCreated = 0;

        await using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            // ── 1) work_batch (RUNNING) — UQ(work_date,batch_no,wave_no) 멱등 ──────────────
            var batch = await _db.WorkBatches.FirstOrDefaultAsync(
                b => b.WorkDate == workDate && b.BatchNo == req.BatchNo && b.WaveNo == req.WaveNo, ct)
                .ConfigureAwait(false);
            if (batch is null)
            {
                batch = new WorkBatch
                {
                    WorkDate = workDate, BatchNo = req.BatchNo, WaveNo = req.WaveNo,
                    Status   = WorkBatchStatus.RUNNING, OpenedAt = now, ClosedAt = null,
                    CreatedAt = now, UpdatedAt = now,
                };
                _db.WorkBatches.Add(batch);
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            // ── 2) 미할당 오더 upsert (DestinationId=null·DestAssignType=null) — UQ(batch,order_no) ──
            //   ★ 슬림: 목적지 미할당. 배정은 설비 관리(2b)의 오더 할당이 담당. RUNNING·GENERAL.
            var existingOrders = await _db.Orders
                .Where(o => o.WorkBatchId == batch.Id)
                .ToDictionaryAsync(o => o.OrderNo, ct).ConfigureAwait(false);

            foreach (var orderNo in orderNos)
            {
                if (!existingOrders.ContainsKey(orderNo))
                {
                    _db.Orders.Add(new WcsOrder
                    {
                        WorkBatchId = batch.Id, OrderNo = orderNo, OrderType = OrderType.GENERAL,
                        DestinationId = null, DestAssignType = null, DestAssignedAt = null,  // 미할당
                        Status = OrderStatus.RUNNING, StartedAt = now, ClosedAt = null,
                        CreatedAt = now, UpdatedAt = now,
                    });
                    ordersCreated++;
                }
            }

            // 오더 ID 확정(order_item FK 연결용).
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            var orderByNo = await _db.Orders.Where(o => o.WorkBatchId == batch.Id)
                .ToDictionaryAsync(o => o.OrderNo, o => o.Id, ct).ConfigureAwait(false);

            // ── 3) order_item (barcode==orderNo, planned_qty=1 고정·OQ-4) — 기존 실적 보존(INSERT 만) ──
            var existingItemKeys = await _db.OrderItems
                .Where(i => orderByNo.Values.Contains(i.OrderId))
                .Select(i => new { i.OrderId, i.Barcode })
                .ToListAsync(ct).ConfigureAwait(false);
            var itemKeySet = existingItemKeys.Select(x => (x.OrderId, x.Barcode)).ToHashSet();

            foreach (var orderNo in orderNos)
            {
                long orderId = orderByNo[orderNo];
                var barcode  = orderNo;   // barcode == orderNo
                if (!itemKeySet.Contains((orderId, barcode)))
                {
                    _db.OrderItems.Add(new OrderItem
                    {
                        OrderId = orderId, Barcode = barcode, PlannedQty = 1,   // OQ-4: 단건 테스트 모델
                        ReservedQty = 0, SortedQty = 0, CreatedAt = now, UpdatedAt = now,
                    });
                    itemsCreated++;
                }
            }

            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);

            var counts = new Dictionary<string, int>
            {
                ["ordersCreated"]     = ordersCreated,
                ["orderItemsCreated"] = itemsCreated,
                ["requestedCount"]    = req.PlannedQty,
            };
            var message = $"생성 완료 — 배치 {req.BatchNo}(#{req.WaveNo}), 미할당 오더 신규 {ordersCreated}건"
                        + $"·항목 신규 {itemsCreated}건 (요청 {req.PlannedQty}건). 목적지 배정은 설비 관리에서 수행하세요.";
            Audit("B2C_GENERATE", OperationLogLevel.INFO, sorterChuteNo: null, destinationId: null,
                $"{{\"batch\":\"{Esc(req.BatchNo)}\",\"workDate\":\"{normalized}\",\"waveNo\":{req.WaveNo},\"ordersCreated\":{ordersCreated},\"itemsCreated\":{itemsCreated}}}");
            return B2cManagementResponse.Ok(message, counts);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    // ── 순수 함수: 행별 파싱·검증(결정적·I/O 무의존 — 절대규칙 #8·테스트가 스펙) ──────────
    /// <summary>
    /// 업로드 원시 행(문자열 셀)을 검증·파싱한다. 순수(부수효과·I/O·DB 0) — 같은 입력 → 같은 출력.
    ///   · 작업일자: 필수·형식(YYYYMMDD|YYYY-MM-DD)·달력 유효(NormalizeBizDay). 정규화("yyyy-MM-dd").
    ///   · 배치명: 필수(1~<see cref="B2cConstants.BatchNoMaxLength"/>자).  · 차수: 선택(빈=기본1)·정수 1~9999.
    ///   · 오더번호: 필수·안전문자(영문·숫자·하이픈·언더스코어 1~100). wcs_order.order_no.
    ///   · 바코드: 필수·안전문자(동일 규칙). order_item.barcode. **오더번호와 별개**(1 오더:N 바코드).
    ///   · 수량(계획수량): 선택(빈=기본1)·정수 1~상한.
    ///   · 파일 내 중복 (작업일자·배치명·차수·**바코드**) → 오류(배치 내 바코드 유일 — 멱등 키 충돌).
    ///     ⚠ 오더번호는 유일성 키에 넣지 않는다: 같은 오더가 여러 행에 정당하게 반복되기 때문(1 오더:N).
    ///     이 키가 "다른 오더가 같은 바코드" + "같은 오더에 같은 바코드 반복"을 모두 잡는다.
    /// 한 행에 사유가 여럿이면 공백으로 결합해 1개 <see cref="B2cUploadRowError"/> 로 반환. Q4 원자성은
    /// 호출측(UploadExcelAsync)이 "오류가 하나라도 있으면 커밋 0"으로 강제(이 함수는 순수 판정만).
    /// </summary>
    public static (IReadOnlyList<B2cUploadParsedRow> Rows, IReadOnlyList<B2cUploadRowError> Errors)
        ValidateUploadRows(IReadOnlyList<B2cUploadRawRow> rawRows)
    {
        var parsed = new List<B2cUploadParsedRow>();
        var errors = new List<B2cUploadRowError>();
        var seen   = new HashSet<(string WorkDate, string BatchNo, int WaveNo, string Barcode)>();

        foreach (var r in rawRows)
        {
            var rowErrs = new List<string>();

            // 작업일자 — 필수·형식·달력 유효.
            string workDateNorm = string.Empty;
            if (string.IsNullOrWhiteSpace(r.WorkDate))
                rowErrs.Add("작업일자는 필수입니다.");
            else if (!Regex.IsMatch(r.WorkDate.Trim(), B2cConstants.WorkDateRegex))
                rowErrs.Add("작업일자 형식이 올바르지 않습니다(YYYYMMDD 또는 YYYY-MM-DD).");
            else
            {
                try { workDateNorm = AppUtils.NormalizeBizDay(r.WorkDate); }
                catch (ArgumentException) { rowErrs.Add($"작업일자가 존재하지 않는 날짜입니다: {r.WorkDate.Trim()}"); }
            }

            // 배치명 — 필수(1~BatchNoMaxLength).
            var batchNo = r.BatchNo.Trim();
            if (batchNo.Length == 0) rowErrs.Add("배치명은 필수입니다.");
            else if (batchNo.Length > B2cConstants.BatchNoMaxLength)
                rowErrs.Add($"배치명은 {B2cConstants.BatchNoMaxLength}자 이하여야 합니다.");

            // 차수 — 선택(빈=기본1)·정수 1~9999.
            int waveNo = B2cConstants.DefaultWaveNo;
            if (!string.IsNullOrWhiteSpace(r.WaveNo)
                && (!int.TryParse(r.WaveNo.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out waveNo)
                    || waveNo < 1 || waveNo > B2cConstants.WaveNoMax))
                rowErrs.Add($"차수는 1~{B2cConstants.WaveNoMax} 사이 정수여야 합니다.");

            // 오더번호 — 필수·안전문자(wcs_order.order_no).
            var orderNo = r.OrderNo.Trim();
            if (orderNo.Length == 0) rowErrs.Add("오더번호는 필수입니다.");
            else if (!Regex.IsMatch(orderNo, B2cConstants.UploadOrderNoRegex))
                rowErrs.Add("오더번호는 영문·숫자·하이픈·언더스코어(1~100자)만 허용합니다.");

            // 바코드 — 필수·안전문자(order_item.barcode · 오더번호와 별개).
            var barcode = r.Barcode.Trim();
            if (barcode.Length == 0) rowErrs.Add("바코드는 필수입니다.");
            else if (!Regex.IsMatch(barcode, B2cConstants.UploadBarcodeRegex))
                rowErrs.Add("바코드는 영문·숫자·하이픈·언더스코어(1~100자)만 허용합니다.");

            // 수량 — 선택(빈=기본1)·정수 1~상한.
            int qty = B2cConstants.DefaultPlannedQty;
            if (!string.IsNullOrWhiteSpace(r.Qty)
                && (!int.TryParse(r.Qty.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out qty)
                    || qty < 1 || qty > B2cConstants.UploadPlannedQtyMax))
                rowErrs.Add($"수량은 1~{B2cConstants.UploadPlannedQtyMax} 사이 정수여야 합니다.");

            // 배치 내 바코드 유일 — 키 필드가 전부 유효할 때만 판정(무효 행은 이미 오류).
            //   키에 OrderNo 없음: 같은 오더 반복(1:N)은 정당, 바코드 중복만 오류.
            if (rowErrs.Count == 0)
            {
                if (!seen.Add((workDateNorm, batchNo, waveNo, barcode)))
                    rowErrs.Add("같은 배치 안에서 바코드가 중복되었습니다 (작업일자·배치명·차수·바코드).");
            }

            if (rowErrs.Count > 0)
                errors.Add(new B2cUploadRowError(r.RowNumber, string.Join(" ", rowErrs)));
            else
                parsed.Add(new B2cUploadParsedRow(r.RowNumber, workDateNorm, batchNo, waveNo, orderNo, barcode, qty));
        }

        return (parsed, errors);
    }

    // ── 엑셀 업로드(S-B2C-DATAGEN-UPLOAD) — 6열 · 1 오더:N 바코드 ─────────────────────
    //   파일 레벨(없음/크기/확장자·MIME)은 컨트롤러가 400 으로 선행. 여기선 구조/행오류를 200 F 로.
    //   Q4 확정 원자성: 행 검증 오류가 하나라도 있으면 커밋 0(트랜잭션 진입 전 조기 반환) + 행별 리포트.
    //   멱등 append: 기존 (배치·오더번호) 오더는 upsert 스킵 · 기존 (오더·바코드) order_item 은 INSERT 스킵
    //   (재업로드 시 신규 카운트 0·기존 reserved/sorted 보존).
    //   ★ 그룹핑 2단: (작업일자·배치명·차수)→work_batch, (그 배치·오더번호)→WcsOrder, 각 행→order_item.
    public async Task<B2cUploadResponse> UploadExcelAsync(Stream excelStream, CancellationToken ct = default)
    {
        // ── 1) 워크북 로드 + 헤더 검증 + 원시 행 수집(파싱 예외는 명시 F 로 표면화 — 삼킴 0) ──
        List<B2cUploadRawRow> rawRows;
        try
        {
            using var wb = new XLWorkbook(excelStream);
            var ws = wb.Worksheet(1);
            var used = ws.RangeUsed();
            if (used is null)
                return FailUpload(B2cConstants.UploadNoData);

            // zip-bomb/대용량 방어 — 압축 해제 후 사용 범위 행·열 상한 조기 차단(O(1)).
            if (used.RowCount() > B2cConstants.UploadMaxRows || used.ColumnCount() > B2cConstants.UploadMaxColumns)
                return FailUpload(B2cConstants.UploadTooLarge);

            var firstRow = used.FirstRow().RowNumber();
            var lastRow  = used.LastRow().RowNumber();
            var firstCol = used.FirstColumn().ColumnNumber();

            // 헤더 필수(구조 검증) — 첫 행 6개 셀이 기대 헤더와 정확히 일치(위치 기반 파싱).
            string Hdr(int off) => ws.Cell(firstRow, firstCol + off).GetString().Trim();
            if (Hdr(0) != B2cConstants.HdrWorkDate || Hdr(1) != B2cConstants.HdrBatchNo
             || Hdr(2) != B2cConstants.HdrWaveNo   || Hdr(3) != B2cConstants.HdrOrderNo
             || Hdr(4) != B2cConstants.HdrBarcode  || Hdr(5) != B2cConstants.HdrQty)
                return FailUpload(B2cConstants.UploadHeaderMismatch);

            rawRows = new List<B2cUploadRawRow>();
            for (var rn = firstRow + 1; rn <= lastRow; rn++)
            {
                string Col(int off) => ws.Cell(rn, firstCol + off).GetString().Trim();
                var workDate = Col(0);
                var batchNo  = Col(1);
                var waveNo   = Col(2);
                var orderNo  = Col(3);
                var barcode  = Col(4);
                var qty      = Col(5);

                // 전 셀 공백 = 빈 행 skip(오류 아님 — 관용).
                if (workDate.Length == 0 && batchNo.Length == 0 && waveNo.Length == 0
                    && orderNo.Length == 0 && barcode.Length == 0 && qty.Length == 0)
                    continue;

                rawRows.Add(new B2cUploadRawRow(rn, workDate, batchNo, waveNo, orderNo, barcode, qty));
            }
        }
        catch (Exception ex)
        {
            // 파싱/포맷 예외 → 명시 F(삼킴 아님 — 사유를 사용자에게 표면화 + 감사 WARN).
            return FailUpload($"엑셀 파싱 오류: {ex.Message}");
        }

        // ── 2) 데이터 행 0 / 상한 초과 ──────────────────────────────────────────────
        if (rawRows.Count == 0)
            return FailUpload(B2cConstants.UploadNoValidData);
        if (rawRows.Count > B2cConstants.UploadDataRowsMax)
            return FailUpload(B2cConstants.UploadTooManyRows(rawRows.Count));

        // ── 3) 행별 검증(순수) — 오류가 하나라도 있으면 전체 거부(Q4 원자성 · 커밋 0) ────────
        var (parsedRows, rowErrors) = ValidateUploadRows(rawRows);
        if (rowErrors.Count > 0)
        {
            var msg = $"업로드 실패 — {rowErrors.Count}개 행에 오류가 있어 전체 취소되었습니다(반영 0건).";
            Audit("B2C_UPLOAD", OperationLogLevel.WARN, null, null,
                $"{{\"upload\":\"reject\",\"dataRows\":{rawRows.Count},\"rowErrors\":{rowErrors.Count}}}");
            return B2cUploadResponse.Fail(msg, null, rowErrors);
        }
        if (parsedRows.Count == 0)
            return FailUpload(B2cConstants.UploadNoValidData);

        // ── 4) 영속화(트랜잭션·원자적) — 배치별 upsert(멱등) → 미할당 오더 → order_item ──────
        var now = DateTime.UtcNow;
        int ordersCreated = 0, itemsCreated = 0;
        var batchKeys = new HashSet<(DateOnly, string, int)>();

        await using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var group in parsedRows.GroupBy(r => (r.WorkDate, r.BatchNo, r.WaveNo)))
            {
                var workDate = DateOnly.ParseExact(group.Key.WorkDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
                var batchNo  = group.Key.BatchNo;
                var waveNo   = group.Key.WaveNo;
                batchKeys.Add((workDate, batchNo, waveNo));

                // work_batch (RUNNING) — UQ(work_date,batch_no,wave_no) 멱등.
                var batch = await _db.WorkBatches.FirstOrDefaultAsync(
                    b => b.WorkDate == workDate && b.BatchNo == batchNo && b.WaveNo == waveNo, ct)
                    .ConfigureAwait(false);
                if (batch is null)
                {
                    batch = new WorkBatch
                    {
                        WorkDate = workDate, BatchNo = batchNo, WaveNo = waveNo,
                        Status = WorkBatchStatus.RUNNING, OpenedAt = now, ClosedAt = null,
                        CreatedAt = now, UpdatedAt = now,
                    };
                    _db.WorkBatches.Add(batch);
                    await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                }

                // 미할당 오더 upsert(DestinationId=null) — UQ(batch,order_no). 오더번호별 1건(1 오더:N 바코드).
                //   distinct OrderNo 로 묶어 같은 오더가 여러 행에 반복돼도 오더는 1건만 생성.
                var existingOrders = await _db.Orders
                    .Where(o => o.WorkBatchId == batch.Id)
                    .ToDictionaryAsync(o => o.OrderNo, ct).ConfigureAwait(false);

                foreach (var orderNo in group.Select(r => r.OrderNo).Distinct())
                {
                    if (!existingOrders.ContainsKey(orderNo))
                    {
                        _db.Orders.Add(new WcsOrder
                        {
                            WorkBatchId = batch.Id, OrderNo = orderNo, OrderType = OrderType.GENERAL,
                            DestinationId = null, DestAssignType = null, DestAssignedAt = null,  // 미할당(2b 소관)
                            Status = OrderStatus.RUNNING, StartedAt = now, ClosedAt = null,
                            CreatedAt = now, UpdatedAt = now,
                        });
                        ordersCreated++;
                    }
                }
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);

                var orderByNo = await _db.Orders.Where(o => o.WorkBatchId == batch.Id)
                    .ToDictionaryAsync(o => o.OrderNo, o => o.Id, ct).ConfigureAwait(false);

                // order_item(barcode=행 바코드, planned_qty=행 수량) — 오더번호 → 그 오더로 연결. INSERT 만.
                //   기존 (OrderId,Barcode) 는 스킵(멱등 · reserved/sorted 실적 보존). 배치 내 바코드 유일은
                //   ValidateUploadRows 가 파일 내에서 이미 보장(한 배치의 한 바코드는 한 오더에만 귀속).
                var existingItemKeys = (await _db.OrderItems
                    .Where(i => orderByNo.Values.Contains(i.OrderId))
                    .Select(i => new { i.OrderId, i.Barcode })
                    .ToListAsync(ct).ConfigureAwait(false))
                    .Select(x => (x.OrderId, x.Barcode)).ToHashSet();

                foreach (var r in group)
                {
                    long orderId = orderByNo[r.OrderNo];
                    if (!existingItemKeys.Contains((orderId, r.Barcode)))
                    {
                        _db.OrderItems.Add(new OrderItem
                        {
                            OrderId = orderId, Barcode = r.Barcode, PlannedQty = r.PlannedQty,
                            ReservedQty = 0, SortedQty = 0, CreatedAt = now, UpdatedAt = now,
                        });
                        itemsCreated++;
                    }
                }
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            await tx.CommitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;   // DB 예외는 삼키지 않음(트랜잭션 롤백 후 재던짐 — 컨트롤러/미들웨어가 표면화).
        }

        var counts = new Dictionary<string, int>
        {
            ["ordersCreated"]     = ordersCreated,
            ["orderItemsCreated"] = itemsCreated,
            ["batches"]           = batchKeys.Count,
            ["dataRows"]          = parsedRows.Count,
        };
        var message = $"업로드 완료 — 데이터 {parsedRows.Count}행, 배치 {batchKeys.Count}개, "
                    + $"미할당 오더 신규 {ordersCreated}건·항목 신규 {itemsCreated}건. 목적지 배정은 설비 관리에서 수행하세요.";
        Audit("B2C_UPLOAD", OperationLogLevel.INFO, null, null,
            $"{{\"upload\":\"ok\",\"dataRows\":{parsedRows.Count},\"batches\":{batchKeys.Count},\"ordersCreated\":{ordersCreated},\"itemsCreated\":{itemsCreated}}}");
        return B2cUploadResponse.Ok(message, counts);
    }

    // 업로드 파일/구조 거부(200 F) — 감사 WARN(전수) + F 응답 조립 단일 소스.
    private B2cUploadResponse FailUpload(string message)
    {
        Audit("B2C_UPLOAD", OperationLogLevel.WARN, null, null,
            $"{{\"upload\":\"reject\",\"message\":\"{Esc(message)}\"}}");
        return B2cUploadResponse.Fail(message);
    }

    // ── 생성 결과 view: 최근 배치 요약(미할당 오더 수 포함) ────────────────────────
    public async Task<List<B2cBatchSummary>> GetBatchesAsync(int take, CancellationToken ct = default)
    {
        var batches = await _db.WorkBatches
            .OrderByDescending(b => b.Id)
            .Take(take)
            .Select(b => new { b.Id, b.WorkDate, b.BatchNo, b.WaveNo, b.Status })
            .ToListAsync(ct).ConfigureAwait(false);

        var result = new List<B2cBatchSummary>(batches.Count);
        foreach (var b in batches)
        {
            int orderTotal      = await _db.Orders.CountAsync(o => o.WorkBatchId == b.Id, ct).ConfigureAwait(false);
            int orderUnassigned = await _db.Orders.CountAsync(o => o.WorkBatchId == b.Id && o.DestinationId == null, ct).ConfigureAwait(false);
            int itemTotal       = await _db.OrderItems
                .CountAsync(i => _db.Orders.Any(o => o.Id == i.OrderId && o.WorkBatchId == b.Id), ct).ConfigureAwait(false);

            result.Add(new B2cBatchSummary(
                b.Id, b.WorkDate, b.BatchNo, b.WaveNo, b.Status.ToString(),
                orderTotal, orderUnassigned, itemTotal));
        }
        return result;
    }

    // ── 요약 집계 ───────────────────────────────────────────────────────────────
    public async Task<List<B2cSorterSummary>> GetSummaryAsync(int? sorterChuteNo, CancellationToken ct = default)
    {
        var sorters = await _db.Destinations
            .Where(d => d.DestType == DestType.SORTER_3D
                     && (sorterChuteNo == null || d.ChuteNo == sorterChuteNo))
            .OrderBy(d => d.ChuteNo)
            .Select(d => new { d.Id, d.ChuteNo, d.Status, d.IsActive })
            .ToListAsync(ct).ConfigureAwait(false);

        var result = new List<B2cSorterSummary>(sorters.Count);
        foreach (var s in sorters)
        {
            int cellTotal = await _db.Cells.CountAsync(c => c.DestinationId == s.Id, ct).ConfigureAwait(false);
            int cellEnabled = await _db.Cells.CountAsync(c => c.DestinationId == s.Id && c.Enabled, ct).ConfigureAwait(false);
            int cellAssigned = await _db.CellAssignments
                .CountAsync(a => a.ReleasedAt == null && a.Cell.DestinationId == s.Id, ct).ConfigureAwait(false);

            // 오더 상태 목록 → in-memory 카운트(값변환 enum GroupBy 번역 회피 — materialize 후 집계).
            var orderStatuses = await _db.Orders
                .Where(o => o.DestinationId == s.Id)
                .Select(o => o.Status)
                .ToListAsync(ct).ConfigureAwait(false);
            int orderTotal     = orderStatuses.Count;
            int orderRunning   = orderStatuses.Count(x => x == OrderStatus.RUNNING);
            int orderCompleted = orderStatuses.Count(x => x == OrderStatus.COMPLETED);
            int orderCancelled = orderStatuses.Count(x => x == OrderStatus.CANCELLED);

            // order_item 수량 합(소터 소속 오더) — nullable Sum 으로 빈 집합 0 보장(GroupBy 회피).
            var q = _db.OrderItems
                .Where(i => _db.Orders.Any(o => o.Id == i.OrderId && o.DestinationId == s.Id));
            int plannedSum  = await q.SumAsync(i => (int?)i.PlannedQty, ct).ConfigureAwait(false) ?? 0;
            int reservedSum = await q.SumAsync(i => (int?)i.ReservedQty, ct).ConfigureAwait(false) ?? 0;
            int sortedSum   = await q.SumAsync(i => (int?)i.SortedQty, ct).ConfigureAwait(false) ?? 0;

            // 진행 중 활성 piece(OQ3 근사 상태 집합·archived 제외) — IsInFlight 술어(OR·값변환 안전).
            int inFlight = await _db.Pieces
                .Where(p => p.DestinationId == s.Id)
                .Where(IsInFlight)
                .CountAsync(ct).ConfigureAwait(false);

            result.Add(new B2cSorterSummary(
                s.Id, s.ChuteNo, s.Status.ToString(), s.IsActive,
                cellTotal, cellEnabled, cellAssigned,
                orderTotal, orderRunning, orderCompleted, orderCancelled,
                plannedSum, reservedSum, sortedSum,
                inFlight));
        }
        return result;
    }

    // ── 셀 상세 ─────────────────────────────────────────────────────────────────
    public async Task<List<B2cCellDetail>> GetDetailAsync(int sorterChuteNo, CancellationToken ct = default)
    {
        var dest = await _db.Destinations
            .FirstOrDefaultAsync(d => d.ChuteNo == sorterChuteNo && d.DestType == DestType.SORTER_3D, ct)
            .ConfigureAwait(false);
        if (dest is null)
            return new List<B2cCellDetail>();   // 미존재 → 빈 목록(일관 정책·500 아님).

        var cells = await _db.Cells
            .Where(c => c.DestinationId == dest.Id)
            .OrderBy(c => c.CellNo)
            .Select(c => new { c.Id, c.CellNo, c.Capacity, c.Enabled })
            .ToListAsync(ct).ConfigureAwait(false);
        if (cells.Count == 0)
            return new List<B2cCellDetail>();

        var cellIds = cells.Select(c => c.Id).ToList();

        // 현재 투입 수량 — SorterCellQty 재사용(IF-05·IF-10 과 동일 산출·archived 제외).
        var loaded = SorterCellQty.LoadedQtyByCell(_db, dest.Id, cellIds);

        // 활성 배정 → 오더번호 + 그 오더 order_item 수량(barcode==orderNo 규약).
        var assigned = await _db.CellAssignments
            .Where(a => a.ReleasedAt == null && a.Cell.DestinationId == dest.Id)
            .Select(a => new
            {
                a.CellId,
                a.Order.OrderNo,
                Reserved = a.Order.Items.Sum(i => (int?)i.ReservedQty) ?? 0,
                Sorted   = a.Order.Items.Sum(i => (int?)i.SortedQty) ?? 0,
            })
            .ToListAsync(ct).ConfigureAwait(false);
        var assignedByCell = assigned
            .GroupBy(a => a.CellId)
            .ToDictionary(g => g.Key, g => g.First());

        return cells.Select(c =>
        {
            assignedByCell.TryGetValue(c.Id, out var a);
            return new B2cCellDetail(
                c.CellNo, c.Capacity, c.Enabled,
                loaded.GetValueOrDefault(c.Id, 0),
                a?.OrderNo, a?.Reserved, a?.Sorted);
        }).ToList();
    }

    // ── 초기화(재테스트 준비 · 배치 스코프 — S-B2C-UX OQ-1) ──────────────────────────
    //   대상 = 배치(work_batch)에 속한 오더(슈트/소터/미할당 무관)에 연결된 piece. 소터 스코프 폐지.
    //   piece 는 order_item(=배치 오더)을 통해 배치에 귀속 — 스코프 술어는 p.OrderItem.Order.WorkBatchId.
    public async Task<B2cManagementResponse> ResetAsync(B2cResetRequest req, CancellationToken ct = default)
    {
        var op = Op(req.OperatorName);
        var batch = await _db.WorkBatches
            .FirstOrDefaultAsync(b => b.Id == req.BatchId, ct)
            .ConfigureAwait(false);
        if (batch is null)
        {
            var msg = $"배치(id={req.BatchId})를 찾을 수 없습니다.";
            Audit("B2C_RESET", OperationLogLevel.WARN, null, null,
                $"{{\"op\":\"{Esc(op)}\",\"batchId\":{req.BatchId},\"notFound\":true}}");
            return B2cManagementResponse.Fail(msg);
        }

        var now = DateTime.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            // 배치 소속 오더 id(수량 리셋·오더 재개·piece 스코프 공용).
            var orderIds = await _db.Orders
                .Where(o => o.WorkBatchId == batch.Id)
                .Select(o => o.Id)
                .ToListAsync(ct).ConfigureAwait(false);

            // ── OQ3 가드(FIX ITER 3 — 코드리뷰 TOCTOU): in-flight 판정을 **트랜잭션 안**·아카이브
            //   UPDATE 직전에 수행한다. 체크가 트랜잭션 밖이면 "체크 → 아카이브" 사이에 IF-05 가 새
            //   RESERVED piece 를 삽입하는 창(TOCTOU)이 생겨, 비강제 reset 이 진행 중 piece 를 조용히
            //   아카이브할 수 있다(사용자 게이트 OQ3 "in-flight 존재 시 force 없이는 거부" 위반).
            //   같은 트랜잭션 내 재판정으로 창을 협착(READ COMMITTED — 단일 운영자 관리 시나리오에서
            //   충분, 리뷰어 확인). 거부 시 Rollback + F(감사 기록은 기존과 동일 — Audit 는 논블로킹
            //   채널 enqueue 라 트랜잭션과 무관). 배치 스코프: piece → order_item → order.WorkBatchId.
            int inFlight = await _db.Pieces
                .Where(p => p.OrderItem != null && orderIds.Contains(p.OrderItem.OrderId))
                .Where(IsInFlight)
                .CountAsync(ct).ConfigureAwait(false);

            if (inFlight > 0 && !req.Force)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                var msg = $"진행 중(in-flight) 작업 {inFlight}건이 있어 초기화를 거부했습니다. 강제 초기화하려면 force=true 로 재요청하세요.";
                Audit("B2C_RESET", OperationLogLevel.WARN, null, null,
                    $"{{\"op\":\"{Esc(op)}\",\"batchId\":{req.BatchId},\"refused\":true,\"inFlight\":{inFlight},\"force\":false}}");
                return B2cManagementResponse.Fail(msg, new Dictionary<string, int> { ["inFlight"] = inFlight });
            }

            // 대상 piece id(아직 활성=archived_at NULL·배치 오더 귀속)를 먼저 캡처 — 연관 이벤트/명령 아카이브에 사용.
            var pieceIds = await _db.Pieces
                .Where(p => p.OrderItem != null && orderIds.Contains(p.OrderItem.OrderId) && p.ArchivedAt == null)
                .Select(p => p.Id)
                .ToListAsync(ct).ConfigureAwait(false);

            // piece_event 아카이브(소프트삭제) — 부모 piece 와 함께. 이미 아카이브된 것 제외.
            int archivedEvents = await _db.PieceEvents
                .Where(e => pieceIds.Contains(e.PieceId) && e.ArchivedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, (DateTime?)now), ct).ConfigureAwait(false);

            // sorter_command 아카이브(소프트삭제) — 셀 currentQty 이중 카운트 차단의 핵심.
            int archivedCommands = await _db.SorterCommands
                .Where(c => pieceIds.Contains(c.PieceId) && c.ArchivedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, (DateTime?)now), ct).ConfigureAwait(false);

            // piece 아카이브(소프트삭제) — 하드삭제 금지(OQ1=B·이력 보존).
            int archivedPieces = await _db.Pieces
                .Where(p => pieceIds.Contains(p.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ArchivedAt, (DateTime?)now), ct).ConfigureAwait(false);

            // order_item reserved/sorted = 0 리셋(배치 소속 오더) — wcs_order/cell_assignment 는 보존(OQ2).
            int resetItems = await _db.OrderItems
                .Where(i => orderIds.Contains(i.OrderId) && (i.ReservedQty != 0 || i.SortedQty != 0))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.ReservedQty, 0)
                    .SetProperty(x => x.SortedQty, 0)
                    .SetProperty(x => x.UpdatedAt, now), ct).ConfigureAwait(false);

            // COMPLETED 오더 → RUNNING 재개(재테스트 가능). enum+값변환기라 tracker 로 안전 갱신.
            //   (QueryDestination 이 COMPLETED/CANCELLED 오더를 제외하므로 재개하지 않으면 재투입 NG.
            //    CANCELLED 는 재개하지 않음 — 취소는 운영자 결정, reset 이 되살리지 않는다.)
            var completedOrders = await _db.Orders
                .Where(o => o.WorkBatchId == batch.Id && o.Status == OrderStatus.COMPLETED)
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var o in completedOrders)
            {
                o.Status    = OrderStatus.RUNNING;
                o.ClosedAt  = null;
                o.UpdatedAt = now;
            }
            int reopened = completedOrders.Count;
            if (reopened > 0)
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);

            var counts = new Dictionary<string, int>
            {
                ["archivedPieces"]        = archivedPieces,
                ["archivedPieceEvents"]   = archivedEvents,
                ["archivedSorterCommands"] = archivedCommands,
                ["resetOrderItems"]       = resetItems,
                ["reopenedOrders"]        = reopened,
                ["forcedInFlight"]        = req.Force ? inFlight : 0,
            };
            var message = $"초기화 완료 — 배치 {batch.BatchNo}(#{batch.WaveNo}): 피스 {archivedPieces}건 보관, "
                        + $"이력 {archivedEvents}·명령 {archivedCommands}건 보관, 수량 리셋 {resetItems}건, 오더 재개 {reopened}건"
                        + (req.Force && inFlight > 0 ? $" (강제 — 진행 중 {inFlight}건 포함)" : "");
            Audit("B2C_RESET", OperationLogLevel.INFO, null, null,
                $"{{\"op\":\"{Esc(op)}\",\"batchId\":{req.BatchId},\"archivedPieces\":{archivedPieces},\"archivedEvents\":{archivedEvents},\"archivedCommands\":{archivedCommands},\"resetItems\":{resetItems},\"reopened\":{reopened},\"force\":{(req.Force ? "true" : "false")}}}");
            return B2cManagementResponse.Ok(message, counts);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    // ── operation_log 감사(STATE·OQ8) — 논블로킹 enqueue(fail-safe) ──────────────────
    private void Audit(string action, OperationLogLevel level, int? sorterChuteNo, long? destinationId, string detail)
        => _opLog.Log(OperationLogCategory.STATE, action, level,
            sorterChuteNo: sorterChuteNo, destinationId: destinationId, detail: detail);

    // 작업자 이름 정규화(감사 귀속 — 공백은 "(unspecified)" 로 대체, 설비 서비스와 동형).
    private static string Op(string? operatorName) =>
        string.IsNullOrWhiteSpace(operatorName) ? "(unspecified)" : operatorName.Trim();

    // detail JSON 문자열 안전 삽입(배치명 등 자유 입력의 따옴표/역슬래시 이스케이프).
    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
