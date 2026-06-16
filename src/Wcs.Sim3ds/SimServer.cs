using System.Buffers.Binary;
using System.Net;
using System.Runtime.CompilerServices;
using FluentModbus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wcs.Core;

namespace Wcs.Sim3ds;

/// <summary>
/// 3DS PLC 시뮬레이터 — SPEC §6 정정본 동작.
/// in-process 기동·구성·고장주입·종료 가능한 타입.
/// Program.cs entrypoint는 이 타입을 호출하는 얇은 래퍼.
/// </summary>
public sealed class SimServer : IAsyncDisposable
{
    // ─── 설정 ────────────────────────────────────────────────────────────────
    public sealed record Options
    {
        public string Host            { get; init; } = "127.0.0.1";
        public int    Port            { get; init; } = 1502;
        public int    TiltDelayMs     { get; init; } = 200;   // 낙하 후 적재 대기
        public int    SortDurationMs  { get; init; } = 500;   // 분류 소요
        public int    MoveDurationMs  { get; init; } = 300;   // 이동 소요
        public int    InitialCurFloor { get; init; } = 1;
        public int    SimLoopMs       { get; init; } = 20;    // 상태 루프 주기
    }

    // ─── 고장 주입 ───────────────────────────────────────────────────────────
    /// <summary>R_Seq를 보낸 C_Seq 대신 이 값으로 교체 (불일치 유발).</summary>
    public int? InjectRSeqOverride { get; set; }

    /// <summary>R_Flag 세팅 전 추가 지연 ms (타임아웃 유발).</summary>
    public int InjectRFlagDelayMs { get; set; }

    /// <summary>
    /// true = 시뮬레이터 상태기계가 C_Flag 감지·R_Flag 세팅 등 일체 처리를 중단.
    /// Modbus 폴 응답은 계속되므로 GW는 Online 유지 — 실제 동작: R_Flag 미응답 → RFlagTimeout 유발.
    /// (서버 소켓을 끊는 OFFLINE 주입이 아님 — OFFLINE은 StopAsync()로 별도 유발)
    /// </summary>
    public bool InjectNoResponse
    {
        get => _noResponse;
        set => _noResponse = value;
    }

    // ─── 내부 ────────────────────────────────────────────────────────────────
    private const byte UnitId = 1; // Modbus 유닛 식별자

    private readonly Options               _opt;
    private readonly ILogger<SimServer>    _log;
    private readonly ModbusTcpServer       _server;
    private readonly Action<string>?       _timelineLog;

    // 내부 HR 섀도 배열 — _server 버퍼와 항상 동기화(lock _hrLock)
    private readonly ushort[] _hr = new ushort[RegisterMap.BlockLength];
    private readonly object   _hrLock = new();

    // 분류·이동 상태 (루프 ↔ 비동기 태스크 간 공유)
    private readonly object _stateLock = new();
    private bool _isSorting;
    private bool _isMoving;

    private volatile bool _noResponse;

    private CancellationTokenSource? _cts;
    private Task?                    _simLoop;

    public SimServer(Options opt, ILogger<SimServer>? log = null, Action<string>? timelineLog = null)
    {
        _opt         = opt;
        _log         = log ?? NullLogger<SimServer>.Instance;
        _timelineLog = timelineLog;
        _server      = new ModbusTcpServer(NullLogger<ModbusTcpServer>.Instance);
    }

    // ─── 시작·종료 ──────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken outerCt = default)
    {
        // 유닛 등록 및 초기 레지스터 세팅
        _server.AddUnit(UnitId);

        lock (_hrLock)
        {
            Array.Clear(_hr, 0, _hr.Length);
            _hr[RegisterMap.CurFloor] = (ushort)_opt.InitialCurFloor;
            _hr[RegisterMap.Flags]    = RegisterMap.D4.Ready; // Ready=1(수용 가능)
            FlushToServerLocked();
        }

        var ep = new IPEndPoint(IPAddress.Parse(_opt.Host), _opt.Port);
        _server.Start(ep);
        LogTimeline($"Sim3ds 서버 기동 {_opt.Host}:{_opt.Port}");

        _cts     = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        _simLoop = Task.Run(() => RunSimLoopAsync(_cts.Token));
        // 고정 sleep 제거 — GW PlcPollingService.StartAsync 후 WaitUntilAsync(Online) 폴링이 흡수
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is not null)
            await _cts.CancelAsync().ConfigureAwait(false);

        if (_simLoop is not null)
        {
            try { await _simLoop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        _server.Stop();
        LogTimeline("Sim3ds 서버 종료");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _server.Dispose();
        _cts?.Dispose();
    }

    // ─── 레지스터 직접 읽기 (테스트 검증용) ─────────────────────────────────

    public PlcSnapshot ReadSnapshot()
    {
        ushort[] copy;
        lock (_hrLock) { copy = _hr.ToArray(); }
        return PlcSnapshot.FromRegisters(copy, online: true, at: DateTimeOffset.Now);
    }

    // ─── 시뮬레이터 메인 루프 ────────────────────────────────────────────────

    private async Task RunSimLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_opt.SimLoopMs, ct).ConfigureAwait(false);

                if (_noResponse) continue;

                bool sorting, moving;
                lock (_stateLock) { sorting = _isSorting; moving = _isMoving; }

                ushort flags, tgtFloor, curFloor;
                lock (_hrLock)
                {
                    // WCS가 FluentModbus를 통해 쓴 값을 섀도에 반영
                    PullFromServerLocked();
                    flags    = _hr[RegisterMap.Flags];
                    tgtFloor = _hr[RegisterMap.TgtFloor];
                    curFloor = _hr[RegisterMap.CurFloor];
                }

                bool cFlag = (flags & RegisterMap.D4.C_Flag) != 0;

                // C_Flag=1 감지 — 분류 중·이동 중이 아닐 때만(직렬 보장)
                if (cFlag && !sorting && !moving)
                {
                    int cellNo, cSeq;
                    lock (_hrLock)
                    {
                        cellNo = _hr[RegisterMap.C_CellNo];
                        cSeq   = _hr[RegisterMap.C_Seq];
                        // 읽은 즉시 C 영역·C_Flag 클리어
                        _hr[RegisterMap.C_CellNo] = 0;
                        _hr[RegisterMap.C_Seq]    = 0;
                        _hr[RegisterMap.Flags]    = (ushort)(flags & ~RegisterMap.D4.C_Flag);
                        FlushToServerLocked();
                    }
                    LogTimeline($"C 수신: CellNo={cellNo} C_Seq={cSeq} → C 영역·C_Flag 즉시 클리어");

                    lock (_stateLock) { _isSorting = true; }
                    _ = Task.Run(() => RunSortSequenceAsync(cellNo, cSeq, ct), CancellationToken.None);
                    continue;
                }

                // 분류 중이 아닐 때 이동 처리
                if (!sorting && !moving && tgtFloor != 0 && tgtFloor != curFloor)
                {
                    lock (_stateLock) { _isMoving = true; }
                    _ = Task.Run(() => RunMoveSequenceAsync(tgtFloor, ct), CancellationToken.None);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Sim3ds 루프 예외");
            }
        }
    }

    // ─── 분류 시퀀스 ─────────────────────────────────────────────────────────

    private async Task RunSortSequenceAsync(int cellNo, int cSeq, CancellationToken ct)
    {
        try
        {
            // TiltDelay — 낙하 후 적재 대기
            await Task.Delay(_opt.TiltDelayMs, ct).ConfigureAwait(false);

            // 분류 시작: Ready=0 + TgtFloor=0 클리어
            ushort prevTgt;
            lock (_hrLock)
            {
                prevTgt = _hr[RegisterMap.TgtFloor];
                _hr[RegisterMap.Flags]    = (ushort)(_hr[RegisterMap.Flags] & ~RegisterMap.D4.Ready);
                _hr[RegisterMap.TgtFloor] = 0;
                FlushToServerLocked();
            }
            LogTimeline($"분류 시작: Ready=0, TgtFloor 클리어 (이전={prevTgt})");

            // SortDuration — 분류 소요
            await Task.Delay(_opt.SortDurationMs, ct).ConfigureAwait(false);

            // R_Flag 지연 고장 주입
            if (InjectRFlagDelayMs > 0)
            {
                LogTimeline($"[고장주입] R_Flag 지연 {InjectRFlagDelayMs}ms");
                await Task.Delay(InjectRFlagDelayMs, ct).ConfigureAwait(false);
            }

            // R_Seq 고장 주입
            int actualRSeq = InjectRSeqOverride.HasValue ? InjectRSeqOverride.Value : cSeq;
            if (InjectRSeqOverride.HasValue)
                LogTimeline($"[고장주입] R_Seq 교체: C_Seq={cSeq} → R_Seq={actualRSeq}");

            // R 기입: R_CellNo·R_Seq·R_Flag=1
            lock (_hrLock)
            {
                _hr[RegisterMap.R_CellNo] = (ushort)cellNo;
                _hr[RegisterMap.R_Seq]    = (ushort)actualRSeq;
                _hr[RegisterMap.Flags]    = (ushort)(_hr[RegisterMap.Flags] | RegisterMap.D4.R_Flag);
                FlushToServerLocked();
            }
            LogTimeline($"R 세팅: R_CellNo={cellNo}, R_Seq={actualRSeq}, R_Flag=1");

            // 복귀 이동 여부 확인
            // 분류 시작 시 TgtFloor를 0으로 클리어했지만,
            // 분류 중에 WCS가 선기입했을 수 있으므로 현재값 재확인
            ushort tgt, cur;
            lock (_hrLock)
            {
                PullFromServerLocked(); // WCS가 분류 중에 기입했을 수 있음
                tgt = _hr[RegisterMap.TgtFloor];
                cur = _hr[RegisterMap.CurFloor];
            }

            bool needMove = (tgt != 0 && tgt != cur);
            if (needMove)
            {
                // 복귀 이동 필요 — Ready=0 유지한 채 이동 시작
                LogTimeline($"분류 완료 후 복귀 이동: TgtFloor={tgt} CurFloor={cur} → Ready=0 유지");
                lock (_stateLock) { _isSorting = false; _isMoving = true; }
                await RunMoveBodyAsync(tgt, ct).ConfigureAwait(false);
                lock (_stateLock) { _isMoving = false; }
            }
            else
            {
                // 복귀 없음 — Ready=1 (블립 없이)
                lock (_hrLock)
                {
                    _hr[RegisterMap.Flags] = (ushort)(_hr[RegisterMap.Flags] | RegisterMap.D4.Ready);
                    FlushToServerLocked();
                }
                LogTimeline("분류 완료: Ready=1 (복귀 이동 없음)");
                lock (_stateLock) { _isSorting = false; }
            }
        }
        catch (OperationCanceledException)
        {
            lock (_stateLock) { _isSorting = false; }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Sim3ds 분류 시퀀스 예외");
            lock (_stateLock) { _isSorting = false; }
        }
    }

    // ─── 이동 시퀀스 ─────────────────────────────────────────────────────────

    private async Task RunMoveSequenceAsync(ushort tgtFloor, CancellationToken ct)
    {
        try
        {
            await RunMoveBodyAsync(tgtFloor, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogError(ex, "Sim3ds 이동 시퀀스 예외"); }
        finally
        {
            lock (_stateLock) { _isMoving = false; }
        }
    }

    private async Task RunMoveBodyAsync(ushort tgtFloor, CancellationToken ct)
    {
        // Ready=0(이동 중)
        lock (_hrLock)
        {
            _hr[RegisterMap.Flags] = (ushort)(_hr[RegisterMap.Flags] & ~RegisterMap.D4.Ready);
            FlushToServerLocked();
        }
        LogTimeline($"이동 시작: TgtFloor={tgtFloor}, Ready=0");

        await Task.Delay(_opt.MoveDurationMs, ct).ConfigureAwait(false);

        lock (_hrLock)
        {
            // CurFloor=TgtFloor, TgtFloor 유지(!)
            _hr[RegisterMap.CurFloor] = _hr[RegisterMap.TgtFloor];
            _hr[RegisterMap.Flags]    = (ushort)(_hr[RegisterMap.Flags] | RegisterMap.D4.Ready);
            FlushToServerLocked();
        }
        ushort cur;
        lock (_hrLock) { cur = _hr[RegisterMap.CurFloor]; }
        LogTimeline($"이동 완료: CurFloor={cur}, TgtFloor 유지, Ready=1");
    }

    // ─── HR ↔ FluentModbus 서버 버퍼 동기화 (호출자는 _hrLock 보유) ─────────

    /// <summary>
    /// 내부 _hr 배열 → FluentModbus 서버 버퍼에 기록. _hrLock 보유 상태에서 호출.
    /// FluentModbus 서버는 raw bytes로 저장, Modbus 클라이언트는 빅엔디언으로 전송.
    /// 호스트가 x86(리틀엔디언)이면 서버 버퍼에서 바이트 순서를 뒤집어야 일치함.
    /// </summary>
    private void FlushToServerLocked()
    {
        var span = _server.GetHoldingRegisters(UnitId);
        for (int i = 0; i < RegisterMap.BlockLength; i++)
            // ushort를 빅엔디언 short로 변환하여 서버 버퍼에 기록
            span[i] = (short)BinaryPrimitives.ReverseEndianness(_hr[i]);
    }

    /// <summary>
    /// FluentModbus 서버 버퍼 → 내부 _hr 배열로 가져옴 (WCS 쓰기 반영). _hrLock 보유 상태에서 호출.
    /// 서버 버퍼는 리틀엔디언으로 저장됨 → 빅엔디언 역변환 적용.
    /// </summary>
    private void PullFromServerLocked()
    {
        var span = _server.GetHoldingRegisters(UnitId);
        for (int i = 0; i < RegisterMap.BlockLength; i++)
        {
            ushort prev = _hr[i];
            // 서버 버퍼 리틀엔디언 → ushort로 역변환
            _hr[i] = BinaryPrimitives.ReverseEndianness((ushort)span[i]);
            if (_hr[i] != prev)
                LogTimeline($"WCS 쓰기 수신: D{i} {prev}→{_hr[i]}");
        }
    }

    // ─── 로그 ────────────────────────────────────────────────────────────────

    private void LogTimeline(string msg, [CallerMemberName] string? _ = null)
    {
        var line = $"[{DateTimeOffset.Now:HH:mm:ss.fff}] {msg}";
        _log.LogInformation("{Line}", line);
        _timelineLog?.Invoke(line);
    }
}
