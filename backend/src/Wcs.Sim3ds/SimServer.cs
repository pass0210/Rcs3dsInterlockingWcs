using System.Buffers.Binary;
using System.IO.Ports;
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
        // ─── 전송 선택(S-SIM3DS-RTU) ─────────────────────────────────────────
        // "Tcp"(기본·현행 보존) | "Rtu". 미지정/기본 = Tcp → 기존 dotnet run 동작 바이트 동일.
        // (WCS 측 기본은 Rtu이나, Sim3ds의 기존 관측 동작은 TCP였으므로 Sim3ds 기본은 Tcp로 두어 현행 보존 우선.)
        public string Transport       { get; init; } = "Tcp";

        // ─── TCP 전용 ─────────────────────────────────────────────────────────
        public string Host            { get; init; } = "127.0.0.1";
        public int    Port            { get; init; } = 1502;

        // ─── RTU 전용(전부 설정값 — 절대규칙 #7, 하드코딩 금지) ────────────────
        // 기본값은 WCS appsettings Sorters[0] placeholder와 정합(BaudRate=9600·Parity=Even·
        // StopBits=One·UnitId=1·Timeout=1000). 단 PortName은 안전한 기본값 없음 —
        // RTU 모드에서 미지정 시 fail-loud(우발적 COM1 점유 방지. WCS와 Sim은 시리얼 페어의 반대쪽 포트).
        public string? PortName       { get; init; }          // 기본 없음 — RTU 시 명시 필수
        public int     BaudRate       { get; init; } = 9600;
        public string  Parity         { get; init; } = "Even"; // "Even"/"Odd"/"None"
        public string  StopBits       { get; init; } = "One";  // "One"/"Two"
        public int     ReadTimeoutMs  { get; init; } = 1000;
        public int     WriteTimeoutMs { get; init; } = 1000;
        public int     UnitId         { get; init; } = 1;      // Modbus 유닛 식별자(TCP/RTU 공통)

        // ─── 시뮬레이션 타이밍 ────────────────────────────────────────────────
        public int    TiltDelayMs     { get; init; } = 200;   // 낙하 후 적재 대기
        public int    SortDurationMs  { get; init; } = 500;   // 분류 소요
        public int    MoveDurationMs  { get; init; } = 300;   // 이동 소요
        public int    InitialCurFloor { get; init; } = 1;
        public int    SimLoopMs       { get; init; } = 20;    // 상태 루프 주기

        // ─── R 잔류 프리셋 (테스트 전용 opt-in — S-HANDSHAKE-RESIDUE §2E) ─────────
        // 기동 시 R 영역에 잔류를 세팅해 "PLC 기동 잔류"(§2B 기동 reconcile 검증)를 결정적 재현.
        // 기본 0/false = 무잔류(기존 StartAsync 동작 보존). 실측 잔류값(R_CellNo=20, R_Seq=123) 재현 가능.
        // (핸드셰이크 시작 시점 잔류(§2A)는 기동 reconcile로 지워지므로 런타임 SetRResidue()로 재현.)
        public int  InitialRCellNo { get; init; }          // 기본 0
        public int  InitialRSeq    { get; init; }          // 기본 0
        public bool InitialRFlag   { get; init; }          // 기본 false

        // ─── RTU 파싱 헬퍼(WCS PlcTransportOptions와 동형 — Consistency Over Preference) ─────
        // 잘못된 값은 Enum.Parse가 fail-loud(예: Parity="Weird" → 명확한 예외).
        public Parity   ParsedParity   => Enum.Parse<Parity>(Parity, ignoreCase: true);
        public StopBits ParsedStopBits => Enum.Parse<StopBits>(StopBits, ignoreCase: true);
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

    /// <summary>
    /// true = WCS가 R를 클리어(ClearR)해도 시뮬레이터가 R_Flag=1을 즉시 재천명(PLC 무ack·ClearR 미반영 모사).
    /// 잔류 대사 R_Flag==0 확인 타임아웃 경로(§2C·S5) 유발용 — 실측 PLC의 R 자체 유지 동작을 깨지 않고
    /// "클리어가 반영되지 않는 고장"만 재현한다. 기본 false(기존 동작 보존).
    /// </summary>
    public bool InjectStickyRResidue { get; set; }

    // ─── 내부 ────────────────────────────────────────────────────────────────
    private readonly byte                  _unitId;   // Modbus 유닛 식별자(설정값·기본 1)

    private readonly Options               _opt;
    private readonly ILogger<SimServer>    _log;
    private readonly Action<string>?       _timelineLog;

    // 전송 계층은 StartAsync에서 생성(Transport 값 검증·fail-loud 포함). 종료 전까지 non-null.
    private readonly IModbusRtuSerialPort? _injectedRtuPort; // 테스트 seam(주입 시 RTU-fake)
    private ISimTransport?                 _transport;

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
        _opt             = opt;
        _log             = log ?? NullLogger<SimServer>.Instance;
        _timelineLog     = timelineLog;
        _unitId          = (byte)opt.UnitId;
        _injectedRtuPort = null; // 전송은 opt.Transport에 따라 StartAsync에서 선택
    }

    /// <summary>
    /// 테스트 전용 생성자 — 주입된 <see cref="IModbusRtuSerialPort"/>로 RTU 슬레이브를 기동한다
    /// (물리 COM 불요·CI에서 실 SimServer(RTU) 상태기계 왕복 검증용, 계약 (b)).
    /// ModbusRtuMaster의 fake-port 주입 생성자 패턴과 동형(Consistency Over Preference).
    /// opt.Transport 값과 무관하게 RTU-주입 모드로 동작한다.
    /// </summary>
    public SimServer(Options opt, IModbusRtuSerialPort fakePort,
                     ILogger<SimServer>? log = null, Action<string>? timelineLog = null)
    {
        _opt             = opt;
        _log             = log ?? NullLogger<SimServer>.Instance;
        _timelineLog     = timelineLog;
        _unitId          = (byte)opt.UnitId;
        _injectedRtuPort = fakePort;
    }

    // ─── 시작·종료 ──────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken outerCt = default)
    {
        // 전송 선택·검증(fail-loud: 잘못된 Transport / RTU PortName 미지정). 서버 버퍼는 여기서 확보.
        _transport = SimTransportFactory.Create(_opt, _unitId, _injectedRtuPort);

        lock (_hrLock)
        {
            Array.Clear(_hr, 0, _hr.Length);
            _hr[RegisterMap.CurFloor] = (ushort)_opt.InitialCurFloor;
            _hr[RegisterMap.Flags]    = RegisterMap.D4.Ready; // Ready=1(수용 가능)

            // R 잔류 프리셋(opt-in — §2E). 기본 0/false면 무잔류(기존 동작 보존).
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

        _transport.Start();
        LogTimeline($"Sim3ds 서버 기동 {_transport.Endpoint}");

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

        _transport?.Stop();
        LogTimeline("Sim3ds 서버 종료");
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _transport?.Dispose();
        _cts?.Dispose();
    }

    // ─── 레지스터 직접 읽기 (테스트 검증용) ─────────────────────────────────

    public PlcSnapshot ReadSnapshot()
    {
        ushort[] copy;
        lock (_hrLock) { copy = _hr.ToArray(); }
        return PlcSnapshot.FromRegisters(copy, online: true, at: DateTimeOffset.Now);
    }

    /// <summary>
    /// 테스트 전용(§2E): 기동 후 R 영역에 잔류를 직접 세팅한다 —
    /// "핸드셰이크 시작 시점에 R_Flag=1(+R_CellNo/R_Seq 지정값) 잔류"를 결정적으로 재현.
    /// 실측 PLC와 동일하게 R는 WCS ClearR로만 비워진다(Sim 자체 클리어 없음 — 함정 5 보존).
    /// </summary>
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

                    // 고장주입(§2C/S5): WCS ClearR로 R_Flag가 꺼졌으면 즉시 재천명(PLC 무ack·ClearR 미반영 모사).
                    // 실측 PLC의 "R 자체 클리어 안 함"은 그대로 두고 "클리어가 반영 안 되는 고장"만 재현.
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
        var span = _transport!.Server.GetHoldingRegisters(_unitId);
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
        var span = _transport!.Server.GetHoldingRegisters(_unitId);
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
