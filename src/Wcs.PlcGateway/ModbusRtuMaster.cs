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
    private readonly string?                  _portName;   // null이면 외부 주입 모드(fake serial)
    private readonly ILogger<ModbusRtuMaster> _log;
    private readonly byte                     _unitId;
    private          bool                     _initialized; // Initialize() 호출 여부 추적

    // ── 물리 COM 포트 생성자 ──────────────────────────────────────────────────

    /// <summary>
    /// 물리 COM 포트 연결용 생성자.
    /// 모든 시리얼 파라미터는 설정에서 주입(하드코딩 금지).
    /// </summary>
    public ModbusRtuMaster(
        string  portName,
        int     baudRate        = 9600,
        Parity  parity          = Parity.Even,
        StopBits stopBits       = StopBits.One,
        int     readTimeoutMs   = 1000,
        int     writeTimeoutMs  = 1000,
        byte    unitId          = 1,
        ILogger<ModbusRtuMaster>? log = null)
    {
        _portName = portName;
        _unitId   = unitId;
        _log      = log ?? NullLogger<ModbusRtuMaster>.Instance;
        _client   = new ModbusRtuClient
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
    /// in-memory fake IModbusRtuSerialPort 주입 생성자.
    /// 물리 COM 없이 CI에서 RTU 왕복 검증 가능(S-RTU VT-2·4·5).
    /// </summary>
    public ModbusRtuMaster(
        IModbusRtuSerialPort fakePort,
        ModbusEndianness     endianness = ModbusEndianness.BigEndian,
        byte                 unitId     = 1,
        ILogger<ModbusRtuMaster>? log   = null)
    {
        _portName = null; // 외부 주입 모드
        _unitId   = unitId;
        _log      = log ?? NullLogger<ModbusRtuMaster>.Instance;
        _client   = new ModbusRtuClient();
        // Initialize 호출 — IsConnected가 true로 전환됨
        _client.Initialize(fakePort, endianness);
        _initialized = true;
    }

    // ── IModbusMaster ────────────────────────────────────────────────────────

    public bool IsConnected => _client.IsConnected;

    public void Connect()
    {
        if (_initialized)
            return; // fake serial 모드: Initialize로 이미 연결됨

        if (!_client.IsConnected && _portName is not null)
            _client.Connect(_portName, ModbusEndianness.BigEndian);
    }

    public void Disconnect()
    {
        if (_initialized)
            return; // fake serial 모드: 외부 소유자가 포트를 관리

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
