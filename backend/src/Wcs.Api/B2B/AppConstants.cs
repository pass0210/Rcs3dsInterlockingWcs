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

    /// <summary>api_call_log 큐 용량(Bounded Channel).</summary>
    public const int ApiCallLogQueueCapacity = 10_000;

    /// <summary>api_call_log 백그라운드 writer 배치 크기.</summary>
    public const int ApiCallLogBatchSize = 100;

    /// <summary>경로 한정 접두 — 이 접두로 시작하는 요청만 api_call_log 기록·400 형식 분기.</summary>
    public const string WorksRoutePrefix = "/api/v1/works";
}
