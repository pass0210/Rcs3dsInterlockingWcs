namespace Wcs.Api.B2B;

// ════════════════════════════════════════════════════════════════════════════
// AppConstants (B2B) — 원본 BowooTestBatchSystem_v2 상수 이식(§5).
// B2B 전용 네임스페이스(기존 Wcs.Api 상수와 격리).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>B2B 공통 상수.</summary>
public static class AppConstants
{
    /// <summary>ChuteNo 3자리 zero-pad 포맷("001").</summary>
    public const string ChuteNoFormat = "D3";

    /// <summary>input/classification/results/box 공통 qty 상한.</summary>
    public const int QtyMaxPerRequest = 9999;

    /// <summary>api_call_log 응답 본문 DB 저장 시 절단 길이.</summary>
    public const int LogTruncateDbLength = 4000;

    /// <summary>api_call_log 조회(E3) 최대 반환 건수 — 하드코딩 금지(절대규칙 #7). 원본 §3.5.</summary>
    public const int ApiCallLogMaxItems = 500;

    /// <summary>api_call_log 큐 용량(Bounded Channel).</summary>
    public const int ApiCallLogQueueCapacity = 10_000;

    /// <summary>api_call_log 백그라운드 writer 배치 크기.</summary>
    public const int ApiCallLogBatchSize = 100;

    /// <summary>경로 한정 접두 — 이 접두로 시작하는 요청만 api_call_log 기록·400 형식 분기.</summary>
    public const string WorksRoutePrefix = "/api/v1/works";

    // ── S-B2B-2a: test-data 관리 API ─────────────────────────────────────────
    /// <summary>test-data 관리 API 경로 접두 — ModelState 400 형식 분기 allowlist(additive).</summary>
    public const string TestDataRoutePrefix = "/api/test-data";

    /// <summary>엑셀 업로드 최대 크기(바이트) — 10MB. 초과 시 400(§1.2).</summary>
    public const long UploadMaxBytes = 10L * 1024 * 1024;

    /// <summary>수동 생성 바코드 개수 상한(§1.1 — RCS qty 9999와 별개·다른 의미).</summary>
    public const int BarcodeCountMax = 10000;

    // ── 업로드 팽창(zip-bomb) 방어 상한 — 압축 해제 후 사용 범위 행·열 상한(하드코딩 금지·절대규칙 #7) ──
    /// <summary>업로드 엑셀 사용 범위 최대 행수 — 초과 시 조기 차단(zip-bomb/대용량 방어).</summary>
    public const int UploadMaxRows = 100_000;

    /// <summary>업로드 엑셀 사용 범위 최대 열수 — 초과 시 조기 차단(zip-bomb/대용량 방어).</summary>
    public const int UploadMaxColumns = 64;
}
