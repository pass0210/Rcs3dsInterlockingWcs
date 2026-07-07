using System.IO.Ports;
using System.Net;
using FluentModbus;
using Microsoft.Extensions.Logging.Abstractions;

namespace Wcs.Sim3ds;

// ════════════════════════════════════════════════════════════════════════════
// SimServer 전송 계층 추상화 (S-SIM3DS-RTU)
//
//   SimServer 상태기계는 이 seam이 노출하는 Server(레지스터 버퍼 = ModbusServer 기반)만
//   사용한다. Flush/Pull·프리셋·고장주입·테스트 훅은 전송에 무관하다.
//   TCP 소켓 바인딩 / RTU 시리얼 오픈이라는 구체 차이(FluentModbus API 발산 — TCP는
//   ctor+AddUnit+Start(IPEndPoint), RTU는 ctor(unitId)+Start(portName|serialPort))는
//   각 구현이 흡수한다. → "전송만 교체, 의미 동일" 불변식을 위한 최소 seam.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Sim3ds 서버의 전송 계층. 구현체는 FluentModbus 서버(TCP/RTU) 하나를 소유하고
/// 레지스터 버퍼(<see cref="Server"/>) 접근·기동·종료를 제공한다.
/// </summary>
internal interface ISimTransport : IDisposable
{
    /// <summary>레지스터 버퍼 접근용 기반 서버(GetHoldingRegisters 등 공통 API).</summary>
    ModbusServer Server { get; }

    /// <summary>로그·관측용 엔드포인트 표기(예: "TCP 127.0.0.1:1502", "RTU COM6 9600/Even/One unit=1").</summary>
    string Endpoint { get; }

    /// <summary>전송을 개시한다(TCP: 소켓 바인딩 / RTU: 시리얼 포트 오픈).</summary>
    void Start();

    /// <summary>전송을 정지한다.</summary>
    void Stop();
}

// ─── TCP 전송(기본·현행 보존) ────────────────────────────────────────────────

/// <summary>
/// Modbus TCP 슬레이브 전송. 기존 SimServer 동작을 그대로 보존한다
/// (ModbusTcpServer + AddUnit + Start(IPEndPoint)).
/// </summary>
internal sealed class TcpSimTransport : ISimTransport
{
    private readonly ModbusTcpServer _server;
    private readonly IPEndPoint      _endpoint;

    public ModbusServer Server   => _server;
    public string       Endpoint { get; }

    public TcpSimTransport(string host, int port, byte unitId)
    {
        _server = new ModbusTcpServer(NullLogger<ModbusTcpServer>.Instance);
        _server.AddUnit(unitId);                       // 버퍼 확보 — Start 이전에도 GetHoldingRegisters 가능
        _endpoint = new IPEndPoint(IPAddress.Parse(host), port);
        Endpoint  = $"TCP {host}:{port}";
    }

    public void Start() => _server.Start(_endpoint);
    public void Stop()  => _server.Stop();
    public void Dispose() => _server.Dispose();
}

// ─── RTU 전송(시리얼 슬레이브) ───────────────────────────────────────────────

/// <summary>
/// Modbus RTU 슬레이브 전송. 물리 COM 포트 또는 테스트용 주입 시리얼 포트로 기동한다.
/// FluentModbus 5.3.2: <c>new ModbusRtuServer(unitId, isAsynchronous:true)</c>(unit ctor 등록) →
/// 시리얼 파라미터 프로퍼티 설정 → <c>Start(portName)</c>(물리) 또는 <c>Start(serialPort)</c>(주입).
/// 시리얼 파라미터(Baud/Parity/StopBits/타임아웃)는 전부 설정 주입(하드코딩 금지 — 절대규칙 #7).
/// </summary>
internal sealed class RtuSimTransport : ISimTransport
{
    private readonly ModbusRtuServer       _server;
    private readonly string?               _portName;   // 물리 COM (주입 모드면 null)
    private readonly IModbusRtuSerialPort? _serialPort; // 테스트 seam (물리 모드면 null)

    public ModbusServer Server   => _server;
    public string       Endpoint { get; }

    /// <summary>물리 COM 포트 기동용. 시리얼 파라미터는 Start(portName)이 포트를 열 때 적용된다.</summary>
    public RtuSimTransport(
        byte     unitId,
        string   portName,
        int      baudRate,
        Parity   parity,
        StopBits stopBits,
        int      readTimeoutMs,
        int      writeTimeoutMs)
    {
        _server = new ModbusRtuServer(unitId, isAsynchronous: true)
        {
            BaudRate     = baudRate,
            Parity       = parity,
            StopBits     = stopBits,
            ReadTimeout  = readTimeoutMs,
            WriteTimeout = writeTimeoutMs,
        };
        _portName = portName;
        Endpoint  = $"RTU {portName} {baudRate}/{parity}/{stopBits} unit={unitId}";
    }

    /// <summary>
    /// 테스트 seam — 주입된 <see cref="IModbusRtuSerialPort"/>(예: in-memory FakeSerialPort)로 기동.
    /// 물리 COM 불요 → CI에서 실 SimServer(RTU) 상태기계를 왕복 검증 가능(계약 (b)).
    /// 포트 생명주기는 주입자(테스트 코드)가 관리한다(ModbusRtuMaster의 externally-owned 패턴과 동형).
    /// </summary>
    public RtuSimTransport(byte unitId, IModbusRtuSerialPort serialPort)
    {
        _server     = new ModbusRtuServer(unitId, isAsynchronous: true);
        _serialPort = serialPort;
        Endpoint    = $"RTU (fake-serial {serialPort.PortName}) unit={unitId}";
    }

    public void Start()
    {
        if (_serialPort is not null) _server.Start(_serialPort);
        else                         _server.Start(_portName!);
    }

    public void Stop()  => _server.Stop();
    public void Dispose() => _server.Dispose();
}

// ─── 전송 팩토리(선택·검증) ──────────────────────────────────────────────────

/// <summary>
/// <see cref="SimServer.Options"/> + 선택적 주입 시리얼 포트 → <see cref="ISimTransport"/>.
/// 잘못된 Transport 값과 RTU 모드의 PortName 미지정은 fail-loud(절대규칙 #8·Core "Fail Loud").
/// </summary>
internal static class SimTransportFactory
{
    public static ISimTransport Create(SimServer.Options opt, byte unitId, IModbusRtuSerialPort? injectedRtuPort)
    {
        // 테스트 주입 시리얼 포트가 있으면 Transport 값과 무관하게 RTU-주입 모드
        // (물리 COM 요구를 건너뜀 — 계약 (b) fake-serial 왕복).
        if (injectedRtuPort is not null)
            return new RtuSimTransport(unitId, injectedRtuPort);

        return (opt.Transport ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "TCP" => new TcpSimTransport(opt.Host, opt.Port, unitId),
            "RTU" => CreateRtu(opt, unitId),
            _     => throw new InvalidOperationException(
                        $"알 수 없는 Sim3ds Transport 값: '{opt.Transport}'. 'Tcp' 또는 'Rtu'를 지정하세요."),
        };
    }

    private static RtuSimTransport CreateRtu(SimServer.Options opt, byte unitId)
    {
        // PortName은 안전한 기본값 없음 — 우발적 COM1 점유 방지(계약 Scope 2). 미지정 시 fail-loud.
        if (string.IsNullOrWhiteSpace(opt.PortName))
            throw new InvalidOperationException(
                "Sim3ds Transport=Rtu 인데 PortName이 지정되지 않았습니다. " +
                "우발적 COM 포트 점유 방지를 위해 RTU 모드는 PortName 명시가 필수입니다 " +
                "(예: --port COM6 또는 appsettings.Sim3ds.json 의 Sim3ds:PortName).");

        return new RtuSimTransport(
            unitId:         unitId,
            portName:       opt.PortName!,
            baudRate:       opt.BaudRate,
            parity:         opt.ParsedParity,
            stopBits:       opt.ParsedStopBits,
            readTimeoutMs:  opt.ReadTimeoutMs,
            writeTimeoutMs: opt.WriteTimeoutMs);
    }
}
