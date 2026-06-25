namespace Wcs.Core;

/// <summary>
/// 3D 소터 정렬·준비 판정 — 순수 함수(I/O 금지). 테스트가 스펙이다(tests/DepositDeciderTests).
///
/// 재설계(RCS↔WCS 인터페이스 재설계 Phase 1):
///   AGV는 항상 운영층(예: 2층)에서 수령하므로 소터를 운영층 1개로 고정 정렬한다.
///   구 IF-08 가부(allowed/WRONG_FLOOR)·agvFloor 비교 개념은 소멸.
///
/// 새 의미:
///   - Ready  = online && CurFloor==operationalFloor && Ready==1  (받을 수 있음).
///   - 정렬   = TgtFloor==0 && (CurFloor!=operationalFloor || Ready==0) → TgtFloor=operationalFloor 기입.
///             진행 중(TgtFloor!=0)엔 절대 덮어쓰지 않는다(핑퐁 차단 — 절대규칙 #2).
///             Offline/Hold(Full·Paused)면 쓰지 않는다(선행 우선순위에서 차단).
///   - WCS는 TgtFloor를 클리어하지 않는다(PLC가 분류 시작 시 클리어 — 절대규칙 #3).
///
/// operationalFloor는 호출자가 설정값(OperationalFloor)에서 주입한다 — Core에 하드코딩 금지(절대규칙 #7).
/// </summary>
public static class DepositDecider
{
    /// <summary>
    /// 소터 정렬·준비 판정. <paramref name="operationalFloor"/>는 설정값(기본 2)이며 Core가 추측하지 않는다.
    /// </summary>
    public static DepositDecision Decide(PlcSnapshot snap, int operationalFloor, WcsHold hold)
    {
        // 우선순위 1: Offline — 정렬 쓰기 금지.
        if (!snap.Online)
            return DepositDecision.Deny(DenyReason.Offline);

        // 우선순위 2: Hold(Full/Paused) — 정렬 쓰기 금지.
        if (hold == WcsHold.Full)
            return DepositDecision.Deny(DenyReason.Full);
        if (hold == WcsHold.Paused)
            return DepositDecision.Deny(DenyReason.Paused);

        // 우선순위 3: 정렬·준비 판정.
        if (snap.Ready)
        {
            if (snap.CurFloor == operationalFloor)
            {
                // online && CurFloor==운영층 && Ready==1 → 받을 수 있음(정렬 완료).
                return DepositDecision.Allow();
            }
            else
            {
                // Ready=1이지만 운영층과 다름 → 미정렬.
                // TgtFloor==0이면 운영층으로 정렬 기입, 아니면 핑퐁 차단(이미 정렬 진행 중).
                int? write = snap.TgtFloor == 0 ? operationalFloor : null;
                return DepositDecision.Deny(DenyReason.NotAligned, write);
            }
        }
        else
        {
            // Ready=0 (분류 중·이동 중) → BUSY.
            // TgtFloor==0이면 운영층 복귀 선기입, 아니면 쓰기 없음(진행 중 — 핑퐁 차단).
            int? write = snap.TgtFloor == 0 ? operationalFloor : null;
            return DepositDecision.Deny(DenyReason.Busy, write);
        }
    }
}
