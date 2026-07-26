using System.ComponentModel.DataAnnotations;

namespace Wcs.Api.B2C;

// ════════════════════════════════════════════════════════════════════════════
// S-B2C-DATAGEN: B2C(3D 소터) 테스트 데이터 관리 API DTO — docs/B2C-DATAGEN.md.
//
// · 프론트 전용 관리 API(RCS 계약 아님). 라우트 접두 = /api/b2c/test-data (OQ7).
// · 관리 액션(generate/reset) 응답 = { status:"S"|"F", message, counts } (B2cManagementResponse).
//   비즈니스 실패도 HTTP 200 + status "F". 파라미터 검증 실패만 HTTP 400(+ 동일 형식 — allowlist 확장).
// · 조회(summary/detail)는 원시 JSON(camelCase, System.Text.Json Web 기본).
// · 하드코딩 금지(절대규칙 #7): 수량/개수 상한은 B2cConstants 상수로 외부화.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>B2C 관리 API 상한·기본값 상수(절대규칙 #7 — 하드코딩 대신 단일 소스).</summary>
internal static class B2cConstants
{
    /// <summary>생성/초기화(2a) 라우트 접두 — 기존 /api/test-data·/api/v1·/api/monitor·/api/ops 무충돌.</summary>
    public const string RoutePrefix = "/api/b2c/test-data";

    /// <summary>설비 관리(2b) 라우트 접두 — 목적지/셀/오더 할당(무충돌·신설).</summary>
    public const string FacilityRoutePrefix = "/api/b2c/facility";

    // ── 생성(2a 슬림) 상한 ────────────────────────────────────────────────────
    /// <summary>1회 생성 가능한 오더/바코드 최대 개수(계획수량 = 생성 개수 · OQ-4).</summary>
    public const int GenerateCountMax = 1000;

    // ── 설비 관리(2b) 상한 ────────────────────────────────────────────────────
    public const int CellCountMax    = 200;    // 소터 셀 벌크 생성 상한(행×열 총합).
    public const int CellCapacityMax = 100000; // 셀 작업 투입 수량 상한.
    public const int CellGridDimMax  = 100;    // 셀 벌크 생성 행/열 각 축 상한(행×열 ≤ CellCountMax 도 검증).
    public const int WaveNoMax       = 9999;   // 차수 상한.
    public const int ChuteNoMax      = 9999;   // 목적지 슈트번호 상한(소터·슈트 공용).
    public const int WorkFullQtyMax  = 1000000;// 슈트 만재 임계 상한.
    public const int FloorMax        = 99;     // 슈트 층 상한(소터는 NULL).

    public const int DefaultCellCapacity = 3;   // 셀 작업 투입 수량 기본.
    public const int DefaultWorkFullQty  = 100; // 슈트 만재 임계 기본(시드 동등).
    public const int DefaultWaveNo       = 1;
    public const int DefaultPlannedQty   = 1;   // 업로드 수량 컬럼 공백 시 order_item.planned_qty 기본.
    public const int BatchNoMaxLength    = 100;  // 배치명 최대 길이(생성 요청·업로드 검증 공용 상수 — 하드코딩 금지).

    // 오더번호/바코드 안전 문자(패턴 인젝션 방지) — 숫자·영문·하이픈·언더스코어만.
    public const string BarcodePrefixRegex = @"^[A-Za-z0-9_\-]{1,50}$";
    public const string BarcodePrefixError = "barcodePrefix may only contain letters, digits, hyphens, and underscores (1-50 chars).";

    // 작업일자 형식(B2B ValidationRules.BizDayRegex 와 동형).
    public const string WorkDateRegex = @"^\d{8}$|^\d{4}-\d{2}-\d{2}$";
    public const string WorkDateError = "workDate must be in YYYYMMDD or YYYY-MM-DD format.";

    // ════════════════════════════════════════════════════════════════════════
    // S-B2C-EXCEL-UPLOAD: 엑셀 업로드 상한·양식 컬럼·메시지(하드코딩 금지·절대규칙 #7).
    //   확정 결정(2026-07-26): 행 단위 = 오더/바코드 1건, .xlsx 전용(.xls 거부),
    //   B2B AppConstants 제한 미러(바이트/행/열·zip-bomb), 데이터 행 상한 = GenerateCountMax(1000),
    //   멱등 append + 오류 시 전체 거부(atomic). 정적 양식 파일(동적 엔드포인트 없음).
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>업로드 파일 최대 크기(바이트) — B2B 10MB 미러(단일 값·드리프트 0).</summary>
    public const long UploadMaxBytes = Wcs.Api.B2B.AppConstants.UploadMaxBytes;
    /// <summary>업로드 엑셀 사용 범위 최대 행수 — zip-bomb/대용량 조기 차단(B2B 미러).</summary>
    public const int UploadMaxRows = Wcs.Api.B2B.AppConstants.UploadMaxRows;
    /// <summary>업로드 엑셀 사용 범위 최대 열수 — zip-bomb/대용량 조기 차단(B2B 미러).</summary>
    public const int UploadMaxColumns = Wcs.Api.B2B.AppConstants.UploadMaxColumns;
    /// <summary>업로드 1회 데이터 행 상한 — 생성 상한(GenerateCountMax=1000)과 동일(한 업로드 오더 총량).</summary>
    public const int UploadDataRowsMax = GenerateCountMax;
    /// <summary>업로드 행별 수량(계획수량) 상한 — order_item.planned_qty(B2B QtyMaxPerRequest 미러).</summary>
    public const int UploadPlannedQtyMax = Wcs.Api.B2B.AppConstants.QtyMaxPerRequest;

    // 양식 헤더 문자열(파서·정적 템플릿이 공유하는 단일 소스 — 헤더 드리프트 0).
    //   컬럼 순서(위치 기반 파싱): [작업일자][배치명][차수][오더번호][바코드][수량].
    //   ★ S-B2C-DATAGEN-UPLOAD: 오더번호 컬럼 신설(바코드 앞) — 1 오더:N 바코드 지원(오더≠바코드).
    public const string HdrWorkDate = "작업일자";
    public const string HdrBatchNo  = "배치명";
    public const string HdrWaveNo   = "차수";
    public const string HdrOrderNo  = "오더번호";
    public const string HdrBarcode  = "바코드";
    public const string HdrQty      = "수량";

    // 바코드 안전 문자(패턴 인젝션 방지) — 숫자·영문·하이픈·언더스코어(1~100자).
    //   생성 폼 접두(1~50)보다 길게 허용(임의 실바코드 직접 업로드 — 확정 결정 Q1).
    public const string UploadBarcodeRegex = @"^[A-Za-z0-9_\-]{1,100}$";

    // 오더번호 안전 문자 — 바코드와 동일 규칙 재사용(단일 소스 · 드리프트 0). 오더번호는 wcs_order.order_no.
    public const string UploadOrderNoRegex = UploadBarcodeRegex;

    // ── 파일 레벨 검증 메시지(HTTP 400 — 컨트롤러 선행) ─────────────────────────
    public const string UploadNoFile        = "파일을 선택하세요.";
    public const string UploadFileTooBig     = "파일 크기는 10MB 이하여야 합니다.";
    public const string UploadOnlyXlsx       = "엑셀(.xlsx) 파일만 업로드할 수 있습니다.";
    public const string UploadInvalidFormat  = "잘못된 파일 형식입니다.";

    // ── 파싱/구조 검증 메시지(HTTP 200 + status "F") ──────────────────────────
    public const string UploadNoData         = "엑셀에 데이터가 없습니다.";
    public const string UploadTooLarge        = "엑셀 파일이 너무 큽니다(허용 범위를 초과).";
    public const string UploadHeaderMismatch  = "양식 헤더가 올바르지 않습니다. 첫 행은 작업일자·배치명·차수·오더번호·바코드·수량 이어야 합니다.";
    public const string UploadNoValidData     = "업로드할 유효한 데이터가 없습니다.";
    public static string UploadTooManyRows(int rows)
        => $"데이터 행이 너무 많습니다({rows}행) — 한 번에 최대 {UploadDataRowsMax}행까지 업로드할 수 있습니다.";
}

/// <summary>
/// 생성 요청(2a 슬림 — 5 파라미터). 오더/바코드만 생성한다(목적지 미할당 — 배정은 설비 관리 2b).
///
/// ★ OQ-4 확정: <see cref="PlannedQty"/> = **생성할 오더/바코드 개수 N**(각 order_item.planned_qty=1 고정).
///   생성 결과 = 미할당 오더 N건(DestinationId=null·DestAssignType=null) + order_item(barcode="{prefix}-{NN}").
///   소터/셀/cell_assignment 는 생성하지 않는다(설비 관리 2b 로 이관 — 스키마 무변경·마이그레이션 0).
/// </summary>
public sealed class B2cGenerateRequest
{
    /// <summary>작업일자 — YYYYMMDD | YYYY-MM-DD.</summary>
    [Required]
    [RegularExpression(B2cConstants.WorkDateRegex, ErrorMessage = B2cConstants.WorkDateError)]
    public string WorkDate { get; set; } = string.Empty;

    /// <summary>배치명(work_batch.batch_no).</summary>
    [Required]
    [StringLength(100, MinimumLength = 1)]
    public string BatchNo { get; set; } = string.Empty;

    /// <summary>차수(work_batch.wave_no).</summary>
    [Range(1, B2cConstants.WaveNoMax, ErrorMessage = "waveNo must be between 1 and 9999.")]
    public int WaveNo { get; set; } = B2cConstants.DefaultWaveNo;

    /// <summary>계획수량 = 생성할 오더/바코드 개수 N(OQ-4). 각 오더의 order_item.planned_qty 는 1로 고정.</summary>
    [Range(1, B2cConstants.GenerateCountMax, ErrorMessage = "plannedQty(생성 개수) must be between 1 and 1000.")]
    public int PlannedQty { get; set; }

    /// <summary>오더번호/바코드 접두(예 "0714-A"). 전체 = "{prefix}-{NN}"(zero-pad).</summary>
    [Required]
    [RegularExpression(B2cConstants.BarcodePrefixRegex, ErrorMessage = B2cConstants.BarcodePrefixError)]
    public string BarcodePrefix { get; set; } = string.Empty;
}

/// <summary>
/// 초기화 요청(재테스트 준비) — ★ S-B2C-UX(OQ-1): 스코프를 **배치(work_batch)** 로 재정의.
///   기존 소터 스코프(sorterChuteNo)는 폐지 — "초기화 = 생성한 배치를 되돌린다"는 도메인 판단과 정합.
///   대상 배치에 속한 오더(슈트/소터/미할당 무관)의 piece 를 아카이브·수량 리셋·COMPLETED→RUNNING 재개한다.
///   시맨틱(아카이브 소프트삭제·수량 리셋·오더/배정 보존·in-flight 거부+force·archived-exclusion)은 전부 보존.
/// </summary>
public sealed class B2cResetRequest
{
    /// <summary>대상 배치 대리키(work_batch.id · 초기화 범위 — OQ-1 배치 스코프).</summary>
    [Range(1, long.MaxValue, ErrorMessage = "batchId must be a positive integer.")]
    public long BatchId { get; set; }

    /// <summary>진행 중(in-flight) 작업이 있어도 강제 초기화(OQ3 — 기본 false 거부, UI 경고 후 true 재요청).</summary>
    public bool Force { get; set; }

    /// <summary>작업자 이름(감사 귀속 — OQ-3 · 선택, 공백 허용). operation_log detail 에 기록.</summary>
    public string? OperatorName { get; set; }
}

// ── 관리 액션 응답 ({status, message, counts}) ──────────────────────────────────

/// <summary>
/// B2C 관리 액션 응답 — { status:"S"|"F", message, counts }.
/// counts 는 액션별 처리 건수(생성/아카이브/리셋). 프론트 성공 판정 = res.ok && status=="S".
/// </summary>
public sealed record B2cManagementResponse(
    string Status,
    string Message,
    IReadOnlyDictionary<string, int>? Counts = null)
{
    public static B2cManagementResponse Ok(string message, IReadOnlyDictionary<string, int>? counts = null)
        => new("S", message, counts);

    public static B2cManagementResponse Fail(string message, IReadOnlyDictionary<string, int>? counts = null)
        => new("F", message, counts);
}

// ── 엑셀 업로드 응답(S-B2C-EXCEL-UPLOAD) ────────────────────────────────────────

/// <summary>
/// 엑셀 업로드 행별 오류 리포트 항목 — Fail-Loud(전체 거부 시 오류가 있던 행을 모두 반환).
/// <see cref="Row"/> = 엑셀 실제 행 번호(1-base·헤더행 포함). <see cref="Message"/> = 그 행의 사유(복수 사유는 공백 결합).
/// </summary>
public sealed record B2cUploadRowError(int Row, string Message);

/// <summary>
/// 엑셀 업로드 결과 — { status:"S"|"F", message, counts, rowErrors }.
///   · 성공(S): counts = ordersCreated·orderItemsCreated·batches·dataRows. rowErrors = null.
///   · 실패(F): message + (행별 검증 실패 시) rowErrors. Q4 확정 원자성 — 오류 시 커밋 0(rowErrors 비어있지 않으면 전체 거부).
/// 프론트 성공 판정 = res.ok && status=="S"(200 F 오인 금지 — 기존 함정).
/// </summary>
public sealed record B2cUploadResponse(
    string Status,
    string Message,
    IReadOnlyDictionary<string, int>? Counts = null,
    IReadOnlyList<B2cUploadRowError>? RowErrors = null)
{
    public static B2cUploadResponse Ok(string message, IReadOnlyDictionary<string, int>? counts = null)
        => new("S", message, counts, null);

    public static B2cUploadResponse Fail(
        string message,
        IReadOnlyDictionary<string, int>? counts = null,
        IReadOnlyList<B2cUploadRowError>? rowErrors = null)
        => new("F", message, counts, rowErrors);
}

/// <summary>
/// 업로드 원시 행(엑셀에서 위치 기반으로 읽은 문자열 셀 — 파싱 전) — 순수 검증 입력(절대규칙 #8·테스트 가능).
/// <see cref="RowNumber"/> = 엑셀 실제 행 번호(오류 리포트 귀속).
/// 컬럼 순서(위치 기반): [WorkDate][BatchNo][WaveNo][OrderNo][Barcode][Qty](6열).
/// </summary>
public sealed record B2cUploadRawRow(
    int RowNumber, string WorkDate, string BatchNo, string WaveNo, string OrderNo, string Barcode, string Qty);

/// <summary>
/// 검증 통과 후 파싱된 행(영속화 준비 완료). <see cref="WorkDate"/> = 정규화("yyyy-MM-dd").
/// ★ S-B2C-DATAGEN-UPLOAD: <see cref="OrderNo"/>(wcs_order.order_no) 와 <see cref="Barcode"/>
///   (order_item.barcode)는 **별개**(1 오더:N 바코드 — 같은 OrderNo 여러 행이 하나의 오더로 묶임).
///   PlannedQty = order_item.planned_qty.
/// </summary>
public sealed record B2cUploadParsedRow(
    int RowNumber, string WorkDate, string BatchNo, int WaveNo, string OrderNo, string Barcode, int PlannedQty);

// ── 조회 응답(원시 JSON) ────────────────────────────────────────────────────────

/// <summary>소터별 B2C 테스트 데이터 요약(집계 — archived 제외).</summary>
public sealed record B2cSorterSummary(
    long   DestinationId,
    int    ChuteNo,
    string Status,       // NORMAL | PAUSED
    bool   IsActive,
    int    CellTotal,        // 소터 소속 전체 셀 수
    int    CellEnabled,      // enabled 셀 수
    int    CellAssigned,     // 활성 cell_assignment 보유 셀 수
    int    OrderTotal,
    int    OrderRunning,
    int    OrderCompleted,
    int    OrderCancelled,
    int    PlannedSum,       // Σ order_item.planned_qty
    int    ReservedSum,      // Σ order_item.reserved_qty
    int    SortedSum,        // Σ order_item.sorted_qty
    int    InFlightPieces);  // 진행 중 활성 piece 수(archived 제외 — OQ3 근사 상태 집합)

/// <summary>셀 상세 행(detail 그리드용 — currentQty 는 SorterCellQty 재사용·archived 제외).</summary>
public sealed record B2cCellDetail(
    int     CellNo,
    int?    Capacity,
    bool    Enabled,
    int     CurrentQty,      // 현재 투입 수량(배정-기간 COMPLETED sorter_command 합·archived 제외)
    string? AssignedOrderNo, // 활성 배정 오더번호(없으면 null)
    int?    ReservedQty,     // 배정 오더의 order_item 합(barcode==orderNo 규약)
    int?    SortedQty);

/// <summary>
/// 생성 결과 view(2a) — 최근 work_batch 요약. 데이터 생성 페이지가 "무엇이 만들어졌는지" 확인.
/// 미할당(destination 없음) 오더 수를 별도로 노출 — 슬림 생성이 미할당 오더를 만든다는 계약 반영.
/// </summary>
public sealed record B2cBatchSummary(
    long     BatchId,
    DateOnly WorkDate,
    string   BatchNo,
    int      WaveNo,
    string   Status,           // WorkBatchStatus(WAITING/RUNNING/CLOSED)
    int      OrderTotal,       // 배치 소속 전체 오더 수
    int      OrderUnassigned,  // DestinationId==null 오더 수(미할당)
    int      ItemTotal);       // 배치 소속 order_item 수
