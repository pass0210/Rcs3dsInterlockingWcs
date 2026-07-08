using System.Threading.Channels;
using Wcs.Data;
using Wcs.Data.B2B;

namespace Wcs.Api.B2B;

// ════════════════════════════════════════════════════════════════════════════
// api_call_log 비동기 적재 — RcsApiLoggingMiddleware(경로 한정)가 TryEnqueue,
// ApiCallLogBackgroundWriter(HostedService)가 배치 DB 기록.
// 미들웨어는 요청 응답을 지연시키지 않는다(논블로킹 enqueue). 저장 실패는 로그만·드롭(가용성 우선).
// Q1(사용자 확정): /api/v1/works/ 접두만 기록. 큐 헬스체크 미이식(우리 /health 유지).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>api_call_log 비동기 큐(Bounded Channel · DropOldest · 단일 리더).</summary>
public sealed class ApiCallLogQueue
{
    private readonly Channel<ApiCallLog> _channel = Channel.CreateBounded<ApiCallLog>(
        new BoundedChannelOptions(AppConstants.ApiCallLogQueueCapacity)
        {
            FullMode     = BoundedChannelFullMode.DropOldest,
            SingleReader = true,   // 백그라운드 writer 전용
            SingleWriter = false,  // 여러 요청 스레드 동시 Enqueue
        });

    /// <summary>논블로킹 enqueue — 큐가 가득 차면 DropOldest.</summary>
    public bool TryEnqueue(ApiCallLog log) => _channel.Writer.TryWrite(log);

    /// <summary>백그라운드 writer 소비용 리더.</summary>
    public ChannelReader<ApiCallLog> Reader => _channel.Reader;

    /// <summary>채널 완료(teardown 결정적 종료 — testhost-channel-race 방어).</summary>
    public void Complete() => _channel.Writer.TryComplete();
}

/// <summary>
/// api_call_log 배치 기록 백그라운드 워커.
/// 최소 1건 블로킹 대기 후 최대 BatchSize 건 모아 일괄 SaveChanges(IServiceScopeFactory 스코프).
/// 저장 실패는 경고 로그만 남기고 드롭(로그 유실 허용 — 본 처리 무영향).
/// </summary>
public sealed class ApiCallLogBackgroundWriter : BackgroundService
{
    private readonly ApiCallLogQueue                    _queue;
    private readonly IServiceScopeFactory               _scopeFactory;
    private readonly ILogger<ApiCallLogBackgroundWriter> _log;

    public ApiCallLogBackgroundWriter(
        ApiCallLogQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<ApiCallLogBackgroundWriter> log)
    {
        _queue        = queue;
        _scopeFactory = scopeFactory;
        _log          = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var first in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                var batch = new List<ApiCallLog>(AppConstants.ApiCallLogBatchSize) { first };
                while (batch.Count < AppConstants.ApiCallLogBatchSize && _queue.Reader.TryRead(out var next))
                    batch.Add(next);

                await PersistAsync(batch, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // 정상 종료(호스트 stopping) — 잔여 드레인 시도.
        }

        // 종료 시 잔여 항목 가능한 만큼 저장(가용성 우선 · best-effort).
        var drain = new List<ApiCallLog>();
        while (_queue.Reader.TryRead(out var item))
            drain.Add(item);
        if (drain.Count > 0)
            await PersistAsync(drain, CancellationToken.None);
    }

    private async Task PersistAsync(List<ApiCallLog> batch, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            db.B2bApiCallLogs.AddRange(batch);
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) { /* 종료 경쟁 — 무시 */ }
        catch (Exception ex)
        {
            // 저장 실패는 로그만 남기고 무시(로그 유실 허용 — 본 처리 무영향·삼킴 아님).
            _log.LogWarning(ex, "[B2B api_call_log] 배치 저장 실패 — {Count}건 드롭", batch.Count);
        }
    }
}
