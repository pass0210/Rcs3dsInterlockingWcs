using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Wcs.Data;

namespace Wcs.Api;

// ════════════════════════════════════════════════════════════════════════════
// OperationLogService — operation_log 비동기·단일경로·fail-safe DB 싱크 (S-OBSERVABILITY).
//
// IOperationLogger(즉시 반환 enqueue) + IHostedService(백그라운드 컨슈머)를 한 싱글톤으로 구현.
//
// 설계(계약 §2 D·§6 성능 가드):
//   · 본 처리 비지연: Log()는 unbounded Channel에 TryWrite만(즉시 반환). 동기 EF SaveChanges를
//     폴 루프/핸드셰이크/HTTP 핫패스에서 호출하지 않는다.
//   · 단일 경로: 모든 operation_log INSERT는 이 컨슈머 한 곳에서만 나간다(쓰기 직렬화).
//   · 배치: 컨슈머가 채널에서 가능한 만큼 드레인해 한 스코프(WcsDbContext)에서 AddRange+SaveChanges.
//   · fail-safe: enqueue·기록 실패가 본 동작을 막지 않는다. 단 예외를 삼키지 않고 ILogger(Serilog)로
//     자체 경고(절대규칙 Fail Loud). 실패한 배치는 드롭(본 도메인 이벤트는 piece_event 등이 별도 보유).
//   · DB 컨텍스트 수명: IServiceScopeFactory(싱글톤)로 배치마다 스코프 생성(기존 패턴 — captive 회피).
//
// 테스트 더블 무해: in-memory SQLite 테스트 팩토리에서도 동일하게 동작(Append만·블로킹 0).
// operation_log 테이블은 EnsureCreated로 생성되므로 기록 가능. 기록 실패해도 테스트 단언 무영향.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// operation_log 비동기 백그라운드 싱크 — IOperationLogger + IHostedService.
/// Log()는 채널 enqueue(논블로킹), 컨슈머가 별도 스코프로 배치 영속화.
/// </summary>
public sealed class OperationLogService : IOperationLogger, IHostedService, IAsyncDisposable
{
    // 한 배치 최대 드레인 수(과도한 메모리 점유·트랜잭션 비대 방지).
    private const int MaxBatch = 256;

    private readonly Channel<OperationLog> _ch = Channel.CreateUnbounded<OperationLog>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly IServiceScopeFactory             _scopeFactory;
    private readonly ILogger<OperationLogService>     _log;

    private CancellationTokenSource? _cts;
    private Task?                    _consumeTask;
    private int                      _stopped;   // 멱등 StopAsync(Interlocked)

    // ── F2 실시간 테일 스트림 훅 (S-FRONTEND-F2) ─────────────────────────────────
    // 단일 컨슈머가 각 엔트리를 발화 → MonitorRelayService가 SignalR로 브로드캐스트.
    // DB 영속화와 별개 경로: 발화는 SaveChanges 이전에 하므로 "기록 실패가 스트림을 막지 않고",
    // 핸들러는 relay가 fire-and-forget·예외 격리하므로 "스트림 실패가 기록을 막지 않는다".
    // 이 이벤트는 배치·teardown·fail-safe 동작을 바꾸지 않는다(브로드캐스트 얹기 전용).
    public event Action<OperationLog>? OnEntry;

    public OperationLogService(
        IServiceScopeFactory         scopeFactory,
        ILogger<OperationLogService> log)
    {
        _scopeFactory = scopeFactory;
        _log          = log;
    }

    // ── IOperationLogger ──────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Log(OperationLog entry)
    {
        if (entry.At == default)
            entry.At = DateTime.UtcNow;

        // TryWrite는 unbounded 채널에서 (닫히지 않은 한) 항상 성공·논블로킹. 실패해도 예외 0(fail-safe).
        if (!_ch.Writer.TryWrite(entry))
        {
            // 채널이 완료(종료 중)된 경우 — 드롭. 종료 경쟁이므로 Debug 레벨(소음 억제).
            try { _log.LogDebug("[operation_log] enqueue 실패(채널 종료) — 드롭: {Category}/{Action}", entry.Category, entry.Action); }
            catch { /* 종료 중 로거 disposed */ }
        }
    }

    /// <inheritdoc/>
    public void Log(
        OperationLogCategory category,
        string               action,
        OperationLogLevel    level         = OperationLogLevel.INFO,
        int?                 sorterChuteNo = null,
        long?                destinationId = null,
        string?              barcode       = null,
        int?                 pId           = null,
        string?              detail        = null)
        => Log(new OperationLog
        {
            At            = DateTime.UtcNow,
            Category      = category,
            Action        = action,
            Level         = level,
            SorterChuteNo = sorterChuteNo,
            DestinationId = destinationId,
            Barcode       = barcode,
            PId           = pId,
            Detail        = detail,
        });

    // ── IHostedService ───────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts         = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _consumeTask = Task.Run(() => RunConsumeLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;

        // 채널을 완료시켜 컨슈머가 잔여 배치를 드레인 후 결정적으로 종료하게 한다
        // (PlcWriteQueue teardown 교훈 — 빈 채널 취소 경쟁 회피).
        _ch.Writer.TryComplete();

        if (_consumeTask is not null)
        {
            try { await _consumeTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception) { }  // teardown 경쟁 예외 흡수
        }

        if (_cts is not null)
        {
            try { await _cts.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _cts?.Dispose();
    }

    // ── 백그라운드 컨슈머 루프 — 드레인 후 배치 영속화 ─────────────────────────

    private async Task RunConsumeLoopAsync(CancellationToken ct)
    {
        var reader = _ch.Reader;
        var buffer = new List<OperationLog>(MaxBatch);

        try
        {
            // 채널 완료(StopAsync) 또는 취소 시까지 대기→드레인.
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                buffer.Clear();
                while (buffer.Count < MaxBatch && reader.TryRead(out var item))
                    buffer.Add(item);

                if (buffer.Count > 0)
                {
                    EmitToObservers(buffer);  // SaveChanges 이전 발화(기록 실패와 독립).
                    await FlushBatchAsync(buffer, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { /* 종료 — 정상 */ }
        catch (Exception ex)
        {
            // WaitToReadAsync 등 예외 — 루프 종료(컨슈머 사망). 본 동작에는 영향 0(enqueue는 계속 동작·드롭).
            try { _log.LogError(ex, "[operation_log] 컨슈머 루프 예외 — 종료(이후 기록 드롭)"); }
            catch { }
        }

        // 채널 완료 후 잔여분 드레인(StopAsync 경로 — 종료 전 마지막 배치 보장).
        try
        {
            buffer.Clear();
            while (buffer.Count < MaxBatch && reader.TryRead(out var item))
                buffer.Add(item);
            if (buffer.Count > 0)
            {
                EmitToObservers(buffer);
                await FlushBatchAsync(buffer, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch { /* 종료 경쟁 — 무시(fail-safe) */ }
    }

    // 실시간 테일 옵저버 발화 — 핸들러 예외를 삼켜(fail-safe) 컨슈머 루프·영속화를 막지 않는다.
    private void EmitToObservers(List<OperationLog> batch)
    {
        var h = OnEntry;
        if (h is null) return;
        foreach (var e in batch)
        {
            try { h(e); }
            catch { /* relay 핸들러 예외 격리 — 스트림 실패가 기록/컨슈머를 막지 않음 */ }
        }
    }

    private async Task FlushBatchAsync(List<OperationLog> batch, CancellationToken ct)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            db.OperationLogs.AddRange(batch);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // DB 다운·연결 끊김 등 — 본 동작(PLC/배정/핸드셰이크/API)을 막지 않는다(fail-safe).
            // 단 예외를 삼키지 않고 자체 경고(절대규칙 Fail Loud). 실패 배치는 드롭(관측 스트림 — 도메인 이벤트는 별도 보유).
            try { _log.LogWarning(ex, "[operation_log] 배치 영속화 실패({Count}건) — 드롭(본 동작 비차단)", batch.Count); }
            catch { }
        }
    }
}
