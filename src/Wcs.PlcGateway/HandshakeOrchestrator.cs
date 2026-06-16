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
}

/// <summary>C/R 핸드셰이크 1건 결과. 성공/실패·사유·대사 정보 포함.</summary>
public sealed record HandshakeResult(
    HandshakeOutcome Outcome,
    int              SentCSeq,
    int              ReceivedRSeq,
    int              ReceivedRCellNo,
    string           Detail)
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

    public HandshakeOrchestrator(
        PlcPollingService gw,
        PlcGatewayOptions opt,
        ILogger<HandshakeOrchestrator>? log = null)
    {
        _gw  = gw;
        _opt = opt;
        _log = log ?? NullLogger<HandshakeOrchestrator>.Instance;
    }

    // ── 핸드셰이크 1건 진행점 ────────────────────────────────────────────────

    /// <summary>
    /// 셀 지정 → R 완료 대기 → 대사 → 클리어의 완전한 핸드셰이크 1건 수행.
    /// 테스트에서 결과(성공/불일치/타임아웃) 관찰 가능.
    /// </summary>
    public async Task<HandshakeResult> ExecuteAsync(int cellNo, CancellationToken ct = default)
    {
        // OFFLINE 사전 확인
        var snap = _gw.Latest;
        if (!snap.Online)
        {
            _log.LogWarning("[핸드셰이크] OFFLINE — 시작 불가");
            return new(HandshakeOutcome.Offline, 0, 0, 0, "OFFLINE at handshake start");
        }

        // C_Seq 증가
        int cSeq = Interlocked.Increment(ref _cSeq);

        _log.LogInformation("[핸드셰이크] 시작: CellNo={CellNo} C_Seq={Seq}", cellNo, cSeq);

        // ── C단계: C_Flag==0 대기 후 CellAssign 큐 투입 ─────────────────────

        var cflagResult = await WaitCFlagZeroAsync(cSeq, ct).ConfigureAwait(false);
        if (cflagResult is not null) return cflagResult;

        // CellAssign 큐 투입 (쓰기는 큐 경유 — 직접 Modbus 호출 금지)
        await _gw.EnqueueAsync(new PlcWrite.CellAssign(cellNo, cSeq), ct).ConfigureAwait(false);
        _log.LogInformation("[핸드셰이크] C 큐 투입: CellNo={CellNo} C_Seq={Seq}", cellNo, cSeq);

        // ── R단계: R_Flag 폴링 → 타임아웃 → 대사 → 클리어 ──────────────────

        return await WaitRFlagAndProcessAsync(cellNo, cSeq, ct).ConfigureAwait(false);
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

        while (true)
        {
            // R_Flag 감지는 RFlagRaised 채널 또는 직접 폴링으로 확인
            // 여기서는 폴링 방식 — 심플하고 결정적
            await Task.Delay(_opt.RFlagPollMs, ct).ConfigureAwait(false);

            var snap = _gw.Latest;

            // OFFLINE 체크
            if (!snap.Online)
            {
                _log.LogError("[핸드셰이크] R 폴링 중 OFFLINE");
                return new(HandshakeOutcome.Offline, cSeq, 0, 0, "OFFLINE during R_Flag poll");
            }

            if (snap.RFlag)
            {
                // R_Flag=1 — R 읽기
                int rCellNo = snap.RCellNo;
                int rSeq    = snap.RSeq;

                _log.LogInformation(
                    "[핸드셰이크] R_Flag=1 수신: R_CellNo={RCellNo} R_Seq={RSeq} (기대 C_Seq={CSeq})",
                    rCellNo, rSeq, cSeq);

                // R_Seq == C_Seq 대사
                if (rSeq != cSeq)
                {
                    // 불일치 알람 — ClearR 투입 후 결과 반환
                    _log.LogError(
                        "[핸드셰이크] R_Seq 대사 실패 — R_Seq={RSeq} ≠ C_Seq={CSeq} (유실·중복 의심)",
                        rSeq, cSeq);

                    await _gw.EnqueueAsync(new PlcWrite.ClearR(), ct).ConfigureAwait(false);

                    return new(
                        HandshakeOutcome.RSeqMismatch, cSeq, rSeq, rCellNo,
                        $"R_Seq mismatch: expected={cSeq}, actual={rSeq}");
                }

                // 대사 성공 — ClearR 큐 투입
                await _gw.EnqueueAsync(new PlcWrite.ClearR(), ct).ConfigureAwait(false);
                _log.LogInformation("[핸드셰이크] 성공 — R_Seq={RSeq} 대사 일치, ClearR 큐 투입", rSeq);

                return new(HandshakeOutcome.Success, cSeq, rSeq, rCellNo,
                    $"OK: C_Seq={cSeq} R_Seq={rSeq} R_CellNo={rCellNo}");
            }

            // 타임아웃 확인
            if (DateTimeOffset.Now >= deadline)
            {
                // P1 — RFLAG_TIMEOUT 알람 + PLC 상태 재확인
                var finalSnap = _gw.Latest;
                _log.LogError(
                    "[핸드셰이크] R_Flag 타임아웃 — {Timeout}ms 초과. PLC 상태 재확인: Online={Online} Ready={Ready}",
                    _opt.RFlagTimeoutMs, finalSnap.Online, finalSnap.Ready);

                return new(HandshakeOutcome.RFlagTimeout, cSeq, 0, 0,
                    $"RFLAG_TIMEOUT after {_opt.RFlagTimeoutMs}ms. Online={finalSnap.Online} Ready={finalSnap.Ready}");
            }
        }
    }
}
