using Wcs.Core;

namespace Wcs.PlcGateway;

// ════════════════════════════════════════════════════════════════════════════
// IModbusMaster — 전송 추상화 인터페이스 (S-RTU Scope A)
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Modbus 전송 추상화 — TCP·RTU 공통 인터페이스.
/// PlcPollingService·HandshakeOrchestrator는 이 인터페이스에만 의존하며,
/// 구체 전송 타입(ModbusTcpClient/ModbusRtuClient)을 직접 참조하지 않는다.
///
/// unitId·엔디안은 어댑터가 관리한다(설정 주입).
/// ReadHoldingRegisters: D0~D6(RegisterMap.BlockLength) 일괄 FC03.
/// WriteSingle: FC06 단일 레지스터.
/// WriteMultiple: FC16 복수 레지스터.
///
/// _clientLock(SemaphoreSlim) 직렬화는 PlcPollingService가 유지(인터페이스 외부).
/// Connect/Disconnect도 임계구역 내에서만 호출한다(M2 IT-4b off-lock 금지 원칙 보존).
/// </summary>
public interface IModbusMaster : IDisposable
{
    /// <summary>현재 연결 상태. true=연결됨.</summary>
    bool IsConnected { get; }

    /// <summary>
    /// PLC에 연결한다.
    /// 이미 연결된 경우 구현체는 무시하거나 재연결할 수 있다.
    /// </summary>
    void Connect();

    /// <summary>
    /// 연결을 끊는다.
    /// 미연결 상태에서 호출하면 예외 없이 무시한다.
    /// </summary>
    void Disconnect();

    /// <summary>
    /// D0~D6 일괄 FC03 읽기.
    /// </summary>
    /// <param name="startAddress">시작 레지스터 주소(D0=0).</param>
    /// <param name="count">읽을 레지스터 수(RegisterMap.BlockLength=7).</param>
    /// <param name="ct">취소 토큰.</param>
    /// <returns>읽은 ushort 배열.</returns>
    Task<ushort[]> ReadHoldingRegistersAsync(ushort startAddress, ushort count, CancellationToken ct);

    /// <summary>
    /// FC06 단일 레지스터 쓰기.
    /// </summary>
    /// <param name="address">쓸 레지스터 주소.</param>
    /// <param name="value">기입할 값(short — FluentModbus 시그니처 일치).</param>
    /// <param name="ct">취소 토큰.</param>
    Task WriteSingleRegisterAsync(ushort address, short value, CancellationToken ct);

    /// <summary>
    /// FC16 복수 레지스터 쓰기.
    /// </summary>
    /// <param name="startAddress">시작 레지스터 주소.</param>
    /// <param name="data">기입할 short 배열(FluentModbus 시그니처 일치).</param>
    /// <param name="ct">취소 토큰.</param>
    Task WriteMultipleRegistersAsync(ushort startAddress, short[] data, CancellationToken ct);
}
