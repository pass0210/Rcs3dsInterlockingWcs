using System.IO.Ports;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Wcs.PlcGateway;

// ════════════════════════════════════════════════════════════════════════════
// PlcTransportOptions — 전송 설정 (S-RTU Scope D)
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// appsettings.json Plc 섹션에서 바인딩하는 전송 설정.
/// Transport 키 미지정 시 기본값 = Rtu (현장 우선).
///
/// 스키마는 소터별 독립 전송 N 확장 표현 가능하도록 설계.
/// 런타임은 단일 소터(Sorter[0])까지만 구현.
/// </summary>
public sealed record PlcTransportOptions
{
    // ── 공통 ────────────────────────────────────────────────────────────────

    /// <summary>전송 선택: "Tcp" 또는 "Rtu". 미지정 기본값 = Rtu.</summary>
    public string Transport { get; init; } = "Rtu";

    // ── TCP 전용 ─────────────────────────────────────────────────────────────

    /// <summary>Modbus TCP 서버 호스트. Transport=Tcp 시 사용.</summary>
    public string Host { get; init; } = "127.0.0.1";

    /// <summary>Modbus TCP 서버 포트. Transport=Tcp 시 사용.</summary>
    public int    Port { get; init; } = 502;

    // ── RTU 전용 ─────────────────────────────────────────────────────────────

    /// <summary>COM 포트명 (예: COM1, /dev/ttyUSB0). Transport=Rtu 시 사용.</summary>
    public string PortName     { get; init; } = "COM1";

    /// <summary>보드레이트. 기본 9600.</summary>
    public int    BaudRate     { get; init; } = 9600;

    /// <summary>패리티. "Even"/"Odd"/"None". 기본 Even.</summary>
    public string Parity       { get; init; } = "Even";

    /// <summary>스톱 비트. "One"/"Two". 기본 One.</summary>
    public string StopBits     { get; init; } = "One";

    /// <summary>읽기 타임아웃 ms. 기본 1000.</summary>
    public int    ReadTimeoutMs  { get; init; } = 1000;

    /// <summary>쓰기 타임아웃 ms. 기본 1000.</summary>
    public int    WriteTimeoutMs { get; init; } = 1000;

    /// <summary>Modbus 유닛 ID (슬레이브 주소). 기본 1.</summary>
    public byte   UnitId       { get; init; } = 1;

    // ── 파싱 헬퍼 ────────────────────────────────────────────────────────────

    public Parity   ParsedParity   => Enum.Parse<Parity>(Parity, ignoreCase: true);
    public StopBits ParsedStopBits => Enum.Parse<StopBits>(StopBits, ignoreCase: true);
}

// ════════════════════════════════════════════════════════════════════════════
// ModbusMasterFactory — 설정 → IModbusMaster 생성 (S-RTU Scope D)
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// PlcTransportOptions 설정에서 IModbusMaster를 생성한다.
/// Plc:Transport=Tcp → ModbusTcpMaster
/// Plc:Transport=Rtu → ModbusRtuMaster (기본)
/// </summary>
public static class ModbusMasterFactory
{
    public static IModbusMaster Create(
        PlcTransportOptions   opt,
        ILoggerFactory?       loggerFactory = null)
    {
        return opt.Transport.ToUpperInvariant() switch
        {
            "TCP" => CreateTcp(opt, loggerFactory),
            "RTU" => CreateRtu(opt, loggerFactory),
            var t => throw new InvalidOperationException(
                $"알 수 없는 Plc:Transport 값: '{t}'. 'Tcp' 또는 'Rtu'를 지정하세요."),
        };
    }

    private static ModbusTcpMaster CreateTcp(PlcTransportOptions opt, ILoggerFactory? lf) =>
        new(
            host:           opt.Host,
            port:           opt.Port,
            readTimeoutMs:  opt.ReadTimeoutMs,
            unitId:         opt.UnitId,
            log:            lf?.CreateLogger<ModbusTcpMaster>());

    private static ModbusRtuMaster CreateRtu(PlcTransportOptions opt, ILoggerFactory? lf) =>
        new(
            portName:       opt.PortName,
            baudRate:       opt.BaudRate,
            parity:         opt.ParsedParity,
            stopBits:       opt.ParsedStopBits,
            readTimeoutMs:  opt.ReadTimeoutMs,
            writeTimeoutMs: opt.WriteTimeoutMs,
            unitId:         opt.UnitId,
            log:            lf?.CreateLogger<ModbusRtuMaster>());
}
