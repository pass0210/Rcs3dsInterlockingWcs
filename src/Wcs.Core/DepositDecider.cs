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
        // TODO(M1): docs/SPEC.md §2 판정 표 구현. 테스트가 GREEN이 될 때까지.
        throw new NotImplementedException("M1: DepositDecider.Decide — see docs/SPEC.md §2");
    }
}
