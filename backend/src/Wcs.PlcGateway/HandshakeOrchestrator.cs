using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wcs.Core;

namespace Wcs.PlcGateway;

// ════════════════════════════════════════════════════════════════════════════
// 핸드셰이크 결과 타입
// ════════════════════════════════════════════════════════════════════════════

/// <summary>C/R 핸드셰이크 한 건의 결과.</summary>
public enum HandshakeOutcome
{
    /// <summary>성공: R_Seq == C_Seq 대사 일치.</summary>
    Success,

    /// <summary>알람: R_Seq ≠ C_Seq (유실·중복 검출).</summary>
    RSeqMismatch,

    /// <summary>타임아웃: RFlagTimeoutMs 내 R_Flag 미상승.</summary>
    RFlagTimeout,

    /// <summary>OFFLINE: 게이트웨이 Online=false.</summary>
    Offline,

    /// <summary>P2 — C_Flag 대기 타임아웃 (CFlagTimeoutMs 초과).</summary>
    CFlagTimeout,

    /// <summary>
    /// S-HANDSHAKE-RESIDUE — 시작 시 R_Flag=1 잔류를 감지해 ClearR 선행 투입했으나
    /// RFlagClearConfirmTimeoutMs 내 R_Flag==0 확인 실패(ClearR 미반영 — PLC 무ack 등).
    /// C를 기입하지 않고 종결한 terminal outcome(더티 상태 진행 금지·§2C).
    /// </summary>
    RFlagResidueTimeout,
}

/// <summary>C/R 핸드셰이크 1건 결과. 성공/실패·사유·대사 정보 포함.</summary>
/// <remarks>
/// S-TWO-FLOOR-CONTROL C1 — 처리 시각 계측을 위해 <see cref="TiltedAt"/>·<see cref="ReturnedAt"/>를
/// append-only로 추가(기본값 null → 기존 5-인자 생성자 호출 전부 보존, IsSuccess 불변).
/// S-SORT-CYCLE-TIME-METRIC — 분류 시작 시각 <see cref="SortStartedAt"/>(Ready 1→0 관측)를 동일 패턴으로
/// append(기본값 null → 기존 호출부 보존). 평균 사이클 시간 = avg(ReturnedAt − SortStartedAt).
/// depositedAt(3DS 투입=IF-10 보고 시각)은 핸드셰이크가 관측하지 않으므로 result에 담지 않는다
/// (RcsController가 저널에 직접 유입 — 계약 (e)).
/// </remarks>
public sealed record HandshakeResult(
    HandshakeOutcome Outcome,
    int              SentCSeq,
    int              ReceivedRSeq,
    int              ReceivedRCellNo,
    string           Detail,
    // 셀 틸트 시각 = R_Flag==1 관측 시점. 성공·불일치에서 non-NULL, R 미수신(타임아웃/OFFLINE)에서 NULL.
    DateTime?        TiltedAt      = null,
    // 복귀 완료 시각 = Ready 0→1(R 영역 클리어) 관측 시점. 성공(복귀 관측)에서만 non-NULL.
    //   복귀 대기 타임아웃(성공이나 Ready 미관측)·불일치·타임아웃·OFFLINE에서 NULL.
    DateTime?        ReturnedAt    = null,
    // 분류 시작 시각 = C 기입 후 Ready 워드 1→0 전이(에지) 관측 시점(S-SORT-CYCLE-TIME-METRIC). R 폴 루프에서
    //   관측만 추가(폴/타이밍/pop/write-on-clear/PLC write 시퀀스 불변). 에지 미관측(폴 주기보다 빠른 분류)·
    //   R 미수신(타임아웃/OFFLINE before Ready 0 관측)에서 NULL → 그 행은 평균 집계 n에서 자연 제외.
    //   단조 보장: SortStartedAt ≤ TiltedAt(관측 지점에서 클램프).
    DateTime?        SortStartedAt = null)
{
    public bool IsSuccess => Outcome == HandshakeOutcome.Success;
}

// ════════════════════════════════════════════════════════════════════════════
// HandshakeOrchestrator — SPEC §4 C/R 핸드셰이크
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// C/R 핸드셰이크 오케스트레이터 — SPEC §4.
///
/// C(셀 지정): C_Flag==0 확인 → CellAssign 큐 투입(C_CellNo·C_Seq·C_Flag=1)
/// R(완료 대기): R_Flag 폴링(RFlagPollMs) → 타임아웃(RFlagTimeoutMs) →
///   R_Flag=1 시 R 읽기 → R_Seq==C_Seq 대사(불일치=알람) → ClearR 큐 투입.
///
/// 쓰기는 전부 큐 경유 — 오케스트레이터가 직접 Modbus 호출 금지.
/// C_Seq 매 건 증가. 한 건씩 직렬.
/// </summary>
public sealed class HandshakeOrchestrator
{
    private readonly PlcPollingService     _gw;
    private readonly PlcGatewayOptions     _opt;
    private readonly ILogger<HandshakeOrchestrator> _log;

    // C_Seq는 매 건 증가 (0 시작, 첫 건은 1)
    private int _cSeq;

    // ── S-OBSERVABILITY 관측 훅 (부수 기록 전용 — 핸드셰이크 의미·타이밍 0 변경) ────
    // EF 비의존 계층이라 DB를 직접 모른다. (action, detailJson) 콜백만 발화하고
    // Wcs.Api 측 싱크가 operation_log(HANDSHAKE)에 기록한다. 핸들러 예외는 격리(fail-safe).
    /// <summary>핸드셰이크 단계 발화 — (action, detailJson). C투입·R수신·대사·ClearR·타임아웃 전수.</summary>
    public event Action<string, string>? OnStage;

    public HandshakeOrchestrator(
        PlcPollingService gw,
        PlcGatewayOptions opt,
        ILogger<HandshakeOrchestrator>? log = null)
    {
        _gw  = gw;
        _opt = opt;
        _log = log ?? NullLogger<HandshakeOrchestrator>.Instance;
    }

    private void EmitStage(string action, string detailJson)
    {
        try { OnStage?.Invoke(action, detailJson); }
        catch { /* 관측 훅 예외 격리 — 핸드셰이크 보존(fail-safe) */ }
    }

    // ── 핸드셰이크 1건 진행점 ────────────────────────────────────────────────

    /// <summary>
    /// 셀 지정 → R 완료 대기 → 대사 → 클리어의 완전한 핸드셰이크 1건 수행.
    /// 테스트에서 결과(성공/불일치/타임아웃) 관찰 가능.
    /// </summary>
    /// <param name="cellNo">지정할 셀 번호.</param>
    /// <param name="ct">호스트 종료 등 취소 토큰.</param>
    /// <param name="depositedAtUtc">
    /// S-IF10-CWRITE-SETTLE-DELAY — 안착 지연(D2)의 기준 시각(anchor) = IF-10 수신(≈틸트) 시각(UTC).
    /// 선택적(역호환) 파라미터 — 기존 호출부(~20개)는 지정하지 않아 null → anchor=handshake-start(경과 0)로
    /// 폴백하며, SettleDelayMs=0(코드 기본)이면 지연 자체가 생략돼 어떤 경우에도 기존 타이밍이 보존된다.
    /// </param>
    public async Task<HandshakeResult> ExecuteAsync(
        int cellNo, CancellationToken ct = default, DateTime? depositedAtUtc = null)
    {
        // OFFLINE 사전 확인
        var snap = _gw.Latest;
        if (!snap.Online)
        {
            _log.LogWarning("[핸드셰이크] OFFLINE — 시작 불가");
            EmitStage("HS_OFFLINE", "{\"phase\":\"start\"}");
            return new(HandshakeOutcome.Offline, 0, 0, 0, "OFFLINE at handshake start");
        }

        // ── arming: C 기입 전 R_Flag==0 관찰 보장 (잔류 대사 — S-HANDSHAKE-RESIDUE §2A) ──
        // 핵심 불변식: 핸드셰이크 1건은 R_Flag==0을 1회 관찰한 뒤에만 이후 R_Flag==1 상승을
        // 자기 응답으로 수용한다(0 확인 이후의 레벨 읽기 = 에지 감지 등가).
        // 시작 시 R_Flag==1이면 직전 건·PLC 기동 잔류이므로 ClearR 선행 후 R_Flag==0 확인.
        // C를 아직 기입하지 않았으므로(cSeq 미증가) 잔류 대사 실패 시 종결이 곧 "C 미기입".
        var armResult = await ArmRFlagZeroAsync(ct).ConfigureAwait(false);
        if (armResult is not null) return armResult; // 잔류 대사 실패(타임아웃/OFFLINE) → 종결(C 미기입)

        // ── 안착 지연 (S-IF10-CWRITE-SETTLE-DELAY — D1: arming 이후·C 기입 이전) ──────────
        // 3DS PLC는 TiltDelay가 0이라 C를 읽는 즉시 라우팅한다. IF-10 수신 후 C를 지연 없이 쓰면 제품이
        // 물리적으로 안착하기 전에 소터가 움직여 오분류·낙하 위험 → 여기서 SettleDelayMs만큼 안착을 기다린다.
        // arming(읽기·잔류 ClearR)은 소터를 움직이지 않으므로 지연을 그 뒤로 두어도 안전하고, arming이 조기
        // 종결(잔류 타임아웃/OFFLINE)되면 지연을 낭비하지 않는다. cSeq 증가 전이라 지연 중 종결이 곧 "C 미기입".
        var settleResult = await SettleDelayAsync(depositedAtUtc, ct).ConfigureAwait(false);
        if (settleResult is not null) return settleResult; // 지연 중 OFFLINE → 종결(C 미기입·더티 진행 0·D3)

        // C_Seq 증가
        int cSeq = Interlocked.Increment(ref _cSeq);

        _log.LogInformation("[핸드셰이크] 시작: CellNo={CellNo} C_Seq={Seq}", cellNo, cSeq);

        // ── C단계: C_Flag==0 대기 후 CellAssign 큐 투입 ─────────────────────

        var cflagResult = await WaitCFlagZeroAsync(cSeq, ct).ConfigureAwait(false);
        if (cflagResult is not null) return cflagResult;

        // CellAssign 큐 투입 (쓰기는 큐 경유 — 직접 Modbus 호출 금지)
        await _gw.EnqueueAsync(new PlcWrite.CellAssign(cellNo, cSeq), ct).ConfigureAwait(false);
        _log.LogInformation("[핸드셰이크] C 큐 투입: CellNo={CellNo} C_Seq={Seq}", cellNo, cSeq);
        EmitStage("HS_C_SENT", $"{{\"cellNo\":{cellNo},\"cSeq\":{cSeq}}}");

        // ── R단계: R_Flag 폴링 → 타임아웃 → 대사 → 클리어 ──────────────────

        return await WaitRFlagAndProcessAsync(cellNo, cSeq, ct).ConfigureAwait(false);
    }

    // ── 안착 지연 (S-IF10-CWRITE-SETTLE-DELAY — D1/D2/D3/D5) ─────────────────────

    /// <summary>
    /// C(CellAssign) 기입 직전 "안착 지연"을 둔다(arming 이후·C 이전 — D1). 반환값:
    ///   - null: 지연 완료(또는 생략) → C 기입 진행 가능.
    ///   - non-null: 지연 도중 OFFLINE 감지 → C 미기입 종결(더티 진행 0 — D3, Offline outcome).
    ///
    /// 기준(anchor·D2) = IF-10 수신 시각(<paramref name="depositedAtUtc"/>, ≈틸트 시각). 실제 대기 =
    ///   max(0, SettleDelayMs − (지연 지점 도달 − anchor)). anchor가 null이면 handshake-start 기준(경과 0).
    ///   IF-10 수신 이후 지연 지점 도달까지 이미 경과한 시간(DB 기록·셀 선택·번들 조회·OFFLINE 사전확인·
    ///   arming)을 잔여에서 차감해 과대 지연을 막는다. 잔여는 ≥0으로 clamp(S5).
    ///
    /// SettleDelayMs<=0이면 지연을 완전히 생략한다 — 추가 대기 0, 경로 무변경(코드 기본 0 = 현행과 바이트
    ///   동일·회귀 0 — D5). 이 조기 반환 덕에 ~20개 기존 호출부의 타이밍이 보존된다.
    ///
    /// 취소·종결(D3): 대기는 취소 토큰(<paramref name="ct"/> — 현행 stopping)을 존중해 즉시 중단하고
    ///   OperationCanceledException을 전파한다(호스트 종료 시 C 미기입·깔끔 종결). 대기 도중 OFFLINE도
    ///   관찰해 조기 종결(응답성) — 최소 요구인 "C 기입 직전 Online 재확인"(WaitCFlagZeroAsync)의 선반영.
    ///   어느 경우에도 절대규칙 #1 유지 — 지연은 순수 대기이며 큐/Modbus를 건드리지 않는다.
    ///
    /// 경과 계산의 "대기" 구간은 단조 시계(<see cref="Environment.TickCount64"/>)로 재어 벽시계 역행을
    ///   방지한다(D2). anchor→now 초기 경과만 벽시계이며 음수는 0으로 clamp한다.
    /// </summary>
    private async Task<HandshakeResult?> SettleDelayAsync(DateTime? depositedAtUtc, CancellationToken ct)
    {
        int settleMs = _opt.SettleDelayMs;

        // 지연 완전 생략(코드 기본 0) — 추가 대기 0·경로 무변경·회귀 0(D5). Stage 발화도 없음(현행 동일).
        if (settleMs <= 0)
            return null;

        // 잔여 대기 = max(0, SettleDelayMs − (now − anchor)). anchor=null이면 경과 0(전량 대기).
        long remainingMs = settleMs;
        if (depositedAtUtc is DateTime anchor)
        {
            double elapsed = (DateTime.UtcNow - anchor).TotalMilliseconds;
            if (elapsed < 0) elapsed = 0;                 // 벽시계 역행 clamp(D2).
            remainingMs = settleMs - (long)elapsed;
            if (remainingMs < 0) remainingMs = 0;         // 잔여 clamp ≥0(S5 — anchor 경과가 지연 초과).
        }

        EmitStage("HS_SETTLE_WAIT",
            $"{{\"settleMs\":{settleMs},\"remainingMs\":{remainingMs}}}");

        // anchor 경과가 지연을 이미 초과 → 추가 대기 ≈0(S5). C 기입 즉시 진행.
        if (remainingMs == 0)
            return null;

        _log.LogInformation(
            "[핸드셰이크] 안착 지연 — {Remaining}ms 대기(SettleDelayMs={Settle}, anchor=IF-10 수신 시각)",
            remainingMs, settleMs);

        // 단조 시계 기준 잔여 대기. 매 스텝 Online 관찰(OFFLINE 조기 종결)·취소 존중.
        int pollMs = _opt.RFlagPollMs > 0 ? _opt.RFlagPollMs : 50;
        long deadlineTick = Environment.TickCount64 + remainingMs;
        while (true)
        {
            long left = deadlineTick - Environment.TickCount64;
            if (left <= 0)
                return null; // 안착 지연 완료 → C 기입 진행.

            // 지연 도중 OFFLINE — C 미기입 종결(더티 진행 0·D3). WaitCFlagZeroAsync Online 재확인의 선반영.
            var s = _gw.Latest;
            if (!s.Online)
            {
                _log.LogError("[핸드셰이크] 안착 지연 중 OFFLINE — C 미기입 종결");
                EmitStage("HS_OFFLINE", "{\"phase\":\"settleDelay\"}");
                return new(HandshakeOutcome.Offline, 0, 0, 0, "OFFLINE during settle delay");
            }

            // 호스트 종료(ct 취소) → OperationCanceledException 전파(C 미기입·깔끔 종결·D3).
            await Task.Delay((int)Math.Min(left, pollMs), ct).ConfigureAwait(false);
        }
    }

    // ── arming: 시작 시 R_Flag==0 관찰 보장 (잔류 대사 — S-HANDSHAKE-RESIDUE §2A/§2C) ──

    /// <summary>
    /// C 기입 전 R_Flag==0을 보장한다(arming). 반환값:
    ///   - null: R_Flag==0 관찰 완료 → C 기입 진행 가능(깨끗한 경로는 추가 지연 0).
    ///   - non-null: 잔류 대사 실패(R_Flag==0 확인 타임아웃 / 대기 중 OFFLINE) → C 미기입 종결.
    ///
    /// 시작 시 R_Flag==1이면 직전 건·PLC 기동 잔류로 간주:
    ///   (1) WARN 로그 + (2) operation_log(HANDSHAKE) 잔류값 기록(OnStage HS_R_RESIDUE)
    ///   + (3) ClearR 선행 큐 투입(절대규칙 #1 — 큐 경유, 직접 Modbus 호출 금지)
    ///   + (4) 폴링 스냅샷에서 R_Flag==0 확인 대기(RFlagClearConfirmTimeoutMs — appsettings, 절대규칙 #7).
    /// 확인 타임아웃 시 C를 기입하지 않고 RFlagResidueTimeout으로 종결(더티 진행 금지).
    /// </summary>
    private async Task<HandshakeResult?> ArmRFlagZeroAsync(CancellationToken ct)
    {
        var snap = _gw.Latest;

        // 깨끗한 경로 — R_Flag가 이미 0이면 즉시 진행(추가 지연 0). 대기는 잔류 케이스에만(함정 1).
        if (!snap.RFlag)
            return null;

        // ── R_Flag==1 잔류 감지 → 대사 ────────────────────────────────────────
        int rCellNo = snap.RCellNo;
        int rSeq    = snap.RSeq;

        _log.LogWarning(
            "[핸드셰이크] 시작 시 R_Flag=1 잔류 감지 — 대사(ClearR 선행 후 R_Flag==0 확인): R_CellNo={RCellNo} R_Seq={RSeq}",
            rCellNo, rSeq);
        EmitStage("HS_R_RESIDUE",
            $"{{\"rCellNo\":{rCellNo},\"rSeq\":{rSeq},\"phase\":\"arming\"}}");

        // ClearR 선행 큐 투입 — 절대규칙 #1(쓰기=단일 큐 경유).
        await _gw.EnqueueAsync(new PlcWrite.ClearR(), ct).ConfigureAwait(false);
        EmitStage("HS_CLEAR_R",
            $"{{\"rCellNo\":{rCellNo},\"rSeq\":{rSeq},\"outcome\":\"Residue\",\"phase\":\"arming\"}}");

        // R_Flag==0 확인 대기 — 상한은 appsettings(절대규칙 #7). PollIntervalMs>RFlagPollMs 창(함정 2)을
        // 닫기 위해 확인 기준을 폴링 스냅샷 갱신으로 한다.
        var deadline = DateTimeOffset.Now.AddMilliseconds(_opt.RFlagClearConfirmTimeoutMs);
        while (true)
        {
            await Task.Delay(_opt.RFlagPollMs, ct).ConfigureAwait(false);

            var s = _gw.Latest;

            // 대기 중 OFFLINE 감지 → 명확 종결(더티 진행 금지·함정 3).
            if (!s.Online)
            {
                _log.LogError("[핸드셰이크] 잔류 대사 R_Flag==0 확인 대기 중 OFFLINE");
                EmitStage("HS_OFFLINE", $"{{\"phase\":\"armingClearWait\",\"rSeq\":{rSeq}}}");
                return new(HandshakeOutcome.Offline, 0, rSeq, rCellNo,
                    "OFFLINE during R_Flag residue clear confirm wait");
            }

            if (!s.RFlag)
            {
                // 잔류 클리어 확인 — arming 완료(이후 R_Flag==1 상승만 자기 응답으로 수용).
                _log.LogInformation("[핸드셰이크] 잔류 R_Flag==0 확인 — arming 완료, C 기입 진행");
                EmitStage("HS_R_ARMED", $"{{\"phase\":\"arming\",\"clearedRSeq\":{rSeq}}}");
                return null;
            }

            if (DateTimeOffset.Now >= deadline)
            {
                // ClearR 미반영(PLC 무ack 등) — C를 기입하지 않고 terminal outcome 종결(§2C·S5).
                _log.LogError(
                    "[핸드셰이크] 잔류 R_Flag==0 확인 타임아웃 — {Ms}ms 내 ClearR 미반영. C 미기입 종결.",
                    _opt.RFlagClearConfirmTimeoutMs);
                EmitStage("HS_R_RESIDUE_TIMEOUT",
                    $"{{\"rCellNo\":{rCellNo},\"rSeq\":{rSeq},\"timeoutMs\":{_opt.RFlagClearConfirmTimeoutMs}}}");
                return new(HandshakeOutcome.RFlagResidueTimeout, 0, rSeq, rCellNo,
                    $"R_Flag residue not cleared within {_opt.RFlagClearConfirmTimeoutMs}ms " +
                    $"(rCellNo={rCellNo}, rSeq={rSeq}) — C not written");
            }
        }
    }

    // ── C_Flag==0 대기 (P2 CFlagTimeoutMs) ──────────────────────────────────

    private async Task<HandshakeResult?> WaitCFlagZeroAsync(int cSeq, CancellationToken ct)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(_opt.CFlagTimeoutMs);

        while (true)
        {
            var snap = _gw.Latest;

            if (!snap.Online)
            {
                _log.LogError("[핸드셰이크] C_Flag 대기 중 OFFLINE");
                EmitStage("HS_OFFLINE", $"{{\"phase\":\"cflagWait\",\"cSeq\":{cSeq}}}");
                return new(HandshakeOutcome.Offline, cSeq, 0, 0, "OFFLINE during C_Flag wait");
            }

            if (!snap.CFlag)
                return null; // C_Flag=0 확인 → 진행 가능

            // C_Flag=1 대기 중
            if (DateTimeOffset.Now >= deadline)
            {
                // P2 — C_Flag 타임아웃 알람 + 상태 재확인
                _log.LogError(
                    "[핸드셰이크] C_Flag 타임아웃 — {Ms}ms 내 C_Flag=0 안됨. Online={Online} Ready={Ready}",
                    _opt.CFlagTimeoutMs, snap.Online, snap.Ready);
                EmitStage("HS_CFLAG_TIMEOUT", $"{{\"cSeq\":{cSeq},\"timeoutMs\":{_opt.CFlagTimeoutMs}}}");
                return new(HandshakeOutcome.CFlagTimeout, cSeq, 0, 0,
                    $"C_Flag still set after {_opt.CFlagTimeoutMs}ms. Online={snap.Online} Ready={snap.Ready}");
            }

            await Task.Delay(_opt.RFlagPollMs, ct).ConfigureAwait(false);
        }
    }

    // ── R_Flag 폴링 → 대사 → 클리어 ─────────────────────────────────────────

    private async Task<HandshakeResult> WaitRFlagAndProcessAsync(
        int cellNo, int cSeq, CancellationToken ct)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(_opt.RFlagTimeoutMs);

        _log.LogInformation("[핸드셰이크] R_Flag 폴링 시작 (타임아웃={Timeout}ms)", _opt.RFlagTimeoutMs);

        // ── S-SORT-CYCLE-TIME-METRIC: 분류 시작(Ready 1→0) 관측 ──────────────────────
        // C 기입 후 소터가 분류를 시작하면 Ready 워드가 1→0으로 떨어진다(절대규칙 #4 — 0=분류/이동 중).
        // 이 R 폴 루프는 이미 RFlagPollMs 주기로 _gw.Latest를 샘플링하므로 Ready 레벨을 함께 관측만 추가한다
        //   (폴/타이밍/pop/write-on-clear/PLC write 시퀀스 불변 — additive). C 기입 직후 첫 Ready==0 관측을
        //   분류 시작 시각으로 잡는다(정상은 1→0 에지, 폴 주기보다 분류가 짧아 에지를 놓치거나 진입 시 이미
        //   Ready==0이면 "첫 Ready==0 레벨" 폴백 — OQ-3 허용). 최초 1회만 기록.
        DateTime? sortStartedAt = null;

        while (true)
        {
            // R_Flag 감지는 RFlagRaised 채널 또는 직접 폴링으로 확인
            // 여기서는 폴링 방식 — 심플하고 결정적
            await Task.Delay(_opt.RFlagPollMs, ct).ConfigureAwait(false);

            var snap = _gw.Latest;

            // 분류 시작(Ready 1→0/폴백 레벨) 관측 — 최초 Ready==0 1회만 기록(관측 전용·부수효과 0).
            if (sortStartedAt is null && !snap.Ready)
                sortStartedAt = DateTime.UtcNow;

            // OFFLINE 체크
            if (!snap.Online)
            {
                _log.LogError("[핸드셰이크] R 폴링 중 OFFLINE");
                EmitStage("HS_OFFLINE", $"{{\"phase\":\"rPoll\",\"cSeq\":{cSeq}}}");
                return new(HandshakeOutcome.Offline, cSeq, 0, 0, "OFFLINE during R_Flag poll",
                    SortStartedAt: sortStartedAt);
            }

            if (snap.RFlag)
            {
                // R_Flag=1 — R 읽기. 이 순간이 셀 틸트 관측 시점(tiltedAt) — 성공·불일치 공통.
                int rCellNo   = snap.RCellNo;
                int rSeq      = snap.RSeq;
                var tiltedAt  = DateTime.UtcNow;

                // 단조 보장(sortStartedAt ≤ tiltedAt) — 시계 비단조/동일-폴 관측 시 tiltedAt로 클램프
                //   (기존 returnedAt<tiltedAt 클램프와 동형). 미관측(null)은 그대로 둔다(집계 n 제외).
                if (sortStartedAt is DateTime ss && ss > tiltedAt) sortStartedAt = tiltedAt;

                _log.LogInformation(
                    "[핸드셰이크] R_Flag=1 수신: R_CellNo={RCellNo} R_Seq={RSeq} (기대 C_Seq={CSeq})",
                    rCellNo, rSeq, cSeq);
                EmitStage("HS_R_RECV", $"{{\"rCellNo\":{rCellNo},\"rSeq\":{rSeq},\"cSeq\":{cSeq}}}");

                // R_Seq == C_Seq 대사
                if (rSeq != cSeq)
                {
                    // 불일치 알람 — 현행 유지: R_Flag==1 즉시 ClearR(복귀 대기 없음 — 정상 사이클 아님).
                    //   tiltedAt 기입(R_Flag==1 관측), returnedAt=NULL(복귀 미측정) — 계약 (d-i)·(e).
                    _log.LogError(
                        "[핸드셰이크] R_Seq 대사 실패 — R_Seq={RSeq} ≠ C_Seq={CSeq} (유실·중복 의심)",
                        rSeq, cSeq);
                    EmitStage("HS_RSEQ_MISMATCH", $"{{\"expectedCSeq\":{cSeq},\"actualRSeq\":{rSeq},\"rCellNo\":{rCellNo}}}");

                    await _gw.EnqueueAsync(new PlcWrite.ClearR(), ct).ConfigureAwait(false);
                    EmitStage("HS_CLEAR_R", $"{{\"cSeq\":{cSeq},\"outcome\":\"RSeqMismatch\"}}");

                    return new(
                        HandshakeOutcome.RSeqMismatch, cSeq, rSeq, rCellNo,
                        $"R_Seq mismatch: expected={cSeq}, actual={rSeq}",
                        TiltedAt: tiltedAt, ReturnedAt: null, SortStartedAt: sortStartedAt);
                }

                // 대사 성공 — R 영역 클리어를 "R_Flag==1 즉시"가 아니라 "Ready==1(복귀 완료) 관측 후"로
                //   지연한다(SPEC §4·계약 (d-i) 성공 경로 한정). 무-이동 사이클(관측 시점 이미 Ready==1)은
                //   추가 지연 0으로 즉시 clear. 복귀 이동 사이클은 Ready==1까지 R 유지 후 clear + returnedAt 기록.
                EmitStage("HS_RSEQ_MATCH", $"{{\"cSeq\":{cSeq},\"rSeq\":{rSeq},\"rCellNo\":{rCellNo}}}");
                return await WaitReadyThenClearRAsync(cSeq, rCellNo, rSeq, tiltedAt, sortStartedAt, snap, ct)
                    .ConfigureAwait(false);
            }

            // 타임아웃 확인
            if (DateTimeOffset.Now >= deadline)
            {
                // P1 — RFLAG_TIMEOUT 알람 + PLC 상태 재확인
                var finalSnap = _gw.Latest;
                _log.LogError(
                    "[핸드셰이크] R_Flag 타임아웃 — {Timeout}ms 초과. PLC 상태 재확인: Online={Online} Ready={Ready}",
                    _opt.RFlagTimeoutMs, finalSnap.Online, finalSnap.Ready);
                EmitStage("HS_TIMEOUT",
                    $"{{\"cSeq\":{cSeq},\"timeoutMs\":{_opt.RFlagTimeoutMs},\"online\":{(finalSnap.Online ? "true" : "false")}}}");

                return new(HandshakeOutcome.RFlagTimeout, cSeq, 0, 0,
                    $"RFLAG_TIMEOUT after {_opt.RFlagTimeoutMs}ms. Online={finalSnap.Online} Ready={finalSnap.Ready}",
                    SortStartedAt: sortStartedAt);
            }
        }
    }

    // ── 복귀 대기 → Ready==1 관측 후 ClearR (성공 경로 한정 — S-TWO-FLOOR-CONTROL C1) ──

    /// <summary>
    /// R_Seq 대사 성공 후 R 영역 클리어 시점을 Ready==1(복귀 완료)로 지연한다(SPEC §4).
    ///
    /// 절대규칙 #4: Ready(D4.2) = 1(수용가능·비분류·정지) / 0(분류 중 또는 이동 중). 복귀 이동이 남으면
    ///   Sim/PLC는 Ready=0을 유지한 채 이동하고 도착 후에만 Ready=1 → "복귀 완료" 판정 = Ready 0→1 상승.
    ///
    /// 케이스:
    ///   · R_Flag==1 관측 스냅샷에서 이미 Ready==1(무-이동) → 즉시 ClearR(추가 지연 0), returnedAt≈tiltedAt.
    ///   · Ready==0(복귀 이동 중) → RFlagPollMs 주기로 Ready==1 폴링 대기(상한 = ReturnReadyTimeoutMs,
    ///     appsettings·하드코딩 금지·절대규칙 #7). Ready==1 관측 시 ClearR + returnedAt 기록.
    ///   · 복귀 대기 타임아웃(Ready 미복귀 — 소터 정체) → ClearR로 완료 ack(온라인) + 알람 발화 +
    ///     returnedAt=NULL 유지. outcome은 Success(분류 자체는 완료·대사 일치 — 계약 (d-iii)).
    ///   · 대기 중 OFFLINE → 명확 종결(ClearR 불가·현행 offline 패턴), Offline outcome·returnedAt=NULL.
    ///
    /// ClearR는 전부 단일 쓰기 큐 경유(_gw.EnqueueAsync — 절대규칙 #1, 직접 Modbus 호출 0).
    /// </summary>
    private async Task<HandshakeResult> WaitReadyThenClearRAsync(
        int cSeq, int rCellNo, int rSeq, DateTime tiltedAt, DateTime? sortStartedAt,
        PlcSnapshot rFlagSnap, CancellationToken ct)
    {
        // 무-이동 사이클: R_Flag==1 관측 스냅샷에서 이미 Ready==1 → 즉시 clear(추가 지연 0).
        if (rFlagSnap.Ready)
            return await ClearRAndReturnSuccessAsync(cSeq, rCellNo, rSeq, tiltedAt, sortStartedAt, ct).ConfigureAwait(false);

        // 복귀 이동 중(Ready=0) — Ready==1(복귀 완료)까지 R 영역 유지 후 clear.
        EmitStage("HS_RETURN_WAIT",
            $"{{\"cSeq\":{cSeq},\"rSeq\":{rSeq},\"timeoutMs\":{_opt.ReturnReadyTimeoutMs}}}");
        _log.LogInformation(
            "[핸드셰이크] 복귀 대기 시작 — Ready==1까지 R 유지(상한={Ms}ms)", _opt.ReturnReadyTimeoutMs);

        var deadline = DateTimeOffset.Now.AddMilliseconds(_opt.ReturnReadyTimeoutMs);
        while (true)
        {
            await Task.Delay(_opt.RFlagPollMs, ct).ConfigureAwait(false);

            var s = _gw.Latest;

            // 대기 중 OFFLINE — 명확 종결(더티 진행 금지·ClearR 불가). returnedAt=NULL.
            if (!s.Online)
            {
                _log.LogError("[핸드셰이크] 복귀 대기 중 OFFLINE — 명확 종결(returnedAt=NULL)");
                EmitStage("HS_OFFLINE", $"{{\"phase\":\"returnWait\",\"cSeq\":{cSeq},\"rSeq\":{rSeq}}}");
                return new(HandshakeOutcome.Offline, cSeq, rSeq, rCellNo,
                    $"OFFLINE during return wait (cSeq={cSeq}, rSeq={rSeq})",
                    TiltedAt: tiltedAt, ReturnedAt: null, SortStartedAt: sortStartedAt);
            }

            // 복귀 완료(Ready 0→1) — R 클리어 + returnedAt 기록.
            if (s.Ready)
                return await ClearRAndReturnSuccessAsync(cSeq, rCellNo, rSeq, tiltedAt, sortStartedAt, ct).ConfigureAwait(false);

            // 복귀 대기 타임아웃 — Ready 미복귀(소터 정체). ClearR로 완료 ack + 알람 + returnedAt=NULL.
            //   분류 자체는 완료·대사 일치이므로 outcome=Success 유지(계약 (d-iii)·(e)).
            if (DateTimeOffset.Now >= deadline)
            {
                _log.LogError(
                    "[핸드셰이크] 복귀 대기 타임아웃 — {Ms}ms 내 Ready==1 미관측(소터 정체 의심). " +
                    "ClearR ack + 알람 발화, returnedAt=NULL.", _opt.ReturnReadyTimeoutMs);
                EmitStage("HS_RETURN_TIMEOUT",
                    $"{{\"cSeq\":{cSeq},\"rSeq\":{rSeq},\"timeoutMs\":{_opt.ReturnReadyTimeoutMs}}}");
                await _gw.EnqueueAsync(new PlcWrite.ClearR(), ct).ConfigureAwait(false);
                EmitStage("HS_CLEAR_R", $"{{\"cSeq\":{cSeq},\"outcome\":\"ReturnTimeout\"}}");
                return new(HandshakeOutcome.Success, cSeq, rSeq, rCellNo,
                    $"OK(return timeout): C_Seq={cSeq} R_Seq={rSeq} — Ready==1 not observed within " +
                    $"{_opt.ReturnReadyTimeoutMs}ms (returnedAt=NULL)",
                    TiltedAt: tiltedAt, ReturnedAt: null, SortStartedAt: sortStartedAt);
            }
        }
    }

    /// <summary>Ready==1 관측 확정 후 ClearR 큐 투입 + returnedAt 기록(단조 보장). 성공 결과 반환.</summary>
    private async Task<HandshakeResult> ClearRAndReturnSuccessAsync(
        int cSeq, int rCellNo, int rSeq, DateTime tiltedAt, DateTime? sortStartedAt, CancellationToken ct)
    {
        // 단조 보장(depositedAt≤sortStartedAt≤tiltedAt≤returnedAt) — 시계 비단조/즉시-clear 시 tiltedAt로 클램프.
        var returnedAt = DateTime.UtcNow;
        if (returnedAt < tiltedAt) returnedAt = tiltedAt;

        await _gw.EnqueueAsync(new PlcWrite.ClearR(), ct).ConfigureAwait(false);
        _log.LogInformation(
            "[핸드셰이크] 성공 — R_Seq={RSeq} 대사 일치·Ready==1(복귀 완료) 관측 → ClearR 큐 투입", rSeq);
        EmitStage("HS_CLEAR_R", $"{{\"cSeq\":{cSeq},\"outcome\":\"Success\"}}");

        return new(HandshakeOutcome.Success, cSeq, rSeq, rCellNo,
            $"OK: C_Seq={cSeq} R_Seq={rSeq} R_CellNo={rCellNo}",
            TiltedAt: tiltedAt, ReturnedAt: returnedAt, SortStartedAt: sortStartedAt);
    }
}
