namespace Wcs.Api.B2B;

// ════════════════════════════════════════════════════════════════════════════
// S-B2B-3a: 조회 전용 응답 DTO(원시 배열 · camelCase · System.Text.Json Web 기본).
//   E1/E2 투입·분류 로그 / E3 API 호출 이력 / E5 3-way 비교 / E6 박스+내품.
//   전부 읽기 전용 — 신규 쓰기 DTO 0(과잉 [StringLength] 부여 금지 · 계약 Craft 기준).
//   원본 형상: docs/PROGRAM_STRUCTURE.md §2.3·§3.2.6·§3.2.7·§9.6.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// E1/E2 — 투입(INPUT)/분류(SORT) 로그 행. test_log 원본 필드 + 등록 test_data 파생(ChuteNo·ReceiveTime).
/// EquipmentNo: INPUT=inductionNo / SORT=chuteNo(3자리). ChuteNo/ReceiveTime 은 Barcode 단독 상관
/// 서브쿼리 파생(원본 §3.2.6 특성 보존 — 임의 1행). ArchivedAt: NULL=활성.
/// </summary>
public sealed record TestLogRow(
    long      Id,
    string    BizDay,
    string    Batch,
    string    Barcode,
    string?   EquipmentNo,
    string?   Pid,
    string?   Status,
    string?   Reason,
    DateTime? LogTime,
    string?   ChuteNo,       // 등록 test_data 슈트(Barcode 단독 매칭 파생)
    DateTime? ReceiveTime,   // 등록 test_data 수신시각(파생)
    DateTime? ArchivedAt);   // NULL=활성 · 세팅됨=아카이브

/// <summary>E3 — RCS API 호출 이력 행. request/response body 는 마스킹 후 저장된 원문(§9.6).</summary>
public sealed record ApiCallLogRow(
    long      Id,
    string    Endpoint,
    string    HttpMethod,
    string?   RequestBody,
    string?   ResponseStatus,
    string?   ResponseBody,
    int       HttpStatusCode,
    long      DurationMs,
    string?   ClientIp,
    string?   ErrorMessage,
    DateTime  CalledAt);

/// <summary>
/// E5 — 투입/분류/결과 3-way 비교 행(§3.2.7). RegisteredChuteNo=test_data 등록 슈트.
/// SortChuteNo=SORT.equipment_no / ResultChuteNo=work_result.chute_no.
/// IsMatch = 3자 존재 + SortChuteNo==ResultChuteNo. IsMissing = 셋 중 하나라도 없음.
/// 매칭 키에 Batch 포함(이월 결함 교정 — 같은 bizDay 다른 batch 오매칭 방지).
/// </summary>
public sealed record ComparisonRow(
    string    BizDay,
    string    Batch,
    string    Barcode,
    string    RegisteredChuteNo,
    bool      HasInput,
    bool      HasSort,
    bool      HasResult,
    string?   InputStatus,
    DateTime? InputTime,
    string?   SortChuteNo,
    string?   SortStatus,
    DateTime? SortTime,
    string?   ResultChuteNo,
    bool      IsMatch,
    bool      IsMissing);

/// <summary>E6 — 박스 헤더 + 내품(§2.3). items = 박스 내 품목 배열(barcode·qty).</summary>
public sealed record BoxRow(
    long             Id,
    string           BizDay,
    string           Batch,
    string           BoxNo,
    string           ChuteNo,
    string?          EndTime,
    DateTime         CreatedAt,
    List<BoxItemRow> Items);

/// <summary>E6 — 박스 내품 행.</summary>
public sealed record BoxItemRow(
    string Barcode,
    int    Qty);
