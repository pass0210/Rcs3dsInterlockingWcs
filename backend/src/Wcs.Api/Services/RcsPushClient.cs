using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Wcs.Data;

namespace Wcs.Api;

// ════════════════════════════════════════════════════════════════════════════
// RcsPushClient — IF-08 아웃바운드 푸시 클라이언트 (WCS → RCS)
//
// RCS↔WCS 재설계 Phase 2, Scope A·E:
//   목적지 ready 전이 시 POST {RcsPush:BaseUrl}{Path}로 {chuteNo, ready, timeStamp} 푸시.
//   - IHttpClientFactory 경유(직접 new HttpClient() 금지 — 소켓 고갈·DNS 갱신 방지).
//   - 재시도: 설정값 경유 지수 백오프(고정 sleep 금지). 소진 후 false 반환(실패 명시).
//   - 예외 삼킴 금지(Fail-Loud): 최종 실패는 명시 로깅 + bool 반환으로 호출자에 전달.
//
// 이 클라이언트는 "1건 전송 + 재시도"만 책임진다. "전이 감지·전이당 1회·복구 재푸시"는
// DestinationStatusPusher가 책임(관심사 분리).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// IF-08 푸시 페이로드 — {chuteNo, ready, timeStamp}.
/// STJ 기본 camelCase 직렬화로 와이어가 `{"chuteNo":15,"ready":true,"timeStamp":"..."}`.
/// 개별 full/paused/online은 포함하지 않는다(복합 ready 하나로 접힘 — 스펙 IF-08).
/// </summary>
public sealed record DestinationStatusPushPayload(int ChuteNo, bool Ready, string TimeStamp);

/// <summary>
/// IF-08 아웃바운드 푸시 클라이언트 인터페이스.
/// DestinationStatusPusher가 전이 감지 후 이 클라이언트로 1건 전송한다.
/// </summary>
public interface IRcsPushClient
{
    /// <summary>푸시가 활성인지(BaseUrl 설정 시 true — 사용자 확정4).</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// destination-status 1건을 RCS로 푸시(재시도 포함).
    /// 성공(2xx) 시 true, 재시도 소진 후 실패 시 false. 예외를 던지지 않는다(false로 수렴).
    /// </summary>
    Task<bool> PushAsync(DestinationStatusPushPayload payload, CancellationToken ct = default);
}

/// <summary>
/// IRcsPushClient 구현 — IHttpClientFactory 기반 named client + 설정 경유 지수 백오프 재시도.
/// </summary>
public sealed class RcsPushClient : IRcsPushClient
{
    /// <summary>IHttpClientFactory 등록 시 사용하는 named client 이름.</summary>
    public const string HttpClientName = "RcsPush";

    private readonly IHttpClientFactory     _httpFactory;
    private readonly RcsPushOptions         _opt;
    private readonly ILogger<RcsPushClient> _log;
    private readonly IOperationLogger       _opLog;

    public RcsPushClient(
        IHttpClientFactory    httpFactory,
        IOptions<WcsOptions>  options,
        ILogger<RcsPushClient> log,
        IOperationLogger      opLog)
    {
        _httpFactory = httpFactory;
        _opt         = options.Value.RcsPush;
        _log         = log;
        _opLog       = opLog;
    }

    /// <inheritdoc/>
    public bool IsEnabled => _opt.IsEnabled;

    /// <inheritdoc/>
    public async Task<bool> PushAsync(DestinationStatusPushPayload payload, CancellationToken ct = default)
    {
        // 사용자 확정4: BaseUrl 미설정이면 푸시 비활성(no-op). 호출자(Pusher)가 이미 IsEnabled로
        // 걸러내지만 방어적으로 한 번 더 — "성공"으로 간주하지 않고 false 반환(미알림 상태 유지).
        if (!_opt.IsEnabled)
            return false;

        // 절대규칙 #7: 엔드포인트는 BaseUrl + Path 설정 조합(하드코딩 0).
        var url = CombineUrl(_opt.BaseUrl!, _opt.Path);

        // 총 시도 = 1(최초) + RetryCount(재시도). 사용자 확정2: 기본 3회 지수 백오프.
        int maxAttempts = 1 + Math.Max(0, _opt.RetryCount);

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // IHttpClientFactory 경유 — 직접 new HttpClient() 금지(소켓 고갈 방지).
                var http = _httpFactory.CreateClient(HttpClientName);

                using var resp = await http.PostAsJsonAsync(url, payload, ct).ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                {
                    if (attempt > 1)
                        _log.LogInformation(
                            "[IF-08푸시] 성공(재시도 {Attempt}회차): chuteNo={ChuteNo} ready={Ready}",
                            attempt, payload.ChuteNo, payload.Ready);
                    // operation_log: IF-08 아웃바운드 푸시 전수(성공) — 부수 기록.
                    _opLog.Log(OperationLogCategory.API, "IF08_PUSH",
                        sorterChuteNo: payload.ChuteNo,
                        detail: $"{{\"chuteNo\":{payload.ChuteNo},\"ready\":{(payload.Ready ? "true" : "false")},\"result\":\"OK\",\"attempt\":{attempt}}}");
                    return true;
                }

                // 비2xx → 재시도 경로(연결은 됐으나 RCS가 5xx/4xx 응답).
                _log.LogWarning(
                    "[IF-08푸시] 비2xx 응답(시도 {Attempt}/{Max}): chuteNo={ChuteNo} ready={Ready} status={Status}",
                    attempt, maxAttempts, payload.ChuteNo, payload.Ready, (int)resp.StatusCode);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 호스트 종료/푸셔 정지 — 재시도 중단(취소 전파). 미알림 상태 유지(확정3).
                throw;
            }
            catch (Exception ex)
            {
                // 연결 거부·타임아웃·DNS 실패 등 — 재시도 경로. 예외 삼키지 않고 경고 로깅(Fail-Loud).
                _log.LogWarning(ex,
                    "[IF-08푸시] 전송 예외(시도 {Attempt}/{Max}): chuteNo={ChuteNo} ready={Ready}",
                    attempt, maxAttempts, payload.ChuteNo, payload.Ready);
            }

            // 마지막 시도면 백오프 없이 종료(소진).
            if (attempt < maxAttempts)
            {
                var delay = ComputeBackoffDelay(attempt);
                try
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
            }
        }

        // 재시도 소진 — 최종 실패 명시 로깅(Fail-Loud). false 반환 → 호출자가 미알림 상태 유지(확정3).
        _log.LogError(
            "[IF-08푸시] 재시도 소진({Max}회) — RCS 미도달: chuteNo={ChuteNo} ready={Ready}. " +
            "미알림 상태 유지 — RCS 복구 시 재푸시.",
            maxAttempts, payload.ChuteNo, payload.Ready);
        // operation_log: IF-08 아웃바운드 푸시 전수(실패) — 부수 기록.
        _opLog.Log(OperationLogCategory.API, "IF08_PUSH", level: OperationLogLevel.WARN,
            sorterChuteNo: payload.ChuteNo,
            detail: $"{{\"chuteNo\":{payload.ChuteNo},\"ready\":{(payload.Ready ? "true" : "false")},\"result\":\"FAIL\",\"attempts\":{maxAttempts}}}");
        return false;
    }

    // ── 지수 백오프 지연 산출 (설정값 경유 — 고정 sleep 0) ─────────────────────
    // attempt n회차(1-기반) 실패 후 지연 = RetryBaseDelayMs × 2^(n-1), 상한 RetryMaxDelayMs.
    // 예: base=1000 → 1s, 2s, 4s(상한 4s에서 클램프).
    private TimeSpan ComputeBackoffDelay(int attempt)
    {
        long baseMs = Math.Max(0, _opt.RetryBaseDelayMs);
        // 2^(attempt-1) — overflow 방지 위해 shift 상한(30회) 가드.
        int shift   = Math.Min(attempt - 1, 30);
        long scaled = baseMs << shift;
        long capped = Math.Min(scaled, Math.Max(baseMs, _opt.RetryMaxDelayMs));
        return TimeSpan.FromMilliseconds(capped);
    }

    // ── BaseUrl + Path 결합(슬래시 중복/누락 정규화) ──────────────────────────
    private static string CombineUrl(string baseUrl, string path)
    {
        var b = baseUrl.TrimEnd('/');
        var p = path.StartsWith('/') ? path : "/" + path;
        return b + p;
    }
}
