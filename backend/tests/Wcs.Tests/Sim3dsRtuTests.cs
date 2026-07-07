using System.IO.Ports;
using FluentModbus;
using Wcs.Core;
using Wcs.PlcGateway;
using Wcs.Sim3ds;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// Sim3dsRtuTests — S-SIM3DS-RTU
//
//   (a) 단위: Sim3ds 전송 선택·RTU 옵션 파싱·fail-loud(잘못된 Transport / PortName 미지정).
//   (b) CI 통합(환경 무관): 실 SimServer(RTU, fake-serial) ↔ WCS ModbusRtuMaster 왕복 —
//       폴 Online + C/R 핸드셰이크 1건(R_Seq==C_Seq) + ClearR + 잔류 프리셋(RTU 동일성).
//   (c) 실선(환경 게이트): WCS_RTU_TEST_PORTS=COMx,COMy 지정 시 실 OS 시리얼 스택 스모크.
//       미지정 시 스킵(사유 출력) — 기존 LiveMultiAgvRunner early-return 패턴 준수(새 의존성 0).
//
//   결정적 설계: 고정 sleep 없음, 폴링/대기로 동기화. fake-serial → 물리 COM 불요.
// ════════════════════════════════════════════════════════════════════════════
public class Sim3dsRtuTests
{
    private readonly ITestOutputHelper _out;
    public Sim3dsRtuTests(ITestOutputHelper output) => _out = output;

    // ════════════════════════════════════════════════════════════════════════
    // (a) 단위 — 전송 선택 + RTU 옵션 파싱 + fail-loud
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>기본(인자 없음·json 없음) = Tcp 127.0.0.1:1502, 타이밍 현행 기본값.</summary>
    [Fact]
    public void A1_Resolve_DefaultIsTcp_PreservesCurrentBehavior()
    {
        using var dir = new TempDir();
        var opt = Sim3dsConfig.Resolve(Array.Empty<string>(), dir.Path);

        Assert.Equal("Tcp", opt.Transport);
        Assert.Equal("127.0.0.1", opt.Host);
        Assert.Equal(1502, opt.Port);
        // 현행 타이밍 기본값 보존(회귀 방지)
        Assert.Equal(200, opt.TiltDelayMs);
        Assert.Equal(500, opt.SortDurationMs);
        Assert.Equal(300, opt.MoveDurationMs);
        Assert.Equal(1, opt.InitialCurFloor);
        Assert.Equal(20, opt.SimLoopMs);
        _out.WriteLine($"[A1] 기본 = {opt.Transport} {opt.Host}:{opt.Port}");
    }

    /// <summary>Transport=Rtu + RTU 옵션 CLI 파싱(--port→COM명 라우팅) + 파싱 헬퍼.</summary>
    [Fact]
    public void A2_Resolve_RtuCliArgs_ParsedAndRouted()
    {
        using var dir = new TempDir();
        var opt = Sim3dsConfig.Resolve(new[]
        {
            "--transport", "rtu", "--port", "COM7",
            "--baud", "19200", "--parity", "None", "--stopbits", "Two", "--unit", "2",
        }, dir.Path);

        Assert.Equal("rtu", opt.Transport);
        Assert.Equal("COM7", opt.PortName);          // --port가 RTU에서 PortName으로 라우팅
        Assert.Equal(19200, opt.BaudRate);
        Assert.Equal("None", opt.Parity);
        Assert.Equal("Two", opt.StopBits);
        Assert.Equal(2, opt.UnitId);
        // 파싱 헬퍼(잘못된 값이면 여기서 fail-loud)
        Assert.Equal(Parity.None, opt.ParsedParity);
        Assert.Equal(StopBits.Two, opt.ParsedStopBits);
        _out.WriteLine($"[A2] {opt.Transport} {opt.PortName} {opt.BaudRate}/{opt.Parity}/{opt.StopBits} unit={opt.UnitId}");
    }

    /// <summary>TCP 모드에서 --port는 TCP 포트 번호로 라우팅(PortName 미설정).</summary>
    [Fact]
    public void A3_Resolve_TcpPortRouting()
    {
        using var dir = new TempDir();
        var opt = Sim3dsConfig.Resolve(new[] { "--transport", "tcp", "--port", "1600" }, dir.Path);

        Assert.Equal("tcp", opt.Transport);
        Assert.Equal(1600, opt.Port);
        Assert.Null(opt.PortName);
        _out.WriteLine($"[A3] {opt.Transport} port={opt.Port} PortName={opt.PortName ?? "(null)"}");
    }

    /// <summary>우선순위: json(기본값) &lt; CLI. json Transport=Rtu·PortName=COM3 → CLI --port COM9가 우선.</summary>
    [Fact]
    public void A4_Resolve_JsonBase_CliOverrides()
    {
        using var dir = new TempDir();
        dir.WriteFile(Sim3dsConfig.FileName, """
        {
          "Transport": "Rtu",
          "PortName": "COM3",
          "BaudRate": 38400
        }
        """);

        // CLI에 --transport 없음 → 유효 Transport는 json의 Rtu → --port는 PortName으로 라우팅
        var opt = Sim3dsConfig.Resolve(new[] { "--port", "COM9" }, dir.Path);

        Assert.Equal("Rtu", opt.Transport);      // json 유지
        Assert.Equal("COM9", opt.PortName);       // CLI가 json COM3 오버라이드
        Assert.Equal(38400, opt.BaudRate);        // json 유지(오버라이드 없음)
        _out.WriteLine($"[A4] {opt.Transport} {opt.PortName} {opt.BaudRate}");
    }

    /// <summary>환경변수(SIM3DS_*) 오버라이드(json/기본값보다 우선, CLI보다 하위).</summary>
    [Fact]
    public void A5_Resolve_EnvOverride()
    {
        using var dir = new TempDir();
        const string k = "SIM3DS_BAUDRATE";
        var prev = Environment.GetEnvironmentVariable(k);
        try
        {
            Environment.SetEnvironmentVariable(k, "57600");
            var opt = Sim3dsConfig.Resolve(new[] { "--transport", "rtu", "--port", "COM4" }, dir.Path);
            Assert.Equal("rtu", opt.Transport);
            Assert.Equal("COM4", opt.PortName);
            Assert.Equal(57600, opt.BaudRate);    // env가 코드 기본값(9600) 오버라이드
            _out.WriteLine($"[A5] env BaudRate={opt.BaudRate}");
        }
        finally { Environment.SetEnvironmentVariable(k, prev); }
    }

    /// <summary>알 수 없는 스위치 → fail-loud(오타 방지).</summary>
    [Fact]
    public void A6_Resolve_UnknownSwitch_FailLoud()
    {
        using var dir = new TempDir();
        var ex = Assert.Throws<InvalidOperationException>(
            () => Sim3dsConfig.Resolve(new[] { "--tranport", "rtu" }, dir.Path));
        Assert.Contains("--tranport", ex.Message);
        _out.WriteLine($"[A6] {ex.Message}");
    }

    /// <summary>잘못된 Transport 값 → StartAsync에서 fail-loud(전송 팩토리 검증).</summary>
    [Fact]
    public async Task A7_StartAsync_UnknownTransport_FailLoud()
    {
        var sim = new SimServer(new SimServer.Options { Transport = "Serial" });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await sim.StartAsync());
        Assert.Contains("Transport", ex.Message);
        await sim.DisposeAsync();
        _out.WriteLine($"[A7] {ex.Message}");
    }

    /// <summary>RTU 모드인데 PortName 미지정 → StartAsync에서 fail-loud(우발적 COM 점유 방지).</summary>
    [Fact]
    public async Task A8_StartAsync_RtuWithoutPortName_FailLoud()
    {
        var sim = new SimServer(new SimServer.Options { Transport = "Rtu", PortName = null });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () => await sim.StartAsync());
        Assert.Contains("PortName", ex.Message);
        await sim.DisposeAsync();
        _out.WriteLine($"[A8] {ex.Message}");
    }

    /// <summary>
    /// C-2: UnitId 범위 밖(0=브로드캐스트·248 이상·byte 초과 300) → 무음 절단((byte)300=44) 대신
    /// 생성 시점 fail-loud. 형제 ParsedParity/ParsedStopBits와 동형(잘못된 값 → 명확한 예외).
    /// </summary>
    [Fact]
    public void A9_UnitId_OutOfRange_FailLoud()
    {
        // 300 → (byte) 무음 절단(44)이 아니라 명확한 예외.
        var ex = Assert.Throws<InvalidOperationException>(
            () => new SimServer(new SimServer.Options { UnitId = 300 }));
        Assert.Contains("UnitId", ex.Message);

        // 0(브로드캐스트)·248(예약 시작)도 슬레이브 식별자로 무효 → 거부.
        Assert.Throws<InvalidOperationException>(
            () => new SimServer(new SimServer.Options { UnitId = 0 }));
        Assert.Throws<InvalidOperationException>(
            () => new SimServer(new SimServer.Options { UnitId = 248 }));

        // 경계값 1·247 은 유효(예외 없음).
        _ = new SimServer(new SimServer.Options { UnitId = 1 });
        _ = new SimServer(new SimServer.Options { UnitId = 247 });
        _out.WriteLine($"[A9] {ex.Message}");
    }

    /// <summary>
    /// C-3: StartAsync 이전에 SetRResidue 호출 → 전송 계층 미생성(_transport null)이라
    /// NRE가 나던 것을 명확한 InvalidOperationException("StartAsync 먼저 호출")으로 대체.
    /// </summary>
    [Fact]
    public void A10_SetRResidue_BeforeStart_FailLoud()
    {
        var sim = new SimServer(new SimServer.Options());   // StartAsync 미호출 → _transport null
        var ex = Assert.Throws<InvalidOperationException>(() => sim.SetRResidue(rCellNo: 20, rSeq: 123));
        Assert.Contains("StartAsync", ex.Message);
        _out.WriteLine($"[A10] {ex.Message}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // (b) CI 통합 — 실 SimServer(RTU, fake-serial) ↔ WCS ModbusRtuMaster
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 핵심 검증(S3): 실 SimServer 상태기계를 RTU(fake-serial)로 태우고 WCS 마스터가 왕복.
    /// 폴 Online → C/R 핸드셰이크 1건 Success(R_Seq==C_Seq) → ClearR 후 R_Flag=0.
    /// "전송만 교체, 의미 동일"의 실증(기존 VT-2는 hand-rolled 서버였음 — 여기선 실 SimServer).
    /// </summary>
    [Fact]
    public async Task B1_RealSimServerRtu_FakeSerial_HandshakeRoundtrip()
    {
        const byte unitId = 1;
        var (clientPort, serverPort) = FakeSerialPortPair.Create();

        var simOpt = new SimServer.Options
        {
            // Transport 값은 주입 fake-port가 RTU-fake로 강제하므로 무관하지만 명시.
            Transport = "Rtu", UnitId = unitId,
            TiltDelayMs = 50, SortDurationMs = 100, MoveDurationMs = 80,
            InitialCurFloor = 1, SimLoopMs = 10,
        };
        var timeline = new List<string>();
        var sim = new SimServer(simOpt, fakePort: serverPort,
                                timelineLog: l => { lock (timeline) timeline.Add(l); });

        var master = new ModbusRtuMaster(fakePort: clientPort, endianness: ModbusEndianness.BigEndian, unitId: unitId);
        var queue  = new PlcWriteQueue();
        var gwOpt  = new PlcGatewayOptions
        {
            PollIntervalMs = 30, OfflineAfterFailures = 3, WriteTimeoutMs = 500,
            RFlagPollMs = 20, RFlagTimeoutMs = 3000, CFlagTimeoutMs = 2000,
            RFlagClearConfirmTimeoutMs = 2000,
        };
        var gw = new PlcPollingService(gwOpt, queue, master);
        var hs = new HandshakeOrchestrator(gw, gwOpt);

        try
        {
            await sim.StartAsync();
            await gw.StartAsync();

            // 폴 Online(스냅샷 왕복)
            await WaitUntilAsync(() => gw.Latest.Online, 3000, "GW Online(RTU fake-serial)");
            Assert.True(gw.Latest.Online, "RTU fake-serial 폴 Online");
            Assert.True(gw.Latest.Ready, "초기 Ready=1");
            _out.WriteLine($"[B1] Online={gw.Latest.Online} Ready={gw.Latest.Ready}");

            // C/R 핸드셰이크 1건 — 실 SimServer가 C_Flag 감지→R 에코
            var result = await hs.ExecuteAsync(cellNo: 5);

            Assert.Equal(HandshakeOutcome.Success, result.Outcome);
            Assert.Equal(result.SentCSeq, result.ReceivedRSeq);   // R_Seq==C_Seq 대사
            Assert.Equal(5, result.ReceivedRCellNo);
            _out.WriteLine($"[B1] 핸드셰이크 {result.Outcome} cSeq={result.SentCSeq} rSeq={result.ReceivedRSeq} rCell={result.ReceivedRCellNo}");

            // ClearR 반영 → R_Flag=0
            await WaitUntilAsync(() => !gw.Latest.RFlag, 2000, "ClearR 후 R_Flag=0");
            Assert.False(gw.Latest.RFlag, "핸드셰이크 후 R_Flag=0");
            Assert.True(gw.Latest.Ready, "핸드셰이크 완료 후 Ready=1 복귀");

            _out.WriteLine("[B1] 실 SimServer(RTU) fake-serial 왕복 완료 — 전송만 교체·의미 동일 실증");
        }
        finally
        {
            queue.Writer.TryComplete();
            await gw.StopAsync();
            await gw.DisposeAsync();
            await sim.DisposeAsync();
            master.Dispose();
            await clientPort.DisposeAsync();
            await serverPort.DisposeAsync();
        }
    }

    /// <summary>
    /// 잔류 프리셋(§2E)이 RTU에서도 TCP와 동일하게 동작함을 실증(2번째 의미 검증).
    /// InitialRFlag/InitialRCellNo/InitialRSeq → 마스터가 시리얼 왕복으로 그대로 관측.
    /// </summary>
    [Fact]
    public async Task B2_RealSimServerRtu_FakeSerial_ResiduePreset_Identical()
    {
        const byte unitId = 1;
        var (clientPort, serverPort) = FakeSerialPortPair.Create();

        var simOpt = new SimServer.Options
        {
            Transport = "Rtu", UnitId = unitId,
            TiltDelayMs = 50, SortDurationMs = 100, MoveDurationMs = 80,
            InitialCurFloor = 1, SimLoopMs = 10,
            // 실측 잔류값 재현(HandshakeResidueTests와 동일: R_CellNo=20, R_Seq=123)
            InitialRFlag = true, InitialRCellNo = 20, InitialRSeq = 123,
        };
        var sim    = new SimServer(simOpt, fakePort: serverPort);
        var master = new ModbusRtuMaster(fakePort: clientPort, endianness: ModbusEndianness.BigEndian, unitId: unitId);
        var queue  = new PlcWriteQueue();
        var gwOpt  = new PlcGatewayOptions { PollIntervalMs = 30, OfflineAfterFailures = 3, WriteTimeoutMs = 500 };
        var gw     = new PlcPollingService(gwOpt, queue, master);

        try
        {
            await sim.StartAsync();
            await gw.StartAsync();

            await WaitUntilAsync(() => gw.Latest.Online && gw.Latest.RFlag, 3000, "잔류 R_Flag=1 관측");
            var snap = gw.Latest;
            Assert.True(snap.RFlag, "RTU 잔류 R_Flag=1");
            Assert.Equal(123, snap.RSeq);       // 프리셋 R_Seq
            Assert.Equal(20, snap.RCellNo);     // 프리셋 R_CellNo
            _out.WriteLine($"[B2] RTU 잔류 프리셋 관측: R_Flag={snap.RFlag} R_Seq={snap.RSeq} R_CellNo={snap.RCellNo}");
        }
        finally
        {
            queue.Writer.TryComplete();
            await gw.StopAsync();
            await gw.DisposeAsync();
            await sim.DisposeAsync();
            master.Dispose();
            await clientPort.DisposeAsync();
            await serverPort.DisposeAsync();
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // (c) 실선 통합 — 환경 게이트(WCS_RTU_TEST_PORTS=COMx,COMy)
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// 실 OS 시리얼 스택 스모크. 환경변수 WCS_RTU_TEST_PORTS=COMclient,COMserver 지정 시에만 실행.
    /// 미지정 시 스킵(사유 출력) — 기존 LiveMultiAgvRunner early-return 패턴(xUnit 2.9.3 동적 Skip 미지원).
    /// 이 머신은 COM 포트 0개 → 항상 스킵 경로가 정상 동작(스킵 로직 자체가 검증 대상).
    /// </summary>
    [Fact]
    public async Task C1_LiveSerial_Roundtrip_WhenPortsProvided()
    {
        var portsEnv = Environment.GetEnvironmentVariable("WCS_RTU_TEST_PORTS");
        if (string.IsNullOrWhiteSpace(portsEnv))
        {
            _out.WriteLine("[C1] WCS_RTU_TEST_PORTS 미설정 — 실선 시리얼 테스트 건너뜀"
                         + "(가상/실 시리얼 페어 필요·com0com 또는 USB 어댑터 크로스 결선). "
                         + "형식: WCS_RTU_TEST_PORTS=COMclient,COMserver");
            return; // 스킵 = GREEN (환경 부재 시 정상)
        }

        var parts = portsEnv.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.True(parts.Length == 2,
            $"WCS_RTU_TEST_PORTS 형식 오류: '{portsEnv}' — 'COMclient,COMserver' 형식이어야 합니다.");
        string clientCom = parts[0], serverCom = parts[1];
        _out.WriteLine($"[C1] 실선 시리얼 왕복: client={clientCom} server={serverCom}");

        const byte unitId = 1;
        var simOpt = new SimServer.Options
        {
            Transport = "Rtu", PortName = serverCom, UnitId = unitId,
            BaudRate = 9600, Parity = "Even", StopBits = "One",
            ReadTimeoutMs = 1000, WriteTimeoutMs = 1000,
            TiltDelayMs = 50, SortDurationMs = 100, MoveDurationMs = 80,
            InitialCurFloor = 1, SimLoopMs = 10,
        };
        var sim    = new SimServer(simOpt);   // 물리 COM(serverCom) 기동
        var master = new ModbusRtuMaster(
            portName: clientCom, baudRate: 9600, parity: Parity.Even, stopBits: StopBits.One,
            readTimeoutMs: 1000, writeTimeoutMs: 1000, unitId: unitId,
            endianness: ModbusEndianness.BigEndian);
        var queue  = new PlcWriteQueue();
        var gwOpt  = new PlcGatewayOptions
        {
            PollIntervalMs = 50, OfflineAfterFailures = 3, WriteTimeoutMs = 1000,
            RFlagPollMs = 30, RFlagTimeoutMs = 5000, CFlagTimeoutMs = 3000,
            RFlagClearConfirmTimeoutMs = 3000,
        };
        var gw = new PlcPollingService(gwOpt, queue, master);
        var hs = new HandshakeOrchestrator(gw, gwOpt);

        try
        {
            await sim.StartAsync();
            master.Connect();
            await gw.StartAsync();

            await WaitUntilAsync(() => gw.Latest.Online, 8000, "실선 GW Online");
            var result = await hs.ExecuteAsync(cellNo: 5);
            Assert.Equal(HandshakeOutcome.Success, result.Outcome);
            Assert.Equal(result.SentCSeq, result.ReceivedRSeq);
            _out.WriteLine($"[C1] 실선 핸드셰이크 {result.Outcome} rSeq={result.ReceivedRSeq}");
        }
        finally
        {
            queue.Writer.TryComplete();
            await gw.StopAsync();
            await gw.DisposeAsync();
            await sim.DisposeAsync();
            master.Dispose();
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // 헬퍼
    // ════════════════════════════════════════════════════════════════════════

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

    /// <summary>테스트용 임시 디렉터리(Resolve basePath 격리 — appsettings 자동 픽업 방지).</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; }
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "sim3ds-cfg-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void WriteFile(string name, string content) =>
            File.WriteAllText(System.IO.Path.Combine(Path, name), content);
        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* 청소 실패 무시 */ }
        }
    }
}
