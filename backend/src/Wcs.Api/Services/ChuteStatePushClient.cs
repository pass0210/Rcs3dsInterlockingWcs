using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Wcs.Data;

namespace Wcs.Api;

// ════════════════════════════════════════════════════════════════════════════
// ChuteStatePushClient — 목적지 수용상태 아웃바운드 푸시 클라이언트 (WCS → RCS)
//
// S-IF08-READY-PUSH (확정 와이어 단일 채널):
//   목적지 수용상태 전이 시 PUT {ChuteStatePush:BaseUrl}{Path}로
//   {chute_numbers:[...], next_states:[...]}(snake_case)를 RCS로 푸시(3=수용가능/2=불가).
//   - IHttpClientFactory 경유(직접 new HttpClient() 금지 — 소켓 고갈·DNS 갱신 방지).
//   - HTTP 메서드 = PUT(RCS 계약 UpdateChuteState).
//   - 재시도: 설정값 경유 지수 백오프(고정 sleep 금지). 소진 후 false 반환(실패 명시).
//   - 예외 삼킴 금지(Fail-Loud): 최종 실패는 명시 ERROR 로깅 + bool 반환으로 호출자에 전달.
//   - BaseUrl 미설정(DORMANT) 시 HTTP 시도 0·즉시 false(미발신).
//
// 이 클라이언트는 "1건 전송 + 재시도"만 책임진다. "전이 감지·전이당 트리거·복구 재푸시"는
// DestinationStatusPusher가 책임(관심사 분리).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// UpdateChuteState 푸시 페이로드 — {chute_numbers, next_states}.
/// ★ 와이어는 snake_case(RCS 계약) — STJ 기본 camelCase에 의존하지 말고 [JsonPropertyName] 명시.
/// 두 배열은 동일 길이·인덱스 정렬(chute_numbers[i] ↔ next_states[i]). 전이당 길이-1 단건.
/// </summary>
public sealed record ChuteStatePushPayload(
    [property: JsonPropertyName("chute_numbers")] int[] ChuteNumbers,
    [property: JsonPropertyName("next_states")]   int[] NextStates);

/// <summary>
/// 고객 슈트 상태 아웃바운드 푸시 클라이언트 인터페이스.
/// ChuteStatePusher가 전이 감지 후 이 클라이언트로 1건 전송한다.
/// </summary>
public interface IChuteStatePushClient
{
    /// <summary>푸시 서브시스템이 활성인지(층 호스트 or 레거시 BaseUrl 설정 시 true — 둘 다 미설정이면 DORMANT).</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// UpdateChuteState 1건을 지정 <paramref name="baseUrl"/>(층 호스트)로 PUT(재시도 포함).
    /// 층은 payload에 유입되지 않고 **어느 호스트로 보내느냐**로만 전달된다(층별 라우팅은 호출자 책임).
    /// 성공(2xx + flag==1) 시 true, 재시도 소진 후 실패 시 false. baseUrl 미지정(null/공백)이면 즉시 false(미발신).
    /// 예외를 던지지 않는다(취소 제외 — false로 수렴).
    /// </summary>
    Task<bool> PushAsync(ChuteStatePushPayload payload, string? baseUrl, CancellationToken ct = default);

    /// <summary>
    /// [레거시 편의] 설정된 BaseUrl로 PUT(재시도 포함). 층별 라우팅을 쓰지 않는 호출부·직접 테스트용.
    /// 내부적으로 <see cref="PushAsync(ChuteStatePushPayload, string?, CancellationToken)"/>에 위임.
    /// </summary>
    Task<bool> PushAsync(ChuteStatePushPayload payload, CancellationToken ct = default);
}

/// <summary>
/// IChuteStatePushClient 구현 — IHttpClientFactory 기반 named client + 설정 경유 지수 백오프 재시도.
/// </summary>
public sealed class ChuteStatePushClient : IChuteStatePushClient
{
    /// <summary>IHttpClientFactory 등록 시 사용하는 named client 이름.</summary>
    public const string HttpClientName = "ChuteStatePush";

    /// <summary>operation_log action 태그(성공/실패 전수 부수 기록).</summary>
    private const string OpLogAction = "CHUTESTATE_PUSH";

    private readonly IHttpClientFactory            _httpFactory;
    private readonly ChuteStatePushOptions         _opt;
    private readonly ILogger<ChuteStatePushClient> _log;
    private readonly IOperationLogger              _opLog;

    public ChuteStatePushClient(
        IHttpClientFactory            httpFactory,
        IOptions<WcsOptions>          options,
        ILogger<ChuteStatePushClient> log,
        IOperationLogger              opLog)
    {
        _httpFactory = httpFactory;
        _opt         = options.Value.ChuteStatePush;
        _log         = log;
        _opLog       = opLog;
    }

    /// <inheritdoc/>
    public bool IsEnabled => _opt.IsEnabled;

    /// <inheritdoc/>
    public Task<bool> PushAsync(ChuteStatePushPayload payload, CancellationToken ct = default)
        => PushAsync(payload, _opt.BaseUrl, ct);   // 레거시 편의 — 설정된 BaseUrl로 위임.

    /// <inheritdoc/>
    public async Task<bool> PushAsync(ChuteStatePushPayload payload, string? baseUrl, CancellationToken ct = default)
    {
        // DORMANT/미라우팅: 호스트 미지정(층 미설정 or BaseUrl 미설정)이면 no-op. 호출자(Pusher)가 이미
        // 층별 DORMANT로 걸러내지만 방어적으로 한 번 더 — "성공"으로 간주하지 않고 false 반환(미발신 유지).
        if (string.IsNullOrWhiteSpace(baseUrl))
            return false;

        // 절대규칙 #7: 엔드포인트는 (층)호스트 + Path 설정 조합(하드코딩 0 — 호스트는 설정값이 주입).
        var url = CombineUrl(baseUrl, _opt.Path);

        // 총 시도 = 1(최초) + RetryCount(재시도). 기본 3회 지수 백오프.
        int maxAttempts = 1 + Math.Max(0, _opt.RetryCount);

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                // IHttpClientFactory 경유 — 직접 new HttpClient() 금지(소켓 고갈 방지).
                var http = _httpFactory.CreateClient(HttpClientName);

                // ★ PUT(고객 계약). snake_case body는 payload의 [JsonPropertyName]가 강제.
                using var resp = await http.PutAsJsonAsync(url, payload, ct).ConfigureAwait(false);

                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                // 성공 판정 = 2xx + flag==1(계약). {result:"Failed"}·flag≠1·비2xx는 모두 실패(재시도).
                if (resp.IsSuccessStatusCode && IsSuccessBody(body))
                {
                    if (attempt > 1)
                        _log.LogInformation(
                            "[CHUTESTATE푸시] 성공(재시도 {Attempt}회차): chute_numbers=[{Chutes}] next_states=[{States}]",
                            attempt, Join(payload.ChuteNumbers), Join(payload.NextStates));
                    // operation_log: 아웃바운드 푸시 전수(성공) — 부수 기록.
                    _opLog.Log(OperationLogCategory.API, OpLogAction,
                        detail: DetailJson(payload, "OK", attempt));
                    return true;
                }

                // 비2xx 또는 실패 body({result:"Failed"}/flag≠1) → 재시도 경로.
                _log.LogWarning(
                    "[CHUTESTATE푸시] 실패 응답(시도 {Attempt}/{Max}): status={Status} chute_numbers=[{Chutes}] next_states=[{States}]",
                    attempt, maxAttempts, (int)resp.StatusCode, Join(payload.ChuteNumbers), Join(payload.NextStates));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // 호스트 종료/푸셔 정지 — 재시도 중단(취소 전파). 미발신 유지.
                throw;
            }
            catch (Exception ex)
            {
                // 연결 거부·타임아웃·DNS 실패 등 — 재시도 경로. 예외 삼키지 않고 경고 로깅(Fail-Loud).
                _log.LogWarning(ex,
                    "[CHUTESTATE푸시] 전송 예외(시도 {Attempt}/{Max}): chute_numbers=[{Chutes}] next_states=[{States}]",
                    attempt, maxAttempts, Join(payload.ChuteNumbers), Join(payload.NextStates));
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

        // 재시도 소진 — 최종 실패 명시 로깅(Fail-Loud, 층 호스트별 독립). false 반환 → 조용한 드롭 금지.
        _log.LogError(
            "[CHUTESTATE푸시] 재시도 소진({Max}회) — 호스트 {Url} 미도달: chute_numbers=[{Chutes}] next_states=[{States}]. " +
            "다음 전이/관찰 시 재발신(해당 층 호스트만 — 타 층 발신엔 영향 없음).",
            maxAttempts, url, Join(payload.ChuteNumbers), Join(payload.NextStates));
        // operation_log: 아웃바운드 푸시 전수(실패) — 부수 기록(WARN).
        _opLog.Log(OperationLogCategory.API, OpLogAction, level: OperationLogLevel.WARN,
            detail: DetailJson(payload, "FAIL", maxAttempts));
        return false;
    }

    // ── 응답 body 성공 판정: {result:"Failed"}=실패, flag==1=성공, 그 외=실패 ──────
    private static bool IsSuccessBody(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            // {result:"Failed"} → 처리 실패(계약).
            if (root.TryGetProperty("result", out var resultEl)
                && resultEl.ValueKind == JsonValueKind.String
                && string.Equals(resultEl.GetString(), "Failed", StringComparison.OrdinalIgnoreCase))
                return false;

            // flag==1 → 처리 성공(계약).
            return root.TryGetProperty("flag", out var flagEl)
                   && flagEl.ValueKind == JsonValueKind.Number
                   && flagEl.TryGetInt32(out var flag)
                   && flag == 1;
        }
        catch (JsonException)
        {
            // 파싱 불가 = 성공 확인 불가 → 실패 처리(재시도). 성공 위장 금지(Fail-Loud).
            return false;
        }
    }

    // ── 지수 백오프 지연 산출 (설정값 경유 — 고정 sleep 0) ─────────────────────
    // attempt n회차(1-기반) 실패 후 지연 = RetryBaseDelayMs × 2^(n-1), 상한 RetryMaxDelayMs.
    private TimeSpan ComputeBackoffDelay(int attempt)
    {
        long baseMs = Math.Max(0, _opt.RetryBaseDelayMs);
        int shift   = Math.Min(attempt - 1, 30);   // overflow 방지 shift 가드.
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

    private static string Join(int[] xs) => string.Join(",", xs);

    private static string DetailJson(ChuteStatePushPayload p, string result, int attempts) =>
        $"{{\"chute_numbers\":[{Join(p.ChuteNumbers)}],\"next_states\":[{Join(p.NextStates)}],\"result\":\"{result}\",\"attempts\":{attempts}}}";
}
