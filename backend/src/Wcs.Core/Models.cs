namespace Wcs.Core;

/// <summary>3DS PLC 레지스터 맵 — 전부 Holding Register(FC03 읽기 / FC06·FC16 쓰기), Coil 미사용.</summary>
public static class RegisterMap
{
    public const ushort C_CellNo = 0; // D0  WCS write — 지정할 셀
    public const ushort C_Seq    = 1; // D1  WCS write — 명령 순번(매 건 증가)
    public const ushort R_CellNo = 2; // D2  PLC write — 실제 적재한 셀
    public const ushort R_Seq    = 3; // D3  PLC write — 처리한 순번(= 받은 C_Seq)
    public const ushort Flags    = 4; // D4  플래그/상태 워드 — 비트 수정은 반드시 RMW
    public const ushort CurFloor = 5; // D5  PLC write — 현재 층(도착 시 기입)
    public const ushort TgtFloor = 6; // D6  WCS write / PLC가 분류 시작 시 0 클리어

    /// <summary>폴링 시 D0~D6 일괄 읽기 길이.</summary>
    public const ushort BlockLength = 7;

    public static class D4
    {
        public const ushort C_Flag = 1 << 0; // WCS set / PLC clear
        public const ushort R_Flag = 1 << 1; // PLC set / WCS clear
        public const ushort Ready  = 1 << 2; // 1=수용 가능(정지·비분류) / 0=분류 중 또는 이동 중
    }
}

/// <summary>폴링 1회의 PLC 상태 스냅샷. Online=false면 폴 타임아웃/소켓 끊김(OFFLINE).</summary>
public sealed record PlcSnapshot(
    int CCellNo, int CSeq, int RCellNo, int RSeq,
    bool CFlag, bool RFlag, bool Ready,
    int CurFloor, int TgtFloor,
    bool Online, DateTimeOffset At)
{
    public static PlcSnapshot FromRegisters(ushort[] d0to6, bool online, DateTimeOffset at)
    {
        if (d0to6.Length < RegisterMap.BlockLength)
            throw new ArgumentException($"need {RegisterMap.BlockLength} registers", nameof(d0to6));
        var f = d0to6[RegisterMap.Flags];
        return new(
            d0to6[RegisterMap.C_CellNo], d0to6[RegisterMap.C_Seq],
            d0to6[RegisterMap.R_CellNo], d0to6[RegisterMap.R_Seq],
            (f & RegisterMap.D4.C_Flag) != 0,
            (f & RegisterMap.D4.R_Flag) != 0,
            (f & RegisterMap.D4.Ready)  != 0,
            d0to6[RegisterMap.CurFloor], d0to6[RegisterMap.TgtFloor],
            online, at);
    }
}

/// <summary>WCS가 자체 판단하는 차단 상태(PLC는 Ready만 제공).</summary>
public enum WcsHold { None, Full, Paused }

/// <summary>
/// 3D 소터가 받을 수 없는 사유(WCS 내부용 — RCS로 전송하지 않음).
/// 재설계: 2층 고정 운영으로 WRONG_FLOOR 개념 소멸 → NotAligned(미정렬)로 대체.
/// </summary>
public enum DenyReason { None, Busy, NotAligned, Full, Paused, Offline }

public static class DenyReasonWire
{
    /// <summary>내부 사유 문자열(piece_event 기록용 — RCS 미전송).</summary>
    public static string? ToWire(this DenyReason r) => r switch
    {
        DenyReason.None       => null,
        DenyReason.Busy       => "BUSY",
        DenyReason.NotAligned => "NOT_ALIGNED",
        DenyReason.Full       => "FULL",
        DenyReason.Paused     => "PAUSED",
        DenyReason.Offline    => "OFFLINE",
        _ => throw new ArgumentOutOfRangeException(nameof(r)),
    };
}

/// <summary>
/// 3D 소터 정렬·준비 판정 결과(순수 함수 산출).
///   - Ready=true: 소터가 지금 받을 수 있음(online && CurFloor==운영층 && Ready==1).
///   - WriteTgtFloor=true: 목표 층 F로 정렬(또는 드리프트 방지 hold)하기 위해 TgtFloorValue(=F)를 쓰기 큐에 투입.
///   - Reason: 받을 수 없는 사유(내부 기록용 — Ready=true면 None).
/// IF-09 도착 정렬 판단(쓸지/값)과 Phase 2 푸시 ready 산출의 공용 재료.
///
/// ★ 푸시 계약(하드 제약 — S-TWO-FLOOR-WRITE-ON-CLEAR): 푸시 소비자(DestinationStatusService.ComputeSorter·
///   DestinationStatusPusher, floor=CurFloor)는 <see cref="Ready"/>·<see cref="Reason"/>만 읽는다. 정렬·드리프트
///   방지를 위한 write-on-clear 변경은 <see cref="WriteTgtFloor"/>·<see cref="TgtFloorValue"/>에만 반영되고
///   Ready/Reason 은 바이트 동일하게 유지된다(정렬돼 있으면 여전히 Ready=true·Reason=None).
/// </summary>
public sealed record DepositDecision(bool Ready, DenyReason Reason, bool WriteTgtFloor, int TgtFloorValue)
{
    /// <summary>
    /// 받을 수 있음(Ready=true·Reason=None). <paramref name="writeTgtFloor"/>가 있으면 정렬 완료 상태에서도
    /// 그 층을 (재)기입한다 — write-on-clear: TgtFloor==0(디폴트층 이동 명령)을 방치하면 캐리지가 드리프트하므로
    /// 같은 층 F를 재기입해 위치를 hold한다. 기본 null(기입 없음 — 종전 <c>Allow()</c> 호환).
    /// </summary>
    public static DepositDecision Allow(int? writeTgtFloor = null) =>
        new(true, DenyReason.None, writeTgtFloor.HasValue, writeTgtFloor ?? 0);
    public static DepositDecision Deny(DenyReason reason, int? writeTgtFloor = null) =>
        new(false, reason, writeTgtFloor.HasValue, writeTgtFloor ?? 0);
}
