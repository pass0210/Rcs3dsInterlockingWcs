using FluentModbus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Wcs.PlcGateway;

// ════════════════════════════════════════════════════════════════════════════
// ModbusTcpMaster — IModbusMaster TCP 어댑터 (S-RTU Scope B)
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Modbus TCP 어댑터.
/// 기존 ModbusTcpClient를 1:1 래핑(Host/Port·BigEndian·ReadTimeout 의미 보존).
/// M2 IT-1~5·3c·4b 단언·코드 변경 없이 GREEN(회귀 0).
/// </summary>
public sealed class ModbusTcpMaster : IModbusMaster
{
    private readonly ModbusTcpClient         _client;
    private readonly System.Net.IPEndPoint   _endpoint;
    private readonly ILogger<ModbusTcpMaster> _log;
    private readonly byte                    _unitId;

    public ModbusTcpMaster(
        string host,
        int    port,
        int    readTimeoutMs = 1000,
        byte   unitId        = 1,
        ILogger<ModbusTcpMaster>? log = null)
    {
        _unitId   = unitId;
        _endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Parse(host), port);
        _log      = log ?? NullLogger<ModbusTcpMaster>.Instance;
        _client   = new ModbusTcpClient { ReadTimeout = readTimeoutMs };
    }

    // ── IModbusMaster ────────────────────────────────────────────────────────

    public bool IsConnected => _client.IsConnected;

    public void Connect()
    {
        if (!_client.IsConnected)
            _client.Connect(_endpoint, ModbusEndianness.BigEndian);
    }

    public void Disconnect()
    {
        try { _client.Disconnect(); }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[TCP 어댑터] Disconnect 예외 무시");
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
