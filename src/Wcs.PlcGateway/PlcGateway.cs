using System.Threading.Channels;
using FluentModbus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wcs.Core;

namespace Wcs.PlcGateway;

// ════════════════════════════════════════════════════════════════════════════
// IPlcGateway — API 계층이 보는 표면
// ════════════════════════════════════════════════════════════════════════════

/// <summary>API 계층이 보는 게이트웨이 — 읽기는 스냅샷 캐시, 쓰기는 큐 투입뿐.</summary>
public interface IPlcGateway
{
    /// <summary>마지막 폴링 스냅샷(논블로킹). Online=false면 OFFLINE.</summary>
    PlcSnapshot Latest { get; }

    /// <summary>쓰기 요청을 단일 큐에 투입(완료를 기다리지 않음 — API 3s 한계와 분리).</summary>
    ValueTask EnqueueAsync(PlcWrite write, CancellationToken ct = default);
}

// ════════════════════════════════════════════════════════════════════════════
// PlcWrite — 쓰기 작업 디스크리미네이티드 유니온
// ════════════════════════════════════════════════════════════════════════════

/// <summary>PLC 쓰기 작업. D4 비트 조작은 컨슈머가 RMW로 수행한다.</summary>
public abstract record PlcWrite
{
    /// <summary>TgtFloor(D6) 기입 — 컨슈머가 쓰기 직전 TgtFloor==0 재확인(핑퐁 차단) 후 FC06.</summary>
    public sealed record SetTgtFloor(int Floor) : PlcWrite;

    /// <summary>C 영역 셀 지정: C_Flag==0 확인 → C_CellNo·C_Seq FC16 → D4 RMW로 C_Flag set.</summary>
    public sealed record CellAssign(int CellNo, int Seq) : PlcWrite;

    /// <summary>R 영역 처리 완료: R_CellNo·R_Seq=0 + D4 RMW로 R_Flag clear.</summary>
    public sealed record ClearR : PlcWrite;
}

// ════════════════════════════════════════════════════════════════════════════
// PlcWriteQueue — 단일 쓰기 큐 (절대 규칙 #1)
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 단일 컨슈머 쓰기 큐 — 절대 규칙 #1.
/// 모든 Modbus 쓰기는 이 큐의 컨슈머 한 곳(PlcWriteConsumer)에서만 나간다.
/// </summary>
public sealed class PlcWriteQueue
{
    private readonly Channel<PlcWrite> _ch = Channel.CreateUnbounded<PlcWrite>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    public ChannelWriter<PlcWrite> Writer => _ch.Writer;
    public ChannelReader<PlcWrite> Reader => _ch.Reader;

    public ValueTask EnqueueAsync(PlcWrite w, CancellationToken ct = default) =>
        _ch.Writer.WriteAsync(w, ct);

    public IAsyncEnumerable<PlcWrite> ReadAllAsync(CancellationToken ct) =>
        _ch.Reader.ReadAllAsync(ct);
}

// ════════════════════════════════════════════════════════════════════════════
// PlcGatewayOptions — 설정값 바인딩
// ════════════════════════════════════════════════════════════════════════════

/// <summary>appsettings.json Plc/Timing 섹션 설정값.</summary>
public sealed record PlcGatewayOptions
{
    // Plc 섹션
    public string Host                 { get; init; } = "127.0.0.1";
    public int    Port                 { get; init; } = 1502;
    public int    PollIntervalMs       { get; init; } = 150;
    public int    OfflineAfterFailures { get; init; } = 3;
    public int    WriteTimeoutMs       { get; init; } = 1000;

    // Timing 섹션
    public int RFlagPollMs      { get; init; } = 100;
    public int RFlagTimeoutMs   { get; init; } = 30000;
    public int CFlagTimeoutMs   { get; init; } = 5000;
}

// ════════════════════════════════════════════════════════════════════════════
// PlcPollingService — PLC 폴링 서비스 (M2 구현, M3에서 IHostedService로 전환 예정)
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// PLC 폴링 서비스 — IHostedService 아님. StartAsync/StopAsync로 수동 관리.
/// (M3 DI에서 AddHostedService로 전환 예정 — 현재는 수동 기동·종료)
/// PollIntervalMs 주기로 D0~D6 일괄 FC03 → PlcSnapshot.FromRegisters 캐시 → Latest 게시.
/// 연속 실패 OfflineAfterFailures회 / 소켓 예외 → Online=false(OFFLINE).
/// 복구 시 Online=true.
/// R_Flag 상승(0→1) 감지 시 RFlagRaised 채널에 알림.
/// _clientLock(SemaphoreSlim)으로 폴 읽기·쓰기·Disconnect/재연결을 단일 임계구역에서 직렬화.
/// </summary>
public sealed class PlcPollingService : IPlcGateway, IAsyncDisposable
{
    private volatile PlcSnapshot _latest =
        new(0, 0, 0, 0, false, false, false, 0, 0, Online: false, DateTimeOffset.MinValue);

    private readonly PlcGatewayOptions _opt;
    private readonly ILogger<PlcPollingService> _log;
    private readonly PlcWriteQueue _writeQueue;
    private readonly ModbusTcpClient _client;

    // 소켓 직렬화 세마포 — 폴 읽기 / 쓰기 / RMW 가 같은 _client를 동시에 사용하지 않도록 보호.
    // FluentModbus ModbusTcpClient는 단일 소켓·단일 트랜잭션 버퍼라 thread-safe 아님.
    // RMW의 read+write는 반드시 하나의 임계구역(lock 획득 상태)으로 수행.
    private readonly SemaphoreSlim _clientLock = new(1, 1);

    // R_Flag 상승 통지 채널 — 핸드셰이크 오케스트레이터가 구독
    private readonly Channel<PlcSnapshot> _rFlagChannel =
        Channel.CreateBounded<PlcSnapshot>(new BoundedChannelOptions(4)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

    public ChannelReader<PlcSnapshot> RFlagRaised => _rFlagChannel.Reader;

    private CancellationTokenSource? _cts;
    private Task? _pollTask;
    private Task? _writeTask;

    public PlcPollingService(PlcGatewayOptions opt, PlcWriteQueue writeQueue, ILogger<PlcPollingService>? log = null)
    {
        _opt        = opt;
        _writeQueue = writeQueue;
        _log        = log ?? NullLogger<PlcPollingService>.Instance;
        _client     = new ModbusTcpClient
        {
            // 읽기 타임아웃: WriteTimeoutMs 기준 (서버 무응답 시 예외 발생)
            ReadTimeout = opt.WriteTimeoutMs
        };
    }

    public PlcSnapshot Latest => _latest;

    public ValueTask EnqueueAsync(PlcWrite write, CancellationToken ct = default) =>
        _writeQueue.EnqueueAsync(write, ct);

    // ── 시작·종료 ────────────────────────────────────────────────────────────

    public async Task StartAsync(CancellationToken outerCt = default)
    {
        _cts      = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        _pollTask  = Task.Run(() => RunPollLoopAsync(_cts.Token));
        _writeTask = Task.Run(() => RunWriteConsumerAsync(_cts.Token));
        await Task.Yield();
    }

    public async Task StopAsync()
    {
        if (_cts is not null)
            await _cts.CancelAsync().ConfigureAwait(false);

        foreach (var t in new[] { _pollTask, _writeTask }.Where(t => t is not null))
        {
            try { await t!.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        try { _client.Disconnect(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _client.Dispose();
        _cts?.Dispose();
        _clientLock.Dispose();
    }

    // ── 폴링 루프 ────────────────────────────────────────────────────────────

    private async Task RunPollLoopAsync(CancellationToken ct)
    {
        int failures = 0;
        bool prevRFlag = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_opt.PollIntervalMs, ct).ConfigureAwait(false);

                // _clientLock으로 폴 읽기 직렬화 — 쓰기 컨슈머와 소켓 충돌 방지
                await _clientLock.WaitAsync(ct).ConfigureAwait(false);
                PlcSnapshot snap;
                try
                {
                    EnsureConnected();

                    // D0~D6 일괄 FC03 → ushort 변환
                    var raw = await ReadHoldingRegistersUInt16Async(ct).ConfigureAwait(false);
                    snap = PlcSnapshot.FromRegisters(raw, online: true, at: DateTimeOffset.Now);
                }
                finally
                {
                    _clientLock.Release();
                }

                _latest  = snap;
                failures = 0;

                // R_Flag 상승 (0→1) 감지
                if (!prevRFlag && snap.RFlag)
                {
                    _log.LogInformation("[폴링] R_Flag 상승 감지 — 핸드셰이크 오케스트레이터에 알림");
                    _rFlagChannel.Writer.TryWrite(snap);
                }
                prevRFlag = snap.RFlag;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                failures++;
                _log.LogWarning(ex, "[폴링] 실패 {Cnt}/{Max}", failures, _opt.OfflineAfterFailures);

                // 소켓 예외이면 즉시 OFFLINE 전이(연속 실패 카운트 무관)
                bool isSocketEx = ex is System.Net.Sockets.SocketException
                               || ex.InnerException is System.Net.Sockets.SocketException
                               || ex is System.IO.IOException;

                if (failures >= _opt.OfflineAfterFailures || isSocketEx)
                {
                    _log.LogError("[폴링] OFFLINE 전이 (연속 실패 {Cnt}회, SocketEx={SocketEx})",
                        failures, isSocketEx);
                    PublishOffline();

                    // Disconnect는 _clientLock 임계구역 안에서 실행 — 쓰기 컨슈머가 진행 중인
                    // 트랜잭션이 완료된 뒤에 소켓을 끊어 프레임/버퍼 손상 방지.
                    await _clientLock.WaitAsync(ct).ConfigureAwait(false);
                    try { TryReconnect(); }
                    finally { _clientLock.Release(); }
                }
            }
        }
    }

    private async Task<ushort[]> ReadHoldingRegistersUInt16Async(CancellationToken ct)
    {
        var mem = await _client.ReadHoldingRegistersAsync<short>(
            1, 0, RegisterMap.BlockLength, ct).ConfigureAwait(false);

        var result = new ushort[RegisterMap.BlockLength];
        var span   = mem.Span;
        for (int i = 0; i < RegisterMap.BlockLength; i++)
            result[i] = (ushort)span[i];
        return result;
    }

    private void EnsureConnected()
    {
        if (!_client.IsConnected)
        {
            _client.Connect(
                new System.Net.IPEndPoint(System.Net.IPAddress.Parse(_opt.Host), _opt.Port),
                ModbusEndianness.BigEndian);
        }
    }

    private void TryReconnect()
    {
        try { _client.Disconnect(); } catch { }
    }

    private void PublishOffline()
    {
        var prev = _latest;
        _latest = new PlcSnapshot(
            prev.CCellNo, prev.CSeq, prev.RCellNo, prev.RSeq,
            prev.CFlag, prev.RFlag, prev.Ready,
            prev.CurFloor, prev.TgtFloor,
            Online: false, At: DateTimeOffset.Now);
    }

    // ── 단일 쓰기 큐 컨슈머 (절대 규칙 #1) ──────────────────────────────────

    private async Task RunWriteConsumerAsync(CancellationToken ct)
    {
        await foreach (var write in _writeQueue.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await ProcessWriteAsync(write, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "[쓰기 큐] 처리 예외: {Write}", write);
                PublishOffline();
            }
        }
    }

    private async Task ProcessWriteAsync(PlcWrite write, CancellationToken ct)
    {
        // _clientLock으로 모든 Modbus 트랜잭션 직렬화 — 폴 루프와 소켓 충돌 방지.
        // RMW의 read+write도 이 임계구역 안에서 원자적으로 수행.
        await _clientLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            EnsureConnected();

            switch (write)
            {
                case PlcWrite.SetTgtFloor(var floor):
                    // 쓰기 직전 TgtFloor==0 재확인 — 핑퐁 차단(절대 규칙 #2)
                    var snapTgt = _latest;
                    if (snapTgt.TgtFloor != 0)
                    {
                        _log.LogWarning("[쓰기 큐] SetTgtFloor 스킵 — TgtFloor={V}(≠0, 핑퐁 차단)", snapTgt.TgtFloor);
                        return;
                    }
                    await _client.WriteSingleRegisterAsync(
                        1, RegisterMap.TgtFloor, (short)floor, ct).ConfigureAwait(false);
                    _log.LogInformation("[쓰기 큐] SetTgtFloor → D6={Floor}", floor);
                    break;

                case PlcWrite.CellAssign(var cellNo, var seq):
                    // C_Flag==0 확인
                    var snapC = _latest;
                    if (snapC.CFlag)
                    {
                        _log.LogWarning("[쓰기 큐] CellAssign 스킵 — C_Flag=1(이미 세팅됨)");
                        return;
                    }
                    // C_CellNo·C_Seq FC16(멀티 레지스터 쓰기, D0~D1)
                    await _client.WriteMultipleRegistersAsync<short>(
                        1, RegisterMap.C_CellNo,
                        new short[] { (short)cellNo, (short)seq },
                        ct).ConfigureAwait(false);
                    // D4 RMW — C_Flag set (read+write를 동일 임계구역에서 수행)
                    await RmwD4LockedAsync(set: RegisterMap.D4.C_Flag, clear: 0, ct).ConfigureAwait(false);
                    _log.LogInformation("[쓰기 큐] CellAssign → D0={CellNo}, D1={Seq}, C_Flag=1", cellNo, seq);
                    break;

                case PlcWrite.ClearR:
                    // R_CellNo·R_Seq=0 + D4 RMW R_Flag clear
                    await _client.WriteMultipleRegistersAsync<short>(
                        1, RegisterMap.R_CellNo,
                        new short[] { 0, 0 },
                        ct).ConfigureAwait(false);
                    // D4 RMW — R_Flag clear (read+write를 동일 임계구역에서 수행)
                    await RmwD4LockedAsync(set: 0, clear: RegisterMap.D4.R_Flag, ct).ConfigureAwait(false);
                    _log.LogInformation("[쓰기 큐] ClearR → D2·D3=0, R_Flag=0");
                    break;
            }
        }
        finally
        {
            _clientLock.Release();
        }
    }

    // ── D4 RMW 헬퍼 ─────────────────────────────────────────────────────────

    /// <summary>
    /// D4(Flags 워드) Read-Modify-Write.
    /// set 비트만 set, clear 비트만 clear, 나머지 보존.
    /// 반드시 _clientLock 보유 상태(ProcessWriteAsync 임계구역 내)에서 호출.
    /// read+write가 하나의 임계구역 안에 있어 폴 루프 읽기와 프레임 교차 없음.
    /// </summary>
    private async Task RmwD4LockedAsync(ushort set, ushort clear, CancellationToken ct)
    {
        // ReadD4 — 이미 _clientLock 보유 중
        var mem = await _client.ReadHoldingRegistersAsync<short>(
            1, RegisterMap.Flags, 1, ct).ConfigureAwait(false);

        ushort current = (ushort)mem.Span[0];

        // 비트 수정 — 상대 비트 보존
        ushort modified = (ushort)((current | set) & ~clear);

        // WriteD4 — read와 동일 임계구역
        await _client.WriteSingleRegisterAsync(
            1, RegisterMap.Flags, (short)modified, ct).ConfigureAwait(false);

        _log.LogDebug("[RMW D4] {Before:X4} → set={Set:X4} clear={Clear:X4} → {After:X4}",
            current, set, clear, modified);
    }

}
