using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using FluentModbus;
using Microsoft.Extensions.Logging;
using Wcs.Core;

namespace Wcs.Sim3ds;

// ════════════════════════════════════════════════════════════════════════════
// SimSlave — 한 unitId(슬레이브)의 레지스터 뱅크 + 상태기계 + 고장주입 (S-MULTISORTER-SHARED-BUS 모듈 A)
//
//   한 물리 버스(TCP 엔드포인트 / RTU 포트)에서 여러 unitId 슬레이브를 흉내내기 위해,
//   SimServer가 갖던 "한 유닛"의 상태(섀도 _hr·상태기계·고장주입·잔류/Ready 세팅)를 이 타입으로 분리한다.
//   각 SimSlave는 공유 ModbusServer 버퍼의 자기 unitId 뱅크(GetHoldingRegisters(unitId))만 만진다.
//   RegistersChanged 이벤트는 e.UnitIdentifier로 자기 유닛만 처리한다.
//
//   단일 유닛 SimServer는 SimSlave 1개를 소유하고 위임 — 동작·바이트·로그 라인 전부 현행 보존
//   (로그 prefix는 단일 유닛이면 빈 문자열 → 기존 타임라인 문자열 매칭 테스트 무영향).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 3DS 소터 한 대(unitId 하나)의 시뮬레이션 단위. 독립 레지스터 뱅크·상태기계·고장주입을 가진다.
/// SimServer가 소유하며, 멀티유닛 구성에서 <see cref="SimServer.Unit(byte)"/>로 슬레이브별 접근한다.
/// </summary>
public sealed class SimSlave
{
    // ─── 식별·설정 ──────────────────────────────────────────────────────────
    public byte UnitId { get; }

    private readonly SimServer.Options _opt;
    private readonly ILogger           _log;
    private readonly Action<string>?   _timelineLog;
    private readonly string            _logPrefix;   // 단일 유닛="" (현행 로그 보존) / 멀티="[u{n}] "

    // ─── 고장 주입 (테스트 스레드 write, Sim 루프/시퀀스 스레드 read — volatile 가시성) ────

    private volatile bool _hasRSeqOverride;
    private volatile int  _rSeqOverride;
    /// <summary>R_Seq를 보낸 C_Seq 대신 이 값으로 교체 (불일치 유발).</summary>
    public int? InjectRSeqOverride
    {
        get => _hasRSeqOverride ? _rSeqOverride : null;
        set { _rSeqOverride = value ?? 0; _hasRSeqOverride = value.HasValue; }
    }

    private volatile int _rFlagDelayMs;
    /// <summary>R_Flag 세팅 전 추가 지연 ms (타임아웃 유발).</summary>
    public int InjectRFlagDelayMs { get => _rFlagDelayMs; set => _rFlagDelayMs = value; }

    private volatile bool _noResponse;
    /// <summary>
    /// true = 이 슬레이브의 상태기계가 C_Flag 감지·R_Flag 세팅 등 일체 처리를 중단.
    /// Modbus 폴 응답은 계속되므로 GW는 이 슬레이브 Online 유지 — R_Flag 미응답 → RFlagTimeout 유발.
    /// (서버 소켓/응답을 끊는 OFFLINE 주입이 아님 — 슬레이브 OFFLINE은 <see cref="InjectUnresponsive"/>.)
    /// </summary>
    public bool InjectNoResponse { get => _noResponse; set => _noResponse = value; }

    private volatile bool _stickyRResidue;
    /// <summary>
    /// true = WCS ClearR로 R_Flag가 0이 되면 이 슬레이브가 즉시 R_Flag=1을 재천명(PLC 무ack·미반영 모사).
    /// </summary>
    public bool InjectStickyRResidue { get => _stickyRResidue; set => _stickyRResidue = value; }

    private volatile bool _unresponsive;
    /// <summary>
    /// true = 이 슬레이브 unitId에 대한 모든 Modbus 요청을 서버가 예외 응답(ServerDeviceFailure)으로 거부한다
    /// (SimServer의 RequestValidator가 슬레이브별로 참조). 다른 슬레이브는 정상 응답 → 버스 상의 슬레이브별
    /// OFFLINE 독립(B5) 검증용. GW는 이 유닛 폴 실패 누적 후 이 유닛만 OFFLINE 전이한다.
    /// </summary>
    public bool InjectUnresponsive { get => _unresponsive; set => _unresponsive = value; }

    // ─── 내부 상태 ──────────────────────────────────────────────────────────
    // 섀도 HR — 서버 버퍼(자기 unitId 뱅크)와 항상 동기화(lock _hrLock).
    private readonly ushort[] _hr = new ushort[RegisterMap.BlockLength];
    private readonly object   _hrLock = new();

    private readonly object _stateLock = new();
    private bool _isSorting;
    private bool _isMoving;

    // 서버 버퍼는 Bind()에서 주입(StartAsync 시점). 그 전엔 null → RequireServer가 fail-loud.
    private ModbusServer? _server;

    public SimSlave(byte unitId, SimServer.Options opt, ILogger log,
                    Action<string>? timelineLog, string logPrefix = "")
    {
        UnitId       = unitId;
        _opt         = opt;
        _log         = log;
        _timelineLog = timelineLog;
        _logPrefix   = logPrefix;
    }

    // ─── 서버 바인딩(StartAsync에서 호출) ──────────────────────────────────

    /// <summary>
    /// 공유 ModbusServer에 이 슬레이브를 결선한다: RegistersChanged 구독 + 초기 레지스터 세팅
    /// (CurFloor·Ready=1·선택적 R 잔류 프리셋). 단일 유닛 SimServer의 기존 StartAsync 초기화와 동일.
    /// </summary>
    internal void Bind(ModbusServer server)
    {
        _server = server;
        // sticky 고장 충실도 — WCS 쓰기 즉시(쓰기 처리 스레드·동기)에 R_Flag 복원(S-S5-FLAKE 보존).
        server.RegistersChanged += OnServerRegistersChanged;

        lock (_hrLock)
        {
            Array.Clear(_hr, 0, _hr.Length);
            _hr[RegisterMap.CurFloor] = (ushort)_opt.InitialCurFloor;
            _hr[RegisterMap.Flags]    = RegisterMap.D4.Ready; // Ready=1(수용 가능)

            if (_opt.InitialRFlag || _opt.InitialRCellNo != 0 || _opt.InitialRSeq != 0)
            {
                _hr[RegisterMap.R_CellNo] = (ushort)_opt.InitialRCellNo;
                _hr[RegisterMap.R_Seq]    = (ushort)_opt.InitialRSeq;
                if (_opt.InitialRFlag)
                    _hr[RegisterMap.Flags] |= RegisterMap.D4.R_Flag;
            }

            FlushToServerLocked();
        }
        if (_opt.InitialRFlag || _opt.InitialRCellNo != 0 || _opt.InitialRSeq != 0)
            LogTimeline($"[프리셋] R 잔류: R_CellNo={_opt.InitialRCellNo} R_Seq={_opt.InitialRSeq} R_Flag={_opt.InitialRFlag}");
    }

    // ─── 레지스터 직접 읽기/세팅 (테스트 검증용) ───────────────────────────

    public PlcSnapshot ReadSnapshot()
    {
        ushort[] copy;
        lock (_hrLock) { copy = _hr.ToArray(); }
        return PlcSnapshot.FromRegisters(copy, online: true, at: DateTimeOffset.Now);
    }

    /// <summary>테스트 전용(§2E): 기동 후 R 영역에 잔류(R_Flag=1 + 지정값)를 직접 세팅.</summary>
    public void SetRResidue(int rCellNo, int rSeq)
    {
        lock (_hrLock)
        {
            _hr[RegisterMap.R_CellNo] = (ushort)rCellNo;
            _hr[RegisterMap.R_Seq]    = (ushort)rSeq;
            _hr[RegisterMap.Flags]    = (ushort)(_hr[RegisterMap.Flags] | RegisterMap.D4.R_Flag);
            FlushToServerLocked();
        }
        LogTimeline($"[테스트] R 잔류 세팅: R_CellNo={rCellNo} R_Seq={rSeq} R_Flag=1");
    }

    /// <summary>
    /// 테스트 전용(S-AUDIT-D-HANDSHAKE-HARDENING D②): C 영역에 잔류(C_Flag=1 + 지정값)를 직접 세팅.
    /// PLC(3DS)가 C_Flag를 소비·클리어하지 못한 "C_Flag 미소비 잔류" 상황을 재현한다 —
    /// <see cref="InjectNoResponse"/>=true(상태기계 정지)와 함께 쓰면 WCS 핸드셰이크의 C_Flag 대기 상한
    /// (CFlagTimeoutMs) 초과 → CFlagTimeout 경로를 결정적으로 유발한다(무한대기 배제 회귀 가드).
    /// </summary>
    public void SetCResidue(int cCellNo, int cSeq)
    {
        lock (_hrLock)
        {
            _hr[RegisterMap.C_CellNo] = (ushort)cCellNo;
            _hr[RegisterMap.C_Seq]    = (ushort)cSeq;
            _hr[RegisterMap.Flags]    = (ushort)(_hr[RegisterMap.Flags] | RegisterMap.D4.C_Flag);
            FlushToServerLocked();
        }
        LogTimeline($"[테스트] C 잔류 세팅: C_CellNo={cCellNo} C_Seq={cSeq} C_Flag=1");
    }

    /// <summary>
    /// 테스트 전용(C2 §4-B): TgtFloor(D6)에 잔류값을 직접 세팅(콜드스타트 클리어 대상). 이동 유발을 피하려면
    /// 호출자가 CurFloor와 같은 값을 준다(tgt==cur → Sim 상태기계 미이동). WCS 기동 클리어가 이 값을 0으로 지운다.
    /// </summary>
    public void SetTgtFloor(int tgtFloor)
    {
        lock (_hrLock)
        {
            _hr[RegisterMap.TgtFloor] = (ushort)tgtFloor;
            FlushToServerLocked();
        }
        LogTimeline($"[테스트] TgtFloor 잔류 세팅: TgtFloor={tgtFloor}");
    }

    /// <summary>테스트 전용: Ready 비트(D4.2)를 직접 세팅(BUSY 재현).</summary>
    public void SetReady(bool ready)
    {
        lock (_hrLock)
        {
            if (ready)
                _hr[RegisterMap.Flags] = (ushort)(_hr[RegisterMap.Flags] | RegisterMap.D4.Ready);
            else
                _hr[RegisterMap.Flags] = (ushort)(_hr[RegisterMap.Flags] & ~RegisterMap.D4.Ready);
            FlushToServerLocked();
        }
        LogTimeline($"[테스트] Ready 세팅: Ready={(ready ? 1 : 0)}");
    }

    // ─── 시뮬레이터 메인 루프 ────────────────────────────────────────────────

    internal async Task RunAsync(CancellationToken ct)
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
                    PullFromServerLocked();

                    // sticky(§2C/S5) 백스톱 — 주 경로는 OnServerRegistersChanged 동기 복원.
                    if (InjectStickyRResidue && (_hr[RegisterMap.Flags] & RegisterMap.D4.R_Flag) == 0)
                    {
                        _hr[RegisterMap.Flags] |= RegisterMap.D4.R_Flag;
                        FlushToServerLocked();
                    }

                    flags    = _hr[RegisterMap.Flags];
                    tgtFloor = _hr[RegisterMap.TgtFloor];
                    curFloor = _hr[RegisterMap.CurFloor];
                }

                bool cFlag = (flags & RegisterMap.D4.C_Flag) != 0;

                if (cFlag && !sorting && !moving)
                {
                    int cellNo, cSeq;
                    lock (_hrLock)
                    {
                        cellNo = _hr[RegisterMap.C_CellNo];
                        cSeq   = _hr[RegisterMap.C_Seq];
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

                if (!sorting && !moving && tgtFloor != 0 && tgtFloor != curFloor)
                {
                    lock (_stateLock) { _isMoving = true; }
                    _ = Task.Run(() => RunMoveSequenceAsync(tgtFloor, ct), CancellationToken.None);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Sim3ds 루프 예외(unit={Unit})", UnitId);
            }
        }
    }

    // ─── sticky 고장 충실도 훅(S-S5-FLAKE) — 자기 unitId만 처리 ─────────────

    private void OnServerRegistersChanged(object? sender, RegistersChangedEventArgs e)
    {
        if (!_stickyRResidue || e.UnitIdentifier != UnitId) return;

        lock (_hrLock)
        {
            var span = RequireServer().GetHoldingRegisters(UnitId);
            ushort flags = BinaryPrimitives.ReverseEndianness((ushort)span[RegisterMap.Flags]);
            if ((flags & RegisterMap.D4.R_Flag) == 0)
            {
                flags |= RegisterMap.D4.R_Flag;
                span[RegisterMap.Flags] = (short)BinaryPrimitives.ReverseEndianness(flags);
                _hr[RegisterMap.Flags]  = flags;
            }
        }
    }

    // ─── 분류 시퀀스 ─────────────────────────────────────────────────────────

    private async Task RunSortSequenceAsync(int cellNo, int cSeq, CancellationToken ct)
    {
        try
        {
            await Task.Delay(_opt.TiltDelayMs, ct).ConfigureAwait(false);

            ushort prevTgt;
            lock (_hrLock)
            {
                prevTgt = _hr[RegisterMap.TgtFloor];
                _hr[RegisterMap.Flags]    = (ushort)(_hr[RegisterMap.Flags] & ~RegisterMap.D4.Ready);
                _hr[RegisterMap.TgtFloor] = 0;
                FlushToServerLocked();
            }
            LogTimeline($"분류 시작: Ready=0, TgtFloor 클리어 (이전={prevTgt})");

            await Task.Delay(_opt.SortDurationMs, ct).ConfigureAwait(false);

            if (InjectRFlagDelayMs > 0)
            {
                LogTimeline($"[고장주입] R_Flag 지연 {InjectRFlagDelayMs}ms");
                await Task.Delay(InjectRFlagDelayMs, ct).ConfigureAwait(false);
            }

            int actualRSeq = InjectRSeqOverride ?? cSeq;
            if (InjectRSeqOverride.HasValue)
                LogTimeline($"[고장주입] R_Seq 교체: C_Seq={cSeq} → R_Seq={actualRSeq}");

            lock (_hrLock)
            {
                _hr[RegisterMap.R_CellNo] = (ushort)cellNo;
                _hr[RegisterMap.R_Seq]    = (ushort)actualRSeq;
                _hr[RegisterMap.Flags]    = (ushort)(_hr[RegisterMap.Flags] | RegisterMap.D4.R_Flag);
                FlushToServerLocked();
            }
            LogTimeline($"R 세팅: R_CellNo={cellNo}, R_Seq={actualRSeq}, R_Flag=1");

            ushort tgt, cur;
            lock (_hrLock)
            {
                PullFromServerLocked();
                tgt = _hr[RegisterMap.TgtFloor];
                cur = _hr[RegisterMap.CurFloor];
            }

            bool needMove = (tgt != 0 && tgt != cur);
            if (needMove)
            {
                LogTimeline($"분류 완료 후 복귀 이동: TgtFloor={tgt} CurFloor={cur} → Ready=0 유지");
                lock (_stateLock) { _isSorting = false; _isMoving = true; }
                await RunMoveBodyAsync(tgt, ct).ConfigureAwait(false);
                lock (_stateLock) { _isMoving = false; }
            }
            else
            {
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
            _log.LogError(ex, "Sim3ds 분류 시퀀스 예외(unit={Unit})", UnitId);
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
        catch (Exception ex) { _log.LogError(ex, "Sim3ds 이동 시퀀스 예외(unit={Unit})", UnitId); }
        finally
        {
            lock (_stateLock) { _isMoving = false; }
        }
    }

    private async Task RunMoveBodyAsync(ushort tgtFloor, CancellationToken ct)
    {
        lock (_hrLock)
        {
            _hr[RegisterMap.Flags] = (ushort)(_hr[RegisterMap.Flags] & ~RegisterMap.D4.Ready);
            FlushToServerLocked();
        }
        LogTimeline($"이동 시작: TgtFloor={tgtFloor}, Ready=0");

        await Task.Delay(_opt.MoveDurationMs, ct).ConfigureAwait(false);

        lock (_hrLock)
        {
            _hr[RegisterMap.CurFloor] = _hr[RegisterMap.TgtFloor];
            _hr[RegisterMap.Flags]    = (ushort)(_hr[RegisterMap.Flags] | RegisterMap.D4.Ready);
            FlushToServerLocked();
        }
        ushort cur;
        lock (_hrLock) { cur = _hr[RegisterMap.CurFloor]; }
        LogTimeline($"이동 완료: CurFloor={cur}, TgtFloor 유지, Ready=1");
    }

    // ─── HR ↔ 서버 버퍼 동기화 (호출자는 _hrLock 보유) ─────────────────────

    private ModbusServer RequireServer([CallerMemberName] string? caller = null) =>
        _server ?? throw new InvalidOperationException(
            $"SimServer가 아직 기동되지 않았습니다({caller}). StartAsync()를 먼저 호출하세요 " +
            "— 전송 계층은 StartAsync에서 생성됩니다.");

    private void FlushToServerLocked()
    {
        var span = RequireServer().GetHoldingRegisters(UnitId);
        for (int i = 0; i < RegisterMap.BlockLength; i++)
            span[i] = (short)BinaryPrimitives.ReverseEndianness(_hr[i]);
    }

    private void PullFromServerLocked()
    {
        var span = RequireServer().GetHoldingRegisters(UnitId);
        for (int i = 0; i < RegisterMap.BlockLength; i++)
        {
            ushort prev = _hr[i];
            _hr[i] = BinaryPrimitives.ReverseEndianness((ushort)span[i]);
            if (_hr[i] != prev)
                LogTimeline($"WCS 쓰기 수신: D{i} {prev}→{_hr[i]}");
        }
    }

    // ─── 로그 ────────────────────────────────────────────────────────────────

    private void LogTimeline(string msg)
    {
        var line = $"[{DateTimeOffset.Now:HH:mm:ss.fff}] {_logPrefix}{msg}";
        _log.LogInformation("{Line}", line);
        _timelineLog?.Invoke(line);
    }
}
