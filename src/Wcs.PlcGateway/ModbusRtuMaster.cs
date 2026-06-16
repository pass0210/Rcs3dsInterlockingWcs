using FluentModbus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.IO.Ports;

namespace Wcs.PlcGateway;

// ════════════════════════════════════════════════════════════════════════════
// ModbusRtuMaster — IModbusMaster RTU 어댑터 (S-RTU Scope C)
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Modbus RTU 어댑터.
/// ModbusRtuClient를 래핑. COM 포트·Baud·Parity·Stop·Read/WriteTimeout·UnitId 전부 설정값.
/// IModbusRtuSerialPort를 외부 주입받으면 in-memory fake serial로 동작(테스트 인프라용).
/// OFFLINE 전이는 RTU 예외(시리얼 타임아웃·IOException)에서도 동작.
/// </summary>
public sealed class ModbusRtuMaster : IModbusMaster
{
    private readonly ModbusRtuClient          _client;
    // null이면 외부 소유 포트 모드(fake serial / 이미 Initialize된 포트).
    // "Externally owned port" 패턴 — 이 경우 Connect/Disconnect는 no-op이며
    // 포트 생명주기는 호출자(테스트 코드)가 관리한다.
    private readonly bool                     _externallyOwnedPort;
    private readonly string?                  _portName;     // 물리 COM 포트 이름(재연결용), 외부 소유이면 null
    private readonly ILogger<ModbusRtuMaster> _log;
    private readonly byte                     _unitId;
    private readonly ModbusEndianness         _endianness;   // S-RTU MINOR-4: 엔디안 필드 통일

    // ── 물리 COM 포트 생성자 ──────────────────────────────────────────────────

    /// <summary>
    /// 물리 COM 포트 연결용 생성자.
    /// 모든 시리얼 파라미터는 설정에서 주입(하드코딩 금지).
    /// 엔디안 기본값 = BigEndian (VEICHI PLC 기준, 현장 다르면 설정 추가).
    /// </summary>
    public ModbusRtuMaster(
        string   portName,
        int      baudRate        = 9600,
        Parity   parity          = Parity.Even,
        StopBits stopBits        = StopBits.One,
        int      readTimeoutMs   = 1000,
        int      writeTimeoutMs  = 1000,
        byte     unitId          = 1,
        ModbusEndianness endianness = ModbusEndianness.BigEndian,  // S-RTU MINOR-4
        ILogger<ModbusRtuMaster>? log = null)
    {
        _externallyOwnedPort = false;  // 이 생성자는 직접 포트를 소유
        _portName            = portName;
        _unitId              = unitId;
        _endianness          = endianness;
        _log                 = log ?? NullLogger<ModbusRtuMaster>.Instance;
        _client              = new ModbusRtuClient
        {
            BaudRate     = baudRate,
            Parity       = parity,
            StopBits     = stopBits,
            ReadTimeout  = readTimeoutMs,
            WriteTimeout = writeTimeoutMs,
        };
    }

    // ── in-memory fake serial 주입 생성자 (테스트 전용) ──────────────────────

    /// <summary>
    /// in-memory fake IModbusRtuSerialPort 주입 생성자 (테스트 인프라용).
    /// 물리 COM 없이 CI에서 RTU 왕복 검증 가능(S-RTU VT-2·4·5).
    ///
    /// <para><b>Externally owned port 패턴:</b> 포트 생명주기는 호출자가 관리한다.
    /// 이 생성자로 생성된 인스턴스의 Connect/Disconnect는 no-op — 이미
    /// <see cref="ModbusRtuClient.Initialize"/>로 연결된 상태이며,
    /// 포트 Close/Dispose는 테스트 코드(FakeSerialPort)가 수행한다.</para>
    /// </summary>
    public ModbusRtuMaster(
        IModbusRtuSerialPort fakePort,
        ModbusEndianness     endianness = ModbusEndianness.BigEndian,
        byte                 unitId     = 1,
        ILogger<ModbusRtuMaster>? log   = null)
    {
        _externallyOwnedPort = true;   // 외부 소유 포트 — Connect/Disconnect no-op
        _portName            = null;   // 외부 소유: portName 없음
        _unitId              = unitId;
        _endianness          = endianness;
        _log                 = log ?? NullLogger<ModbusRtuMaster>.Instance;
        _client              = new ModbusRtuClient();
        // Initialize 호출 — IsConnected가 true로 전환됨
        _client.Initialize(fakePort, endianness);
    }

    // ── IModbusMaster ────────────────────────────────────────────────────────

    public bool IsConnected => _client.IsConnected;

    /// <summary>
    /// PLC에 연결한다.
    /// <para>외부 소유 포트 모드(<see cref="_externallyOwnedPort"/>)에서는 no-op —
    /// Initialize로 이미 연결된 상태이며 포트를 다시 Open하지 않는다.</para>
    /// </summary>
    public void Connect()
    {
        if (_externallyOwnedPort)
            return; // 외부 소유 포트: 이미 Initialize로 연결됨 — no-op

        if (!_client.IsConnected && _portName is not null)
            _client.Connect(_portName, _endianness);
    }

    /// <summary>
    /// 연결을 끊는다.
    /// <para>외부 소유 포트 모드(<see cref="_externallyOwnedPort"/>)에서는 no-op —
    /// 포트 생명주기는 호출자(테스트 코드)가 관리한다.</para>
    /// </summary>
    public void Disconnect()
    {
        if (_externallyOwnedPort)
            return; // 외부 소유 포트: 호출자가 관리 — no-op

        try { _client.Close(); }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[RTU 어댑터] Close 예외 무시");
        }
    }

    public async Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort count, CancellationToken ct)
    {
        var mem    = await _client.ReadHoldingRegistersAsync<short>(_unitId, startAddress, count, ct).ConfigureAwait(false);
        var result = new ushort[count];
        var span   = mem.Span;
        for (int i = 0; i < count; i++)
            result[i] = (ushort)span[i];
        return result;
    }

    public Task WriteSingleRegisterAsync(ushort address, short value, CancellationToken ct) =>
        _client.WriteSingleRegisterAsync(_unitId, address, value, ct);

    public Task WriteMultipleRegistersAsync(ushort startAddress, short[] data, CancellationToken ct) =>
        _client.WriteMultipleRegistersAsync<short>(_unitId, startAddress, data, ct);

    // ── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        try { Disconnect(); } catch { }
        _client.Dispose();
    }
}
