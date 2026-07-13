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
    /// <summary>라우트 접두(OQ7) — 기존 /api/test-data·/api/v1·/api/monitor·/api/ops 무충돌.</summary>
    public const string RoutePrefix = "/api/b2c/test-data";

    public const int CellCountMax     = 200;    // 물리 소터 셀 상한(생성 셀 개수).
    public const int CellCapacityMax  = 100000; // 셀 작업 투입 수량 상한.
    public const int PlannedQtyMax    = 100000; // 오더 항목 계획 수량 상한.
    public const int WaveNoMax        = 9999;   // 차수 상한.
    public const int SorterChuteNoMax = 9999;   // 소터 슈트번호 상한.

    public const int DefaultCellCapacity = 3;   // 현장 시드 기본(seed-field-20cells.sql @cellCap).
    public const int DefaultPlannedQty   = 3;   // 현장 시드 기본(@plannedQty).
    public const int DefaultWaveNo       = 1;

    // 오더번호/바코드 안전 문자(패턴 인젝션 방지) — 숫자·영문·하이픈·언더스코어만.
    public const string OrderPrefixRegex = @"^[A-Za-z0-9_\-]{1,50}$";
    public const string OrderPrefixError = "orderPrefix may only contain letters, digits, hyphens, and underscores (1-50 chars).";

    // 작업일자 형식(B2B ValidationRules.BizDayRegex 와 동형).
    public const string WorkDateRegex = @"^\d{8}$|^\d{4}-\d{2}-\d{2}$";
    public const string WorkDateError = "workDate must be in YYYYMMDD or YYYY-MM-DD format.";
}

/// <summary>
/// 생성 요청(OQ4 멱등 upsert·OQ5 규약·OQ6 셀·소터 생성). 셀 N ↔ 오더 N 결정적 배정(N↔N).
/// orderNo == barcode == "{orderPrefix}-{NN}"(zero-pad) — 현 현장 규약 재현(0701-CELL-01 형).
/// </summary>
public sealed class B2cGenerateRequest
{
    /// <summary>대상 3D 소터 슈트번호. 없으면 SORTER_3D 로 생성(OQ6).</summary>
    [Range(1, B2cConstants.SorterChuteNoMax, ErrorMessage = "sorterChuteNo must be between 1 and 9999.")]
    public int SorterChuteNo { get; set; }

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

    /// <summary>생성 셀 개수(1..N) — 각 셀에 오더 1개 대응(N↔N).</summary>
    [Range(1, B2cConstants.CellCountMax, ErrorMessage = "cellCount must be between 1 and 200.")]
    public int CellCount { get; set; }

    /// <summary>셀 작업 투입 수량(cell.capacity). ≤0 은 클라 검증에서 걸러짐 — 여기선 양수만.</summary>
    [Range(1, B2cConstants.CellCapacityMax, ErrorMessage = "cellCapacity must be between 1 and 100000.")]
    public int CellCapacity { get; set; } = B2cConstants.DefaultCellCapacity;

    /// <summary>오더 항목 계획 수량(order_item.planned_qty).</summary>
    [Range(1, B2cConstants.PlannedQtyMax, ErrorMessage = "plannedQty must be between 1 and 100000.")]
    public int PlannedQty { get; set; } = B2cConstants.DefaultPlannedQty;

    /// <summary>오더번호/바코드 접두(예 "0701-CELL"). 전체 = "{prefix}-{NN}"(zero-pad, N↔N).</summary>
    [Required]
    [RegularExpression(B2cConstants.OrderPrefixRegex, ErrorMessage = B2cConstants.OrderPrefixError)]
    public string OrderPrefix { get; set; } = string.Empty;
}

/// <summary>초기화 요청(OQ1 아카이브·OQ2 범위·OQ3 가드). 대상 소터 지정 + force.</summary>
public sealed class B2cResetRequest
{
    /// <summary>대상 3D 소터 슈트번호(초기화 범위 — OQ2 대상 소터 지정).</summary>
    [Range(1, B2cConstants.SorterChuteNoMax, ErrorMessage = "sorterChuteNo must be between 1 and 9999.")]
    public int SorterChuteNo { get; set; }

    /// <summary>진행 중(in-flight) 작업이 있어도 강제 초기화(OQ3 — 기본 false 거부, UI 경고 후 true 재요청).</summary>
    public bool Force { get; set; }
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
