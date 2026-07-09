using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wcs.Api;
using Wcs.Core;
using Wcs.Data;
using Wcs.PlcGateway;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// S-CHUTESTATE-PUSH — 고객 슈트 상태 아웃바운드 푸시 검증 (WCS → 고객 PUT /api/UpdateChuteState)
//
// 가짜 고객 수신 HTTP 서버(FakeChuteStateServer)로 WCS가 보낸 UpdateChuteState 푸시를
// 실제로 수신·카운트·payload 검증한다. 인메모리 GREEN을 PASS 근거로 삼지 않고
// "가짜 고객 서버가 수신한 실제 JSON 본문"으로 입증(RcsPushTests 메타교훈 미러).
//
// 검증 시나리오(계약 Completion Conditions):
//   CS-PUSH-1  PAUSE 전이(CHUTE·SORTER) → {chute_numbers:[ChuteNo], next_states:[2]} 1건
//   CS-PUSH-2  RESUME 전이 → {chute_numbers:[ChuteNo], next_states:[3]} 1건
//   CS-PUSH-3  scope 게이트: FULL·O6·AlreadyInState → 발신 0(전이만 푸시), 실제 pause는 1건
//   CS-PUSH-4  성공 응답: 200 {flag:1} → 성공(재시도 없음)
//   CS-PUSH-5  실패 처리: 비2xx/{result:"Failed"}/flag≠1 → 재시도 후 false(Fail-Loud), 복구 후 도달
//   CS-PUSH-6  DORMANT: BaseUrl null → 크래시 0·수신 0·인바운드 정상
//   CS-PUSH-7  payload 정합: 키가 정확히 chute_numbers·next_states(camelCase 아님)·PUT·인덱스 정렬
// ════════════════════════════════════════════════════════════════════════════

/// <summary>가짜 고객 서버 응답 모드 — 성공/거부/실패body/flag0 토글.</summary>
public enum ChuteStateRespMode { Success, Reject503, FailBody400, FlagZero200 }

// ── 가짜 고객 수신 서버 ──────────────────────────────────────────────────────

/// <summary>
/// 가짜 고객 수신 HTTP 서버 — PUT /api/UpdateChuteState 수신·기록.
/// Kestrel(동적 포트)로 in-process 기동. 모든 HTTP 메서드를 매칭해 **실제 메서드**를 기록하고
/// (PUT 여부 positive 검증), 응답 모드 토글로 성공/실패/복구를 시뮬레이션한다.
/// </summary>
public sealed class FakeChuteStateServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<ReceivedPut> _received = new();
    private volatile ChuteStateRespMode _mode = ChuteStateRespMode.Success;

    /// <summary>수신 1건 — HTTP 메서드 + 파싱된 배열 + 원문.</summary>
    public sealed record ReceivedPut(string Method, int[] ChuteNumbers, int[] NextStates, string RawBody);

    public string BaseUrl { get; }

    private FakeChuteStateServer(WebApplication app, string baseUrl)
    {
        _app    = app;
        BaseUrl = baseUrl;
    }

    public static async Task<FakeChuteStateServer> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");   // 동적 포트
        builder.Logging.ClearProviders();
        var app = builder.Build();

        FakeChuteStateServer? self = null;

        // app.Map(패턴, 핸들러)는 모든 HTTP 메서드를 매칭 — 메서드를 기록해 PUT positive 검증.
        app.Map("/api/UpdateChuteState", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var raw    = await reader.ReadToEndAsync();
            var method = ctx.Request.Method;

            int[] chutes = Array.Empty<int>();
            int[] states = Array.Empty<int>();
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (root.TryGetProperty("chute_numbers", out var cEl) && cEl.ValueKind == JsonValueKind.Array)
                    chutes = cEl.EnumerateArray().Select(x => x.GetInt32()).ToArray();
                if (root.TryGetProperty("next_states", out var sEl) && sEl.ValueKind == JsonValueKind.Array)
                    states = sEl.EnumerateArray().Select(x => x.GetInt32()).ToArray();
            }
            catch { /* 파싱 실패도 원문은 기록(디버깅) */ }

            // 매 시도 기록(성공/거부 무관) — 재시도 횟수 관측용.
            self!.Record(new ReceivedPut(method, chutes, states, raw));

            switch (self.Mode)
            {
                case ChuteStateRespMode.Reject503:
                    ctx.Response.StatusCode = 503;
                    await ctx.Response.WriteAsync("rejecting");
                    return;
                case ChuteStateRespMode.FailBody400:
                    ctx.Response.StatusCode = 400;
                    await ctx.Response.WriteAsJsonAsync(new { result = "Failed" });
                    return;
                case ChuteStateRespMode.FlagZero200:
                    ctx.Response.StatusCode = 200;
                    await ctx.Response.WriteAsJsonAsync(new { flag = 0 });
                    return;
                default:  // Success
                    ctx.Response.StatusCode = 200;
                    await ctx.Response.WriteAsJsonAsync(new
                    {
                        flag   = 1,
                        result = new[]
                        {
                            new { status = 0, msg = "", chute_id = chutes.FirstOrDefault(), last_changed = 1719999999000L },
                        },
                    });
                    return;
            }
        });

        await app.StartAsync();

        var server  = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
        var feature = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!;
        var address = feature.Addresses.First();

        self = new FakeChuteStateServer(app, address);
        return self;
    }

    public ChuteStateRespMode Mode => _mode;
    public void SetMode(ChuteStateRespMode m) => _mode = m;

    private void Record(ReceivedPut put) => _received.Enqueue(put);

    /// <summary>전체 수신(순서 보존).</summary>
    public IReadOnlyList<ReceivedPut> All => _received.ToArray();

    /// <summary>특정 chuteNo 수신 건수(모든 시도 포함).</summary>
    public int CountFor(int chuteNo) => _received.Count(p => p.ChuteNumbers.Contains(chuteNo));

    /// <summary>특정 chuteNo 마지막 수신.</summary>
    public ReceivedPut? LastFor(int chuteNo) =>
        _received.Where(p => p.ChuteNumbers.Contains(chuteNo)).LastOrDefault();

    public async ValueTask DisposeAsync()
    {
        try { await _app.StopAsync(TimeSpan.FromSeconds(3)); } catch { }
        await _app.DisposeAsync();
    }
}

// ── 관찰용 WebApplicationFactory (RcsPushWebApplicationFactory 미러) ──────────

/// <summary>
/// ChuteState 푸시 검증용 팩토리.
/// Wcs:ChuteStatePush:BaseUrl을 가짜 고객 서버로 설정(활성)하고, RcsPush는 BaseUrl 미설정으로
/// DORMANT 유지(무간섭). 전이는 IDestinationControlService.PauseAsync/ResumeAsync로 유도.
/// ChuteStatePusher를 IHostedService로 재등록해 실제 관찰·푸시가 살아있게 한다.
/// </summary>
public sealed class ChuteStatePushWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public FakeModbusMasterForApi FakeMaster { get; } = new();

    private readonly string? _chuteBaseUrl;
    private readonly int     _retryCount;
    private readonly int     _retryBaseDelayMs;

    private readonly PlcWriteQueue     _fakeWriteQueue = new();
    private readonly PlcGatewayOptions _fakeGwOpt = new()
    {
        Host = "127.0.0.1", Port = 1502,
        PollIntervalMs = 30, OfflineAfterFailures = 3, WriteTimeoutMs = 1000,
        RFlagPollMs = 100, RFlagTimeoutMs = 30000, CFlagTimeoutMs = 5000,
    };
    private PlcPollingService?         _fakePolling;
    private HandshakeOrchestrator?     _fakeHandshake;
    private FakeSorterGatewayRegistry? _fakeRegistry;

    private readonly string _dbName = $"WcsChuteStateTest_{Guid.NewGuid():N}";
    private readonly Microsoft.Data.Sqlite.SqliteConnection _anchorConnection;

    public long SorterDestinationId { get; private set; }
    public int  SorterChuteNo       { get; private set; }

    public ChuteStatePushWebApplicationFactory(string? chuteBaseUrl, int retryCount = 3, int retryBaseDelayMs = 30)
    {
        _chuteBaseUrl     = chuteBaseUrl;
        _retryCount       = retryCount;
        _retryBaseDelayMs = retryBaseDelayMs;

        _anchorConnection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={_dbName};Mode=Memory;Cache=Shared");
        _anchorConnection.Open();

        _fakePolling   = new PlcPollingService(_fakeGwOpt, _fakeWriteQueue, FakeMaster);
        _fakeHandshake = new HandshakeOrchestrator(_fakePolling, _fakeGwOpt);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // 즉시 평가 키(Database:Provider)는 UseSetting으로(2026-06-30 교훈).
        builder.UseSetting("Database:Provider", "Sqlite");

        // ── ChuteStatePush 설정 주입(IOptions 지연 소비 키 — ConfigureAppConfiguration OK) ──
        // RcsPush:BaseUrl은 미설정(base appsettings=null) → DORMANT 유지(무간섭).
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var dict = new Dictionary<string, string?>
            {
                ["Wcs:ChuteStatePush:BaseUrl"]          = _chuteBaseUrl,
                ["Wcs:ChuteStatePush:RetryCount"]       = _retryCount.ToString(),
                ["Wcs:ChuteStatePush:RetryBaseDelayMs"] = _retryBaseDelayMs.ToString(),
                ["Wcs:ChuteStatePush:RetryMaxDelayMs"]  = (_retryBaseDelayMs * 4).ToString(),
                ["Wcs:ChuteStatePush:HttpTimeoutMs"]    = "2000",
            };
            cfg.AddInMemoryCollection(dict);
        });

        builder.ConfigureServices(services =>
        {
            // WcsDbContext → named in-memory SQLite
            var dbDescriptors = services
                .Where(d => d.ServiceType == typeof(DbContextOptions<WcsDbContext>)
                         || d.ServiceType == typeof(WcsDbContext))
                .ToList();
            foreach (var d in dbDescriptors) services.Remove(d);

            var connStr = $"Data Source={_dbName};Mode=Memory;Cache=Shared";
            services.AddDbContext<WcsDbContext>(opts =>
                opts.UseSqlite(connStr, sqlite => sqlite.CommandTimeout(30))
                    .ConfigureWarnings(w => w.Ignore(
                        Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)),
                ServiceLifetime.Scoped);

            // 스키마 + 시드
            var dbOpts = new DbContextOptionsBuilder<WcsDbContext>()
                .UseSqlite(_anchorConnection)
                .ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
                .Options;
            using var db = new WcsDbContext(dbOpts);
            db.Database.EnsureCreated();
            DbSeeder.Seed(db, new Dictionary<string, int> { ["1"] = 1, ["2"] = 2 });

            var sorterDest = db.Destinations
                .First(d => d.DestType == Wcs.Data.DestType.SORTER_3D && d.IsActive);
            SorterDestinationId = sorterDest.Id;
            SorterChuteNo       = sorterDest.ChuteNo;

            var bundle = new SorterBundleHandle(
                destinationId: sorterDest.Id,
                chuteNo:       sorterDest.ChuteNo,
                polling:       _fakePolling!,
                handshake:     _fakeHandshake!);
            _fakeRegistry = new FakeSorterGatewayRegistry(bundle);

            // SorterRegistryFactory + ISorterGatewayRegistry 교체(DB 기동 판별 우회)
            var srfToRemove = services
                .Where(d => d.ServiceType == typeof(SorterRegistryFactory)
                         || d.ServiceType == typeof(ISorterGatewayRegistry))
                .ToList();
            foreach (var d in srfToRemove) services.Remove(d);

            // ImplementationType=null 인 IHostedService 전부 제거(람다 등록분).
            var nullHosted = services
                .Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == null)
                .ToList();
            foreach (var d in nullHosted) services.Remove(d);

            // ChuteCapacityService IHostedService 재등록(FULL/PAUSED 인메모리 집계 — scope-gate 테스트 필요)
            services.AddSingleton<IHostedService>(sp =>
                sp.GetRequiredService<ChuteCapacityService>());

            // NopSorterRegistryFactory: 폴링 기동·종료 + FakeSorterGatewayRegistry 라우팅
            var nop = new NopSorterRegistryFactory(_fakePolling!, _fakeRegistry!);
            services.AddSingleton<ISorterGatewayRegistry>(nop);
            services.AddSingleton<IHostedService>(nop);

            // ChuteStatePusher IHostedService 재등록(관찰·푸시 활성 유지 — 검증 대상).
            // RcsPush(DestinationStatusPusher)는 재등록하지 않음(BaseUrl null → 무간섭).
            services.AddSingleton<IHostedService>(sp =>
                sp.GetRequiredService<ChuteStatePusher>());
        });
    }

    Task IAsyncLifetime.InitializeAsync() => Task.CompletedTask;
    Task IAsyncLifetime.DisposeAsync() => DisposeAsyncCore().AsTask();
    public override ValueTask DisposeAsync() => DisposeAsyncCore();

    private async ValueTask DisposeAsyncCore()
    {
        _fakeWriteQueue.Writer.TryComplete();
        await base.DisposeAsync().ConfigureAwait(false);
        _anchorConnection.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _anchorConnection.Dispose();
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 클라이언트 직접 검증 (CS-PUSH-4/5/7 + dormant) — 재시도·성공/실패·payload·PUT를
// 실제 가짜 서버 본문으로 결정적 입증. bool 반환값을 직접 단언(Fail-Loud).
// ════════════════════════════════════════════════════════════════════════════

public class ChuteStatePushClientTests
{
    private readonly ITestOutputHelper _out;
    public ChuteStatePushClientTests(ITestOutputHelper output) => _out = output;

    /// <summary>테스트용 no-op operation logger(백그라운드 싱크 없이 클라이언트 단독 검증).</summary>
    private sealed class NoOpOperationLogger : IOperationLogger
    {
        public void Log(OperationLog entry) { }
        public void Log(OperationLogCategory category, string action, OperationLogLevel level = OperationLogLevel.INFO,
            int? sorterChuteNo = null, long? destinationId = null, string? barcode = null, int? pId = null,
            string? detail = null) { }
    }

    private static ChuteStatePushClient BuildClient(
        string? baseUrl, int retryCount = 2, int retryBaseDelayMs = 20)
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
            httpFactory, opts, NullLogger<ChuteStatePushClient>.Instance, new NoOpOperationLogger());
    }

    // ── CS-PUSH-4: 200 {flag:1} → 성공(재시도 0) ─────────────────────────────
    [Fact]
    public async Task CS_PUSH_4_Success_FlagOne_Accepted_NoRetry()
    {
        await using var srv = await FakeChuteStateServer.StartAsync();
        var client = BuildClient(srv.BaseUrl);

        bool ok = await client.PushAsync(new ChuteStatePushPayload(new[] { 7 }, new[] { 2 }));

        Assert.True(ok, "200 {flag:1} → 성공");
        Assert.Equal(1, srv.All.Count);      // 재시도 없이 정확히 1회 전송.
        _out.WriteLine($"[CS-PUSH-4] 성공 응답 → ok={ok}, 전송 {srv.All.Count}회(재시도 0)");
    }

    // ── CS-PUSH-5: 비2xx/{result:Failed}/flag≠1 → 재시도 후 false(Fail-Loud) + 복구 도달 ──
    [Fact]
    public async Task CS_PUSH_5_Failure_Retry_FailLoud_And_Recover()
    {
        await using var srv = await FakeChuteStateServer.StartAsync();
        var client = BuildClient(srv.BaseUrl, retryCount: 2, retryBaseDelayMs: 20);

        // (a) 비2xx(503) → 재시도 소진 후 false. 총 시도 = 1 + retryCount(2) = 3회.
        srv.SetMode(ChuteStateRespMode.Reject503);
        bool ok503 = await client.PushAsync(new ChuteStatePushPayload(new[] { 8 }, new[] { 2 }));
        Assert.False(ok503, "503 → 재시도 소진 후 false(조용한 성공 위장 금지)");
        Assert.Equal(3, srv.All.Count);      // 1 + 2 재시도 = 3회 전부 서버 도달(조용한 드롭 0).

        // (b) {result:"Failed"}(400) → 실패.
        srv.SetMode(ChuteStateRespMode.FailBody400);
        bool okFail = await client.PushAsync(new ChuteStatePushPayload(new[] { 8 }, new[] { 2 }));
        Assert.False(okFail, "{result:\"Failed\"} → false");

        // (c) flag≠1(200 {flag:0}) → 실패(2xx라도 flag로 실패 판정).
        srv.SetMode(ChuteStateRespMode.FlagZero200);
        bool okFlag0 = await client.PushAsync(new ChuteStatePushPayload(new[] { 8 }, new[] { 2 }));
        Assert.False(okFlag0, "200 flag:0 → false");

        // (d) 복구 — 다음 전송은 정상 도달.
        srv.SetMode(ChuteStateRespMode.Success);
        bool okRecover = await client.PushAsync(new ChuteStatePushPayload(new[] { 8 }, new[] { 3 }));
        Assert.True(okRecover, "복구 후 성공 도달");

        _out.WriteLine($"[CS-PUSH-5] 503(3시도)→false, Failed→false, flag0→false, 복구→true. 총 전송 {srv.All.Count}회");
    }

    // ── CS-PUSH-7: payload 정합 — snake_case 키·PUT·인덱스 정렬·동일 길이 ──────
    [Fact]
    public async Task CS_PUSH_7_PayloadShape_SnakeCase_Put_IndexAligned()
    {
        await using var srv = await FakeChuteStateServer.StartAsync();
        var client = BuildClient(srv.BaseUrl);

        bool ok = await client.PushAsync(new ChuteStatePushPayload(new[] { 1001 }, new[] { 2 }));
        Assert.True(ok);

        var last = srv.All.Single();
        Assert.Equal("PUT", last.Method);                        // ★ PUT 메서드(positive).

        // 키가 정확히 chute_numbers·next_states(camelCase 아님)·2개.
        using var doc = JsonDocument.Parse(last.RawBody);
        var props = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Equal(2, props.Count);
        Assert.Contains("chute_numbers", props);
        Assert.Contains("next_states",   props);
        Assert.DoesNotContain("chuteNumbers", props);            // camelCase 금지(계약 함정).
        Assert.DoesNotContain("nextStates",   props);

        // 값·인덱스 정렬·동일 길이.
        Assert.Equal(new[] { 1001 }, last.ChuteNumbers);
        Assert.Equal(new[] { 2 },    last.NextStates);
        Assert.Equal(last.ChuteNumbers.Length, last.NextStates.Length);

        _out.WriteLine($"[CS-PUSH-7] PUT snake_case 인덱스 정렬 확인: {last.RawBody}");
    }

    // ── CS-PUSH-6(client): DORMANT — BaseUrl null → HTTP 시도 0·false ─────────
    [Fact]
    public async Task CS_PUSH_6c_Dormant_NoBaseUrl_NoHttp()
    {
        await using var srv = await FakeChuteStateServer.StartAsync();
        var client = BuildClient(baseUrl: null);

        Assert.False(client.IsEnabled);
        bool ok = await client.PushAsync(new ChuteStatePushPayload(new[] { 1 }, new[] { 2 }));

        Assert.False(ok, "DORMANT → false(미발신)");
        Assert.Empty(srv.All);      // HTTP 시도 0.
        _out.WriteLine($"[CS-PUSH-6c] DORMANT → ok={ok}, 수신 {srv.All.Count}건");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 관찰(observer) 통합 검증 (CS-PUSH-1/2/3/6) — 실제 PAUSED/RESUMED 전이 →
// ChuteStatePusher → 클라이언트 → 가짜 고객 서버 수신 본문으로 end-to-end 입증.
// ════════════════════════════════════════════════════════════════════════════

public class ChuteStatePushObserverTests
{
    private readonly ITestOutputHelper _out;
    public ChuteStatePushObserverTests(ITestOutputHelper output) => _out = output;

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs, string msg, int pollMs = 20)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTimeOffset.Now > deadline) Assert.Fail($"WaitUntil 타임아웃({timeoutMs}ms): {msg}");
            await Task.Delay(pollMs);
        }
    }

    private static async Task WaitUntilExactAsync(
        Func<int> countFunc, int expected, int stableCount, int timeoutMs, string msg, int pollMs = 25)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        int consecutive = 0;
        while (consecutive < stableCount)
        {
            if (DateTimeOffset.Now > deadline)
                Assert.Fail($"WaitUntilExact 타임아웃({timeoutMs}ms): {msg} (현재={countFunc()}, 기대={expected})");
            if (countFunc() == expected) consecutive++;
            else                         consecutive = 0;
            await Task.Delay(pollMs);
        }
    }

    private static (long destId, int chuteNo) ChuteDest(ChuteStatePushWebApplicationFactory f, int chuteNo)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        var d  = db.Destinations.First(x => x.ChuteNo == chuteNo && x.DestType == Wcs.Data.DestType.CHUTE);
        return (d.Id, d.ChuteNo);
    }

    // ── CS-PUSH-1: PAUSE(CHUTE) 전이 → {chute_numbers:[ChuteNo], next_states:[2]} 1건 ──
    [Fact]
    public async Task CS_PUSH_1_Pause_Chute_Pushes_State2()
    {
        await using var srv     = await FakeChuteStateServer.StartAsync();
        await using var factory = new ChuteStatePushWebApplicationFactory(srv.BaseUrl);
        _ = factory.CreateClient();

        var (destId, chuteNo) = ChuteDest(factory, 1);   // 시드 NORMAL 슈트.
        var control = factory.Services.GetRequiredService<IDestinationControlService>();

        var res = await control.PauseAsync(destId, "op-test");
        Assert.Equal(DestControlOutcome.Transitioned, res.Outcome);

        await WaitUntilAsync(() => srv.CountFor(chuteNo) >= 1, 5000, "PAUSE 푸시 수신");
        await WaitUntilExactAsync(() => srv.CountFor(chuteNo), 1, stableCount: 5, timeoutMs: 3000, "전이당 1건(중복 0)");

        var last = srv.LastFor(chuteNo)!;
        Assert.Equal("PUT", last.Method);
        Assert.Equal(new[] { chuteNo }, last.ChuteNumbers);
        Assert.Equal(new[] { 2 },       last.NextStates);
        _out.WriteLine($"[CS-PUSH-1] CHUTE PAUSE → chute_numbers=[{chuteNo}] next_states=[2] 1건");
    }

    // ── CS-PUSH-1b: PAUSE(SORTER_3D) 전이도 동일하게 푸시 ──────────────────────
    [Fact]
    public async Task CS_PUSH_1b_Pause_Sorter_Pushes_State2()
    {
        await using var srv     = await FakeChuteStateServer.StartAsync();
        await using var factory = new ChuteStatePushWebApplicationFactory(srv.BaseUrl);
        _ = factory.CreateClient();

        int sorterChute = factory.SorterChuteNo;
        var control = factory.Services.GetRequiredService<IDestinationControlService>();

        var res = await control.PauseAsync(factory.SorterDestinationId, "op-test");
        Assert.Equal(DestControlOutcome.Transitioned, res.Outcome);
        Assert.Equal(Wcs.Data.DestType.SORTER_3D, res.DestType);

        await WaitUntilAsync(() => srv.CountFor(sorterChute) >= 1, 5000, "소터 PAUSE 푸시 수신");
        await WaitUntilExactAsync(() => srv.CountFor(sorterChute), 1, stableCount: 5, timeoutMs: 3000, "소터 전이당 1건");

        var last = srv.LastFor(sorterChute)!;
        Assert.Equal(new[] { sorterChute }, last.ChuteNumbers);
        Assert.Equal(new[] { 2 },           last.NextStates);
        _out.WriteLine($"[CS-PUSH-1b] SORTER PAUSE → chute_numbers=[{sorterChute}] next_states=[2] 1건");
    }

    // ── CS-PUSH-2: RESUME 전이 → next_states:[3] 1건 ─────────────────────────
    [Fact]
    public async Task CS_PUSH_2_Resume_Pushes_State3()
    {
        await using var srv     = await FakeChuteStateServer.StartAsync();
        await using var factory = new ChuteStatePushWebApplicationFactory(srv.BaseUrl);
        _ = factory.CreateClient();

        // 시드 chuteNo 6 = PAUSED. RESUME → NORMAL 전이 → next_state 3.
        long destId6;
        int  chuteNo6 = 6;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            destId6 = db.Destinations.First(d => d.ChuteNo == 6 && d.DestType == Wcs.Data.DestType.CHUTE).Id;
        }
        var control = factory.Services.GetRequiredService<IDestinationControlService>();

        var res = await control.ResumeAsync(destId6, "op-test");
        Assert.Equal(DestControlOutcome.Transitioned, res.Outcome);

        await WaitUntilAsync(() => srv.CountFor(chuteNo6) >= 1, 5000, "RESUME 푸시 수신");
        await WaitUntilExactAsync(() => srv.CountFor(chuteNo6), 1, stableCount: 5, timeoutMs: 3000, "RESUME 전이당 1건");

        var last = srv.LastFor(chuteNo6)!;
        Assert.Equal(new[] { chuteNo6 }, last.ChuteNumbers);
        Assert.Equal(new[] { 3 },        last.NextStates);
        _out.WriteLine($"[CS-PUSH-2] RESUME → chute_numbers=[{chuteNo6}] next_states=[3] 1건");
    }

    // ── CS-PUSH-3: scope 게이트 — FULL·O6·AlreadyInState 무발신, 실제 pause만 발신 ──
    [Fact]
    public async Task CS_PUSH_3_ScopeGate_Full_O6_Idempotent_NoPush_RealPause_Pushes()
    {
        await using var srv     = await FakeChuteStateServer.StartAsync();
        await using var factory = new ChuteStatePushWebApplicationFactory(srv.BaseUrl);
        var httpClient = factory.CreateClient();

        var (dest1Id, _) = ChuteDest(factory, 1);
        var (dest2Id, chute2) = ChuteDest(factory, 2);
        long destId6;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            destId6 = db.Destinations.First(d => d.ChuteNo == 6 && d.DestType == Wcs.Data.DestType.CHUTE).Id;
        }
        var control  = factory.Services.GetRequiredService<IDestinationControlService>();
        var capacity = factory.Services.GetRequiredService<IChuteCapacityService>();

        // (1) FULL(capacity) 상태 변화 → OnChuteStateChanged만 발화(OnTransition 아님) → 발신 0.
        using (var scope = factory.Services.CreateScope())
        {
            var db     = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            var detail = db.ChuteDetails.First(cd => cd.DestinationId == dest1Id);
            capacity.OnReserved(dest1Id, detail.WorkFullQty);   // 만재 진입(FULL)
        }

        // (2) O6 CellAssign(소터 PLC 쓰기 경로) — DestinationControlService 무접촉 → 발신 0.
        var o6 = await httpClient.PostAsJsonAsync(
            $"/api/ops/sorters/{factory.SorterDestinationId}/cell-assign",
            new { cellNo = 1, seq = 1, operatorName = "op-test" });
        _out.WriteLine($"[CS-PUSH-3] O6 cell-assign status={(int)o6.StatusCode}(발신과 무관)");

        // (3) AlreadyInState(멱등) — chuteNo 6은 이미 PAUSED. PauseAsync → AlreadyInState → 발신 0.
        var idem = await control.PauseAsync(destId6, "op-test");
        Assert.Equal(DestControlOutcome.AlreadyInState, idem.Outcome);

        // 위 3종 이후에도 가짜 서버 수신 0건(전이 종류 게이트).
        await Task.Delay(500);
        Assert.Empty(srv.All);
        _out.WriteLine($"[CS-PUSH-3] FULL·O6·AlreadyInState 후 수신 {srv.All.Count}건(무발신)");

        // (4) 실제 PAUSE 전이(chuteNo 2, NORMAL→PAUSED) → 정확히 1건(pusher 생존·게이트 진성 확인).
        var real = await control.PauseAsync(dest2Id, "op-test");
        Assert.Equal(DestControlOutcome.Transitioned, real.Outcome);
        await WaitUntilAsync(() => srv.CountFor(chute2) >= 1, 5000, "실제 PAUSE 푸시 수신");
        await WaitUntilExactAsync(() => srv.All.Count, 1, stableCount: 5, timeoutMs: 3000, "총 1건(scope 게이트 진성)");
        Assert.Equal(new[] { 2 }, srv.LastFor(chute2)!.NextStates);
        _out.WriteLine($"[CS-PUSH-3] 실제 PAUSE → 총 {srv.All.Count}건(chuteNo 2, next_state 2)");
    }

    // ── CS-PUSH-6: DORMANT — BaseUrl null → 크래시 0·수신 0·인바운드 정상 ─────
    [Fact]
    public async Task CS_PUSH_6_Dormant_NoBaseUrl_NoHttp_NoCrash_InboundOk()
    {
        await using var srv     = await FakeChuteStateServer.StartAsync();
        await using var factory = new ChuteStatePushWebApplicationFactory(chuteBaseUrl: null);
        var httpClient = factory.CreateClient();   // 기동 크래시 없어야 함.

        var (dest1Id, _) = ChuteDest(factory, 1);
        var control = factory.Services.GetRequiredService<IDestinationControlService>();

        // pause/resume 전이를 발생시켜도(DORMANT) 아무것도 발신되지 않음.
        await control.PauseAsync(dest1Id, "op-test");
        await control.ResumeAsync(dest1Id, "op-test");
        await control.PauseAsync(factory.SorterDestinationId, "op-test");

        // 인바운드 IF-05 정상(회귀 0 — 푸시 비활성이 인바운드를 막지 않음).
        var req  = new { pId = 20001, agvNo = 1, barcode = "TEST-BARCODE-1", inductionNo = 1, qty = 1, timeStamp = (string?)null };
        var resp = await httpClient.PostAsJsonAsync("/api/v1/destination-query", req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        await Task.Delay(500);
        Assert.Empty(srv.All);   // HTTP 시도 0.
        _out.WriteLine($"[CS-PUSH-6] DORMANT → 전이 3회에도 수신 {srv.All.Count}건, IF-05 정상(200)");
    }
}
