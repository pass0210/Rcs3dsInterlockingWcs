using System.Threading.Channels;
using Wcs.Core;

namespace Wcs.PlcGateway;

/// <summary>API 계층이 보는 게이트웨이 — 읽기는 스냅샷 캐시, 쓰기는 큐 투입뿐.</summary>
public interface IPlcGateway
{
    /// <summary>마지막 폴링 스냅샷(논블로킹). Online=false면 OFFLINE.</summary>
    PlcSnapshot Latest { get; }

    /// <summary>쓰기 요청을 단일 큐에 투입(완료를 기다리지 않음 — API 3s 한계와 분리).</summary>
    ValueTask EnqueueAsync(PlcWrite write, CancellationToken ct = default);
}

/// <summary>PLC 쓰기 작업. D4 비트 조작은 컨슈머가 RMW로 수행한다.</summary>
public abstract record PlcWrite
{
    /// <summary>TgtFloor(D6) 기입 — 컨슈머가 쓰기 직전 TgtFloor==0 재확인(레이스 최소화) 후 FC06.</summary>
    public sealed record SetTgtFloor(int Floor) : PlcWrite;

    /// <summary>C 영역 셀 지정: C_Flag==0 확인 → C_CellNo·C_Seq FC16 → D4 RMW로 C_Flag set.</summary>
    public sealed record CellAssign(int CellNo, int Seq) : PlcWrite;

    /// <summary>R 영역 처리 완료: R_CellNo·R_Seq=0 + D4 RMW로 R_Flag clear.</summary>
    public sealed record ClearR : PlcWrite;
}

/// <summary>
/// 단일 컨슈머 쓰기 큐 — 절대 규칙 #1. 모든 Modbus 쓰기는 이 큐의 컨슈머 한 곳에서만 나간다.
/// TODO(M2): BackgroundService 컨슈머 루프 + FluentModbus.ModbusTcpClient + RMW 헬퍼(ReadD4→bit set/clear→WriteD4).
/// </summary>
public sealed class PlcWriteQueue
{
    private readonly Channel<PlcWrite> _ch = Channel.CreateUnbounded<PlcWrite>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public ValueTask EnqueueAsync(PlcWrite w, CancellationToken ct = default) => _ch.Writer.WriteAsync(w, ct);
    public IAsyncEnumerable<PlcWrite> ReadAllAsync(CancellationToken ct) => _ch.Reader.ReadAllAsync(ct);
}

/// <summary>
/// 폴링 서비스 골격.
/// TODO(M2): BackgroundService로 전환 — 주기(PollIntervalMs)마다 FC03으로 D0~D6(BlockLength) 일괄 읽기
///   → PlcSnapshot.FromRegisters로 캐시 갱신. 연속 실패 N회/소켓 예외 → Online=false(OFFLINE) 스냅샷 게시.
///   R_Flag 상승 감지 시 핸드셰이크 오케스트레이터에 알림(이벤트/Channel).
/// </summary>
public sealed class PlcPollingService
{
    private volatile PlcSnapshot _latest =
        new(0, 0, 0, 0, false, false, false, 0, 0, Online: false, DateTimeOffset.MinValue);

    public PlcSnapshot Latest => _latest;

    internal void Publish(PlcSnapshot s) => _latest = s; // 폴 루프 전용
}
