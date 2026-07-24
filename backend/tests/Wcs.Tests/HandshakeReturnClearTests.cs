using System.Net;
using System.Net.Sockets;
using Wcs.Core;
using Wcs.PlcGateway;
using Wcs.Sim3ds;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// S-TWO-FLOOR-CONTROL C1 — R 영역 클리어 시점을 "R_Flag==1 즉시"에서
//   "Ready==1(복귀 완료) 관측 후"로 지연(성공 경로) + 처리 시각(tiltedAt/returnedAt) 계측 검증.
//
// 검증 방식: 실 SimServer(TCP) + PlcPollingService + HandshakeOrchestrator 직접 번들
//   (HandshakeResidueTests 하니스 패턴 재사용). Outcome·시각·관측 훅(OnStage/OnWrite/OnRegisterChange)
//   으로 실증. DB 불요(3시각 DB 단조는 E2E에서 실증).
//
// 시나리오:
//   R1 무-이동 사이클 → 즉시 clear(추가 지연 0)·returnedAt≈tiltedAt
//   R2 복귀 이동 사이클 → Ready==1까지 R 유지, Ready==1 이후에만 clear(레지스터 순서)·returnedAt>tiltedAt
//   R3 복귀 대기 타임아웃(Ready 미복귀) → Success·tiltedAt 기입·returnedAt=NULL·HS_RETURN_TIMEOUT·ClearR ack
//   R4 불일치 회귀 → 즉시 clear·tiltedAt 기입·returnedAt=NULL(현행 유지)
// ════════════════════════════════════════════════════════════════════════════

[Collection("RealSimSerial")]
public sealed class HandshakeReturnClearTests
{
    private readonly ITestOutputHelper _out;
    public HandshakeReturnClearTests(ITestOutputHelper output) => _out = output;

    // ─────────────────────────────────────────────────────────────────────────
    // 테스트 하니스 — Sim + 큐 + GW + 핸드셰이크 번들 + 관측 훅 캡처
    // ─────────────────────────────────────────────────────────────────────────
    private sealed class Harness : IAsyncDisposable
    {
        public SimServer             Sim   { get; }
        public PlcWriteQueue         Queue { get; }
        public PlcPollingService     Gw    { get; }
        public HandshakeOrchestrator Hs    { get; }

        private readonly object _lock = new();
        private readonly List<(string action, string detail)> _stages = new();
        private readonly List<(string action, string detail)> _writes = new();
        private readonly List<(string reg, int oldV, int newV)> _regChanges = new();

        public IReadOnlyList<(string action, string detail)> Stages
        { get { lock (_lock) return _stages.ToList(); } }
        public IReadOnlyList<(string action, string detail)> Writes
        { get { lock (_lock) return _writes.ToList(); } }
        public IReadOnlyList<(string reg, int oldV, int newV)> RegChanges
        { get { lock (_lock) return _regChanges.ToList(); } }

        private Harness(SimServer.Options simOpt, PlcGatewayOptions gwOpt)
        {
            Sim   = new SimServer(simOpt);
            Queue = new PlcWriteQueue();
            Gw    = new PlcPollingService(gwOpt, Queue);
            Hs    = new HandshakeOrchestrator(Gw, gwOpt);

            Hs.OnStage          += (a, d) => { lock (_lock) _stages.Add((a, d)); };
            Gw.OnWrite          += (a, d) => { lock (_lock) _writes.Add((a, d)); };
            Gw.OnRegisterChange += (r, o, n) => { lock (_lock) _regChanges.Add((r, o, n)); };
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

    private static SimServer.Options DefaultSimOpt(int port, int moveMs = 80, int sortMs = 120,
        int initialCurFloor = 2) => new()
    {
        Host = "127.0.0.1", Port = port,
        TiltDelayMs = 40, SortDurationMs = sortMs, MoveDurationMs = moveMs,
        InitialCurFloor = initialCurFloor, SimLoopMs = 10,
    };

    private static PlcGatewayOptions DefaultGwOpt(int port, int returnReadyTimeoutMs = 5000) => new()
    {
        Host = "127.0.0.1", Port = port,
        PollIntervalMs = 25, OfflineAfterFailures = 3, WriteTimeoutMs = 500,
        RFlagPollMs = 20, RFlagTimeoutMs = 4000, CFlagTimeoutMs = 2000,
        RFlagClearConfirmTimeoutMs = 2000, ReturnReadyTimeoutMs = returnReadyTimeoutMs,
    };

    private async Task<Harness> StartRobustAsync(
        Func<SimServer.Options, SimServer.Options>? simTweak = null, int returnReadyTimeoutMs = 5000)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            int port   = GetFreePort();
            var simOpt = DefaultSimOpt(port);
            if (simTweak is not null) simOpt = simTweak(simOpt);
            var gwOpt  = DefaultGwOpt(port, returnReadyTimeoutMs);
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

    // ─────────────────────────────────────────────────────────────────────────
    // R1 — 무-이동 사이클: R_Flag==1 관측 시 이미 Ready==1 → 즉시 clear(추가 지연 0).
    //   Sim MoveDuration을 크게(1000ms) 잡아, 만약 이동이 잘못 발생하면 gap이 ~1000ms가 되도록.
    //   실제로는 이동 없음(TgtFloor 미기입)이라 gap<300ms → "이동 지연 없음"을 대조로 실증.
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task R1_NoMove_ImmediateClear_ReturnedAtNearTilted()
    {
        await using var h = await StartRobustAsync(simTweak: o => o with { MoveDurationMs = 1000 });

        var result = await h.Hs.ExecuteAsync(cellNo: 5, ct: CancellationToken.None);
        _out.WriteLine($"[R1] Outcome={result.Outcome} tilted={result.TiltedAt:HH:mm:ss.fff} returned={result.ReturnedAt:HH:mm:ss.fff}");

        Assert.Equal(HandshakeOutcome.Success, result.Outcome);
        Assert.NotNull(result.TiltedAt);
        Assert.NotNull(result.ReturnedAt);
        Assert.True(result.ReturnedAt >= result.TiltedAt, "단조: returnedAt >= tiltedAt");

        // 무-이동 = 이동 지연 없음. MoveDuration=1000이지만 이동을 안 하므로 gap이 그보다 훨씬 작다.
        var gap = (result.ReturnedAt!.Value - result.TiltedAt!.Value).TotalMilliseconds;
        _out.WriteLine($"[R1] gap(returned-tilted)={gap}ms (MoveDuration=1000ms — 이동 없음)");
        Assert.True(gap < 300, $"무-이동 즉시 clear(추가 지연 0): gap={gap}ms < 300ms");

        // ClearR 반영(R_Flag=0 관측)까지 대기 — enqueue는 비동기라 즉시 단언은 컨슈머와 경쟁.
        await WaitUntilAsync(() => !h.Gw.Latest.RFlag, 3000, "ClearR 반영(R_Flag=0)");

        // 복귀 대기 타임아웃 미발생 + R 클리어 발생.
        Assert.DoesNotContain(h.Stages, s => s.action == "HS_RETURN_TIMEOUT");
        Assert.Contains(h.Writes, w => w.action == "CLEAR_R");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // R2 — 복귀 이동 사이클: 분류 후 복귀 이동(Ready=0 구간) 동안 R 영역 유지 →
    //   Ready==1(복귀 완료) 이후에만 ClearR. 레지스터 전이 순서로 "clear가 Ready==1 이후"를 실증.
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task R2_ReturnMove_RHeldUntilReady_ClearAfterReady()
    {
        // CurFloor=1 시작 + 넉넉한 MoveDuration(400ms)로 복귀 이동을 결정적으로 만든다.
        await using var h = await StartRobustAsync(
            simTweak: o => o with { InitialCurFloor = 1, MoveDurationMs = 400, SortDurationMs = 250 },
            returnReadyTimeoutMs: 8000);

        var hsTask = h.Hs.ExecuteAsync(cellNo: 7, ct: CancellationToken.None);

        // 분류 시작(Ready=0) 관측 후 복귀 목표층(2)을 기입 → Sim이 분류 후 복귀 이동을 수행.
        await WaitUntilAsync(() => !h.Gw.Latest.Ready, 3000, "분류 시작 Ready=0");
        await h.Gw.EnqueueAsync(new PlcWrite.SetTgtFloor(2));

        var result = await hsTask;
        _out.WriteLine($"[R2] Outcome={result.Outcome} tilted={result.TiltedAt:HH:mm:ss.fff} returned={result.ReturnedAt:HH:mm:ss.fff}");

        Assert.Equal(HandshakeOutcome.Success, result.Outcome);
        Assert.NotNull(result.TiltedAt);
        Assert.NotNull(result.ReturnedAt);

        // 복귀 이동 사이클: returnedAt - tiltedAt ≈ MoveDuration(측정 가능한 간격 > 0).
        var gap = (result.ReturnedAt!.Value - result.TiltedAt!.Value).TotalMilliseconds;
        _out.WriteLine($"[R2] gap(returned-tilted)={gap}ms (MoveDuration=400ms — 이동 있음)");
        Assert.True(gap >= 200, $"복귀 이동 간격이 측정됨: gap={gap}ms >= 200ms");

        // 복귀 대기 스테이지 발화(이동 구간 존재).
        Assert.Contains(h.Stages, s => s.action == "HS_RETURN_WAIT");

        // ClearR 반영(R_Flag=0 관측)까지 대기 — R_Flag 1→0 전이가 폴에 잡히도록.
        await WaitUntilAsync(() => !h.Gw.Latest.RFlag, 3000, "ClearR 반영(R_Flag=0)");

        // 레지스터 전이 순서: R_Flag(0→1 틸트) → Ready(0→1 복귀완료) → R_Flag(1→0 클리어).
        //   clear(R_Flag→0)가 Ready→1 이후임을 인덱스로 실증(= Ready==1 전엔 R 유지).
        var rc = h.RegChanges;
        int idxRFlagUp   = FirstIndex(rc, ("R_Flag", 0, 1));
        int idxReadyUp   = FirstIndexFrom(rc, ("Ready", 0, 1), idxRFlagUp);
        int idxRFlagDown = FirstIndexFrom(rc, ("R_Flag", 1, 0), idxReadyUp);
        _out.WriteLine($"[R2] RegChange 순서: R_Flag↑={idxRFlagUp} Ready↑={idxReadyUp} R_Flag↓={idxRFlagDown}");
        Assert.True(idxRFlagUp >= 0,   "R_Flag 0→1(틸트) 전이 관측");
        Assert.True(idxReadyUp > idxRFlagUp, "Ready 0→1(복귀 완료)이 R_Flag 틸트 이후");
        Assert.True(idxRFlagDown > idxReadyUp, "R_Flag 1→0(클리어)가 Ready 0→1(복귀) 이후 — R가 복귀까지 유지됨");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // R3 — 복귀 대기 타임아웃: Ready 복귀가 상한(ReturnReadyTimeoutMs)보다 오래 걸림 →
    //   Success·tiltedAt 기입·returnedAt=NULL·HS_RETURN_TIMEOUT·ClearR ack.
    //   MoveDuration(3000ms) ≫ ReturnReadyTimeoutMs(250ms)로 결정적 유발.
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task R3_ReturnWaitTimeout_SuccessButReturnedAtNull_AlarmStage()
    {
        await using var h = await StartRobustAsync(
            simTweak: o => o with { InitialCurFloor = 1, MoveDurationMs = 3000, SortDurationMs = 250 },
            returnReadyTimeoutMs: 250);

        var hsTask = h.Hs.ExecuteAsync(cellNo: 9, ct: CancellationToken.None);

        await WaitUntilAsync(() => !h.Gw.Latest.Ready, 3000, "분류 시작 Ready=0");
        await h.Gw.EnqueueAsync(new PlcWrite.SetTgtFloor(2));  // 긴 복귀 이동 유발.

        var result = await hsTask;
        _out.WriteLine($"[R3] Outcome={result.Outcome} tilted={result.TiltedAt:HH:mm:ss.fff} returned={result.ReturnedAt}");

        // 분류 자체는 완료·대사 일치 → Success. 복귀만 미측정 → returnedAt=NULL + 알람.
        Assert.Equal(HandshakeOutcome.Success, result.Outcome);
        Assert.NotNull(result.TiltedAt);          // R_Flag==1 관측(틸트) 기입.
        Assert.Null(result.ReturnedAt);           // 복귀 미관측 → NULL.
        Assert.Contains(h.Stages, s => s.action == "HS_RETURN_TIMEOUT");

        // 타임아웃에도 ClearR로 완료 ack(온라인) — R 잔류 방지. 반영(R_Flag=0)까지 대기 후 단언.
        await WaitUntilAsync(() => !h.Gw.Latest.RFlag, 3000, "타임아웃 ClearR 반영(R_Flag=0)");
        Assert.Contains(h.Writes, w => w.action == "CLEAR_R");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // R4 — 불일치 회귀: R_Seq 불일치는 현행대로 R_Flag==1 즉시 clear.
    //   tiltedAt 기입(R_Flag==1 관측)·returnedAt=NULL(복귀 대기 없음).
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task R4_Mismatch_ImmediateClear_TiltedSet_ReturnedNull()
    {
        await using var h = await StartRobustAsync();

        h.Sim.InjectRSeqOverride = 999;  // R_Seq를 C_Seq와 다른 값으로 → 불일치.

        var result = await h.Hs.ExecuteAsync(cellNo: 3, ct: CancellationToken.None);
        _out.WriteLine($"[R4] Outcome={result.Outcome} tilted={result.TiltedAt:HH:mm:ss.fff} returned={result.ReturnedAt}");

        Assert.Equal(HandshakeOutcome.RSeqMismatch, result.Outcome);
        Assert.NotNull(result.TiltedAt);   // R_Flag==1 관측 시 항상 기입(성공·불일치).
        Assert.Null(result.ReturnedAt);    // 불일치는 복귀 계측 대상 아님.
        Assert.DoesNotContain(h.Stages, s => s.action == "HS_RETURN_WAIT");

        // 즉시 ClearR 반영(R_Flag=0)까지 대기 후 단언(enqueue-컨슈머 경쟁 회피).
        await WaitUntilAsync(() => !h.Gw.Latest.RFlag, 3000, "불일치 ClearR 반영(R_Flag=0)");
        Assert.Contains(h.Writes, w => w.action == "CLEAR_R");
    }

    // ── 헬퍼 ──────────────────────────────────────────────────────────────────
    private static int FirstIndex(IReadOnlyList<(string reg, int oldV, int newV)> list, (string, int, int) key)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i].reg == key.Item1 && list[i].oldV == key.Item2 && list[i].newV == key.Item3) return i;
        return -1;
    }

    private static int FirstIndexFrom(IReadOnlyList<(string reg, int oldV, int newV)> list,
        (string, int, int) key, int from)
    {
        if (from < 0) return -1;
        for (int i = from + 1; i < list.Count; i++)
            if (list[i].reg == key.Item1 && list[i].oldV == key.Item2 && list[i].newV == key.Item3) return i;
        return -1;
    }

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
