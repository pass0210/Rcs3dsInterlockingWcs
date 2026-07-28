using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Wcs.Api;
using Wcs.Core;
using Wcs.PlcGateway;
using Wcs.Sim3ds;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// S-IF10-CWRITE-SETTLE-DELAY — arming 이후·C(CellAssign) 기입 이전에 삽입한 설정 기반
//   "안착 지연"의 정확성·비블로킹·종결 안전을 실 SimServer(TCP) + PlcPollingService +
//   HandshakeOrchestrator 직접 번들(HandshakeReturnClearTests 하니스 패턴)로 검증.
//
//   S1  SettleDelayMs>0 → C가 지연 하한 전엔 발생 안 함(하한) / =0 → 추가 지연 0(상한).
//   S2  지연 중 OFFLINE → C 미기입(Offline outcome·CELL_ASSIGN 부재).
//   S3  지연 중 취소(호스트 종료) → C 미기입·깔끔 종결(OCE·CELL_ASSIGN 부재).
//   S4  arming 순서 보존: 잔류→ClearR→(HS_R_ARMED)→안착 지연→C.
//   S5  anchor 경과>지연 → 추가 대기 ≈0(잔여 clamp).
//   + 설정 바인딩: 공통 Timing:SettleDelayMs + 소터별 오버라이드 해소(SettleDelayBindingTests).
//
//   관측은 조건 폴링(고정 sleep 금지)으로 결정성 확보. 각 스테이지 발화 시각(tick)을 기록해
//   "C 기입 시점이 지연 하한 이후"를 post-hoc으로 단언(mid-flight sleep 불필요).
// ════════════════════════════════════════════════════════════════════════════

[Collection("RealSimSerial")]
public sealed class HandshakeSettleDelayTests
{
    private readonly ITestOutputHelper _out;
    public HandshakeSettleDelayTests(ITestOutputHelper output) => _out = output;

    // ─────────────────────────────────────────────────────────────────────────
    // 하니스 — Sim + 큐 + GW + 핸드셰이크 + 스테이지/쓰기 캡처(발화 tick 포함).
    // ─────────────────────────────────────────────────────────────────────────
    private sealed class Harness : IAsyncDisposable
    {
        public SimServer             Sim   { get; }
        public PlcWriteQueue         Queue { get; }
        public PlcPollingService     Gw    { get; }
        public HandshakeOrchestrator Hs    { get; }

        private readonly object _lock = new();
        private readonly List<(string action, string detail, long tick)> _stages = new();
        private readonly List<(string action, string detail, long tick)> _writes = new();

        public IReadOnlyList<(string action, string detail, long tick)> Stages
        { get { lock (_lock) return _stages.ToList(); } }
        public IReadOnlyList<(string action, string detail, long tick)> Writes
        { get { lock (_lock) return _writes.ToList(); } }

        private Harness(SimServer.Options simOpt, PlcGatewayOptions gwOpt)
        {
            Sim   = new SimServer(simOpt);
            Queue = new PlcWriteQueue();
            Gw    = new PlcPollingService(gwOpt, Queue);
            Hs    = new HandshakeOrchestrator(Gw, gwOpt);

            Hs.OnStage += (a, d) => { lock (_lock) _stages.Add((a, d, Environment.TickCount64)); };
            Gw.OnWrite += (a, d) => { lock (_lock) _writes.Add((a, d, Environment.TickCount64)); };
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
            // 쓰기 큐 채널 먼저 완료 → 컨슈머 결정적 종료(teardown 채널 경쟁 회피 — 교훈).
            Queue.Writer.TryComplete();
            await Gw.StopAsync();
            await Gw.DisposeAsync();
            await Sim.DisposeAsync();
        }
    }

    private static SimServer.Options DefaultSimOpt(int port, int initialCurFloor = 2) => new()
    {
        Host = "127.0.0.1", Port = port,
        TiltDelayMs = 30, SortDurationMs = 80, MoveDurationMs = 60,
        InitialCurFloor = initialCurFloor, SimLoopMs = 10,
    };

    private static PlcGatewayOptions DefaultGwOpt(int port, int settleDelayMs) => new()
    {
        Host = "127.0.0.1", Port = port,
        PollIntervalMs = 20, OfflineAfterFailures = 3, WriteTimeoutMs = 400,
        RFlagPollMs = 15, RFlagTimeoutMs = 4000, CFlagTimeoutMs = 2000,
        RFlagClearConfirmTimeoutMs = 2000, ReturnReadyTimeoutMs = 5000,
        SettleDelayMs = settleDelayMs,
    };

    private async Task<Harness> StartRobustAsync(
        int settleDelayMs, Func<SimServer.Options, SimServer.Options>? simTweak = null)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            int port   = GetFreePort();
            var simOpt = DefaultSimOpt(port);
            if (simTweak is not null) simOpt = simTweak(simOpt);
            var gwOpt  = DefaultGwOpt(port, settleDelayMs);
            try { return await Harness.StartAsync(simOpt, gwOpt); }
            catch (SocketException ex)
            {
                last = ex;
                _out.WriteLine($"[포트 경쟁 재시도 {attempt + 1}] port={port}: {ex.Message}");
                await Task.Delay(50);
            }
        }
        throw new Xunit.Sdk.XunitException($"Harness 기동 실패(포트 경쟁 6회): {last?.Message}");
    }

    private static long? StageTick(IReadOnlyList<(string action, string detail, long tick)> list, string action)
        => list.FirstOrDefault(s => s.action == action) is { action: not null } hit ? hit.tick : (long?)null;

    // ─────────────────────────────────────────────────────────────────────────
    // S1(하한) — SettleDelayMs=400: C(CELL_ASSIGN 쓰기 / HS_C_SENT)가 handshake-start(anchor=null)
    //   기준 ~SettleDelayMs 경과 전에는 발생하지 않음. 깨끗한 경로(R_Flag=0)라 arming은 즉시 통과 →
    //   지연이 C 시점을 지배한다. 발화 tick으로 post-hoc 단언(고정 sleep 없음).
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S1_SettleDelay_Positive_CNotBeforeLowerBound()
    {
        const int settle = 400;
        await using var h = await StartRobustAsync(settle);

        long start = Environment.TickCount64;
        var result = await h.Hs.ExecuteAsync(cellNo: 5, ct: CancellationToken.None, depositedAtUtc: null);
        Assert.Equal(HandshakeOutcome.Success, result.Outcome);

        // 안착 지연 스테이지 발화 + C 스테이지 발화 tick 확인.
        long? cSentTick = StageTick(h.Stages, "HS_C_SENT");
        Assert.True(cSentTick.HasValue, "HS_C_SENT 발화");
        Assert.Contains(h.Stages, s => s.action == "HS_SETTLE_WAIT");

        long cDelay = cSentTick!.Value - start;
        _out.WriteLine($"[S1] SettleDelayMs={settle} → C 기입까지 경과={cDelay}ms");
        // 하한: 지연(400ms) − 타이머 해상도/지터 여유(60ms). 0-지연이면 수십 ms라 명확히 구분.
        Assert.True(cDelay >= settle - 60, $"C가 안착 지연 하한 이후 발생: {cDelay}ms >= {settle - 60}ms");

        // CELL_ASSIGN 쓰기도 그 이후 발생.
        await WaitUntilAsync(() => h.Writes.Any(w => w.action == "CELL_ASSIGN"), 3000, "CELL_ASSIGN 반영");
        long cWriteTick = StageTick(h.Writes, "CELL_ASSIGN")!.Value;
        Assert.True(cWriteTick - start >= settle - 60, "CELL_ASSIGN 쓰기도 지연 하한 이후");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // S1(상한) — SettleDelayMs=0: 추가 지연 0(현행과 동일). HS_SETTLE_WAIT 스테이지 자체가 없고
    //   C 기입이 즉시(수십 ms) 이뤄진다 — "경로 무변경·회귀 0" 실증.
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S1b_SettleDelay_Zero_NoExtraDelay_PathUnchanged()
    {
        await using var h = await StartRobustAsync(settleDelayMs: 0);

        long start = Environment.TickCount64;
        var result = await h.Hs.ExecuteAsync(cellNo: 6, ct: CancellationToken.None, depositedAtUtc: null);
        Assert.Equal(HandshakeOutcome.Success, result.Outcome);

        // 지연 생략 → HS_SETTLE_WAIT 스테이지 부재(경로 무변경).
        Assert.DoesNotContain(h.Stages, s => s.action == "HS_SETTLE_WAIT");

        long cDelay = StageTick(h.Stages, "HS_C_SENT")!.Value - start;
        _out.WriteLine($"[S1b] SettleDelayMs=0 → C 기입까지 경과={cDelay}ms(추가 지연 0)");
        Assert.True(cDelay < 200, $"SettleDelayMs=0: C 즉시 기입(추가 지연 0): {cDelay}ms < 200ms");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // S2 — 지연 도중 OFFLINE(Sim 중단) → C 미기입(Offline outcome·CELL_ASSIGN/HS_C_SENT 부재).
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S2_OfflineDuringSettle_NoCWritten()
    {
        await using var h = await StartRobustAsync(settleDelayMs: 3000);

        var hsTask = h.Hs.ExecuteAsync(cellNo: 7, ct: CancellationToken.None, depositedAtUtc: null);

        // 안착 지연 진입 관측 후 Sim 중단(OFFLINE 유발).
        await WaitUntilAsync(() => h.Stages.Any(s => s.action == "HS_SETTLE_WAIT"), 3000, "HS_SETTLE_WAIT 진입");
        await h.Sim.StopAsync();
        await WaitUntilAsync(() => !h.Gw.Latest.Online, 5000, "지연 중 OFFLINE 전이");

        var result = await hsTask;
        _out.WriteLine($"[S2] Outcome={result.Outcome} (지연 중 OFFLINE)");

        Assert.Equal(HandshakeOutcome.Offline, result.Outcome);
        Assert.DoesNotContain(h.Stages, s => s.action == "HS_C_SENT");   // C 큐 투입 안 함.
        Assert.DoesNotContain(h.Writes, w => w.action == "CELL_ASSIGN"); // C 물리 기입 안 함.
    }

    // ─────────────────────────────────────────────────────────────────────────
    // S3 — 지연 도중 호스트 종료(취소) → C 미기입·깔끔 종결(OCE 전파·더티 진행 0).
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S3_CancelDuringSettle_NoCWritten_CleanTerminate()
    {
        await using var h = await StartRobustAsync(settleDelayMs: 3000);
        using var cts = new CancellationTokenSource();

        var hsTask = h.Hs.ExecuteAsync(cellNo: 8, ct: cts.Token, depositedAtUtc: null);

        await WaitUntilAsync(() => h.Stages.Any(s => s.action == "HS_SETTLE_WAIT"), 3000, "HS_SETTLE_WAIT 진입");
        cts.Cancel();  // 호스트 종료(ApplicationStopping) 모사.

        // 취소 전파(OCE) — 더티 진행 0. terminal outcome 반환도 계약상 허용이나 현행 구현은 OCE 전파.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await hsTask);
        _out.WriteLine("[S3] 지연 중 취소 → OCE 전파(C 미기입)");

        Assert.DoesNotContain(h.Stages, s => s.action == "HS_C_SENT");
        Assert.DoesNotContain(h.Writes, w => w.action == "CELL_ASSIGN");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // S4 — arming 순서 보존(잔류): 잔류(R_Flag=1 프리셋) → HS_R_RESIDUE → ClearR(arming) → HS_R_ARMED
    //   → HS_SETTLE_WAIT → HS_C_SENT. 스테이지 발화 tick 순서로 실증(안착 지연이 arming "후"·C "전").
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S4_ArmingOrderPreserved_ResiduePath()
    {
        const int settle = 250;
        await using var h = await StartRobustAsync(settle);

        // 운영 중 잔류(핸드셰이크 시작 시 R_Flag=1) — 기동 StartupClear no-op 이후 런타임 세팅.
        h.Sim.SetRResidue(20, 123);
        await WaitUntilAsync(() => h.Gw.Latest.RFlag, 2500, "GW가 잔류 R_Flag=1 관찰");

        var result = await h.Hs.ExecuteAsync(cellNo: 9, ct: CancellationToken.None, depositedAtUtc: null);
        Assert.Equal(HandshakeOutcome.Success, result.Outcome);

        var st = h.Stages;
        long? residue = StageTick(st, "HS_R_RESIDUE");
        long? armed   = StageTick(st, "HS_R_ARMED");
        long? settleW = StageTick(st, "HS_SETTLE_WAIT");
        long? cSent   = StageTick(st, "HS_C_SENT");
        // arming 단계의 ClearR(첫 HS_CLEAR_R — outcome=Residue)
        long? clearArm = st.FirstOrDefault(s => s.action == "HS_CLEAR_R" && s.detail.Contains("Residue")) is
            { action: not null } hit ? hit.tick : (long?)null;

        _out.WriteLine($"[S4] residue={residue} clearArm={clearArm} armed={armed} settle={settleW} cSent={cSent}");

        Assert.True(residue.HasValue && clearArm.HasValue && armed.HasValue && settleW.HasValue && cSent.HasValue,
            "잔류→ClearR→armed→settle→C 스테이지 전부 발화");
        // 순서: 잔류감지 ≤ ClearR(arming) ≤ armed ≤ 안착 지연 ≤ C 기입.
        Assert.True(residue <= clearArm,  "HS_R_RESIDUE ≤ HS_CLEAR_R(arming)");
        Assert.True(clearArm <= armed,    "HS_CLEAR_R(arming) ≤ HS_R_ARMED");
        Assert.True(armed    <= settleW,  "HS_R_ARMED ≤ HS_SETTLE_WAIT (지연은 arming 후)");
        Assert.True(settleW  <= cSent,    "HS_SETTLE_WAIT ≤ HS_C_SENT (지연은 C 전)");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // S4b — 깨끗한 경로(R_Flag=0): 잔류 없음 → arming 즉시 통과 → 안착 지연 → C. HS_R_RESIDUE 부재 +
    //   HS_SETTLE_WAIT가 HS_C_SENT 앞.
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S4b_ArmingOrderPreserved_CleanPath()
    {
        await using var h = await StartRobustAsync(settleDelayMs: 250);

        var result = await h.Hs.ExecuteAsync(cellNo: 4, ct: CancellationToken.None, depositedAtUtc: null);
        Assert.Equal(HandshakeOutcome.Success, result.Outcome);

        var st = h.Stages;
        Assert.DoesNotContain(st, s => s.action == "HS_R_RESIDUE");  // 잔류 없음(깨끗한 경로).
        long? settleW = StageTick(st, "HS_SETTLE_WAIT");
        long? cSent   = StageTick(st, "HS_C_SENT");
        Assert.True(settleW.HasValue && cSent.HasValue, "HS_SETTLE_WAIT·HS_C_SENT 발화");
        Assert.True(settleW <= cSent, "HS_SETTLE_WAIT ≤ HS_C_SENT");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // S5 — anchor(D2): IF-10 수신~지연 지점 경과가 SettleDelayMs를 이미 초과하면 추가 대기 ≈0(잔여 clamp).
    //   depositedAtUtc를 과거(now−900ms)로 주고 SettleDelayMs=400 → remaining=0 → C 즉시 기입.
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S5_AnchorElapsedBeyondDelay_NoExtraWait()
    {
        const int settle = 400;
        await using var h = await StartRobustAsync(settle);

        long start = Environment.TickCount64;
        var anchor = DateTime.UtcNow.AddMilliseconds(-(settle + 500));  // 지연을 이미 초과한 과거 anchor.
        var result = await h.Hs.ExecuteAsync(cellNo: 3, ct: CancellationToken.None, depositedAtUtc: anchor);
        Assert.Equal(HandshakeOutcome.Success, result.Outcome);

        long cDelay = StageTick(h.Stages, "HS_C_SENT")!.Value - start;
        _out.WriteLine($"[S5] anchor 경과>지연 → C 기입까지 경과={cDelay}ms(추가 대기 ≈0)");
        Assert.True(cDelay < 200, $"anchor 경과가 지연 초과: 추가 대기 ≈0({cDelay}ms < 200ms)");

        // 관측: HS_SETTLE_WAIT 스테이지의 remainingMs=0(잔여 clamp 실증).
        var settleStage = h.Stages.First(s => s.action == "HS_SETTLE_WAIT");
        Assert.Contains("\"remainingMs\":0", settleStage.detail);
    }

    // ── 헬퍼 ──────────────────────────────────────────────────────────────────
    private static async Task WaitUntilAsync(Func<bool> cond, int timeoutMs, string msg, int pollMs = 15)
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

// ════════════════════════════════════════════════════════════════════════════
// S-IF10-CWRITE-SETTLE-DELAY — 설정 바인딩: 공통 Timing:SettleDelayMs + 소터별 오버라이드가
//   실 IConfiguration 바인딩에서 정확히 해소됨을 검증(공통 record 바인딩 + SorterConfig.Timing
//   오버라이드 바인딩 + BuildGatewayOptions 병합 규칙 `t?.SettleDelayMs ?? common`).
//   BuildGatewayOptions는 private static이라 병합 표현식을 문서 규칙 그대로 적용해 검증한다.
// ════════════════════════════════════════════════════════════════════════════
public sealed class SettleDelayBindingTests
{
    private static IConfiguration BuildConfig(IDictionary<string, string?> kv) =>
        new ConfigurationBuilder().AddInMemoryCollection(kv).Build();

    [Fact]
    public void Common_SettleDelayMs_BindsToPlcGatewayOptions()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["Timing:SettleDelayMs"] = "250",
        });

        // Program.cs와 동일 경로: GetSection("Timing").Get<PlcGatewayOptions>().
        var common = cfg.GetSection("Timing").Get<PlcGatewayOptions>() ?? new PlcGatewayOptions();
        Assert.Equal(250, common.SettleDelayMs);
    }

    [Fact]
    public void Default_SettleDelayMs_IsZero_WhenAbsent()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["Timing:RFlagPollMs"] = "100",  // 다른 키만 존재 — SettleDelayMs 미지정.
        });
        var common = cfg.GetSection("Timing").Get<PlcGatewayOptions>() ?? new PlcGatewayOptions();
        Assert.Equal(0, common.SettleDelayMs);   // 코드 기본 0.
    }

    [Fact]
    public void PerSorter_Override_Wins_And_AbsentInheritsCommon()
    {
        var cfg = BuildConfig(new Dictionary<string, string?>
        {
            ["Timing:SettleDelayMs"]              = "250",   // 공통.
            ["Sorters:0:ChuteNo"]                 = "1",
            ["Sorters:0:Timing:SettleDelayMs"]    = "700",   // 소터0 오버라이드.
            ["Sorters:1:ChuteNo"]                 = "2",
            // Sorters:1은 Timing 오버라이드 없음 → 공통 상속.
        });

        var common  = cfg.GetSection("Timing").Get<PlcGatewayOptions>() ?? new PlcGatewayOptions();
        var sorters = cfg.GetSection("Sorters").Get<List<SorterConfig>>() ?? new();

        Assert.Equal(250, common.SettleDelayMs);
        Assert.Equal(700, sorters[0].Timing?.SettleDelayMs);   // 오버라이드 바인딩.
        Assert.Null(sorters[1].Timing?.SettleDelayMs);         // 미지정 → null(공통 상속).

        // BuildGatewayOptions 병합 규칙(t?.SettleDelayMs ?? common.SettleDelayMs)의 해소값.
        int eff0 = sorters[0].Timing?.SettleDelayMs ?? common.SettleDelayMs;
        int eff1 = sorters[1].Timing?.SettleDelayMs ?? common.SettleDelayMs;
        Assert.Equal(700, eff0);   // 오버라이드 우선.
        Assert.Equal(250, eff1);   // 상속.
    }
}
