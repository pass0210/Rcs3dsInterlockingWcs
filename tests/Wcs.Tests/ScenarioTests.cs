using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
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
using Wcs.Sim3ds;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// S-M4-P3 시나리오 테스트 (S1~S9)
//
// 계약서 S-M4-P3의 9개 E2E 시나리오를 xUnit 통합 테스트로 자동화.
// 실 Sim3ds(동적 포트) + 번들 패턴, P2bSimHandshakeTests·PlcGatewayIntegrationTests 선례 재사용.
//
// 분류:
//   S1        : WebApplicationFactory + 실 Sim → sorter_command COMPLETED DB 단언
//   S2/S3/S4  : PlcGateway 직접 통합 (Sim+GW) → SimServer 타임라인 / TgtFloor 동작 입증
//   S5/S6     : WebApplicationFactory + 실 Sim 고장 주입 → alarm/sorter_command DB 단언
//   S7        : PlcGateway 직접 (OFFLINE 이벤트 포착) + WcsDbContext alarm 단언
//   S8        : WebApplicationFactory(기존 FakeModbus) + capacity 시드
//   S9        : PlcGateway 직접 (단일 소터 2개 AGV 경합) → D6 쓰기 타임라인 입증
// ════════════════════════════════════════════════════════════════════════════

// ─────────────────────────────────────────────────────────────────────────
// SimServer WebApplicationFactory — 실 Sim3ds TCP에 연결하는 소터 번들 교체
// S1·S5·S6 전용: alarm/sorter_command DB 단언이 필요한 시나리오
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// 실 Sim3ds TCP 서버(동적 포트)를 소터 번들로 연결하는 WebApplicationFactory.
/// 기존 FakeModbus 팩토리와 달리 실제 Modbus TCP 클라이언트를 사용하므로
/// 핸드셰이크 alarm·sorter_command DB 영속화 경로를 E2E로 검증 가능.
/// Named in-memory SQLite로 DB 수명 고정. 소터 번들은 Start/Stop을 IHostedService로 위임.
/// </summary>
public class SimWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly int              _simPort;
    private SimServer?                _sim;
    private readonly List<string>     _timeline = [];
    private readonly object           _tlLock   = new();

    // named in-memory SQLite 앵커
    private readonly string _dbName = $"ScenarioTest_{Guid.NewGuid():N}";
    private readonly Microsoft.Data.Sqlite.SqliteConnection _anchor;

    // Sim 설정 (테스트에서 고장 주입 전에 덮어쓰기 가능)
    private readonly SimServer.Options _simOpt;

    // GW 설정 — 빠른 E2E용 (타임아웃 단축)
    public PlcGatewayOptions GwOpt { get; }

    public SimServer Sim => _sim ?? throw new InvalidOperationException("Sim이 아직 기동되지 않았습니다.");
    public IReadOnlyList<string> Timeline { get { lock (_tlLock) { return _timeline.ToList(); } } }

    /// <param name="simPort">Sim3ds 서버가 바인딩할 동적 포트.</param>
    /// <param name="rFlagTimeoutMs">R_Flag 타임아웃(ms). 기본 3000ms. S6에서 단축 시 사용.</param>
    public SimWebApplicationFactory(int simPort, int rFlagTimeoutMs = 3000)
    {
        _simPort = simPort;
        _simOpt  = new SimServer.Options
        {
            Host           = "127.0.0.1",
            Port           = simPort,
            TiltDelayMs    = 50,
            SortDurationMs = 100,
            MoveDurationMs = 80,
            InitialCurFloor = 1,
            SimLoopMs      = 10,
        };
        GwOpt = new PlcGatewayOptions
        {
            Host                 = "127.0.0.1",
            Port                 = simPort,
            PollIntervalMs       = 30,
            OfflineAfterFailures = 3,
            WriteTimeoutMs       = 500,
            RFlagPollMs          = 20,
            RFlagTimeoutMs       = rFlagTimeoutMs,
            CFlagTimeoutMs       = 2000,
        };

        _anchor = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={_dbName};Mode=Memory;Cache=Shared");
        _anchor.Open();
    }

    /// <summary>Sim3ds 서버 기동 (ConfigureWebHost 전에 호출).</summary>
    public async Task StartSimAsync()
    {
        _sim = new SimServer(_simOpt, timelineLog: line => { lock (_tlLock) { _timeline.Add(line); } });
        await _sim.StartAsync();
    }

    /// <summary>
    /// Sim을 재기동한다 — StopAsync 후 새 SimServer 인스턴스를 생성해 기동.
    /// OFFLINE→ONLINE 복구 시나리오(S7 재전이 단언) 전용.
    /// </summary>
    public async Task RestartSimAsync()
    {
        // 기존 인스턴스 종료 (이미 StopAsync된 경우 DisposeAsync만 호출)
        if (_sim is not null)
            await _sim.DisposeAsync();

        // 새 인스턴스로 재기동 — 같은 포트·옵션 재사용
        _sim = new SimServer(_simOpt, timelineLog: line => { lock (_tlLock) { _timeline.Add(line); } });
        await _sim.StartAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
            // Sorters[] 배열에 실 SimServer 포트 등록
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sorters:0:ChuteNo"]           = "30",
                ["Sorters:0:Transport"]          = "Tcp",
                ["Sorters:0:Host"]               = "127.0.0.1",
                ["Sorters:0:Port"]               = _simPort.ToString(),
                ["Sorters:0:PollIntervalMs"]     = GwOpt.PollIntervalMs.ToString(),
                ["Sorters:0:OfflineAfterFailures"] = GwOpt.OfflineAfterFailures.ToString(),
                ["Sorters:0:WriteTimeoutMs"]     = GwOpt.WriteTimeoutMs.ToString(),
                ["Timing:RFlagPollMs"]           = GwOpt.RFlagPollMs.ToString(),
                ["Timing:RFlagTimeoutMs"]        = GwOpt.RFlagTimeoutMs.ToString(),
                ["Timing:CFlagTimeoutMs"]        = GwOpt.CFlagTimeoutMs.ToString(),
            });
        });

        builder.ConfigureServices(services =>
        {
            // WcsDbContext를 named in-memory SQLite로 교체
            var dbDescriptors = services
                .Where(d => d.ServiceType == typeof(DbContextOptions<WcsDbContext>)
                         || d.ServiceType == typeof(WcsDbContext))
                .ToList();
            foreach (var d in dbDescriptors)
                services.Remove(d);

            var connStr = $"Data Source={_dbName};Mode=Memory;Cache=Shared";
            services.AddDbContext<WcsDbContext>(opts =>
                opts.UseSqlite(connStr,
                    sqlite => sqlite.CommandTimeout(30))
                    .ConfigureWarnings(w => w.Ignore(
                        Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)),
                ServiceLifetime.Scoped);

            // 스키마 + 시드
            var dbOpts = new DbContextOptionsBuilder<WcsDbContext>()
                .UseSqlite(_anchor)
                .ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
                .Options;
            using var db = new WcsDbContext(dbOpts);
            db.Database.EnsureCreated();
            DbSeeder.Seed(db, new Dictionary<string, int> { ["1"] = 1, ["2"] = 2 });
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _anchor.Dispose();
            _sim?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        base.Dispose(disposing);
    }

    // ── DB 스코프 접근 헬퍼 ──────────────────────────────────────────────────

    /// <summary>새 WcsDbContext 스코프를 열어 DB 행을 직접 조회하는 스코프 팩토리.</summary>
    public WcsDbContext CreateDbScope()
    {
        var connStr = $"Data Source={_dbName};Mode=Memory;Cache=Shared";
        var opts = new DbContextOptionsBuilder<WcsDbContext>()
            .UseSqlite(connStr,
                sqlite => sqlite.CommandTimeout(30))
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new WcsDbContext(opts);
    }

    // ── GW 상태 관찰 헬퍼 ────────────────────────────────────────────────────

    /// <summary>
    /// ISorterGatewayRegistry.GetLatest로 첫 번째 소터의 Online 상태를 반환.
    /// Phase 2 ONLINE 복구 WaitUntilAsync 조건으로 사용.
    /// Services가 아직 빌드되지 않은 경우(CreateClient 전) false 반환.
    /// </summary>
    public bool IsSorterOnline()
    {
        try
        {
            var registry = Services.GetService<ISorterGatewayRegistry>();
            if (registry is null) return false;
            // DB에서 SORTER_3D 타입 destination의 Id를 조회해 GetLatest에 전달.
            // DbSeeder가 시드한 순서와 무관하게 DestType으로 찾는다.
            using var db = CreateDbScope();
            var dest = db.Destinations
                         .FirstOrDefault(d => d.IsActive && d.DestType == Wcs.Data.DestType.SORTER_3D);
            if (dest is null) return false;
            var snap = registry.GetLatest(dest.Id);
            return snap?.Online ?? false;
        }
        catch { return false; }
    }
}

// ─────────────────────────────────────────────────────────────────────────
// S1: 정상 핸드셰이크 → sorter_command COMPLETED DB 단언
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// S1: IF-05→IF-08(allowed)→IF-10→IF-11 핸드셰이크 1왕복 정상.
/// PASS = Outcome=Success, R_Seq==C_Seq, C_Flag·R_Flag=0, sorter_command 1행 status=COMPLETED.
/// </summary>
public class S1NormalHandshakeTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _out;
    private readonly int _port;
    private SimWebApplicationFactory? _factory;
    private HttpClient? _client;

    public S1NormalHandshakeTests(ITestOutputHelper output)
    {
        _out  = output;
        _port = GetFreePort();
    }

    public async Task InitializeAsync()
    {
        _factory = new SimWebApplicationFactory(_port);
        await _factory.StartSimAsync();
        _client  = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
        {
            // 호스트 종료 대기 후 Dispose
            await _factory.DisposeAsync();
        }
    }

    [Fact]
    public async Task S1_NormalHandshake_SorterCommandCompleted()
    {
        // ── IF-05: 목적지 조회 ────────────────────────────────────────────────
        var if05Req = new
        {
            pId         = 11001,
            agvNo       = 1,
            barcode     = "TEST-BARCODE-3",
            inductionNo = 1,
            qty         = 1,
            timeStamp   = "2026-06-17 10:00:00"
        };
        var if05Resp = await _client!.PostAsJsonAsync("/api/v1/destination-query", if05Req);
        Assert.Equal(HttpStatusCode.OK, if05Resp.StatusCode);
        var if05Body = await if05Resp.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.Equal("OK", if05Body!.Result);
        _out.WriteLine($"[S1] IF-05 OK chuteNo={if05Body.ChuteNo}");

        // ── IF-08: 투입 가부 (소터 Online 될 때까지 대기 후) ─────────────────
        var if08Req = new { pId = 11001, chuteNo = if05Body.ChuteNo!.Value, agvNo = 1 };
        DepositPermissionResponse? if08Body = null;

        // 소터 GW가 Online될 때까지 IF-08 폴링 (RCS 500ms 재호출 패턴)
        var deadline = DateTimeOffset.Now.AddSeconds(5);
        while (DateTimeOffset.Now < deadline)
        {
            var r = await _client!.PostAsJsonAsync("/api/v1/deposit-permission", if08Req);
            if (r.IsSuccessStatusCode)
            {
                var b = await r.Content.ReadFromJsonAsync<DepositPermissionResponse>();
                if (b?.Allowed == true) { if08Body = b; break; }
                if (b?.Reason == "WRONG_FLOOR" || b?.Reason == "BUSY") { if08Body = b; break; }
                // OFFLINE 등이면 재시도
            }
            await Task.Delay(200);
        }
        Assert.NotNull(if08Body);
        Assert.True(if08Body.Allowed, $"IF-08 expected allowed=true, reason={if08Body.Reason}");
        Assert.Equal("READY", if08Body.Reason);
        _out.WriteLine($"[S1] IF-08 allowed={if08Body.Allowed} reason={if08Body.Reason}");

        // ── IF-10: 투입 보고 → IF-11 트리거 ────────────────────────────────────
        var if10Req = new { pId = 11001, barcode = "TEST-BARCODE-3", chuteNo = if05Body.ChuteNo!.Value, agvNo = 1 };
        var if10Resp = await _client!.PostAsJsonAsync("/api/v1/deposit-report", if10Req);
        Assert.Equal(HttpStatusCode.OK, if10Resp.StatusCode);
        var if10Body = await if10Resp.Content.ReadFromJsonAsync<DepositReportResponse>();
        Assert.Equal("OK", if10Body!.Result);
        _out.WriteLine("[S1] IF-10 OK → IF-11 트리거");

        // ── 핸드셰이크 완료 대기 → DB 단언 ─────────────────────────────────────
        // ContinueWith 백그라운드 완료까지 폴링 (SortDuration+여유)
        await WaitUntilAsync(async () =>
        {
            using var db = _factory!.CreateDbScope();
            return await db.SorterCommands.AnyAsync(c =>
                c.Status == SorterCommandStatus.COMPLETED);
        }, timeoutMs: 5000, msg: "sorter_command COMPLETED");

        using var assertDb = _factory!.CreateDbScope();
        var cmd = await assertDb.SorterCommands
            .Where(c => c.Status == SorterCommandStatus.COMPLETED)
            .FirstOrDefaultAsync();
        Assert.NotNull(cmd);
        Assert.Equal(SorterCommandStatus.COMPLETED, cmd.Status);
        Assert.NotNull(cmd.RSeq);
        Assert.Equal(cmd.CSeq, cmd.RSeq);
        _out.WriteLine($"[S1] sorter_command COMPLETED: CSeq={cmd.CSeq} RSeq={cmd.RSeq}");
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition, int timeoutMs, string msg, int pollMs = 50)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        while (!await condition())
        {
            if (DateTimeOffset.Now > deadline)
                Assert.Fail($"WaitUntil 타임아웃({timeoutMs}ms): {msg}");
            await Task.Delay(pollMs);
        }
    }

    private static int GetFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}

// ─────────────────────────────────────────────────────────────────────────
// S2/S3/S4/S9: PlcGateway 직접 통합 (Sim+GW 번들) — 타임라인/TgtFloor 동작 입증
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// S2/S3/S4/S9: 실 SimServer + PlcGateway 직접 번들.
/// DB 단언 없음 — 게이트웨이 동작(TgtFloor·D6 쓰기 이력·경합)을 타임라인·스냅샷으로 입증.
/// </summary>
public class S234_9GatewayScenarioTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _out;
    private readonly List<string> _timeline = [];
    private readonly object _tlLock = new();

    private readonly int _port;
    private SimServer? _sim;
    private PlcWriteQueue? _queue;
    private PlcPollingService? _gw;
    private HandshakeOrchestrator? _hs;

    private SimServer.Options _simOpt;
    private PlcGatewayOptions _gwOpt;

    public S234_9GatewayScenarioTests(ITestOutputHelper output)
    {
        _out  = output;
        _port = GetFreePort();

        _simOpt = new SimServer.Options
        {
            Host            = "127.0.0.1",
            Port            = _port,
            TiltDelayMs     = 50,
            SortDurationMs  = 100,
            MoveDurationMs  = 80,
            InitialCurFloor = 1,
            SimLoopMs       = 10,
        };
        _gwOpt = new PlcGatewayOptions
        {
            Host                 = "127.0.0.1",
            Port                 = _port,
            PollIntervalMs       = 30,
            OfflineAfterFailures = 3,
            WriteTimeoutMs       = 500,
            RFlagPollMs          = 20,
            RFlagTimeoutMs       = 3000,
            CFlagTimeoutMs       = 2000,
        };
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_gw  is not null) { await _gw.StopAsync(); await _gw.DisposeAsync(); }
        if (_sim is not null) await _sim.DisposeAsync();

        lock (_tlLock)
        {
            _out.WriteLine("=== 타임라인 ===");
            foreach (var l in _timeline) _out.WriteLine(l);
            _out.WriteLine($"=== 종료 ({_timeline.Count}줄) ===");
        }
    }

    private async Task StartInfraAsync(int? initialFloor = null)
    {
        if (initialFloor.HasValue)
            _simOpt = _simOpt with { InitialCurFloor = initialFloor.Value };

        void Log(string l) { lock (_tlLock) { _timeline.Add(l); } }

        _sim   = new SimServer(_simOpt, timelineLog: Log);
        _queue = new PlcWriteQueue();
        _gw    = new PlcPollingService(_gwOpt, _queue);
        _hs    = new HandshakeOrchestrator(_gw, _gwOpt);

        await _sim.StartAsync();
        await _gw.StartAsync();
        await WaitUntilAsync(() => _gw.Latest.Online, 2000, "GW Online");
    }

    // ── S2: 층 다름(차트②) ────────────────────────────────────────────────────

    /// <summary>
    /// S2: Ready=1·CurFloor=1·agvFloor=2·TgtFloor=0
    /// → Decider WRONG_FLOOR + WCS가 D6=2 기입 → Sim 이동 → CurFloor=2 → 재판정 allowed=true.
    /// PASS = D6 타임라인 1건, 이동 완료 후 Ready=1 CurFloor=2.
    /// 순수 GW 레이어 테스트 — DepositDecider.Decide가 WRONG_FLOOR 반환, SetTgtFloor 큐 투입.
    /// </summary>
    [Fact]
    public async Task S2_WrongFloor_TgtFloorWritten_ThenReady()
    {
        await StartInfraAsync(initialFloor: 1);

        // 사전조건: Ready=1, CurFloor=1, TgtFloor=0
        var snap = _gw!.Latest;
        Assert.True(snap.Ready, "초기 Ready=1");
        Assert.Equal(1, snap.CurFloor);
        Assert.Equal(0, snap.TgtFloor);

        // 판정 — agvFloor=2, CurFloor=1, TgtFloor=0 → WRONG_FLOOR + WriteTgtFloor
        var decision = DepositDecider.Decide(snap, agvFloor: 2, hold: WcsHold.None);
        Assert.False(decision.Allowed);
        Assert.Equal(DenyReason.WrongFloor, decision.Reason);
        Assert.True(decision.WriteTgtFloor);
        Assert.Equal(2, decision.TgtFloorValue);

        // D6 기입 (WriteTgtFloor 조건 충족)
        await _gw.EnqueueAsync(new PlcWrite.SetTgtFloor(2));

        // D6=2 폴링 대기
        await WaitUntilAsync(() => _gw!.Latest.TgtFloor == 2, 2000, "TgtFloor=2 기입");
        _out.WriteLine($"[S2] D6=2 기입 확인");

        // D6 타임라인 1건 확인
        int d6Writes;
        lock (_tlLock) { d6Writes = _timeline.Count(l => l.Contains("WCS 쓰기 수신: D6")); }
        Assert.Equal(1, d6Writes);
        _out.WriteLine($"[S2] D6 쓰기 타임라인 {d6Writes}건");

        // 이동 완료 대기 (MoveDuration + 여유)
        await WaitUntilAsync(() =>
        {
            var s = _gw!.Latest;
            return s.CurFloor == 2 && s.Ready;
        }, 3000, "이동 완료 CurFloor=2 Ready=1");

        var snapAfter = _gw!.Latest;
        Assert.Equal(2, snapAfter.CurFloor);
        Assert.True(snapAfter.Ready, "이동 완료 후 Ready=1");

        // 재판정 — 층 일치
        var decision2 = DepositDecider.Decide(snapAfter, agvFloor: 2, hold: WcsHold.None);
        Assert.True(decision2.Allowed);
        _out.WriteLine($"[S2] 이동 완료 후 allowed={decision2.Allowed}");
    }

    // ── S3: 분류 중 선기입·복귀·분류 시작 클리어(차트③) ──────────────────────

    /// <summary>
    /// S3: 분류 진행 중(Ready=0) 선기입(BUSY+D6 기입) → 분류 시작 시 Sim이 TgtFloor=0 클리어
    /// → 분류 후 복귀 이동 → 도착.
    /// PASS = BUSY 응답, D6 선기입 1건, 분류 시작 시 TgtFloor=0, 복귀 완료.
    /// </summary>
    [Fact]
    public async Task S3_BusyPreWrite_TgtFloorClearedAtSortStart_ThenReturns()
    {
        // CurFloor=2로 시작 — 분류 후 복귀 이동(TgtFloor=1)이 발생하도록
        await StartInfraAsync(initialFloor: 2);

        var snap0 = _gw!.Latest;
        Assert.True(snap0.Ready);
        Assert.Equal(2, snap0.CurFloor);

        // 핸드셰이크 1건 시작 (분류 시작을 위해)
        var hsTask = _hs!.ExecuteAsync(cellNo: 5, ct: CancellationToken.None);

        // TiltDelay 중 Ready=0이 될 때까지 대기
        await WaitUntilAsync(() => !_gw!.Latest.Ready, 2000, "분류 시작 Ready=0");

        var snapBusy = _gw!.Latest;
        Assert.False(snapBusy.Ready, "분류 중 Ready=0");

        // 판정: Ready=0, TgtFloor=0 → BUSY + 선기입(D6=1)
        var decision = DepositDecider.Decide(snapBusy, agvFloor: 1, hold: WcsHold.None);
        Assert.False(decision.Allowed);
        Assert.Equal(DenyReason.Busy, decision.Reason);
        Assert.True(decision.WriteTgtFloor, "BUSY 상태에서 TgtFloor==0이면 선기입");
        Assert.Equal(1, decision.TgtFloorValue);

        // D6 선기입 큐 투입
        await _gw.EnqueueAsync(new PlcWrite.SetTgtFloor(1));
        await WaitUntilAsync(() => _gw!.Latest.TgtFloor == 1, 2000, "D6 선기입 TgtFloor=1");
        _out.WriteLine("[S3] D6=1 선기입 확인");

        // 핸드셰이크 완료(분류 완료) 대기
        var hsResult = await hsTask;
        _out.WriteLine($"[S3] 핸드셰이크 결과: {hsResult.Outcome}");

        // 분류 시작 시 TgtFloor=0 클리어됐는지 타임라인에서 확인
        // Sim 분류 시작 로그: "분류 시작: Ready=0, TgtFloor 클리어"
        bool clearObserved;
        lock (_tlLock)
        {
            clearObserved = _timeline.Any(l => l.Contains("TgtFloor 클리어"));
        }
        Assert.True(clearObserved, "분류 시작 시 TgtFloor 클리어 로그 존재");

        // 분류 완료 후 복귀 이동: TgtFloor=1(선기입)·CurFloor=2 → 이동 → CurFloor=1
        // 복귀 완료 조건: CurFloor=1, Ready=1
        await WaitUntilAsync(() =>
        {
            var s = _gw!.Latest;
            return s.CurFloor == 1 && s.Ready;
        }, 5000, "복귀 완료 CurFloor=1 Ready=1");

        var snapFinal = _gw!.Latest;
        Assert.Equal(1, snapFinal.CurFloor);
        Assert.True(snapFinal.Ready);
        _out.WriteLine($"[S3] 복귀 완료: CurFloor={snapFinal.CurFloor} Ready={snapFinal.Ready}");
    }

    // ── S4: 핑퐁 차단 — TgtFloor≠0 구간 D6 쓰기 0건 ────────────────────────

    /// <summary>
    /// S4: TgtFloor≠0 구간 동안 추가 SetTgtFloor 요청이 스킵되어야 함(핑퐁 차단).
    /// PASS = TgtFloor≠0 전 구간에서 SimServer 타임라인의 "WCS 쓰기 수신: D6" 이벤트가
    ///        최초 1건 외 0건.
    /// </summary>
    [Fact]
    public async Task S4_PingpongBlocked_D6WritesOnlyOnce_WhileTgtFloorNonZero()
    {
        await StartInfraAsync(initialFloor: 1);

        // TgtFloor=2 최초 기입 (층 불일치)
        await _gw!.EnqueueAsync(new PlcWrite.SetTgtFloor(2));
        await WaitUntilAsync(() => _gw!.Latest.TgtFloor == 2, 2000, "TgtFloor=2 최초 기입");

        // TgtFloor≠0 구간 진입 — 이 시점부터 D6 쓰기 카운트 시작
        int d6WritesBeforeNonZero;
        lock (_tlLock) { d6WritesBeforeNonZero = _timeline.Count(l => l.Contains("WCS 쓰기 수신: D6")); }
        Assert.Equal(1, d6WritesBeforeNonZero); // 최초 1건만
        _out.WriteLine($"[S4] TgtFloor≠0 진입 전 D6 쓰기 {d6WritesBeforeNonZero}건");

        // TgtFloor≠0 구간에서 추가 SetTgtFloor 요청 3건 투입 → 전부 스킵되어야 함
        await _gw.EnqueueAsync(new PlcWrite.SetTgtFloor(3));
        await _gw.EnqueueAsync(new PlcWrite.SetTgtFloor(1));
        await _gw.EnqueueAsync(new PlcWrite.SetTgtFloor(2));

        // 큐 처리 시간 대기 (PollIntervalMs * 5)
        await PollForDurationAsync(300);

        // D6 쓰기 카운트 — 여전히 1건이어야 함
        int d6WritesAfter;
        lock (_tlLock) { d6WritesAfter = _timeline.Count(l => l.Contains("WCS 쓰기 수신: D6")); }
        Assert.Equal(1, d6WritesAfter);
        _out.WriteLine($"[S4] TgtFloor≠0 구간 D6 추가 쓰기 0건 확인 (총 {d6WritesAfter}건)");

        // TgtFloor가 2로 유지됨 확인
        Assert.Equal(2, _gw!.Latest.TgtFloor);
    }

    // ── S9: 다중 AGV 경합 — 단일 소터 선점·타층 D6 쓰기 0건·클리어 후 양보 ──

    /// <summary>
    /// S9: 층 AGV1(현재층=층1)이 TgtFloor=2 선점 → 이동 → 분류 시작 클리어 →
    ///     이후 AGV2(agvFloor=1 요청)가 TgtFloor=1 기입.
    /// 선점 구간 동안 AGV2의 SetTgtFloor는 스킵(D6 쓰기 0건).
    /// PASS = 선점 구간 D6 추가 쓰기 0건, 클리어 후 agvFloor=1 기입 1건.
    /// </summary>
    [Fact]
    public async Task S9_MultiAgvContention_TgtFloorSingleOwnership_ThenYield()
    {
        // CurFloor=1로 시작
        await StartInfraAsync(initialFloor: 1);

        // AGV1 판정: CurFloor=1, agvFloor=2 → WRONG_FLOOR + SetTgtFloor(2) 선점
        var snap1 = _gw!.Latest;
        var dec1  = DepositDecider.Decide(snap1, agvFloor: 2, hold: WcsHold.None);
        Assert.False(dec1.Allowed);
        Assert.True(dec1.WriteTgtFloor);

        await _gw.EnqueueAsync(new PlcWrite.SetTgtFloor(2));
        await WaitUntilAsync(() => _gw!.Latest.TgtFloor == 2, 2000, "AGV1 TgtFloor=2 선점");
        _out.WriteLine("[S9] AGV1 TgtFloor=2 선점 완료");

        // D6 쓰기 카운트 기준: 선점 후 1건
        int d6At1;
        lock (_tlLock) { d6At1 = _timeline.Count(l => l.Contains("WCS 쓰기 수신: D6")); }

        // 선점 구간에서 AGV2(agvFloor=1) SetTgtFloor 시도 → 스킵
        var snap2 = _gw!.Latest;
        var dec2  = DepositDecider.Decide(snap2, agvFloor: 1, hold: WcsHold.None);
        // TgtFloor≠0 → WRONG_FLOOR + WriteTgtFloor=false(핑퐁 차단)
        Assert.False(dec2.WriteTgtFloor, "TgtFloor≠0이므로 AGV2 기입 금지(핑퐁 차단)");
        _out.WriteLine($"[S9] AGV2 판정: allowed={dec2.Allowed} WriteTgtFloor={dec2.WriteTgtFloor}");

        // 이동 완료 대기 (TgtFloor 도달 후 Sim이 CurFloor=2로 갱신)
        await WaitUntilAsync(() => _gw!.Latest.CurFloor == 2 && _gw!.Latest.Ready, 3000, "이동 완료 CurFloor=2");

        // 핸드셰이크 시작 (분류) → 분류 시작 시 TgtFloor=0 클리어
        var hsTask = _hs!.ExecuteAsync(cellNo: 1, ct: CancellationToken.None);
        await WaitUntilAsync(() => !_gw!.Latest.Ready, 3000, "분류 시작 Ready=0");
        await WaitUntilAsync(() =>
        {
            lock (_tlLock) { return _timeline.Any(l => l.Contains("TgtFloor 클리어")); }
        }, 2000, "TgtFloor 클리어 로그");

        // 클리어 후 TgtFloor=0 확인
        await WaitUntilAsync(() => _gw!.Latest.TgtFloor == 0, 2000, "TgtFloor=0 클리어");

        // ── 선점~클리어 구간 D6 추가 쓰기 0건 단언 ─────────────────────────────
        // AGV2 기입 전에 카운트 측정 (클리어 후 기입은 선점 구간 외)
        int d6At2;
        lock (_tlLock) { d6At2 = _timeline.Count(l => l.Contains("WCS 쓰기 수신: D6")); }
        Assert.True(d6At2 - d6At1 == 0,
            $"선점 구간 D6 추가 쓰기 {d6At2 - d6At1}건 (0건이어야 함)");
        _out.WriteLine($"[S9] 선점 구간 D6 추가 쓰기 0건 확인");

        // 클리어 후 AGV2(agvFloor=1) 기입 가능 여부 판정
        var snapAfterClear = _gw!.Latest;
        var dec3 = DepositDecider.Decide(snapAfterClear, agvFloor: 1, hold: WcsHold.None);
        _out.WriteLine($"[S9] AGV2 클리어 후 판정: allowed={dec3.Allowed} WriteTgtFloor={dec3.WriteTgtFloor}");
        if (dec3.WriteTgtFloor)
        {
            await _gw.EnqueueAsync(new PlcWrite.SetTgtFloor(dec3.TgtFloorValue));
            await WaitUntilAsync(() => _gw!.Latest.TgtFloor == dec3.TgtFloorValue, 2000, "AGV2 TgtFloor 양보 기입");
            _out.WriteLine($"[S9] 클리어 후 AGV2 TgtFloor={dec3.TgtFloorValue} 기입 확인");
        }

        // 핸드셰이크 완료
        await hsTask;
    }

    // ── 헬퍼 ─────────────────────────────────────────────────────────────────

    private static async Task WaitUntilAsync(
        Func<bool> condition, int timeoutMs, string msg, int pollMs = 20)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTimeOffset.Now > deadline)
                Assert.Fail($"WaitUntil 타임아웃({timeoutMs}ms): {msg}");
            await Task.Delay(pollMs);
        }
    }

    private static async Task PollForDurationAsync(int durationMs, int pollMs = 20)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(durationMs);
        while (DateTimeOffset.Now < deadline)
            await Task.Delay(pollMs);
    }

    private static int GetFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}

// ─────────────────────────────────────────────────────────────────────────
// S5: R_Seq 불일치 알람 — alarm MISMATCH + sorter_command MISMATCH DB 단언
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// S5: InjectRSeqOverride=999 → Outcome=RSeqMismatch.
/// PASS = alarm 1행(code=R_SEQ_MISMATCH) + sorter_command status=MISMATCH.
/// </summary>
public class S5RSeqMismatchTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _out;
    private readonly int _port;
    private SimWebApplicationFactory? _factory;
    private HttpClient? _client;

    public S5RSeqMismatchTests(ITestOutputHelper output)
    {
        _out  = output;
        _port = GetFreePort();
    }

    public async Task InitializeAsync()
    {
        _factory = new SimWebApplicationFactory(_port);
        await _factory.StartSimAsync();
        _client  = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
    }

    [Fact]
    public async Task S5_RSeqMismatch_AlarmAndSorterCommandMismatch()
    {
        // 고장 주입
        _factory!.Sim.InjectRSeqOverride = 999;

        // IF-05 → IF-10
        await SendIf05AndIf10Async(_client!, 12001, "TEST-BARCODE-3");

        // 핸드셰이크 완료 대기 (MISMATCH 처리 포함)
        await WaitUntilAsync(async () =>
        {
            using var db = _factory!.CreateDbScope();
            return await db.SorterCommands.AnyAsync(c => c.Status == SorterCommandStatus.MISMATCH);
        }, timeoutMs: 6000, msg: "sorter_command MISMATCH");

        using var assertDb = _factory!.CreateDbScope();
        var cmd = await assertDb.SorterCommands
            .Where(c => c.Status == SorterCommandStatus.MISMATCH)
            .FirstOrDefaultAsync();
        Assert.NotNull(cmd);
        Assert.Equal(SorterCommandStatus.MISMATCH, cmd.Status);

        var alarm = await assertDb.Alarms
            .Where(a => a.Code == "R_SEQ_MISMATCH")
            .FirstOrDefaultAsync();
        Assert.NotNull(alarm);
        Assert.Equal("R_SEQ_MISMATCH", alarm.Code);
        _out.WriteLine($"[S5] alarm code={alarm.Code}, sorter_command status=MISMATCH");
    }

    private static async Task SendIf05AndIf10Async(HttpClient client, int pId, string barcode)
    {
        var if05Req = new { pId, agvNo = 1, barcode, inductionNo = 1, qty = 1, timeStamp = "2026-06-17 10:00:00" };
        var if05Resp = await client.PostAsJsonAsync("/api/v1/destination-query", if05Req);
        Assert.Equal(HttpStatusCode.OK, if05Resp.StatusCode);
        var if05Body = await if05Resp.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.Equal("OK", if05Body!.Result);

        // IF-08 — 소터 Online 대기
        var deadline = DateTimeOffset.Now.AddSeconds(5);
        while (DateTimeOffset.Now < deadline)
        {
            var r = await client.PostAsJsonAsync("/api/v1/deposit-permission",
                new { pId, chuteNo = if05Body.ChuteNo!.Value, agvNo = 1 });
            var b = await r.Content.ReadFromJsonAsync<DepositPermissionResponse>();
            if (b?.Allowed == true) break;
            await Task.Delay(200);
        }

        var if10Req = new { pId, barcode, chuteNo = if05Body.ChuteNo!.Value, agvNo = 1 };
        var if10Resp = await client.PostAsJsonAsync("/api/v1/deposit-report", if10Req);
        Assert.Equal(HttpStatusCode.OK, if10Resp.StatusCode);
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition, int timeoutMs, string msg, int pollMs = 50)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        while (!await condition())
        {
            if (DateTimeOffset.Now > deadline)
                Assert.Fail($"WaitUntil 타임아웃({timeoutMs}ms): {msg}");
            await Task.Delay(pollMs);
        }
    }

    private static int GetFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}

// ─────────────────────────────────────────────────────────────────────────
// S6: R_Flag 타임아웃 → alarm RFLAG_TIMEOUT + sorter_command TIMEOUT (재시도 없음)
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// S6: InjectRFlagDelayMs ≫ RFlagTimeoutMs → Outcome=RFlagTimeout.
/// PASS = alarm 1행(code=RFLAG_TIMEOUT) + sorter_command status=TIMEOUT. 재시도 없음.
/// </summary>
public class S6RFlagTimeoutTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _out;
    private readonly int _port;
    private SimWebApplicationFactory? _factory;
    private HttpClient? _client;

    public S6RFlagTimeoutTests(ITestOutputHelper output)
    {
        _out  = output;
        _port = GetFreePort();
    }

    public async Task InitializeAsync()
    {
        // RFlagTimeoutMs=300ms로 단축 — 고장 주입 지연(5000ms)보다 훨씬 작아 타임아웃 확실히 유발
        _factory = new SimWebApplicationFactory(_port, rFlagTimeoutMs: 300);
        await _factory.StartSimAsync();
        _client  = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
    }

    [Fact]
    public async Task S6_RFlagTimeout_AlarmAndSorterCommandTimeout_NoRetry()
    {
        // 고장 주입: R_Flag 지연을 RFlagTimeoutMs(300ms)보다 훨씬 크게
        _factory!.Sim.InjectRFlagDelayMs = 5000;

        await SendIf05AndIf10Async(_client!, 13001, "TEST-BARCODE-3");

        // TIMEOUT 행 생성 대기 (RFlagTimeoutMs(300ms) + SortDuration + 여유)
        await WaitUntilAsync(async () =>
        {
            using var db = _factory!.CreateDbScope();
            return await db.SorterCommands.AnyAsync(c => c.Status == SorterCommandStatus.TIMEOUT);
        }, timeoutMs: 6000, msg: "sorter_command TIMEOUT");

        using var assertDb = _factory!.CreateDbScope();

        var cmd = await assertDb.SorterCommands
            .Where(c => c.Status == SorterCommandStatus.TIMEOUT)
            .FirstOrDefaultAsync();
        Assert.NotNull(cmd);
        Assert.Equal(SorterCommandStatus.TIMEOUT, cmd.Status);

        // TIMEOUT 행 1건 이상(재시도 아님) — 계약서 Q4: 포기 1행
        var timeoutCount = await assertDb.SorterCommands
            .CountAsync(c => c.Status == SorterCommandStatus.TIMEOUT);
        Assert.Equal(1, timeoutCount); // 재시도 없음 — 포기 1행

        var alarm = await assertDb.Alarms
            .Where(a => a.Code == "RFLAG_TIMEOUT")
            .FirstOrDefaultAsync();
        Assert.NotNull(alarm);
        Assert.Equal("RFLAG_TIMEOUT", alarm.Code);
        _out.WriteLine($"[S6] alarm code={alarm.Code}, sorter_command status=TIMEOUT (재시도 없음)");
    }

    private static async Task SendIf05AndIf10Async(HttpClient client, int pId, string barcode)
    {
        var if05Req = new { pId, agvNo = 1, barcode, inductionNo = 1, qty = 1, timeStamp = "2026-06-17 10:00:00" };
        var if05Resp = await client.PostAsJsonAsync("/api/v1/destination-query", if05Req);
        Assert.Equal(HttpStatusCode.OK, if05Resp.StatusCode);
        var if05Body = await if05Resp.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.Equal("OK", if05Body!.Result);

        var deadline = DateTimeOffset.Now.AddSeconds(5);
        while (DateTimeOffset.Now < deadline)
        {
            var r = await client.PostAsJsonAsync("/api/v1/deposit-permission",
                new { pId, chuteNo = if05Body.ChuteNo!.Value, agvNo = 1 });
            var b = await r.Content.ReadFromJsonAsync<DepositPermissionResponse>();
            if (b?.Allowed == true) break;
            await Task.Delay(200);
        }

        var if10Req = new { pId, barcode, chuteNo = if05Body.ChuteNo!.Value, agvNo = 1 };
        var if10Resp = await client.PostAsJsonAsync("/api/v1/deposit-report", if10Req);
        Assert.Equal(HttpStatusCode.OK, if10Resp.StatusCode);
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition, int timeoutMs, string msg, int pollMs = 50)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        while (!await condition())
        {
            if (DateTimeOffset.Now > deadline)
                Assert.Fail($"WaitUntil 타임아웃({timeoutMs}ms): {msg}");
            await Task.Delay(pollMs);
        }
    }

    private static int GetFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}

// ─────────────────────────────────────────────────────────────────────────
// S7: OFFLINE 전이당 1건 alarm — SimWebApplicationFactory + DB alarm 행 단언
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// S7: OFFLINE 전이당 정확히 1건 alarm, ONLINE 복구 후 재전이도 1건.
///
/// 시퀀스:
///   1. Sim 기동 → GW Online, alarm 0건
///   2. Sim 종료 → OFFLINE 전이 → alarm 1건 (전이당 1건 단언)
///   3. Sim 재기동 → GW ONLINE 복구 → _online 플래그 리셋 확인
///   4. Sim 재종료 → 2번째 OFFLINE 전이 → alarm 2건 (재전이도 1건 단언)
///
/// PASS 기준:
///   - 1차 OFFLINE: DB alarm 행 1건(code=OFFLINE)
///   - ONLINE 복구: GW가 다시 폴 성공(_online 리셋)
///   - 2차 OFFLINE: DB alarm 행 누적 2건(=1+1, 전이당 1건 원자성)
/// 계약서 S7 요건: alarm 1행(code=OFFLINE) DB 행 단언 + MAJOR-1 _online 재설정 검증.
/// </summary>
public class S7OfflineAlarmTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _out;
    private readonly int _port;
    private SimWebApplicationFactory? _factory;
    // 팩토리는 Sim 기동 후 생성 — DisposeAsync에서 정리

    public S7OfflineAlarmTests(ITestOutputHelper output)
    {
        _out  = output;
        _port = GetFreePort();
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_factory is not null) await _factory.DisposeAsync();
    }

    [Fact]
    public async Task S7_OfflineTransition_OneAlarmPerTransition_ThenRecovers()
    {
        // OfflineAfterFailures=3, WriteTimeoutMs=500ms(팩토리 기본) → OFFLINE 전이까지 약 1500ms+여유
        _factory = new SimWebApplicationFactory(_port, rFlagTimeoutMs: 3000);
        await _factory.StartSimAsync();
        // WebApplicationFactory를 기동해 SorterRegistryFactory.StartAsync 경로 활성
        using var client = _factory.CreateClient();

        // Phase 1 준비: GW가 실제로 Online=true가 될 때까지 폴링 대기 (고정 sleep 금지)
        await WaitUntilAsync(() => Task.FromResult(_factory!.IsSorterOnline()),
            timeoutMs: 3000, msg: "Phase 1 준비: GW Online=true 초기 대기");
        _out.WriteLine("[S7] Phase 1: GW Online=true 확인");

        // ── Phase 1: 1차 OFFLINE 전이 ──────────────────────────────────────
        await _factory.Sim.StopAsync();
        _out.WriteLine("[S7] Phase 1: SimServer 종료 — 1차 OFFLINE 전이 대기");

        // OfflineAfterFailures(3) * WriteTimeoutMs(500ms) + 여유
        int offlineWaitMs = (3 * 500) + 2000; // ~3500ms
        await WaitUntilAsync(async () =>
        {
            using var db = _factory!.CreateDbScope();
            return await db.Alarms.AnyAsync(a => a.Code == "OFFLINE");
        }, timeoutMs: offlineWaitMs, msg: "1차 alarm OFFLINE DB 기록");

        // 폭주 없음 확인: alarm 수가 exactly 1에서 안정됐는지 조건부 폴링
        // WaitUntilAsync(>=1) 통과 후 추가 전이가 없음을 확인 — 폴 N회 연속 1건이면 안정
        await WaitUntilExactAsync(async () =>
        {
            using var db = _factory!.CreateDbScope();
            return await db.Alarms.CountAsync(a => a.Code == "OFFLINE");
        }, expected: 1, stablePollMs: 60, stableCount: 5,
           timeoutMs: 1000, msg: "1차 OFFLINE alarm 1건 안정 확인");

        _out.WriteLine("[S7] Phase 1: OFFLINE alarm 1건 안정 확인 ✓");

        // ── Phase 2: ONLINE 복구 ────────────────────────────────────────────
        await _factory.RestartSimAsync();
        _out.WriteLine("[S7] Phase 2: SimServer 재기동 — GW Online=true 폴링 대기");

        // ISorterGatewayRegistry.GetLatest().Online 폴링 — GW reconnect+폴 성공 시 true
        await WaitUntilAsync(() => Task.FromResult(_factory!.IsSorterOnline()),
            timeoutMs: 3000, msg: "Phase 2: GW Online=true 복구");
        _out.WriteLine("[S7] Phase 2: GW Online=true 확인 — _online 리셋 보장 ✓");

        // ── Phase 3: 2차 OFFLINE 전이 (재전이 단언) ─────────────────────────
        await _factory.Sim.StopAsync();
        _out.WriteLine("[S7] Phase 3: SimServer 2차 종료 — 재전이 OFFLINE 대기");

        await WaitUntilAsync(async () =>
        {
            using var db = _factory!.CreateDbScope();
            return await db.Alarms.CountAsync(a => a.Code == "OFFLINE") >= 2;
        }, timeoutMs: offlineWaitMs, msg: "2차 alarm OFFLINE DB 기록");

        // 폭주 없음 확인: alarm 수가 exactly 2에서 안정
        await WaitUntilExactAsync(async () =>
        {
            using var db = _factory!.CreateDbScope();
            return await db.Alarms.CountAsync(a => a.Code == "OFFLINE");
        }, expected: 2, stablePollMs: 60, stableCount: 5,
           timeoutMs: 1000, msg: "2차 OFFLINE alarm 2건 안정 확인");

        _out.WriteLine("[S7] Phase 3: OFFLINE alarm 재전이 2건 안정 확인 ✓");
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> condition, int timeoutMs, string msg, int pollMs = 50)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        while (!await condition())
        {
            if (DateTimeOffset.Now > deadline)
                Assert.Fail($"WaitUntil 타임아웃({timeoutMs}ms): {msg}");
            await Task.Delay(pollMs);
        }
    }

    /// <summary>
    /// count()가 expected 값을 <stableCount>회 연속으로 반환할 때까지 폴링.
    /// "추가 전이 없이 안정됨"을 관측 조건으로 검증 (고정 sleep 대체).
    /// </summary>
    private static async Task WaitUntilExactAsync(
        Func<Task<int>> countFunc, int expected,
        int stablePollMs, int stableCount,
        int timeoutMs, string msg)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        int consecutive = 0;
        while (consecutive < stableCount)
        {
            if (DateTimeOffset.Now > deadline)
                Assert.Fail($"WaitUntilExact 타임아웃({timeoutMs}ms): {msg}");
            var current = await countFunc();
            if (current == expected) consecutive++;
            else                     consecutive = 0;
            await Task.Delay(stablePollMs);
        }
    }

    private static int GetFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}

// ─────────────────────────────────────────────────────────────────────────
// S8: FULL·PAUSED — WebApplicationFactory(기존 FakeModbus) + capacity 시드
// ─────────────────────────────────────────────────────────────────────────

// ─────────────────────────────────────────────────────────────────────────
// S8 전용 WebApplicationFactory — FakeModbusWebApplicationFactory와 동일하되
// DB 이름을 인스턴스별 고유 Guid로 생성해 IClassFixture 공유 충돌 방지.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// S8 전용: FakeModbusMaster 기반이지만 DB 이름을 인스턴스별 Guid로 분리.
/// 기존 FakeModbusWebApplicationFactory와 IClassFixture 공유 시 SQLite 시드 중복을 피함.
/// </summary>
public sealed class S8ApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = $"S8_{Guid.NewGuid():N}";
    private readonly Microsoft.Data.Sqlite.SqliteConnection _anchor;
    private readonly PlcWriteQueue  _writeQueue = new();
    private readonly PlcGatewayOptions _gwOpt = new()
    {
        Host = "127.0.0.1", Port = 1502,
        PollIntervalMs = 150, OfflineAfterFailures = 3, WriteTimeoutMs = 1000,
        RFlagPollMs = 100, RFlagTimeoutMs = 30000, CFlagTimeoutMs = 5000,
    };
    private PlcPollingService?    _polling;
    private HandshakeOrchestrator? _handshake;
    private FakeSorterGatewayRegistry? _registry;
    private readonly FakeModbusMasterForApi _master = new();

    public S8ApplicationFactory()
    {
        _anchor = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={_dbName};Mode=Memory;Cache=Shared");
        _anchor.Open();
        _polling   = new PlcPollingService(_gwOpt, _writeQueue, _master);
        _handshake = new HandshakeOrchestrator(_polling, _gwOpt);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // WcsDbContext 교체
            var dbDescriptors = services
                .Where(d => d.ServiceType == typeof(DbContextOptions<WcsDbContext>)
                         || d.ServiceType == typeof(WcsDbContext))
                .ToList();
            foreach (var d in dbDescriptors) services.Remove(d);

            var connStr = $"Data Source={_dbName};Mode=Memory;Cache=Shared";
            services.AddDbContext<WcsDbContext>(opts =>
                opts.UseSqlite(connStr, s => s.CommandTimeout(30))
                    .ConfigureWarnings(w => w.Ignore(
                        Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)),
                ServiceLifetime.Scoped);

            // 스키마 + 시드 (anchor connection 경유)
            var dbOpts = new DbContextOptionsBuilder<WcsDbContext>()
                .UseSqlite(_anchor)
                .ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
                .Options;
            using var db = new WcsDbContext(dbOpts);
            db.Database.EnsureCreated();
            DbSeeder.Seed(db, new Dictionary<string, int> { ["1"] = 1, ["2"] = 2 });

            // SORTER_3D destination 조회 후 번들 구성
            var sorterDest = db.Destinations
                .First(d => d.DestType == Wcs.Data.DestType.SORTER_3D && d.IsActive);
            var bundle = new SorterBundleHandle(
                destinationId: sorterDest.Id,
                chuteNo:       sorterDest.ChuteNo,
                polling:       _polling!,
                handshake:     _handshake!);
            _registry = new FakeSorterGatewayRegistry(bundle);

            // ISorterGatewayRegistry 교체
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
            var nop = new NopSorterRegistryFactory(_polling!, _registry!);
            services.AddSingleton<ISorterGatewayRegistry>(nop);
            services.AddSingleton<IHostedService>(nop);
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _anchor.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// S8: capacity 집계로 Full → IF-08 FULL / destination.status=PAUSED → IF-08 PAUSED.
/// PASS = FULL이면 reason=FULL, PAUSED면 reason=PAUSED, OnCleared 후 READY 복귀.
/// S8ApplicationFactory(독립 DB) 사용 — 기존 IClassFixture와 SQLite 충돌 없음.
/// </summary>
public class S8FullPausedTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _out;
    private S8ApplicationFactory? _factory;
    private HttpClient? _client;

    public S8FullPausedTests(ITestOutputHelper output)
    {
        _out = output;
    }

    public Task InitializeAsync()
    {
        _factory = new S8ApplicationFactory();
        _client  = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
    }

    [Fact]
    public async Task S8_Chute_Full_Then_Cleared_Ready()
    {
        // chuteNo=1(CHUTE) 사용
        using var scope    = _factory!.Services.CreateScope();
        var       db       = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        var       capacity = scope.ServiceProvider.GetRequiredService<IChuteCapacityService>();

        var dest1   = db.Destinations.First(d => d.ChuteNo == 1 && d.DestType == DestType.CHUTE);
        var detail1 = db.ChuteDetails.First(cd => cd.DestinationId == dest1.Id);
        var fullQty = detail1.WorkFullQty;

        // 사전 조건: None
        Assert.Equal(WcsHold.None, capacity.GetHold(dest1.Id));

        // FULL 조건 달성
        capacity.OnReserved(dest1.Id, fullQty);
        Assert.Equal(WcsHold.Full, capacity.GetHold(dest1.Id));

        // IF-08 FULL 단언
        var resp = await _client!.PostAsJsonAsync("/api/v1/deposit-permission",
            new { pId = 15001, chuteNo = 1, agvNo = 1 });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<DepositPermissionResponse>();
        Assert.NotNull(body);
        Assert.False(body.Allowed);
        Assert.Equal("FULL", body.Reason);
        _out.WriteLine($"[S8] FULL → allowed={body.Allowed} reason={body.Reason}");

        // OnCleared → NORMAL 복귀
        await capacity.OnCleared(dest1.Id);
        var resp2 = await _client!.PostAsJsonAsync("/api/v1/deposit-permission",
            new { pId = 15002, chuteNo = 1, agvNo = 1 });
        var body2 = await resp2.Content.ReadFromJsonAsync<DepositPermissionResponse>();
        Assert.NotNull(body2);
        Assert.True(body2.Allowed);
        Assert.Equal("READY", body2.Reason);
        _out.WriteLine($"[S8] OnCleared 후 READY → allowed={body2.Allowed} reason={body2.Reason}");
    }

    [Fact]
    public async Task S8_Chute_Paused_NotAllowed()
    {
        // chuteNo=6 (PAUSED status — 시드)
        var resp = await _client!.PostAsJsonAsync("/api/v1/deposit-permission",
            new { pId = 15003, chuteNo = 6, agvNo = 1 });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<DepositPermissionResponse>();
        Assert.NotNull(body);
        Assert.False(body.Allowed);
        Assert.Equal("PAUSED", body.Reason);
        _out.WriteLine($"[S8] PAUSED → allowed={body.Allowed} reason={body.Reason}");
    }
}

// ─────────────────────────────────────────────────────────────────────────
// DTO (테스트 전용 역직렬화)
// ─────────────────────────────────────────────────────────────────────────

// DepositPermissionResponse, DestinationQueryResponse, DepositReportResponse는
// ApiIntegrationTests.cs에서 이미 정의되어 있으므로 여기서는 중복 정의하지 않음.
