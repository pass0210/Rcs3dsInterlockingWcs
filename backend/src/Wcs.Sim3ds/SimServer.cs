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

        // Modbus 유닛 식별자를 byte로 검증 변환한다. RTU 슬레이브 유효 범위 1~247
        // (0=브로드캐스트·248~255=예약). 범위 밖(예: 300)은 (byte) 무음 절단(300→44) 대신
        // fail-loud — 형제 ParsedParity/ParsedStopBits와 동형(Consistency Over Preference).
        public byte ParsedUnitId => UnitId is >= 1 and <= 247
            ? (byte)UnitId
            : throw new InvalidOperationException(
                $"Sim3ds UnitId 범위 오류: {UnitId}. Modbus 유닛 식별자는 1~247 이어야 합니다 " +
                "(0=브로드캐스트·248~255=예약 — byte 무음 절단 방지).");
    }

    // ─── 고장 주입 ───────────────────────────────────────────────────────────
    // 주입 필드는 테스트 스레드가 쓰고 Sim 루프·시퀀스(Task.Run) 스레드가 읽는다 →
    // 크로스 스레드 가시성을 위해 형제 _noResponse(volatile)와 동형으로 volatile 백킹 필드에 담는다
    // (A-3, 테스트 결정성 — lock 우연 배리어에 의존하지 않음). 의미·기본값은 불변.

    // R_Seq 교체값은 int?(Nullable<int>) — volatile 대상 타입이 아니라 직접 volatile 불가.
    // '유무 플래그 + 값' 두 volatile 필드로 분해해 가시성을 확보(setter: 값→플래그 순, getter: 플래그→값 순).
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
    public int InjectRFlagDelayMs
    {
        get => _rFlagDelayMs;
        set => _rFlagDelayMs = value;
    }

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

    private volatile bool _stickyRResidue;
    /// <summary>
    /// true = WCS가 R를 클리어(ClearR)해도 시뮬레이터가 R_Flag=1을 즉시 재천명(PLC 무ack·ClearR 미반영 모사).
    /// 잔류 대사 R_Flag==0 확인 타임아웃 경로(§2C·S5) 유발용 — 실측 PLC의 R 자체 유지 동작을 깨지 않고
    /// "클리어가 반영되지 않는 고장"만 재현한다. 기본 false(기존 동작 보존).
    /// </summary>
    public bool InjectStickyRResidue
    {
        get => _stickyRResidue;
        set => _stickyRResidue = value;
    }

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
        _unitId          = opt.ParsedUnitId;   // 1~247 범위 검증 fail-loud(무음 절단 방지 — C-2)
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
        _unitId          = opt.ParsedUnitId;   // 1~247 범위 검증 fail-loud(무음 절단 방지 — C-2)
        _injectedRtuPort = fakePort;
    }

    // ─── 시작·종료 ──────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken outerCt = default)
    {
        // 전송 선택·검증(fail-loud: 잘못된 Transport / RTU PortName 미지정). 서버 버퍼는 여기서 확보.
        _transport = SimTransportFactory.Create(_opt, _unitId, _injectedRtuPort);

        // ── sticky 고장 충실도(S-S5-FLAKE) ────────────────────────────────────────
        // WCS 쓰기가 서버 버퍼를 바꾸는 "그 순간" 동기적으로(쓰기 처리 스레드·ModbusServer.Lock 보유 중)
        // R_Flag를 복원할 수 있도록 RegistersChanged 이벤트를 구독한다(핸들러는 sticky 비활성 시 즉시 반환
        // — 다른 모든 테스트/전송에 영향 0). 클라이언트 연결(Start) 이전에 구독해 첫 쓰기부터 포착.
        _transport.Server.EnableRaisingEvents = true;
        _transport.Server.RegistersChanged   += OnServerRegistersChanged;

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

    /// <summary>
    /// 테스트 전용: Ready 비트(D4.2)를 직접 세팅한다 — "소터 BUSY(분류/이동 중, Ready==0)"를
    /// 분류/이동 타이밍에 의존하지 않고 결정적으로 재현(수동 O4/O6 Ready 사전점검 409 검증용).
    /// 유휴 상태의 Sim 루프는 Ready 비트를 건드리지 않으므로(분류/이동 시퀀스에서만 조작) 세팅값이 유지된다.
    /// SetRResidue와 동형 test seam(형제 opt-in 주입 필드들과 같은 패턴).
    /// </summary>
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

                    // 고장주입(§2C/S5): WCS ClearR로 R_Flag가 꺼졌으면 재천명(PLC 무ack·ClearR 미반영 모사).
                    // 실측 PLC의 "R 자체 클리어 안 함"은 그대로 두고 "클리어가 반영 안 되는 고장"만 재현.
                    // 주 경로는 RegistersChanged 이벤트(OnServerRegistersChanged)가 쓰기 즉시 동기 복원한다
                    // — 이 Sim 루프 재천명은 백스톱(이벤트가 이미 복원했으면 여기선 무변화). S-S5-FLAKE.
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

    // ─── sticky 고장 충실도 훅(S-S5-FLAKE) ───────────────────────────────────

    /// <summary>
    /// FluentModbus <c>RegistersChanged</c> 핸들러 — WCS 클라이언트 쓰기가 서버 버퍼를 변경한 직후
    /// (쓰기 처리 스레드에서 동기 발화, async 서버는 <c>ModbusServer.Lock</c> 보유 중)에 호출된다.
    ///
    /// sticky 고장(<see cref="InjectStickyRResidue"/>) 활성 시, WCS ClearR(D4 RMW FC06)가 R_Flag를
    /// 0으로 바꾸는 그 순간 R_Flag=1을 즉시 복원한다. 이벤트가 FC06 응답 반환 이전(그리고 GW의 다음 폴
    /// FC03 read 이전 — 단일 커넥션 직렬)에 동기 실행되므로, 서버 버퍼는 sticky 구간에서 R_Flag=0을
    /// "한 순간도" 노출하지 않는다. 이로써 GW 폴이 일시적 0을 샘플링해 arming(잔류 대사)을 거짓 완료시키던
    /// 창(S-S5-FLAKE 근본원인 — CPU 경합 시 Sim 루프 재천명 지연으로 확대)을 스케줄링과 무관하게 결정적으로
    /// 제거한다. "ClearR 미반영" 고장을 충실히(=클리어가 관측상 전혀 반영되지 않음) 모델링 —
    /// 실 무ack PLC의 의미와 일치. sticky 비활성이면 즉시 반환(다른 모든 테스트·전송에 영향 0).
    ///
    /// 잠금 안전성: 핸들러는 <c>_hrLock</c>만 취득한다. GetHoldingRegisters는 내부적으로
    /// ModbusServer.Lock을 취득하지 않으므로(FluentModbus 5.3.2 — 무동기 span 반환), Sim 루프
    /// (_hrLock→버퍼)와 이 핸들러(ModbusServer.Lock→_hrLock) 사이에 락 순서 순환이 없다(데드락 없음).
    /// </summary>
    private void OnServerRegistersChanged(object? sender, RegistersChangedEventArgs e)
    {
        if (!_stickyRResidue || e.UnitIdentifier != _unitId) return;

        lock (_hrLock)
        {
            var span = RequireTransport().Server.GetHoldingRegisters(_unitId);
            // 서버 버퍼는 빅엔디언 저장 — Flags 워드만 역변환해 R_Flag 확인/복원(Flush/Pull과 동형 변환).
            ushort flags = BinaryPrimitives.ReverseEndianness((ushort)span[RegisterMap.Flags]);
            if ((flags & RegisterMap.D4.R_Flag) == 0)
            {
                flags |= RegisterMap.D4.R_Flag;
                span[RegisterMap.Flags] = (short)BinaryPrimitives.ReverseEndianness(flags);
                _hr[RegisterMap.Flags]  = flags; // 섀도 일관성 — 다음 Sim 루프 Pull이 되돌리지 않도록.
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
    /// 전송 계층(StartAsync에서 생성)을 반환하거나, 기동 전이면 fail-loud한다.
    /// SetRResidue/Flush/Pull 등을 StartAsync 이전에 호출하면 <c>_transport</c>가 null이라
    /// NRE가 나던 것을 명확한 예외로 대체(C-3). CallerMemberName으로 어느 동작이 기동 전이었는지 표기.
    /// </summary>
    private ISimTransport RequireTransport([CallerMemberName] string? caller = null) =>
        _transport ?? throw new InvalidOperationException(
            $"SimServer가 아직 기동되지 않았습니다({caller}). StartAsync()를 먼저 호출하세요 " +
            "— 전송 계층은 StartAsync에서 생성됩니다.");

    /// <summary>
    /// 내부 _hr 배열 → FluentModbus 서버 버퍼에 기록. _hrLock 보유 상태에서 호출.
    /// FluentModbus 서버는 raw bytes로 저장, Modbus 클라이언트는 빅엔디언으로 전송.
    /// 호스트가 x86(리틀엔디언)이면 서버 버퍼에서 바이트 순서를 뒤집어야 일치함.
    /// </summary>
    private void FlushToServerLocked()
    {
        var span = RequireTransport().Server.GetHoldingRegisters(_unitId);
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
        var span = RequireTransport().Server.GetHoldingRegisters(_unitId);
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
