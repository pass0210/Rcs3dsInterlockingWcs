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

public enum DenyReason { None, WrongFloor, Busy, Full, Paused, Offline }

public static class DenyReasonWire
{
    /// <summary>API 응답용 문자열(docs/SPEC.md §2).</summary>
    public static string? ToWire(this DenyReason r) => r switch
    {
        DenyReason.None       => null,
        DenyReason.WrongFloor => "WRONG_FLOOR",
        DenyReason.Busy       => "BUSY",
        DenyReason.Full       => "FULL",
        DenyReason.Paused     => "PAUSED",
        DenyReason.Offline    => "OFFLINE",
        _ => throw new ArgumentOutOfRangeException(nameof(r)),
    };
}

/// <summary>IF-08 판정 결과. WriteTgtFloor=true면 게이트웨이 쓰기 큐에 TgtFloorValue 투입.</summary>
public sealed record DepositDecision(bool Allowed, DenyReason Reason, bool WriteTgtFloor, int TgtFloorValue)
{
    public static DepositDecision Allow() => new(true, DenyReason.None, false, 0);
    public static DepositDecision Deny(DenyReason reason, int? writeTgtFloor = null) =>
        new(false, reason, writeTgtFloor.HasValue, writeTgtFloor ?? 0);
}
