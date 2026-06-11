using Wcs.Core;
using Xunit;

namespace Wcs.Tests;

/// <summary>docs/SPEC.md §2 판정 표 — 이 테스트가 스펙이다. M1에서 전부 GREEN으로.</summary>
public class DepositDeciderTests
{
    private static PlcSnapshot Snap(bool ready, int cur, int tgt, bool online = true) =>
        new(0, 0, 0, 0, CFlag: false, RFlag: false, Ready: ready,
            CurFloor: cur, TgtFloor: tgt, Online: online, At: DateTimeOffset.UtcNow);

    // 행1 — Ready=1 · 층 일치 → 허가, 쓰기 없음
    [Fact]
    public void Row1_Ready_FloorMatch_Allows()
    {
        var d = DepositDecider.Decide(Snap(ready: true, cur: 1, tgt: 0), agvFloor: 1, WcsHold.None);
        Assert.True(d.Allowed);
        Assert.Equal(DenyReason.None, d.Reason);
        Assert.False(d.WriteTgtFloor);
    }

    // 행2 — Ready=1 · 층 다름 · TgtFloor==0 → WRONG_FLOOR + agvFloor 기입
    [Fact]
    public void Row2_FloorDiffer_TgtZero_WritesTgtFloor()
    {
        var d = DepositDecider.Decide(Snap(true, cur: 2, tgt: 0), agvFloor: 1, WcsHold.None);
        Assert.False(d.Allowed);
        Assert.Equal(DenyReason.WrongFloor, d.Reason);
        Assert.True(d.WriteTgtFloor);
        Assert.Equal(1, d.TgtFloorValue);
    }

    // 행3 — 층 다름이지만 TgtFloor≠0(이동 명령 진행 중) → 덮어쓰지 않음(핑퐁 차단)
    [Fact]
    public void Row3_FloorDiffer_TgtBusy_DoesNotOverwrite()
    {
        var d = DepositDecider.Decide(Snap(true, cur: 2, tgt: 1), agvFloor: 1, WcsHold.None);
        Assert.False(d.Allowed);
        Assert.Equal(DenyReason.WrongFloor, d.Reason);
        Assert.False(d.WriteTgtFloor);
    }

    // 행4a — Ready=0(분류 중·이동 중) · TgtFloor==0 → BUSY + 복귀 선기입
    [Fact]
    public void Row4_Busy_TgtZero_PrewritesReturnFloor()
    {
        var d = DepositDecider.Decide(Snap(ready: false, cur: 2, tgt: 0), agvFloor: 1, WcsHold.None);
        Assert.False(d.Allowed);
        Assert.Equal(DenyReason.Busy, d.Reason);
        Assert.True(d.WriteTgtFloor);
        Assert.Equal(1, d.TgtFloorValue);
    }

    // 행4b — 층이 같아도 Ready=0이면 BUSY(받을 수 없음) + 선기입은 동일 규칙
    [Fact]
    public void Row4_Busy_SameFloor_StillBusy()
    {
        var d = DepositDecider.Decide(Snap(false, cur: 1, tgt: 0), agvFloor: 1, WcsHold.None);
        Assert.False(d.Allowed);
        Assert.Equal(DenyReason.Busy, d.Reason);
        Assert.True(d.WriteTgtFloor);
        Assert.Equal(1, d.TgtFloorValue);
    }

    // 행5 — Ready=0 · TgtFloor≠0 → BUSY, 쓰기 없음
    [Fact]
    public void Row5_Busy_TgtBusy_NoWrite()
    {
        var d = DepositDecider.Decide(Snap(false, cur: 2, tgt: 1), agvFloor: 1, WcsHold.None);
        Assert.False(d.Allowed);
        Assert.Equal(DenyReason.Busy, d.Reason);
        Assert.False(d.WriteTgtFloor);
    }

    // 행6 — FULL/PAUSED는 Ready·층과 무관하게 거부 + 쓰기 금지(장기 차단, 점유 방지)
    [Theory]
    [InlineData(WcsHold.Full, DenyReason.Full)]
    [InlineData(WcsHold.Paused, DenyReason.Paused)]
    public void Row6_Hold_Denies_WithoutWrite(WcsHold hold, DenyReason expected)
    {
        var d = DepositDecider.Decide(Snap(true, cur: 2, tgt: 0), agvFloor: 1, hold);
        Assert.False(d.Allowed);
        Assert.Equal(expected, d.Reason);
        Assert.False(d.WriteTgtFloor);
    }

    // 행7 — OFFLINE이 최우선: Hold·Ready·층 전부 무시, 쓰기 금지
    [Fact]
    public void Row7_Offline_OverridesEverything()
    {
        var d = DepositDecider.Decide(Snap(true, cur: 1, tgt: 0, online: false), agvFloor: 1, WcsHold.Full);
        Assert.False(d.Allowed);
        Assert.Equal(DenyReason.Offline, d.Reason);
        Assert.False(d.WriteTgtFloor);
    }

    // 와이어 포맷 — API 응답 reason 문자열 고정
    [Fact]
    public void Wire_Strings_AreStable()
    {
        Assert.Null(DenyReason.None.ToWire());
        Assert.Equal("WRONG_FLOOR", DenyReason.WrongFloor.ToWire());
        Assert.Equal("BUSY", DenyReason.Busy.ToWire());
        Assert.Equal("FULL", DenyReason.Full.ToWire());
        Assert.Equal("PAUSED", DenyReason.Paused.ToWire());
        Assert.Equal("OFFLINE", DenyReason.Offline.ToWire());
    }
}
