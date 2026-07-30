using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wcs.Api;
using Wcs.Data;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// S-TRACE-READY-PUSH-AND-DEFAULT 단위 테스트 — 신규 이벤트 7/8/9/10 발화.
//   · 이벤트 7·9(Ready 전이): TraceWiring.BuildReadyEdgeRecord 순수 매핑(1→0=7·0→1=9·그 외 null).
//   · 이벤트 8·10(IF-08 push): ChuteStatePushClient.PushAsync 전송 지점 계측(next_state 2=8·3=10),
//     DORMANT(baseUrl null) no-op, 2/3 외 값 안전 스킵, 실패 전송도 result=FAIL 로 정직 계측.
//   · 파일 sink: 신규 EventNo 도 전용 파일에 `[N] {json}` 로 raw 기입됨(제너릭 sink 확인).
//   전부 scratch/temp 또는 인프로세스 fake 사용(실경로 D:\ 무접촉·절대규칙 #7). 관측/로깅 전용·논블로킹·fail-safe.
// ════════════════════════════════════════════════════════════════════════════
public sealed class TraceReadyPushTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir;

    public TraceReadyPushTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "wcs-trace-readypush", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    // ── 캡처용 논블로킹 ITraceLogger(파일·SignalR 없이 발화 레코드만 수집) ────────
    private sealed class CapturingTraceLogger : ITraceLogger
    {
        public readonly ConcurrentQueue<TraceRecord> Records = new();
        public event Action<TraceRecord>? OnEntry;
        public void Log(TraceRecord record) { Records.Enqueue(record); OnEntry?.Invoke(record); }
    }

    private sealed class NoOpOperationLogger : IOperationLogger
    {
        public void Log(OperationLog entry) { }
        public void Log(OperationLogCategory category, string action, OperationLogLevel level = OperationLogLevel.INFO,
            int? sorterChuteNo = null, long? destinationId = null, string? barcode = null, int? pId = null,
            string? detail = null) { }
    }

    private static ChuteStatePushClient BuildClient(
        string? baseUrl, ITraceLogger trace, int retryCount = 2, int retryBaseDelayMs = 15)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(ChuteStatePushClient.HttpClientName);
        var sp = services.BuildServiceProvider();
        var httpFactory = sp.GetRequiredService<IHttpClientFactory>();

        var opts = Options.Create(new WcsOptions
        {
            ChuteStatePush = new ChuteStatePushOptions
            {
                BaseUrl          = baseUrl,
                Path             = "/api/UpdateChuteState",
                RetryCount       = retryCount,
                RetryBaseDelayMs = retryBaseDelayMs,
                RetryMaxDelayMs  = retryBaseDelayMs * 4,
                HttpTimeoutMs    = 2000,
            },
        });

        return new ChuteStatePushClient(
            httpFactory, opts, NullLogger<ChuteStatePushClient>.Instance, new NoOpOperationLogger(), trace);
    }

    private static (int nextState, string result, int attempts, string host) ParseDetail(string? detail)
    {
        Assert.NotNull(detail);
        using var doc = JsonDocument.Parse(detail!);
        var root = doc.RootElement;
        return (
            root.GetProperty("next_state").GetInt32(),
            root.GetProperty("result").GetString()!,
            root.GetProperty("attempts").GetInt32(),
            root.GetProperty("host").GetString()!);
    }

    // ════════════════════════════════════════════════════════════════════════
    // 이벤트 8·10 — IF-08 push 전송 지점 계측(next_state 분기).
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Push_NextState2_EmitsEvent8_Busy()
    {
        await using var srv = await FakeChuteStateServer.StartAsync();
        var trace  = new CapturingTraceLogger();
        var client = BuildClient(srv.BaseUrl, trace);

        bool ok = await client.PushAsync(new ChuteStatePushPayload(new[] { 30 }, new[] { 2 }));

        Assert.True(ok);
        Assert.Single(trace.Records);
        var rec = trace.Records.Single();
        Assert.Equal(8, rec.EventNo);
        Assert.Equal("CHUTESTATE_PUSH_BUSY", rec.Event);
        Assert.Equal(30, rec.ChuteNo);
        Assert.Null(rec.PId);                       // 소터 scope — 피스 상관 없음.
        Assert.Null(rec.CSeq);
        Assert.Null(rec.CellNo);
        Assert.Equal("IF08_PUSH", rec.Trigger);
        var (ns, result, attempts, host) = ParseDetail(rec.Detail);
        Assert.Equal(2, ns);
        Assert.Equal("OK", result);
        Assert.Equal(1, attempts);                  // 성공(재시도 0) → 1회차.
        Assert.Equal(srv.BaseUrl, host);
        _out.WriteLine($"[8] busy push 계측 · detail={rec.Detail}");
    }

    [Fact]
    public async Task Push_NextState3_EmitsEvent10_Ready()
    {
        await using var srv = await FakeChuteStateServer.StartAsync();
        var trace  = new CapturingTraceLogger();
        var client = BuildClient(srv.BaseUrl, trace);

        bool ok = await client.PushAsync(new ChuteStatePushPayload(new[] { 42 }, new[] { 3 }));

        Assert.True(ok);
        var rec = trace.Records.Single();
        Assert.Equal(10, rec.EventNo);
        Assert.Equal("CHUTESTATE_PUSH_READY", rec.Event);
        Assert.Equal(42, rec.ChuteNo);
        var (ns, result, _, _) = ParseDetail(rec.Detail);
        Assert.Equal(3, ns);
        Assert.Equal("OK", result);
        _out.WriteLine($"[10] ready push 계측 · detail={rec.Detail}");
    }

    [Fact]
    public async Task Push_Dormant_NullBaseUrl_NoTraceEvent()
    {
        var trace  = new CapturingTraceLogger();
        var client = BuildClient(baseUrl: null, trace);   // DORMANT — 층 호스트 미설정.

        bool ok = await client.PushAsync(new ChuteStatePushPayload(new[] { 30 }, new[] { 2 }));

        Assert.False(ok);                           // 미발신(false).
        Assert.Empty(trace.Records);                // PUT 전송 0 → 이벤트 8/10 미발화(no-op).
        _out.WriteLine("[dormant] baseUrl null → PUT 0 · 트레이스 발화 0");
    }

    [Fact]
    public async Task Push_FailureExhausted_EmitsEvent8_WithFailResult()
    {
        await using var srv = await FakeChuteStateServer.StartAsync();
        srv.SetMode(ChuteStateRespMode.Reject503);   // 항상 503 → 재시도 소진.
        var trace  = new CapturingTraceLogger();
        var client = BuildClient(srv.BaseUrl, trace, retryCount: 2);

        bool ok = await client.PushAsync(new ChuteStatePushPayload(new[] { 30 }, new[] { 2 }));

        Assert.False(ok);                            // 재시도 소진 후 실패.
        var rec = trace.Records.Single();            // 실패 전송도 정확히 1건 계측(대체 아님·additive).
        Assert.Equal(8, rec.EventNo);
        var (ns, result, attempts, _) = ParseDetail(rec.Detail);
        Assert.Equal(2, ns);
        Assert.Equal("FAIL", result);                // 결과 정직 기록.
        Assert.Equal(3, attempts);                   // 1 + retryCount(2) = 3회 소진.
        _out.WriteLine($"[8·fail] 실패 전송 계측 · detail={rec.Detail}");
    }

    [Fact]
    public async Task Push_NextStateOutOfRange_NoTraceEvent_SafeSkip()
    {
        await using var srv = await FakeChuteStateServer.StartAsync();
        var trace  = new CapturingTraceLogger();
        var client = BuildClient(srv.BaseUrl, trace);

        // 2/3 외 값(이론상) — 매핑 EventNo 없음 → 안전 스킵(발화 0). push 자체는 정상 진행(본 동작 무영향).
        bool ok = await client.PushAsync(new ChuteStatePushPayload(new[] { 30 }, new[] { 5 }));

        Assert.True(ok);
        Assert.Empty(trace.Records);
        _out.WriteLine("[safe] next_state=5 → 트레이스 발화 0(push 본 동작은 정상)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // 이벤트 7·9 — Ready 워드 전이 매핑(순수 함수 — I/O 무의존·결정적).
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ReadyEdge_1to0_IsEvent7_WithFloorAndDetail()
    {
        var rec = TraceWiring.BuildReadyEdgeRecord(chuteNo: 30, destId: 7, oldV: 1, newV: 0, curFloor: 2);
        Assert.NotNull(rec);
        Assert.Equal(7, rec!.EventNo);
        Assert.Equal("READY_1TO0", rec.Event);
        Assert.Equal(30, rec.ChuteNo);
        Assert.Equal(7, rec.DestId);
        Assert.Equal(2, rec.Floor);                  // 전이 관측 시점 CurFloor.
        Assert.Null(rec.PId);                        // 소터 scope — 피스 상관 없음.
        Assert.Null(rec.CSeq);
        Assert.Null(rec.CellNo);
        Assert.Equal("READY_EDGE", rec.Trigger);
        using var doc = JsonDocument.Parse(rec.Detail!);
        Assert.Equal("Ready", doc.RootElement.GetProperty("reg").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("old").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("new").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("curFloor").GetInt32());
        _out.WriteLine($"[7] Ready 1→0 · detail={rec.Detail}");
    }

    [Fact]
    public void ReadyEdge_0to1_IsEvent9()
    {
        var rec = TraceWiring.BuildReadyEdgeRecord(chuteNo: 30, destId: 7, oldV: 0, newV: 1, curFloor: 1);
        Assert.NotNull(rec);
        Assert.Equal(9, rec!.EventNo);
        Assert.Equal("READY_0TO1", rec.Event);
        Assert.Equal(1, rec.Floor);
        using var doc = JsonDocument.Parse(rec.Detail!);
        Assert.Equal(0, doc.RootElement.GetProperty("old").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("new").GetInt32());
        _out.WriteLine($"[9] Ready 0→1 · detail={rec.Detail}");
    }

    [Theory]
    [InlineData(1, 1)]   // 무변화(방어) — Ready 델타는 0/1 에지만.
    [InlineData(0, 0)]
    public void ReadyEdge_NonEdge_IsNull(int oldV, int newV)
    {
        Assert.Null(TraceWiring.BuildReadyEdgeRecord(30, 7, oldV, newV, 2));
    }

    // ════════════════════════════════════════════════════════════════════════
    // 파일 sink — 신규 EventNo(7·8·9·10)도 전용 파일에 `[N] {json}` raw 기입(제너릭 sink).
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Sink_WritesNewEventNumberedLines_AndBacklogReadsThemBack()
    {
        await using var svc = new TraceLogService(
            Options.Create(new TraceLogOptions
            {
                Enabled = true, Directory = _dir, FileNamePattern = "trace-.log", RollingInterval = "Day",
                FileSizeLimitBytes = 104_857_600, RetainedFileCountLimit = 30,
                BacklogTakeDefault = 100, BacklogTakeMax = 500,
            }),
            NullLogger<TraceLogService>.Instance);
        await svc.StartAsync(CancellationToken.None);

        svc.Log(new TraceRecord(7, "READY_1TO0", DateTimeOffset.Now, null, null, 30, 7, null, 2, null,
            "READY_EDGE", "{\"reg\":\"Ready\",\"old\":1,\"new\":0,\"curFloor\":2}"));
        svc.Log(new TraceRecord(8, "CHUTESTATE_PUSH_BUSY", DateTimeOffset.Now, null, null, 30, null, null, null, null,
            "IF08_PUSH", "{\"next_state\":2,\"result\":\"OK\",\"attempts\":1,\"host\":\"http://rcs\"}"));
        svc.Log(new TraceRecord(9, "READY_0TO1", DateTimeOffset.Now, null, null, 30, 7, null, 1, null,
            "READY_EDGE", "{\"reg\":\"Ready\",\"old\":0,\"new\":1,\"curFloor\":1}"));
        svc.Log(new TraceRecord(10, "CHUTESTATE_PUSH_READY", DateTimeOffset.Now, null, null, 30, null, null, null, null,
            "IF08_PUSH", "{\"next_state\":3,\"result\":\"OK\",\"attempts\":1,\"host\":\"http://rcs\"}"));

        await WaitUntilAsync(() => svc.Read(500, null, null, null).Count >= 4, 5000, "신규 4개 이벤트 기입");

        var all = svc.Read(500, null, null, null);
        Assert.Equal(new[] { 7, 8, 9, 10 }, all.Select(r => r.EventNo).ToArray());

        // 앞머리 [N] 태그 raw 파일 확인(fresh evidence). Detail 은 레코드 내 문자열 필드라 파일 줄에선
        // JSON 문자열로 이스케이프됨(\"reg\"…) → 원문 검증은 백로그 파싱 레코드(round-trip)로 확인.
        var file  = Directory.GetFiles(_dir, "trace-*.log").Single();
        var lines = ReadSharedLines(file);
        Assert.Equal(4, lines.Length);
        Assert.StartsWith("[7] ",  lines[0]);
        Assert.StartsWith("[8] ",  lines[1]);
        Assert.StartsWith("[9] ",  lines[2]);
        Assert.StartsWith("[10] ", lines[3]);
        Assert.Contains("\"reg\":\"Ready\"",  all[0].Detail);
        Assert.Contains("\"next_state\":2",   all[1].Detail);

        // eventNo 필터(신규 번호)로 조회 가능.
        Assert.Single(svc.Read(500, eventNo: 8, null, null));
        Assert.Single(svc.Read(500, eventNo: 10, null, null));
        _out.WriteLine("[sink] 신규 7·8·9·10 파일 raw 태그 + eventNo 필터 확인");
    }

    private static string[] ReadSharedLines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        var list = new List<string>();
        string? line;
        while ((line = sr.ReadLine()) is not null)
            if (line.Length > 0) list.Add(line);
        return list.ToArray();
    }

    private static async Task WaitUntilAsync(Func<bool> cond, int timeoutMs, string msg, int pollMs = 25)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        while (!cond())
        {
            if (DateTimeOffset.Now > deadline) Assert.Fail($"WaitUntil 타임아웃({timeoutMs}ms): {msg}");
            await Task.Delay(pollMs);
        }
    }
}
