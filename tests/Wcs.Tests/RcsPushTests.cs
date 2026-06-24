using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Wcs.Api;
using Wcs.Core;
using Wcs.Data;
using Wcs.PlcGateway;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// RCS↔WCS 재설계 Phase 2 — IF-08 아웃바운드 푸시 검증
//
// 가짜 RCS 수신 HTTP 서버(FakeRcsServer)로 WCS가 보낸 destination-status 푸시를
// 실제로 수신·카운트·payload 검증한다. 인메모리 GREEN을 PASS 근거로 삼지 않고
// "가짜 RCS가 수신한 실제 JSON 본문"으로 입증(메타교훈 — 전용 시나리오).
//
// 검증 시나리오(계약 §Verification Scenarios):
//   VS-PUSH-1 슈트 ready 전이(true→false→true) → 전이당 정확히 1건 수신
//   VS-PUSH-2 소터 ready 전이(false→true→false) → 전이당 정확히 1건 + 폴마다 폭주 0
//   VS-PUSH-3 무변화 → 푸시 0건(폭주 방지·핵심)
//   VS-PUSH-4 동시 전이 → 전이당 1회 멱등(중복 0·누락 0)
//   VS-PUSH-5 RCS 미도달 → 재시도 → 복구 후 푸시 도달(최신값 유지)
//   VS-PUSH-6 초기 스냅샷 푸시(부트스트랩 — 기동 시 전 목적지 1회)
//   VS-PUSH-7 payload 정합({chuteNo, ready, timeStamp} — 개별 full/paused/online 키 부재)
//   VS-PUSH-8 BaseUrl 미설정 → 푸시 비활성(크래시 X·수신 0)
// ════════════════════════════════════════════════════════════════════════════

// ── 가짜 RCS 수신 서버 ────────────────────────────────────────────────────────

/// <summary>
/// 가짜 RCS 수신 HTTP 서버 — POST /api/v1/destination-status 수신·기록.
/// Kestrel(동적 포트)로 in-process 기동. 거부 모드 토글로 미도달/복구 시뮬레이션.
/// </summary>
public sealed class FakeRcsServer : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<ReceivedPush> _received = new();

    // true면 503 반환(미도달 시뮬레이션). false면 정상 {result:"OK"}.
    private volatile bool _rejecting;

    public sealed record ReceivedPush(int ChuteNo, bool Ready, string? TimeStamp, string RawBody);

    public string BaseUrl { get; }

    private FakeRcsServer(WebApplication app, string baseUrl)
    {
        _app    = app;
        BaseUrl = baseUrl;
    }

    /// <summary>가짜 RCS 서버 기동(동적 포트).</summary>
    public static async Task<FakeRcsServer> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");  // 동적 포트
        builder.Logging.ClearProviders();
        var app = builder.Build();

        FakeRcsServer? self = null;

        app.MapPost("/api/v1/destination-status", async (HttpContext ctx) =>
        {
            using var reader = new StreamReader(ctx.Request.Body);
            var raw = await reader.ReadToEndAsync();

            if (self!.Rejecting)
            {
                ctx.Response.StatusCode = 503;
                await ctx.Response.WriteAsync("rejecting");
                return;
            }

            // payload 파싱 — camelCase 와이어
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            int  chuteNo = root.GetProperty("chuteNo").GetInt32();
            bool ready   = root.GetProperty("ready").GetBoolean();
            string? ts   = root.TryGetProperty("timeStamp", out var tsEl) ? tsEl.GetString() : null;

            self.Record(new ReceivedPush(chuteNo, ready, ts, raw));

            ctx.Response.StatusCode = 200;
            await ctx.Response.WriteAsJsonAsync(new { result = "OK" });
        });

        await app.StartAsync();

        // 실제 바인딩된 주소 조회
        var server  = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
        var feature = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!;
        var address = feature.Addresses.First();

        self = new FakeRcsServer(app, address);
        return self;
    }

    public bool Rejecting => _rejecting;
    public void StartRejecting() => _rejecting = true;
    public void StopRejecting()  => _rejecting = false;

    private void Record(ReceivedPush push) => _received.Enqueue(push);

    /// <summary>전체 수신 푸시(순서 보존).</summary>
    public IReadOnlyList<ReceivedPush> All => _received.ToArray();

    /// <summary>특정 chuteNo 수신 건수.</summary>
    public int CountFor(int chuteNo) => _received.Count(p => p.ChuteNo == chuteNo);

    /// <summary>특정 chuteNo의 마지막 수신.</summary>
    public ReceivedPush? LastFor(int chuteNo) =>
        _received.Where(p => p.ChuteNo == chuteNo).LastOrDefault();

    public async ValueTask DisposeAsync()
    {
        try { await _app.StopAsync(TimeSpan.FromSeconds(3)); } catch { }
        await _app.DisposeAsync();
    }
}

// ── 푸시 검증용 WebApplicationFactory ────────────────────────────────────────

/// <summary>
/// IF-08 푸시 검증용 팩토리.
/// FakeModbusWebApplicationFactory와 달리 DestinationStatusPusher를 **활성** 유지하고
/// Wcs:RcsPush:BaseUrl을 가짜 RCS로 설정한다(생성자 인자).
/// 소터 ready 전이는 FakeMaster 레지스터 조작 → 폴링 스냅샷 변화로 유도.
/// </summary>
public sealed class RcsPushWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public FakeModbusMasterForApi FakeMaster { get; } = new();

    private readonly string? _rcsBaseUrl;
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

    public PlcPollingService? FakePolling => _fakePolling;

    private static readonly string _dbName = $"WcsPushTest_{Guid.NewGuid():N}";
    private readonly Microsoft.Data.Sqlite.SqliteConnection _anchorConnection;

    public long SorterDestinationId { get; private set; }
    public int  SorterChuteNo       { get; private set; }

    public RcsPushWebApplicationFactory(string? rcsBaseUrl, int retryCount = 3, int retryBaseDelayMs = 50)
    {
        _rcsBaseUrl       = rcsBaseUrl;
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
        // ── RcsPush 설정 주입(BaseUrl·재시도 — 하드코딩 0, 설정 경유) ───────────────
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var dict = new Dictionary<string, string?>
            {
                ["Wcs:RcsPush:BaseUrl"]                 = _rcsBaseUrl,
                ["Wcs:RcsPush:RetryCount"]              = _retryCount.ToString(),
                ["Wcs:RcsPush:RetryBaseDelayMs"]        = _retryBaseDelayMs.ToString(),
                ["Wcs:RcsPush:RetryMaxDelayMs"]         = (_retryBaseDelayMs * 4).ToString(),
                ["Wcs:RcsPush:HttpTimeoutMs"]           = "2000",
                ["Wcs:RcsPush:SorterObserveIntervalMs"] = "30",
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

            // ImplementationType=null 인 IHostedService 전부 제거
            //   (SorterRegistryFactory 람다 + DestinationStatusPusher 람다 포함).
            // → ChuteCapacityService·DestinationStatusPusher를 다시 명시 재등록.
            var nullHosted = services
                .Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == null)
                .ToList();
            foreach (var d in nullHosted) services.Remove(d);

            // ChuteCapacityService IHostedService 재등록
            services.AddSingleton<IHostedService>(sp =>
                sp.GetRequiredService<ChuteCapacityService>());

            // NopSorterRegistryFactory: 폴링 기동·종료 + FakeSorterGatewayRegistry 라우팅
            var nop = new NopSorterRegistryFactory(_fakePolling!, _fakeRegistry!);
            services.AddSingleton<ISorterGatewayRegistry>(nop);
            services.AddSingleton<IHostedService>(nop);

            // DestinationStatusPusher IHostedService 재등록(푸시 활성 유지).
            //   → 가짜 RCS 수신 검증을 위해 반드시 살아 있어야 한다.
            services.AddSingleton<IHostedService>(sp =>
                sp.GetRequiredService<DestinationStatusPusher>());
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
// VS-PUSH 테스트
// ════════════════════════════════════════════════════════════════════════════

public class RcsPushTests
{
    private readonly ITestOutputHelper _out;
    public RcsPushTests(ITestOutputHelper output) => _out = output;

    // ── 헬퍼: 조건 폴링 ────────────────────────────────────────────────────────
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs, string msg, int pollMs = 20)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTimeOffset.Now > deadline)
                Assert.Fail($"WaitUntil 타임아웃({timeoutMs}ms): {msg}");
            await Task.Delay(pollMs);
        }
    }

    /// <summary>count()가 expected를 stableCount회 연속 반환할 때까지 폴링(추가 전이 없음=안정).</summary>
    private static async Task WaitUntilExactAsync(
        Func<int> countFunc, int expected, int stableCount,
        int timeoutMs, string msg, int pollMs = 30)
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

    private static async Task WaitForSnapshotAsync(
        RcsPushWebApplicationFactory factory, Func<PlcSnapshot, bool> condition, int timeoutMs, int pollMs = 20)
    {
        var polling = factory.FakePolling!;
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        while (!condition(polling.Latest))
        {
            if (DateTimeOffset.Now > deadline) Assert.Fail($"WaitForSnapshot 타임아웃({timeoutMs}ms)");
            await Task.Delay(pollMs);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // VS-PUSH-6 + VS-PUSH-7: 부트스트랩 초기 스냅샷 푸시 + payload 정합
    // 기동 시 전 목적지(슈트 5 + PAUSED 슈트 1 + 소터 1)의 현재 ready를 1회 푸시.
    // payload는 {chuteNo, ready, timeStamp} 정확히 — 개별 full/paused/online 키 부재.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task PUSH6_7_Bootstrap_InitialSnapshot_And_PayloadShape()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        _ = factory.CreateClient();  // 호스트 기동(HostedService StartAsync 실행)

        // 부트스트랩: 활성 목적지 전부 1회 푸시 — 슈트 1~5(chuteNo 1~5) + PAUSED(chuteNo 6) + 소터(chuteNo 30)
        // = 7개. 각 chuteNo가 정확히 1건씩 수신될 때까지 대기.
        await WaitUntilAsync(() => rcs.All.Count >= 7, 8000, "부트스트랩 7목적지 푸시 수신");

        // 무변화 안정 — 7건에서 더 늘지 않음(폴마다 폭주 0)
        await WaitUntilExactAsync(() => rcs.All.Count, 7, stableCount: 6, timeoutMs: 4000,
            msg: "부트스트랩 후 무변화 안정");

        // 슈트 1~5 = NORMAL → ready:true / chuteNo 6 = PAUSED → ready:false / 소터 30 = 미정렬(CurFloor=1≠2) → ready:false
        Assert.Equal(1, rcs.CountFor(1));
        var c1 = rcs.LastFor(1)!;
        Assert.True(c1.Ready, "NORMAL 슈트 1 → ready:true");

        Assert.Equal(1, rcs.CountFor(6));
        Assert.False(rcs.LastFor(6)!.Ready, "PAUSED 슈트 6 → ready:false");

        Assert.Equal(1, rcs.CountFor(factory.SorterChuteNo));
        Assert.False(rcs.LastFor(factory.SorterChuteNo)!.Ready, "미정렬 소터 → ready:false");

        // payload 정합: {chuteNo, ready, timeStamp} 정확히 — 개별 full/paused/online 키 부재.
        using var doc = JsonDocument.Parse(c1.RawBody);
        var props = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Equal(3, props.Count);
        Assert.Contains("chuteNo",   props);
        Assert.Contains("ready",     props);
        Assert.Contains("timeStamp", props);
        Assert.DoesNotContain("full",   props);
        Assert.DoesNotContain("paused", props);
        Assert.DoesNotContain("online", props);
        // timeStamp 포맷 "yyyy-MM-dd HH:mm:ss"
        Assert.Matches(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}$", c1.TimeStamp!);

        _out.WriteLine($"[VS-PUSH-6/7] 부트스트랩 7건 수신. payload 정합·timeStamp={c1.TimeStamp}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // VS-PUSH-1: 슈트 ready 전이(true→false→true) → 전이당 정확히 1건
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task PUSH1_Chute_ReadyTransition_OnePushPerTransition()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        _ = factory.CreateClient();

        // 부트스트랩 정착 대기(슈트 4 = NORMAL → ready:true 1건)
        await WaitUntilAsync(() => rcs.CountFor(4) >= 1, 8000, "부트스트랩 슈트4 수신");
        await WaitUntilExactAsync(() => rcs.CountFor(4), 1, stableCount: 5, timeoutMs: 4000, "부트스트랩 후 슈트4 안정");

        using var scope = factory.Services.CreateScope();
        var db       = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        var capacity = scope.ServiceProvider.GetRequiredService<IChuteCapacityService>();
        var dest4    = db.Destinations.First(d => d.ChuteNo == 4 && d.DestType == Wcs.Data.DestType.CHUTE);
        var detail4  = db.ChuteDetails.First(cd => cd.DestinationId == dest4.Id);

        // true→false: 만재 경계 통과(OnReserved qty=workFullQty)
        capacity.OnReserved(dest4.Id, detail4.WorkFullQty);
        await WaitUntilAsync(() => rcs.CountFor(4) >= 2, 5000, "슈트4 true→false 푸시");
        await WaitUntilExactAsync(() => rcs.CountFor(4), 2, stableCount: 5, timeoutMs: 4000, "true→false 후 안정(중복 0)");
        Assert.False(rcs.LastFor(4)!.Ready, "만재 → ready:false");

        // false→true: 비움(OnCleared)
        await capacity.OnCleared(dest4.Id);
        await WaitUntilAsync(() => rcs.CountFor(4) >= 3, 5000, "슈트4 false→true 푸시");
        await WaitUntilExactAsync(() => rcs.CountFor(4), 3, stableCount: 5, timeoutMs: 4000, "false→true 후 안정");
        Assert.True(rcs.LastFor(4)!.Ready, "비움 → ready:true");

        // 총 3건(부트스트랩 1 + 전이 2). 전이당 정확히 1건.
        Assert.Equal(3, rcs.CountFor(4));
        _out.WriteLine($"[VS-PUSH-1] 슈트4 전이당 1건 — 총 {rcs.CountFor(4)}건(부트1+전이2)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // VS-PUSH-2 + VS-PUSH-3: 소터 ready 전이(false→true→false) + 무변화 0건(폭주 방지)
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task PUSH2_3_Sorter_ReadyTransition_OnePush_NoFloodOnUnchangedPolls()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        _ = factory.CreateClient();

        int sorterChute = factory.SorterChuteNo;

        // 부트스트랩: 소터 미정렬(CurFloor=1≠운영층2) → ready:false 1건
        await WaitUntilAsync(() => rcs.CountFor(sorterChute) >= 1, 8000, "부트스트랩 소터 수신");
        await WaitUntilExactAsync(() => rcs.CountFor(sorterChute), 1, stableCount: 6, timeoutMs: 4000,
            "무변화 폴 다수에도 소터 푸시 1건 유지(폭주 0)");
        Assert.False(rcs.LastFor(sorterChute)!.Ready);

        // false→true: 운영층 정렬(CurFloor=2·Ready=1) → ready:true
        factory.FakeMaster.SetReady(true);
        factory.FakeMaster.SetCurFloor(2);
        factory.FakeMaster.SetTgtFloor(0);
        await WaitForSnapshotAsync(factory, s => s.Online && s.CurFloor == 2 && s.Ready, 5000);
        await WaitUntilAsync(() => rcs.CountFor(sorterChute) >= 2, 5000, "소터 false→true 푸시");
        await WaitUntilExactAsync(() => rcs.CountFor(sorterChute), 2, stableCount: 6, timeoutMs: 4000,
            "false→true 후 무변화 폴 다수에도 2건 유지(폭주 0·핵심)");
        Assert.True(rcs.LastFor(sorterChute)!.Ready, "정렬 완료 → ready:true");

        // true→false: 분류 시작(Ready 1→0) → ready:false
        factory.FakeMaster.SetReady(false);
        await WaitForSnapshotAsync(factory, s => s.Online && !s.Ready, 5000);
        await WaitUntilAsync(() => rcs.CountFor(sorterChute) >= 3, 5000, "소터 true→false 푸시");
        await WaitUntilExactAsync(() => rcs.CountFor(sorterChute), 3, stableCount: 6, timeoutMs: 4000,
            "true→false 후 안정(중복 0)");
        Assert.False(rcs.LastFor(sorterChute)!.Ready, "분류 시작 → ready:false");

        // 총 3건(부트스트랩 1 + 전이 2). 무변화 폴 N회에도 폭주 0.
        Assert.Equal(3, rcs.CountFor(sorterChute));
        _out.WriteLine($"[VS-PUSH-2/3] 소터 전이당 1건·폭주 0 — 총 {rcs.CountFor(sorterChute)}건");
    }

    // ════════════════════════════════════════════════════════════════════════
    // VS-PUSH-4: 동시 전이 → 전이당 1회 멱등(중복 0·누락 0)
    // 슈트 콜백(스레드 다수) + 소터 관찰 타이머가 동시에 같은/다른 chuteNo를 갱신.
    // 같은 chuteNo의 한 전이에 대해 정확히 1건 수신.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task PUSH4_ConcurrentTransition_ExactlyOncePerTransition()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        _ = factory.CreateClient();

        // 부트스트랩 정착(슈트 5 chuteNo 5 = NORMAL → ready:true 1건)
        await WaitUntilAsync(() => rcs.CountFor(5) >= 1, 8000, "부트스트랩 슈트5 수신");
        await WaitUntilExactAsync(() => rcs.CountFor(5), 1, stableCount: 5, timeoutMs: 4000, "부트스트랩 안정");

        using var scope = factory.Services.CreateScope();
        var db       = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        var capacity = scope.ServiceProvider.GetRequiredService<IChuteCapacityService>();
        var dest5    = db.Destinations.First(d => d.ChuteNo == 5 && d.DestType == Wcs.Data.DestType.CHUTE);
        var detail5  = db.ChuteDetails.First(cd => cd.DestinationId == dest5.Id);

        // 같은 chuteNo의 한 전이(true→false)를 다수 스레드가 동시에 통지(만재 도달 후 추가 예약 폭주).
        // 첫 전이만 1건, 이후 같은 ready=false 재산출은 0건이어야 함(동일 전이 중복 억제 + 동시 멱등).
        capacity.OnReserved(dest5.Id, detail5.WorkFullQty);  // 만재 진입 = true→false 전이

        // 동시에 같은 ready=false를 유발하는 NotifyChuteChanged를 병렬 발사(추가 예약 — 이미 만재)
        const int concurrency = 16;
        using var barrier = new Barrier(concurrency);
        var tasks = Enumerable.Range(0, concurrency).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            capacity.OnReserved(dest5.Id, 1);  // 이미 만재 → ready 여전히 false(전이 없음)
        })).ToArray();
        await Task.WhenAll(tasks);

        // 전이는 1회(true→false)뿐 — 부트스트랩 1 + 전이 1 = 정확히 2건. 동시 폭주에도 중복 0.
        await WaitUntilAsync(() => rcs.CountFor(5) >= 2, 5000, "슈트5 전이 1건 도달");
        await WaitUntilExactAsync(() => rcs.CountFor(5), 2, stableCount: 8, timeoutMs: 5000,
            "동시 16통지에도 전이당 정확히 1건(중복 0)");
        Assert.Equal(2, rcs.CountFor(5));
        Assert.False(rcs.LastFor(5)!.Ready);
        _out.WriteLine($"[VS-PUSH-4] 동시 {concurrency}통지 → 전이당 1건(총 {rcs.CountFor(5)}건=부트1+전이1)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // VS-PUSH-5: RCS 미도달 → 재시도 → 복구 후 푸시 도달(최신값 유지·확정3)
    // 가짜 RCS를 거부(503)로 토글 → 전이 발생 → 재시도 소진(미알림 유지) →
    // 가짜 RCS 재개 → 다음 전이(또는 재산출)에서 최신 ready 도달.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task PUSH5_RcsUnreachable_Retry_RecoverAndPushLatest()
    {
        await using var rcs = await FakeRcsServer.StartAsync();
        // 재시도 2회·짧은 백오프(테스트 속도) — 설정 경유.
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl, retryCount: 2, retryBaseDelayMs: 30);
        _ = factory.CreateClient();

        // 부트스트랩 정착(슈트 3 chuteNo 3 = NORMAL → ready:true 1건)
        await WaitUntilAsync(() => rcs.CountFor(3) >= 1, 8000, "부트스트랩 슈트3 수신");
        await WaitUntilExactAsync(() => rcs.CountFor(3), 1, stableCount: 5, timeoutMs: 4000, "부트스트랩 안정");
        int baseline = rcs.CountFor(3);

        using var scope = factory.Services.CreateScope();
        var db       = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        var capacity = scope.ServiceProvider.GetRequiredService<IChuteCapacityService>();
        var dest3    = db.Destinations.First(d => d.ChuteNo == 3 && d.DestType == Wcs.Data.DestType.CHUTE);
        var detail3  = db.ChuteDetails.First(cd => cd.DestinationId == dest3.Id);

        // RCS 거부 모드 — 이후 푸시는 재시도 소진(미도달).
        rcs.StartRejecting();

        // true→false 전이 발생(만재) — 재시도 소진되어 수신 0건(거부 중).
        capacity.OnReserved(dest3.Id, detail3.WorkFullQty);

        // 재시도 소진까지 대기 후에도 수신 카운트는 baseline 유지(미알림 — 확정3: 실패를 성공으로 간주 안 함).
        await Task.Delay(400);
        Assert.Equal(baseline, rcs.CountFor(3));
        _out.WriteLine($"[VS-PUSH-5] 거부 중 전이 → 수신 {rcs.CountFor(3)}건(미알림 유지)");

        // RCS 복구 — 거부 해제.
        rcs.StopRejecting();

        // 복구 후 재푸시 유도: 같은 chuteNo에 추가 상태 변화 통지(여전히 만재 → ready:false).
        // Acked가 미갱신(stale=true)이므로 Computed(false)≠Acked(true) → 재푸시 1건 도달.
        capacity.OnReservationCancelled(dest3.Id, 0);  // 상태 무변(만재 유지) → 재평가 트리거

        await WaitUntilAsync(() => rcs.CountFor(3) >= baseline + 1, 5000, "복구 후 재푸시 도달");
        await WaitUntilExactAsync(() => rcs.CountFor(3), baseline + 1, stableCount: 5, timeoutMs: 4000,
            "복구 재푸시 1건(최신 ready=false)");
        Assert.False(rcs.LastFor(3)!.Ready, "복구 후 최신 ready=false 도달");
        _out.WriteLine($"[VS-PUSH-5] 복구 후 재푸시 도달 — 최신 ready={rcs.LastFor(3)!.Ready}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // VS-PUSH-8: BaseUrl 미설정 → 푸시 비활성(크래시 X·수신 0) — 사용자 확정4
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task PUSH8_NoBaseUrl_PushDisabled_NoCrash_NoReceive()
    {
        await using var rcs = await FakeRcsServer.StartAsync();
        // BaseUrl=null → 푸시 비활성. 인바운드는 정상.
        await using var factory = new RcsPushWebApplicationFactory(rcsBaseUrl: null);
        var client = factory.CreateClient();  // 기동 크래시 없어야 함

        // 인바운드 IF-05 정상 동작(회귀 0 — 푸시 비활성이 인바운드를 막지 않음)
        var req  = new { pId = 20001, agvNo = 1, barcode = "TEST-BARCODE-1", inductionNo = 1, qty = 1, timeStamp = (string?)null };
        var resp = await client.PostAsJsonAsync("/api/v1/destination-query", req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // 충분히 대기해도 가짜 RCS 수신 0건(푸시 비활성).
        await Task.Delay(500);
        Assert.Empty(rcs.All);
        _out.WriteLine($"[VS-PUSH-8] BaseUrl 미설정 → 푸시 비활성. 수신 {rcs.All.Count}건, IF-05 정상(200)");
    }
}
