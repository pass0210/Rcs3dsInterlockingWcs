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
using Wcs.Api;
using Wcs.Core;
using Wcs.Data;
using Wcs.PlcGateway;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// S-IF08-READY-PUSH — 목적지 수용상태 아웃바운드 푸시 검증(확정 와이어 UpdateChuteState)
//
// 가짜 RCS 수신 HTTP 서버(FakeChuteStateServer — ChuteStatePushTests에 정의, PUT /api/UpdateChuteState
// 수신)로 WCS가 보낸 수용상태 발신을 실제로 수신·카운트·payload 검증한다. 인메모리 GREEN을 PASS
// 근거로 삼지 않고 "가짜 RCS가 수신한 실제 JSON 본문"으로 입증(메타교훈).
//
// 발신 합성(SC-2): next_state 3 = 수용가능(accept) / 2 = 불가.
//   accept = Compute().Ready ∧ !Compute().Paused (슈트=비만재∧비정지 / 소터=운영 ready∧!paused,
//   셀 만재 SorterFull 제외).
//
// 검증 시나리오(계약 §Verification Scenarios):
//   PUSH6_7  VS-2/VS-9  부트스트랩(전 목적지 1회) + 와이어 형태(PUT·snake_case·단건 배열·{2,3})
//   PUSH1    VS-4       슈트 수용상태 전이(3→2→3) → 전이당 1건
//   PUSH2_3  VS-1       소터 분류 사이클(2→3→2) → 전이당 1건 + 무변화 폴 폭주 0
//   PUSH4    VS-7       동시 전이 → 전이당 1회 멱등(중복 0·누락 0)
//   PUSH5    VS-11      RCS 미도달 → 재시도 → 복구 후 최신값 도달(Fail-Loud)
//   PUSH8    VS-6       BaseUrl 미설정(DORMANT) → 발신 0·크래시 0·인바운드 정상
// ════════════════════════════════════════════════════════════════════════════

// ── 푸시 검증용 WebApplicationFactory (공유 픽스처) ───────────────────────────

/// <summary>
/// 목적지 수용상태 푸시 검증용 팩토리(공유 픽스처 — SorterCellFullnessTests·Field20CellsGateTests·
/// SorterPushOperationalTests가 재사용). 통합 발신 소스 DestinationStatusPusher를 **활성** 유지하고
/// Wcs:ChuteStatePush:BaseUrl을 가짜 RCS로 설정한다(생성자 인자).
/// 소터 수용상태 전이는 FakeMaster 레지스터 조작 → 폴링 스냅샷 변화로 유도.
/// </summary>
public sealed class RcsPushWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public FakeModbusMasterForApi FakeMaster { get; } = new();

    private readonly string? _rcsBaseUrl;
    private readonly int     _retryCount;
    private readonly int     _retryBaseDelayMs;
    // C3 항목2: 기본 등록 뒤 테스트별 서비스 오버라이드 훅(예: IDestinationStatusService 카운팅 데코레이터).
    // 기본 null → 기존 호출자 동작 불변. ConfigureServices 말미에 base 등록 이후 1회 적용.
    private readonly Action<IServiceCollection>? _configureExtra;

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

    // 인스턴스별 고유 DB 이름 — RcsPushWebApplicationFactory는 테스트마다 새로 생성되므로
    // static이면 병렬 테스트 클래스가 같은 in-memory DB를 공유해 시드 충돌(UNIQUE/table exists).
    // 인스턴스 필드로 각 팩토리가 독립 DB를 갖게 한다.
    private readonly string _dbName = $"WcsPushTest_{Guid.NewGuid():N}";
    private readonly Microsoft.Data.Sqlite.SqliteConnection _anchorConnection;

    public long SorterDestinationId { get; private set; }
    public int  SorterChuteNo       { get; private set; }

    public RcsPushWebApplicationFactory(
        string? rcsBaseUrl, int retryCount = 3, int retryBaseDelayMs = 50,
        Action<IServiceCollection>? configureExtra = null)
    {
        _rcsBaseUrl       = rcsBaseUrl;
        _retryCount       = retryCount;
        _retryBaseDelayMs = retryBaseDelayMs;
        _configureExtra   = configureExtra;

        _anchorConnection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={_dbName};Mode=Memory;Cache=Shared");
        _anchorConnection.Open();

        _fakePolling   = new PlcPollingService(_fakeGwOpt, _fakeWriteQueue, FakeMaster);
        _fakeHandshake = new HandshakeOrchestrator(_fakePolling, _fakeGwOpt);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // base appsettings=SqlServer → 테스트 더블은 인메모리 SQLite. host setting으로
        // Provider=Sqlite를 Program의 builder.Configuration 읽기 전에 주입해 Program이 SQLite 분기로
        // 등록(EF SqlServer provider 미등록 → "Only a single database provider" 충돌 회피).
        // DbContext connection은 아래 ConfigureServices가 named in-memory(anchor)로 재등록.
        builder.UseSetting("Database:Provider", "Sqlite");

        // ── ChuteStatePush 설정 주입(확정 와이어 — BaseUrl·재시도·소터 관찰주기, 하드코딩 0) ────
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            var dict = new Dictionary<string, string?>
            {
                ["Wcs:ChuteStatePush:BaseUrl"]                 = _rcsBaseUrl,
                ["Wcs:ChuteStatePush:RetryCount"]              = _retryCount.ToString(),
                ["Wcs:ChuteStatePush:RetryBaseDelayMs"]        = _retryBaseDelayMs.ToString(),
                ["Wcs:ChuteStatePush:RetryMaxDelayMs"]         = (_retryBaseDelayMs * 4).ToString(),
                ["Wcs:ChuteStatePush:HttpTimeoutMs"]           = "2000",
                ["Wcs:ChuteStatePush:SorterObserveIntervalMs"] = "30",
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

            // 통합 발신 소스(DestinationStatusPusher) IHostedService 재등록(발신 활성 유지).
            //   → 가짜 RCS 수신 검증을 위해 반드시 살아 있어야 한다.
            services.AddSingleton<IHostedService>(sp =>
                sp.GetRequiredService<DestinationStatusPusher>());

            // C3: 테스트별 서비스 오버라이드(base 등록 이후 — 예: IDestinationStatusService 데코레이터). 기본 no-op.
            _configureExtra?.Invoke(services);
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
// PUSH 테스트 (확정 와이어 UpdateChuteState — 가짜 RCS 수신 본문 입증)
// ════════════════════════════════════════════════════════════════════════════

// [Collection("RealSimSerial")] — push-결정성(정확 push 카운트) 테스트를 무거운 실-Sim 테스트와 동일
//   직렬 컬렉션에 편입(병렬 CPU 경합 flake 제거 — S-TWO-FLOOR-CONTROL A flake-fix, SorterPushOperationalTests 동형).
[Collection("RealSimSerial")]
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
    // PUSH6_7 (VS-2 + VS-9): 부트스트랩 초기 스냅샷 발신 + 와이어 형태 정합.
    // 기동 시 전 목적지(슈트 5 + PAUSED 슈트 1 + 소터 1)의 현재 수용상태를 1회 발신.
    // 와이어: PUT · snake_case chute_numbers/next_states · 단건 배열(길이 1) · 값 ∈ {2,3}.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task PUSH6_7_Bootstrap_InitialSnapshot_And_WireShape()
    {
        await using var rcs     = await FakeChuteStateServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        _ = factory.CreateClient();  // 호스트 기동(HostedService StartAsync 실행)

        // 부트스트랩: 활성 목적지 전부 1회 발신 — 슈트 1~5(chuteNo 1~5) + PAUSED(chuteNo 6) + 소터(chuteNo 30).
        // 각 chuteNo가 최종 수용상태로 정착할 때까지 대기(소터는 offline→online 전이로 push 수가 1~2 가변이라
        // 총 count가 아니라 목적지별 최종값으로 단언 — 인덕션 기반 2층 제어에서 소터는 online·Ready=1 → 3).
        int[] chuteNos = { 1, 2, 3, 4, 5, 6, factory.SorterChuteNo };
        await WaitUntilAsync(() => chuteNos.All(c => rcs.CountFor(c) >= 1), 8000, "부트스트랩 전 목적지 발신 수신");

        // 슈트 1~5 = NORMAL·비만재 → 3 / chuteNo 6 = PAUSED → 2 / 소터 30 = online·Ready=1 → 3(CurFloor 기준).
        await WaitUntilAsync(() => rcs.LastFor(1) is { Ready: true }, 5000, "슈트1 정착 = 3");
        await WaitUntilAsync(() => rcs.LastFor(6) is { Ready: false }, 5000, "슈트6(PAUSED) 정착 = 2");
        await WaitUntilAsync(() => rcs.LastFor(factory.SorterChuteNo) is { Ready: true }, 5000, "소터 정착 = 3(online·Ready=1)");

        // 정적 NORMAL 슈트(1)는 상태 변화가 없으므로 폴마다 폭주 0 — 정확히 1건 유지(폭주 회귀 방지).
        await WaitUntilExactAsync(() => rcs.CountFor(1), 1, stableCount: 6, timeoutMs: 4000, "슈트1 무변화 폴 폭주 0");

        var c1 = rcs.LastFor(1)!;
        Assert.Equal(new[] { 3 }, c1.NextStates);   // NORMAL 슈트 1 → 수용가능(3)
        Assert.Equal(1, rcs.CountFor(1));

        Assert.Equal(new[] { 2 }, rcs.LastFor(6)!.NextStates);   // PAUSED 슈트 6 → 불가(2)
        Assert.Equal(new[] { 3 }, rcs.LastFor(factory.SorterChuteNo)!.NextStates);   // 소터 online·Ready=1 → 3

        // ── 와이어 형태 정합(VS-9) ──────────────────────────────────────────────
        Assert.Equal("PUT", c1.Method);   // ★ PUT(positive).

        using var doc = JsonDocument.Parse(c1.RawBody);
        var props = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Equal(2, props.Count);
        Assert.Contains("chute_numbers", props);
        Assert.Contains("next_states",   props);
        Assert.DoesNotContain("chuteNumbers", props);   // camelCase 금지(계약 함정).
        Assert.DoesNotContain("nextStates",   props);
        Assert.DoesNotContain("chuteNo",   props);      // 폐지 와이어 키 부재.
        Assert.DoesNotContain("ready",     props);
        Assert.DoesNotContain("timeStamp", props);

        // 두 배열 동일 길이·인덱스 정렬·길이 1, 값 ∈ {2,3}.
        Assert.Single(c1.ChuteNumbers);
        Assert.Single(c1.NextStates);
        Assert.Equal(1, c1.ChuteNumbers[0]);
        Assert.Contains(c1.NextStates[0], new[] { 2, 3 });

        _out.WriteLine($"[PUSH6_7] 부트스트랩 전 목적지 수신(소터=3·PAUSED=2) · 와이어 PUT snake_case 단건 {{2,3}}: {c1.RawBody}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // PUSH1 (VS-4): 슈트 수용상태 전이(3→2→3) → 전이당 정확히 1건.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task PUSH1_Chute_AcceptTransition_OnePushPerTransition()
    {
        await using var rcs     = await FakeChuteStateServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        _ = factory.CreateClient();

        // 부트스트랩 정착(슈트 4 = NORMAL → next_state 3, 1건)
        await WaitUntilAsync(() => rcs.CountFor(4) >= 1, 8000, "부트스트랩 슈트4 수신");
        await WaitUntilExactAsync(() => rcs.CountFor(4), 1, stableCount: 5, timeoutMs: 4000, "부트스트랩 후 슈트4 안정");
        Assert.Equal(new[] { 3 }, rcs.LastFor(4)!.NextStates);

        using var scope = factory.Services.CreateScope();
        var db       = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        var capacity = scope.ServiceProvider.GetRequiredService<IChuteCapacityService>();
        var dest4    = db.Destinations.First(d => d.ChuteNo == 4 && d.DestType == Wcs.Data.DestType.CHUTE);
        var detail4  = db.ChuteDetails.First(cd => cd.DestinationId == dest4.Id);

        // 3→2: 만재 경계 통과(OnReserved qty=workFullQty)
        capacity.OnReserved(dest4.Id, detail4.WorkFullQty);
        await WaitUntilAsync(() => rcs.CountFor(4) >= 2, 5000, "슈트4 3→2 발신");
        await WaitUntilExactAsync(() => rcs.CountFor(4), 2, stableCount: 5, timeoutMs: 4000, "3→2 후 안정(중복 0)");
        Assert.Equal(new[] { 2 }, rcs.LastFor(4)!.NextStates);   // 만재 → 수용불가(2)

        // 2→3: 비움(OnCleared)
        await capacity.OnCleared(dest4.Id, "test-op");
        await WaitUntilAsync(() => rcs.CountFor(4) >= 3, 5000, "슈트4 2→3 발신");
        await WaitUntilExactAsync(() => rcs.CountFor(4), 3, stableCount: 5, timeoutMs: 4000, "2→3 후 안정");
        Assert.Equal(new[] { 3 }, rcs.LastFor(4)!.NextStates);   // 비움 → 수용가능(3)

        // 총 3건(부트스트랩 1 + 전이 2). 전이당 정확히 1건.
        Assert.Equal(3, rcs.CountFor(4));
        _out.WriteLine($"[PUSH1] 슈트4 전이당 1건 — 총 {rcs.CountFor(4)}건(부트1+전이2)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // PUSH2_3 (VS-1): 소터 분류 사이클(2→3→2) + 무변화 폴 폭주 0.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task PUSH2_3_Sorter_SortingCycle_OnePush_NoFloodOnUnchangedPolls()
    {
        await using var rcs     = await FakeChuteStateServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        _ = factory.CreateClient();

        int sorterChute = factory.SorterChuteNo;

        // 부트스트랩: 소터 online·Ready=1 → 운영 ready=true(next_state 3, CurFloor 기준 — 인덕션 기반 2층 제어).
        //   offline→online 전이 흡수(부트스트랩 push 수 1~2 가변) → "ready=true 정착 + baseline" 방식으로 견고화.
        await WaitUntilAsync(() => rcs.LastFor(sorterChute) is { Ready: true }, 8000, "부트스트랩 소터 ready=true(3)");
        int baseline = rcs.CountFor(sorterChute);
        await WaitUntilExactAsync(() => rcs.CountFor(sorterChute), baseline, stableCount: 6, timeoutMs: 4000,
            "무변화 폴 다수에도 소터 발신 폭주 0");
        Assert.Equal(new[] { 3 }, rcs.LastFor(sorterChute)!.NextStates);

        // 3→2: 분류 시작(Ready 1→0 = busy) → next_state 2
        factory.FakeMaster.SetReady(false);
        await WaitForSnapshotAsync(factory, s => s.Online && !s.Ready, 5000);
        await WaitUntilAsync(() => rcs.CountFor(sorterChute) >= baseline + 1, 5000, "소터 3→2 발신");
        await WaitUntilExactAsync(() => rcs.CountFor(sorterChute), baseline + 1, stableCount: 6, timeoutMs: 4000,
            "3→2 후 무변화 폴 다수에도 폭주 0(핵심)");
        Assert.Equal(new[] { 2 }, rcs.LastFor(sorterChute)!.NextStates);   // 분류 시작 → 2

        // 2→3: 분류 완료(Ready 0→1) → next_state 3
        factory.FakeMaster.SetReady(true);
        await WaitForSnapshotAsync(factory, s => s.Online && s.Ready, 5000);
        await WaitUntilAsync(() => rcs.CountFor(sorterChute) >= baseline + 2, 5000, "소터 2→3 발신");
        await WaitUntilExactAsync(() => rcs.CountFor(sorterChute), baseline + 2, stableCount: 6, timeoutMs: 4000,
            "2→3 후 안정(중복 0)");
        Assert.Equal(new[] { 3 }, rcs.LastFor(sorterChute)!.NextStates);   // 분류 완료 → 3

        // 전이당 정확히 1건(폭주 0). 총 = baseline + 2.
        Assert.Equal(baseline + 2, rcs.CountFor(sorterChute));
        _out.WriteLine($"[PUSH2_3] 소터 분류 사이클(Ready 1↔0) 전이당 1건·폭주 0 — 총 {rcs.CountFor(sorterChute)}건");
    }

    // ════════════════════════════════════════════════════════════════════════
    // PUSH4 (VS-7): 동시 전이 → 전이당 1회 멱등(중복 0·누락 0).
    // 슈트 콜백(스레드 다수)이 동시에 같은 chuteNo를 갱신해도 한 전이에 정확히 1건.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task PUSH4_ConcurrentTransition_ExactlyOncePerTransition()
    {
        await using var rcs     = await FakeChuteStateServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        _ = factory.CreateClient();

        // 부트스트랩 정착(슈트 5 chuteNo 5 = NORMAL → next_state 3, 1건)
        await WaitUntilAsync(() => rcs.CountFor(5) >= 1, 8000, "부트스트랩 슈트5 수신");
        await WaitUntilExactAsync(() => rcs.CountFor(5), 1, stableCount: 5, timeoutMs: 4000, "부트스트랩 안정");

        using var scope = factory.Services.CreateScope();
        var db       = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        var capacity = scope.ServiceProvider.GetRequiredService<IChuteCapacityService>();
        var dest5    = db.Destinations.First(d => d.ChuteNo == 5 && d.DestType == Wcs.Data.DestType.CHUTE);
        var detail5  = db.ChuteDetails.First(cd => cd.DestinationId == dest5.Id);

        // 같은 chuteNo의 한 전이(3→2)를 다수 스레드가 동시에 통지(만재 도달 후 추가 예약 폭주).
        // 첫 전이만 1건, 이후 같은 값(2) 재산출은 0건이어야 함(동일 전이 중복 억제 + 동시 멱등).
        capacity.OnReserved(dest5.Id, detail5.WorkFullQty);  // 만재 진입 = 3→2 전이

        const int concurrency = 16;
        using var barrier = new Barrier(concurrency);
        var tasks = Enumerable.Range(0, concurrency).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            capacity.OnReserved(dest5.Id, 1);  // 이미 만재 → 값 여전히 2(전이 없음)
        })).ToArray();
        await Task.WhenAll(tasks);

        // 전이는 1회(3→2)뿐 — 부트스트랩 1 + 전이 1 = 정확히 2건. 동시 폭주에도 중복 0.
        await WaitUntilAsync(() => rcs.CountFor(5) >= 2, 5000, "슈트5 전이 1건 도달");
        await WaitUntilExactAsync(() => rcs.CountFor(5), 2, stableCount: 8, timeoutMs: 5000,
            "동시 16통지에도 전이당 정확히 1건(중복 0)");
        Assert.Equal(2, rcs.CountFor(5));
        Assert.Equal(new[] { 2 }, rcs.LastFor(5)!.NextStates);
        _out.WriteLine($"[PUSH4] 동시 {concurrency}통지 → 전이당 1건(총 {rcs.CountFor(5)}건=부트1+전이1)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // PUSH5 (VS-11): RCS 미도달 → 재시도 → 복구 후 최신값 도달(Fail-Loud).
    // 가짜 RCS를 거부(503)로 토글 → 전이 발생 → 재시도 소진(미알림 유지) →
    // 가짜 RCS 재개 → 재산출에서 최신 수용상태 도달.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task PUSH5_RcsUnreachable_Retry_RecoverAndPushLatest()
    {
        await using var rcs = await FakeChuteStateServer.StartAsync();
        // 재시도 2회·짧은 백오프(테스트 속도) — 설정 경유.
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl, retryCount: 2, retryBaseDelayMs: 30);
        _ = factory.CreateClient();

        // 부트스트랩 정착(슈트 3 chuteNo 3 = NORMAL → next_state 3, 1건)
        await WaitUntilAsync(() => rcs.CountFor(3) >= 1, 8000, "부트스트랩 슈트3 수신");
        await WaitUntilExactAsync(() => rcs.CountFor(3), 1, stableCount: 5, timeoutMs: 4000, "부트스트랩 안정");
        int baseline = rcs.CountFor(3);

        using var scope = factory.Services.CreateScope();
        var db       = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        var capacity = scope.ServiceProvider.GetRequiredService<IChuteCapacityService>();
        var dest3    = db.Destinations.First(d => d.ChuteNo == 3 && d.DestType == Wcs.Data.DestType.CHUTE);
        var detail3  = db.ChuteDetails.First(cd => cd.DestinationId == dest3.Id);

        // RCS 거부 모드 — 이후 발신은 재시도 소진(미도달·성공 delivery 0건).
        rcs.StartRejecting();

        // 3→2 전이 발생(만재) — 재시도 소진되어 성공 delivery 0건(거부 중).
        capacity.OnReserved(dest3.Id, detail3.WorkFullQty);

        // 재시도 소진까지 대기 후에도 성공 카운트는 baseline 유지(미알림 — 실패를 성공으로 간주 안 함).
        await Task.Delay(400);
        Assert.Equal(baseline, rcs.CountFor(3));
        _out.WriteLine($"[PUSH5] 거부 중 전이 → 성공 delivery {rcs.CountFor(3)}건(미알림 유지)");

        // RCS 복구 — 거부 해제.
        rcs.StopRejecting();

        // 복구 후 재푸시 유도: 같은 chuteNo에 추가 상태 변화 통지(여전히 만재 → next_state 2).
        // Acked가 미갱신(stale)이므로 Computed(2)≠Acked(3) → 재푸시 1건 도달.
        capacity.OnReservationCancelled(dest3.Id, 0);  // 상태 무변(만재 유지) → 재평가 트리거

        await WaitUntilAsync(() => rcs.CountFor(3) >= baseline + 1, 5000, "복구 후 재푸시 도달");
        await WaitUntilExactAsync(() => rcs.CountFor(3), baseline + 1, stableCount: 5, timeoutMs: 4000,
            "복구 재푸시 1건(최신 next_state 2)");
        Assert.Equal(new[] { 2 }, rcs.LastFor(3)!.NextStates);   // 복구 후 최신 수용불가(2) 도달
        _out.WriteLine($"[PUSH5] 복구 후 재푸시 도달 — 최신 next_states={string.Join(",", rcs.LastFor(3)!.NextStates)}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // PUSH8 (VS-6): BaseUrl 미설정(DORMANT) → 발신 비활성(크래시 0·수신 0·인바운드 정상).
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task PUSH8_NoBaseUrl_PushDisabled_NoCrash_NoReceive()
    {
        await using var rcs = await FakeChuteStateServer.StartAsync();
        // BaseUrl=null → 발신 비활성. 인바운드는 정상.
        await using var factory = new RcsPushWebApplicationFactory(rcsBaseUrl: null);
        var client = factory.CreateClient();  // 기동 크래시 없어야 함

        // 인바운드 IF-05 정상 동작(회귀 0 — 발신 비활성이 인바운드를 막지 않음)
        var req  = new { pId = 20001, agvNo = 1, barcode = "TEST-BARCODE-1", inductionNo = 1, qty = 1, timeStamp = (string?)null };
        var resp = await client.PostAsJsonAsync("/api/v1/destination-query", req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // 충분히 대기해도 가짜 RCS 수신 0건(발신 비활성).
        await Task.Delay(500);
        Assert.Empty(rcs.All);
        _out.WriteLine($"[PUSH8] BaseUrl 미설정(DORMANT) → 발신 비활성. 수신 {rcs.All.Count}건, IF-05 정상(200)");
    }
}
