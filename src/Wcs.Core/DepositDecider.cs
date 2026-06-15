namespace Wcs.Core;

/// <summary>
/// IF-08 투입 가부 판정 — 순수 함수(I/O 금지). 스펙: docs/SPEC.md §2 표 7행 = tests/DepositDeciderTests.
///
/// 우선순위: Offline → Hold(Full/Paused) → Ready/층 비교.
/// TgtFloor 쓰기 조건: TgtFloor==0 && (CurFloor!=agvFloor || Ready==0). Hold/Offline이면 절대 안 씀.
/// WCS는 TgtFloor를 클리어하지 않는다(PLC가 분류 시작 시 클리어).
/// </summary>
public static class DepositDecider
{
    public static DepositDecision Decide(PlcSnapshot snap, int agvFloor, WcsHold hold)
    {
        // 우선순위 1: Offline (행7)
        if (!snap.Online)
            return DepositDecision.Deny(DenyReason.Offline);

        // 우선순위 2: Hold — Full / Paused (행6). TgtFloor 쓰기 금지.
        if (hold == WcsHold.Full)
            return DepositDecision.Deny(DenyReason.Full);
        if (hold == WcsHold.Paused)
            return DepositDecision.Deny(DenyReason.Paused);

        // 우선순위 3: Ready / 층 비교 (행1~5)
        if (snap.Ready)
        {
            if (snap.CurFloor == agvFloor)
            {
                // 행1: Online && Hold=None && Ready=1 && CurFloor==agvFloor → 허가 (TgtFloor 무관)
                return DepositDecision.Allow();
            }
            else
            {
                // 행2/3: Ready=1 && CurFloor≠agvFloor
                // TgtFloor==0이면 agvFloor 기입(행2), 아니면 핑퐁 차단(행3)
                int? write = snap.TgtFloor == 0 ? agvFloor : null;
                return DepositDecision.Deny(DenyReason.WrongFloor, write);
            }
        }
        else
        {
            // 행4/5: Ready=0 (분류 중·이동 중)
            // TgtFloor==0이면 복귀 선기입(행4), 아니면 쓰기 없음(행5)
            int? write = snap.TgtFloor == 0 ? agvFloor : null;
            return DepositDecision.Deny(DenyReason.Busy, write);
        }
    }
}
