using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wcs.Api;
using Wcs.Api.Monitoring;
using Wcs.Data;
using Wcs.PlcGateway;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// F2 SignalR 허브 통합 테스트 (WcsMonitorHub + MonitorRelayService) + operation-log REST.
//
// TestServer는 WebSocket 협상을 그대로 못 하므로(함정 §5-4) HubConnection을 TestServer
// HttpMessageHandler로 결선하고 **LongPolling 트랜스포트**로 대체한다.
//
// 팩토리는 MonitoringWebApplicationFactory와 동일한 인스턴스-고유 in-memory SQLite + Fake 소터
// 배선을 쓰되, relay·operation_log 컨슈머 IHostedService를 **유지**한다(그 팩토리는 null-impl
// hosted를 전부 제거하므로 relay가 안 돎 — 여기선 명시 재등록).
//
// ⚠ 직렬 컬렉션(DisableParallelization): 각 테스트가 실 호스트 + LongPolling + 폴 루프를 띄우는
//   무거운 통합이라, xUnit 기본 병렬로 무거운 E2E 스위트와 동시 실행되면 상호 부하로 기존
//   타이밍-취약 테스트(S9·IT4b)의 저빈도 flake를 발현시킬 수 있다(계약 함정 §5-4·E2E 부하 교훈).
//   이 컬렉션을 비병렬로 격리해 부하 기여를 제거하고 허브 테스트 자체 결정성도 확보한다.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>허브 통합 테스트 비병렬 컬렉션 — 무거운 실 호스트·LongPolling 부하 격리(계약 §5-4).</summary>
[CollectionDefinition("MonitorHubSerial", DisableParallelization = true)]
public sealed class MonitorHubSerialCollection { }

[Collection("MonitorHubSerial")]
public sealed class MonitorHubTests
{
    private readonly ITestOutputHelper _out;
    public MonitorHubTests(ITestOutputHelper output) => _out = output;

    private static HubConnection BuildConnection(HubWebApplicationFactory f)
    {
        // 호스트 기동(hosted services StartAsync — relay 구독·폴링·oplog 컨슈머).
        _ = f.CreateClient();
        return new HubConnectionBuilder()
            .WithUrl(new Uri(f.Server.BaseAddress, "hubs/monitor"), o =>
            {
                o.HttpMessageHandlerFactory = _ => f.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling; // TestServer websocket 한계 회피.
            })
            .Build();
    }

    // ── 부트스트랩: 접속 → AllBundles Latest 전체 스냅샷 1회 수신 ────────────────
    [Fact]
    public async Task Bootstrap_OnConnect_ReceivesSorterSnapshot()
    {
        await using var f = new HubWebApplicationFactory();
        await using var conn = BuildConnection(f);

        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.On<JsonElement>("Bootstrap", el => tcs.TrySetResult(el));

        await conn.StartAsync();
        var boot = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(JsonValueKind.Array, boot.ValueKind);
        Assert.True(boot.GetArrayLength() >= 1, "부트스트랩에 시드 소터(chuteNo=30) 1대 이상 기대");
        var first = boot[0];
        Assert.True(first.TryGetProperty("chuteNo", out _), "워드 스냅샷에 chuteNo 필드 존재");
        Assert.True(first.TryGetProperty("curFloor", out _), "워드 스냅샷에 D5 curFloor 필드 존재");
        Assert.True(first.TryGetProperty("tgtFloor", out _), "워드 스냅샷에 D6 tgtFloor 필드 존재");
        _out.WriteLine($"[Bootstrap] sorters={boot.GetArrayLength()} chuteNo={first.GetProperty("chuteNo").GetInt32()}");
    }

    // ── 델타: 레지스터 변화 → 관측 훅 발화 → 허브 브로드캐스트 → 클라이언트 수신 ──
    [Fact]
    public async Task RegisterChange_Pushes_RegisterDelta()
    {
        await using var f = new HubWebApplicationFactory();
        await using var conn = BuildConnection(f);

        var bootTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.On<JsonElement>("Bootstrap", el => bootTcs.TrySetResult(el));
        await conn.StartAsync();
        var boot = await bootTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var destId = boot[0].GetProperty("destId").GetInt64();

        // ⚠ 폴링이 baseline 스냅샷(Online·CurFloor)을 확립하기 전에 레지스터를 바꾸면
        //   baseline 자체가 새 값이라 "변화분"이 관측되지 않는다(부하 시 노출된 함정).
        //   → Online baseline 확립 후 현재 CurFloor를 읽고, 그와 다른 값으로 바꿔 델타를 강제한다.
        var registry = f.Services.GetRequiredService<ISorterGatewayRegistry>();
        int baseFloor = await WaitForOnlineFloorAsync(registry, destId, TimeSpan.FromSeconds(15));
        int target = baseFloor + 5;

        var deltaTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.On<JsonElement>("RegisterDelta", el =>
        {
            if (el.GetProperty("reg").GetString() == "CurFloor" &&
                el.GetProperty("newValue").GetInt32() == target)
                deltaTcs.TrySetResult(el);
        });

        // CurFloor baseFloor→target (관측 훅 OnRegisterChange 발화 유발).
        f.FakeMaster.SetCurFloor(target);

        var delta = await deltaTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("CurFloor", delta.GetProperty("reg").GetString());
        Assert.Equal(target, delta.GetProperty("newValue").GetInt32());
        Assert.Equal(baseFloor, delta.GetProperty("oldValue").GetInt32());
        Assert.True(delta.TryGetProperty("chuteNo", out _));
        _out.WriteLine($"[Delta] reg=CurFloor {baseFloor}->{target} chuteNo={delta.GetProperty("chuteNo").GetInt32()}");
    }

    // 폴링이 첫 성공 폴로 Online baseline을 확립할 때까지 대기 후 현재 CurFloor 반환.
    private static async Task<int> WaitForOnlineFloorAsync(
        ISorterGatewayRegistry registry, long destId, TimeSpan timeout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var snap = registry.GetLatest(destId);
            if (snap is { Online: true }) return snap.CurFloor;
            await Task.Delay(100);
        }
        throw new TimeoutException("폴링 Online baseline 미확립 — 테스트 사전조건 실패");
    }

    // ── oplog 테일: API 엔트리 수신 + POLL_CHANGE 기본 미포함(옵트아웃) ──────────
    [Fact]
    public async Task OperationLog_Tail_ReceivesApiEntry_ButNotPollChangeByDefault()
    {
        await using var f = new HubWebApplicationFactory();
        await using var conn = BuildConnection(f);

        var apiTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pollReceived = false;
        conn.On<JsonElement>("OpLog", el =>
        {
            var cat = el.GetProperty("category").GetString();
            if (cat == "POLL_CHANGE") pollReceived = true;
            if (cat == "API" && el.GetProperty("action").GetString() == "IF05_HUBTEST")
                apiTcs.TrySetResult(el);
        });

        var bootTcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.On<JsonElement>("Bootstrap", el => bootTcs.TrySetResult(el));
        await conn.StartAsync();
        await bootTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

        var logger = f.Services.GetRequiredService<IOperationLogger>();
        // POLL_CHANGE 먼저(옵트아웃이면 미전달) → API(기본 스트림 전달). 순서상 API 도착 시 POLL_CHANGE도 도착했어야.
        logger.Log(OperationLogCategory.POLL_CHANGE, "REG_CHANGE", detail: "{\"reg\":\"CurFloor\",\"old\":1,\"new\":2}");
        logger.Log(OperationLogCategory.API, "IF05_HUBTEST", detail: "{\"probe\":true}");

        var api = await apiTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal("API", api.GetProperty("category").GetString());
        Assert.False(pollReceived, "POLL_CHANGE는 기본 스트림(oplog)에서 제외돼야 함(옵트인 전용)");
        _out.WriteLine($"[OpLog] API 수신·POLL_CHANGE 미수신(기본 옵트아웃) 확인");
    }

    // ── negotiate: catch-all/fallback이 /hubs/monitor를 삼키지 않음(404 아님·검증 ⑥) ──
    [Fact]
    public async Task HubNegotiate_NotSwallowedByCatchAll()
    {
        await using var f = new HubWebApplicationFactory();
        var client = f.CreateClient();

        var resp = await client.PostAsync("/hubs/monitor/negotiate?negotiateVersion=1", null);
        Assert.NotEqual(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("connectionId", body); // negotiate 응답 형상
        _out.WriteLine($"[negotiate] /hubs/monitor/negotiate → {(int)resp.StatusCode} (catch-all 미삼킴)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // operation-log REST 엔드포인트 (형상·페이징·필터·POLL_CHANGE 기본 제외)
    // 데이터는 WcsDbContext에 직접 삽입(결정적) — 컨슈머 flush 타이밍 비의존.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task OperationLogRest_ShapeFilterAndPaging()
    {
        await using var f = new HubWebApplicationFactory();
        var client = f.CreateClient();

        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            var t0 = DateTime.UtcNow.AddMinutes(-10);
            db.OperationLogs.AddRange(
                NewLog(t0.AddSeconds(1), OperationLogCategory.API, "IF05_REQ", OperationLogLevel.INFO, 1),
                NewLog(t0.AddSeconds(2), OperationLogCategory.HANDSHAKE, "C_WRITE", OperationLogLevel.INFO, 30),
                NewLog(t0.AddSeconds(3), OperationLogCategory.STATE, "OFFLINE", OperationLogLevel.ERROR, 30),
                NewLog(t0.AddSeconds(4), OperationLogCategory.POLL_CHANGE, "REG_CHANGE", OperationLogLevel.INFO, 30),
                NewLog(t0.AddSeconds(5), OperationLogCategory.POLL_CHANGE, "REG_CHANGE", OperationLogLevel.INFO, 30),
                NewLog(t0.AddSeconds(6), OperationLogCategory.API, "IF10_REQ", OperationLogLevel.INFO, 1));
            db.SaveChanges();
        }

        // 기본(카테고리 미지정) — POLL_CHANGE 제외 확인.
        var def = await client.GetFromJsonAsync<PagedResult<OperationLogDto>>("/api/monitor/operation-log?take=50");
        Assert.NotNull(def);
        Assert.DoesNotContain(def!.Items, x => x.Category == "POLL_CHANGE");
        Assert.Equal(4, def.Items.Count); // API×2 + HANDSHAKE + STATE (POLL_CHANGE 2건 제외)
        // 최신순(Id desc): 마지막 삽입 IF10_REQ가 선두.
        Assert.Equal("IF10_REQ", def.Items[0].Action);

        // category=POLL_CHANGE 명시 옵트인 → POLL_CHANGE만.
        var poll = await client.GetFromJsonAsync<PagedResult<OperationLogDto>>("/api/monitor/operation-log?category=POLL_CHANGE&take=50");
        Assert.Equal(2, poll!.Items.Count);
        Assert.All(poll.Items, x => Assert.Equal("POLL_CHANGE", x.Category));

        // level=ERROR 필터.
        var err = await client.GetFromJsonAsync<PagedResult<OperationLogDto>>("/api/monitor/operation-log?level=ERROR&take=50");
        Assert.Single(err!.Items);
        Assert.Equal("OFFLINE", err.Items[0].Action);

        // sorterChuteNo=1 필터(API 2건).
        var byChute = await client.GetFromJsonAsync<PagedResult<OperationLogDto>>("/api/monitor/operation-log?sorterChuteNo=1&take=50");
        Assert.Equal(2, byChute!.Items.Count);
        Assert.All(byChute.Items, x => Assert.Equal(1, x.SorterChuteNo));

        // 키셋 커서 페이징(take=2).
        var p1 = await client.GetFromJsonAsync<PagedResult<OperationLogDto>>("/api/monitor/operation-log?take=2");
        Assert.Equal(2, p1!.Items.Count);
        Assert.NotNull(p1.NextCursor);
        var p2 = await client.GetFromJsonAsync<PagedResult<OperationLogDto>>($"/api/monitor/operation-log?take=2&cursor={p1.NextCursor}");
        Assert.True(p2!.Items.Count >= 1);
        Assert.True(p2.Items[0].Id < p1.Items[^1].Id, "커서 이후 Id는 이전 페이지보다 작아야(최신순)");

        // 잘못된 카테고리 → 빈 결과(500 아님·일관).
        var bogus = await client.GetFromJsonAsync<PagedResult<OperationLogDto>>("/api/monitor/operation-log?category=BOGUS");
        Assert.Empty(bogus!.Items);

        _out.WriteLine($"[oplog REST] 기본 {def.Items.Count}(POLL 제외)·POLL {poll.Items.Count}·ERROR {err.Items.Count}·chute1 {byChute.Items.Count}·페이징 OK");
    }

    private static OperationLog NewLog(
        DateTime at, OperationLogCategory cat, string action, OperationLogLevel level, int chuteNo) => new()
    {
        At = at, Category = cat, Action = action, Level = level, SorterChuteNo = chuteNo,
    };
}

// ════════════════════════════════════════════════════════════════════════════
// HubWebApplicationFactory — 인스턴스-고유 in-memory SQLite + Fake 소터 + relay·oplog 유지.
// (MonitoringWebApplicationFactory와 동일 배선이나 relay·OperationLogService IHostedService를
//  명시 재등록해 실시간 push 경로를 살린다. 공개 헬퍼 재사용.)
// ════════════════════════════════════════════════════════════════════════════
public sealed class HubWebApplicationFactory : WebApplicationFactory<Program>
{
    public FakeModbusMasterForApi FakeMaster { get; } = new();

    private readonly string _dbName = $"HubTest_{Guid.NewGuid():N}";
    private readonly Microsoft.Data.Sqlite.SqliteConnection _anchor;
    private readonly PlcWriteQueue     _writeQueue = new();
    private readonly PlcGatewayOptions _gwOpt = new()
    {
        Host = "127.0.0.1", Port = 1502,
        PollIntervalMs = 150, OfflineAfterFailures = 3, WriteTimeoutMs = 1000,
        RFlagPollMs = 100, RFlagTimeoutMs = 30000, CFlagTimeoutMs = 5000,
    };
    private readonly PlcPollingService     _polling;
    private readonly HandshakeOrchestrator _handshake;

    public HubWebApplicationFactory()
    {
        _anchor = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbName};Mode=Memory;Cache=Shared");
        _anchor.Open();
        _polling   = new PlcPollingService(_gwOpt, _writeQueue, FakeMaster);
        _handshake = new HandshakeOrchestrator(_polling, _gwOpt);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Database:Provider", "Sqlite");

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
                .UseSqlite(_anchor)
                .ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
                .Options;
            using var db = new WcsDbContext(dbOpts);
            db.Database.EnsureCreated();
            DbSeeder.Seed(db, new Dictionary<string, int> { ["1"] = 1, ["2"] = 2 });

            var sorterDest = db.Destinations.First(d => d.DestType == DestType.SORTER_3D && d.IsActive);
            var bundle = new SorterBundleHandle(
                destinationId: sorterDest.Id, chuteNo: sorterDest.ChuteNo,
                polling: _polling, handshake: _handshake);
            var registry = new FakeSorterGatewayRegistry(bundle);

            // SorterRegistryFactory + ISorterGatewayRegistry + null-impl hosted 제거.
            var srfToRemove = services
                .Where(d => d.ServiceType == typeof(SorterRegistryFactory)
                         || d.ServiceType == typeof(ISorterGatewayRegistry))
                .ToList();
            foreach (var d in srfToRemove) services.Remove(d);

            var nullHosted = services
                .Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == null)
                .ToList();
            foreach (var d in nullHosted) services.Remove(d);

            // 필요한 hosted service 재등록: ChuteCapacity·OperationLog 컨슈머·Nop 레지스트리·relay.
            services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ChuteCapacityService>());
            services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<OperationLogService>());

            var nop = new NopSorterRegistryFactory(_polling, registry);
            services.AddSingleton<ISorterGatewayRegistry>(nop);
            services.AddSingleton<IHostedService>(nop);

            // relay는 nop(레지스트리) 등록 이후에 등록 — StartAsync가 나중에 돌아 AllBundles 유효.
            services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<MonitorRelayService>());
        });
    }

    public override async ValueTask DisposeAsync()
    {
        _writeQueue.Writer.TryComplete();
        await base.DisposeAsync().ConfigureAwait(false);
        _anchor.Dispose();
        GC.SuppressFinalize(this);
    }
}
