using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Wcs.Data.B2B;

namespace Wcs.Api.B2B;

// ════════════════════════════════════════════════════════════════════════════
// RcsApiLoggingMiddleware — B2B RCS API 호출 원문 감사(api_call_log).
//
// Q1(사용자 확정): 경로를 /api/v1/works/ 로 한정 — 기존 /api/v1/ RCS 엔드포인트
//   (destination-query·arrival-report·deposit-report)는 미기록(무접촉). 그 외 경로는 즉시 통과.
// 미들웨어는 큐에 논블로킹 enqueue 만 — DB 쓰기는 백그라운드 writer(요청 응답 비지연).
// 응답 본문은 JSON/텍스트만 캡처(바이너리/스트리밍 안전) · 4000자 truncate. 민감 키 마스킹.
// ════════════════════════════════════════════════════════════════════════════

public sealed class RcsApiLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ApiCallLogQueue _queue;

    public RcsApiLoggingMiddleware(RequestDelegate next, ApiCallLogQueue queue)
    {
        _next  = next;
        _queue = queue;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // ── 경로 한정: /api/v1/works/ 접두만 기록. 그 외는 원본 파이프라인 그대로 통과(무접촉). ──
        if (!context.Request.Path.StartsWithSegments(
                AppConstants.WorksRoutePrefix, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var sw          = Stopwatch.StartNew();
        var requestBody = await ReadRequestBodyAsync(context.Request);

        // 응답 본문 캡처를 위해 임시 스트림으로 교체(이 경로 한정 — 기존 엔드포인트 무영향).
        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context);
        }
        catch (Exception ex)
        {
            sw.Stop();
            context.Response.Body = originalBody;   // 스트림 원복 후 재던짐(기본 처리에 위임)
            Enqueue(context, requestBody, responseBody: null, sw, ex.Message);
            throw;
        }

        // 응답 본문 조건부 캡처(JSON/텍스트만) 후 원본 스트림으로 복사.
        buffer.Position = 0;
        string? responseBody = null;
        if (IsCapturable(context.Response.ContentType))
        {
            using var reader = new StreamReader(buffer, Encoding.UTF8, false, leaveOpen: true);
            responseBody = await reader.ReadToEndAsync();
            buffer.Position = 0;
        }
        await buffer.CopyToAsync(originalBody);
        context.Response.Body = originalBody;

        sw.Stop();
        Enqueue(context, requestBody, responseBody, sw, null);
    }

    // ── 큐 적재(논블로킹 · fail-safe) ────────────────────────────────────────────
    // 감사 로깅(Mask/ExtractStatus/Truncate/enqueue)의 어떤 예외도 본 API 응답을 방해하지 않는다.
    // 관측 훅 fail-safe 원칙 — 예외는 무시(로그 유실 허용 · 응답 경로 절대 불간섭).
    private void Enqueue(HttpContext ctx, string? requestBody, string? responseBody,
        Stopwatch sw, string? errorMessage)
    {
        try
        {
            _queue.TryEnqueue(new ApiCallLog
            {
                Endpoint       = ctx.Request.Path.Value ?? string.Empty,
                HttpMethod     = ctx.Request.Method,
                RequestBody    = Mask(requestBody),
                ResponseStatus = ExtractStatus(responseBody),
                ResponseBody   = Truncate(Mask(responseBody), AppConstants.LogTruncateDbLength),
                HttpStatusCode = ctx.Response.StatusCode,
                DurationMs     = sw.ElapsedMilliseconds,
                ClientIp       = ctx.Connection.RemoteIpAddress?.ToString(),
                ErrorMessage   = Truncate(errorMessage, 500),
                CalledAt       = DateTime.Now,   // B2B 로컬타임
            });
        }
        catch
        {
            // 감사 로깅 예외는 삼켜 본 응답을 보존한다(fail-safe · 관측 훅 격리).
        }
    }

    // ── 요청 본문 읽기(EnableBuffering 후 되감기 — 컨트롤러가 재독) ────────────────
    private static async Task<string?> ReadRequestBodyAsync(HttpRequest request)
    {
        request.EnableBuffering();
        if (request.ContentLength is null or 0)
            return null;

        request.Body.Position = 0;
        using var reader = new StreamReader(
            request.Body, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        var body = await reader.ReadToEndAsync();
        request.Body.Position = 0;   // 컨트롤러 모델 바인딩이 다시 읽도록 되감기
        return body;
    }

    // ── 응답 status("S"/"F") 추출(unprocessed 최상위 배열 등은 null) ───────────────
    private static string? ExtractStatus(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody)) return null;
        try
        {
            using var doc = JsonDocument.Parse(responseBody);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("status", out var s) &&
                s.ValueKind == JsonValueKind.String)
                return s.GetString();
        }
        catch (JsonException) { /* 파싱 불가(배열·비JSON) → null */ }
        return null;
    }

    private static bool IsCapturable(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType)) return false;
        return contentType.Contains("application/json", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("application/problem+json", StringComparison.OrdinalIgnoreCase)
            || contentType.Contains("text/", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Truncate(string? s, int max) =>
        s is null ? null : (s.Length <= max ? s : s[..max]);

    // ── 민감 키 마스킹(로그 기록 전용 — 컨트롤러엔 원본 전달) ────────────────────────
    private static readonly Regex SensitiveKey = new(
        "\"(password|pwd|passwd|token|accessToken|refreshToken|authorization|apiKey|api_key|secret|ssn|residentNo|rrn)\"\\s*:\\s*\"[^\"]*\"",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static string? Mask(string? body)
    {
        if (string.IsNullOrEmpty(body)) return body;
        return SensitiveKey.Replace(body, m =>
        {
            var key = m.Value[..(m.Value.IndexOf(':') )];
            return $"{key}:\"***\"";
        });
    }
}
