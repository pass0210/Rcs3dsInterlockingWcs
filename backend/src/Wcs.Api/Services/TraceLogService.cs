using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Core;

namespace Wcs.Api;

// ════════════════════════════════════════════════════════════════════════════
// S-TRACE-LOG-VIEWER — 현장 추적용 전용 로그 sink (additive · 관측/로깅 전용).
//
// 핵심 이벤트를 각 줄 앞머리에 "이벤트 번호(1~10)"를 달아 전용 파일에 기입한다:
//   1 TgtFloor 펜딩큐 인큐 (IF-05 enqueue + 재시작 복원 re-enqueue)
//   2 TgtFloor 펜딩큐 디큐 (관측 루프 pop)
//   3 IF-10 도착         (DepositReport 진입)
//   4 C 인큐            (HandshakeOrchestrator HS_C_SENT)
//   5 C 디큐            (PlcGateway CELL_ASSIGN 실제 write)
//   6 C 클리어          (PLC가 C_Flag 1→0으로 클리어한 것을 폴 스냅샷에서 관측)
//   7 Ready 1→0        (소터 Ready 워드 1→0 전이를 폴 스냅샷에서 관측 — S-TRACE-READY-PUSH-AND-DEFAULT)
//   8 슈트상태 push(busy) (IF-08 UpdateChuteState PUT 전송 중 next_state==2 — 수용 불가 통지)
//   9 Ready 0→1        (소터 Ready 워드 0→1 전이를 폴 스냅샷에서 관측)
//   10 슈트상태 push(ready)(IF-08 UpdateChuteState PUT 전송 중 next_state==3 — 수용 가능 통지)
//
//   ★ 요청1(S-TRACE-READY-PUSH-AND-DEFAULT): 이벤트 7·9(Ready 전이)와 8·10(IF-08 push 전송) 시각을
//     관측해 "Ready 전이 ↔ RCS 통지(IF-08 push) 지연"을 측정한다. 7·9 는 소터 scope(층-경계,
//     pId/cSeq 상관 없음), 8·10 은 전송 chokepoint(ChuteStatePushClient) 계측(chuteNo best-effort).
//     Ready 에지와 push 는 직접 인과가 아니다(별개 관찰 루프 — 조사 C) — 상관은 chuteNo+시각+next_state 로만.
//
// 설계(OperationLogService 와 동형 — 논블로킹 백그라운드 채널 sink):
//   · Log()는 unbounded Channel 에 TryWrite 만(즉시 반환·논블로킹). 폴/핸드셰이크/HTTP 핫패스 무블로킹.
//   · 컨슈머가 별도 스레드에서 각 레코드를 (a) OnEntry 발화(SignalR relay) → (b) 전용 Serilog File 싱크에 1줄 기입.
//   · 전용 Serilog 로거 인스턴스(전역 Log.Logger 와 격리) — 기존 operation_log/Serilog 파일에 영향 0.
//   · 경로·롤링·크기·보존·파일명·백로그 take = appsettings("TraceLog") 전부 설정값(절대규칙 #7).
//   · fail-safe: enqueue·기록 실패가 본 동작을 막지 않는다(로깅 실패가 핸드셰이크/응답을 차단하지 않음).
//   · 절대규칙 #8: HandshakeOrchestrator/PlcGateway(EF 비의존)는 이 sink 를 모른다 — 기존 콜백 훅으로만
//     발화하고 이 Wcs.Api sink 가 파일에 기록한다(operation_log 패턴과 동일).
// ════════════════════════════════════════════════════════════════════════════

// ── 전용 트레이스 로그 설정(appsettings "TraceLog") — 하드코딩 금지(절대규칙 #7) ──

/// <summary>appsettings.json "TraceLog" 섹션 — 전용 추적 로그 sink 설정.</summary>
public sealed record TraceLogOptions
{
    /// <summary>트레이스 서브시스템 활성 여부. false 면 Log() no-op·컨슈머 미기동·백로그 빈 목록.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// 전용 로그 디렉터리(없으면 생성). 기본 = <c>D:\Rcs3dsInterlockingWcsLogs</c>.
    /// ★ 테스트는 이 실경로에 쓰지 말고 설정 오버라이드(env <c>TraceLog__Directory</c> 등)로 scratch 디렉터리 사용.
    /// </summary>
    public string Directory { get; init; } = @"D:\Rcs3dsInterlockingWcsLogs";

    /// <summary>파일명 패턴(Serilog 롤링 — 날짜/시퀀스가 하이픈 자리에 삽입). 기본 <c>trace-.log</c>.</summary>
    public string FileNamePattern { get; init; } = "trace-.log";

    /// <summary>롤링 주기(Serilog RollingInterval — Day/Hour/…). 기본 Day.</summary>
    public string RollingInterval { get; init; } = "Day";

    /// <summary>파일 1개 크기 상한(byte). 초과 시 시퀀스 롤(_001…). 기본 100MB.</summary>
    public long FileSizeLimitBytes { get; init; } = 104_857_600;

    /// <summary>보존 파일 수. 기본 30(일 롤링 30일).</summary>
    public int RetainedFileCountLimit { get; init; } = 30;

    /// <summary>백로그 REST 기본 take(미지정 시).</summary>
    public int BacklogTakeDefault { get; init; } = 100;

    /// <summary>백로그 REST take 상한(초과 요청 clamp).</summary>
    public int BacklogTakeMax { get; init; } = 500;

    /// <summary>take 를 [1, BacklogTakeMax]로 clamp. null/≤0 → BacklogTakeDefault.</summary>
    public int ClampTake(int? take) =>
        take is null or <= 0 ? BacklogTakeDefault : Math.Min(take.Value, BacklogTakeMax);
}

// ── 트레이스 레코드(파일 1줄·SignalR payload·백로그 반환의 단일 형상 — 카멜케이스 JSON) ──

/// <summary>
/// 추적 이벤트 1건. <see cref="EventNo"/>(1~10)로 이벤트 종류를 즉시 식별한다(피스 상관키가 아닌 종류 태그).
/// 피스 흐름(3~6)은 <see cref="PId"/> + (<see cref="ChuteNo"/>/<see cref="DestId"/>, <see cref="CSeq"/>)로 상관.
/// 층-큐 흐름(1·2)·Ready 전이(7·9)·IF-08 push(8·10)는 소터+층 scope(pId/cSeq 미포함 — 소터 경계).
/// Ready↔push 상관은 pId 아닌 <see cref="ChuteNo"/> + 시각 + next_state(Detail)로 이룬다(조사 C — 비인과).
/// </summary>
public sealed record TraceRecord(
    int            EventNo,       // 1~10(줄 앞머리 태그)
    string         Event,         // 사람이 읽는 이벤트명(TGTFLOOR_ENQUEUE 등)
    DateTimeOffset At,            // 로컬 시각(ms 포함)
    int?           PId,           // 피스 식별(RCS 부여) — 3~6·1(best-effort). 2는 null.
    int?           CSeq,          // C 핸드셰이크 시퀀스(소터별 단조) — 4·5·6.
    int?           ChuteNo,       // 목적지 chuteNo
    long?          DestId,        // destination.id
    int?           CellNo,        // 셀 번호 — 4·5·6.
    int?           Floor,         // 목표 층 — 1·2.
    int?           InductionNo,   // 인덕션(층 파생 근거) — 1(best-effort).
    string?        Trigger,       // 이벤트 트리거 구분(예: IF05/RESTORE for 1)
    string?        Detail);       // 이벤트별 부가 파라미터(원문 JSON)

// ── 쓰기/구독 인터페이스(관측/로깅 전용) ─────────────────────────────────────

/// <summary>전용 추적 로그 sink 쓰기 API(논블로킹 enqueue) + 실시간 테일 발화(relay 구독용).</summary>
public interface ITraceLogger
{
    /// <summary>추적 레코드를 채널에 enqueue(즉시 반환·논블로킹·fail-safe).</summary>
    void Log(TraceRecord record);

    /// <summary>단일 컨슈머가 각 레코드를 파일 기입 직전 발화 — MonitorRelayService 가 SignalR 로 브로드캐스트.</summary>
    event Action<TraceRecord>? OnEntry;
}

/// <summary>전용 추적 로그 백로그(파일 tail) 조회 — 프론트 뷰어 시드용(읽기 전용).</summary>
public interface ITraceBacklog
{
    /// <summary>최근 트레이스 레코드 N개(최신 하단이 되도록 시계열 오름차순 반환). 디렉터리 부재 시 생성 후 빈 목록.</summary>
    IReadOnlyList<TraceRecord> Read(int? take, int? eventNo, int? pId, int? cSeq);
}

// ── 전용 sink 서비스 — 논블로킹 채널 + 백그라운드 파일 writer(OperationLogService 동형) ──

/// <summary>
/// 전용 추적 로그 sink — <see cref="ITraceLogger"/>(논블로킹 enqueue) + <see cref="ITraceBacklog"/>(파일 tail) +
/// IHostedService(백그라운드 컨슈머). 컨슈머가 OnEntry 발화 후 전용 Serilog File 싱크에 <c>[N] {json}</c> 1줄 기입.
/// </summary>
public sealed class TraceLogService : ITraceLogger, ITraceBacklog, IHostedService, IAsyncDisposable
{
    private const int MaxBatch = 256;

    // 카멜케이스 + 대소문자 무시(파일 JSON write/read 대칭). enum·DateTimeOffset 기본 직렬화.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy       = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition     = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };

    private readonly Channel<TraceRecord> _ch = Channel.CreateUnbounded<TraceRecord>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly TraceLogOptions             _opt;
    private readonly ILogger<TraceLogService>    _log;

    private Logger?                  _fileLogger;   // 전용 Serilog 로거(전역 Log.Logger 와 격리).
    private CancellationTokenSource? _cts;
    private Task?                    _consumeTask;
    private int                      _stopped;
    private volatile bool            _fileWritable;

    /// <inheritdoc/>
    public event Action<TraceRecord>? OnEntry;

    public TraceLogService(IOptions<TraceLogOptions> opt, ILogger<TraceLogService> log)
    {
        _opt = opt.Value;
        _log = log;
    }

    // ── ITraceLogger ────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public void Log(TraceRecord record)
    {
        if (!_opt.Enabled) return;
        // unbounded 채널 TryWrite — 닫히지 않은 한 항상 성공·논블로킹. 실패해도 예외 0(fail-safe).
        if (!_ch.Writer.TryWrite(record))
        {
            try { _log.LogDebug("[trace] enqueue 실패(채널 종료) — 드롭: event={EventNo}", record.EventNo); }
            catch { /* 종료 중 로거 disposed */ }
        }
    }

    // ── IHostedService ────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_opt.Enabled)
        {
            _log.LogInformation("[trace] 전용 추적 로그 비활성(TraceLog:Enabled=false) — sink·컨슈머 미기동");
            return Task.CompletedTask;
        }

        InitFileLogger();

        _cts         = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _consumeTask = Task.Run(() => RunConsumeLoopAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0) return;

        // 채널 완료 → 컨슈머가 잔여분 드레인 후 결정 종료(빈 채널 취소 경쟁 회피 — OperationLogService 교훈).
        _ch.Writer.TryComplete();

        if (_consumeTask is not null)
        {
            try { await _consumeTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception) { }
        }

        if (_cts is not null)
        {
            try { await _cts.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { }
        }

        try { _fileLogger?.Dispose(); } catch { }
        _fileLogger = null;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _cts?.Dispose();
    }

    // ── 전용 Serilog File 싱크 초기화(경로 생성 실패는 fail-safe — file 비활성, relay 는 계속) ──
    private void InitFileLogger()
    {
        try
        {
            System.IO.Directory.CreateDirectory(_opt.Directory);

            var rolling = Enum.TryParse<RollingInterval>(_opt.RollingInterval, ignoreCase: true, out var ri)
                ? ri
                : RollingInterval.Day;

            _fileLogger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.File(
                    path:                   System.IO.Path.Combine(_opt.Directory, _opt.FileNamePattern),
                    rollingInterval:        rolling,
                    rollOnFileSizeLimit:    true,
                    fileSizeLimitBytes:     _opt.FileSizeLimitBytes,
                    retainedFileCountLimit: _opt.RetainedFileCountLimit,
                    shared:                 true,     // 백로그 reader 가 동시 read(FileShare.ReadWrite) 가능.
                    buffered:               false,    // 각 write flush — 백로그 read-back 즉시성.
                    // 라인 = 우리가 만든 문자열 그대로(타임스탬프/레벨 래핑 없음). :l = 문자열 리터럴(따옴표 없음).
                    outputTemplate:         "{TraceLine:l}{NewLine}")
                .CreateLogger();

            _fileWritable = true;
            _log.LogInformation(
                "[trace] 전용 추적 로그 sink 시작 — dir={Dir} file={File} rolling={Rolling} sizeLimit={Size} retain={Retain}",
                _opt.Directory, _opt.FileNamePattern, _opt.RollingInterval, _opt.FileSizeLimitBytes, _opt.RetainedFileCountLimit);
        }
        catch (Exception ex)
        {
            _fileWritable = false;
            _log.LogError(ex,
                "[trace] 전용 로그 디렉터리/파일 초기화 실패 — 파일 기록 비활성(SignalR relay 는 계속). dir={Dir}", _opt.Directory);
        }
    }

    // ── 백그라운드 컨슈머 — 드레인 후 OnEntry 발화 + 파일 기입 ─────────────────
    private async Task RunConsumeLoopAsync(CancellationToken ct)
    {
        var reader = _ch.Reader;
        var buffer = new List<TraceRecord>(MaxBatch);
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                buffer.Clear();
                while (buffer.Count < MaxBatch && reader.TryRead(out var item))
                    buffer.Add(item);
                foreach (var rec in buffer)
                    Emit(rec);
            }
        }
        catch (OperationCanceledException) { /* 종료 — 정상 */ }
        catch (Exception ex)
        {
            try { _log.LogError(ex, "[trace] 컨슈머 루프 예외 — 종료(이후 기록 드롭)"); }
            catch { }
        }

        // 채널 완료 후 잔여 드레인(StopAsync 경로).
        try
        {
            while (reader.TryRead(out var item))
                Emit(item);
        }
        catch { /* 종료 경쟁 — 무시(fail-safe) */ }
    }

    // OnEntry(relay) 발화 후 전용 파일 1줄 기입. 어느 쪽 실패도 다른 쪽·본 동작을 막지 않는다(fail-safe·예외 격리).
    private void Emit(TraceRecord rec)
    {
        var h = OnEntry;
        if (h is not null)
        {
            try { h(rec); }
            catch { /* relay 핸들러 예외 격리 — 스트림 실패가 파일/컨슈머를 막지 않음 */ }
        }

        if (!_fileWritable || _fileLogger is null) return;
        try
        {
            _fileLogger.Information("{TraceLine}", FormatLine(rec));
        }
        catch (Exception ex)
        {
            try { _log.LogWarning(ex, "[trace] 파일 기입 실패(드롭·본 동작 비차단): event={EventNo}", rec.EventNo); }
            catch { }
        }
    }

    // 파일 1줄 = "[N] {json}". 앞머리 [N] = 이벤트 번호 태그(즉시 식별). json = 카멜케이스 구조화(백로그 파싱 대칭).
    private static string FormatLine(TraceRecord rec) =>
        $"[{rec.EventNo}] {JsonSerializer.Serialize(rec, JsonOpts)}";

    // ── ITraceBacklog — 파일 tail 조회(읽기 전용·시드용) ──────────────────────

    /// <inheritdoc/>
    public IReadOnlyList<TraceRecord> Read(int? take, int? eventNo, int? pId, int? cSeq)
    {
        if (!_opt.Enabled) return Array.Empty<TraceRecord>();

        int limit = _opt.ClampTake(take);

        List<string> lines;
        try
        {
            // 디렉터리 부재 → 생성 후 빈 목록(500 금지).
            System.IO.Directory.CreateDirectory(_opt.Directory);
            lines = TailLines(limit * 4);   // 필터 여유분 확보 후 필터·take.
        }
        catch (Exception ex)
        {
            try { _log.LogWarning(ex, "[trace] 백로그 읽기 실패 — 빈 목록 반환. dir={Dir}", _opt.Directory); }
            catch { }
            return Array.Empty<TraceRecord>();
        }

        var result = new List<TraceRecord>(limit);
        foreach (var line in lines)   // TailLines 는 시계열 오름차순(오래된→최신) 반환.
        {
            var rec = TryParse(line);
            if (rec is null) continue;
            if (eventNo is int en && rec.EventNo != en) continue;
            if (pId     is int p  && rec.PId     != p)  continue;
            if (cSeq    is int s  && rec.CSeq    != s)  continue;
            result.Add(rec);
        }

        // 필터 후 최근 limit 개만(시계열 오름차순 유지 — 최신이 마지막).
        if (result.Count > limit)
            result.RemoveRange(0, result.Count - limit);
        return result;
    }

    // 최신 파일들에서 마지막 need 줄을 시계열 오름차순으로 수집(롤링 경계 넘어 채움).
    private List<string> TailLines(int need)
    {
        var files = new System.IO.DirectoryInfo(_opt.Directory)
            .GetFiles(FileGlob())
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ToList();

        var collected = new List<string>(need);
        foreach (var f in files)
        {
            var fileLines = ReadAllLinesShared(f.FullName);
            // 이 파일의 뒤에서부터 필요한 만큼(앞 파일이 더 최신이므로 뒤 파일은 더 오래됨).
            // collected 는 "최신 우선"으로 쌓았다가 마지막에 뒤집어 시계열 오름차순으로 만든다.
            for (int i = fileLines.Count - 1; i >= 0 && collected.Count < need; i--)
                collected.Add(fileLines[i]);
            if (collected.Count >= need) break;
        }
        collected.Reverse();   // 최신-우선 → 시계열 오름차순.
        return collected;
    }

    // "trace-.log" → "trace-*.log"(롤링 파일 매칭). 하이픈 뒤에 날짜/시퀀스가 삽입됨.
    private string FileGlob()
    {
        var name = _opt.FileNamePattern;
        int dot = name.LastIndexOf('.');
        return dot > 0 ? name[..dot] + "*" + name[dot..] : name + "*";
    }

    private static List<string> ReadAllLinesShared(string path)
    {
        var result = new List<string>();
        using var fs = new System.IO.FileStream(
            path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
        using var sr = new System.IO.StreamReader(fs);
        string? line;
        while ((line = sr.ReadLine()) is not null)
            if (line.Length > 0) result.Add(line);
        return result;
    }

    // "[N] {json}" → TraceRecord. 앞머리 태그를 건너뛰고 첫 '{' 부터 파싱(fail-safe — 파싱 실패 줄 skip).
    private static TraceRecord? TryParse(string line)
    {
        try
        {
            int brace = line.IndexOf('{');
            if (brace < 0) return null;
            return JsonSerializer.Deserialize<TraceRecord>(line[brace..], JsonOpts);
        }
        catch { return null; }
    }
}

// ════════════════════════════════════════════════════════════════════════════
// TraceCorrelator — C 흐름(이벤트 4·5·6)에 pId 를 실시간 전파하는 소터별 상관 컨텍스트(싱글톤).
//
// 배경(계약 상관키 결정): 핸드셰이크(HandshakeOrchestrator)는 cSeq 만 알고 pId 를 모른다.
//   RcsController.TriggerSorterHandshake 는 pId·cellNo·chuteNo 를 알지만 cSeq 를 (핸드셰이크 내부
//   Interlocked 증가라) 미리 모른다. 이 상관기가 소터별 직렬 핸드셰이크 전제(SPEC §6 물리 직렬) 하에서
//   pId 를 C 흐름 이벤트에 잇는다:
//     · RcsController 가 ExecuteHandshakeAsync 직전 RegisterHandshake(...) → 소터별 FIFO 등록 + 토큰 반환.
//     · 이벤트 4(HS_C_SENT, cSeq+cellNo 보유) 발화 시 ResolveCSent 가 FIFO head 를 pop(소비 표시)해 pId 를
//       붙이고 cSeq→컨텍스트를 저장(이벤트 5·6 해소용). 소터별 "미결 C"(마지막 CELL_ASSIGN)도 갱신.
//     · 이벤트 5(CELL_ASSIGN write, cSeq 보유) 발화 시 ResolveWrite 가 cSeq→컨텍스트로 pId 해소.
//     · 이벤트 6(C_Flag 1→0, cSeq/pId 미보유 — D1 도 0으로 클리어됨) 발화 시 ResolveClear 가 소터별
//       "미결 C"에서 cSeq/pId/cellNo 해소(소터별 직렬 → 미결 C 유일·모호성 0). 해소 후 정리.
//
// ★ 누수·오귀속 방어(fix): 핸드셰이크가 HS_C_SENT 에 도달하지 못하고 조기 종결하는 경로가 존재한다 —
//   시작 시 OFFLINE / 잔류(arming) 실패 / 안착지연 중 OFFLINE(전부 cSeq 증가 前, SentCSeq==0),
//   그리고 **cSeq 증가 後·HS_C_SENT 前** 의 C_Flag 대기 OFFLINE·CFlagTimeout(SentCSeq≥1). 이때 ResolveCSent
//   가 호출되지 않아 등록 head 가 소비되지 않는다. 이를 방치하면 (a) _pending 무한 증가, (b) FIFO 특성상
//   고아 head 가 매핑을 한 칸 밀어 **다음 성공 핸드셰이크가 이전 피스의 pId 로 오귀속**(완료조건 #6 무력화).
//   → RcsController 는 항상 실행되는 continuation 에서 등록 토큰으로 DiscardPending 을 **무조건** 호출한다.
//     토큰은 소비 플래그(Consumed)를 지녀 idempotent — HS_C_SENT 도달 시 이미 소비됐으면 no-op, 미도달이면
//     그 토큰만 정확히 제거한다(동시 등록된 다음 피스 토큰은 건드리지 않음). SentCSeq 판정에 의존하지 않아
//     cSeq 증가 後 조기종결(CFlagTimeout 등)까지 전부 포섭한다.
//   또한 소터별 _pending 상한(MaxPending)으로 방어 심화 — 폐기 누락이 무한 증가하지 못하게 최오래 항목을
//     WARN 과 함께 축출(Fail Loud). 정상 경로에선 도달하지 않는다.
//
// ⚠ 알려진 한계(계약 수용·스코프 밖): 상관은 **소터별 순차 dispatch(SPEC §6 물리 직렬)** 를 전제한다.
//   등록은 IF-10 HTTP 도착 순서이나 cSeq 는 ExecuteAsync 내부에서 나중에 부여되므로, 한 소터에 IF-10 이
//   **동시(overlap)** 로 오면 pId↔cSeq 가 교차 매핑될 수 있다. 현 코드베이스는 한 소터의 동시 IF-10 을
//   실제로 직렬화하지 않는다(lessons: single-sorter-concurrent-handshake-gap). 동시 IF-10 직렬화는 이
//   스프린트 스코프 밖 — 순차 dispatch 하에서만 상관 정확성이 보장된다.
//
// 절대규칙 #8: 이 클래스는 Wcs.Api 계층 — HandshakeOrchestrator/PlcGateway 는 이것을 모른다(기존
//   콜백만 발화). 컨텍스트 미등록 경로(OpsController 수동 CellAssign 등)는 pId=null best-effort(fail-safe).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>C 핸드셰이크 상관 컨텍스트(소터별). pId 를 C 흐름 이벤트(4·5·6)에 실시간 전파.</summary>
public sealed class TraceCorrelator
{
    // 소터별 미소비 등록 상한(방어 심화). 정상(순차·discard 정상)에선 큐 길이 ≤1. 초과 = discard 누락 신호.
    private const int MaxPending = 32;

    /// <summary>한 핸드셰이크의 상관 컨텍스트(pId·cellNo·chuteNo, 이후 cSeq 확정).</summary>
    public sealed record HandshakeContext(int PId, int CellNo, int ChuteNo, int? CSeq = null);

    // 등록 토큰(가변) — ResolveCSent 가 소비 시 Consumed 표시. DiscardPending 이 미소비 토큰만 제거(idempotent).
    private sealed class PendingReg
    {
        public required int PId;
        public required int CellNo;
        public required int ChuteNo;
        public bool Consumed;
    }

    // 소터별 등록 FIFO(LinkedList — head pop + 토큰 identity 제거 모두 지원). 락으로 직렬화(저빈도).
    private sealed class SorterPending
    {
        public readonly object Lock = new();
        public readonly LinkedList<PendingReg> Queue = new();
    }

    private readonly ConcurrentDictionary<long, SorterPending> _pending = new();
    // 소터별 cSeq→컨텍스트(이벤트 4 저장 → 이벤트 5 조회).
    private readonly ConcurrentDictionary<long, ConcurrentDictionary<int, HandshakeContext>> _byCSeq = new();
    // 소터별 "미결 C"(마지막 CELL_ASSIGN 컨텍스트 — 이벤트 6 해소용).
    private readonly ConcurrentDictionary<long, HandshakeContext> _pendingC = new();

    private readonly ILogger<TraceCorrelator> _log;

    // DI 는 ILogger 를 공급. 단위 테스트는 인자 없이 생성(NullLogger 폴백).
    public TraceCorrelator(ILogger<TraceCorrelator>? log = null)
        => _log = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<TraceCorrelator>.Instance;

    /// <summary>
    /// ExecuteHandshakeAsync 직전 등록(소터별 FIFO). 이벤트 4 에서 pop 되어 pId 를 붙인다. 반환 토큰(opaque)은
    /// 핸드셰이크 종결 continuation 에서 <see cref="DiscardPending"/> 로 넘겨 미도달(HS_C_SENT 前 종결) 시 폐기한다.
    /// </summary>
    public object RegisterHandshake(long destId, int pId, int cellNo, int chuteNo)
    {
        var sp  = _pending.GetOrAdd(destId, static _ => new SorterPending());
        var reg = new PendingReg { PId = pId, CellNo = cellNo, ChuteNo = chuteNo };
        lock (sp.Lock)
        {
            sp.Queue.AddLast(reg);
            // 방어 심화 — 상한 초과 시 최오래 항목 축출(discard 누락 신호이므로 Fail Loud).
            while (sp.Queue.Count > MaxPending)
            {
                var oldest = sp.Queue.First!.Value;
                sp.Queue.RemoveFirst();
                try
                {
                    _log.LogWarning(
                        "[trace] 소터 destId={DestId} 미소비 상관 등록이 상한({Max}) 초과 — 최오래(pId={PId}) 축출. " +
                        "discard 누락 의심(관측 전용 — 본 동작 무영향).", destId, MaxPending, oldest.PId);
                }
                catch { }
            }
        }
        return reg;
    }

    /// <summary>
    /// 핸드셰이크 종결 continuation 에서 호출(무조건). 토큰이 아직 미소비(HS_C_SENT 미도달)면 그 토큰만 정확히
    /// 제거한다. 이미 소비됐으면(정상 진행) no-op — idempotent. SentCSeq 판정 불요(cSeq 증가 後 조기종결도 포섭).
    /// </summary>
    public void DiscardPending(long destId, object? token)
    {
        if (token is not PendingReg reg) return;
        if (!_pending.TryGetValue(destId, out var sp)) return;
        lock (sp.Lock)
        {
            if (!reg.Consumed)
                sp.Queue.Remove(reg);   // identity 제거(LinkedList.Remove(T) — 참조 동일성). 동시 등록 다음 피스 무영향.
        }
    }

    /// <summary>이벤트 4(HS_C_SENT) — FIFO head pop(소비 표시)으로 pId 해소 + cSeq 확정 저장. 미등록이면 pId 미상.</summary>
    public HandshakeContext ResolveCSent(long destId, int cSeq, int cellNo, int chuteNo)
    {
        PendingReg? reg = null;
        if (_pending.TryGetValue(destId, out var sp))
        {
            lock (sp.Lock)
            {
                var node = sp.Queue.First;
                if (node is not null)
                {
                    reg = node.Value;
                    reg.Consumed = true;         // DiscardPending 이 이후 이 토큰을 no-op 처리하도록.
                    sp.Queue.RemoveFirst();
                }
            }
        }

        // cellNo 는 발화 이벤트가 authoritative(등록값과 불일치 시 이벤트 값 채택).
        var ctx = reg is not null
            ? new HandshakeContext(reg.PId, cellNo, chuteNo, cSeq)
            : new HandshakeContext(PId: 0, CellNo: cellNo, ChuteNo: chuteNo, CSeq: cSeq);

        var map = _byCSeq.GetOrAdd(destId, static _ => new ConcurrentDictionary<int, HandshakeContext>());

        // 직전 미결 C(이전 핸드셰이크의 C 가 아직 클리어 관측 안 됨)는 stale — byCSeq 에서 정리(맵 bounded).
        if (_pendingC.TryGetValue(destId, out var prev) && prev.CSeq is int prevSeq && prevSeq != cSeq)
            map.TryRemove(prevSeq, out _);

        map[cSeq]        = ctx;
        _pendingC[destId] = ctx;
        return ctx;
    }

    /// <summary>이벤트 5(CELL_ASSIGN write) — cSeq→컨텍스트 조회. 미상이면 null.</summary>
    public HandshakeContext? ResolveWrite(long destId, int cSeq) =>
        _byCSeq.TryGetValue(destId, out var map) && map.TryGetValue(cSeq, out var ctx) ? ctx : null;

    /// <summary>이벤트 6(C_Flag 1→0) — 소터별 미결 C 에서 해소(소터 직렬 → 유일). 해소 후 정리.</summary>
    public HandshakeContext? ResolveClear(long destId)
    {
        if (!_pendingC.TryRemove(destId, out var ctx)) return null;
        if (ctx.CSeq is int seq && _byCSeq.TryGetValue(destId, out var map))
            map.TryRemove(seq, out _);
        return ctx;
    }

    /// <summary>소터별 미소비 등록 수(테스트·진단 — 누수 검증).</summary>
    public int PendingCount(long destId)
    {
        if (!_pending.TryGetValue(destId, out var sp)) return 0;
        lock (sp.Lock) { return sp.Queue.Count; }
    }
}

// ════════════════════════════════════════════════════════════════════════════
// TraceWiring — 소터 번들 관측 훅(기존 이벤트) → 전용 sink 발화 결선(이벤트 4·5·6·7·9).
//   SorterRegistryFactory.StartAsync 의 operation_log 구독과 나란히 "추가 구독"(기존 구독 무변경).
//   HandshakeOrchestrator/PlcGateway 는 무접촉 — 기존 (action, detailJson)/(reg,old,new) 콜백만 소비.
//   S-TRACE-READY-PUSH-AND-DEFAULT: reg=="Ready" 델타를 추가 구독해 7(1→0)·9(0→1)를 발화(이벤트 6 무변경).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>번들 관측 훅에 전용 트레이스 발화(이벤트 4·5·6·7·9)를 추가 구독하는 결선 헬퍼.</summary>
public static class TraceWiring
{
    /// <summary>한 소터 번들에 이벤트 4(C 인큐)·5(C 디큐)·6(C 클리어)·7(Ready 1→0)·9(Ready 0→1) 트레이스 발화를 추가 구독한다.</summary>
    public static void Wire(SorterBundleHandle bundle, ITraceLogger trace, TraceCorrelator correlator)
    {
        long destId  = bundle.DestinationId;
        int  chuteNo = bundle.ChuteNo;

        // 이벤트 4 — C 인큐(HandshakeOrchestrator HS_C_SENT: {cellNo,cSeq}).
        bundle.SubscribeHandshakeStage((action, detail) =>
        {
            if (action != "HS_C_SENT") return;
            var (cellNo, cSeq) = ParseCellSeq(detail);
            var ctx = correlator.ResolveCSent(destId, cSeq, cellNo, chuteNo);
            trace.Log(new TraceRecord(
                EventNo: 4, Event: "C_ENQUEUE", At: DateTimeOffset.Now,
                PId: ctx.PId > 0 ? ctx.PId : null, CSeq: cSeq, ChuteNo: chuteNo, DestId: destId,
                CellNo: cellNo, Floor: null, InductionNo: null, Trigger: "HS_C_SENT", Detail: detail));
        });

        // 이벤트 5 — C 디큐(PlcGateway CELL_ASSIGN 실제 write: {cellNo,cSeq,cFlag}).
        bundle.SubscribeWrite((action, detail) =>
        {
            if (action != "CELL_ASSIGN") return;
            var (cellNo, cSeq) = ParseCellSeq(detail);
            var ctx = correlator.ResolveWrite(destId, cSeq);
            trace.Log(new TraceRecord(
                EventNo: 5, Event: "C_DEQUEUE", At: DateTimeOffset.Now,
                PId: (ctx?.PId ?? 0) > 0 ? ctx!.PId : null, CSeq: cSeq, ChuteNo: chuteNo, DestId: destId,
                CellNo: cellNo, Floor: null, InductionNo: null, Trigger: "CELL_ASSIGN", Detail: detail));
        });

        // 이벤트 6 — C 클리어(폴 관측 C_Flag 1→0). D1 도 0이라 델타엔 cSeq 없음 → 미결 C 에서 해소.
        bundle.SubscribeRegisterChange((reg, oldV, newV) =>
        {
            if (reg != "C_Flag" || oldV != 1 || newV != 0) return;
            var ctx = correlator.ResolveClear(destId);
            trace.Log(new TraceRecord(
                EventNo: 6, Event: "C_CLEAR", At: DateTimeOffset.Now,
                PId: (ctx?.PId ?? 0) > 0 ? ctx!.PId : null, CSeq: ctx?.CSeq, ChuteNo: chuteNo, DestId: destId,
                CellNo: ctx?.CellNo, Floor: null, InductionNo: null, Trigger: "C_FLAG_1_TO_0",
                Detail: "{\"reg\":\"C_Flag\",\"old\":1,\"new\":0}"));
        });

        // 이벤트 7·9 — 소터 Ready 워드 전이(폴 관측). 1→0=7(READY_1TO0)·0→1=9(READY_0TO1). 층-scope(피스 상관 없음):
        //   PId/CSeq/CellNo=null, ChuteNo/DestId 세팅, Floor=전이 관측 시점 CurFloor(bundle.Latest — EmitRegisterChanges
        //   는 _latest 갱신 후 발화하므로 cur 스냅샷). 이벤트 6(C_Flag) 구독과 나란히 "추가 구독" — 기존 구독 무변경.
        //   trace.Log=Channel.TryWrite(논블로킹). 발화 예외는 격리(폴 스레드 비차단·fail-safe).
        bundle.SubscribeRegisterChange((reg, oldV, newV) =>
        {
            if (reg != "Ready") return;
            try
            {
                int curFloor = bundle.Latest.CurFloor;
                var rec = BuildReadyEdgeRecord(chuteNo, destId, oldV, newV, curFloor);
                if (rec is not null) trace.Log(rec);
            }
            catch { /* 관측 훅 예외 격리 — 폴 루프 보존(fail-safe) */ }
        });
    }

    /// <summary>
    /// Ready 워드 에지(oldV→newV)를 이벤트 7(1→0)·9(0→1) TraceRecord 로 매핑(순수·부수효과 0). 그 외 조합은 null.
    /// TraceWiring.Wire 의 reg=="Ready" 핸들러가 소비 — 발화 로직을 I/O 무의존 함수로 분리해 결정적으로 테스트한다.
    /// </summary>
    public static TraceRecord? BuildReadyEdgeRecord(int chuteNo, long destId, int oldV, int newV, int curFloor)
    {
        int eventNo;
        string eventName;
        if (oldV == 1 && newV == 0)      { eventNo = 7;  eventName = "READY_1TO0"; }
        else if (oldV == 0 && newV == 1) { eventNo = 9;  eventName = "READY_0TO1"; }
        else return null;   // Ready 는 0/1 만 — 그 외 조합은 발화 안 함(방어).

        return new TraceRecord(
            EventNo: eventNo, Event: eventName, At: DateTimeOffset.Now,
            PId: null, CSeq: null, ChuteNo: chuteNo, DestId: destId, CellNo: null,
            Floor: curFloor, InductionNo: null, Trigger: "READY_EDGE",
            Detail: $"{{\"reg\":\"Ready\",\"old\":{oldV},\"new\":{newV},\"curFloor\":{curFloor}}}");
    }

    // "{...cellNo:X,cSeq:Y...}" → (cellNo, cSeq). 파싱 실패는 (0,0)(fail-safe).
    private static (int cellNo, int cSeq) ParseCellSeq(string detail)
    {
        try
        {
            using var doc = JsonDocument.Parse(detail);
            var root = doc.RootElement;
            int cellNo = root.TryGetProperty("cellNo", out var c) && c.TryGetInt32(out var cv) ? cv : 0;
            int cSeq   = root.TryGetProperty("cSeq",   out var s) && s.TryGetInt32(out var sv) ? sv : 0;
            return (cellNo, cSeq);
        }
        catch { return (0, 0); }
    }
}
