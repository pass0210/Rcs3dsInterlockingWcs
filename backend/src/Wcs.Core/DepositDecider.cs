namespace Wcs.Core;

/// <summary>
/// 3D 소터 정렬·준비 판정 — 순수 함수(I/O 금지). 테스트가 스펙이다(tests/DepositDeciderTests).
///
/// 설계(인덕션 기반 2층 제어 — 2026-07-21, 구 "운영 2층 고정" 대체):
///   목표 층 F는 더 이상 단일 상수(운영층 2)가 아니라 **소터별 pending-floor 큐 머리 피스의 층**
///   (인덕션 파생 1/2)이다. 이 판정은 그 F를 파라미터로 받아 **게이트만** 수행한다 — 층 값이
///   상수 2에서 큐 공급 F로 바뀌었을 뿐 게이트·핑퐁 차단·클리어 금지 규칙은 불변(§2-A/§2-C).
///
/// 판정 의미:
///   - Ready  = online &amp;&amp; CurFloor==F &amp;&amp; Ready==1  (그 층 F에서 받을 수 있음).
///   - 정렬   = TgtFloor==0 &amp;&amp; (CurFloor!=F || Ready==0) → TgtFloor=F 기입.
///             진행 중(TgtFloor!=0)엔 절대 덮어쓰지 않는다(핑퐁 차단 — 절대규칙 #2).
///             Offline/Hold(Full·Paused)면 쓰지 않는다(선행 우선순위에서 차단).
///   - WCS는 TgtFloor를 클리어하지 않는다(PLC가 분류 시작 시 클리어 — 절대규칙 #3).
///
/// floor(F)는 호출자가 주입한다 — Core에 하드코딩 금지(절대규칙 #7). 호출자:
///   · 큐 관측 루프(SorterFloorReturnService)가 큐 머리 층 F를 넘긴다.
///   · IF-05 층 파생은 InductionFloorMap(순수)이 inductionNo→F를 산출.
/// </summary>
public static class DepositDecider
{
    /// <summary>
    /// 소터 정렬·준비 판정. <paramref name="floor"/>는 목표 층 F(인덕션 파생·큐 공급)이며 Core가 추측하지 않는다.
    /// </summary>
    public static DepositDecision Decide(PlcSnapshot snap, int floor, WcsHold hold)
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
            if (snap.CurFloor == floor)
            {
                // online && CurFloor==F && Ready==1 → 받을 수 있음(정렬 완료).
                return DepositDecision.Allow();
            }
            else
            {
                // Ready=1이지만 목표 층 F와 다름 → 미정렬.
                // TgtFloor==0이면 F로 정렬 기입, 아니면 핑퐁 차단(이미 정렬 진행 중).
                int? write = snap.TgtFloor == 0 ? floor : null;
                return DepositDecision.Deny(DenyReason.NotAligned, write);
            }
        }
        else
        {
            // Ready=0 (분류 중·이동 중) → BUSY.
            // TgtFloor==0이면 F 복귀 선기입, 아니면 쓰기 없음(진행 중 — 핑퐁 차단).
            int? write = snap.TgtFloor == 0 ? floor : null;
            return DepositDecision.Deny(DenyReason.Busy, write);
        }
    }
}
