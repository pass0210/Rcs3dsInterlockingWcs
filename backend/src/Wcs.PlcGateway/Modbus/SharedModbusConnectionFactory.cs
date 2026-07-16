using Microsoft.Extensions.Logging;

namespace Wcs.PlcGateway;

// ════════════════════════════════════════════════════════════════════════════
// SharedModbusConnectionFactory — 설정 → ISharedModbusConnection 1개 생성
//   (S-MULTISORTER-SHARED-BUS Phase 2 — 계약 B-fac 작은 가산)
//
//   Phase 1의 SharedTcpModbusConnection / SharedRtuModbusConnection 타입을 재작성하지 않고
//   소비만 한다(계약 B 원칙). 한 물리 버스(같은 host:port / 같은 PortName)당 이 팩토리로
//   ISharedModbusConnection 1개를 만들어 ModbusBus에 넘긴다.
//
//   기존 ModbusMasterFactory(소터당 독립 마스터 = 구경로)와 병존한다(삭제 금지 — M2 테스트 참조).
//   ModbusMasterFactory는 IModbusMaster(독립 포트 Open)를, 이 팩토리는 ISharedModbusConnection
//   (버스당 1 Open, unitId per-call)을 만든다.
//
//   BusKey 규칙은 SharedXxxConnection과 일치(TCP=`host:port` / RTU=`portName`).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 버스 연결 생성 seam(테스트가 데코레이터를 끼울 수 있도록 — S-MULTISORTER-SHARED-BUS Phase 2 C4 fix).
/// 프로덕션 기본 구현은 <see cref="DefaultSharedModbusConnectionFactory"/>(정적 팩토리 위임).
/// SorterRegistryFactory는 DI에 이 서비스가 등록돼 있으면 그것을, 없으면 기본 구현을 쓴다.
/// </summary>
public interface ISharedModbusConnectionFactory
{
    ISharedModbusConnection Create(PlcTransportOptions opt, ILogger? log = null);
}

/// <summary>기본 구현 — 정적 <see cref="SharedModbusConnectionFactory.Create"/>에 위임(무동작 래퍼).</summary>
public sealed class DefaultSharedModbusConnectionFactory : ISharedModbusConnectionFactory
{
    public ISharedModbusConnection Create(PlcTransportOptions opt, ILogger? log = null)
        => SharedModbusConnectionFactory.Create(opt, log);
}

/// <summary>
/// <see cref="PlcTransportOptions"/> 전송 설정에서 버스당 1개 <see cref="ISharedModbusConnection"/>을 생성한다.
/// Transport=Tcp → <see cref="SharedTcpModbusConnection"/> / Transport=Rtu → <see cref="SharedRtuModbusConnection"/>.
/// 알 수 없는 Transport 값은 fail-loud(InvalidOperationException).
/// </summary>
public static class SharedModbusConnectionFactory
{
    public static ISharedModbusConnection Create(PlcTransportOptions opt, ILogger? log = null)
    {
        // BusKeyOf와 동일한 정규화(Trim) — " Tcp " 같은 값도 그룹핑-일관 경로로(불일치 시 여전히 fail-loud, 메시지만 명확).
        return (opt.Transport ?? "").Trim().ToUpperInvariant() switch
        {
            "TCP" => new SharedTcpModbusConnection(
                        host:          opt.Host,
                        port:          opt.Port,
                        readTimeoutMs: opt.ReadTimeoutMs,
                        log:           log),
            "RTU" => new SharedRtuModbusConnection(
                        portName:       opt.PortName,
                        baudRate:       opt.BaudRate,
                        parity:         opt.ParsedParity,
                        stopBits:       opt.ParsedStopBits,
                        readTimeoutMs:  opt.ReadTimeoutMs,
                        writeTimeoutMs: opt.WriteTimeoutMs,
                        // endianness는 설정 표면에 없어 고정 BigEndian(OQ10) — SharedRtuModbusConnection 기본값 사용.
                        log:            log),
            var t => throw new InvalidOperationException(
                        $"알 수 없는 Transport 값: '{t}'. 'Tcp' 또는 'Rtu'를 지정하세요."),
        };
    }
}
