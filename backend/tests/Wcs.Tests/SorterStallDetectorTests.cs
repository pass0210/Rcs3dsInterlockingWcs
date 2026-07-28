using System.Linq;
using System.Threading;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
// S-TWO-FLOOR-CONTROL 서브 스프린트 C3 — 항목 1: 샘플링 스톨 fail-loud 감지기 검증.
//
// SorterFloorReturnService(관측 루프)를 직접 구성(FakeModbusMasterForApi = Modbus 슬레이브 스탠드인)하고,
// 스톨 감지기가 **실제 지속 스톨(머리 존재+유휴+TgtFloor==0+머리 불변 N틱)에서만** WARN + operation_log 를
// 에피소드당 1회 발화하고, 정상/에지(큐 빔·정상 사이클링·오프라인·PAUSED)에서 **오탐 0**임을 실증한다.
//
//   CC1.1  오탐 0: 큐 빔 · 정상 사이클(busy 전이 리셋) · 오프라인 · PAUSED 에서 발화 0.
//   CC1.2  실제 스톨에서만 정확히 1회 발화(에피소드당 1회) + 조건 붕괴 후 재무장 → 2번째 에피소드 재발화.
//   CC1.3  관측 전용 무부작용: 발화가 D6 쓰기·pop 을 유발하지 않음(TgtFloor 0 유지·큐 머리 불변).
//   CC1.2(cross-layer)  관측 루프 → IOperationLogger → OperationLogService 컨슈머 → operation_log DB 영속.
//
// [Collection("RealSimSerial")] — 타이밍-민감 정확-카운트 단언을 무거운 실-Sim 테스트와 직렬화
//   (병렬 CPU 경합 지터 flake 제거 — TwoFloorWriteGateI2Tests·SorterPushOperationalTests 동형).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>full OperationLog 엔트리를 캡처하는 IOperationLogger(스톨 detail/Level/식별자 검증용).</summary>
public sealed class RecordingOperationLogger : IOperationLogger
{
    private readonly object _lock = new();
    private readonly List<OperationLog> _entries = new();

    public IReadOnlyList<OperationLog> FullEntries { get { lock (_lock) return _entries.ToList(); } }
    public int CountFor(string action) { lock (_lock) return _entries.Count(e => e.Action == action); }

    public void Log(OperationLog entry) { lock (_lock) _entries.Add(entry); }

    public void Log(OperationLogCategory category, string action, OperationLogLevel level = OperationLogLevel.INFO,
        int? sorterChuteNo = null, long? destinationId = null, string? barcode = null, int? pId = null, string? detail = null)
        => Log(new OperationLog
        {
            At = DateTime.UtcNow, Category = category, Action = action, Level = level,
            SorterChuteNo = sorterChuteNo, DestinationId = destinationId, Barcode = barcode, PId = pId, Detail = detail,
        });
}

[Collection("RealSimSerial")]
public class SorterStallDetectorTests
{
    private readonly ITestOutputHelper _out;
    public SorterStallDetectorTests(ITestOutputHelper output) => _out = output;

    // ── 테스트 더블 ────────────────────────────────────────────────────────────

    /// <summary>IsPaused 를 토글하는 status 더블. Compute 는 스톨 감지기가 호출하면 안 됨(I-2) — 호출수 계측.</summary>
    private sealed class TogglePausedStatusService : IDestinationStatusService
    {
        private volatile bool _paused;
        private int _computeCalls;
        public int ComputeCalls => Volatile.Read(ref _computeCalls);
        public void SetPaused(bool p) => _paused = p;

        public DestinationReadiness Compute(long destinationId, DestType destType)
        {
            Interlocked.Increment(ref _computeCalls);   // 스톨 감지기/write-gate 는 이걸 호출하지 않아야 함.
            return new DestinationReadiness(Ready: false, Full: false, Paused: _paused, Online: true, DenyReason.None);
        }
        public bool SorterHasAssignedCellWithRoomForBarcode(long destinationId, string barcode) => false;
        public bool SorterCanAcceptBarcode(long destinationId, string barcode) => false;
        public bool IsPaused(long destinationId) => _paused;
    }

    private sealed class NoopQueueRestorer : IPendingFloorQueueRestorer
    {
        public Task<int> RestoreAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted  => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped  => CancellationToken.None;
        public void StopApplication() { }
    }

    // ── 헬퍼 ──────────────────────────────────────────────────────────────────

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
        BuildService(IOperationLogger opLog, IDestinationStatusService status, long destId, int chuteNo, int stallTicks,
            out SorterPendingFloorQueues queues)
    {
        var master  = new FakeModbusMasterForApi();   // 기본 online·Ready=1·CurFloor=1·TgtFloor=0.
        var gwOpt   = new PlcGatewayOptions
        {
            Host = "127.0.0.1", Port = 1502,
            PollIntervalMs = 20, OfflineAfterFailures = 3, WriteTimeoutMs = 1000,
            RFlagPollMs = 100, RFlagTimeoutMs = 30000, CFlagTimeoutMs = 5000,
        };
        var wq        = new PlcWriteQueue();
        var polling   = new PlcPollingService(gwOpt, wq, master);
        var handshake = new HandshakeOrchestrator(polling, gwOpt);
        var bundle    = new SorterBundleHandle(destId, chuteNo, polling, handshake);
        var registry  = new FakeSorterGatewayRegistry(bundle);

        queues = new SorterPendingFloorQueues();

        var opts = Options.Create(new WcsOptions
        {
            SorterFloorReturn = new SorterFloorReturnOptions { ObserveIntervalMs = 20, StallSuspectTicks = stallTicks },
        });
        var svc = new SorterFloorReturnService(
            registry, queues, status, new FakeLifetime(), new NoopQueueRestorer(), opLog, new NopTraceLogger(), opts,
            NullLogger<SorterFloorReturnService>.Instance);

        return (svc, master, wq, polling);
    }

    // ════════════════════════════════════════════════════════════════════════
    // CC1.2 / CC1.3: 실제 스톨에서만 정확히 1회 발화 + 관측 전용 무부작용 + 재무장.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task Stall_HeadAlignedIdle_TgtFloor0_FiresOnce_ObserveOnly_ThenReArms()
    {
        const long destId = 700; const int chuteNo = 30; const int stallTicks = 5;
        var opLog  = new RecordingOperationLogger();
        var status = new TogglePausedStatusService();   // 비정지.
        var (svc, master, wq, polling) = BuildService(opLog, status, destId, chuteNo, stallTicks, out var queues);
        await polling.StartAsync(CancellationToken.None);
        try
        {
            master.SetReady(true); master.SetCurFloor(1); master.SetTgtFloor(0);
            await WaitUntilAsync(() => polling.Latest.Online && polling.Latest.Ready
                                    && polling.Latest.CurFloor == 1 && polling.Latest.TgtFloor == 0, 3000, "스냅샷 정착");

            // head=1 == CurFloor=1 → 정렬 완료(기입 안 함) → TgtFloor 0 유지 → 유휴·머리 불변 지속 = 스톨.
            queues.Enqueue(destId, 1);
            await svc.StartAsync(CancellationToken.None);

            // 임계 지속 → 정확히 1회 발화.
            await WaitUntilAsync(() => opLog.CountFor("SORTER_STALL_SUSPECT") >= 1, 4000, "스톨 WARN 발화");
            await Task.Delay(300);   // 계속 유휴 — 에피소드당 1회(지속 중 반복 발화 0).
            Assert.Equal(1, opLog.CountFor("SORTER_STALL_SUSPECT"));

            // CC1.3 관측 전용 무부작용: D6 미기입(TgtFloor 0)·큐 머리 불변(pop 0)·Compute(셀 집계) 미호출.
            Assert.Equal(0, master.GetTgtFloor());
            Assert.Equal(1, queues.Count(destId));
            Assert.Equal(0, status.ComputeCalls);

            // 발화 엔트리 검증: Level=WARN·chuteNo·destId·구조화 detail.
            var e = opLog.FullEntries.First(x => x.Action == "SORTER_STALL_SUSPECT");
            Assert.Equal(OperationLogCategory.STATE, e.Category);
            Assert.Equal(OperationLogLevel.WARN, e.Level);
            Assert.Equal(chuteNo, e.SorterChuteNo);
            Assert.Equal(destId, e.DestinationId);
            Assert.StartsWith("{", e.Detail);
            Assert.Contains("\"headFloor\":1", e.Detail);
            Assert.Contains("\"observedOnly\":true", e.Detail);

            // ── 재무장: 조건 붕괴(TgtFloor≠0) → 리셋(재발화 0) → 재확립 → 2번째 에피소드 발화 ──
            master.SetTgtFloor(9);
            await WaitUntilAsync(() => polling.Latest.TgtFloor == 9, 2000, "TgtFloor 9(조건 붕괴)");
            await Task.Delay(200);
            Assert.Equal(1, opLog.CountFor("SORTER_STALL_SUSPECT"));   // 붕괴 중 재발화 0.

            master.SetTgtFloor(0);
            await WaitUntilAsync(() => polling.Latest.TgtFloor == 0, 2000, "TgtFloor 0(조건 재확립)");
            await WaitUntilAsync(() => opLog.CountFor("SORTER_STALL_SUSPECT") >= 2, 4000, "재무장 후 2번째 에피소드 발화");
            await Task.Delay(200);
            Assert.Equal(2, opLog.CountFor("SORTER_STALL_SUSPECT"));   // 새 에피소드도 1회만.
            Assert.Equal(0, master.GetTgtFloor());                     // 여전히 무부작용.
            Assert.Equal(1, queues.Count(destId));

            _out.WriteLine($"[Stall] 실제 스톨 1회 발화·관측전용(D6 0·큐 불변·Compute {status.ComputeCalls}회)·재무장 후 2번째 발화");
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
            wq.Writer.TryComplete();
            await polling.DisposeAsync();
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // CC1.1(a): 큐 빈 소터(머리 없음) — 유휴 idle 이어도 발화 0.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task Stall_EmptyQueue_IdleOnline_NeverFires()
    {
        const long destId = 710; const int stallTicks = 5;
        var opLog  = new RecordingOperationLogger();
        var status = new TogglePausedStatusService();
        var (svc, master, wq, polling) = BuildService(opLog, status, destId, chuteNo: 30, stallTicks, out _);
        await polling.StartAsync(CancellationToken.None);
        try
        {
            master.SetReady(true); master.SetCurFloor(1); master.SetTgtFloor(0);
            await WaitUntilAsync(() => polling.Latest.Online && polling.Latest.Ready, 3000, "스냅샷 정착");

            await svc.StartAsync(CancellationToken.None);   // 큐 비어 있음(enqueue 0).
            await Task.Delay(stallTicks * 20 * 5 + 400);    // 임계 지속시간의 여러 배 경과.

            Assert.Equal(0, opLog.CountFor("SORTER_STALL_SUSPECT"));   // 머리 없음 → 발화 0.
            _out.WriteLine("[Stall] 큐 빔(유휴 idle) → 발화 0");
        }
        finally { await svc.StopAsync(CancellationToken.None); wq.Writer.TryComplete(); await polling.DisposeAsync(); }
    }

    // ════════════════════════════════════════════════════════════════════════
    // CC1.1(b): 정상 사이클링(busy 전이 반복) — 머리 있어도 busy 창이 카운터 리셋 → 발화 0.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task Stall_NormalCycling_BusyTransitionsReset_NeverFires()
    {
        const long destId = 720; const int stallTicks = 8;
        var opLog  = new RecordingOperationLogger();
        var status = new TogglePausedStatusService();
        var (svc, master, wq, polling) = BuildService(opLog, status, destId, chuteNo: 30, stallTicks, out var queues);
        await polling.StartAsync(CancellationToken.None);
        try
        {
            master.SetReady(true); master.SetCurFloor(1); master.SetTgtFloor(0);
            await WaitUntilAsync(() => polling.Latest.Online && polling.Latest.Ready, 3000, "스냅샷 정착");

            // head=1==CurFloor 여러 개 — 사이클마다 하나씩 소비되며 머리는 계속 존재.
            for (int i = 0; i < 8; i++) queues.Enqueue(destId, 1);
            await svc.StartAsync(CancellationToken.None);

            // 분류 사이클(Ready 1→0→1)을 임계보다 짧은 간격으로 반복 — busy 창이 매번 카운터 리셋.
            for (int i = 0; i < 6; i++)
            {
                master.SetReady(false);
                await WaitUntilAsync(() => !polling.Latest.Ready, 2000, "busy 관측");
                await Task.Delay(30);
                master.SetReady(true);
                await WaitUntilAsync(() => polling.Latest.Ready, 2000, "idle 관측");
                await Task.Delay(30);   // idle 창(~1~2틱) << 임계 8틱.
            }

            Assert.Equal(0, opLog.CountFor("SORTER_STALL_SUSPECT"));   // 정상 사이클링 → 오탐 0.
            _out.WriteLine("[Stall] 정상 사이클링(busy 전이 리셋) → 발화 0");
        }
        finally { await svc.StopAsync(CancellationToken.None); wq.Writer.TryComplete(); await polling.DisposeAsync(); }
    }

    // ════════════════════════════════════════════════════════════════════════
    // CC1.1(c): 오프라인 소터 — 머리 있어도 정당한 미기입 상태 → 발화 0.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task Stall_OfflineSorter_NeverFires()
    {
        const long destId = 730; const int stallTicks = 5;
        var opLog  = new RecordingOperationLogger();
        var status = new TogglePausedStatusService();
        var (svc, master, wq, polling) = BuildService(opLog, status, destId, chuteNo: 30, stallTicks, out var queues);
        await polling.StartAsync(CancellationToken.None);
        try
        {
            master.SetFailReads(true);   // 읽기 실패 주입 → OFFLINE 전이.
            await WaitUntilAsync(() => !polling.Latest.Online, 6000, "오프라인 전이");

            queues.Enqueue(destId, 1);
            await svc.StartAsync(CancellationToken.None);
            await Task.Delay(stallTicks * 20 * 5 + 400);

            Assert.Equal(0, opLog.CountFor("SORTER_STALL_SUSPECT"));   // 오프라인 = 정당한 미기입 → 발화 0.
            _out.WriteLine("[Stall] 오프라인 소터(머리 존재) → 발화 0");
        }
        finally { await svc.StopAsync(CancellationToken.None); wq.Writer.TryComplete(); await polling.DisposeAsync(); }
    }

    // ════════════════════════════════════════════════════════════════════════
    // CC1.1(d): PAUSED 소터 — 정당한 미기입 상태 → 발화 0.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task Stall_PausedSorter_NeverFires()
    {
        const long destId = 740; const int stallTicks = 5;
        var opLog  = new RecordingOperationLogger();
        var status = new TogglePausedStatusService();
        status.SetPaused(true);   // PAUSED.
        var (svc, master, wq, polling) = BuildService(opLog, status, destId, chuteNo: 30, stallTicks, out var queues);
        await polling.StartAsync(CancellationToken.None);
        try
        {
            master.SetReady(true); master.SetCurFloor(1); master.SetTgtFloor(0);
            await WaitUntilAsync(() => polling.Latest.Online && polling.Latest.Ready, 3000, "스냅샷 정착");

            queues.Enqueue(destId, 1);
            await svc.StartAsync(CancellationToken.None);
            await Task.Delay(stallTicks * 20 * 5 + 400);

            Assert.Equal(0, opLog.CountFor("SORTER_STALL_SUSPECT"));   // PAUSED = 정당한 미기입 → 발화 0.
            _out.WriteLine("[Stall] PAUSED 소터(머리 존재·유휴) → 발화 0");
        }
        finally { await svc.StopAsync(CancellationToken.None); wq.Writer.TryComplete(); await polling.DisposeAsync(); }
    }

    // ════════════════════════════════════════════════════════════════════════
    // CC1.1(e): 비활성(StallSuspectTicks ≤ 0) — 스톨 상태여도 발화 0(감지기 off).
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task Stall_DetectorDisabled_TicksZero_NeverFires()
    {
        const long destId = 750;
        var opLog  = new RecordingOperationLogger();
        var status = new TogglePausedStatusService();
        var (svc, master, wq, polling) = BuildService(opLog, status, destId, chuteNo: 30, stallTicks: 0, out var queues);
        await polling.StartAsync(CancellationToken.None);
        try
        {
            master.SetReady(true); master.SetCurFloor(1); master.SetTgtFloor(0);
            await WaitUntilAsync(() => polling.Latest.Online && polling.Latest.Ready, 3000, "스냅샷 정착");

            queues.Enqueue(destId, 1);   // 실제 스톨 상태(머리 정렬·유휴)지만 감지기 비활성.
            await svc.StartAsync(CancellationToken.None);
            await Task.Delay(600);

            Assert.Equal(0, opLog.CountFor("SORTER_STALL_SUSPECT"));   // ≤0 = 비활성 → 발화 0.
            _out.WriteLine("[Stall] StallSuspectTicks=0(비활성) → 스톨 상태여도 발화 0");
        }
        finally { await svc.StopAsync(CancellationToken.None); wq.Writer.TryComplete(); await polling.DisposeAsync(); }
    }

    // ════════════════════════════════════════════════════════════════════════
    // CC1.2 크로스레이어: 관측 루프 → IOperationLogger → OperationLogService 컨슈머 → operation_log DB 영속.
    //   비동기 싱크이므로 로그 출현을 조건 대기 후 캡처(교훈 sim-timeline-log-vs-snapshot-race).
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task Stall_CrossLayer_PersistsToOperationLogTable()
    {
        var dbName = $"StallXLayer_{Guid.NewGuid():N}";
        var anchor = new SqliteConnection($"Data Source={dbName};Mode=Memory;Cache=Shared");
        anchor.Open();
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            var connStr = $"Data Source={dbName};Mode=Memory;Cache=Shared";
            services.AddDbContext<WcsDbContext>(o =>
                o.UseSqlite(connStr).ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)),
                ServiceLifetime.Scoped);
            services.AddSingleton<OperationLogService>();
            await using var sp = services.BuildServiceProvider();

            using (var scope = sp.CreateScope())
                scope.ServiceProvider.GetRequiredService<WcsDbContext>().Database.EnsureCreated();

            var opLogSvc = sp.GetRequiredService<OperationLogService>();
            await ((IHostedService)opLogSvc).StartAsync(CancellationToken.None);   // 백그라운드 컨슈머 기동.

            const long destId = 800; const int chuteNo = 30; const int stallTicks = 5;
            var status = new TogglePausedStatusService();
            var (svc, master, wq, polling) = BuildService(opLogSvc, status, destId, chuteNo, stallTicks, out var queues);
            await polling.StartAsync(CancellationToken.None);
            try
            {
                master.SetReady(true); master.SetCurFloor(1); master.SetTgtFloor(0);
                await WaitUntilAsync(() => polling.Latest.Online && polling.Latest.Ready
                                        && polling.Latest.CurFloor == 1 && polling.Latest.TgtFloor == 0, 3000, "스냅샷 정착");

                queues.Enqueue(destId, 1);
                await svc.StartAsync(CancellationToken.None);

                // 비동기 싱크 — operation_log 행 출현을 조건 대기 후 캡처.
                await WaitUntilAsync(() =>
                {
                    using var scope = sp.CreateScope();
                    var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
                    return db.OperationLogs.Any(o => o.Action == "SORTER_STALL_SUSPECT");
                }, 8000, "operation_log DB 영속");

                using (var scope = sp.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
                    var rows = db.OperationLogs.Where(o => o.Action == "SORTER_STALL_SUSPECT").ToList();
                    Assert.Single(rows);   // 에피소드당 1회.
                    var row = rows[0];
                    Assert.Equal(OperationLogLevel.WARN, row.Level);
                    Assert.Equal(OperationLogCategory.STATE, row.Category);
                    Assert.Equal(chuteNo, row.SorterChuteNo);
                    Assert.Equal(destId, row.DestinationId);
                    Assert.Contains("headFloor", row.Detail);
                    _out.WriteLine($"[Stall] 크로스레이어 DB 영속 — Level={row.Level} detail={row.Detail}");
                }
            }
            finally
            {
                await svc.StopAsync(CancellationToken.None);
                wq.Writer.TryComplete();
                await polling.DisposeAsync();
                await ((IHostedService)opLogSvc).StopAsync(CancellationToken.None);
            }
        }
        finally { anchor.Dispose(); }
    }
}

// ════════════════════════════════════════════════════════════════════════════
// S-TWO-FLOOR-CONTROL 서브 스프린트 C3 — 항목 2: pusher 경량화 비용 절감 실증(CC2.3).
//
// DestinationStatusPusher 의 소터 관찰 경로가 매 틱마다 무거운 Compute(→ ComputeSorterFull 셀 집계)를
// 더 이상 호출하지 않고, 저비용 IsPaused(경량 readiness — I-2 동형)만 호출함을 카운팅 데코레이터로 실증한다.
//   · 발신 동일성(CC2.1)·기존 push 테스트군 회귀 0(CC2.2)은 SorterPushOperationalTests·RcsPushTests·
//     TwoFloorHostRoutingTests 등 기존 스위트가 그대로 GREEN 인 것으로 커버(accept byte-identical).
//   · 여기서는 "발신은 동일, 비용만 절감"의 비용 측면을 spy 로 닫는다(VSE2a/b 동형 패턴 — Compute vs IsPaused).
// ════════════════════════════════════════════════════════════════════════════
[Collection("RealSimSerial")]
public class PusherLightweightReadinessTests
{
    private readonly ITestOutputHelper _out;
    public PusherLightweightReadinessTests(ITestOutputHelper output) => _out = output;

    /// <summary>실 DestinationStatusService 를 감싸 소터 Compute vs IsPaused 호출수를 계측하는 데코레이터.</summary>
    private sealed class CountingStatusDecorator(IDestinationStatusService inner) : IDestinationStatusService
    {
        private int _computeSorter;
        private int _isPaused;
        public int ComputeSorterCount => Volatile.Read(ref _computeSorter);
        public int IsPausedCount      => Volatile.Read(ref _isPaused);

        public DestinationReadiness Compute(long destinationId, DestType destType)
        {
            if (destType == DestType.SORTER_3D) Interlocked.Increment(ref _computeSorter);   // heavy(ComputeSorterFull) 경로.
            return inner.Compute(destinationId, destType);
        }
        public bool SorterHasAssignedCellWithRoomForBarcode(long d, string b) => inner.SorterHasAssignedCellWithRoomForBarcode(d, b);
        public bool SorterCanAcceptBarcode(long d, string b) => inner.SorterCanAcceptBarcode(d, b);
        public bool IsPaused(long destinationId) { Interlocked.Increment(ref _isPaused); return inner.IsPaused(destinationId); }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs, string msg, int pollMs = 20)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTimeOffset.Now > deadline) Assert.Fail($"WaitUntil 타임아웃({timeoutMs}ms): {msg}");
            await Task.Delay(pollMs);
        }
    }

    // ── VSC2: 소터 관찰이 N틱 도는 동안 heavy Compute(ComputeSorterFull) 매-틱 미호출·IsPaused 사용 ──
    [Fact]
    public async Task VSC2_Sorter_Observe_UsesLightReadiness_HeavyComputeNotCalledPerTick()
    {
        await using var rcs = await FakeChuteStateServer.StartAsync();

        CountingStatusDecorator? spy = null;
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl, configureExtra: services =>
        {
            // 기본 등록된 IDestinationStatusService 를 카운팅 데코레이터로 감싸 교체(실 산출은 그대로).
            var descriptor = services.Single(x => x.ServiceType == typeof(IDestinationStatusService));
            services.Remove(descriptor);
            services.AddSingleton<IDestinationStatusService>(sp =>
            {
                var real = ActivatorUtilities.CreateInstance<DestinationStatusService>(sp);
                spy = new CountingStatusDecorator(real);
                return spy;
            });
        });
        _ = factory.CreateClient();

        int sorterChute = factory.SorterChuteNo;

        // 데코레이터 해석(pusher 구성 시점) 대기.
        await WaitUntilAsync(() => spy is not null, 5000, "IDestinationStatusService 데코레이터 해석");

        // 소터 정렬(online·Ready=1·CurFloor=2) → push ready=true 정착.
        factory.FakeMaster.SetReady(true); factory.FakeMaster.SetCurFloor(2); factory.FakeMaster.SetTgtFloor(0);
        await WaitUntilAsync(() => rcs.LastFor(sorterChute) is { Ready: true }, 8000, "소터 push ready=true 정착");

        // 정착 후 baseline 캡처.
        int computeSorterBefore = spy!.ComputeSorterCount;
        int isPausedBefore      = spy.IsPausedCount;

        // 소터는 매 관찰 주기 Observe → 경량 readiness 로 IsPaused 가 계속 호출됨(관찰 틱 다수 경과 실증).
        await WaitUntilAsync(() => spy.IsPausedCount >= isPausedBefore + 5, 5000, "소터 관찰 틱 다수 경과(경량 IsPaused 사용)");

        // ★ heavy Compute(→ ComputeSorterFull 셀 집계)는 소터 관찰에서 매 틱 호출되지 않음(비용 절감 실증).
        Assert.Equal(computeSorterBefore, spy.ComputeSorterCount);
        // 발신은 여전히 동일 — 마지막 발신 = 3(ready).
        Assert.True(rcs.LastFor(sorterChute)!.Ready);

        _out.WriteLine($"[VSC2] 소터 관찰 {spy.IsPausedCount - isPausedBefore}+틱 동안 IsPaused 사용·heavy Compute 증가 0" +
                       $"(before {computeSorterBefore} == after {spy.ComputeSorterCount})");
    }
}
