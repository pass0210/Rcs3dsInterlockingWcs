using System.IO.Ports;
using FluentModbus;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wcs.Core;

namespace Wcs.Sim3ds;

/// <summary>
/// 3DS PLC 시뮬레이터 — SPEC §6 정정본 동작.
/// in-process 기동·구성·고장주입·종료 가능한 타입.
/// Program.cs entrypoint는 이 타입을 호출하는 얇은 래퍼.
///
/// S-MULTISORTER-SHARED-BUS(모듈 A): 한 물리 버스(TCP 엔드포인트 / RTU 포트)에서 여러 unitId
/// 슬레이브를 응답할 수 있다. 유닛별 상태(레지스터 뱅크·상태기계·고장주입)는 <see cref="SimSlave"/>로
/// 분리했다. 단일 유닛 구성(기존 생성자)은 SimSlave 1개를 소유·위임 — 동작·바이트·로그 라인 전부 현행 보존.
/// </summary>
public sealed class SimServer : IAsyncDisposable
{
    // ─── 설정 ────────────────────────────────────────────────────────────────
    public sealed record Options
    {
        // ─── 전송 선택(S-SIM3DS-RTU) ─────────────────────────────────────────
        // "Tcp"(기본·현행 보존) | "Rtu". 미지정/기본 = Tcp → 기존 dotnet run 동작 바이트 동일.
        public string Transport       { get; init; } = "Tcp";

        // ─── TCP 전용 ─────────────────────────────────────────────────────────
        public string Host            { get; init; } = "127.0.0.1";
        public int    Port            { get; init; } = 1502;

        // ─── RTU 전용(전부 설정값 — 절대규칙 #7, 하드코딩 금지) ────────────────
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
        public int  InitialRCellNo { get; init; }          // 기본 0
        public int  InitialRSeq    { get; init; }          // 기본 0
        public bool InitialRFlag   { get; init; }          // 기본 false

        // ─── RTU 파싱 헬퍼(WCS PlcTransportOptions와 동형 — Consistency Over Preference) ─────
        public Parity   ParsedParity   => Enum.Parse<Parity>(Parity, ignoreCase: true);
        public StopBits ParsedStopBits => Enum.Parse<StopBits>(StopBits, ignoreCase: true);

        // Modbus 유닛 식별자를 byte로 검증 변환한다(1~247, 무음 절단 방지 — C-2).
        public byte ParsedUnitId => ValidateUnitId(UnitId);
    }

    /// <summary>
    /// Modbus 유닛 식별자 범위 검증(1~247). 0=브로드캐스트·248~255=예약·byte 초과는 fail-loud
    /// (무음 절단 방지). Options.ParsedUnitId와 멀티유닛 생성자가 공유.
    /// </summary>
    internal static byte ValidateUnitId(int unitId) => unitId is >= 1 and <= 247
        ? (byte)unitId
        : throw new InvalidOperationException(
            $"Sim3ds UnitId 범위 오류: {unitId}. Modbus 유닛 식별자는 1~247 이어야 합니다 " +
            "(0=브로드캐스트·248~255=예약 — byte 무음 절단 방지).");

    // ─── 고장 주입(단일 유닛 파사드 — primary 슬레이브에 위임) ─────────────────
    // 멀티유닛은 Unit(unitId)로 슬레이브별 접근. 아래는 기존 단일 유닛 API 보존용 위임.

    /// <summary>R_Seq를 보낸 C_Seq 대신 이 값으로 교체 (primary 슬레이브).</summary>
    public int? InjectRSeqOverride   { get => _primary.InjectRSeqOverride;   set => _primary.InjectRSeqOverride = value; }
    /// <summary>R_Flag 세팅 전 추가 지연 ms (primary 슬레이브).</summary>
    public int  InjectRFlagDelayMs   { get => _primary.InjectRFlagDelayMs;   set => _primary.InjectRFlagDelayMs = value; }
    /// <summary>상태기계 처리 중단(폴은 계속 — RFlagTimeout 유발, primary 슬레이브).</summary>
    public bool InjectNoResponse     { get => _primary.InjectNoResponse;     set => _primary.InjectNoResponse = value; }
    /// <summary>ClearR 미반영(R_Flag 재천명) 고장(primary 슬레이브).</summary>
    public bool InjectStickyRResidue { get => _primary.InjectStickyRResidue; set => _primary.InjectStickyRResidue = value; }

    // ─── 내부 ────────────────────────────────────────────────────────────────
    private readonly Options               _opt;
    private readonly ILogger<SimServer>    _log;
    private readonly Action<string>?       _timelineLog;
    private readonly IModbusRtuSerialPort? _injectedRtuPort; // 테스트 seam(주입 시 RTU-fake)

    private readonly byte[]                    _unitIds;
    private readonly SimSlave[]                _slaves;
    private readonly Dictionary<byte, SimSlave> _byUnit;
    private readonly SimSlave                  _primary;   // 단일 유닛 파사드 대상(= _slaves[0])

    private ISimTransport?           _transport;
    private CancellationTokenSource? _cts;
    private Task[]?                  _loops;

    // ─── 생성자 ────────────────────────────────────────────────────────────────

    /// <summary>단일 유닛(현행 API 보존).</summary>
    public SimServer(Options opt, ILogger<SimServer>? log = null, Action<string>? timelineLog = null)
        : this(opt, new[] { opt.ParsedUnitId }, injectedRtuPort: null, log, timelineLog) { }

    /// <summary>
    /// 테스트 전용 — 주입된 <see cref="IModbusRtuSerialPort"/>로 RTU 슬레이브 기동(물리 COM 불요, 단일 유닛).
    /// </summary>
    public SimServer(Options opt, IModbusRtuSerialPort fakePort,
                     ILogger<SimServer>? log = null, Action<string>? timelineLog = null)
        : this(opt, new[] { opt.ParsedUnitId }, injectedRtuPort: fakePort, log, timelineLog) { }

    /// <summary>
    /// 멀티유닛(S-MULTISORTER-SHARED-BUS 모듈 A) — 한 엔드포인트/포트에서 여러 unitId 슬레이브 응답.
    /// 각 unitId는 독립 레지스터 뱅크·상태기계·고장주입을 가진다(<see cref="Unit(byte)"/>로 접근).
    /// 타이밍·초기층·잔류 프리셋은 <paramref name="opt"/> 공통(잔류는 opt-in — 기본 무잔류).
    /// </summary>
    public SimServer(Options opt, IReadOnlyList<byte> unitIds,
                     ILogger<SimServer>? log = null, Action<string>? timelineLog = null)
        : this(opt, ValidateUnitIds(unitIds), injectedRtuPort: null, log, timelineLog) { }

    private SimServer(Options opt, byte[] unitIds, IModbusRtuSerialPort? injectedRtuPort,
                      ILogger<SimServer>? log, Action<string>? timelineLog)
    {
        _opt             = opt;
        _log             = log ?? NullLogger<SimServer>.Instance;
        _timelineLog     = timelineLog;
        _injectedRtuPort = injectedRtuPort;
        _unitIds         = unitIds;

        // 로그 prefix: 단일 유닛이면 "" → 기존 타임라인 문자열 매칭 테스트 무영향. 멀티면 "[u{n}] ".
        bool multi = unitIds.Length > 1;
        _slaves = unitIds
            .Select(u => new SimSlave(u, opt, _log, timelineLog, logPrefix: multi ? $"[u{u}] " : ""))
            .ToArray();
        _byUnit  = _slaves.ToDictionary(s => s.UnitId);
        _primary = _slaves[0];
    }

    /// <summary>멀티유닛 unitIds 검증: 각 1~247 + 중복 금지 + 최소 1개.</summary>
    private static byte[] ValidateUnitIds(IReadOnlyList<byte> unitIds)
    {
        if (unitIds is null || unitIds.Count == 0)
            throw new InvalidOperationException("Sim3ds: unitIds가 비어 있습니다(최소 1개 필요).");
        var result = unitIds.Select(u => ValidateUnitId(u)).ToArray();
        if (result.Distinct().Count() != result.Length)
            throw new InvalidOperationException(
                $"Sim3ds: 중복 unitId — [{string.Join(",", result)}]. 한 버스의 슬레이브 주소는 유일해야 합니다.");
        return result;
    }

    // ─── 유닛 접근 ──────────────────────────────────────────────────────────

    /// <summary>등록된 unitId 목록.</summary>
    public IReadOnlyList<byte> UnitIds => _unitIds;

    /// <summary>지정 unitId의 슬레이브(멀티유닛 슬레이브별 검증·고장주입·잔류/Ready 세팅).</summary>
    public SimSlave Unit(byte unitId) => _byUnit.TryGetValue(unitId, out var s)
        ? s
        : throw new ArgumentOutOfRangeException(nameof(unitId),
            $"Sim3ds: unitId={unitId} 슬레이브가 없습니다. 등록: [{string.Join(",", _unitIds)}].");

    // ─── 단일 유닛 파사드(primary 슬레이브 위임 — 현행 API 보존) ──────────────

    public PlcSnapshot ReadSnapshot()             => _primary.ReadSnapshot();
    public void        SetRResidue(int rCellNo, int rSeq) => _primary.SetRResidue(rCellNo, rSeq);
    public void        SetTgtFloor(int tgtFloor)  => _primary.SetTgtFloor(tgtFloor);
    public void        SetReady(bool ready)       => _primary.SetReady(ready);

    // ─── 시작·종료 ──────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken outerCt = default)
    {
        // 전송 선택·검증(fail-loud). 멀티유닛이면 유닛 수만큼 AddUnit / RTU 멀티유닛 ctor.
        _transport = SimTransportFactory.Create(_opt, _unitIds, _injectedRtuPort);

        var server = _transport.Server;
        server.EnableRaisingEvents = true;

        // 슬레이브별 무응답 고장주입(B5 슬레이브별 OFFLINE 독립 검증용) — 서버 단일 RequestValidator가
        // 슬레이브별 InjectUnresponsive를 참조해 해당 unitId 요청만 예외 응답. 무고장 시 OK(무동작).
        server.RequestValidator = (unit, _, _, _) =>
            _byUnit.TryGetValue(unit, out var s) && s.InjectUnresponsive
                ? ModbusExceptionCode.ServerDeviceFailure
                : ModbusExceptionCode.OK;

        // 클라이언트 연결(Start) 이전에 각 슬레이브 결선(RegistersChanged 구독 + 초기 레지스터).
        foreach (var slave in _slaves)
            slave.Bind(server);

        _transport.Start();
        LogTimeline(_unitIds.Length > 1
            ? $"Sim3ds 서버 기동 {_transport.Endpoint} (units=[{string.Join(",", _unitIds)}])"
            : $"Sim3ds 서버 기동 {_transport.Endpoint}");

        _cts   = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        _loops = _slaves.Select(s => Task.Run(() => s.RunAsync(_cts.Token))).ToArray();
        // 고정 sleep 제거 — GW WaitUntil(Online) 폴링이 흡수.
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (_cts is not null)
            await _cts.CancelAsync().ConfigureAwait(false);

        if (_loops is not null)
            foreach (var t in _loops)
            {
                try { await t.ConfigureAwait(false); }
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

    // ─── 로그 ────────────────────────────────────────────────────────────────

    private void LogTimeline(string msg)
    {
        var line = $"[{DateTimeOffset.Now:HH:mm:ss.fff}] {msg}";
        _log.LogInformation("{Line}", line);
        _timelineLog?.Invoke(line);
    }
}
