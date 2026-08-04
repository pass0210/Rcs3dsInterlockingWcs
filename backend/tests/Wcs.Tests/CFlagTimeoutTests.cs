using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Wcs.Core;
using Wcs.PlcGateway;
using Wcs.Sim3ds;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// S-AUDIT-D-HANDSHAKE-HARDENING D② — CFlagTimeout '단독' 결정적 단언 (감사 D-2)
//
// 결함(회귀 위험): C_Flag=1 잔류(미소비) 상태로 핸드셰이크가 시작되면 WaitCFlagZeroAsync 가
//   C_Flag==0 을 영영 못 봐 무한 대기할 수 있다. 현 유일 타임아웃 E2E(E2EGroupCD D5)는
//   `CFLAG_TIMEOUT || RFLAG_TIMEOUT` 택일이라 C_Flag 무한대기 회귀가 RFLAG 만으로 통과한다.
//
// 이 테스트(VS-1)는 C_Flag=1 잔류를 결정적으로 심고 Outcome==HandshakeOutcome.CFlagTimeout '단독'
//   (택일 아님) + 경과 ≤ CFlagTimeoutMs+ε(무한대기 배제) + C 미기입(HS_C_SENT 없음)을 단언한다.
//   타임아웃 상한은 appsettings(설정 주입 — 절대규칙 #7)로 짧게 주입한다.
//
// ★ 핸드셰이크 제어 흐름 무변경(관측/단언만 추가). Sim 측은 기존 SetRResidue 와 동형인 test-only
//   SetCResidue(C_Flag=1 잔류) + InjectNoResponse(상태기계 정지 — C_Flag 미소비 재현)만 사용.
// ════════════════════════════════════════════════════════════════════════════

[Collection("RealSimSerial")]
public sealed class CFlagTimeoutTests
{
    private readonly ITestOutputHelper _out;
    public CFlagTimeoutTests(ITestOutputHelper output) => _out = output;

    // ─────────────────────────────────────────────────────────────────────────
    // 테스트 하니스 — Sim + 큐 + GW + 핸드셰이크 + 관측 훅 캡처(HandshakeResidueTests 패턴 재사용)
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

        public IReadOnlyList<(string action, string detail)> Stages
        { get { lock (_lock) return _stages.ToList(); } }
        public IReadOnlyList<(string action, string detail)> Writes
        { get { lock (_lock) return _writes.ToList(); } }

        private Harness(SimServer.Options simOpt, PlcGatewayOptions gwOpt)
        {
            Sim   = new SimServer(simOpt);
            Queue = new PlcWriteQueue();
            Gw    = new PlcPollingService(gwOpt, Queue);
            Hs    = new HandshakeOrchestrator(Gw, gwOpt);

            Hs.OnStage += (a, d) => { lock (_lock) _stages.Add((a, d)); };
            Gw.OnWrite += (a, d) => { lock (_lock) _writes.Add((a, d)); };
        }

        public static async Task<Harness> StartAsync(SimServer.Options simOpt, PlcGatewayOptions gwOpt)
        {
            var h = new Harness(simOpt, gwOpt);
            try
            {
                await h.Sim.StartAsync();
                await h.Gw.StartAsync();
                await WaitUntilAsync(() => h.Gw.Latest.Online, 3000, "GW Online");
                // 콜드스타트 StartupClear 완료 대기 — 이후 SetCResidue(C_Flag=1)가 StartupClear에 지워지지 않게
                //   순서 배리어(절대규칙 #3 콜드스타트 리셋은 먼저·1회). C 잔류는 이 배리어 뒤에만 심는다.
                await h.Gw.StartupClearCompleted.WaitAsync(TimeSpan.FromSeconds(3));
                return h;
            }
            catch
            {
                await h.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
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

    private static PlcGatewayOptions DefaultGwOpt(int port, int cFlagTimeoutMs) => new()
    {
        Host = "127.0.0.1", Port = port,
        PollIntervalMs = 30, OfflineAfterFailures = 3, WriteTimeoutMs = 500,
        RFlagPollMs = 20, RFlagTimeoutMs = 3000, CFlagTimeoutMs = cFlagTimeoutMs,
        RFlagClearConfirmTimeoutMs = 2000,
    };

    /// <summary>포트 바인드 경쟁(GetFreePort TOCTOU)에 강인하게 새 포트로 재시도(다른 실-Sim 테스트 교훈).</summary>
    private async Task<Harness> StartRobustAsync(int cFlagTimeoutMs)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            int port = GetFreePort();
            try
            {
                return await Harness.StartAsync(DefaultSimOpt(port), DefaultGwOpt(port, cFlagTimeoutMs));
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
    // VS-1 — C_Flag=1 잔류(미소비) → Outcome==CFlagTimeout '단독' + 경과 ≤ CFlagTimeoutMs+ε + C 미기입
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task VS1_CFlagResidue_Unconsumed_CFlagTimeoutAlone_Bounded_NoCWritten()
    {
        const int cFlagTimeoutMs = 500;   // 설정 주입(절대규칙 #7) — 짧게 잡아 결정적.
        await using var h = await StartRobustAsync(cFlagTimeoutMs);

        // 깨끗한 시작 확인(무-R잔류) — arming은 즉시 통과해 C 단계로 진입한다.
        Assert.False(h.Gw.Latest.RFlag, "arming 즉시 통과(R_Flag=0)");

        // ── C_Flag=1 잔류 주입(미소비) ──────────────────────────────────────────
        // InjectNoResponse: Sim 상태기계 정지 → 심어둔 C_Flag를 감지·소비하지 않는다(현장 무ack 재현).
        //   (폴 응답 자체는 계속 → Online 유지.) StartupClear는 이미 완료(배리어)라 C 잔류를 지우지 않는다.
        h.Sim.InjectNoResponse = true;
        h.Sim.SetCResidue(cCellNo: 12, cSeq: 99);
        await WaitUntilAsync(() => h.Gw.Latest.CFlag, 2000, "GW가 잔류 C_Flag=1 관찰");

        // ── 핸드셰이크 1건 — C_Flag 대기 상한 초과 → CFlagTimeout ────────────────
        var sw = Stopwatch.StartNew();
        var result = await h.Hs.ExecuteAsync(cellNo: 5, ct: CancellationToken.None);
        sw.Stop();
        _out.WriteLine($"[VS-1] Outcome={result.Outcome} elapsed={sw.ElapsedMilliseconds}ms Detail={result.Detail}");

        // (1) '단독' 단언 — 택일(CFLAG||RFLAG) 아닌 정확히 CFlagTimeout.
        Assert.Equal(HandshakeOutcome.CFlagTimeout, result.Outcome);

        // (2) 무한대기 배제 — 경과가 상한 부근에서 유계(하한: 실제로 상한까지 대기 / 상한: CFlagTimeoutMs+ε).
        Assert.True(sw.ElapsedMilliseconds >= cFlagTimeoutMs - 100,
            $"C_Flag 상한까지 실제로 대기했어야(elapsed={sw.ElapsedMilliseconds}ms ≥ {cFlagTimeoutMs - 100}ms) — 즉시 반환 아님");
        Assert.True(sw.ElapsedMilliseconds <= cFlagTimeoutMs + 2500,
            $"무한대기 배제 — 경과 ≤ CFlagTimeoutMs+ε(elapsed={sw.ElapsedMilliseconds}ms ≤ {cFlagTimeoutMs + 2500}ms)");

        // (3) C 미기입 — WaitCFlagZeroAsync가 CellAssign 전에 종결했으므로 HS_C_SENT 미발화(C 미투입).
        Assert.DoesNotContain(h.Stages, s => s.action == "HS_C_SENT");
        Assert.DoesNotContain(h.Writes, w => w.action == "CELL_ASSIGN");

        // (4) CFLAG_TIMEOUT 단계만 발화 — RFlag 관련(수신/타임아웃) 단계는 없음(단독 재확인).
        Assert.Contains(h.Stages, s => s.action == "HS_CFLAG_TIMEOUT");
        Assert.DoesNotContain(h.Stages, s => s.action == "HS_R_RECV");
        Assert.DoesNotContain(h.Stages, s => s.action == "HS_TIMEOUT");        // RFLAG_TIMEOUT 단계
        Assert.DoesNotContain(h.Stages, s => s.action == "HS_RSEQ_MISMATCH");
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
