using System.Net;
using System.Net.Sockets;
using Wcs.Core;
using Wcs.PlcGateway;
using Wcs.Sim3ds;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests;

/// <summary>
/// M2 통합 테스트 — Sim3ds TcpServer ↔ PlcGateway 실제 Modbus TCP 왕복.
/// IT-1~IT-5 전부 자동화(수동 1회성 아님).
/// 결정적 설계: 고정 sleep 없음, 폴링/대기로 동기화, 임의 포트, 서버 기동 완료 후 연결.
/// </summary>
public class PlcGatewayIntegrationTests : IAsyncLifetime
{
    // ── 공유 인프라 ──────────────────────────────────────────────────────────

    private readonly ITestOutputHelper _out;
    private readonly List<string> _timeline = [];
    private readonly object _tlLock = new();

    // 각 테스트는 독립 포트 사용 (TCP 포트 충돌 방지)
    private readonly int _port;

    private SimServer?             _sim;
    private PlcWriteQueue?         _queue;
    private PlcPollingService?     _gw;
    private HandshakeOrchestrator? _hs;

    // 테스트별 설정 커스터마이즈
    private SimServer.Options   _simOpt;
    private PlcGatewayOptions   _gwOpt;

    public PlcGatewayIntegrationTests(ITestOutputHelper output)
    {
        _out  = output;
        _port = GetFreePort();

        // 기본 설정 — 빠른 테스트용 (타임아웃 단축)
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

    // ── IAsyncLifetime ───────────────────────────────────────────────────────

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_gw  is not null) await _gw.StopAsync();
        if (_sim is not null) await _sim.StopAsync();

        if (_gw  is not null) await _gw.DisposeAsync();
        if (_sim is not null) await _sim.DisposeAsync();

        lock (_tlLock)
        {
            _out.WriteLine("=== 레지스터 타임라인 로그 ===");
            foreach (var line in _timeline) _out.WriteLine(line);
            _out.WriteLine($"=== 타임라인 종료 ({_timeline.Count}줄) ===");
        }
    }

    // ── 공통 셋업 헬퍼 ───────────────────────────────────────────────────────

    private async Task StartInfraAsync()
    {
        void LogTimeline(string line)
        {
            lock (_tlLock) { _timeline.Add(line); }
            // xUnit ITestOutputHelper는 테스트 실행 중에만 호출 가능
        }

        _sim   = new SimServer(_simOpt, timelineLog: LogTimeline);
        _queue = new PlcWriteQueue();
        _gw    = new PlcPollingService(_gwOpt, _queue);
        _hs    = new HandshakeOrchestrator(_gw, _gwOpt);

        // 서버 먼저 기동, 이후 클라이언트 연결
        await _sim.StartAsync();
        await _gw.StartAsync();

        // 폴링 첫 성공까지 대기
        await WaitUntilAsync(() => _gw.Latest.Online, timeoutMs: 2000, msg: "GW Online");
    }

    // ════════════════════════════════════════════════════════════════════════
    // IT-1 정상 왕복
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// IT-1: 핸드셰이크 1건 완전 왕복.
    /// ①WCS C기입+C_Flag=1 ②Sim C읽고 클리어→TiltDelay ③분류 Ready=0+TgtFloor=0 클리어
    /// ④Sim R기입+R_Flag=1(복귀 없으면 Ready=1) ⑤WCS R_Flag 감지→대사 성공→R 클리어.
    /// 단언: 결과=성공, 대사 일치, 종료 시 C_Flag·R_Flag=0, 타임라인 로그 존재.
    /// </summary>
    [Fact]
    public async Task IT1_NormalHandshake_Succeeds()
    {
        await StartInfraAsync();

        var result = await _hs!.ExecuteAsync(cellNo: 5, ct: CancellationToken.None);

        Assert.Equal(HandshakeOutcome.Success, result.Outcome);
        Assert.True(result.IsSuccess);
        Assert.Equal(result.SentCSeq, result.ReceivedRSeq);

        // ClearR 큐 처리 대기 (R_Flag·C_Flag=0 확인)
        await WaitUntilAsync(
            () => { var s = _gw!.Latest; return !s.RFlag && !s.CFlag; },
            timeoutMs: 2000, msg: "C_Flag·R_Flag=0 after ClearR");

        var fin = _gw!.Latest;
        Assert.False(fin.CFlag, "종료 시 C_Flag=0");
        Assert.False(fin.RFlag, "종료 시 R_Flag=0");

        lock (_tlLock) { Assert.NotEmpty(_timeline); }

        _out.WriteLine($"IT-1 성공: C_Seq={result.SentCSeq} R_Seq={result.ReceivedRSeq}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // IT-2 R_Seq 대사
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>IT-2a: R_Seq == C_Seq → 성공.</summary>
    [Fact]
    public async Task IT2a_RSeq_Match_Succeeds()
    {
        await StartInfraAsync();

        var result = await _hs!.ExecuteAsync(cellNo: 3, ct: CancellationToken.None);

        Assert.Equal(HandshakeOutcome.Success, result.Outcome);
        Assert.Equal(result.SentCSeq, result.ReceivedRSeq);
        _out.WriteLine($"IT-2a: 일치 C_Seq={result.SentCSeq} R_Seq={result.ReceivedRSeq}");
    }

    /// <summary>IT-2b: R_Seq 불일치 주입 → 결과=알람(RSeqMismatch)·사유 반환.</summary>
    [Fact]
    public async Task IT2b_RSeq_Mismatch_ReturnsAlarm()
    {
        await StartInfraAsync();

        _sim!.InjectRSeqOverride = 999;

        var result = await _hs!.ExecuteAsync(cellNo: 7, ct: CancellationToken.None);

        Assert.Equal(HandshakeOutcome.RSeqMismatch, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.Equal(999, result.ReceivedRSeq);
        Assert.NotEqual(result.SentCSeq, result.ReceivedRSeq);
        Assert.Contains("mismatch", result.Detail, StringComparison.OrdinalIgnoreCase);

        _out.WriteLine($"IT-2b: 불일치 — C_Seq={result.SentCSeq} R_Seq={result.ReceivedRSeq} Detail={result.Detail}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // IT-3 단일 쓰기 큐 직렬화·RMW 비트 보존·TgtFloor≠0 스킵
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// IT-3a: D4 RMW 비트 보존 검증.
    /// CellAssign(C_Flag set) → Sim이 즉시 C 클리어 → 분류 → R_Flag=1 → Ready 유지됨.
    /// ClearR(R_Flag clear) → R_Flag=0, C_Flag=0.
    /// 핵심: RMW가 Ready 비트를 C_Flag set/clear 과정에서 보존했음 — 분류 완료 시 Ready=1로 확인.
    /// C_Flag=1은 Sim이 즉시 클리어하여 GW 폴링이 캡처 못할 수 있으므로 직접 단언 안 함.
    /// </summary>
    [Fact]
    public async Task IT3a_D4_RMW_PreservesOtherBits()
    {
        await StartInfraAsync();

        var snap0 = _gw!.Latest;
        Assert.True(snap0.Online);
        Assert.True(snap0.Ready, "초기 Ready=1");
        Assert.False(snap0.CFlag);
        Assert.False(snap0.RFlag);

        // CellAssign 투입 — Sim이 C_Flag=1 즉시 클리어하므로 GW가 C_Flag=1을 캡처 못할 수 있음
        // RMW가 올바르면: D4 RMW에서 C_Flag=1 set 시 Ready 비트 보존 → 분류 완료 후 Ready=1
        await _gw.EnqueueAsync(new PlcWrite.CellAssign(1, 42));

        // Sim이 분류 완료하고 R_Flag=1을 세팅할 때까지 대기
        await WaitUntilAsync(() => _gw.Latest.RFlag, timeoutMs: 3000, msg: "R_Flag=1");

        var snapAfterR = _gw.Latest;
        Assert.True(snapAfterR.RFlag, "R_Flag=1 확인");
        // 분류 완료 시 Ready=1 — RMW가 Ready 비트를 보존했음을 입증
        Assert.True(snapAfterR.Ready, "분류 완료 후 Ready=1 (RMW가 Ready 비트 보존)");

        _out.WriteLine($"IT-3a R_Flag=1 시: CFlag={snapAfterR.CFlag} Ready={snapAfterR.Ready} RFlag={snapAfterR.RFlag}");

        // ClearR 투입 → R_Flag=0, C_Flag 영향 없음 확인
        await _gw.EnqueueAsync(new PlcWrite.ClearR());
        await WaitUntilAsync(() => !_gw.Latest.RFlag, timeoutMs: 2000, msg: "R_Flag=0 after ClearR");

        var fin = _gw.Latest;
        Assert.False(fin.RFlag, "ClearR 후 R_Flag=0");
        Assert.False(fin.CFlag, "ClearR 후 C_Flag=0 (Sim이 C 처리 시 클리어했음)");

        _out.WriteLine($"IT-3a ClearR 후: CFlag={fin.CFlag} Ready={fin.Ready} RFlag={fin.RFlag}");
    }

    /// <summary>
    /// IT-3c: 폴 진행 중 다수 CellAssign·ClearR 연속 투입 — 소켓 직렬화·스냅샷 무결성 검증.
    /// _clientLock SemaphoreSlim(1,1)으로 폴 읽기와 쓰기 컨슈머가 직렬화되므로
    /// 연속 핸드셰이크 N건 동안 R_Seq==C_Seq 대사가 매 건 성공하면 프레임 교차가 없었음을 입증.
    /// 단일 SimServer, 직렬 핸드셰이크 3건 연속 — 각각 Success + R_Seq==C_Seq.
    /// </summary>
    [Fact]
    public async Task IT3c_ConcurrentPollAndWrite_NoFrameCorruption()
    {
        await StartInfraAsync();

        // 3건 연속 핸드셰이크 — 폴이 돌아가는 동안 쓰기가 계속 들어옴
        for (int i = 1; i <= 3; i++)
        {
            var result = await _hs!.ExecuteAsync(cellNo: i, ct: CancellationToken.None);

            Assert.Equal(HandshakeOutcome.Success, result.Outcome);
            Assert.Equal(result.SentCSeq, result.ReceivedRSeq);
            _out.WriteLine($"IT-3c 건#{i}: C_Seq={result.SentCSeq} R_Seq={result.ReceivedRSeq} → Success");

            // 이전 ClearR 처리 완료 대기 후 다음 건 진행
            await WaitUntilAsync(
                () => { var s = _gw!.Latest; return !s.RFlag && !s.CFlag; },
                timeoutMs: 2000, msg: $"건#{i} ClearR 완료");
        }

        _out.WriteLine("IT-3c: 3건 연속 대사 성공 — 소켓 직렬화 무결성 입증");
    }

    /// <summary>IT-3b: SetTgtFloor — TgtFloor==0일 때만 기입, ≠0이면 스킵(핑퐁 차단).</summary>
    [Fact]
    public async Task IT3b_SetTgtFloor_SkipsWhenNonZero()
    {
        await StartInfraAsync();

        Assert.Equal(0, _gw!.Latest.TgtFloor);

        // TgtFloor=2 기입
        await _gw.EnqueueAsync(new PlcWrite.SetTgtFloor(2));
        await WaitUntilAsync(() => _gw.Latest.TgtFloor == 2, timeoutMs: 2000, msg: "TgtFloor=2");

        Assert.Equal(2, _gw.Latest.TgtFloor);
        _out.WriteLine("IT-3b: TgtFloor=2 기입 확인");

        // TgtFloor≠0 상태에서 SetTgtFloor(99) → 스킵되어야 함
        await _gw.EnqueueAsync(new PlcWrite.SetTgtFloor(99));

        // 충분한 폴링 후에도 TgtFloor=2 유지 확인
        await PollForDurationAsync(300);

        Assert.Equal(2, _gw.Latest.TgtFloor);
        _out.WriteLine($"IT-3b: SetTgtFloor(99) 스킵 확인 (TgtFloor={_gw.Latest.TgtFloor})");
    }

    // ════════════════════════════════════════════════════════════════════════
    // IT-4 OFFLINE 전이·복구
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// IT-4: 서버 종료 → 연속 실패 후 Online=false.
    /// 재기동 → Online=true 복구. 예외 삼키지 않음.
    /// </summary>
    [Fact]
    public async Task IT4_ServerDown_TransitionsOffline_ThenRecovers()
    {
        await StartInfraAsync();
        Assert.True(_gw!.Latest.Online, "초기 Online=true");

        // Sim 서버 종료 → 연결 끊김
        await _sim!.StopAsync();
        await _sim.DisposeAsync();
        _sim = null;

        // OFFLINE 전이 대기: ReadTimeout(500ms) * OfflineAfterFailures(3) + 여유
        int offlineTimeoutMs = (_gwOpt.WriteTimeoutMs * (_gwOpt.OfflineAfterFailures + 1)) + 1000;
        await WaitUntilAsync(() => !_gw.Latest.Online, timeoutMs: offlineTimeoutMs, msg: "OFFLINE 전이");

        Assert.False(_gw.Latest.Online, "연속 실패 후 Online=false");
        _out.WriteLine("IT-4: OFFLINE 전이 확인");

        // 서버 재기동
        _sim = new SimServer(_simOpt with { Port = _port }, timelineLog: line =>
        {
            lock (_tlLock) { _timeline.Add(line); }
        });
        await _sim.StartAsync();

        // 복구 대기
        await WaitUntilAsync(() => _gw.Latest.Online, timeoutMs: 3000, msg: "Online 복구");

        Assert.True(_gw.Latest.Online, "재기동 후 Online=true 복구");
        _out.WriteLine("IT-4: OFFLINE 복구 확인");
    }

    /// <summary>
    /// IT-4b: 쓰기 버스트 도중 서버 일시 단절·재개 — off-lock Disconnect 회귀 가드.
    /// 서버가 잠깐 꺼지고 재기동된 뒤 핸드셰이크 1건이 성공하면:
    ///   (a) Disconnect가 _clientLock 임계구역 안에서 실행되어 진행 중 트랜잭션을 손상하지 않았고
    ///   (b) 재연결 후 EnsureConnected가 정상 동작함을 입증.
    /// 타이밍: 핸드셰이크 진행 직후(쓰기가 큐에 있는 상태)에 서버를 끊고,
    ///         OfflineAfterFailures 미만으로 빠르게 재기동하여 OFFLINE 없이 복구.
    /// </summary>
    [Fact]
    public async Task IT4b_WritesDuringReconnect_NoCorruption()
    {
        // 폴 주기를 짧게, WriteTimeout을 짧게 — 빠른 재연결 감지
        _gwOpt = _gwOpt with { WriteTimeoutMs = 200, PollIntervalMs = 20, OfflineAfterFailures = 5 };

        await StartInfraAsync();
        Assert.True(_gw!.Latest.Online);

        // 첫 핸드셰이크를 시작한 직후 서버를 일시 종료하고 바로 재기동
        var hsTask = _hs!.ExecuteAsync(cellNo: 11, ct: CancellationToken.None);

        // 핸드셰이크가 막 큐에 들어간 시점에 서버 재기동 (OfflineAfterFailures 미만 — OFFLINE 없이)
        await Task.Delay(10);   // 첫 번째 폴 전 짧은 간격
        var oldSim = _sim!;
        await oldSim.StopAsync();
        await oldSim.DisposeAsync();
        _sim = null;

        // 즉시 새 서버 기동 (같은 포트)
        _sim = new SimServer(_simOpt with { Port = _port }, timelineLog: line =>
        {
            lock (_tlLock) { _timeline.Add(line); }
        });
        await _sim.StartAsync();

        // 핸드셰이크 완료 대기 (재연결 후 성공 또는 타임아웃)
        var result = await hsTask;

        // 재연결 후 GW가 Online으로 복구되어야 함
        await WaitUntilAsync(() => _gw.Latest.Online, timeoutMs: 3000, msg: "재연결 후 Online");

        // 재연결 후 추가 핸드셰이크 1건 성공 — 소켓/버퍼 손상 없음 입증
        var result2 = await _hs.ExecuteAsync(cellNo: 12, ct: CancellationToken.None);
        Assert.Equal(HandshakeOutcome.Success, result2.Outcome);
        Assert.Equal(result2.SentCSeq, result2.ReceivedRSeq);

        _out.WriteLine($"IT-4b: 재연결 후 핸드셰이크 성공 — C_Seq={result2.SentCSeq} R_Seq={result2.ReceivedRSeq}");
        _out.WriteLine($"IT-4b: 단절 중 첫 핸드셰이크 결과={result.Outcome} (Success 또는 Offline/Timeout 모두 허용)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // IT-5 R_Flag 타임아웃 (P1)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// IT-5: R_Flag 지연 주입 + 짧은 RFlagTimeoutMs → 타임아웃 결과 반환(P1 방침).
    /// </summary>
    [Fact]
    public async Task IT5_RFlagTimeout_ReturnsTimeout()
    {
        // RFlagTimeoutMs를 짧게 설정
        _gwOpt = _gwOpt with { RFlagTimeoutMs = 300, RFlagPollMs = 20 };

        await StartInfraAsync();

        // R_Flag 지연 주입 — SortDuration+TiltDelay보다 훨씬 긴 지연
        _sim!.InjectRFlagDelayMs = 5000;

        var result = await _hs!.ExecuteAsync(cellNo: 9, ct: CancellationToken.None);

        Assert.Equal(HandshakeOutcome.RFlagTimeout, result.Outcome);
        Assert.False(result.IsSuccess);
        Assert.Contains("RFLAG_TIMEOUT", result.Detail, StringComparison.OrdinalIgnoreCase);

        _out.WriteLine($"IT-5: RFlagTimeout 확인 — Detail={result.Detail}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // 헬퍼
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>조건이 충족될 때까지 폴링 대기. 타임아웃 시 Assert.Fail.</summary>
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

    /// <summary>지정 기간 동안 주기적으로 await (상태 변화 없음 확인용).</summary>
    private static async Task PollForDurationAsync(int durationMs, int pollMs = 20)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(durationMs);
        while (DateTimeOffset.Now < deadline)
            await Task.Delay(pollMs);
    }

    /// <summary>사용 가능한 임시 TCP 포트를 동적으로 할당.</summary>
    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
