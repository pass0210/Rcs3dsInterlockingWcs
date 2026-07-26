using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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
// S-TWO-FLOOR-CONTROL 서브 스프린트 B — IF-08 층별 호스트 라우팅 · 소터 dual-host push 검증.
//
// 가짜 RCS 수신 HTTP 서버를 **층당 1대(총 2대)** 띄우고(FakeChuteStateServer), WCS가 층→호스트 맵
// (Wcs:ChuteStatePush:FloorHosts)으로 라우팅한 UpdateChuteState 를 각 호스트가 실제로 수신·검증한다.
// 인메모리 GREEN 금지 — "각 층 fake 서버가 수신한 실제 JSON 본문"으로 실증(메타교훈).
//
//   VS-B1  고정 슈트 1층 전이 → 1층 서버만 수신(2층 0), RESUME 대칭
//   VS-B2  고정 슈트 2층 전이 → 2층 서버만 수신(1층 0) — 층 라우팅 대칭
//   VS-B3  소터 dual-host(정렬 1층) → 1층=3 · 2층=2 (둘 다 1건, 층 필드 없음)
//   VS-B4  소터 CurFloor 1→2 → 1층 3→2 1건 + 2층 2→3 1건 (각 전이당 1건, 중복 0)
//   VS-B5  부트스트랩 dual-host — 소터 A·B 모두, 고정 슈트 자기 층 1곳(각 조합 1건)
//   VS-B6  한쪽 층 호스트 다운(부분 실패 격리) — 2층 정상·1층 재시도 소진·독립·복구 재푸시
//   VS-B7  층별 DORMANT — 1층만 설정 시 2층 목적지 no-op / 전 층 미설정 시 전체 DORMANT
//   VS-B8  소터 오프라인/미수용 → 두 층 모두 2 (단일 층 3 누출 0)
//   VS-B9  wire 계약 불변 — 두 호스트 snake_case · PUT · 층 필드 유입 0
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 층별 호스트 라우팅 검증용 팩토리 — Wcs:ChuteStatePush:FloorHosts 를 층당 fake 서버로 결선.
/// 시드 CHUTE(Floor=NULL)에 층을 부여(chuteNo1→1·chuteNo2→2)해 "고정 슈트" 라우팅을 검증한다.
/// 소터(chuteNo30·Floor=NULL)는 FakeMaster 스냅샷으로 dual-host 라우팅을 구동.
/// DestinationStatusPusher(단일 발신 소스)를 활성 유지(실제 관찰·발신).
/// </summary>
public sealed class TwoFloorPushWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public FakeModbusMasterForApi FakeMaster { get; } = new();

    private readonly string? _floor1Url;
    private readonly string? _floor2Url;
    private readonly int     _retryCount;
    private readonly int     _retryBaseDelayMs;

    private readonly PlcWriteQueue     _fakeWriteQueue = new();
    private readonly PlcGatewayOptions _fakeGwOpt = new()
    {
        Host = "127.0.0.1", Port = 1502,
        PollIntervalMs = 30, OfflineAfterFailures = 3, WriteTimeoutMs = 1000,
        RFlagPollMs = 100, RFlagTimeoutMs = 30000, CFlagTimeoutMs = 5000,
    };
    private readonly PlcPollingService     _fakePolling;
    private readonly HandshakeOrchestrator _fakeHandshake;
    private FakeSorterGatewayRegistry?     _fakeRegistry;

    private readonly string _dbName = $"WcsTwoFloorTest_{Guid.NewGuid():N}";
    private readonly Microsoft.Data.Sqlite.SqliteConnection _anchorConnection;

    public long SorterDestinationId { get; private set; }
    public int  SorterChuteNo       { get; private set; }

    public TwoFloorPushWebApplicationFactory(
        string? floor1Url, string? floor2Url, int retryCount = 3, int retryBaseDelayMs = 30)
    {
        _floor1Url        = floor1Url;
        _floor2Url        = floor2Url;
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
        builder.UseSetting("Database:Provider", "Sqlite");

        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var dict = new Dictionary<string, string?>
            {
                ["Wcs:ChuteStatePush:RetryCount"]              = _retryCount.ToString(),
                ["Wcs:ChuteStatePush:RetryBaseDelayMs"]        = _retryBaseDelayMs.ToString(),
                ["Wcs:ChuteStatePush:RetryMaxDelayMs"]         = (_retryBaseDelayMs * 4).ToString(),
                ["Wcs:ChuteStatePush:HttpTimeoutMs"]           = "2000",
                ["Wcs:ChuteStatePush:SorterObserveIntervalMs"] = "30",
            };
            // 층→호스트 맵(설정값만 — 코드 리터럴 0). null 층은 미설정(그 층 DORMANT).
            if (_floor1Url is not null) dict["Wcs:ChuteStatePush:FloorHosts:1"] = _floor1Url;
            if (_floor2Url is not null) dict["Wcs:ChuteStatePush:FloorHosts:2"] = _floor2Url;
            cfg.AddInMemoryCollection(dict);
        });

        builder.ConfigureServices(services =>
        {
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

            var dbOpts = new DbContextOptionsBuilder<WcsDbContext>()
                .UseSqlite(_anchorConnection)
                .ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
                .Options;
            using var db = new WcsDbContext(dbOpts);
            db.Database.EnsureCreated();
            DbSeeder.Seed(db, new Dictionary<string, int> { ["1"] = 1, ["2"] = 2 });

            // ★ 시드 CHUTE(Floor=NULL)에 층 부여 — "고정 슈트"(Floor 비-NULL) 라우팅 검증용.
            //   chuteNo1 → 1층 / chuteNo2 → 2층. 나머지(3·4·5·6)는 Floor=NULL 유지(층-무관 → 전 층 발신).
            db.Destinations.First(d => d.ChuteNo == 1 && d.DestType == DestType.CHUTE).Floor = 1;
            db.Destinations.First(d => d.ChuteNo == 2 && d.DestType == DestType.CHUTE).Floor = 2;
            db.SaveChanges();

            var sorterDest = db.Destinations
                .First(d => d.DestType == DestType.SORTER_3D && d.IsActive);
            SorterDestinationId = sorterDest.Id;
            SorterChuteNo       = sorterDest.ChuteNo;

            var bundle = new SorterBundleHandle(
                destinationId: sorterDest.Id,
                chuteNo:       sorterDest.ChuteNo,
                polling:       _fakePolling,
                handshake:     _fakeHandshake);
            _fakeRegistry = new FakeSorterGatewayRegistry(bundle);

            var srfToRemove = services
                .Where(d => d.ServiceType == typeof(SorterRegistryFactory)
                         || d.ServiceType == typeof(ISorterGatewayRegistry))
                .ToList();
            foreach (var d in srfToRemove) services.Remove(d);

            var nullHosted = services
                .Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == null)
                .ToList();
            foreach (var d in nullHosted) services.Remove(d);

            services.AddSingleton<IHostedService>(sp =>
                sp.GetRequiredService<ChuteCapacityService>());

            var nop = new NopSorterRegistryFactory(_fakePolling, _fakeRegistry!);
            services.AddSingleton<ISorterGatewayRegistry>(nop);
            services.AddSingleton<IHostedService>(nop);

            // 통합 발신 소스 활성 — 층별 라우팅 실 발신 검증 대상.
            services.AddSingleton<IHostedService>(sp =>
                sp.GetRequiredService<DestinationStatusPusher>());
        });
    }

    public (long destId, int chuteNo) ChuteDest(int chuteNo)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        var d  = db.Destinations.First(x => x.ChuteNo == chuteNo && x.DestType == DestType.CHUTE);
        return (d.Id, d.ChuteNo);
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
// VS-B — 층별 호스트 라우팅(층당 fake 서버 2대 수신 본문 실증)
// [Collection("RealSimSerial")] — 정확-카운트 단언을 무거운 실-Sim 테스트와 직렬화(병렬 부하 flake 제거).
// ════════════════════════════════════════════════════════════════════════════
[Collection("RealSimSerial")]
public class TwoFloorHostRoutingTests
{
    private readonly ITestOutputHelper _out;
    public TwoFloorHostRoutingTests(ITestOutputHelper output) => _out = output;

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

    // ── VS-B1: 고정 슈트 1층 전이 → 1층 서버만 수신 ─────────────────────────────
    [Fact]
    public async Task VSB1_FixedChute_Floor1_Transition_RoutesToFloor1HostOnly()
    {
        await using var srvA = await FakeChuteStateServer.StartAsync();   // 1층
        await using var srvB = await FakeChuteStateServer.StartAsync();   // 2층
        await using var factory = new TwoFloorPushWebApplicationFactory(srvA.BaseUrl, srvB.BaseUrl);
        _ = factory.CreateClient();

        var (destId, chuteNo) = factory.ChuteDest(1);   // Floor=1 부여됨.

        // 부트스트랩: NORMAL·비만재 → 3, 1층(srvA)만 수신. 2층(srvB)엔 chuteNo1 수신 0.
        await WaitUntilAsync(() => srvA.CountFor(chuteNo) >= 1, 6000, "1층 부트스트랩 수신");
        await WaitUntilExactAsync(() => srvA.CountFor(chuteNo), 1, stableCount: 5, timeoutMs: 4000, "1층 부트스트랩 안정");
        Assert.Equal(new[] { 3 }, srvA.LastFor(chuteNo)!.NextStates);
        Assert.Equal(0, srvB.CountFor(chuteNo));

        var control = factory.Services.GetRequiredService<IDestinationControlService>();
        Assert.Equal(DestControlOutcome.Transitioned, (await control.PauseAsync(destId, "op")).Outcome);

        // PAUSE → 1층 [2] 정확히 1건. 2층 여전히 0.
        await WaitUntilAsync(() => srvA.CountFor(chuteNo) >= 2, 5000, "1층 PAUSE 발신");
        await WaitUntilExactAsync(() => srvA.CountFor(chuteNo), 2, stableCount: 5, timeoutMs: 3000, "1층 전이당 1건");
        Assert.Equal(new[] { 2 }, srvA.LastFor(chuteNo)!.NextStates);
        Assert.Equal(0, srvB.CountFor(chuteNo));

        // RESUME → 1층 [3].
        Assert.Equal(DestControlOutcome.Transitioned, (await control.ResumeAsync(destId, "op")).Outcome);
        await WaitUntilAsync(() => srvA.LastFor(chuteNo) is { NextStates: [3] }, 5000, "1층 RESUME [3]");
        Assert.Equal(0, srvB.CountFor(chuteNo));
        _out.WriteLine($"[VS-B1] 1층 슈트 전이 3→2→3, 1층만 수신(2층 {srvB.CountFor(chuteNo)}건)");
    }

    // ── VS-B2: 고정 슈트 2층 전이 → 2층 서버만 수신(대칭) ──────────────────────
    [Fact]
    public async Task VSB2_FixedChute_Floor2_Transition_RoutesToFloor2HostOnly()
    {
        await using var srvA = await FakeChuteStateServer.StartAsync();
        await using var srvB = await FakeChuteStateServer.StartAsync();
        await using var factory = new TwoFloorPushWebApplicationFactory(srvA.BaseUrl, srvB.BaseUrl);
        _ = factory.CreateClient();

        var (destId, chuteNo) = factory.ChuteDest(2);   // Floor=2 부여됨.

        await WaitUntilAsync(() => srvB.CountFor(chuteNo) >= 1, 6000, "2층 부트스트랩 수신");
        await WaitUntilExactAsync(() => srvB.CountFor(chuteNo), 1, stableCount: 5, timeoutMs: 4000, "2층 부트스트랩 안정");
        Assert.Equal(new[] { 3 }, srvB.LastFor(chuteNo)!.NextStates);
        Assert.Equal(0, srvA.CountFor(chuteNo));

        var control = factory.Services.GetRequiredService<IDestinationControlService>();
        Assert.Equal(DestControlOutcome.Transitioned, (await control.PauseAsync(destId, "op")).Outcome);

        await WaitUntilAsync(() => srvB.CountFor(chuteNo) >= 2, 5000, "2층 PAUSE 발신");
        await WaitUntilExactAsync(() => srvB.CountFor(chuteNo), 2, stableCount: 5, timeoutMs: 3000, "2층 전이당 1건");
        Assert.Equal(new[] { 2 }, srvB.LastFor(chuteNo)!.NextStates);
        Assert.Equal(0, srvA.CountFor(chuteNo));   // 1층 서버엔 2층 슈트 유입 0.
        _out.WriteLine($"[VS-B2] 2층 슈트 전이 → 2층만 수신(1층 {srvA.CountFor(chuteNo)}건) — 라우팅 대칭");
    }

    // ── VS-B3: 소터 dual-host(정렬 1층·ready) → 1층=3 · 2층=2 ─────────────────
    [Fact]
    public async Task VSB3_Sorter_DualHost_AlignedFloor1_Floor1Is3_Floor2Is2()
    {
        await using var srvA = await FakeChuteStateServer.StartAsync();
        await using var srvB = await FakeChuteStateServer.StartAsync();
        await using var factory = new TwoFloorPushWebApplicationFactory(srvA.BaseUrl, srvB.BaseUrl);
        _ = factory.CreateClient();
        int sorter = factory.SorterChuteNo;

        // FakeMaster 기본 online·Ready=1·CurFloor=1 → 소터 1층 정렬 수용.
        await WaitUntilAsync(() => srvA.LastFor(sorter) is { NextStates: [3] }, 8000, "1층 호스트 next_state 3");
        await WaitUntilAsync(() => srvB.LastFor(sorter) is { NextStates: [2] }, 8000, "2층 호스트 next_state 2");
        await WaitUntilExactAsync(() => srvA.CountFor(sorter), srvA.CountFor(sorter), stableCount: 4, timeoutMs: 2000, "안정");

        var a = srvA.LastFor(sorter)!;
        var b = srvB.LastFor(sorter)!;
        Assert.Equal(new[] { 3 }, a.NextStates);   // CurFloor=1 층 = 수용
        Assert.Equal(new[] { 2 }, b.NextStates);   // 다른 층 = 불가
        // 두 호스트 payload 동일 형식·층 필드 없음.
        Assert.Equal(new[] { sorter }, a.ChuteNumbers);
        Assert.Equal(new[] { sorter }, b.ChuteNumbers);
        _out.WriteLine("[VS-B3] 소터 정렬 1층 → 1층=3 · 2층=2 (dual-host, 층 필드 없음)");
    }

    // ── VS-B4: 소터 CurFloor 1→2 → 1층 3→2 1건 + 2층 2→3 1건 (각 전이당 1건) ──
    [Fact]
    public async Task VSB4_Sorter_CurFloorTransition_1to2_ExactlyOnePerFloorHost()
    {
        await using var srvA = await FakeChuteStateServer.StartAsync();
        await using var srvB = await FakeChuteStateServer.StartAsync();
        await using var factory = new TwoFloorPushWebApplicationFactory(srvA.BaseUrl, srvB.BaseUrl);
        _ = factory.CreateClient();
        int sorter = factory.SorterChuteNo;

        // 정렬 1층 정착: 1층=3, 2층=2.
        await WaitUntilAsync(() => srvA.LastFor(sorter) is { NextStates: [3] } && srvB.LastFor(sorter) is { NextStates: [2] },
            8000, "정렬 1층 정착");
        int baseA = srvA.CountFor(sorter);
        int baseB = srvB.CountFor(sorter);
        await WaitUntilExactAsync(() => srvA.CountFor(sorter) + srvB.CountFor(sorter), baseA + baseB,
            stableCount: 6, timeoutMs: 4000, "정렬 안정");

        // CurFloor 1→2 재정렬(Ready 유지). 1층 3→2(서비스 중단) 1건 + 2층 2→3(새 서비스) 1건.
        factory.FakeMaster.SetCurFloor(2);

        await WaitUntilAsync(() => srvA.LastFor(sorter) is { NextStates: [2] }, 5000, "1층 3→2");
        await WaitUntilAsync(() => srvB.LastFor(sorter) is { NextStates: [3] }, 5000, "2층 2→3");
        // 각 (dest,floor) 전이당 정확히 1건(중복 0). 무변화 관찰 폴에서 추가 발신 0.
        await WaitUntilExactAsync(() => srvA.CountFor(sorter), baseA + 1, stableCount: 8, timeoutMs: 5000, "1층 전이 정확히 1건");
        await WaitUntilExactAsync(() => srvB.CountFor(sorter), baseB + 1, stableCount: 8, timeoutMs: 5000, "2층 전이 정확히 1건");
        Assert.Equal(new[] { 2 }, srvA.LastFor(sorter)!.NextStates);
        Assert.Equal(new[] { 3 }, srvB.LastFor(sorter)!.NextStates);
        _out.WriteLine($"[VS-B4] CurFloor 1→2 → 1층 3→2(1건)·2층 2→3(1건), 무변화 폴 추가 발신 0");
    }

    // ── VS-B5: 부트스트랩 dual-host — 각 목적지·호스트 조합 정확히 1건 ──────────
    [Fact]
    public async Task VSB5_Bootstrap_DualHost_EachDestHostComboExactlyOnce()
    {
        await using var srvA = await FakeChuteStateServer.StartAsync();
        await using var srvB = await FakeChuteStateServer.StartAsync();
        await using var factory = new TwoFloorPushWebApplicationFactory(srvA.BaseUrl, srvB.BaseUrl);
        _ = factory.CreateClient();
        int sorter = factory.SorterChuteNo;

        // 소터: A(1층)=3(CurFloor=1 수용) + B(2층)=2. 고정 슈트1(1층)=srvA. 고정 슈트2(2층)=srvB.
        //   소터는 기동 offline→online 전이로 부트 count가 1~2 가변(폴 워밍업 아티팩트 — PUSH6_7 동형)이라
        //   count 정확-1 대신 **최종값 + 안정(폭주 0)**으로 단언. 고정 슈트(항상 online)는 정확히 1건.
        await WaitUntilAsync(() =>
            srvA.LastFor(sorter) is { NextStates: [3] } && srvB.LastFor(sorter) is { NextStates: [2] }
            && srvA.CountFor(1) >= 1 && srvB.CountFor(2) >= 1, 8000, "부트스트랩 전 목적지 정착(소터 3·2, 슈트 자기 층)");

        // 고정 슈트: 자기 층 호스트에 정확히 1건(전이 없음).
        await WaitUntilExactAsync(() => srvA.CountFor(1), 1, stableCount: 6, timeoutMs: 4000, "슈트1 1층 정확히 1건");
        await WaitUntilExactAsync(() => srvB.CountFor(2), 1, stableCount: 6, timeoutMs: 4000, "슈트2 2층 정확히 1건");
        // 소터: count 안정(무변화 폴 추가 발신 0 — 폭주 방지).
        int sa = srvA.CountFor(sorter), sb = srvB.CountFor(sorter);
        await WaitUntilExactAsync(() => srvA.CountFor(sorter), sa, stableCount: 6, timeoutMs: 4000, "소터 1층 안정(폭주 0)");
        await WaitUntilExactAsync(() => srvB.CountFor(sorter), sb, stableCount: 6, timeoutMs: 4000, "소터 2층 안정(폭주 0)");

        Assert.Equal(new[] { 3 }, srvA.LastFor(sorter)!.NextStates);   // 소터 CurFloor=1 → 1층 3
        Assert.Equal(new[] { 2 }, srvB.LastFor(sorter)!.NextStates);   // 다른 층 2
        Assert.Equal(new[] { 3 }, srvA.LastFor(1)!.NextStates);        // 고정 슈트1 자기 층(1)
        Assert.Equal(new[] { 3 }, srvB.LastFor(2)!.NextStates);        // 고정 슈트2 자기 층(2)
        Assert.Equal(0, srvB.CountFor(1));   // 슈트1은 2층 호스트에 유입 0.
        Assert.Equal(0, srvA.CountFor(2));   // 슈트2는 1층 호스트에 유입 0.
        _out.WriteLine("[VS-B5] 부트스트랩 — 소터 두 층 모두(3·2), 고정 슈트 자기 층 1곳, 각 조합 1건");
    }

    // ── VS-B6: 한쪽 층 호스트 다운(부분 실패 격리) ──────────────────────────────
    [Fact]
    public async Task VSB6_OneFloorHostDown_PartialFailureIsolation_And_Recovery()
    {
        await using var srvA = await FakeChuteStateServer.StartAsync();   // 1층 — 다운 시뮬
        await using var srvB = await FakeChuteStateServer.StartAsync();   // 2층 — 정상
        srvA.StartRejecting();   // 1층 연결 거부(503).
        await using var factory = new TwoFloorPushWebApplicationFactory(srvA.BaseUrl, srvB.BaseUrl, retryCount: 2, retryBaseDelayMs: 20);
        _ = factory.CreateClient();
        int sorter = factory.SorterChuteNo;

        // 2층(srvB)은 정상 수신(소터 CurFloor=1 → 2층 = next_state 2). 1층 다운과 무관하게 delivery.
        await WaitUntilAsync(() => srvB.LastFor(sorter) is { NextStates: [2] }, 8000, "2층 정상 수신(1층 다운 무관)");
        Assert.True(srvB.CountFor(sorter) >= 1, "2층은 1층 실패에 영향받지 않음");

        // 1층(srvA)은 재시도 소진 — 성공 delivery 0(Accepted=false). 시도 이력은 존재(실패 명시).
        await WaitUntilAsync(() => srvA.All.Count >= 1, 5000, "1층 재시도 시도 도달");
        Assert.Equal(0, srvA.CountFor(sorter));   // 거부 중 성공 delivery 0.

        // 1층 다운 중 2층 독립 전이: CurFloor 1→2 → 2층 2→3 정상 delivery(1층 실패 무관).
        factory.FakeMaster.SetCurFloor(2);
        await WaitUntilAsync(() => srvB.LastFor(sorter) is { NextStates: [3] }, 5000, "2층 2→3 독립 delivery");

        // 1층 복구 → 다음 관찰에서 재푸시 도달(복구 하트비트 — CurFloor=2라 1층 = next_state 2로 수렴).
        //   ★ 재시도 중 CurFloor 1→2 전이가 겹치면 pusher 가 재시도하던 stale target(=3)이 복구 직후 먼저
        //     도달할 수 있으나(전이당-1회 발신의 재평가로 곧 최신값 [2] 재푸시), 최종은 [2]로 수렴한다. 따라서
        //     특정 push 순간을 instant assert 하던 취약성을 제거하고 **수렴값**을 WaitUntil 로 단언한다
        //     (B 발신 로직 무변경 — 관측 시점만 견고화. C2 기동 배리어의 타이밍 시프트로 노출된 pre-existing
        //     경합의 견고화).
        srvA.StopRejecting();
        await WaitUntilAsync(() => srvA.LastFor(sorter) is { NextStates: [2] }, 6000, "1층 복구 후 재푸시 [2] 수렴");
        _out.WriteLine($"[VS-B6] 1층 다운 중 2층 정상(2→3)·1층 재시도 소진, 복구 후 1층 재푸시 도달");
    }

    // ── VS-B7: 층별 DORMANT ──────────────────────────────────────────────────────
    [Fact]
    public async Task VSB7_PerFloorDormant_And_FullDormant()
    {
        // (a) 1층만 설정 → 1층 목적지 정상, 2층 목적지·소터 2층분 no-op.
        await using (var srvA = await FakeChuteStateServer.StartAsync())
        await using (var srvB = await FakeChuteStateServer.StartAsync())
        await using (var factory = new TwoFloorPushWebApplicationFactory(srvA.BaseUrl, floor2Url: null))
        {
            var client = factory.CreateClient();
            int sorter = factory.SorterChuteNo;

            // 1층 슈트(chuteNo1) + 소터 1층분 수신. 2층 슈트(chuteNo2) no-op.
            await WaitUntilAsync(() => srvA.CountFor(1) >= 1 && srvA.CountFor(sorter) >= 1, 8000, "1층 목적지 수신");
            await WaitUntilExactAsync(() => srvA.CountFor(2), 0, stableCount: 8, timeoutMs: 3000, "2층 슈트 no-op(1층 서버에 유입 0)");
            Assert.Equal(0, srvB.All.Count);   // 2층 미설정 → 2층 서버 총 수신 0.
            Assert.Equal(new[] { 3 }, srvA.LastFor(sorter)!.NextStates);   // 소터 1층분(CurFloor=1)만.

            // 인바운드(IF-05) 정상 회귀 0.
            var resp = await client.PostAsJsonAsync("/api/v1/destination-query",
                new { pId = 27101, agvNo = 1, barcode = "TEST-BARCODE-1", inductionNo = 1, qty = 1, timeStamp = (string?)null });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            _out.WriteLine("[VS-B7a] 1층만 설정 → 1층 정상·2층 no-op(수신 0)·IF-05 정상");
        }

        // (b) 전 층 미설정 → 서브시스템 전체 DORMANT(총 수신 0·크래시 0·인바운드 정상).
        await using (var srvA = await FakeChuteStateServer.StartAsync())
        await using (var srvB = await FakeChuteStateServer.StartAsync())
        await using (var factory = new TwoFloorPushWebApplicationFactory(floor1Url: null, floor2Url: null))
        {
            var client = factory.CreateClient();   // 기동 크래시 없어야 함.
            var control = factory.Services.GetRequiredService<IDestinationControlService>();
            var (destId, _) = factory.ChuteDest(1);
            await control.PauseAsync(destId, "op");   // 전이 유발해도 미발신.

            var resp = await client.PostAsJsonAsync("/api/v1/destination-query",
                new { pId = 27102, agvNo = 1, barcode = "TEST-BARCODE-1", inductionNo = 1, qty = 1, timeStamp = (string?)null });
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

            await Task.Delay(500);
            Assert.Empty(srvA.All);
            Assert.Empty(srvB.All);
            _out.WriteLine("[VS-B7b] 전 층 미설정 → 전체 DORMANT(총 수신 0·크래시 0·IF-05 정상)");
        }
    }

    // ── VS-B8: 소터 오프라인/미수용 → 두 층 모두 2 ─────────────────────────────
    [Fact]
    public async Task VSB8_SorterOfflineOrNotAccepting_BothFloorsGet2()
    {
        await using var srvA = await FakeChuteStateServer.StartAsync();
        await using var srvB = await FakeChuteStateServer.StartAsync();
        await using var factory = new TwoFloorPushWebApplicationFactory(srvA.BaseUrl, srvB.BaseUrl);
        _ = factory.CreateClient();
        int sorter = factory.SorterChuteNo;

        // 먼저 정렬(1층=3) 정착.
        await WaitUntilAsync(() => srvA.LastFor(sorter) is { NextStates: [3] }, 8000, "정렬 1층 3");

        // OFFLINE 주입 → 두 층 모두 next_state 2(단일 층 3 누출 0).
        factory.FakeMaster.SetFailReads(true);
        await WaitUntilAsync(() => srvA.LastFor(sorter) is { NextStates: [2] }, 6000, "오프라인 → 1층 2");
        await WaitUntilAsync(() => srvB.LastFor(sorter) is { NextStates: [2] }, 6000, "오프라인 → 2층 2");
        Assert.Equal(new[] { 2 }, srvA.LastFor(sorter)!.NextStates);
        Assert.Equal(new[] { 2 }, srvB.LastFor(sorter)!.NextStates);
        _out.WriteLine("[VS-B8] 소터 오프라인 → 두 층 호스트 모두 next_state 2(3 누출 0)");
    }

    // ── VS-B9: wire 계약 불변(두 호스트 snake_case · PUT · 층 필드 유입 0) ──────
    [Fact]
    public async Task VSB9_WireContract_SnakeCase_Put_NoFloorField_BothHosts()
    {
        await using var srvA = await FakeChuteStateServer.StartAsync();
        await using var srvB = await FakeChuteStateServer.StartAsync();
        await using var factory = new TwoFloorPushWebApplicationFactory(srvA.BaseUrl, srvB.BaseUrl);
        _ = factory.CreateClient();
        int sorter = factory.SorterChuteNo;

        // 소터 dual-host 발신으로 두 호스트 모두 수신 확보.
        await WaitUntilAsync(() => srvA.CountFor(sorter) >= 1 && srvB.CountFor(sorter) >= 1, 8000, "두 호스트 수신");

        foreach (var (name, last) in new[] { ("1층", srvA.LastFor(sorter)!), ("2층", srvB.LastFor(sorter)!) })
        {
            Assert.Equal("PUT", last.Method);   // ★ PUT(positive).
            using var doc = JsonDocument.Parse(last.RawBody);
            var props = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
            Assert.Equal(2, props.Count);
            Assert.Contains("chute_numbers", props);
            Assert.Contains("next_states",   props);
            Assert.DoesNotContain("chuteNumbers", props);   // camelCase 금지.
            Assert.DoesNotContain("nextStates",   props);
            Assert.DoesNotContain("floor", props);          // 층 필드 유입 0.
            Assert.DoesNotContain("Floor", props);
            _out.WriteLine($"[VS-B9] {name} wire: {last.RawBody}");
        }
    }
}

// ════════════════════════════════════════════════════════════════════════════
// VS-E2 (I-2 · Q5) — 만재 소터 정렬 기입 + ComputeSorterFull 매 틱 미호출 증거.
//
// SorterFloorReturnService(관측 루프)를 직접 구성(FakeMaster = Modbus 슬레이브 스탠드인, 단일 쓰기 큐
// → 레지스터 경로 실증)하고, IDestinationStatusService 를 **호출 카운트 스파이**로 주입한다.
//   레이어: 관측 루프 트리거 → Core 게이트(DepositDecider) → 소터별 단일 쓰기 큐 → FakeMaster D6.
//   증거: 관측 루프가 매 유휴 틱마다 IsPaused(저비용)만 호출하고 Compute(→ ComputeSorterFull 셀 집계)는
//         **0회** 호출한다. 스파이 Compute 가 Full=true 를 산출하도록 해도 호출되지 않으므로 만재가 정렬
//         기입을 차단하지 못함을 구조적으로 실증(Q5). Paused=true 면 여전히 미기입(#2 유지).
// ════════════════════════════════════════════════════════════════════════════
[Collection("RealSimSerial")]
public class TwoFloorWriteGateI2Tests
{
    private readonly ITestOutputHelper _out;
    public TwoFloorWriteGateI2Tests(ITestOutputHelper output) => _out = output;

    /// <summary>호출 카운트 스파이 — Compute(→ComputeSorterFull)와 IsPaused 호출 수를 분리 계측.</summary>
    private sealed class CountingStatusService(bool paused) : IDestinationStatusService
    {
        private int _computeCount;
        private int _isPausedCount;
        public int ComputeCount  => Volatile.Read(ref _computeCount);
        public int IsPausedCount => Volatile.Read(ref _isPausedCount);

        // 만약 관측 루프가 이걸 호출하면(구 코드) Full=true 라 정렬 기입이 차단됐을 것 — 호출 0이어야 함.
        public DestinationReadiness Compute(long destinationId, DestType destType)
        {
            Interlocked.Increment(ref _computeCount);
            return new DestinationReadiness(Ready: false, Full: true, Paused: paused, Online: true, DenyReason.Full);
        }

        public bool SorterHasAssignedCellWithRoomForBarcode(long destinationId, string barcode) => false;
        public bool SorterCanAcceptBarcode(long destinationId, string barcode) => false;

        public bool IsPaused(long destinationId)
        {
            Interlocked.Increment(ref _isPausedCount);
            return paused;
        }
    }

    /// <summary>관측 루프 단위 테스트용 no-op I-3 복원기 — DB 없이 재파생 skip(테스트가 큐를 직접 조작).</summary>
    private sealed class NoopQueueRestorer : IPendingFloorQueueRestorer
    {
        public Task<int> RestoreAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    /// <summary>테스트용 IHostApplicationLifetime — ApplicationStopping=None(취소 안 됨).</summary>
    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted  => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped  => CancellationToken.None;
        public void StopApplication() { }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs, string msg, int pollMs = 15)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTimeOffset.Now > deadline) Assert.Fail($"WaitUntil 타임아웃({timeoutMs}ms): {msg}");
            await Task.Delay(pollMs);
        }
    }

    private static (SorterFloorReturnService svc, FakeModbusMasterForApi master, PlcWriteQueue wq, PlcPollingService polling)
        BuildReturnService(CountingStatusService spy, long destId, int chuteNo, out SorterPendingFloorQueues queues)
    {
        var master  = new FakeModbusMasterForApi();   // 기본 online·Ready=1·CurFloor=1·TgtFloor=0.
        var gwOpt   = new PlcGatewayOptions
        {
            Host = "127.0.0.1", Port = 1502,
            PollIntervalMs = 20, OfflineAfterFailures = 3, WriteTimeoutMs = 1000,
            RFlagPollMs = 100, RFlagTimeoutMs = 30000, CFlagTimeoutMs = 5000,
        };
        var wq      = new PlcWriteQueue();
        var polling = new PlcPollingService(gwOpt, wq, master);
        var handshake = new HandshakeOrchestrator(polling, gwOpt);
        var bundle  = new SorterBundleHandle(destId, chuteNo, polling, handshake);
        var registry = new FakeSorterGatewayRegistry(bundle);

        queues = new SorterPendingFloorQueues();

        var opts = Options.Create(new WcsOptions
        {
            SorterFloorReturn = new SorterFloorReturnOptions { ObserveIntervalMs = 20 },
        });
        var svc = new SorterFloorReturnService(
            registry, queues, spy, new FakeLifetime(), new NoopQueueRestorer(),
            new CapturingOperationLogger(), opts,
            NullLogger<SorterFloorReturnService>.Instance);

        return (svc, master, wq, polling);
    }

    // ── VS-E2(a): 만재(would-be Full) 소터 idle·미정렬 → 정렬 기입 발생 + Compute 0회 ──
    [Fact]
    public async Task VSE2a_FullSorter_Idle_Misaligned_WritesTgtFloor_And_ComputeNotCalledPerTick()
    {
        const long destId = 500;
        var spy = new CountingStatusService(paused: false);
        var (svc, master, wq, polling) = BuildReturnService(spy, destId, chuteNo: 30, out var queues);
        await polling.StartAsync(CancellationToken.None);
        try
        {
            // idle(Ready=1)·미정렬(CurFloor=1, 큐 머리 F=2). 폴 스냅샷 online 정착 대기.
            master.SetReady(true); master.SetCurFloor(1); master.SetTgtFloor(0);
            await WaitUntilAsync(() => polling.Latest.Online && polling.Latest.Ready && polling.Latest.CurFloor == 1, 3000, "스냅샷 정착");

            queues.Enqueue(destId, 2);   // 머리 F=2 (CurFloor=1과 다름 → 정렬 기입 대상).

            await svc.StartAsync(CancellationToken.None);

            // 관측 루프가 F=2 를 단일 쓰기 큐로 기입 → FakeMaster D6=2(만재여도 차단 안 됨 — Q5).
            await WaitUntilAsync(() => master.GetTgtFloor() == 2, 4000, "TgtFloor=2 기입(만재여도 정렬 진행)");

            // 매 틱 IsPaused 는 호출되나 Compute(→ComputeSorterFull)는 0회(I-2 — 셀 집계 매틱 미호출).
            await WaitUntilAsync(() => spy.IsPausedCount >= 5, 3000, "IsPaused 매 틱 호출(저비용 게이트)");
            int isPausedNow = spy.IsPausedCount;
            await Task.Delay(200);   // 추가 틱 경과.
            Assert.True(spy.IsPausedCount > isPausedNow, "IsPaused 는 관측 틱마다 계속 호출됨");
            Assert.Equal(0, spy.ComputeCount);   // ★ Compute(→ComputeSorterFull 셀 집계)는 한 번도 호출 안 됨.

            Assert.Equal(2, master.GetTgtFloor());
            _out.WriteLine($"[VS-E2a] 만재 소터 정렬 기입 O(TgtFloor=2) · IsPaused {spy.IsPausedCount}회 · Compute {spy.ComputeCount}회(0)");
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
            wq.Writer.TryComplete();
            await polling.DisposeAsync();
        }
    }

    // ── VS-E2(b): Paused 소터 → 정렬 기입 안 함(#2 유지) + Compute 0회 ─────────────
    [Fact]
    public async Task VSE2b_PausedSorter_Idle_Misaligned_DoesNotWrite_And_ComputeNotCalled()
    {
        const long destId = 501;
        var spy = new CountingStatusService(paused: true);
        var (svc, master, wq, polling) = BuildReturnService(spy, destId, chuteNo: 30, out var queues);
        await polling.StartAsync(CancellationToken.None);
        try
        {
            master.SetReady(true); master.SetCurFloor(1); master.SetTgtFloor(0);
            await WaitUntilAsync(() => polling.Latest.Online && polling.Latest.Ready && polling.Latest.CurFloor == 1, 3000, "스냅샷 정착");

            queues.Enqueue(destId, 2);
            await svc.StartAsync(CancellationToken.None);

            // Paused → 게이트 차단(DepositDecider Deny). 여러 틱 경과해도 TgtFloor=0 유지.
            await WaitUntilAsync(() => spy.IsPausedCount >= 5, 3000, "IsPaused 호출(게이트 진입)");
            await Task.Delay(200);
            Assert.Equal(0, master.GetTgtFloor());   // Paused → 미기입(#2 유지).
            Assert.Equal(0, spy.ComputeCount);        // Compute(셀 집계) 여전히 0회.
            _out.WriteLine($"[VS-E2b] Paused 소터 → 정렬 기입 0(TgtFloor=0 유지) · Compute {spy.ComputeCount}회(0)");
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
            wq.Writer.TryComplete();
            await polling.DisposeAsync();
        }
    }
}
