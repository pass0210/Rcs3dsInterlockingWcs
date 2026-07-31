using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api;
using Wcs.Api.Monitoring;
using Wcs.Data;
using Wcs.PlcGateway;
using Wcs.Sim3ds;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// S-SORT-CYCLE-TIME-METRIC — 평균 사이클 시간(분류시작~복귀) 계측·집계 검증.
//
//   집계 = avg(ReturnedAt − SortStartedAt), SortStartedAt = 분류 시작(Ready 워드 1→0 전이).
//   · 전 행(ArchivedAt 무필터) 중 SortStartedAt·ReturnedAt 둘 다 non-null 행만 n에 포함.
//   · n=0 → avgSeconds=null·200(비크래시). 단조상 음수 미발생(0 나눗셈만 방어).
//
//   본 파일은 두 축을 분리 검증한다:
//     A) CycleTimeAggregationTests — Fake Modbus 팩토리(빠름·병렬)로 GET /api/monitor/cycle-time-avg
//        집계 정확성 + ArchivedAt 포함 + null 제외 + n=0 + 저널 기입(SortStartedAt 지속·단조).
//     B) CycleTimeCaptureTests — 실 Sim(TCP) 핸드셰이크로 Ready 1→0 실관측 → HandshakeResult.SortStartedAt
//        기입 + 단조(SortStartedAt ≤ TiltedAt ≤ ReturnedAt) 실증([Collection] 직렬 — 실 소켓 부하 flake 격리).
//
//   ★ 자동 offset 손세팅만으론 불충분(iter1 교훈) — Evaluator가 실 Sim E2E로 표시=DB Σ/n·양수를 실측한다.
//     여기서는 (A)로 집계 산술, (B)로 실관측 캡처를 각각 잠근다.
// ════════════════════════════════════════════════════════════════════════════

public sealed class CycleTimeAggregationTests
{
    private readonly ITestOutputHelper _out;
    public CycleTimeAggregationTests(ITestOutputHelper output) => _out = output;

    private static long SorterDestId(MonitoringWebApplicationFactory f)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        return db.Destinations.First(d => d.DestType == DestType.SORTER_3D).Id;
    }

    private static Piece NewLoadedPiece(WcsDbContext db, int pid, long destId, DateTime now)
    {
        var piece = new Piece
        {
            PId = pid, IsActive = true, Barcode = $"CT-{pid}", Qty = 1,
            Status = PieceStatus.LOADED, DestinationId = destId,
            CreatedAt = now, UpdatedAt = now,
        };
        db.Pieces.Add(piece);
        db.SaveChanges();
        return piece;
    }

    // ── B-2 happy path + ArchivedAt 포함 + null 제외를 한 팩토리에서 병치 ─────────
    // 행 A: 둘 다 non-null(10s)·비아카이브 → n 포함.
    // 행 B: 둘 다 non-null(20s)·아카이브(ArchivedAt!=null) → n 포함(양성 — ArchivedAt 무필터).
    // 행 C: SortStartedAt=null·ReturnedAt non-null → n 제외.
    // 행 D: SortStartedAt non-null·ReturnedAt=null → n 제외.
    //   기대: n=2, avg = (10+20)/2 = 15.0초.
    [Fact]
    public async Task Aggregate_BothNonNull_IncludesArchived_ExcludesNulls_MatchesHandCalc()
    {
        await using var f = new MonitoringWebApplicationFactory();
        var client = f.CreateClient();
        var destId = SorterDestId(f);

        using (var scope = f.Services.CreateScope())
        {
            var db  = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            var now = DateTime.UtcNow;
            var cell1 = db.Cells.First(c => c.DestinationId == destId && c.CellNo == 1);
            var piece = NewLoadedPiece(db, 43001, destId, now);
            var t0 = now.AddMinutes(-10);

            SorterCommand Cmd(int cseq, DateTime? sortStarted, DateTime? returned, DateTime? archived) => new()
            {
                PieceId = piece.Id, CellId = cell1.Id, CSeq = cseq, CellNo = 1,
                CWrittenAt = t0, DepositedAt = t0, TiltedAt = returned ?? t0,
                SortStartedAt = sortStarted, ReturnedAt = returned, ArchivedAt = archived,
                Status = SorterCommandStatus.COMPLETED, CreatedAt = t0,
            };

            db.SorterCommands.AddRange(
                Cmd(1, t0,                 t0.AddSeconds(10), null),          // A: 10s 포함
                Cmd(2, t0,                 t0.AddSeconds(20), now),           // B: 20s 아카이브 포함
                Cmd(3, null,               t0.AddSeconds(5),  null),          // C: SortStartedAt null → 제외
                Cmd(4, t0,                 null,              null));         // D: ReturnedAt null → 제외
            db.SaveChanges();
        }

        var resp = await client.GetAsync("/api/monitor/cycle-time-avg");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var dto = await resp.Content.ReadFromJsonAsync<CycleTimeAvgDto>();
        Assert.NotNull(dto);
        _out.WriteLine($"[집계] n={dto!.N} avgSeconds={dto.AvgSeconds}");

        Assert.Equal(2, dto.N);                                   // A·B만(C·D 제외).
        Assert.NotNull(dto.AvgSeconds);
        Assert.True(Math.Abs(dto.AvgSeconds!.Value - 15.0) < 0.05, // (10+20)/2 = 15.0초 손계산 일치.
            $"avgSeconds={dto.AvgSeconds} ≈ 15.0 기대");
    }

    // ── B-3 경계: n=0(둘 다 non-null 행 전무) → avgSeconds=null·200(500 아님) ─────
    [Fact]
    public async Task Aggregate_ZeroN_ReturnsNullNotError()
    {
        await using var f = new MonitoringWebApplicationFactory();
        var client = f.CreateClient();

        var resp = await client.GetAsync("/api/monitor/cycle-time-avg");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);          // 500 아님.
        var dto = await resp.Content.ReadFromJsonAsync<CycleTimeAvgDto>();
        Assert.NotNull(dto);
        Assert.Equal(0, dto!.N);
        Assert.Null(dto.AvgSeconds);                              // 0 나눗셈 없이 null.
        _out.WriteLine($"[집계 n=0] avgSeconds={(dto.AvgSeconds?.ToString() ?? "null")} n={dto.N}");
    }

    // ── B-5 기입 정확성 + B-6 단조: 저널 Finalize가 result.SortStartedAt를 그 행에 지속하고
    //   DepositedAt ≤ SortStartedAt ≤ TiltedAt ≤ ReturnedAt 단조가 성립함을 실증(핸드셰이크 result → EF 기입). ─
    [Fact]
    public async Task Journal_Finalize_PersistsSortStartedAt_AndMonotone()
    {
        await using var f = new MonitoringWebApplicationFactory();
        var destId = SorterDestId(f);

        using var scope = f.Services.CreateScope();
        var db      = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        var journal = scope.ServiceProvider.GetRequiredService<ISorterCommandJournal>();

        var cell1 = db.Cells.First(c => c.DestinationId == destId && c.CellNo == 1);
        var now   = DateTime.UtcNow;
        var piece = NewLoadedPiece(db, 43002, destId, now);

        var deposited    = now.AddSeconds(-30);
        var sortStarted  = deposited.AddSeconds(2);
        var tilted       = deposited.AddSeconds(5);
        var returned     = deposited.AddSeconds(12);

        long cmdId = journal.CreateSent(piece.Id, cell1.Id, cSeq: 7, cellNo: 1, depositedAt: deposited);
        var result = new HandshakeResult(
            HandshakeOutcome.Success, 7, 7, 1, "OK",
            TiltedAt: tilted, ReturnedAt: returned, SortStartedAt: sortStarted);
        journal.Finalize(cmdId, result);

        var row = db.SorterCommands.AsNoTracking().First(c => c.Id == cmdId);
        _out.WriteLine($"[저널] deposited={row.DepositedAt:HH:mm:ss.fff} sortStarted={row.SortStartedAt:HH:mm:ss.fff} " +
                       $"tilted={row.TiltedAt:HH:mm:ss.fff} returned={row.ReturnedAt:HH:mm:ss.fff}");

        Assert.NotNull(row.SortStartedAt);
        // SortStartedAt 지속(round-trip 오차 ≤ 1ms).
        Assert.True(Math.Abs((row.SortStartedAt!.Value - sortStarted).TotalMilliseconds) < 1,
            $"SortStartedAt 지속: {row.SortStartedAt} ≈ {sortStarted}");
        // 단조: DepositedAt ≤ SortStartedAt ≤ TiltedAt ≤ ReturnedAt.
        Assert.NotNull(row.DepositedAt);
        Assert.True(row.DepositedAt <= row.SortStartedAt, "DepositedAt ≤ SortStartedAt");
        Assert.True(row.SortStartedAt <= row.TiltedAt,    "SortStartedAt ≤ TiltedAt");
        Assert.True(row.TiltedAt <= row.ReturnedAt,       "TiltedAt ≤ ReturnedAt");
        // 사이클 시간(복귀−분류시작) = 10s (음수 아님).
        var cycle = (row.ReturnedAt!.Value - row.SortStartedAt!.Value).TotalSeconds;
        Assert.True(cycle >= 0, "사이클 시간 ≥ 0(단조상 음수 미발생)");
        Assert.True(Math.Abs(cycle - 10.0) < 0.01, $"사이클 시간 ≈ 10s (실제 {cycle})");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// B) 실 Sim 핸드셰이크 — Ready 1→0(분류 시작) 실관측 → HandshakeResult.SortStartedAt 캡처 + 단조.
//   실 소켓 I/O 타이밍 부하 flake 격리를 위해 RealSimSerial 컬렉션에 편입(HandshakeReturnClearTests 동형).
// ════════════════════════════════════════════════════════════════════════════
[Collection("RealSimSerial")]
public sealed class CycleTimeCaptureTests
{
    private readonly ITestOutputHelper _out;
    public CycleTimeCaptureTests(ITestOutputHelper output) => _out = output;

    private sealed class Harness : IAsyncDisposable
    {
        public SimServer             Sim   { get; }
        public PlcWriteQueue         Queue { get; }
        public PlcPollingService     Gw    { get; }
        public HandshakeOrchestrator Hs    { get; }

        private Harness(SimServer.Options simOpt, PlcGatewayOptions gwOpt)
        {
            Sim   = new SimServer(simOpt);
            Queue = new PlcWriteQueue();
            Gw    = new PlcPollingService(gwOpt, Queue);
            Hs    = new HandshakeOrchestrator(Gw, gwOpt);
        }

        public static async Task<Harness> StartAsync(SimServer.Options simOpt, PlcGatewayOptions gwOpt)
        {
            var h = new Harness(simOpt, gwOpt);
            try
            {
                await h.Sim.StartAsync();
                await h.Gw.StartAsync();
                await WaitUntilAsync(() => h.Gw.Latest.Online, 3000, "GW Online");
                return h;
            }
            catch { await h.DisposeAsync(); throw; }
        }

        public async ValueTask DisposeAsync()
        {
            Queue.Writer.TryComplete();
            await Gw.StopAsync();
            await Gw.DisposeAsync();
            await Sim.DisposeAsync();
        }
    }

    private static SimServer.Options DefaultSimOpt(int port, int sortMs = 150) => new()
    {
        Host = "127.0.0.1", Port = port,
        TiltDelayMs = 40, SortDurationMs = sortMs, MoveDurationMs = 80,
        InitialCurFloor = 2, SimLoopMs = 10,
    };

    private static PlcGatewayOptions DefaultGwOpt(int port) => new()
    {
        Host = "127.0.0.1", Port = port,
        PollIntervalMs = 25, OfflineAfterFailures = 3, WriteTimeoutMs = 500,
        RFlagPollMs = 20, RFlagTimeoutMs = 4000, CFlagTimeoutMs = 2000,
        RFlagClearConfirmTimeoutMs = 2000, ReturnReadyTimeoutMs = 5000,
    };

    private async Task<Harness> StartRobustAsync()
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            int port = GetFreePort();
            try { return await Harness.StartAsync(DefaultSimOpt(port), DefaultGwOpt(port)); }
            catch (SocketException ex)
            {
                last = ex;
                _out.WriteLine($"[포트 경쟁 재시도 {attempt + 1}] port={port}: {ex.Message}");
                await Task.Delay(50);
            }
        }
        throw new Xunit.Sdk.XunitException($"Harness 기동 실패(포트 경쟁 6회): {last?.Message}");
    }

    // ── B-5 캡처 정확성: C 기입 후 소터 분류 시작(Ready 1→0)을 R 폴 루프가 실관측해 SortStartedAt에 기입.
    //   SortStartedAt은 TiltedAt보다 앞(분류→틸트 순서)·ReturnedAt보다 앞(단조). 성공 사이클 실증.
    [Fact]
    public async Task Capture_ReadyOneToZero_SetsSortStartedAt_BeforeTiltAndReturn()
    {
        await using var h = await StartRobustAsync();

        var result = await h.Hs.ExecuteAsync(cellNo: 5, ct: CancellationToken.None);
        _out.WriteLine($"[캡처] Outcome={result.Outcome} sortStarted={result.SortStartedAt:HH:mm:ss.fff} " +
                       $"tilted={result.TiltedAt:HH:mm:ss.fff} returned={result.ReturnedAt:HH:mm:ss.fff}");

        Assert.Equal(HandshakeOutcome.Success, result.Outcome);
        Assert.NotNull(result.SortStartedAt);      // Ready 1→0(분류 시작) 실관측.
        Assert.NotNull(result.TiltedAt);
        Assert.NotNull(result.ReturnedAt);

        // 단조 + 순서: 분류 시작이 틸트보다 앞(분류 소요 존재), 틸트가 복귀보다 앞.
        Assert.True(result.SortStartedAt < result.TiltedAt,
            $"SortStartedAt({result.SortStartedAt:HH:mm:ss.fff}) < TiltedAt({result.TiltedAt:HH:mm:ss.fff})");
        Assert.True(result.TiltedAt <= result.ReturnedAt, "TiltedAt ≤ ReturnedAt");

        // 사이클 시간(복귀−분류시작) 양수(음수 미발생).
        var cycle = (result.ReturnedAt!.Value - result.SortStartedAt!.Value).TotalMilliseconds;
        _out.WriteLine($"[캡처] 사이클(복귀−분류시작)={cycle}ms");
        Assert.True(cycle > 0, $"사이클 시간 양수: {cycle}ms");
    }

    // ── 헬퍼 ──────────────────────────────────────────────────────────────────
    private static async Task WaitUntilAsync(Func<bool> cond, int timeoutMs, string msg, int pollMs = 20)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        while (!cond())
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
