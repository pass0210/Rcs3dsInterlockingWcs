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
// 시나리오 테스트 (S1~S9) — RCS↔WCS 재설계 Phase 1 전환 반영
//
// 전환 명세(계약 §회귀·전환):
//   - 구 IF-08 폴링(deposit-permission)은 폐지 → S1/S5/S6의 IF-08 단계 제거,
//     IF-05 → IF-09(도착·2층 정렬) → IF-10 흐름으로 재작성.
//   - 핸드셰이크 DB 단언(COMPLETED/MISMATCH/TIMEOUT·alarm)은 유지(IF-10→IF-11 경로 불변).
//   - S8 FULL/PAUSED는 IF-05 상류 필터(FULL/PAUSED→NG)로 재타겟.
//   - S2/S3/S4/S9(게이트웨이 직접)는 2층 고정 운영 기준 재작성
//     (agvFloor 비교 → operationalFloor(2) 비교, WRONG_FLOOR → NotAligned, .Allowed → .Ready).
//
// 운영층 상수(테스트) = 2 (production은 appsettings Wcs:OperationalFloor).
// ════════════════════════════════════════════════════════════════════════════

// ─────────────────────────────────────────────────────────────────────────
// SimServer WebApplicationFactory — 실 Sim3ds TCP에 연결하는 소터 번들 교체
// S1·S5·S6 전용: alarm/sorter_command DB 단언이 필요한 시나리오
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// 실 Sim3ds TCP 서버(동적 포트)를 소터 번들로 연결하는 WebApplicationFactory.
/// 실제 Modbus TCP 클라이언트를 사용하므로 핸드셰이크 alarm·sorter_command DB 영속화를 E2E로 검증.
/// Named in-memory SQLite로 DB 수명 고정. 소터 번들은 Start/Stop을 IHostedService로 위임.
/// </summary>
public class SimWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly int              _simPort;
    private SimServer?                _sim;
    private readonly List<string>     _timeline = [];
    private readonly object           _tlLock   = new();

    private readonly string _dbName = $"ScenarioTest_{Guid.NewGuid():N}";
    private readonly Microsoft.Data.Sqlite.SqliteConnection _anchor;

    private readonly SimServer.Options _simOpt;

    public PlcGatewayOptions GwOpt { get; }

    public SimServer Sim => _sim ?? throw new InvalidOperationException("Sim이 아직 기동되지 않았습니다.");
    public IReadOnlyList<string> Timeline { get { lock (_tlLock) { return _timeline.ToList(); } } }

    /// <param name="simPort">Sim3ds 서버가 바인딩할 동적 포트.</param>
    /// <param name="rFlagTimeoutMs">R_Flag 타임아웃(ms). 기본 3000ms. S6에서 단축 시 사용.</param>
    /// <param name="initialCurFloor">Sim 초기 CurFloor. 기본 운영층(2)로 시작해 핸드셰이크 즉시 진행.</param>
    public SimWebApplicationFactory(int simPort, int rFlagTimeoutMs = 3000, int initialCurFloor = 2)
    {
        _simPort = simPort;
        _simOpt  = new SimServer.Options
        {
            Host           = "127.0.0.1",
            Port           = simPort,
            TiltDelayMs    = 50,
            SortDurationMs = 100,
            MoveDurationMs = 80,
            InitialCurFloor = initialCurFloor,
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

    /// <summary>Sim을 재기동 — OFFLINE→ONLINE 복구 시나리오(S7) 전용.</summary>
    public async Task RestartSimAsync()
    {
        if (_sim is not null)
            await _sim.DisposeAsync();

        _sim = new SimServer(_simOpt, timelineLog: line => { lock (_tlLock) { _timeline.Add(line); } });
        await _sim.StartAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, cfg) =>
        {
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
                // 운영층 — 재설계 2층 고정 정렬(설정 경유, 하드코딩 금지)
                ["Wcs:OperationalFloor"]         = "2",
            });
        });

        builder.ConfigureServices(services =>
        {
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

    // 비동기 종료 경로 — IAsyncLifetime.DisposeAsync()가 await로 호출.
    // 호스트(IHost)·실 Sim TCP 서버 종료를 sync-over-async 없이 비동기로 수행해
    // teardown 단계의 스레드풀 블로킹 데드락(Task.Wait)을 회피한다.
    public override async ValueTask DisposeAsync()
    {
        // 실 Sim 먼저 종료(소켓 accept 루프 정리) → 베이스(IHost) 종료 → 앵커 연결 해제.
        if (_sim is not null) await _sim.DisposeAsync().ConfigureAwait(false);
        await base.DisposeAsync().ConfigureAwait(false);
        _anchor.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override void Dispose(bool disposing)
    {
        // 동기 Dispose 경로(파이널라이저/IClassFixture)에서도 sync-over-async를 피한다.
        // _sim 비동기 종료는 DisposeAsync에서 수행 — 여기서는 앵커 연결만 정리.
        if (disposing)
            _anchor.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>새 WcsDbContext 스코프를 열어 DB 행을 직접 조회.</summary>
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

    /// <summary>첫 번째 소터의 Online 상태(폴 성공 여부).</summary>
    public bool IsSorterOnline()
    {
        try
        {
            var registry = Services.GetService<ISorterGatewayRegistry>();
            if (registry is null) return false;
            using var db = CreateDbScope();
            var dest = db.Destinations
                         .FirstOrDefault(d => d.IsActive && d.DestType == Wcs.Data.DestType.SORTER_3D);
            if (dest is null) return false;
            var snap = registry.GetLatest(dest.Id);
            return snap?.Online ?? false;
        }
        catch { return false; }
    }

    /// <summary>첫 번째 소터의 최신 스냅샷(CurFloor 정렬 관찰용).</summary>
    public Wcs.Core.PlcSnapshot? SorterSnapshot()
    {
        try
        {
            var registry = Services.GetService<ISorterGatewayRegistry>();
            if (registry is null) return null;
            using var db = CreateDbScope();
            var dest = db.Destinations
                         .FirstOrDefault(d => d.IsActive && d.DestType == Wcs.Data.DestType.SORTER_3D);
            if (dest is null) return null;
            return registry.GetLatest(dest.Id);
        }
        catch { return null; }
    }
}

// ─────────────────────────────────────────────────────────────────────────
// S1: 정상 흐름 (IF-05 → IF-09 도착·2층 정렬 → IF-10 → IF-11) → sorter_command COMPLETED
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// S1: IF-05 → IF-09(도착·2층 정렬) → IF-10 → IF-11 핸드셰이크 1왕복 정상.
/// 전환: 구 IF-08 폴링 단계 삭제 → IF-09 도착 보고로 대체. 핸드셰이크 DB 단언은 유지.
/// PASS = sorter_command 1행 status=COMPLETED, R_Seq==C_Seq.
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
        // Sim 초기 CurFloor=2(운영층) — 도착 즉시 정렬 완료 상태.
        _factory = new SimWebApplicationFactory(_port, initialCurFloor: 2);
        await _factory.StartSimAsync();
        _client  = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
    }

    [Fact]
    public async Task S1_NormalFlow_SorterCommandCompleted()
    {
        // 소터 GW Online 대기
        await WaitUntilAsync(() => Task.FromResult(_factory!.IsSorterOnline()),
            timeoutMs: 5000, msg: "소터 GW Online");

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

        // ── IF-09: 도착 보고 (3D 소터 → 운영층 정렬) ────────────────────────────
        var if09Req = new { pId = 11001, chuteNo = if05Body.ChuteNo!.Value, agvNo = 1, timeStamp = (string?)null };
        var if09Resp = await _client!.PostAsJsonAsync("/api/v1/arrival-report", if09Req);
        Assert.Equal(HttpStatusCode.OK, if09Resp.StatusCode);
        var if09Body = await if09Resp.Content.ReadFromJsonAsync<ArrivalReportResponse>();
        Assert.Equal("OK", if09Body!.Result);
        _out.WriteLine("[S1] IF-09 도착 보고 OK");

        // 운영층(2) 정렬 완료 대기 (이미 CurFloor=2면 즉시 충족)
        await WaitUntilAsync(() => Task.FromResult(_factory!.SorterSnapshot()?.CurFloor == 2),
            timeoutMs: 4000, msg: "소터 운영층(2) 정렬");

        // ── IF-09 도착이 piece_event(IF09_ARRIVAL)로 기록됐는지 단언 ───────────
        using (var evDb = _factory!.CreateDbScope())
        {
            var arrived = await evDb.PieceEvents.AnyAsync(e => e.EventType == PieceEventType.IF09_ARRIVAL);
            Assert.True(arrived, "IF-09 도착 → piece_event IF09_ARRIVAL 기록");
        }

        // ── IF-10: 투입 보고 → IF-11 트리거 ────────────────────────────────────
        var if10Req = new { pId = 11001, barcode = "TEST-BARCODE-3", chuteNo = if05Body.ChuteNo!.Value, agvNo = 1 };
        var if10Resp = await _client!.PostAsJsonAsync("/api/v1/deposit-report", if10Req);
        Assert.Equal(HttpStatusCode.OK, if10Resp.StatusCode);
        var if10Body = await if10Resp.Content.ReadFromJsonAsync<DepositReportResponse>();
        Assert.Equal("OK", if10Body!.Result);
        _out.WriteLine("[S1] IF-10 OK → IF-11 트리거");

        // ── 핸드셰이크 완료 대기 → DB 단언 ─────────────────────────────────────
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
// S2/S3/S4/S9: PlcGateway 직접 통합 (Sim+GW 번들) — 2층 고정 운영 기준 재작성
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// S2/S3/S4/S9: 실 SimServer + PlcGateway 직접 번들.
/// DB 단언 없음 — 게이트웨이 동작(TgtFloor·D6 쓰기 이력·경합)을 타임라인·스냅샷으로 입증.
/// 재설계: agvFloor 비교 → operationalFloor(2) 비교, WRONG_FLOOR → NotAligned, .Allowed → .Ready.
/// </summary>
public class S234_9GatewayScenarioTests : IAsyncLifetime
{
    // 운영층 — 재설계 기준값(테스트 상수). production은 appsettings Wcs:OperationalFloor.
    private const int OperFloor = 2;

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
        // 쓰기 큐 채널을 먼저 완료시켜 RunWriteConsumerAsync가 결정적으로 종료되게 한다
        // (CTS 취소만으로는 빈 채널 parked ReadAllAsync가 안 깨어나는 타이밍 경쟁 → StopAsync 데드락).
        _queue?.Writer.TryComplete();
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

    // ── S2: 미정렬 → 운영층 정렬 → 정렬 완료(Ready) ──────────────────────────

    /// <summary>
    /// S2: Ready=1·CurFloor=1·운영층=2·TgtFloor=0
    /// → Decider NotAligned + WCS가 D6=2 기입 → Sim 이동 → CurFloor=2 → 재판정 Ready.
    /// PASS = D6 타임라인 1건, 이동 완료 후 Ready=1 CurFloor=2.
    /// </summary>
    [Fact]
    public async Task S2_NotAligned_TgtFloorWritten_ThenReady()
    {
        await StartInfraAsync(initialFloor: 1);

        var snap = _gw!.Latest;
        Assert.True(snap.Ready, "초기 Ready=1");
        Assert.Equal(1, snap.CurFloor);
        Assert.Equal(0, snap.TgtFloor);

        // 판정 — operationalFloor=2, CurFloor=1, TgtFloor=0 → NotAligned + WriteTgtFloor
        var decision = DepositDecider.Decide(snap, OperFloor, hold: WcsHold.None);
        Assert.False(decision.Ready);
        Assert.Equal(DenyReason.NotAligned, decision.Reason);
        Assert.True(decision.WriteTgtFloor);
        Assert.Equal(OperFloor, decision.TgtFloorValue);

        // D6 기입 (WriteTgtFloor 조건 충족)
        await _gw.EnqueueAsync(new PlcWrite.SetTgtFloor(OperFloor));

        await WaitUntilAsync(() => _gw!.Latest.TgtFloor == OperFloor, 2000, "TgtFloor=2 기입");
        _out.WriteLine($"[S2] D6=2 기입 확인");

        int d6Writes;
        lock (_tlLock) { d6Writes = _timeline.Count(l => l.Contains("WCS 쓰기 수신: D6")); }
        Assert.Equal(1, d6Writes);
        _out.WriteLine($"[S2] D6 쓰기 타임라인 {d6Writes}건");

        // 이동 완료 대기 (MoveDuration + 여유)
        await WaitUntilAsync(() =>
        {
            var s = _gw!.Latest;
            return s.CurFloor == OperFloor && s.Ready;
        }, 3000, "이동 완료 CurFloor=2 Ready=1");

        var snapAfter = _gw!.Latest;
        Assert.Equal(OperFloor, snapAfter.CurFloor);
        Assert.True(snapAfter.Ready, "이동 완료 후 Ready=1");

        // 재판정 — 운영층 일치 → Ready
        var decision2 = DepositDecider.Decide(snapAfter, OperFloor, hold: WcsHold.None);
        Assert.True(decision2.Ready);
        _out.WriteLine($"[S2] 이동 완료 후 Ready={decision2.Ready}");
    }

    // ── S3: 분류 중 선기입·복귀·분류 시작 클리어 ─────────────────────────────

    /// <summary>
    /// S3: 분류 진행 중(Ready=0) 선기입(BUSY+운영층 복귀 D6 기입) → 분류 시작 시 Sim TgtFloor=0 클리어
    /// → 분류 후 복귀 이동 → 운영층 도착.
    /// PASS = BUSY 판정, D6 선기입 1건, 분류 시작 시 TgtFloor 클리어, 복귀 완료 CurFloor=2.
    /// </summary>
    [Fact]
    public async Task S3_BusyPreWrite_TgtFloorClearedAtSortStart_ThenReturns()
    {
        // CurFloor=1로 시작 — 분류 후 복귀 이동(운영층 2)이 발생하도록
        await StartInfraAsync(initialFloor: 1);

        var snap0 = _gw!.Latest;
        Assert.True(snap0.Ready);
        Assert.Equal(1, snap0.CurFloor);

        // 핸드셰이크 1건 시작 (분류 시작을 위해)
        var hsTask = _hs!.ExecuteAsync(cellNo: 5, ct: CancellationToken.None);

        // TiltDelay 중 Ready=0이 될 때까지 대기
        await WaitUntilAsync(() => !_gw!.Latest.Ready, 2000, "분류 시작 Ready=0");

        var snapBusy = _gw!.Latest;
        Assert.False(snapBusy.Ready, "분류 중 Ready=0");

        // 판정: Ready=0, TgtFloor=0 → BUSY + 운영층(2) 복귀 선기입
        var decision = DepositDecider.Decide(snapBusy, OperFloor, hold: WcsHold.None);
        Assert.False(decision.Ready);
        Assert.Equal(DenyReason.Busy, decision.Reason);
        Assert.True(decision.WriteTgtFloor, "BUSY 상태에서 TgtFloor==0이면 선기입");
        Assert.Equal(OperFloor, decision.TgtFloorValue);

        // D6 선기입 큐 투입
        await _gw.EnqueueAsync(new PlcWrite.SetTgtFloor(OperFloor));
        await WaitUntilAsync(() => _gw!.Latest.TgtFloor == OperFloor, 2000, "D6 선기입 TgtFloor=2");
        _out.WriteLine("[S3] D6=2 선기입 확인");

        // 핸드셰이크 완료(분류 완료) 대기
        var hsResult = await hsTask;
        _out.WriteLine($"[S3] 핸드셰이크 결과: {hsResult.Outcome}");

        // 분류 시작 시 TgtFloor=0 클리어 타임라인 확인
        bool clearObserved;
        lock (_tlLock)
        {
            clearObserved = _timeline.Any(l => l.Contains("TgtFloor 클리어"));
        }
        Assert.True(clearObserved, "분류 시작 시 TgtFloor 클리어 로그 존재");

        // 분류 완료 후 복귀 이동: TgtFloor=2(선기입)·CurFloor=1 → 이동 → CurFloor=2
        await WaitUntilAsync(() =>
        {
            var s = _gw!.Latest;
            return s.CurFloor == OperFloor && s.Ready;
        }, 5000, "복귀 완료 CurFloor=2 Ready=1");

        var snapFinal = _gw!.Latest;
        Assert.Equal(OperFloor, snapFinal.CurFloor);
        Assert.True(snapFinal.Ready);
        _out.WriteLine($"[S3] 복귀 완료: CurFloor={snapFinal.CurFloor} Ready={snapFinal.Ready}");
    }

    // ── S4: 핑퐁 차단 — TgtFloor≠0 구간 D6 쓰기 0건 ────────────────────────

    /// <summary>
    /// S4: TgtFloor≠0 구간 동안 추가 SetTgtFloor 요청이 스킵(핑퐁 차단).
    /// PASS = TgtFloor≠0 전 구간에서 "WCS 쓰기 수신: D6" 이벤트가 최초 1건 외 0건.
    /// </summary>
    [Fact]
    public async Task S4_PingpongBlocked_D6WritesOnlyOnce_WhileTgtFloorNonZero()
    {
        await StartInfraAsync(initialFloor: 1);

        // TgtFloor=2 최초 기입 (미정렬 → 운영층 정렬)
        await _gw!.EnqueueAsync(new PlcWrite.SetTgtFloor(OperFloor));
        await WaitUntilAsync(() => _gw!.Latest.TgtFloor == OperFloor, 2000, "TgtFloor=2 최초 기입");

        int d6WritesBeforeNonZero;
        lock (_tlLock) { d6WritesBeforeNonZero = _timeline.Count(l => l.Contains("WCS 쓰기 수신: D6")); }
        Assert.Equal(1, d6WritesBeforeNonZero);
        _out.WriteLine($"[S4] TgtFloor≠0 진입 전 D6 쓰기 {d6WritesBeforeNonZero}건");

        // TgtFloor≠0 구간에서 추가 SetTgtFloor 요청 3건 → 전부 스킵되어야 함
        await _gw.EnqueueAsync(new PlcWrite.SetTgtFloor(3));
        await _gw.EnqueueAsync(new PlcWrite.SetTgtFloor(1));
        await _gw.EnqueueAsync(new PlcWrite.SetTgtFloor(2));

        await PollForDurationAsync(300);

        int d6WritesAfter;
        lock (_tlLock) { d6WritesAfter = _timeline.Count(l => l.Contains("WCS 쓰기 수신: D6")); }
        Assert.Equal(1, d6WritesAfter);
        _out.WriteLine($"[S4] TgtFloor≠0 구간 D6 추가 쓰기 0건 확인 (총 {d6WritesAfter}건)");

        Assert.Equal(OperFloor, _gw!.Latest.TgtFloor);
    }

    // ── S9: 다중 AGV 경합 — 단일 소터 선점·핑퐁 차단·클리어 후 재정렬 ─────────

    /// <summary>
    /// S9: AGV1(현재층=층1)이 운영층(2) 정렬 선점 → 이동 → 분류 시작 클리어 →
    ///     이후 재정렬 기입. 선점 구간 동안 추가 SetTgtFloor는 스킵(D6 쓰기 0건).
    /// PASS = 선점 구간 D6 추가 쓰기 0건.
    /// </summary>
    [Fact]
    public async Task S9_MultiAgvContention_TgtFloorSingleOwnership_ThenYield()
    {
        await StartInfraAsync(initialFloor: 1);

        // AGV1 판정: CurFloor=1, 운영층=2 → NotAligned + SetTgtFloor(2) 선점
        var snap1 = _gw!.Latest;
        var dec1  = DepositDecider.Decide(snap1, OperFloor, hold: WcsHold.None);
        Assert.False(dec1.Ready);
        Assert.True(dec1.WriteTgtFloor);

        await _gw.EnqueueAsync(new PlcWrite.SetTgtFloor(OperFloor));
        await WaitUntilAsync(() => _gw!.Latest.TgtFloor == OperFloor, 2000, "AGV1 TgtFloor=2 선점");
        _out.WriteLine("[S9] AGV1 TgtFloor=2 선점 완료");

        int d6At1;
        lock (_tlLock) { d6At1 = _timeline.Count(l => l.Contains("WCS 쓰기 수신: D6")); }

        // 선점 구간에서 추가 SetTgtFloor 시도 → 스킵 (TgtFloor≠0 핑퐁 차단)
        var snap2 = _gw!.Latest;
        var dec2  = DepositDecider.Decide(snap2, OperFloor, hold: WcsHold.None);
        Assert.False(dec2.WriteTgtFloor, "TgtFloor≠0이므로 추가 기입 금지(핑퐁 차단)");
        _out.WriteLine($"[S9] 경합 판정: Ready={dec2.Ready} WriteTgtFloor={dec2.WriteTgtFloor}");

        // 이동 완료 대기
        await WaitUntilAsync(() => _gw!.Latest.CurFloor == OperFloor && _gw!.Latest.Ready, 3000, "이동 완료 CurFloor=2");

        // 핸드셰이크 시작 (분류) → 분류 시작 시 TgtFloor=0 클리어
        var hsTask = _hs!.ExecuteAsync(cellNo: 1, ct: CancellationToken.None);
        await WaitUntilAsync(() => !_gw!.Latest.Ready, 3000, "분류 시작 Ready=0");
        await WaitUntilAsync(() =>
        {
            lock (_tlLock) { return _timeline.Any(l => l.Contains("TgtFloor 클리어")); }
        }, 2000, "TgtFloor 클리어 로그");

        await WaitUntilAsync(() => _gw!.Latest.TgtFloor == 0, 2000, "TgtFloor=0 클리어");

        // ── 선점~클리어 구간 D6 추가 쓰기 0건 단언 ─────────────────────────────
        int d6At2;
        lock (_tlLock) { d6At2 = _timeline.Count(l => l.Contains("WCS 쓰기 수신: D6")); }
        Assert.True(d6At2 - d6At1 == 0,
            $"선점 구간 D6 추가 쓰기 {d6At2 - d6At1}건 (0건이어야 함)");
        _out.WriteLine($"[S9] 선점 구간 D6 추가 쓰기 0건 확인");

        // 핸드셰이크 완료
        await hsTask;
    }

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
/// 전환: IF-08 폴링 단계 삭제 — IF-05 → IF-09 → IF-10 흐름. 핸드셰이크 DB 단언 유지.
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
        _factory = new SimWebApplicationFactory(_port, initialCurFloor: 2);
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
        _factory!.Sim.InjectRSeqOverride = 999;

        await SendIf05Through10Async(_factory!, _client!, 12001, "TEST-BARCODE-3");

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

    // ── S5/S6 공용 흐름 헬퍼 (IF-05 → IF-09 도착 → IF-10) ───────────────────
    internal static async Task SendIf05Through10Async(
        SimWebApplicationFactory factory, HttpClient client, int pId, string barcode)
    {
        // 소터 GW Online 대기
        var deadline = DateTimeOffset.Now.AddSeconds(5);
        while (!factory.IsSorterOnline() && DateTimeOffset.Now < deadline)
            await Task.Delay(50);

        // IF-05
        var if05Req = new { pId, agvNo = 1, barcode, inductionNo = 1, qty = 1, timeStamp = "2026-06-17 10:00:00" };
        var if05Resp = await client.PostAsJsonAsync("/api/v1/destination-query", if05Req);
        Assert.Equal(HttpStatusCode.OK, if05Resp.StatusCode);
        var if05Body = await if05Resp.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.Equal("OK", if05Body!.Result);

        // IF-09 도착 보고 (운영층 정렬)
        var if09Req = new { pId, chuteNo = if05Body.ChuteNo!.Value, agvNo = 1, timeStamp = (string?)null };
        var if09Resp = await client.PostAsJsonAsync("/api/v1/arrival-report", if09Req);
        Assert.Equal(HttpStatusCode.OK, if09Resp.StatusCode);

        // 운영층 정렬 완료 대기
        var d2 = DateTimeOffset.Now.AddSeconds(4);
        while (factory.SorterSnapshot()?.CurFloor != 2 && DateTimeOffset.Now < d2)
            await Task.Delay(50);

        // IF-10
        var if10Req = new { pId, barcode, chuteNo = if05Body.ChuteNo!.Value, agvNo = 1 };
        var if10Resp = await client.PostAsJsonAsync("/api/v1/deposit-report", if10Req);
        Assert.Equal(HttpStatusCode.OK, if10Resp.StatusCode);
    }
}

// ─────────────────────────────────────────────────────────────────────────
// S6: R_Flag 타임아웃 → alarm RFLAG_TIMEOUT + sorter_command TIMEOUT (재시도 없음)
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// S6: InjectRFlagDelayMs ≫ RFlagTimeoutMs → Outcome=RFlagTimeout.
/// 전환: IF-08 폴링 단계 삭제 — IF-05 → IF-09 → IF-10. 핸드셰이크 DB 단언 유지.
/// PASS = alarm 1행(code=RFLAG_TIMEOUT) + sorter_command status=TIMEOUT. 재시도 없음(1행).
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
        // RFlagTimeoutMs=300ms로 단축 — 고장 주입 지연(5000ms)보다 작아 타임아웃 확실히 유발
        _factory = new SimWebApplicationFactory(_port, rFlagTimeoutMs: 300, initialCurFloor: 2);
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
        _factory!.Sim.InjectRFlagDelayMs = 5000;

        await S5RSeqMismatchTests.SendIf05Through10Async(_factory!, _client!, 13001, "TEST-BARCODE-3");

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

        // TIMEOUT 행 1건(재시도 아님 — 포기 1행)
        var timeoutCount = await assertDb.SorterCommands
            .CountAsync(c => c.Status == SorterCommandStatus.TIMEOUT);
        Assert.Equal(1, timeoutCount);

        var alarm = await assertDb.Alarms
            .Where(a => a.Code == "RFLAG_TIMEOUT")
            .FirstOrDefaultAsync();
        Assert.NotNull(alarm);
        Assert.Equal("RFLAG_TIMEOUT", alarm.Code);
        _out.WriteLine($"[S6] alarm code={alarm.Code}, sorter_command status=TIMEOUT (재시도 없음)");
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
// S7: OFFLINE 전이당 1건 alarm — IF-08 무관(게이트웨이 OFFLINE 이벤트). 그대로 유지.
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// S7: OFFLINE 전이당 정확히 1건 alarm, ONLINE 복구 후 재전이도 1건.
/// 게이트웨이 OFFLINE 이벤트 경로(IF-08 폐지와 무관) — 무변경 유지.
/// </summary>
public class S7OfflineAlarmTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _out;
    private readonly int _port;
    private SimWebApplicationFactory? _factory;

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
        _factory = new SimWebApplicationFactory(_port, rFlagTimeoutMs: 3000);
        await _factory.StartSimAsync();
        using var client = _factory.CreateClient();

        await WaitUntilAsync(() => Task.FromResult(_factory!.IsSorterOnline()),
            timeoutMs: 3000, msg: "Phase 1 준비: GW Online=true 초기 대기");
        _out.WriteLine("[S7] Phase 1: GW Online=true 확인");

        // ── Phase 1: 1차 OFFLINE 전이 ──────────────────────────────────────
        await _factory.Sim.StopAsync();
        _out.WriteLine("[S7] Phase 1: SimServer 종료 — 1차 OFFLINE 전이 대기");

        int offlineWaitMs = (3 * 500) + 2000; // ~3500ms
        await WaitUntilAsync(async () =>
        {
            using var db = _factory!.CreateDbScope();
            return await db.Alarms.AnyAsync(a => a.Code == "OFFLINE");
        }, timeoutMs: offlineWaitMs, msg: "1차 alarm OFFLINE DB 기록");

        await WaitUntilExactAsync(async () =>
        {
            using var db = _factory!.CreateDbScope();
            return await db.Alarms.CountAsync(a => a.Code == "OFFLINE");
        }, expected: 1, stablePollMs: 60, stableCount: 5,
           timeoutMs: 1000, msg: "1차 OFFLINE alarm 1건 안정 확인");

        _out.WriteLine("[S7] Phase 1: OFFLINE alarm 1건 안정 확인");

        // ── Phase 2: ONLINE 복구 ────────────────────────────────────────────
        await _factory.RestartSimAsync();
        _out.WriteLine("[S7] Phase 2: SimServer 재기동 — GW Online=true 폴링 대기");

        await WaitUntilAsync(() => Task.FromResult(_factory!.IsSorterOnline()),
            timeoutMs: 3000, msg: "Phase 2: GW Online=true 복구");
        _out.WriteLine("[S7] Phase 2: GW Online=true 확인 — _online 리셋 보장");

        // ── Phase 3: 2차 OFFLINE 전이 (재전이 단언) ─────────────────────────
        await _factory.Sim.StopAsync();
        _out.WriteLine("[S7] Phase 3: SimServer 2차 종료 — 재전이 OFFLINE 대기");

        await WaitUntilAsync(async () =>
        {
            using var db = _factory!.CreateDbScope();
            return await db.Alarms.CountAsync(a => a.Code == "OFFLINE") >= 2;
        }, timeoutMs: offlineWaitMs, msg: "2차 alarm OFFLINE DB 기록");

        await WaitUntilExactAsync(async () =>
        {
            using var db = _factory!.CreateDbScope();
            return await db.Alarms.CountAsync(a => a.Code == "OFFLINE");
        }, expected: 2, stablePollMs: 60, stableCount: 5,
           timeoutMs: 1000, msg: "2차 OFFLINE alarm 2건 안정 확인");

        _out.WriteLine("[S7] Phase 3: OFFLINE alarm 재전이 2건 안정 확인");
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
    /// count()가 expected 값을 stableCount회 연속 반환할 때까지 폴링(추가 전이 없음 = 안정).
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
// S8: FULL·PAUSED — IF-05 상류 필터로 재타겟 (구 IF-08 FULL/PAUSED 응답 → IF-05 NG)
// ─────────────────────────────────────────────────────────────────────────

/// <summary>
/// S8 전용: FakeModbusMaster 기반이지만 DB 이름을 인스턴스별 Guid로 분리.
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

            var dbOpts = new DbContextOptionsBuilder<WcsDbContext>()
                .UseSqlite(_anchor)
                .ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
                .Options;
            using var db = new WcsDbContext(dbOpts);
            db.Database.EnsureCreated();
            DbSeeder.Seed(db, new Dictionary<string, int> { ["1"] = 1, ["2"] = 2 });

            var sorterDest = db.Destinations
                .First(d => d.DestType == Wcs.Data.DestType.SORTER_3D && d.IsActive);
            var bundle = new SorterBundleHandle(
                destinationId: sorterDest.Id,
                chuteNo:       sorterDest.ChuteNo,
                polling:       _polling!,
                handshake:     _handshake!);
            _registry = new FakeSorterGatewayRegistry(bundle);

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

    // 비동기 종료 — IHost 종료를 비동기로 수행해 teardown sync-over-async 데드락 회피.
    public override async ValueTask DisposeAsync()
    {
        // 쓰기 큐 채널을 먼저 완료(Complete)시켜 PlcPollingService.RunWriteConsumerAsync의
        // `await foreach (ReadAllAsync)`가 우아하게 종료되게 한다.
        // (CTS 취소만으로는 빈 채널에 parked된 ReadAllAsync가 깨어나지 않는 타이밍 경쟁이 있어
        //  StopAsync가 _writeTask를 영원히 await → 호스트 종료 데드락. 채널 완료는 결정적으로 루프를 끝낸다.)
        _writeQueue.Writer.TryComplete();
        await base.DisposeAsync().ConfigureAwait(false);
        _anchor.Dispose();
        GC.SuppressFinalize(this);
    }

    protected override void Dispose(bool disposing)
    {
        // 동기 Dispose 경로에서 base.Dispose(disposing)를 호출하면 WebApplicationFactory가
        // IHost를 sync-over-async로 블로킹 종료하는데, Program.cs의 app.Run()은 별도 스레드에서
        // 돌고 있어 teardown이 데드락한다(테스트호스트가 응답 불가 → 비활성 타임아웃 → 중단).
        // IHost 종료는 DisposeAsync(비동기)에 일임하고, 여기서는 앵커 연결만 정리한다.
        if (disposing) _anchor.Dispose();
    }
}

/// <summary>
/// S8: 슈트 FULL/PAUSED → IF-05 상류 필터로 NG (구 IF-08 응답 단언 재타겟).
/// FULL이면 NG, 비움 후 OK / PAUSED 목적지면 NG.
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
    public async Task S8_Chute_Full_Then_Cleared_Ok()
    {
        using var scope    = _factory!.Services.CreateScope();
        var       db       = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        var       capacity = scope.ServiceProvider.GetRequiredService<IChuteCapacityService>();

        // TEST-BARCODE-1 → chuteNo=1 (CHUTE). 그 목적지를 FULL로 만든다.
        var dest1   = db.Destinations.First(d => d.ChuteNo == 1 && d.DestType == DestType.CHUTE);
        var detail1 = db.ChuteDetails.First(cd => cd.DestinationId == dest1.Id);
        var fullQty = detail1.WorkFullQty;

        Assert.Equal(WcsHold.None, capacity.GetHold(dest1.Id));

        capacity.OnReserved(dest1.Id, fullQty);
        Assert.Equal(WcsHold.Full, capacity.GetHold(dest1.Id));

        // IF-05 FULL 상류 필터 → NG (chuteNo=null)
        var resp = await _client!.PostAsJsonAsync("/api/v1/destination-query",
            new { pId = 15001, agvNo = 1, barcode = "TEST-BARCODE-1", inductionNo = 1, qty = 1, timeStamp = (string?)null });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.NotNull(body);
        Assert.Equal("NG", body.Result);
        Assert.Null(body.ChuteNo);
        _out.WriteLine($"[S8] FULL → IF-05 NG chuteNo=null");

        // OnCleared → NORMAL 복귀 → IF-05 OK
        await capacity.OnCleared(dest1.Id);
        var resp2 = await _client!.PostAsJsonAsync("/api/v1/destination-query",
            new { pId = 15002, agvNo = 1, barcode = "TEST-BARCODE-1", inductionNo = 1, qty = 1, timeStamp = (string?)null });
        var body2 = await resp2.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.NotNull(body2);
        Assert.Equal("OK", body2.Result);
        Assert.Equal(1, body2.ChuteNo);
        _out.WriteLine($"[S8] OnCleared 후 → IF-05 OK chuteNo={body2.ChuteNo}");
    }

    [Fact]
    public async Task S8_Chute_Paused_Ng()
    {
        // TEST-BARCODE-PAUSED → destPaused(chuteNo=6, status PAUSED) → IF-05 NG
        var resp = await _client!.PostAsJsonAsync("/api/v1/destination-query",
            new { pId = 15003, agvNo = 1, barcode = "TEST-BARCODE-PAUSED", inductionNo = 1, qty = 1, timeStamp = (string?)null });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.NotNull(body);
        Assert.Equal("NG", body.Result);
        Assert.Null(body.ChuteNo);
        _out.WriteLine($"[S8] PAUSED → IF-05 NG chuteNo=null");
    }
}

// ─────────────────────────────────────────────────────────────────────────
// DTO (테스트 전용 역직렬화)
// ─────────────────────────────────────────────────────────────────────────
// DestinationQueryResponse, ArrivalReportResponse, DepositReportResponse는
// ApiIntegrationTests.cs / Dtos.cs에서 정의되어 있으므로 여기서는 중복 정의하지 않음.
