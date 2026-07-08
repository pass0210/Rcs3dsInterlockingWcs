using System.Net;
using System.Net.Sockets;
using Wcs.Core;
using Wcs.PlcGateway;
using Wcs.Sim3ds;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// S-HANDSHAKE-RESIDUE — R_Flag 잔류 대사(arming) + 기동 reconcile 검증 (감사 A-1)
//
// 결함: HandshakeOrchestrator가 R_Flag를 "레벨"로 읽어 직전 건·PLC 기동 잔류 R_Flag=1을
//       새 건의 응답으로 오소비 → 허위 RSEQ_MISMATCH off-by-one 연쇄 자가지속.
// 수정: C 기입 전 R_Flag==0 관찰 보장(arming) + 기동 첫 폴 잔류 ClearR reconcile.
//
// 검증 방식: 실 SimServer(TCP) + PlcPollingService + HandshakeOrchestrator 직접 번들
//   (S234_9GatewayScenarioTests 패턴 재사용). Outcome을 직접 단언 + OnStage/OnWrite/
//   OnRegisterChange 관측 훅으로 잔류값 기록을 실증. DB 불요.
//
// 시나리오(계약 §4):
//   S1 잔류→대사→성공        S2 연속 3건 연쇄 차단→전건 성공
//   S3 기동 reconcile        S4 무잔류 정상(대사 미발화·지연 0)
//   S5 확인 타임아웃 종결     S6 진짜 무응답 타임아웃 회귀
// ════════════════════════════════════════════════════════════════════════════

public sealed class HandshakeResidueTests
{
    private readonly ITestOutputHelper _out;
    public HandshakeResidueTests(ITestOutputHelper output) => _out = output;

    // ── 실측 잔류값(현장 2026-07-06) ─────────────────────────────────────────
    private const int FieldRCellNo = 20;
    private const int FieldRSeq    = 123;

    // ─────────────────────────────────────────────────────────────────────────
    // 테스트 하니스 — Sim + 큐 + GW + 핸드셰이크 번들 + 관측 훅 캡처
    // ─────────────────────────────────────────────────────────────────────────
    private sealed class Harness : IAsyncDisposable
    {
        public SimServer            Sim   { get; }
        public PlcWriteQueue        Queue { get; }
        public PlcPollingService    Gw    { get; }
        public HandshakeOrchestrator Hs   { get; }

        private readonly object _lock = new();
        private readonly List<(string action, string detail)> _stages = new();
        private readonly List<(string action, string detail)> _writes = new();
        private readonly List<(string reg, int oldV, int newV)> _regChanges = new();
        private readonly List<string> _timeline = new();
        private readonly Action<string> _log;

        public IReadOnlyList<(string action, string detail)> Stages
        { get { lock (_lock) return _stages.ToList(); } }
        public IReadOnlyList<(string action, string detail)> Writes
        { get { lock (_lock) return _writes.ToList(); } }
        public IReadOnlyList<(string reg, int oldV, int newV)> RegChanges
        { get { lock (_lock) return _regChanges.ToList(); } }
        public IReadOnlyList<string> Timeline
        { get { lock (_lock) return _timeline.ToList(); } }

        private Harness(SimServer.Options simOpt, PlcGatewayOptions gwOpt, Action<string> log)
        {
            _log  = log;
            Sim   = new SimServer(simOpt, timelineLog: l => { lock (_lock) _timeline.Add(l); });
            Queue = new PlcWriteQueue();
            Gw    = new PlcPollingService(gwOpt, Queue);
            Hs    = new HandshakeOrchestrator(Gw, gwOpt);

            // 관측 훅 구독 — StartAsync 이전에 구독해 기동 reconcile ClearR·전이도 포착.
            Hs.OnStage          += (a, d) => { lock (_lock) _stages.Add((a, d)); };
            Gw.OnWrite          += (a, d) => { lock (_lock) _writes.Add((a, d)); };
            Gw.OnRegisterChange += (r, o, n) => { lock (_lock) _regChanges.Add((r, o, n)); };
        }

        public static async Task<Harness> StartAsync(
            SimServer.Options simOpt, PlcGatewayOptions gwOpt, Action<string> log)
        {
            var h = new Harness(simOpt, gwOpt, log);
            try
            {
                await h.Sim.StartAsync();
                await h.Gw.StartAsync();
                await WaitUntilAsync(() => h.Gw.Latest.Online, 3000, "GW Online");
                return h;
            }
            catch
            {
                // 기동 실패(예: 포트 바인드 경쟁) 시 부분 기동 자원 정리 후 전파(상위에서 재시도).
                await h.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            // 쓰기 큐 채널을 먼저 완료 → RunWriteConsumerAsync 결정적 종료(teardown 채널 경쟁 회피).
            Queue.Writer.TryComplete();
            await Gw.StopAsync();
            await Gw.DisposeAsync();
            await Sim.DisposeAsync();
        }
    }

    private static SimServer.Options DefaultSimOpt(int port) => new()
    {
        Host = "127.0.0.1", Port = port,
        TiltDelayMs = 50, SortDurationMs = 100, MoveDurationMs = 80,
        InitialCurFloor = 2, SimLoopMs = 10,
    };

    private static PlcGatewayOptions DefaultGwOpt(int port, int clearConfirmMs = 2000, int rFlagTimeoutMs = 3000) => new()
    {
        Host = "127.0.0.1", Port = port,
        PollIntervalMs = 30, OfflineAfterFailures = 3, WriteTimeoutMs = 500,
        RFlagPollMs = 20, RFlagTimeoutMs = rFlagTimeoutMs, CFlagTimeoutMs = 2000,
        RFlagClearConfirmTimeoutMs = clearConfirmMs,
    };

    /// <summary>
    /// 하니스를 기동하되 포트 바인드 경쟁(GetFreePort TOCTOU — 열려있는 free 포트를 다른
    /// 테스트가 선점)에 강인하게 새 포트로 재시도한다. 신규 테스트가 기존 flake를 악화시키지
    /// 않도록(자기 테스트는 결정적 통과) 방어 — 병렬 부하 하 포트 경쟁 flake 교훈 반영.
    /// </summary>
    private async Task<Harness> StartRobustAsync(
        int clearConfirmMs = 2000, int rFlagTimeoutMs = 3000,
        Func<SimServer.Options, SimServer.Options>? simTweak = null)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            int port   = GetFreePort();
            var simOpt = DefaultSimOpt(port);
            if (simTweak is not null) simOpt = simTweak(simOpt);
            var gwOpt  = DefaultGwOpt(port, clearConfirmMs, rFlagTimeoutMs);
            try
            {
                return await Harness.StartAsync(simOpt, gwOpt, _out.WriteLine);
            }
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
    // S1 — 잔류(R_CellNo=20, R_Seq=123, R_Flag=1) → 대사 → 성공
    //  (구 레벨-읽기 코드였다면 RSeqMismatch — fix 입증 대조 대상)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S1_Residue_Reconciled_ThenSuccess()
    {
        await using var h = await StartRobustAsync();

        // 기동 reconcile(R=0)이 no-op임을 지나, 운영 중 잔류가 발생한 상황 재현:
        // 첫 유효 폴 이후 런타임으로 R 잔류 세팅(핸드셰이크 시작 시점 R_Flag=1 잔류).
        h.Sim.SetRResidue(FieldRCellNo, FieldRSeq);
        await WaitUntilAsync(() => h.Gw.Latest.RFlag, 2000, "GW가 잔류 R_Flag=1 관찰");

        var result = await h.Hs.ExecuteAsync(cellNo: 5, ct: CancellationToken.None);
        _out.WriteLine($"[S1] Outcome={result.Outcome} SentCSeq={result.SentCSeq} RSeq={result.ReceivedRSeq}");

        Assert.Equal(HandshakeOutcome.Success, result.Outcome);
        Assert.Equal(result.SentCSeq, result.ReceivedRSeq); // 대사 일치(새 건 응답)

        // 관측성(§2D): HS_R_RESIDUE에 잔류값(rCellNo=20/rSeq=123) 기록됨.
        var residue = h.Stages.FirstOrDefault(s => s.action == "HS_R_RESIDUE");
        Assert.NotNull(residue.action);
        Assert.Contains($"\"rCellNo\":{FieldRCellNo}", residue.detail);
        Assert.Contains($"\"rSeq\":{FieldRSeq}", residue.detail);
        // arming 완료 후 C 기입됨(성공 경로).
        Assert.Contains(h.Stages, s => s.action == "HS_R_ARMED");
        Assert.Contains(h.Stages, s => s.action == "HS_C_SENT");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // S2 — 잔류 상태에서 같은 소터 연속 3건 → 3건 모두 성공(off-by-one 연쇄 차단)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S2_ResidueChain_ThreeConsecutive_AllSuccess()
    {
        await using var h = await StartRobustAsync();

        // 초기 잔류 세팅(현장 back-to-back 연쇄의 씨앗).
        h.Sim.SetRResidue(FieldRCellNo, FieldRSeq);
        await WaitUntilAsync(() => h.Gw.Latest.RFlag, 2000, "GW가 잔류 R_Flag=1 관찰");

        for (int i = 1; i <= 3; i++)
        {
            var r = await h.Hs.ExecuteAsync(cellNo: i, ct: CancellationToken.None);
            _out.WriteLine($"[S2] #{i} Outcome={r.Outcome} CSeq={r.SentCSeq} RSeq={r.ReceivedRSeq}");
            Assert.Equal(HandshakeOutcome.Success, r.Outcome);
            Assert.Equal(r.SentCSeq, r.ReceivedRSeq);
        }

        // 연쇄 차단 확인: RSeqMismatch outcome은 한 건도 없어야 함.
        Assert.DoesNotContain(h.Stages, s => s.action == "HS_RSEQ_MISMATCH");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // S3 — 기동 reconcile: 게이트웨이가 R_Flag=1 잔류 상태에서 폴링 시작 → ClearR로 클리어
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S3_StartupReconcile_ResidueClearedByPollLoop()
    {
        // Sim을 R 잔류 프리셋으로 기동 — GW 첫 폴이 R_Flag=1을 보게 함(PLC 기동 잔류 모사).
        await using var h = await StartRobustAsync(simTweak: o => o with
        {
            InitialRCellNo = FieldRCellNo,
            InitialRSeq    = FieldRSeq,
            InitialRFlag   = true,
        });

        // 기동 reconcile ClearR로 R_Flag==0 도달.
        await WaitUntilAsync(() => !h.Gw.Latest.RFlag, 3000, "기동 잔류 ClearR로 R_Flag==0 도달");

        // 관측성(§2B/§2D): ClearR OnWrite(PLC_WRITE) 발화 + 잔류값(20/123)이 POLL_CHANGE 전이로 기록.
        Assert.Contains(h.Writes, w => w.action == "CLEAR_R");
        Assert.Contains(h.RegChanges, c => c.reg == "R_CellNo" && c.oldV == FieldRCellNo && c.newV == 0);
        Assert.Contains(h.RegChanges, c => c.reg == "R_Seq" && c.oldV == FieldRSeq && c.newV == 0);
        Assert.Contains(h.RegChanges, c => c.reg == "R_Flag" && c.oldV == 1 && c.newV == 0);

        // 이후 첫 핸드셰이크가 잔류를 오소비하지 않음 → 성공.
        var result = await h.Hs.ExecuteAsync(cellNo: 3, ct: CancellationToken.None);
        _out.WriteLine($"[S3] reconcile 후 Outcome={result.Outcome}");
        Assert.Equal(HandshakeOutcome.Success, result.Outcome);
        // reconcile로 이미 R=0이므로 핸드셰이크 자체는 잔류 대사 미발화(HS_R_RESIDUE 없음).
        Assert.DoesNotContain(h.Stages, s => s.action == "HS_R_RESIDUE");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // S4 — 무잔류 정상 경로 회귀: 연속 2건 성공 + 잔류 대사 미발화(추가 지연 0)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S4_CleanPath_TwoConsecutive_NoResidueReconcile()
    {
        await using var h = await StartRobustAsync();

        // 깨끗한 상태 확인(R_Flag=0).
        Assert.False(h.Gw.Latest.RFlag, "초기 R_Flag=0(무잔류)");

        for (int i = 1; i <= 2; i++)
        {
            // 각 건은 깨끗한 상태(R_Flag=0)에서 시작 — 직전 건 ClearR 정착 대기.
            // 실 운영은 건 간 물리 시간 간격이 있어 ClearR가 정착한 뒤 다음 건이 온다(깨끗한 경로 정의).
            // (건 간 간격 없이 곧바로 이어지는 잔류 케이스는 S2에서 arming이 처리함을 별도 검증.)
            await WaitUntilAsync(() => !h.Gw.Latest.RFlag, 2000, $"#{i} 시작 전 R_Flag=0(깨끗한 상태)");
            var r = await h.Hs.ExecuteAsync(cellNo: i, ct: CancellationToken.None);
            _out.WriteLine($"[S4] #{i} Outcome={r.Outcome}");
            Assert.Equal(HandshakeOutcome.Success, r.Outcome);
        }

        // 깨끗한 경로에서는 잔류 대사가 한 번도 발화하지 않음(추가 지연 0·기존 동작 보존).
        Assert.DoesNotContain(h.Stages, s => s.action == "HS_R_RESIDUE");
        Assert.DoesNotContain(h.Stages, s => s.action == "HS_R_ARMED");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // S5 — R_Flag==0 확인 타임아웃: ClearR 미반영(PLC 무ack 모사) → C 미기입 종결
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S5_ResidueClearNotReflected_TerminalTimeout_NoCWritten()
    {
        // 확인 타임아웃을 짧게(300ms) — 결정적 유발.
        await using var h = await StartRobustAsync(clearConfirmMs: 300);

        // 잔류 세팅 후 sticky 고장 주입(WCS ClearR를 계속 무효화 — PLC 무ack).
        h.Sim.SetRResidue(FieldRCellNo, FieldRSeq);
        h.Sim.InjectStickyRResidue = true;
        await WaitUntilAsync(() => h.Gw.Latest.RFlag, 2000, "GW가 잔류 R_Flag=1 관찰");

        var result = await h.Hs.ExecuteAsync(cellNo: 7, ct: CancellationToken.None);
        _out.WriteLine($"[S5] Outcome={result.Outcome} Detail={result.Detail}");

        // terminal outcome — 조용히 성공/진행하지 않고 사유가 드러나며 단정 가능.
        Assert.Equal(HandshakeOutcome.RFlagResidueTimeout, result.Outcome);
        Assert.Equal(0, result.SentCSeq);                 // C 미기입(cSeq 미증가)
        Assert.Equal(FieldRSeq, result.ReceivedRSeq);      // 잔류값 보존(사유 노출)
        Assert.Equal(FieldRCellNo, result.ReceivedRCellNo);

        // C를 기입하지 않았음을 관측으로 확증(HS_C_SENT 없음).
        Assert.DoesNotContain(h.Stages, s => s.action == "HS_C_SENT");
        Assert.Contains(h.Stages, s => s.action == "HS_R_RESIDUE_TIMEOUT");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // S6 — 진짜 R_Flag 무응답 타임아웃 회귀: arming이 이 경로를 훼손하지 않음
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S6_RealNoResponse_RFlagTimeout_Preserved()
    {
        // 무응답 → R 미상승. RFlagTimeoutMs를 짧게(400ms) 잡아 결정적 타임아웃.
        await using var h = await StartRobustAsync(rFlagTimeoutMs: 400);

        Assert.False(h.Gw.Latest.RFlag, "무잔류(R_Flag=0) — arming 즉시 진행");
        h.Sim.InjectNoResponse = true;

        var result = await h.Hs.ExecuteAsync(cellNo: 9, ct: CancellationToken.None);
        _out.WriteLine($"[S6] Outcome={result.Outcome}");

        Assert.Equal(HandshakeOutcome.RFlagTimeout, result.Outcome);
        // 무잔류 경로이므로 잔류 대사는 발화하지 않음(arming 즉시 진행).
        Assert.DoesNotContain(h.Stages, s => s.action == "HS_R_RESIDUE");
        // C는 정상 기입됨(무응답은 R단계 회귀 — C단계는 통과).
        Assert.Contains(h.Stages, s => s.action == "HS_C_SENT");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // S5b — Sim 충실도 불변식(S-S5-FLAKE 회귀 가드): sticky 고장 활성 구간에서 WCS가 R_Flag를
    //       클리어해도 서버 버퍼는 R_Flag=0을 "관측상 한 번도" 노출하지 않는다.
    //
    // 배경: S5는 xUnit 병렬 전체 스위트의 CPU 경합 하에서만 간헐 실패했다. 근본원인은 Sim의
    //   sticky 재천명이 Sim 루프(SimLoopMs) 주기에만 일어나, WCS ClearR RMW가 서버 버퍼에
    //   R_Flag=0을 쓴 뒤 다음 Sim 루프까지의 [RMW-write, 재천명] 창(부하 시 확대)에서 버퍼가
    //   R_Flag=0을 노출 → GW 폴이 그 0을 샘플링해 arming(잔류 대사)을 거짓 완료 → C 기입 →
    //   outcome이 RFlagResidueTimeout이 아니게 됨. 수정: Sim이 RegistersChanged 이벤트에서
    //   쓰기 즉시(동기·FC06 응답 이전) R_Flag=1을 복원해 창을 제거.
    //
    // 이 테스트는 그 불변식을 스케줄링 무관하게 결정적으로 단언한다 — 단일 커넥션에서
    //   "R_Flag 클리어 → 즉시 read-back"을 반복하며 read-back이 항상 R_Flag=1임을 확인.
    //   (수정 되돌리면: 재천명이 Sim 루프까지 지연되므로 write 직후 read-back이 R_Flag=0을
    //    잡아 결정적으로 RED — revert-대조 가드.)
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task S5b_StickyResidue_ServerBufferNeverExposesRFlagZero()
    {
        // Sim만 필요(GW/HS 불요) — 실 Modbus 클라이언트로 서버 버퍼 충실도를 직접 검증.
        // 포트 경쟁(GetFreePort TOCTOU)에 강인하게 재시도하며, 성공한 port를 캡처해 master 연결에 재사용.
        SimServer? sim = null;
        int port = 0;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            port = GetFreePort();
            var candidate = new SimServer(DefaultSimOpt(port));
            try { await candidate.StartAsync(); sim = candidate; break; }
            catch (SocketException ex)
            {
                _out.WriteLine($"[포트 경쟁 재시도 {attempt + 1}] {ex.Message}");
                await candidate.DisposeAsync();
                await Task.Delay(50);
            }
        }
        Assert.NotNull(sim);
        await using var _ = sim!;

        // 잔류 세팅(R_Flag=1) + sticky 고장 주입(WCS 클리어 미반영 모사 — 실 PLC 무ack).
        sim!.SetRResidue(FieldRCellNo, FieldRSeq);
        sim.InjectStickyRResidue = true;

        using var master = new ModbusTcpMaster("127.0.0.1", port, readTimeoutMs: 1000);
        master.Connect();

        // 단일 커넥션에서 "R_Flag 클리어(RMW FC06) → 즉시 read-back(FC03)"을 반복.
        // 각 클리어는 서버 버퍼에 R_Flag=0을 쓰지만, RegistersChanged 훅이 FC06 응답 이전에
        // 동기 복원하므로 read-back은 항상 R_Flag=1이어야 한다(스케줄링 무관·결정적).
        const int iterations = 100;
        int observedZero = 0;
        for (int i = 0; i < iterations; i++)
        {
            var before = await master.ReadHoldingRegistersAsync(RegisterMap.Flags, 1, CancellationToken.None);
            ushort cleared = (ushort)(before[0] & ~RegisterMap.D4.R_Flag); // R_Flag만 클리어(다른 비트 보존)
            await master.WriteSingleRegisterAsync(RegisterMap.Flags, (short)cleared, CancellationToken.None);

            var after = await master.ReadHoldingRegistersAsync(RegisterMap.Flags, 1, CancellationToken.None);
            if ((after[0] & RegisterMap.D4.R_Flag) == 0) observedZero++;
        }

        _out.WriteLine($"[S5b] {iterations}회 클리어 후 즉시 read-back에서 R_Flag=0 관측 = {observedZero}");
        // 충실도 불변식: sticky 활성 구간에서 R_Flag=0은 관측상 한 번도 노출되지 않는다.
        Assert.Equal(0, observedZero);
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
