using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
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
// S-CLEANUP-FIELD Module 1 — 백엔드 관측성 표적 테스트
//
//   D-1  OFFLINE 로그 스팸 억제(전이 1회 상세 + 지속 억제 + ONLINE 복구 1회)  → CleanupFieldM1_OfflineLogTests
//   A-1  HS_R_RESIDUE operation_log 레벨 분류(INFO→WARN 승격)                  → CleanupFieldM1_ClassifierTests
//   D-3  /health liveness(항상 200 + JSON 상태, 부수효과 0)                    → CleanupFieldM1_HttpTests
//   D-4  입력 상한(과길이 barcode/timeStamp·qty 오버플로·IF-10 음수 → 400)      → CleanupFieldM1_HttpTests
//
// 결정적 설계: 고정 sleep 없음, WaitUntil 폴링 동기화. Sim/더블만 사용(실 PLC·RTU 미기동).
// ════════════════════════════════════════════════════════════════════════════

// ── 로그 캡처용 ILogger — 폴 루프 스레드에서 쓰고 테스트 스레드에서 읽으므로 lock 보호 ──
internal sealed record LogEntry(LogLevel Level, string Message, bool HasException);

internal sealed class ListLogger<T> : ILogger<T>
{
    private readonly List<LogEntry> _entries = [];
    private readonly object _lock = new();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    // 모든 레벨 캡처(Debug 강등 라인도 관측하기 위함).
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var msg = formatter(state, exception);
        lock (_lock) { _entries.Add(new LogEntry(logLevel, msg, exception is not null)); }
    }

    public List<LogEntry> Snapshot()
    {
        lock (_lock) { return [.. _entries]; }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

// ════════════════════════════════════════════════════════════════════════════
// D-1 — OFFLINE 지속 중 로그 스팸 억제
// ════════════════════════════════════════════════════════════════════════════

public class CleanupFieldM1_OfflineLogTests
{
    private readonly ITestOutputHelper _out;
    public CleanupFieldM1_OfflineLogTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// D-1: OFFLINE 전이는 스택 포함 상세 1회, 지속 폴 실패는 스택 없이 강등/주기요약, ONLINE 복구 1회.
    /// FakeModbusMasterForApi.SetFailReads(true)로 매 폴 IOException 유발(지속 OFFLINE 시뮬레이션).
    /// 단정: (a) "OFFLINE 전이" ERROR == 1, (b) 예외 첨부(스택) 라인 == 1, (c) 지속 라인은 다수인데도
    ///        ERROR는 여전히 1(폴마다 스택 반복 아님 = 스팸 억제), (d) 주기 요약 WARN ≥ 1,
    ///        (e) 복구 시 "ONLINE" INFO 1회 증가.
    /// </summary>
    [Fact]
    public async Task D1_SustainedOffline_SuppressesLogSpam_OneTransitionAndRecovery()
    {
        var logger = new ListLogger<PlcPollingService>();
        var master = new FakeModbusMasterForApi();   // 초기 Ready=1 · 읽기 성공
        var queue  = new PlcWriteQueue();
        var opt = new PlcGatewayOptions
        {
            Host = "127.0.0.1", Port = 1502,
            PollIntervalMs = 10, OfflineAfterFailures = 3, WriteTimeoutMs = 200,
            RFlagPollMs = 10, RFlagTimeoutMs = 1000, CFlagTimeoutMs = 500,
            OfflineLogSummaryEveryPolls = 3,          // 지속 실패 3회마다 요약 1줄
        };
        var gw = new PlcPollingService(opt, queue, master, logger);

        try
        {
            await gw.StartAsync();

            // 정상 기동 — Online 확립
            await WaitUntilAsync(() => gw.Latest.Online, 3000, "초기 Online");
            int onlineCountBefore = Count(logger, LogLevel.Information, "ONLINE");

            // ── OFFLINE 유도 ──────────────────────────────────────────────────────
            master.SetFailReads(true);
            await WaitUntilAsync(() => !gw.Latest.Online, 3000, "OFFLINE 전이");

            // 지속 OFFLINE 동안 폴이 다수 반복되도록 대기(지속 라인 ≥ 12 관측 = 여러 폴 경과 입증).
            await WaitUntilAsync(() => Count(logger, "지속") >= 12, 5000, "지속 OFFLINE 폴 다수 경과");

            var offlineSnap = logger.Snapshot();
            int transitionLines = offlineSnap.Count(e =>
                e.Level == LogLevel.Error && e.Message.Contains("OFFLINE 전이"));
            int errorLines      = offlineSnap.Count(e => e.Level == LogLevel.Error);
            int exceptionLines  = offlineSnap.Count(e => e.HasException);
            int sustainedLines  = offlineSnap.Count(e => e.Message.Contains("지속"));
            int summaryWarnLines = offlineSnap.Count(e =>
                e.Level == LogLevel.Warning && e.Message.Contains("OFFLINE 지속 — 누적"));

            // (a) 전이 상세는 정확히 1회
            Assert.Equal(1, transitionLines);
            // (c) 지속 폴이 다수(≥12) 발생했는데도 ERROR는 여전히 1 — 폴마다 스택 반복 아님(스팸 억제 핵심)
            Assert.Equal(1, errorLines);
            // (b) 스택(예외 첨부)은 전이 1회에만 — 지속 라인은 스택 없음
            Assert.Equal(1, exceptionLines);
            // 지속 라인은 다수(폴이 계속 돌았음을 입증)
            Assert.True(sustainedLines >= 12, $"지속 라인 {sustainedLines}(≥12 기대)");
            // (d) 주기 요약 WARN ≥ 1 (3폴마다 1줄)
            Assert.True(summaryWarnLines >= 1, $"요약 WARN {summaryWarnLines}(≥1 기대)");

            _out.WriteLine($"[D-1] 전이ERROR={transitionLines} 총ERROR={errorLines} 예외첨부={exceptionLines} " +
                           $"지속라인={sustainedLines} 요약WARN={summaryWarnLines}");

            // ── 복구 ──────────────────────────────────────────────────────────────
            master.SetFailReads(false);
            await WaitUntilAsync(() => gw.Latest.Online, 3000, "ONLINE 복구");

            int onlineCountAfter = Count(logger, LogLevel.Information, "ONLINE");
            // (e) 복구 시 ONLINE INFO 1회 증가
            Assert.Equal(onlineCountBefore + 1, onlineCountAfter);
            _out.WriteLine($"[D-1] ONLINE 로그 {onlineCountBefore}→{onlineCountAfter} (복구 1회)");
        }
        finally
        {
            queue.Writer.TryComplete();
            await gw.StopAsync();
            await gw.DisposeAsync();
        }
    }

    private static int Count(ListLogger<PlcPollingService> log, string contains) =>
        log.Snapshot().Count(e => e.Message.Contains(contains));

    private static int Count(ListLogger<PlcPollingService> log, LogLevel level, string contains) =>
        log.Snapshot().Count(e => e.Level == level && e.Message.Contains(contains));

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs, string msg, int pollMs = 15)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTimeOffset.Now > deadline)
                Assert.Fail($"WaitUntil 타임아웃({timeoutMs}ms): {msg}");
            await Task.Delay(pollMs);
        }
    }
}

// ════════════════════════════════════════════════════════════════════════════
// A-1 — HS_R_RESIDUE operation_log 레벨 분류기(INFO→WARN 승격)
// operation_log HANDSHAKE 레벨은 이 분류기가 단일 진실(Program.cs 구독부가 위임 호출).
// ════════════════════════════════════════════════════════════════════════════

public class CleanupFieldM1_ClassifierTests
{
    [Fact]
    public void A1_Residue_IsWarn_NotInfo()
    {
        // A-1 핵심: 잔류 감지(HS_R_RESIDUE)는 WARN으로 승격(과거 INFO 매몰 → 현장 추적 가능).
        Assert.Equal(OperationLogLevel.WARN, OperationLogClassifier.ForHandshakeStage("HS_R_RESIDUE"));
    }

    [Theory]
    // 실패·이상 계열은 ERROR 유지(RESIDUE_TIMEOUT은 TIMEOUT 포함 → ERROR)
    [InlineData("HS_R_RESIDUE_TIMEOUT", OperationLogLevel.ERROR)]
    [InlineData("HS_RSEQ_MISMATCH",     OperationLogLevel.ERROR)]
    [InlineData("HS_OFFLINE",           OperationLogLevel.ERROR)]
    [InlineData("HS_TIMEOUT",           OperationLogLevel.ERROR)]
    [InlineData("HS_CFLAG_TIMEOUT",     OperationLogLevel.ERROR)]
    // 잔류 감지 → WARN
    [InlineData("HS_R_RESIDUE",         OperationLogLevel.WARN)]
    // 정상 진행 단계 → INFO
    [InlineData("HS_C_SENT",            OperationLogLevel.INFO)]
    [InlineData("HS_R_RECV",            OperationLogLevel.INFO)]
    [InlineData("HS_RSEQ_MATCH",        OperationLogLevel.INFO)]
    [InlineData("HS_R_ARMED",           OperationLogLevel.INFO)]
    [InlineData("HS_CLEAR_R",           OperationLogLevel.INFO)]
    public void A1_HandshakeStageLevels(string action, OperationLogLevel expected)
    {
        Assert.Equal(expected, OperationLogClassifier.ForHandshakeStage(action));
    }
}

// ════════════════════════════════════════════════════════════════════════════
// CleanupFieldM1HttpWebApplicationFactory — 이 스프린트 HTTP 테스트 전용 웹 팩토리.
//
// ⚠ 격리 필수(통합 병렬 충돌 회피): 기존 FakeModbusWebApplicationFactory(ApiIntegrationTests.cs)는
//   `_dbName`이 **static readonly** 이라 그 타입의 모든 인스턴스가 같은 named in-memory SQLite DB를
//   공유한다. ApiIntegrationTests 한 클래스만 쓸 땐(단일 IClassFixture 인스턴스) 무해했으나,
//   이 스프린트가 두 번째 IClassFixture<FakeModbusWebApplicationFactory>를 추가하면 두 클래스 픽스처가
//   같은 DB 이름으로 해석돼 xUnit 클래스 병렬 실행 시 EnsureCreated+Seed가 이중 수행 →
//   'table "agv" already exists' / destination·cell·work_batch UNIQUE 위반이 발생한다.
//
//   해결(기존 관행과 동형): HubWebApplicationFactory/MonitoringWebApplicationFactory/RcsPush… 처럼
//   **인스턴스-고유 _dbName**(Guid)로 자체 팩토리를 둔다. 배선(폴링·소터 레지스트리·시드·teardown)은
//   FakeModbusWebApplicationFactory와 동일하되 DB 이름만 인스턴스 단위로 분리해 완전 격리한다.
//   공개 헬퍼(FakeModbusMasterForApi·NopSorterRegistryFactory·FakeSorterGatewayRegistry)는 재사용.
// ════════════════════════════════════════════════════════════════════════════
public sealed class CleanupFieldM1HttpWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>테스트에서 직접 레지스터를 조작하기 위해 공개.</summary>
    public FakeModbusMasterForApi FakeMaster { get; } = new();

    private readonly PlcWriteQueue     _fakeWriteQueue = new();
    private readonly PlcGatewayOptions _fakeGwOpt      = new()
    {
        Host = "127.0.0.1", Port = 1502,
        PollIntervalMs = 150, OfflineAfterFailures = 3, WriteTimeoutMs = 1000,
        RFlagPollMs = 100, RFlagTimeoutMs = 30000, CFlagTimeoutMs = 5000,
    };
    private readonly PlcPollingService     _fakePolling;
    private readonly HandshakeOrchestrator _fakeHandshake;
    private FakeSorterGatewayRegistry?     _fakeRegistry;

    // 기존 테스트가 스냅샷 조건을 폴링할 수 있도록 public 노출.
    public PlcPollingService? FakePolling => _fakePolling;

    // ── 인스턴스-고유 named in-memory SQLite (static 금지 — 클래스 병렬 충돌 회피 핵심) ──
    private readonly string _dbName = $"CleanupM1Test_{Guid.NewGuid():N}";
    private readonly Microsoft.Data.Sqlite.SqliteConnection _anchorConnection;

    public CleanupFieldM1HttpWebApplicationFactory()
    {
        _anchorConnection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={_dbName};Mode=Memory;Cache=Shared");
        _anchorConnection.Open();

        _fakePolling   = new PlcPollingService(_fakeGwOpt, _fakeWriteQueue, FakeMaster);
        _fakeHandshake = new HandshakeOrchestrator(_fakePolling, _fakeGwOpt);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // base appsettings=SqlServer → 테스트 더블은 인메모리 SQLite. host setting으로 Provider=Sqlite 주입.
        builder.UseSetting("Database:Provider", "Sqlite");

        builder.ConfigureServices(services =>
        {
            // ── WcsDbContext를 인스턴스-고유 named in-memory SQLite로 교체 ──
            var dbDescriptors = services
                .Where(d => d.ServiceType == typeof(DbContextOptions<WcsDbContext>)
                         || d.ServiceType == typeof(WcsDbContext))
                .ToList();
            foreach (var d in dbDescriptors)
                services.Remove(d);

            var connStr = $"Data Source={_dbName};Mode=Memory;Cache=Shared";
            services.AddDbContext<WcsDbContext>(opts =>
                opts.UseSqlite(connStr, sqlite => sqlite.CommandTimeout(30))
                    .ConfigureWarnings(w => w.Ignore(
                        Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)),
                ServiceLifetime.Scoped);

            // ── 스키마 생성 + 시드 ──
            var dbOpts = new DbContextOptionsBuilder<WcsDbContext>()
                .UseSqlite(_anchorConnection)
                .ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
                .Options;
            using var db = new WcsDbContext(dbOpts);
            db.Database.EnsureCreated();
            DbSeeder.Seed(db, new Dictionary<string, int> { ["1"] = 1, ["2"] = 2 });

            // ── SORTER_3D destination 실제 id 조회 후 번들 구성 ──
            var sorterDest = db.Destinations
                .First(d => d.DestType == Wcs.Data.DestType.SORTER_3D && d.IsActive);
            var bundle = new SorterBundleHandle(
                destinationId: sorterDest.Id,
                chuteNo:       sorterDest.ChuteNo,
                polling:       _fakePolling,
                handshake:     _fakeHandshake);
            _fakeRegistry = new FakeSorterGatewayRegistry(bundle);

            // ── SorterRegistryFactory + ISorterGatewayRegistry + null-impl hosted 교체 ──
            var srfToRemove = services
                .Where(d => d.ServiceType == typeof(SorterRegistryFactory)
                         || d.ServiceType == typeof(ISorterGatewayRegistry))
                .ToList();
            foreach (var d in srfToRemove)
                services.Remove(d);

            var nullHosted = services
                .Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == null)
                .ToList();
            foreach (var d in nullHosted)
                services.Remove(d);

            services.AddSingleton<IHostedService>(sp =>
                sp.GetRequiredService<ChuteCapacityService>());

            var nop = new NopSorterRegistryFactory(_fakePolling, _fakeRegistry);
            services.AddSingleton<ISorterGatewayRegistry>(nop);
            services.AddSingleton<IHostedService>(nop);
        });
    }

    // ── 비동기 종료 (teardown 데드락 회피 — FakeModbusWebApplicationFactory와 동일 근거) ──
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
        if (disposing)
            _anchorConnection.Dispose();
    }
}

// ════════════════════════════════════════════════════════════════════════════
// D-3 /health + D-4 입력 상한 — 실 HTTP 왕복(FakeModbus 웹 팩토리, 인스턴스-격리)
// ════════════════════════════════════════════════════════════════════════════

public class CleanupFieldM1_HttpTests : IClassFixture<CleanupFieldM1HttpWebApplicationFactory>
{
    private readonly CleanupFieldM1HttpWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly ITestOutputHelper _out;

    public CleanupFieldM1_HttpTests(CleanupFieldM1HttpWebApplicationFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _client  = factory.CreateClient();
        _out     = output;
    }

    // ── D-3 /health ───────────────────────────────────────────────────────────

    [Fact]
    public async Task D3_Health_Returns200_WithStatusDbSorters()
    {
        // 소터 폴링이 Online 스냅샷을 확립할 때까지 대기(결정적).
        await WaitUntilAsync(() => _factory.FakePolling?.Latest.Online == true, 5000, "소터 Online");

        // 부수효과 0 확인용 — 호출 전 TgtFloor 스냅샷
        int tgtBefore = _factory.FakeMaster.GetTgtFloor();

        var resp = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);   // 항상 200 liveness

        var json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.True(root.GetProperty("db").GetBoolean(), "db=true (in-memory SQLite 연결)");

        var sorters = root.GetProperty("sorters");
        Assert.Equal(JsonValueKind.Array, sorters.ValueKind);
        Assert.True(sorters.GetArrayLength() >= 1, "소터 ≥ 1 (시드 chuteNo=30)");

        var s0 = sorters[0];
        Assert.Equal(30, s0.GetProperty("chuteNo").GetInt32());   // 시드 SORTER_3D chuteNo=30
        Assert.True(s0.GetProperty("online").GetBoolean(), "소터 online=true");
        Assert.True(s0.TryGetProperty("lastPollAt", out _), "lastPollAt 필드 존재");

        // 부수효과 0 — 두 번째 호출도 200, TgtFloor 불변
        var resp2 = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        Assert.Equal(tgtBefore, _factory.FakeMaster.GetTgtFloor());

        _out.WriteLine($"[D-3] /health 200 status=ok db=true sorters[0].chuteNo=30 online=true (부수효과 0)");
    }

    // ── D-4 입력 상한 ─────────────────────────────────────────────────────────

    [Fact]
    public async Task D4_If05_BarcodeTooLong_Returns400()
    {
        var longBarcode = new string('X', 201);   // 스키마 200 초과
        var req = new { pId = 21001, agvNo = 1, barcode = longBarcode, inductionNo = 1, qty = 1, timeStamp = "2026-07-07 10:00:00" };
        var resp = await _client.PostAsJsonAsync("/api/v1/destination-query", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, resp.StatusCode);
        _out.WriteLine("[D-4] IF-05 barcode 201자 → 400");
    }

    [Fact]
    public async Task D4_If05_QtyOverflow_Returns400_NoDataPollution()
    {
        const int pId = 21002;
        var req = new { pId, agvNo = 1, barcode = "TEST-BARCODE-1", inductionNo = 1, qty = int.MaxValue, timeStamp = "2026-07-07 10:00:00" };
        var resp = await _client.PostAsJsonAsync("/api/v1/destination-query", req);

        // 500 아님(int 오버플로 우회 차단) — 400 거부
        Assert.NotEqual(HttpStatusCode.InternalServerError, resp.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);

        // 데이터 오염 0 — DB 도달 전 거부이므로 그 pId로 생성된 piece가 없어야 함
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        Assert.False(db.Pieces.Any(p => p.PId == pId), "qty 오버플로 거부 — piece 미생성(오염 0)");

        _out.WriteLine("[D-4] IF-05 qty=int.MaxValue → 400, piece 미생성(오염 0)");
    }

    [Fact]
    public async Task D4_If05_TimeStampTooLong_Returns400()
    {
        var longTs = new string('9', 31);   // ClientTs 스키마 30 초과
        var req = new { pId = 21003, agvNo = 1, barcode = "TEST-BARCODE-1", inductionNo = 1, qty = 1, timeStamp = longTs };
        var resp = await _client.PostAsJsonAsync("/api/v1/destination-query", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        _out.WriteLine("[D-4] IF-05 timeStamp 31자 → 400");
    }

    [Fact]
    public async Task D4_If10_NegativeQty_Returns400()
    {
        var req = new { pId = 21004, barcode = "TEST-BARCODE-1", chuteNo = 1, agvNo = 1, qty = (int?)-5, timeStamp = (string?)null };
        var resp = await _client.PostAsJsonAsync("/api/v1/deposit-report", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.NotEqual(HttpStatusCode.InternalServerError, resp.StatusCode);
        _out.WriteLine("[D-4] IF-10 qty=-5 → 400");
    }

    [Fact]
    public async Task D4_If05_ValidInput_StillOk_NoRegression()
    {
        // 무변경 가드 — 정상 입력은 새 검증에도 그대로 200 OK.
        var req = new { pId = 21005, agvNo = 1, barcode = "TEST-BARCODE-1", inductionNo = 1, qty = 3, timeStamp = "2026-07-07 10:00:00" };
        var resp = await _client.PostAsJsonAsync("/api/v1/destination-query", req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.NotNull(body);
        Assert.Equal("OK", body!.Result);
        _out.WriteLine($"[D-4] 정상 입력 IF-05 → OK chuteNo={body.ChuteNo} (회귀 없음)");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs, string msg, int pollMs = 25)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTimeOffset.Now > deadline)
                Assert.Fail($"WaitUntil 타임아웃({timeoutMs}ms): {msg}");
            await Task.Delay(pollMs);
        }
    }
}
