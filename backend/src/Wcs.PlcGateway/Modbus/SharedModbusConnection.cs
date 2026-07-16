using FluentModbus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Ports;

namespace Wcs.PlcGateway;

// ════════════════════════════════════════════════════════════════════════════
// 공유 버스 연결 (S-MULTISORTER-SHARED-BUS 모듈 B1/B2)
//
//   한 물리 버스(같은 host:port / 같은 PortName)의 여러 슬레이브가 클라이언트+포트를 1개만 Open해
//   공유한다(B1). unitId는 이미 프레임마다 실려 나가므로(FluentModbus 클라 요청마다 unitId 인자),
//   공유 클라이언트를 감싼 per-slave 어댑터(BusSlaveMaster)가 자기 unitId를 실어 슬레이브를 구분한다(B2).
//
//   설계 선택(계약 B2 권장): IModbusMaster 시그니처를 깨지 않는 가산적 접근.
//   - ISharedModbusConnection: unitId per-call 저수준 연결(버스당 1개 Open).
//   - BusSlaveMaster: 고정 unitId를 주입해 기존 IModbusMaster로 노출(폴러/핸드셰이크 무변경).
//
//   스레드 안전: 호출자(ModbusBus)가 버스 락으로 직렬화한다(B3). 연결 자체는 내부 락 없음
//   (FluentModbus 클라이언트는 단일 트랜잭션 버퍼 — 버스 락 밖에서 동시 호출 금지).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 버스당 1개 Open하는 저수준 Modbus 연결(TCP 클라이언트 1개 / RTU 포트 1개). 요청마다 unitId를 받아
/// 슬레이브를 구분한다. 트랜잭션 직렬화는 외부(버스 락)가 담당한다.
/// </summary>
public interface ISharedModbusConnection : IDisposable
{
    /// <summary>버스 식별(로그·진단용, 예: "127.0.0.1:1502" / "COM3").</summary>
    string BusKey { get; }

    /// <summary>현재 연결 상태.</summary>
    bool IsConnected { get; }

    /// <summary>버스를 Open한다(이미 열려 있으면 무동작 — 멤버 슬레이브들이 공유·멱등).</summary>
    void Connect();

    /// <summary>버스를 닫는다.</summary>
    void Disconnect();

    Task<ushort[]> ReadHoldingRegistersAsync(byte unitId, ushort startAddress, ushort count, CancellationToken ct);
    Task WriteSingleRegisterAsync(byte unitId, ushort address, short value, CancellationToken ct);
    Task WriteMultipleRegistersAsync(byte unitId, ushort startAddress, short[] data, CancellationToken ct);
}

// ─── TCP 공유 연결 ───────────────────────────────────────────────────────────

/// <summary>
/// 한 host:port에 TCP 클라이언트 1개를 Open해 여러 unitId 요청을 실어 보낸다(TCP=테스트 vehicle — OQ2).
/// 엔디안·ReadTimeout 의미는 <see cref="ModbusTcpMaster"/>와 동일(BigEndian).
/// </summary>
public sealed class SharedTcpModbusConnection : ISharedModbusConnection
{
    private readonly ModbusTcpClient       _client;
    private readonly System.Net.IPEndPoint _endpoint;
    private readonly ILogger               _log;

    public string BusKey { get; }

    public SharedTcpModbusConnection(string host, int port, int readTimeoutMs = 1000, ILogger? log = null)
    {
        _endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse(host), port);
        _log      = log ?? NullLogger.Instance;
        _client   = new ModbusTcpClient { ReadTimeout = readTimeoutMs };
        BusKey    = $"{host}:{port}";
    }

    public bool IsConnected => _client.IsConnected;

    public void Connect()
    {
        if (!_client.IsConnected)
            _client.Connect(_endpoint, ModbusEndianness.BigEndian);
    }

    public void Disconnect()
    {
        try { _client.Disconnect(); }
        catch (Exception ex) { _log.LogDebug(ex, "[공유버스 TCP] Disconnect 예외 무시 ({BusKey})", BusKey); }
    }

    public async Task<ushort[]> ReadHoldingRegistersAsync(byte unitId, ushort startAddress, ushort count, CancellationToken ct)
    {
        var mem    = await _client.ReadHoldingRegistersAsync<short>(unitId, startAddress, count, ct).ConfigureAwait(false);
        var result = new ushort[count];
        var span   = mem.Span;
        for (int i = 0; i < count; i++) result[i] = (ushort)span[i];
        return result;
    }

    public Task WriteSingleRegisterAsync(byte unitId, ushort address, short value, CancellationToken ct) =>
        _client.WriteSingleRegisterAsync(unitId, address, value, ct);

    public Task WriteMultipleRegistersAsync(byte unitId, ushort startAddress, short[] data, CancellationToken ct) =>
        _client.WriteMultipleRegistersAsync<short>(unitId, startAddress, data, ct);

    public void Dispose()
    {
        try { Disconnect(); } catch { }
        _client.Dispose();
    }
}

// ─── RTU 공유 연결(현장 목표 — 물리 포트 1개 공유) ───────────────────────────

/// <summary>
/// 한 COM 포트에 RTU 클라이언트 1개를 Open해 여러 unitId 요청을 실어 보낸다(현장 운영 목표 — OQ2).
/// 물리 COM 또는 테스트용 주입 시리얼 포트(externally-owned) 지원. 시리얼 파라미터는 전부 설정 주입.
/// </summary>
public sealed class SharedRtuModbusConnection : ISharedModbusConnection
{
    private readonly ModbusRtuClient       _client;
    private readonly bool                  _externallyOwnedPort;
    private readonly string?               _portName;
    private readonly ModbusEndianness      _endianness;
    private readonly ILogger               _log;

    public string BusKey { get; }

    /// <summary>물리 COM 포트 공유 버스.</summary>
    public SharedRtuModbusConnection(
        string   portName,
        int      baudRate       = 9600,
        Parity   parity         = Parity.Even,
        StopBits stopBits       = StopBits.One,
        int      readTimeoutMs  = 1000,
        int      writeTimeoutMs = 1000,
        ModbusEndianness endianness = ModbusEndianness.BigEndian,
        ILogger? log = null)
    {
        _externallyOwnedPort = false;
        _portName            = portName;
        _endianness          = endianness;
        _log                 = log ?? NullLogger.Instance;
        _client              = new ModbusRtuClient
        {
            BaudRate     = baudRate,
            Parity       = parity,
            StopBits     = stopBits,
            ReadTimeout  = readTimeoutMs,
            WriteTimeout = writeTimeoutMs,
        };
        BusKey = portName;
    }

    /// <summary>테스트용 주입 시리얼 포트(externally-owned — Connect/Disconnect no-op).</summary>
    public SharedRtuModbusConnection(
        IModbusRtuSerialPort fakePort,
        ModbusEndianness endianness = ModbusEndianness.BigEndian,
        ILogger? log = null)
    {
        _externallyOwnedPort = true;
        _portName            = null;
        _endianness          = endianness;
        _log                 = log ?? NullLogger.Instance;
        _client              = new ModbusRtuClient();
        _client.Initialize(fakePort, endianness);
        BusKey = $"(fake-serial {fakePort.PortName})";
    }

    public bool IsConnected => _client.IsConnected;

    public void Connect()
    {
        if (_externallyOwnedPort) return;               // 이미 Initialize로 연결됨
        if (!_client.IsConnected && _portName is not null)
            _client.Connect(_portName, _endianness);
    }

    public void Disconnect()
    {
        if (_externallyOwnedPort) return;               // 포트 생명주기는 호출자
        try { _client.Close(); }
        catch (Exception ex) { _log.LogDebug(ex, "[공유버스 RTU] Close 예외 무시 ({BusKey})", BusKey); }
    }

    public async Task<ushort[]> ReadHoldingRegistersAsync(byte unitId, ushort startAddress, ushort count, CancellationToken ct)
    {
        var mem    = await _client.ReadHoldingRegistersAsync<short>(unitId, startAddress, count, ct).ConfigureAwait(false);
        var result = new ushort[count];
        var span   = mem.Span;
        for (int i = 0; i < count; i++) result[i] = (ushort)span[i];
        return result;
    }

    public Task WriteSingleRegisterAsync(byte unitId, ushort address, short value, CancellationToken ct) =>
        _client.WriteSingleRegisterAsync(unitId, address, value, ct);

    public Task WriteMultipleRegistersAsync(byte unitId, ushort startAddress, short[] data, CancellationToken ct) =>
        _client.WriteMultipleRegistersAsync<short>(unitId, startAddress, data, ct);

    public void Dispose()
    {
        try { Disconnect(); } catch { }
        _client.Dispose();
    }
}

// ─── per-slave 어댑터 (IModbusMaster — 시그니처 무변경 B2) ────────────────────

/// <summary>
/// 공유 버스 연결 위의 한 슬레이브(unitId 고정)를 기존 <see cref="IModbusMaster"/>로 노출한다.
/// 요청마다 자기 unitId를 실어 공유 연결로 라우팅한다(B2). 연결 생명주기는 버스가 소유하므로
/// <see cref="Dispose"/>는 no-op(공유 연결을 닫지 않는다). Connect/Disconnect는 공유 연결에 위임(멱등).
/// </summary>
public sealed class BusSlaveMaster : IModbusMaster
{
    private readonly ISharedModbusConnection _conn;
    private readonly byte                    _unitId;

    public BusSlaveMaster(ISharedModbusConnection conn, byte unitId)
    {
        _conn   = conn;
        _unitId = unitId;
    }

    public byte UnitId => _unitId;

    public bool IsConnected => _conn.IsConnected;

    // 첫 슬레이브의 EnsureConnected가 버스를 1회 Open(이후 멤버는 멱등 no-op) → 버스당 1 Open(B1).
    public void Connect()    => _conn.Connect();
    public void Disconnect() => _conn.Disconnect();

    public Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort count, CancellationToken ct) =>
        _conn.ReadHoldingRegistersAsync(_unitId, startAddress, count, ct);

    public Task WriteSingleRegisterAsync(ushort address, short value, CancellationToken ct) =>
        _conn.WriteSingleRegisterAsync(_unitId, address, value, ct);

    public Task WriteMultipleRegistersAsync(ushort startAddress, short[] data, CancellationToken ct) =>
        _conn.WriteMultipleRegistersAsync(_unitId, startAddress, data, ct);

    // 공유 연결 생명주기는 ModbusBus가 소유 — 어댑터 Dispose는 no-op(형제 슬레이브 연결 보호).
    public void Dispose() { }
}
