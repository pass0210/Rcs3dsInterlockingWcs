using System.Net;
using System.Net.Sockets;
using Wcs.Core;
using Wcs.PlcGateway;
using Wcs.Sim3ds;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// MultiSorterSameBusTests — S-MULTISORTER-SHARED-BUS (Phase 1) 통합 (fan-in)
//
//   TCP 멀티유닛 Sim3ds ↔ PlcGateway 공유 버스(ModbusBus). 전송은 반드시 TCP(테스트 vehicle — OQ2),
//   실 COM1/RTU 금지. 결정적 설계: 고정 sleep 없음, WaitUntil 폴링, Online baseline 확립 후 관찰,
//   비동기 로그/스냅샷은 조건 폴링으로 대기.
//
//   시나리오:
//     (a) 한 버스 두 슬레이브 ONLINE·독립 폴링 — 마스터/포트 1개(공유 연결 1개) + 뱅크 독립.
//     (b) 서로 다른 두 슬레이브 동시 핸드셰이크 ≥20회 — 각 R_Seq==자기 C_Seq, 프레임/R_Seq 교차 0(버스 락).
//     (c1) 슬레이브 B InjectNoResponse → B 핸드셰이크 RFlagTimeout, A Online 유지·Success.
//     (c2) 슬레이브 B InjectUnresponsive → B만 OFFLINE 전이, A Online 유지·Success(슬레이브별 독립 B5).
//     (d) 서로 다른 버스(엔드포인트 2개) 병렬 ONLINE·Success(멀티 포트 회귀 0).
//     (e) 절대규칙 #1 — 두 슬레이브에 인터리브 쓰기, D4 RMW 비트 보존·C 영역 교차 오염 0.
// ════════════════════════════════════════════════════════════════════════════
[Collection("RealSimSerial")]
public sealed class MultiSorterSameBusTests : IAsyncDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly List<string>      _timeline = [];
    private readonly object            _tlLock = new();
    private readonly List<IAsyncDisposable> _cleanup = [];

    public MultiSorterSameBusTests(ITestOutputHelper output) => _out = output;

    // 기본(빠른) 옵션 — 테스트별 with 로 조정.
    private static SimServer.Options SimOpt(int port) => new()
    {
        Host = "127.0.0.1", Port = port,
        TiltDelayMs = 50, SortDurationMs = 100, MoveDurationMs = 80,
        InitialCurFloor = 1, SimLoopMs = 10,
    };

    private static PlcGatewayOptions GwOpt() => new()
    {
        Host = "127.0.0.1",   // 미사용(공유 연결 명시 host:port 사용) — 명시만.
        PollIntervalMs = 30, OfflineAfterFailures = 3, WriteTimeoutMs = 500,
        RFlagPollMs = 20, RFlagTimeoutMs = 3000, CFlagTimeoutMs = 2000,
        RFlagClearConfirmTimeoutMs = 2000,
    };

    // ── 버스 리그 구성 ─────────────────────────────────────────────────────────

    private sealed record BusRig(
        SimServer Sim, ModbusBus Bus,
        IReadOnlyDictionary<byte, PlcPollingService> Slaves,
        IReadOnlyDictionary<byte, HandshakeOrchestrator> Hs);

    /// <summary>멀티유닛 TCP Sim + ModbusBus(공유 연결 1개) + 멤버 슬레이브·핸드셰이크 구성 후 기동.</summary>
    private async Task<BusRig> StartBusAsync(int port, byte[] unitIds, PlcGatewayOptions gwOpt)
    {
        var sim = new SimServer(SimOpt(port), unitIds, timelineLog: line =>
        {
            lock (_tlLock) { _timeline.Add(line); }
        });

        // 공유 연결 1개(= 마스터/포트 1개, B1). 두 슬레이브가 이 연결을 공유.
        var conn = new SharedTcpModbusConnection("127.0.0.1", port, readTimeoutMs: 1000);
        var bus  = new ModbusBus(conn, gwOpt);

        var slaves = new Dictionary<byte, PlcPollingService>();
        var hs     = new Dictionary<byte, HandshakeOrchestrator>();
        foreach (var u in unitIds)
        {
            var svc = bus.AddSlave(u);
            slaves[u] = svc;
            hs[u]     = new HandshakeOrchestrator(svc, gwOpt);
        }

        await sim.StartAsync();
        await bus.StartAsync();

        // teardown: 리그당 버스 먼저(폴/쓰기 루프·공유 연결 정지) → 그 다음 Sim 서버 정지(결정적).
        _cleanup.Add(new Disposer(async () =>
        {
            await bus.DisposeAsync();
            await sim.DisposeAsync();
        }));

        return new BusRig(sim, bus, slaves, hs);
    }

    public async ValueTask DisposeAsync()
    {
        // 역순 정리(버스 → Sim) — 등록 순서대로 add 했고 각 리그가 (버스, Sim) 순.
        for (int i = _cleanup.Count - 1; i >= 0; i--)
        {
            try { await _cleanup[i].DisposeAsync(); } catch { }
        }
        lock (_tlLock)
        {
            _out.WriteLine($"=== 타임라인 {_timeline.Count}줄 ===");
            foreach (var l in _timeline) _out.WriteLine(l);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // (a) 한 버스 두 슬레이브 ONLINE·독립 폴링 — 마스터/포트 1개
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task A_TwoSlaves_OneBus_BothOnline_IndependentBanks()
    {
        int port = GetFreePort();
        var rig  = await StartBusAsync(port, new byte[] { 1, 2 }, GwOpt());

        // 두 슬레이브 모두 Online·Ready — 공유 연결 1개(bus.Slaves.Count==2, 마스터/포트 1).
        await WaitUntilAsync(() => rig.Slaves[1].Latest.Online && rig.Slaves[2].Latest.Online,
            3000, "두 슬레이브 Online");

        Assert.True(rig.Slaves[1].Latest.Online, "슬레이브1 Online");
        Assert.True(rig.Slaves[2].Latest.Online, "슬레이브2 Online");
        Assert.True(rig.Slaves[1].Latest.Ready,  "슬레이브1 Ready=1");
        Assert.True(rig.Slaves[2].Latest.Ready,  "슬레이브2 Ready=1");
        Assert.Equal(2, rig.Bus.Slaves.Count);   // 멤버 2 — 공유 연결(마스터) 1개.

        // 레지스터 뱅크 독립 입증: 슬레이브1만 Ready=0으로 만들고 폴로 관측 → 슬레이브2는 Ready 유지.
        rig.Sim.Unit(1).SetReady(false);
        await WaitUntilAsync(() => !rig.Slaves[1].Latest.Ready, 2000, "슬레이브1 Ready=0 관측");
        Assert.False(rig.Slaves[1].Latest.Ready, "슬레이브1 Ready=0(독립 뱅크)");
        Assert.True(rig.Slaves[2].Latest.Ready,  "슬레이브2 Ready=1 유지(뱅크 간섭 0)");

        _out.WriteLine("[a] 한 엔드포인트·공유 연결 1개로 두 슬레이브 Online·독립 뱅크 입증");
    }

    // ════════════════════════════════════════════════════════════════════════
    // (b) 서로 다른 두 슬레이브 동시 핸드셰이크 ≥20회 — R_Seq 교차 0(버스 락)
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task B_ConcurrentHandshake_DifferentSlaves_NoCross_Repeated()
    {
        int port = GetFreePort();
        var rig  = await StartBusAsync(port, new byte[] { 1, 2 }, GwOpt());

        await WaitUntilAsync(() => rig.Slaves[1].Latest.Online && rig.Slaves[2].Latest.Online,
            3000, "baseline 두 슬레이브 Online");

        const int reps = 20;
        for (int i = 1; i <= reps; i++)
        {
            // 슬레이브별 서로 다른 셀 — 동시 트리거(WhenAll). 같은 슬레이브 동시 핸드셰이크는 out-of-scope(OQ6).
            int cell1 = 10 + i, cell2 = 50 + i;
            var t1 = rig.Hs[1].ExecuteAsync(cell1);
            var t2 = rig.Hs[2].ExecuteAsync(cell2);
            var r  = await Task.WhenAll(t1, t2);

            var r1 = r[0]; var r2 = r[1];
            Assert.Equal(HandshakeOutcome.Success, r1.Outcome);
            Assert.Equal(HandshakeOutcome.Success, r2.Outcome);
            // 각 R_Seq == 자기 C_Seq (프레임 교차 시 상대 값이 섞여 RSeqMismatch가 났을 것).
            Assert.Equal(r1.SentCSeq, r1.ReceivedRSeq);
            Assert.Equal(r2.SentCSeq, r2.ReceivedRSeq);
            // R_CellNo도 각자 값(뱅크·프레임 무교차).
            Assert.Equal(cell1, r1.ReceivedRCellNo);
            Assert.Equal(cell2, r2.ReceivedRCellNo);

            // 다음 반복 전 두 슬레이브 clean(R_Flag·C_Flag=0) 대기 — 결정적.
            await WaitUntilAsync(
                () => { var s1 = rig.Slaves[1].Latest; var s2 = rig.Slaves[2].Latest;
                        return !s1.RFlag && !s1.CFlag && !s2.RFlag && !s2.CFlag; },
                3000, $"rep#{i} 두 슬레이브 clean");
        }

        _out.WriteLine($"[b] 서로 다른 두 슬레이브 동시 핸드셰이크 {reps}회 전부 Success·R_Seq 교차 0(버스 락)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // (c1) 슬레이브 무응답(상태기계 동결) → B RFlagTimeout, A Online·Success
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task C1_SlaveNoResponse_BTimesOut_AStaysOnlineSuccess()
    {
        int port = GetFreePort();
        var gwOpt = GwOpt() with { RFlagTimeoutMs = 1200 };   // B 대기 단축(스냅)
        var rig   = await StartBusAsync(port, new byte[] { 1, 2 }, gwOpt);

        await WaitUntilAsync(() => rig.Slaves[1].Latest.Online && rig.Slaves[2].Latest.Online,
            3000, "baseline 두 슬레이브 Online");

        // 슬레이브2 상태기계 동결 — Modbus 폴은 계속 응답(Online 유지)하나 C_Flag 미처리 → R 미상승.
        rig.Sim.Unit(2).InjectNoResponse = true;

        var tA = rig.Hs[1].ExecuteAsync(cellNo: 7);
        var tB = rig.Hs[2].ExecuteAsync(cellNo: 8);
        var rA = await tA;
        var rB = await tB;

        Assert.Equal(HandshakeOutcome.Success, rA.Outcome);
        Assert.Equal(rA.SentCSeq, rA.ReceivedRSeq);
        Assert.Equal(HandshakeOutcome.RFlagTimeout, rB.Outcome);

        // A는 B 실패와 무관하게 Online 유지. B도 폴은 응답하므로 Online 유지(앱-동결이지 OFFLINE 아님).
        Assert.True(rig.Slaves[1].Latest.Online, "A Online 유지");
        Assert.True(rig.Slaves[2].Latest.Online, "B는 폴 응답 지속 → Online 유지(RFlagTimeout는 핸드셰이크 결과)");

        _out.WriteLine($"[c1] A={rA.Outcome} B={rB.Outcome} — A Online·Success, B RFlagTimeout(슬레이브 독립)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // (c2) 슬레이브 무응답(Modbus 예외) → B만 OFFLINE, A Online·Success (B5)
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task C2_SlaveUnresponsive_OnlyBOffline_AStaysOnlineSuccess()
    {
        int port = GetFreePort();
        var rig  = await StartBusAsync(port, new byte[] { 1, 2 }, GwOpt());

        await WaitUntilAsync(() => rig.Slaves[1].Latest.Online && rig.Slaves[2].Latest.Online,
            3000, "baseline 두 슬레이브 Online");

        // 슬레이브2 unitId 요청을 서버가 예외 응답 → GW 폴이 연속 실패 → B만 OFFLINE 전이.
        rig.Sim.Unit(2).InjectUnresponsive = true;

        await WaitUntilAsync(() => !rig.Slaves[2].Latest.Online, 3000, "슬레이브2 OFFLINE 전이");
        Assert.False(rig.Slaves[2].Latest.Online, "슬레이브2 OFFLINE");

        // A는 공유 물리 버스에서 계속 정상 폴 — Online 유지 + 핸드셰이크 Success.
        // (soft 실패라 공유 연결을 끊지 않으므로 A 무영향 — B5 슬레이브별 독립.)
        Assert.True(rig.Slaves[1].Latest.Online, "슬레이브1 Online 유지(B OFFLINE에도)");
        var rA = await rig.Hs[1].ExecuteAsync(cellNo: 5);
        Assert.Equal(HandshakeOutcome.Success, rA.Outcome);
        Assert.Equal(rA.SentCSeq, rA.ReceivedRSeq);

        // 복구: 고장 해제 → 슬레이브2 다시 Online.
        rig.Sim.Unit(2).InjectUnresponsive = false;
        await WaitUntilAsync(() => rig.Slaves[2].Latest.Online, 3000, "슬레이브2 Online 복구");
        Assert.True(rig.Slaves[2].Latest.Online, "슬레이브2 복구");

        _out.WriteLine("[c2] 슬레이브2 OFFLINE 동안 슬레이브1 Online·Success — 슬레이브별 OFFLINE 독립(B5)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // (d) 서로 다른 버스(엔드포인트 2개) 병렬 ONLINE·Success — 멀티 포트 회귀 0
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task D_TwoDifferentBuses_ParallelOnline_Success()
    {
        int portA = GetFreePort();
        int portB = GetFreePort();
        var rigA = await StartBusAsync(portA, new byte[] { 1 }, GwOpt());
        var rigB = await StartBusAsync(portB, new byte[] { 1 }, GwOpt());

        await WaitUntilAsync(() => rigA.Slaves[1].Latest.Online && rigB.Slaves[1].Latest.Online,
            3000, "두 버스 각각 Online");

        Assert.True(rigA.Slaves[1].Latest.Online, "버스A Online");
        Assert.True(rigB.Slaves[1].Latest.Online, "버스B Online");
        Assert.NotEqual(portA, portB);           // 독립 엔드포인트(독립 마스터/포트).

        var rA = await rigA.Hs[1].ExecuteAsync(cellNo: 3);
        var rB = await rigB.Hs[1].ExecuteAsync(cellNo: 4);
        Assert.Equal(HandshakeOutcome.Success, rA.Outcome);
        Assert.Equal(HandshakeOutcome.Success, rB.Outcome);

        _out.WriteLine("[d] 서로 다른 버스 2개 병렬 Online·Success — 멀티 포트 보존(회귀 0)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // (e) 절대규칙 #1 — 인터리브 쓰기: D4 RMW 비트 보존·C 영역 교차 오염 0
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task E_InterleavedWrites_D4Rmw_PreservesBits_NoCrossContamination()
    {
        int port = GetFreePort();
        var rig  = await StartBusAsync(port, new byte[] { 1, 2 }, GwOpt());

        await WaitUntilAsync(() => rig.Slaves[1].Latest.Online && rig.Slaves[2].Latest.Online,
            3000, "baseline 두 슬레이브 Online");

        // 두 슬레이브 상태기계 동결 — C_Flag가 자동 소비되지 않아 C 영역/C_Flag를 결정적으로 관측.
        rig.Sim.Unit(1).InjectNoResponse = true;
        rig.Sim.Unit(2).InjectNoResponse = true;

        // 서로 다른 슬레이브에 CellAssign을 인터리브 투입(같은 버스 공유 큐·단일 컨슈머).
        await rig.Slaves[1].EnqueueAsync(new PlcWrite.CellAssign(11, 101));
        await rig.Slaves[2].EnqueueAsync(new PlcWrite.CellAssign(22, 202));

        // 각 슬레이브 C 영역이 자기 값으로 설정 + C_Flag=1 + Ready 비트 보존(D4 RMW).
        await WaitUntilAsync(() =>
        {
            var s1 = rig.Slaves[1].Latest; var s2 = rig.Slaves[2].Latest;
            return s1.CFlag && s1.CCellNo == 11 && s1.CSeq == 101
                && s2.CFlag && s2.CCellNo == 22 && s2.CSeq == 202;
        }, 3000, "두 슬레이브 C 영역 각자 값·C_Flag=1");

        var f1 = rig.Slaves[1].Latest; var f2 = rig.Slaves[2].Latest;
        Assert.Equal(11, f1.CCellNo);   Assert.Equal(101, f1.CSeq);   // 슬레이브2 값(22/202)로 오염 0
        Assert.Equal(22, f2.CCellNo);   Assert.Equal(202, f2.CSeq);   // 슬레이브1 값(11/101)로 오염 0
        Assert.True(f1.CFlag);          Assert.True(f2.CFlag);
        Assert.True(f1.Ready, "슬레이브1 Ready 비트 보존(D4 RMW)");    // RMW가 Ready 비트 미훼손
        Assert.True(f2.Ready, "슬레이브2 Ready 비트 보존(D4 RMW)");

        _out.WriteLine("[e] 인터리브 CellAssign — D4 RMW Ready 보존·C 영역 교차 오염 0(버스 단일 큐/락)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // (c3) 슬레이브 READ TIMEOUT → B만 OFFLINE, 공유 연결 무-churn, A Online·복구 (I1)
    //   현장(RTU) 실패 모드: 죽은/부재 슬레이브는 Modbus 예외 응답이 아니라 '무응답=read timeout'으로
    //   신호한다. 타임아웃을 '하드'로 처리하면 매 폴 사이클 공유 포트를 close+open 해 모든 형제가
    //   ReadTimeout 동안 멈춘다(이 스프린트가 만들려는 격리가 깨짐). TCP InjectUnresponsive는 soft
    //   Modbus 예외라 이 경로를 재현하지 못하므로, 공유 연결 데코레이터로 read timeout을 주입해 검증.
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task C3_SlaveReadTimeout_OnlyBOffline_SharedConnNotChurned_AOnlineAndRecovers()
    {
        int port = GetFreePort();
        var gwOpt = GwOpt();   // OfflineAfterFailures=3, PollIntervalMs=30

        var sim   = new SimServer(SimOpt(port), new byte[] { 1, 2 },
            timelineLog: line => { lock (_tlLock) { _timeline.Add(line); } });
        var inner = new SharedTcpModbusConnection("127.0.0.1", port, readTimeoutMs: 1000);
        var conn  = new TimeoutInjectingConnection(inner);   // read timeout 주입 데코레이터(테스트 전용)
        var bus   = new ModbusBus(conn, gwOpt);
        var a = bus.AddSlave(1);
        var b = bus.AddSlave(2);
        var hsA = new HandshakeOrchestrator(a, gwOpt);

        await sim.StartAsync();
        await bus.StartAsync();
        _cleanup.Add(new Disposer(async () => { await bus.DisposeAsync(); await sim.DisposeAsync(); }));

        // baseline — 두 슬레이브 Online. 이 시점까지 재연결(Disconnect) 0.
        await WaitUntilAsync(() => a.Latest.Online && b.Latest.Online, 3000, "baseline 두 슬레이브 Online");
        int discBaseline = conn.DisconnectCalls;
        Assert.Equal(0, discBaseline);

        // 슬레이브 B read timeout 주입(공유 소켓 자체는 정상 — B unitId read만 TimeoutException).
        conn.SetTimeoutUnit(2);

        // (1) B가 OFFLINE 전이(연속 실패 누적 — soft 경로).
        await WaitUntilAsync(() => !b.Latest.Online, 5000, "B OFFLINE(read timeout, soft 누적)");
        Assert.False(b.Latest.Online, "B OFFLINE");

        // (2) A는 Online 유지 + 핸드셰이크 Success(형제 무영향).
        Assert.True(a.Latest.Online, "A Online 유지");
        var rA = await hsA.ExecuteAsync(cellNo: 5);
        Assert.Equal(HandshakeOutcome.Success, rA.Outcome);
        Assert.Equal(rA.SentCSeq, rA.ReceivedRSeq);
        Assert.True(a.Latest.Online, "핸드셰이크 후에도 A Online");

        // (3) 공유 연결 무-churn: B가 매 폴 타임아웃하는 동안에도 Disconnect(재연결) 0.
        //     (버그 시: 타임아웃=하드 → 매 폴 Disconnect+reopen → DisconnectCalls 급증.)
        await PollForDurationAsync(400);   // 수 개 폴 사이클 경과
        Assert.Equal(discBaseline, conn.DisconnectCalls);   // 공유 포트 미절단(격리 유지)

        // (4) 타임아웃 해제 → B가 공유 재연결 없이 Online 복구.
        conn.SetTimeoutUnit(-1);
        await WaitUntilAsync(() => b.Latest.Online, 5000, "B Online 복구(공유 재연결 없이)");
        Assert.True(b.Latest.Online, "B 복구");
        Assert.Equal(discBaseline, conn.DisconnectCalls);   // 복구까지도 Disconnect 0

        _out.WriteLine($"[c3] B read-timeout → B만 OFFLINE·A Online·Success, 공유 Disconnect={conn.DisconnectCalls} (무-churn), B 복구");
    }

    // ════════════════════════════════════════════════════════════════════════
    // 헬퍼
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 테스트 전용 ISharedModbusConnection 데코레이터 — 지정 unitId read/write를 즉시 TimeoutException으로
    /// 만들어 '죽은/부재 슬레이브 read timeout'(RTU 실 실패 모드)을 결정적으로 재현한다. 공유 연결의
    /// Connect/Disconnect 호출 수를 계측해 "타임아웃 중 공유 포트 무-churn"을 검증한다(I1). 실제 소켓은
    /// 건드리지 않으므로(타임아웃 unit은 소켓 왕복 없이 throw) 형제 unit 트래픽·상태에 영향 0.
    /// </summary>
    private sealed class TimeoutInjectingConnection(ISharedModbusConnection inner) : ISharedModbusConnection
    {
        private volatile int _timeoutUnit = -1;   // -1 = 없음
        private int _connectCalls;
        private int _disconnectCalls;

        public int ConnectCalls    => Volatile.Read(ref _connectCalls);
        public int DisconnectCalls => Volatile.Read(ref _disconnectCalls);

        /// <summary>이 unitId의 read/write를 타임아웃시킨다(-1=해제).</summary>
        public void SetTimeoutUnit(int unitId) => _timeoutUnit = unitId;

        public string BusKey      => inner.BusKey;
        public bool   IsConnected => inner.IsConnected;

        public void Connect()    { Interlocked.Increment(ref _connectCalls);    inner.Connect(); }
        public void Disconnect() { Interlocked.Increment(ref _disconnectCalls); inner.Disconnect(); }

        public Task<ushort[]> ReadHoldingRegistersAsync(byte unitId, ushort startAddress, ushort count, CancellationToken ct)
        {
            if (unitId == _timeoutUnit)
                throw new TimeoutException($"[test] injected read timeout for unit {unitId}");
            return inner.ReadHoldingRegistersAsync(unitId, startAddress, count, ct);
        }

        public Task WriteSingleRegisterAsync(byte unitId, ushort address, short value, CancellationToken ct)
        {
            if (unitId == _timeoutUnit)
                throw new TimeoutException($"[test] injected write timeout for unit {unitId}");
            return inner.WriteSingleRegisterAsync(unitId, address, value, ct);
        }

        public Task WriteMultipleRegistersAsync(byte unitId, ushort startAddress, short[] data, CancellationToken ct)
        {
            if (unitId == _timeoutUnit)
                throw new TimeoutException($"[test] injected write timeout for unit {unitId}");
            return inner.WriteMultipleRegistersAsync(unitId, startAddress, data, ct);
        }

        public void Dispose() => inner.Dispose();
    }

    /// <summary>지정 기간 동안 주기적으로 await(상태 무변화/무-churn 확인용).</summary>
    private static async Task PollForDurationAsync(int durationMs, int pollMs = 20)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(durationMs);
        while (DateTimeOffset.Now < deadline)
            await Task.Delay(pollMs);
    }

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

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class Disposer(Func<Task> dispose) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await dispose();
    }
}
