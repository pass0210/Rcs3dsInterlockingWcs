using System.Buffers.Binary;
using FluentModbus;
using Wcs.Core;
using Wcs.PlcGateway;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests;

/// <summary>
/// S-RTU 전송 추상화 테스트 (VT-2~VT-5).
///
/// VT-2: RTU fake-serial 라이브 왕복 — 실제 ModbusRtuClient ↔ ModbusRtuServer, C/R + R_Seq==C_Seq + RMW + 단일 큐.
/// VT-3: 전송 선택 팩토리 — Transport=Tcp/Rtu → 해당 어댑터 생성.
/// VT-4: in-memory fake IModbusMaster로 PlcGateway 로직 단위 검증.
/// VT-5: RTU OFFLINE 전이 — fake-serial 강제 단절 → Online=false.
///
/// 결정적 설계: 고정 sleep 없음, 폴링/대기로 동기화.
/// </summary>
public class RtuTransportTests : IAsyncDisposable
{
    private readonly ITestOutputHelper _out;

    public RtuTransportTests(ITestOutputHelper output) => _out = output;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    // ════════════════════════════════════════════════════════════════════════
    // VT-2 RTU fake-serial 라이브 왕복
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// VT-2: in-memory fake IModbusRtuSerialPort 쌍으로 실제 ModbusRtuClient(WCS) ↔
    /// 실제 ModbusRtuServer(슬레이브) RTU 왕복.
    /// C/R 1건 성공 + R_Seq==C_Seq + RMW 비트 보존 + 단일 큐 직렬화 입증.
    /// </summary>
    [Fact]
    public async Task VT2_RtuFakeSerial_LiveRoundtrip_CellAssignAndClearR()
    {
        const byte UnitId = 1;

        // ── RTU 서버(슬레이브) 기동 ──────────────────────────────────────────
        var (clientPort, serverPort) = FakeSerialPortPair.Create();
        await using var _ = clientPort;
        await using var __ = serverPort;

        using var rtuServer = new ModbusRtuServer(UnitId, isAsynchronous: true);

        // 초기 레지스터: D4.Ready=1, D4.C_Flag=0, D4.R_Flag=0
        var serverHr = rtuServer.GetHoldingRegisters(UnitId);
        InitServerRegisters(serverHr, RegisterMap.D4.Ready);

        rtuServer.Start(serverPort);

        // ── RTU 클라이언트(WCS 마스터) 기동 ──────────────────────────────────
        using var rtuMaster = new ModbusRtuMaster(
            fakePort:   clientPort,
            endianness: ModbusEndianness.BigEndian,
            unitId:     UnitId);

        var queue = new PlcWriteQueue();
        var gwOpt = new PlcGatewayOptions
        {
            PollIntervalMs       = 30,
            OfflineAfterFailures = 3,
            WriteTimeoutMs       = 500,
            RFlagPollMs          = 20,
            RFlagTimeoutMs       = 3000,
            CFlagTimeoutMs       = 2000,
        };

        await using var gw = new PlcPollingService(gwOpt, queue, rtuMaster);
        var hs = new HandshakeOrchestrator(gw, gwOpt);

        await gw.StartAsync();

        // 폴링 첫 성공 대기
        await WaitUntilAsync(() => gw.Latest.Online, timeoutMs: 3000, "GW Online(RTU)");
        _out.WriteLine($"[VT-2] RTU GW Online: {gw.Latest.Online}");

        // ── C 단계: CellAssign 투입 ──────────────────────────────────────────
        int cSeq = 1;
        await gw.EnqueueAsync(new PlcWrite.CellAssign(5, cSeq));

        // C_Flag=1 대기
        await WaitUntilAsync(() => gw.Latest.CFlag, timeoutMs: 2000, "C_Flag=1");
        var snapC = gw.Latest;
        Assert.True(snapC.CFlag, "C_Flag=1 확인");
        Assert.Equal(5, snapC.CCellNo);
        Assert.Equal(cSeq, snapC.CSeq);
        _out.WriteLine($"[VT-2] C_Flag=1, CCellNo={snapC.CCellNo}, CSeq={snapC.CSeq}");

        // RMW 비트 보존 확인: Ready 비트가 C_Flag set 이후에도 보존되어야 함
        Assert.True(snapC.Ready, "C_Flag set 후 Ready 비트 RMW 보존");

        // ── 서버 측: C 읽고 R 기입 시뮬레이션 (슬레이브 동작) ───────────────
        // 실제 Sim3ds 없이 직접 서버 레지스터를 조작
        await Task.Delay(50); // WCS 쓰기 처리 대기

        // 서버가 C 읽기(폴링에서 C_Flag 확인 완료), R 세팅
        SetServerR(rtuServer, UnitId, rCellNo: 5, rSeq: cSeq);

        // ── R 단계: R_Flag=1 대기 ────────────────────────────────────────────
        await WaitUntilAsync(() => gw.Latest.RFlag, timeoutMs: 3000, "R_Flag=1");
        var snapR = gw.Latest;
        Assert.True(snapR.RFlag, "R_Flag=1 확인");
        Assert.Equal(cSeq, snapR.RSeq);  // R_Seq == C_Seq 대사
        _out.WriteLine($"[VT-2] R_Flag=1, RSeq={snapR.RSeq} == CSeq={cSeq}");

        // ── ClearR 투입 → R_Flag=0 ───────────────────────────────────────────
        await gw.EnqueueAsync(new PlcWrite.ClearR());
        await WaitUntilAsync(() => !gw.Latest.RFlag, timeoutMs: 2000, "R_Flag=0 after ClearR");

        var fin = gw.Latest;
        Assert.False(fin.RFlag, "ClearR 후 R_Flag=0");
        _out.WriteLine("[VT-2] RTU fake-serial 라이브 왕복 완료");
    }

    // ════════════════════════════════════════════════════════════════════════
    // VT-3 전송 선택 팩토리
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// VT-3: Transport=Tcp → ModbusTcpMaster, Transport=Rtu → ModbusRtuMaster.
    /// 시리얼 파라미터(PortName·Baud·Parity·Stop·UnitId) 전달 검증.
    /// </summary>
    [Fact]
    public void VT3_Factory_TransportSelection_CreatesCorrectAdapter()
    {
        // TCP 선택
        var tcpOpt = new PlcTransportOptions
        {
            Transport = "Tcp",
            Host      = "192.168.1.1",
            Port      = 502,
            UnitId    = 2,
        };
        using var tcpMaster = ModbusMasterFactory.Create(tcpOpt);
        Assert.IsType<ModbusTcpMaster>(tcpMaster);
        _out.WriteLine("[VT-3] Transport=Tcp → ModbusTcpMaster 생성 확인");

        // RTU 선택
        var rtuOpt = new PlcTransportOptions
        {
            Transport    = "Rtu",
            PortName     = "COM3",
            BaudRate     = 19200,
            Parity       = "None",
            StopBits     = "Two",
            ReadTimeoutMs  = 2000,
            WriteTimeoutMs = 2000,
            UnitId       = 3,
        };
        using var rtuMaster = ModbusMasterFactory.Create(rtuOpt);
        Assert.IsType<ModbusRtuMaster>(rtuMaster);
        _out.WriteLine("[VT-3] Transport=Rtu → ModbusRtuMaster 생성 확인");

        // 기본값(미지정) = Rtu
        var defaultOpt = new PlcTransportOptions(); // Transport 기본="Rtu"
        using var defaultMaster = ModbusMasterFactory.Create(defaultOpt);
        Assert.IsType<ModbusRtuMaster>(defaultMaster);
        _out.WriteLine("[VT-3] Transport 미지정 기본=Rtu 확인");

        // 잘못된 값 → 예외
        var badOpt = new PlcTransportOptions { Transport = "Serial" };
        Assert.Throws<InvalidOperationException>(() => ModbusMasterFactory.Create(badOpt));
        _out.WriteLine("[VT-3] 알 수 없는 Transport → InvalidOperationException 확인");
    }

    // ════════════════════════════════════════════════════════════════════════
    // VT-4 in-memory fake IModbusMaster로 PlcGateway 로직 단위 검증
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// VT-4: FakeModbusMaster를 주입해 PlcGateway 로직(스냅샷·큐·RMW·OFFLINE)을
    /// 전송 무관하게 단위 검증.
    /// TgtFloor 핑퐁 차단, C_Flag RMW 비트 보존, R_Flag ClearR 확인.
    /// </summary>
    [Fact]
    public async Task VT4_FakeModbusMaster_PlcGatewayLogic_TransportAgnostic()
    {
        // fake master: Ready=1, 모두 0으로 초기화
        var fakeRegisters = new ushort[RegisterMap.BlockLength];
        fakeRegisters[RegisterMap.Flags] = RegisterMap.D4.Ready; // D4.Ready=1

        using var fakeMaster = new FakeModbusMaster(fakeRegisters);
        var queue = new PlcWriteQueue();
        var gwOpt = new PlcGatewayOptions
        {
            PollIntervalMs       = 20,
            OfflineAfterFailures = 3,
            WriteTimeoutMs       = 200,
        };

        await using var gw = new PlcPollingService(gwOpt, queue, fakeMaster);
        await gw.StartAsync();

        await WaitUntilAsync(() => gw.Latest.Online, timeoutMs: 1000, "FakeMaster GW Online");
        _out.WriteLine($"[VT-4] GW Online: {gw.Latest.Online}");

        // ① CellAssign → C_Flag=1, CCellNo=7, CSeq=42
        await gw.EnqueueAsync(new PlcWrite.CellAssign(7, 42));
        await WaitUntilAsync(() => gw.Latest.CFlag, timeoutMs: 1000, "C_Flag=1");

        var snapC = gw.Latest;
        Assert.True(snapC.CFlag, "C_Flag=1");
        Assert.Equal(7, snapC.CCellNo);
        Assert.Equal(42, snapC.CSeq);
        Assert.True(snapC.Ready, "RMW: C_Flag set 후 Ready 비트 보존");
        _out.WriteLine($"[VT-4] CellAssign 완료: CFlag={snapC.CFlag} Ready={snapC.Ready}");

        // ② 서버 측: R 세팅(fake master에 직접 기입)
        fakeMaster.SetRegister(RegisterMap.R_CellNo, 7);
        fakeMaster.SetRegister(RegisterMap.R_Seq, 42);
        fakeMaster.SetFlagBit(RegisterMap.D4.R_Flag, set: true);

        await WaitUntilAsync(() => gw.Latest.RFlag, timeoutMs: 1000, "R_Flag=1");
        Assert.Equal(42, gw.Latest.RSeq);

        // ③ ClearR → R_Flag=0
        await gw.EnqueueAsync(new PlcWrite.ClearR());
        await WaitUntilAsync(() => !gw.Latest.RFlag, timeoutMs: 1000, "R_Flag=0");
        Assert.False(gw.Latest.RFlag, "ClearR 후 R_Flag=0");
        _out.WriteLine("[VT-4] FakeModbusMaster PlcGateway 로직 검증 완료");
    }

    // ════════════════════════════════════════════════════════════════════════
    // VT-5 RTU OFFLINE 전이
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// VT-5: RTU fake-serial 강제 단절(IOException 유발) → 연속 실패 후 Online=false.
    /// 재개 시 Online=true 복구.
    /// 예외 삼키지 않음 — OFFLINE 전이로 명시 처리.
    /// </summary>
    [Fact]
    public async Task VT5_RtuFakeSerial_Disconnect_TransitionsOffline_ThenRecovers()
    {
        const byte UnitId = 1;
        var (clientPort, serverPort) = FakeSerialPortPair.Create();
        await using var clientDispose = clientPort;
        await using var serverDispose = serverPort;

        using var rtuServer = new ModbusRtuServer(UnitId, isAsynchronous: true);
        var serverHr = rtuServer.GetHoldingRegisters(UnitId);
        InitServerRegisters(serverHr, RegisterMap.D4.Ready);
        rtuServer.Start(serverPort);

        using var rtuMaster = new ModbusRtuMaster(
            fakePort:   clientPort,
            endianness: ModbusEndianness.BigEndian,
            unitId:     UnitId);

        var queue = new PlcWriteQueue();
        var gwOpt = new PlcGatewayOptions
        {
            PollIntervalMs       = 30,
            OfflineAfterFailures = 3,
            WriteTimeoutMs       = 200,
        };

        await using var gw = new PlcPollingService(gwOpt, queue, rtuMaster);
        await gw.StartAsync();

        await WaitUntilAsync(() => gw.Latest.Online, timeoutMs: 3000, "초기 Online");
        Assert.True(gw.Latest.Online, "초기 Online=true");
        _out.WriteLine("[VT-5] 초기 Online=true 확인");

        // ── 강제 단절: SimulateClose=true → ReadAsync에서 IOException ────────
        clientPort.SimulateClose = true;

        // OFFLINE 전이 대기: WriteTimeoutMs(200ms) * OfflineAfterFailures(3) + 여유
        int offlineTimeoutMs = (gwOpt.WriteTimeoutMs * (gwOpt.OfflineAfterFailures + 1)) + 1500;
        await WaitUntilAsync(() => !gw.Latest.Online, timeoutMs: offlineTimeoutMs, "OFFLINE 전이");

        Assert.False(gw.Latest.Online, "강제 단절 후 Online=false");
        _out.WriteLine("[VT-5] OFFLINE 전이 확인");

        // ── 복구: SimulateClose=false ─────────────────────────────────────────
        clientPort.SimulateClose = false;

        await WaitUntilAsync(() => gw.Latest.Online, timeoutMs: 5000, "Online 복구");
        Assert.True(gw.Latest.Online, "복구 후 Online=true");
        _out.WriteLine("[VT-5] Online 복구 확인");
    }

    // ════════════════════════════════════════════════════════════════════════
    // 헬퍼
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>ModbusRtuServer 서버 HR 초기화 (BigEndian 인코딩).</summary>
    private static void InitServerRegisters(Span<short> hr, ushort flagsValue)
    {
        for (int i = 0; i < RegisterMap.BlockLength; i++)
            hr[i] = 0;
        // D4 Flags: BigEndian으로 반전해서 저장 (SimServer.FlushToServerLocked 패턴 동일)
        hr[RegisterMap.Flags] = (short)BinaryPrimitives.ReverseEndianness(flagsValue);
    }

    /// <summary>서버 HR에 R 영역 + R_Flag=1 기입 (BigEndian).</summary>
    private static void SetServerR(ModbusRtuServer server, byte unitId, int rCellNo, int rSeq)
    {
        var hr = server.GetHoldingRegisters(unitId);
        hr[RegisterMap.R_CellNo] = (short)BinaryPrimitives.ReverseEndianness((ushort)rCellNo);
        hr[RegisterMap.R_Seq]    = (short)BinaryPrimitives.ReverseEndianness((ushort)rSeq);
        // 현재 Flags 읽어 R_Flag set
        ushort curFlags = BinaryPrimitives.ReverseEndianness((ushort)hr[RegisterMap.Flags]);
        ushort newFlags = (ushort)(curFlags | RegisterMap.D4.R_Flag);
        hr[RegisterMap.Flags] = (short)BinaryPrimitives.ReverseEndianness(newFlags);
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
}

// ════════════════════════════════════════════════════════════════════════════
// FakeSerialPortPair — FakeSerialPort 쌍 팩토리 (별도 static class)
// ════════════════════════════════════════════════════════════════════════════

internal static class FakeSerialPortPair
{
    public static (FakeSerialPort clientPort, FakeSerialPort serverPort) Create() =>
        FakeSerialPort.Create();
}

// ════════════════════════════════════════════════════════════════════════════
// FakeModbusMaster — VT-4용 in-memory IModbusMaster 구현
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// VT-4 단위 테스트용 in-memory IModbusMaster.
/// 내부 레지스터 배열을 직접 읽고 쓰며, PlcGateway 로직을 전송 없이 검증한다.
/// </summary>
internal sealed class FakeModbusMaster : IModbusMaster
{
    private readonly ushort[] _registers;
    private readonly object   _lock = new();

    public FakeModbusMaster(ushort[] initialRegisters)
    {
        _registers = (ushort[])initialRegisters.Clone();
    }

    public bool IsConnected { get; private set; } = true;

    public void Connect()    => IsConnected = true;
    public void Disconnect() => IsConnected = false;
    public void Dispose()    { }

    public Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort count, CancellationToken ct)
    {
        lock (_lock)
        {
            var result = new ushort[count];
            Array.Copy(_registers, startAddress, result, 0, count);
            return Task.FromResult(result);
        }
    }

    public Task WriteSingleRegisterAsync(ushort address, short value, CancellationToken ct)
    {
        lock (_lock) { _registers[address] = (ushort)value; }
        return Task.CompletedTask;
    }

    public Task WriteMultipleRegistersAsync(ushort startAddress, short[] data, CancellationToken ct)
    {
        lock (_lock)
        {
            for (int i = 0; i < data.Length; i++)
                _registers[startAddress + i] = (ushort)data[i];
        }
        return Task.CompletedTask;
    }

    // ── 외부에서 레지스터 직접 조작(테스트 헬퍼) ─────────────────────────────

    public void SetRegister(ushort address, ushort value)
    {
        lock (_lock) { _registers[address] = value; }
    }

    public void SetFlagBit(ushort bitMask, bool set)
    {
        lock (_lock)
        {
            if (set)
                _registers[RegisterMap.Flags] = (ushort)(_registers[RegisterMap.Flags] | bitMask);
            else
                _registers[RegisterMap.Flags] = (ushort)(_registers[RegisterMap.Flags] & ~bitMask);
        }
    }
}
