using System.Threading.Channels;
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
    // Plc 섹션 — TCP 호환 유지(회귀 보존)
    public string Host                 { get; init; } = "127.0.0.1";
    public int    Port                 { get; init; } = 1502;
    public int    PollIntervalMs       { get; init; } = 150;
    public int    OfflineAfterFailures { get; init; } = 3;
    public int    WriteTimeoutMs       { get; init; } = 1000;

    // Timing 섹션
    public int RFlagPollMs      { get; init; } = 100;
    public int RFlagTimeoutMs   { get; init; } = 30000;
    public int CFlagTimeoutMs   { get; init; } = 5000;

    // S-HANDSHAKE-RESIDUE — 잔류 대사 ClearR 후 R_Flag==0 확인 대기 상한(ms).
    // 하드코딩 금지(절대규칙 #7). 분류 최대 소요와 무관한 "클리어 반영" 대기이므로
    // RFlagTimeoutMs보다 짧게(현장 폴 주기 몇 배) 잡는다. 초과 시 C 미기입 종결(§2C).
    public int RFlagClearConfirmTimeoutMs { get; init; } = 2000;
}

// ════════════════════════════════════════════════════════════════════════════
// PlcPollingService — PLC 폴링 서비스 (M2 구현, M3에서 IHostedService로 전환 예정)
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// PLC 폴링 서비스 — IHostedService 아님. StartAsync/StopAsync로 수동 관리.
/// (M3 DI에서 AddHostedService로 전환 예정 — 현재는 수동 기동·종료)
/// PollIntervalMs 주기로 D0~D6 일괄 FC03 → PlcSnapshot.FromRegisters 캐시 → Latest 게시.
/// 연속 실패 OfflineAfterFailures회 / 예외(SocketException·IOException·RTU 시리얼 타임아웃) → Online=false(OFFLINE).
/// 복구 시 Online=true.
/// R_Flag 상승(0→1) 감지 시 RFlagRaised 채널에 알림.
/// _clientLock(SemaphoreSlim)으로 폴 읽기·쓰기·Disconnect/재연결을 단일 임계구역에서 직렬화.
/// IModbusMaster에만 의존 — TCP·RTU 전송 무관.
/// </summary>
public sealed class PlcPollingService : IPlcGateway, IAsyncDisposable
{
    private volatile PlcSnapshot _latest =
        new(0, 0, 0, 0, false, false, false, 0, 0, Online: false, DateTimeOffset.MinValue);

    // OFFLINE 전이 알림 이벤트 — Online true→false 시 1회만 발화 (폴마다 반복 금지).
    // API 계층이 구독해 alarm 1행(전이당 1건) 영속화. 게이트웨이 본문 동작 무변경.
    public event Action<PlcSnapshot>? OnOfflineTransition;

    // ── S-OBSERVABILITY 관측 훅 (부수 기록 전용 — 게이트웨이 의미·타이밍 0 변경) ─────
    // EF 비의존 계층이라 DB를 직접 모른다. 콜백만 발화하고 Wcs.Api 측 싱크가 operation_log에 기록한다.
    // 핸들러 예외가 폴/쓰기 루프를 죽이지 않도록 발화부를 try로 감싼다(fail-safe).

    /// <summary>ONLINE 복구 전이(Online false→true) 시 1회 발화. STATE/ONLINE 로그용.</summary>
    public event Action<PlcSnapshot>? OnOnlineTransition;

    /// <summary>폴링 레지스터 전이(변화분) — (reg, old, new). 무변화 폴링은 발화 0(변화분 정책).</summary>
    public event Action<string, int, int>? OnRegisterChange;

    /// <summary>PLC 쓰기 완료 — (action, detailJson). SetTgtFloor·CellAssign·ClearR·RMW_D4 전수.</summary>
    public event Action<string, string>? OnWrite;

    // OFFLINE 전이 원자 플래그 — 1=ONLINE(초기), 0=OFFLINE.
    // Interlocked.Exchange로 동시 호출(폴 루프 + 쓰기 컨슈머)에서 정확히 1회 발화 보장.
    private int _online = 1;

    private readonly PlcGatewayOptions _opt;
    private readonly ILogger<PlcPollingService> _log;
    private readonly PlcWriteQueue _writeQueue;

    // 전송 추상화 — TCP·RTU 어댑터 모두 허용 (S-RTU Scope A)
    private readonly IModbusMaster _master;

    // 소켓/직렬 직렬화 세마포 — 폴 읽기 / 쓰기 / RMW 가 같은 _master를 동시에 사용하지 않도록 보호.
    // FluentModbus 클라이언트는 단일 트랜잭션 버퍼라 thread-safe 아님.
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

    // ── 생성자 (IModbusMaster 주입) ──────────────────────────────────────────

    /// <summary>
    /// IModbusMaster 직접 주입 — TCP·RTU 어댑터 모두 허용.
    /// 통합 테스트에서 fake IModbusMaster를 주입할 때도 이 생성자 사용.
    /// </summary>
    public PlcPollingService(
        PlcGatewayOptions opt,
        PlcWriteQueue     writeQueue,
        IModbusMaster     master,
        ILogger<PlcPollingService>? log = null)
    {
        _opt        = opt;
        _writeQueue = writeQueue;
        _master     = master;
        _log        = log ?? NullLogger<PlcPollingService>.Instance;
    }

    /// <summary>
    /// TCP 어댑터를 내부 생성하는 편의 생성자 — M2 통합 테스트 회귀 보존(TCP 명시).
    /// opt.Host·opt.Port·opt.WriteTimeoutMs로 ModbusTcpMaster 생성.
    /// </summary>
    public PlcPollingService(
        PlcGatewayOptions opt,
        PlcWriteQueue     writeQueue,
        ILogger<PlcPollingService>? log = null)
        : this(opt, writeQueue, new ModbusTcpMaster(opt.Host, opt.Port, opt.WriteTimeoutMs), log)
    {
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

    private int _stopped;  // 멱등 StopAsync 플래그 (Interlocked)

    public async Task StopAsync()
    {
        // 이중 호출(StopAsync 후 DisposeAsync 내부 StopAsync 재진입) 방어 — Interlocked로 1회만 실행
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;

        if (_cts is not null)
            await _cts.CancelAsync().ConfigureAwait(false);

        foreach (var t in new[] { _pollTask, _writeTask }.Where(t => t is not null))
        {
            try { await t!.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception) { }  // 폴 루프 내 로거 disposed 등 teardown 경쟁 예외 흡수
        }
        try { _master.Disconnect(); } catch { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _master.Dispose();
        _cts?.Dispose();
        _clientLock.Dispose();
    }

    // ── 폴링 루프 ────────────────────────────────────────────────────────────

    private async Task RunPollLoopAsync(CancellationToken ct)
    {
        int failures = 0;
        bool prevRFlag = false;
        // S-OBSERVABILITY: 전체 레지스터 전이 감지용 직전 스냅샷(변화분 정책 — 무변화는 발화 0).
        // null = 첫 폴(직전값 없음 → 전이 기록 안 함, baseline만 설정).
        PlcSnapshot? prevSnap = null;
        // S-HANDSHAKE-RESIDUE §2B: 기동 잔류 R_Flag reconciliation 1회 게이트(첫 유효 폴 기준).
        bool startupReconciled = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_opt.PollIntervalMs, ct).ConfigureAwait(false);

                // _clientLock으로 폴 읽기 직렬화 — 쓰기 컨슈머와 소켓/시리얼 충돌 방지
                await _clientLock.WaitAsync(ct).ConfigureAwait(false);
                PlcSnapshot snap;
                try
                {
                    EnsureConnected();

                    // D0~D6 일괄 FC03 → ushort 변환
                    var raw = await _master.ReadHoldingRegistersAsync(
                        0, RegisterMap.BlockLength, ct).ConfigureAwait(false);
                    snap = PlcSnapshot.FromRegisters(raw, online: true, at: DateTimeOffset.Now);
                }
                finally
                {
                    _clientLock.Release();
                }

                var prevOnline = _latest.Online;
                _latest  = snap;
                failures = 0;
                // ONLINE 복구 시 _online 플래그 리셋 — 다음 OFFLINE 전이에서 이벤트 재발화 허용
                Interlocked.Exchange(ref _online, 1);

                // ONLINE 복구 전이(false→true) — 부수 기록(STATE/ONLINE). 핸들러 예외 격리.
                if (!prevOnline)
                {
                    try { OnOnlineTransition?.Invoke(snap); } catch { }
                }

                // 레지스터 전이(변화분만) — 직전 스냅샷과 다른 값만 1건씩 발화. 핸들러 예외 격리(fail-safe).
                if (prevSnap is { } p)
                    EmitRegisterChanges(p, snap);
                prevSnap = snap;

                // ── S-HANDSHAKE-RESIDUE §2B: 기동 잔류 R_Flag reconciliation (첫 유효 폴 1회) ──
                // PLC 기동 직후 R 영역 잔류(실측: R_CellNo=20, R_Seq=123)를 새 핸드셰이크가
                // 소비하기 전에 차단한다. 근거: 기동 잔류를 지우면 그 응답을 기다리는 대기자는 없고
                // C_Seq 카운터도 리셋 상태이므로, 잔류를 유지하면 후속 전(全) 건이 "직전 응답"을
                // 오소비하는 off-by-one 연쇄를 낳는다 → 클리어가 정당한 복구(§A3).
                // 쓰기는 반드시 단일 큐 경유(절대규칙 #1) — 폴 루프가 직접 Modbus 호출 금지.
                // 관측: WARN 로그(잔류값 포함) + ClearR의 OnWrite(PLC_WRITE) + 이후 폴의
                //       OnRegisterChange(R_CellNo 20→0·R_Seq 123→0·R_Flag 1→0)로 잔류값이 기록됨.
                if (!startupReconciled)
                {
                    startupReconciled = true; // 첫 유효(Online) 폴에서만 1회 판정(§A3)
                    if (snap.RFlag)
                    {
                        try
                        {
                            _log.LogWarning(
                                "[폴링] 기동 첫 폴 R_Flag=1 잔류 감지 — ClearR 대사(단일 큐 경유): " +
                                "R_CellNo={RCellNo} R_Seq={RSeq}", snap.RCellNo, snap.RSeq);
                        }
                        catch { /* 로거 disposed — 무시 */ }

                        // 큐 경유 ClearR(컨슈머가 RMW로 R_Flag clear + R 영역 0). TryWrite = 논블로킹.
                        _writeQueue.Writer.TryWrite(new PlcWrite.ClearR());
                    }
                }

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
                // 호스트 종료 시 CTS 취소 → SocketException 경로로 여기 진입 가능.
                // 이 시점에서 _log(EventLogInternal 등)가 이미 disposed일 수 있으므로
                // 모든 로깅 호출을 try로 보호해 폴 루프 예외 전파 방지.
                failures++;
                try { _log.LogWarning(ex, "[폴링] 실패 {Cnt}/{Max}", failures, _opt.OfflineAfterFailures); }
                catch { /* 로거 disposed — 무시 */ }

                // 예외 종류 불문(SocketException·IOException·시리얼 타임아웃 모두) OFFLINE 판단.
                // TCP: SocketException·IOException / RTU: IOException·TimeoutException
                bool isHardEx = ex is System.Net.Sockets.SocketException
                             || ex is System.IO.IOException
                             || ex is TimeoutException
                             || ex.InnerException is System.Net.Sockets.SocketException
                             || ex.InnerException is System.IO.IOException;

                if (failures >= _opt.OfflineAfterFailures || isHardEx)
                {
                    try { _log.LogError("[폴링] OFFLINE 전이 (연속 실패 {Cnt}회, HardEx={HardEx})", failures, isHardEx); }
                    catch { /* 로거 disposed — 무시 */ }
                    PublishOffline();

                    // Disconnect는 _clientLock 임계구역 안에서 실행 — 쓰기 컨슈머가 진행 중인
                    // 트랜잭션이 완료된 뒤에 소켓/포트를 끊어 프레임/버퍼 손상 방지.
                    await _clientLock.WaitAsync(ct).ConfigureAwait(false);
                    try { TryReconnect(); }
                    finally { _clientLock.Release(); }
                }
            }
        }
    }

    private void EnsureConnected()
    {
        if (!_master.IsConnected)
            _master.Connect();
    }

    private void TryReconnect()
    {
        try { _master.Disconnect(); } catch { }
    }

    private void PublishOffline()
    {
        var prev = _latest;
        var offlineSnap = new PlcSnapshot(
            prev.CCellNo, prev.CSeq, prev.RCellNo, prev.RSeq,
            prev.CFlag, prev.RFlag, prev.Ready,
            prev.CurFloor, prev.TgtFloor,
            Online: false, At: DateTimeOffset.Now);
        _latest = offlineSnap;

        // 전이당 1회만 이벤트 발화 — Interlocked CAS로 동시 호출(폴 루프 + 쓰기 컨슈머) 경쟁 원자화.
        // _online 1→0 교환에 성공한 호출자만 이벤트를 발화해 alarm 중복 방지.
        if (Interlocked.Exchange(ref _online, 0) == 1)
            OnOfflineTransition?.Invoke(offlineSnap);
    }

    // ── S-OBSERVABILITY: 레지스터 전이(변화분) 발화 ─────────────────────────────
    // 직전 스냅샷과 다른 레지스터만 (reg, old, new) 1건씩 발화. 무변화면 발화 0(변화분 정책).
    // OnRegisterChange 핸들러 예외가 폴 루프를 죽이지 않도록 전체를 try로 감싼다(fail-safe).
    private void EmitRegisterChanges(PlcSnapshot prev, PlcSnapshot cur)
    {
        var h = OnRegisterChange;
        if (h is null) return;

        try
        {
            // D0~D6 + D4 비트(C_Flag·R_Flag·Ready). int(bool→0/1)로 old→new 표현.
            if (prev.CCellNo  != cur.CCellNo)  h("C_CellNo", prev.CCellNo,  cur.CCellNo);
            if (prev.CSeq     != cur.CSeq)     h("C_Seq",    prev.CSeq,     cur.CSeq);
            if (prev.RCellNo  != cur.RCellNo)  h("R_CellNo", prev.RCellNo,  cur.RCellNo);
            if (prev.RSeq     != cur.RSeq)     h("R_Seq",    prev.RSeq,     cur.RSeq);
            if (prev.CFlag    != cur.CFlag)    h("C_Flag",   prev.CFlag ? 1 : 0, cur.CFlag ? 1 : 0);
            if (prev.RFlag    != cur.RFlag)    h("R_Flag",   prev.RFlag ? 1 : 0, cur.RFlag ? 1 : 0);
            if (prev.Ready    != cur.Ready)    h("Ready",    prev.Ready ? 1 : 0, cur.Ready ? 1 : 0);
            if (prev.CurFloor != cur.CurFloor) h("CurFloor", prev.CurFloor, cur.CurFloor);
            if (prev.TgtFloor != cur.TgtFloor) h("TgtFloor", prev.TgtFloor, cur.TgtFloor);
        }
        catch { /* 관측 훅 예외 격리 — 폴 루프 보존(fail-safe) */ }
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
        // _clientLock으로 모든 Modbus 트랜잭션 직렬화 — 폴 루프와 소켓/시리얼 충돌 방지.
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
                    await _master.WriteSingleRegisterAsync(
                        RegisterMap.TgtFloor, (short)floor, ct).ConfigureAwait(false);
                    _log.LogInformation("[쓰기 큐] SetTgtFloor → D6={Floor}", floor);
                    EmitWrite("SET_TGTFLOOR", $"{{\"reg\":\"D6\",\"floor\":{floor}}}");
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
                    await _master.WriteMultipleRegistersAsync(
                        RegisterMap.C_CellNo,
                        new short[] { (short)cellNo, (short)seq },
                        ct).ConfigureAwait(false);
                    // D4 RMW — C_Flag set (read+write를 동일 임계구역에서 수행)
                    await RmwD4LockedAsync(set: RegisterMap.D4.C_Flag, clear: 0, ct).ConfigureAwait(false);
                    _log.LogInformation("[쓰기 큐] CellAssign → D0={CellNo}, D1={Seq}, C_Flag=1", cellNo, seq);
                    EmitWrite("CELL_ASSIGN", $"{{\"cellNo\":{cellNo},\"cSeq\":{seq},\"cFlag\":1}}");
                    break;

                case PlcWrite.ClearR:
                    // R_CellNo·R_Seq=0 + D4 RMW R_Flag clear
                    await _master.WriteMultipleRegistersAsync(
                        RegisterMap.R_CellNo,
                        new short[] { 0, 0 },
                        ct).ConfigureAwait(false);
                    // D4 RMW — R_Flag clear (read+write를 동일 임계구역에서 수행)
                    await RmwD4LockedAsync(set: 0, clear: RegisterMap.D4.R_Flag, ct).ConfigureAwait(false);
                    _log.LogInformation("[쓰기 큐] ClearR → D2·D3=0, R_Flag=0");
                    EmitWrite("CLEAR_R", "{\"reg\":\"D2,D3\",\"rFlag\":0}");
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
        var raw = await _master.ReadHoldingRegistersAsync(
            RegisterMap.Flags, 1, ct).ConfigureAwait(false);

        ushort current  = raw[0];
        ushort modified = (ushort)((current | set) & ~clear);

        // WriteD4 — read와 동일 임계구역
        await _master.WriteSingleRegisterAsync(
            RegisterMap.Flags, (short)modified, ct).ConfigureAwait(false);

        _log.LogDebug("[RMW D4] {Before:X4} → set={Set:X4} clear={Clear:X4} → {After:X4}",
            current, set, clear, modified);
        // D4 RMW before→after 전수 기록(부수 — 절대규칙 #1 단일 큐 경로 안에서의 부수 기록).
        EmitWrite("RMW_D4",
            $"{{\"reg\":\"D4\",\"before\":{current},\"set\":{set},\"clear\":{clear},\"after\":{modified}}}");
    }

    // ── S-OBSERVABILITY: PLC 쓰기 완료 발화(부수 — 핸들러 예외 격리) ────────────
    private void EmitWrite(string action, string detailJson)
    {
        try { OnWrite?.Invoke(action, detailJson); }
        catch { /* 관측 훅 예외 격리 — 쓰기 컨슈머 보존(fail-safe) */ }
    }
}
