using System.IO.Ports;
using System.Net;
using FluentModbus;
using Microsoft.Extensions.Logging.Abstractions;

namespace Wcs.Sim3ds;

// ════════════════════════════════════════════════════════════════════════════
// SimServer 전송 계층 추상화 (S-SIM3DS-RTU · 멀티유닛 확장 S-MULTISORTER-SHARED-BUS)
//
//   SimServer 상태기계(슬레이브들)는 이 seam이 노출하는 Server(레지스터 버퍼)만 사용한다.
//   한 물리 버스(TCP 엔드포인트 / RTU 포트)에서 여러 unitId를 응답하도록, 전송 생성 시 unitId 목록을
//   받아 유닛 수만큼 버퍼를 확보한다(TCP: AddUnit(unitId) 반복 / RTU: 멀티유닛 ctor).
//   단일 유닛 목록은 현행과 바이트/동작 동일(회귀 0).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Sim3ds 서버의 전송 계층. 구현체는 FluentModbus 서버(TCP/RTU) 하나를 소유하고
/// 레지스터 버퍼(<see cref="Server"/>) 접근·기동·종료를 제공한다(유닛 다중 등록 포함).
/// </summary>
internal interface ISimTransport : IDisposable
{
    /// <summary>레지스터 버퍼 접근용 기반 서버(GetHoldingRegisters(unitId) 등 공통 API).</summary>
    ModbusServer Server { get; }

    /// <summary>로그·관측용 엔드포인트 표기.</summary>
    string Endpoint { get; }

    /// <summary>전송을 개시한다(TCP: 소켓 바인딩 / RTU: 시리얼 포트 오픈).</summary>
    void Start();

    /// <summary>전송을 정지한다.</summary>
    void Stop();
}

// ─── TCP 전송(기본·현행 보존) ────────────────────────────────────────────────

/// <summary>
/// Modbus TCP 슬레이브 전송. unitId 목록을 유닛 수만큼 AddUnit으로 등록한다
/// (단일 유닛은 현행 동작 동일 — AddUnit 1회).
/// </summary>
internal sealed class TcpSimTransport : ISimTransport
{
    private readonly ModbusTcpServer _server;
    private readonly IPEndPoint      _endpoint;

    public ModbusServer Server   => _server;
    public string       Endpoint { get; }

    public TcpSimTransport(string host, int port, IReadOnlyList<byte> unitIds)
    {
        _server = new ModbusTcpServer(NullLogger<ModbusTcpServer>.Instance);
        foreach (var u in unitIds)
            _server.AddUnit(u);                        // 버퍼 확보 — Start 이전에도 GetHoldingRegisters 가능
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
/// FluentModbus 5.3.2 멀티유닛: 단일 유닛은 <c>ModbusRtuServer(unitId, isAsynchronous)</c>(현행 보존),
/// 멀티유닛은 <c>ModbusRtuServer(IEnumerable&lt;byte&gt;, isAsynchronous)</c>로 여러 슬레이브 주소 응답.
/// 시리얼 파라미터는 전부 설정 주입(하드코딩 금지 — 절대규칙 #7).
/// </summary>
internal sealed class RtuSimTransport : ISimTransport
{
    private readonly ModbusRtuServer       _server;
    private readonly string?               _portName;   // 물리 COM (주입 모드면 null)
    private readonly IModbusRtuSerialPort? _serialPort; // 테스트 seam (물리 모드면 null)

    public ModbusServer Server   => _server;
    public string       Endpoint { get; }

    /// <summary>물리 COM 포트 기동용.</summary>
    public RtuSimTransport(
        IReadOnlyList<byte> unitIds,
        string   portName,
        int      baudRate,
        Parity   parity,
        StopBits stopBits,
        int      readTimeoutMs,
        int      writeTimeoutMs)
    {
        _server = CreateServer(unitIds);
        _server.BaudRate     = baudRate;
        _server.Parity       = parity;
        _server.StopBits     = stopBits;
        _server.ReadTimeout  = readTimeoutMs;
        _server.WriteTimeout = writeTimeoutMs;
        _portName = portName;
        Endpoint  = $"RTU {portName} {baudRate}/{parity}/{stopBits} units=[{string.Join(",", unitIds)}]";
    }

    /// <summary>테스트 seam — 주입된 <see cref="IModbusRtuSerialPort"/>로 기동(물리 COM 불요).</summary>
    public RtuSimTransport(IReadOnlyList<byte> unitIds, IModbusRtuSerialPort serialPort)
    {
        _server     = CreateServer(unitIds);
        _serialPort = serialPort;
        Endpoint    = $"RTU (fake-serial {serialPort.PortName}) units=[{string.Join(",", unitIds)}]";
    }

    // 단일 유닛은 기존 단일 ctor(현행 바이트 보존), 멀티는 IEnumerable ctor.
    private static ModbusRtuServer CreateServer(IReadOnlyList<byte> unitIds) =>
        unitIds.Count == 1
            ? new ModbusRtuServer(unitIds[0], isAsynchronous: true)
            : new ModbusRtuServer(unitIds, isAsynchronous: true);

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
/// <see cref="SimServer.Options"/> + unitId 목록 + 선택적 주입 시리얼 포트 → <see cref="ISimTransport"/>.
/// 잘못된 Transport 값과 RTU 모드의 PortName 미지정은 fail-loud.
/// </summary>
internal static class SimTransportFactory
{
    public static ISimTransport Create(
        SimServer.Options opt, IReadOnlyList<byte> unitIds, IModbusRtuSerialPort? injectedRtuPort)
    {
        if (injectedRtuPort is not null)
            return new RtuSimTransport(unitIds, injectedRtuPort);

        return (opt.Transport ?? string.Empty).Trim().ToUpperInvariant() switch
        {
            "TCP" => new TcpSimTransport(opt.Host, opt.Port, unitIds),
            "RTU" => CreateRtu(opt, unitIds),
            _     => throw new InvalidOperationException(
                        $"알 수 없는 Sim3ds Transport 값: '{opt.Transport}'. 'Tcp' 또는 'Rtu'를 지정하세요."),
        };
    }

    private static RtuSimTransport CreateRtu(SimServer.Options opt, IReadOnlyList<byte> unitIds)
    {
        if (string.IsNullOrWhiteSpace(opt.PortName))
            throw new InvalidOperationException(
                "Sim3ds Transport=Rtu 인데 PortName이 지정되지 않았습니다. " +
                "우발적 COM 포트 점유 방지를 위해 RTU 모드는 PortName 명시가 필수입니다 " +
                "(예: --port COM6 또는 appsettings.Sim3ds.json 의 Sim3ds:PortName).");

        return new RtuSimTransport(
            unitIds:        unitIds,
            portName:       opt.PortName!,
            baudRate:       opt.BaudRate,
            parity:         opt.ParsedParity,
            stopBits:       opt.ParsedStopBits,
            readTimeoutMs:  opt.ReadTimeoutMs,
            writeTimeoutMs: opt.WriteTimeoutMs);
    }
}
