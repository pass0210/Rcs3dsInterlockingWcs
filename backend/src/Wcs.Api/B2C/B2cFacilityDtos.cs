using System.ComponentModel.DataAnnotations;

namespace Wcs.Api.B2C;

// ════════════════════════════════════════════════════════════════════════════
// S-B2C-FACILITY: B2C 설비 관리 API DTO — /api/b2c/facility/*.
//
// 프론트 전용 관리 API(RCS 계약 아님). 관리 액션 응답 = B2cManagementResponse({status,message,counts}) 재사용.
// 조회(orders)는 원시 JSON(camelCase). 성공 판정 = res.ok && status=="S"(200 F 오인 금지).
//
// 파괴/변경 안전(OQ-2/OQ-3): 비활성화·재배정은 가드(진행 중 거부·force) + operation_log STATE 감사(전수).
// DataAnnotations 검증 실패 = 400(경로분기 팩토리 allowlist 에 FacilityRoutePrefix 추가 → Fail 형식).
// 비즈니스 실패(중복·미존재·가드 거부) = 200 + {status:"F"}.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>목적지 생성(소터/슈트). SORTER_3D 는 floor 무시(NULL 강제)·chute_detail 없음.</summary>
public sealed class B2cCreateDestinationRequest
{
    /// <summary>슈트번호(UNIQUE). 중복이면 비즈니스 실패(F).</summary>
    [Range(1, B2cConstants.ChuteNoMax, ErrorMessage = "chuteNo must be between 1 and 9999.")]
    public int ChuteNo { get; set; }

    /// <summary>목적지 종류 — "CHUTE" | "SORTER_3D"(대소문자 무시). 그 외 → F.</summary>
    [Required]
    public string DestType { get; set; } = string.Empty;

    /// <summary>슈트 층(선택 — CHUTE 만 의미). SORTER_3D 는 무시(NULL).</summary>
    [Range(0, B2cConstants.FloorMax, ErrorMessage = "floor must be between 0 and 99.")]
    public int? Floor { get; set; }

    /// <summary>슈트 만재 임계(선택 — CHUTE 만). 미지정 시 기본값.</summary>
    [Range(1, B2cConstants.WorkFullQtyMax, ErrorMessage = "workFullQty must be between 1 and 1000000.")]
    public int? WorkFullQty { get; set; }

    /// <summary>작업자 이름(감사 귀속 — 선택, 공백 허용). destination_event operator 로 기록.</summary>
    public string? OperatorName { get; set; }
}

/// <summary>목적지 활성/비활성 토글. 비활성화(false)는 진행 중이면 거부(force 로만 강행 — OQ-2).</summary>
public sealed class B2cActivateRequest
{
    /// <summary>true=활성화 / false=비활성화.</summary>
    public bool IsActive { get; set; }

    /// <summary>진행 중(in-flight/활성 배정) 있어도 강제 비활성화(OQ-2 — 기본 false 거부).</summary>
    public bool Force { get; set; }

    public string? OperatorName { get; set; }
}

/// <summary>목적지 수정 — status/floor/workFullQty 만(chuteNo·destType 변경 불가 — OQ-2·정합 위험).</summary>
public sealed class B2cUpdateDestinationRequest
{
    /// <summary>운영 상태 — "NORMAL" | "PAUSED"(선택·대소문자 무시). 그 외 → F.
    /// ⚠ 실 pause/resume(인메모리·IF-08 push 동기)은 운영 제어(/api/ops)를 쓰는 것이 정본.
    ///   여기 status 변경은 DB 값만 갱신(관리 편의) — 런타임 전이 동기는 안 한다.</summary>
    public string? Status { get; set; }

    [Range(0, B2cConstants.FloorMax, ErrorMessage = "floor must be between 0 and 99.")]
    public int? Floor { get; set; }

    [Range(1, B2cConstants.WorkFullQtyMax, ErrorMessage = "workFullQty must be between 1 and 1000000.")]
    public int? WorkFullQty { get; set; }

    public string? OperatorName { get; set; }
}

/// <summary>
/// 소터 셀 벌크 설정(OQ-1 = UI 생성기) — 행×열 → 순차 cellNo 1..(rows×cols). 스키마 무변경.
/// 멱등: 기존 셀은 Capacity/Enabled 보정, 없는 셀은 생성(축소 시 초과 셀 삭제 안 함 — 보존).
/// </summary>
public sealed class B2cCellBulkRequest
{
    [Range(1, B2cConstants.CellGridDimMax, ErrorMessage = "rows must be between 1 and 100.")]
    public int Rows { get; set; }

    [Range(1, B2cConstants.CellGridDimMax, ErrorMessage = "cols must be between 1 and 100.")]
    public int Cols { get; set; }

    /// <summary>셀 작업 투입 수량(선택). 미지정 시 기본값. NULL 저장은 미지원(무제한은 후속).</summary>
    [Range(1, B2cConstants.CellCapacityMax, ErrorMessage = "capacity must be between 1 and 100000.")]
    public int? Capacity { get; set; }

    /// <summary>셀 활성 여부(기본 true).</summary>
    public bool Enabled { get; set; } = true;

    public string? OperatorName { get; set; }
}

/// <summary>오더→목적지(+셀) 할당. 미시작 오더만(OQ-3). 소터+cellNo 면 cell_assignment 생성.</summary>
public sealed class B2cAssignOrderRequest
{
    public long OrderId { get; set; }

    public long DestinationId { get; set; }

    /// <summary>소터 대상 셀 번호(선택). CHUTE 대상이면 지정 불가(F). 소터+미지정이면 셀 배정 없이 목적지만.</summary>
    [Range(1, int.MaxValue, ErrorMessage = "cellNo must be a positive integer.")]
    public int? CellNo { get; set; }

    public string? OperatorName { get; set; }
}

/// <summary>오더 목적지 할당 해제(재배정 전 단계). 미시작 오더만(OQ-3).</summary>
public sealed class B2cUnassignOrderRequest
{
    public long OrderId { get; set; }

    public string? OperatorName { get; set; }
}

// ── 조회 응답(원시 JSON) ────────────────────────────────────────────────────────

/// <summary>
/// 오더 할당 UI 소스 행 — 미할당/전체 오더 목록. CanReassign = 미시작(reserved=0·sorted=0·활성 피스 0·OQ-3).
/// </summary>
public sealed record B2cOrderDto(
    long    OrderId,
    string  OrderNo,
    long    BatchId,
    string  BatchLabel,          // "YYYY-MM-DD / batchNo #wave"
    string  Barcode,             // 대표 바코드(첫 order_item)
    int     PlannedQty,          // Σ order_item.planned_qty
    int     ReservedQty,         // Σ order_item.reserved_qty
    int     SortedQty,           // Σ order_item.sorted_qty
    string  Status,              // OrderStatus(WAITING/RUNNING/COMPLETED/CANCELLED)
    long?   DestinationId,       // 미할당이면 null
    int?    DestinationChuteNo,
    string? DestType,            // "CHUTE" | "SORTER_3D" | null
    string? AssignType,          // DestAssignType(UPSTREAM/AUTO/MANUAL) | null
    int?    AssignedCellNo,      // 소터 활성 배정 셀(없으면 null)
    bool    HasActivePiece,      // 차단 피스 존재(활성·비아카이브·**비-DENIED**) — OQ-3 보완: DENIED 는 제외
    bool    CanReassign);        // OQ-3: 미시작(예약=0·분류=0·차단 피스 0)이면 배정/재배정 허용
