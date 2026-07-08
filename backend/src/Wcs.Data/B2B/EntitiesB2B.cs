namespace Wcs.Data.B2B;

// ════════════════════════════════════════════════════════════════════════════
// B2B(작업 테스트 데이터) 엔티티 — 원본 BowooTestBatchSystem_v2 이식.
// 근거(정본): docs/B2B-SCHEMA.md §1(실측 DDL)·§2(엔티티↔컬럼 매핑).
//
// 기존 B2C(WCS↔RCS↔3DS) 17테이블과 완전 분리 — 테이블명·컬럼 충돌 0(§7 확인).
// 컬럼명은 원본 그대로 snake_case(HasColumnName은 WcsDbContext.ConfigureB2B*에서 지정).
//
// ⚠ created_at 는 B2B 로컬타임(DateTime.Now) — 원본 BowooTestBatchSystem_v2 동작 보존.
//   기존 B2C 테이블은 UTC(ERD 원칙)이나 B2B는 분리 테이블이라 로컬타임 유지(사용자 확정 Q3).
//   (log_time/receive_time 은 클라이언트 inTime/sortTime 파싱값 — 원문 의미 보존.)
//
// C# 프로퍼티는 PascalCase(엔티티) → snake_case(DB) → camelCase(API JSON, System.Text.Json).
// pId·inductionNo 는 RCS 자체생성 정수 — 서버 미검증, .ToString()으로 문자열 컬럼에 그대로 저장.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 등록된 테스트 데이터(마스터). bizDay·batch·barcode·chute 배치.
/// unprocessed 조회 시 receive_time 이 일괄 마킹된다(미작업 = receive_time NULL).
/// </summary>
public sealed class TestData
{
    public long      Id          { get; set; }   // PK 대리키 bigint identity
    public string    BizDay      { get; set; } = string.Empty; // 정규화 "YYYY-MM-DD"로 저장
    public string    Batch       { get; set; } = string.Empty;
    public string    Barcode     { get; set; } = string.Empty; // 유니크 아님(동일 바코드 다중 슈트 허용)
    public string    ChuteNo     { get; set; } = string.Empty; // 3자리 zero-pad("001")
    public DateTime? ReceiveTime { get; set; }   // unprocessed 조회 시 일괄 마킹(미작업=NULL)
    public DateTime  CreatedAt   { get; set; }   // B2B 로컬타임(DateTime.Now)
    public string?   Barcode2    { get; set; }   // Reject-Multi-Barcode 2번째 바코드(없으면 단일)
}

/// <summary>
/// 투입/분류 로그 (log_type = INPUT | SORT).
/// test_data_id 는 처리행 매핑용 — 논리적 참조(DB FK 제약 없음 · 원본 동일 · 이력 불변).
/// </summary>
public sealed class TestLog
{
    public long      Id          { get; set; }   // PK 대리키
    public string    LogType     { get; set; } = string.Empty; // "INPUT" 또는 "SORT"
    public string    BizDay      { get; set; } = string.Empty;
    public string    Batch       { get; set; } = string.Empty;
    public string    Barcode     { get; set; } = string.Empty;
    public string?   EquipmentNo { get; set; }   // INPUT=inductionNo, SORT=chuteNo(3자리)
    public string?   Pid         { get; set; }   // RCS 부여 정수를 문자열로 저장(미검증)
    public string?   Status      { get; set; }   // "OK"/"NG"
    public string?   Reason      { get; set; }
    public DateTime? LogTime     { get; set; }   // inTime/sortTime 파싱값(파싱 실패 시 now)
    public DateTime  CreatedAt   { get; set; }   // B2B 로컬타임(DateTime.Now)
    public long?     TestDataId  { get; set; }   // test_data.id 참조(FK 없음 · 인덱스만)
}

/// <summary>전체 작업 결과. results 엔드포인트가 append.</summary>
public sealed class WorkResult
{
    public long     Id        { get; set; }   // PK 대리키
    public string   BizDay    { get; set; } = string.Empty;
    public string   Batch     { get; set; } = string.Empty;
    public string   Barcode   { get; set; } = string.Empty;
    public string?  ChuteNo   { get; set; }   // 3자리 zero-pad(nullable)
    public DateTime CreatedAt { get; set; }   // B2B 로컬타임(DateTime.Now)
}

/// <summary>박스 마감 헤더. (biz_day,batch,box_no) 유니크(재전송 방지).</summary>
public sealed class Box
{
    public long     Id        { get; set; }   // PK 대리키
    public string   BizDay    { get; set; } = string.Empty;
    public string   Batch     { get; set; } = string.Empty;
    public string   BoxNo     { get; set; } = string.Empty;
    public string   ChuteNo   { get; set; } = string.Empty; // 3자리 zero-pad
    public string?  EndTime   { get; set; }   // 클라이언트 문자열 그대로 저장
    public DateTime CreatedAt { get; set; }   // B2B 로컬타임(DateTime.Now)

    // 네비게이션 — 1:N, ON DELETE CASCADE(단일 경로 → 1785 위험 없음)
    public ICollection<BoxItem> Items { get; set; } = [];
}

/// <summary>박스 내 품목 (box 1:N). box_item.barcode 는 100자(test_* 는 50자).</summary>
public sealed class BoxItem
{
    public long   Id      { get; set; }   // PK 대리키
    public long   BoxId   { get; set; }   // FK → box.id, ON DELETE CASCADE
    public string Barcode { get; set; } = string.Empty;
    public int    Qty     { get; set; } = 1;

    // 네비게이션
    public Box Box { get; set; } = null!;
}

/// <summary>RCS API 호출 원문 감사 로그. RcsApiLoggingMiddleware 가 /api/v1/works/ 만 기록.</summary>
public sealed class ApiCallLog
{
    public long     Id             { get; set; }  // PK 대리키
    public string   Endpoint       { get; set; } = string.Empty; // 경로(예: "/api/v1/works/input")
    public string   HttpMethod     { get; set; } = string.Empty;
    public string?  RequestBody    { get; set; }  // 마스킹 후 저장
    public string?  ResponseStatus { get; set; }  // "S"/"F"
    public string?  ResponseBody   { get; set; }  // 4000자 truncate
    public int      HttpStatusCode { get; set; }  // 기본 0
    public long     DurationMs     { get; set; }  // 기본 0
    public string?  ClientIp       { get; set; }
    public string?  ErrorMessage   { get; set; }
    public DateTime CalledAt       { get; set; }  // B2B 로컬타임(DateTime.Now)
}
