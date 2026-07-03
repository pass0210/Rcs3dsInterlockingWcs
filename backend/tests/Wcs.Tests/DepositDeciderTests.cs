using Wcs.Core;
using Xunit;

namespace Wcs.Tests;

/// <summary>
/// 3D 소터 정렬·준비 판정 — 이 테스트가 스펙이다(RCS↔WCS 재설계 Phase 1).
///
/// 전환: 구 IF-08 가부(allowed/WRONG_FLOOR·agvFloor 비교)는 폐지.
///   새 의미는 2층 고정 운영(operationalFloor) — Ready/정렬/핑퐁 차단/Hold/Offline.
///   operationalFloor=2 고정으로 호출(설정값은 호출자가 주입; 테스트는 2를 명시).
/// </summary>
public class DepositDeciderTests
{
    // 운영층 — 재설계 기준값(설정 OperationalFloor 기본 2). 테스트 상수로 명시.
    private const int OperFloor = 2;

    private static PlcSnapshot Snap(bool ready, int cur, int tgt, bool online = true) =>
        new(0, 0, 0, 0, CFlag: false, RFlag: false, Ready: ready,
            CurFloor: cur, TgtFloor: tgt, Online: online, At: DateTimeOffset.UtcNow);

    // 행1 — Ready=1 · CurFloor==운영층 → Ready(받을 수 있음), 쓰기 없음
    [Fact]
    public void Row1_Ready_AtOperationalFloor_IsReady()
    {
        var d = DepositDecider.Decide(Snap(ready: true, cur: OperFloor, tgt: 0), OperFloor, WcsHold.None);
        Assert.True(d.Ready);
        Assert.Equal(DenyReason.None, d.Reason);
        Assert.False(d.WriteTgtFloor);
    }

    // 행2 — Ready=1 · CurFloor≠운영층 · TgtFloor==0 → NOT_ALIGNED + 운영층 정렬 기입
    [Fact]
    public void Row2_NotAligned_TgtZero_WritesOperationalFloor()
    {
        var d = DepositDecider.Decide(Snap(true, cur: 1, tgt: 0), OperFloor, WcsHold.None);
        Assert.False(d.Ready);
        Assert.Equal(DenyReason.NotAligned, d.Reason);
        Assert.True(d.WriteTgtFloor);
        Assert.Equal(OperFloor, d.TgtFloorValue);
    }

    // 행3 — 미정렬이지만 TgtFloor≠0(정렬 진행 중) → 덮어쓰지 않음(핑퐁 차단)
    [Fact]
    public void Row3_NotAligned_TgtBusy_DoesNotOverwrite()
    {
        var d = DepositDecider.Decide(Snap(true, cur: 1, tgt: OperFloor), OperFloor, WcsHold.None);
        Assert.False(d.Ready);
        Assert.Equal(DenyReason.NotAligned, d.Reason);
        Assert.False(d.WriteTgtFloor);
    }

    // 행4a — Ready=0(분류 중·이동 중) · TgtFloor==0 → BUSY + 운영층 복귀 선기입
    [Fact]
    public void Row4_Busy_TgtZero_PrewritesOperationalFloor()
    {
        var d = DepositDecider.Decide(Snap(ready: false, cur: 1, tgt: 0), OperFloor, WcsHold.None);
        Assert.False(d.Ready);
        Assert.Equal(DenyReason.Busy, d.Reason);
        Assert.True(d.WriteTgtFloor);
        Assert.Equal(OperFloor, d.TgtFloorValue);
    }

    // 행4b — CurFloor==운영층이어도 Ready=0이면 BUSY(받을 수 없음) + 선기입 동일 규칙
    [Fact]
    public void Row4_Busy_AtOperationalFloor_StillBusy()
    {
        var d = DepositDecider.Decide(Snap(false, cur: OperFloor, tgt: 0), OperFloor, WcsHold.None);
        Assert.False(d.Ready);
        Assert.Equal(DenyReason.Busy, d.Reason);
        Assert.True(d.WriteTgtFloor);
        Assert.Equal(OperFloor, d.TgtFloorValue);
    }

    // 행5 — Ready=0 · TgtFloor≠0 → BUSY, 쓰기 없음(진행 중)
    [Fact]
    public void Row5_Busy_TgtBusy_NoWrite()
    {
        var d = DepositDecider.Decide(Snap(false, cur: 1, tgt: OperFloor), OperFloor, WcsHold.None);
        Assert.False(d.Ready);
        Assert.Equal(DenyReason.Busy, d.Reason);
        Assert.False(d.WriteTgtFloor);
    }

    // 행6 — FULL/PAUSED는 Ready·층과 무관하게 차단 + 쓰기 금지(장기 차단, 점유 방지)
    [Theory]
    [InlineData(WcsHold.Full, DenyReason.Full)]
    [InlineData(WcsHold.Paused, DenyReason.Paused)]
    public void Row6_Hold_Denies_WithoutWrite(WcsHold hold, DenyReason expected)
    {
        var d = DepositDecider.Decide(Snap(true, cur: 1, tgt: 0), OperFloor, hold);
        Assert.False(d.Ready);
        Assert.Equal(expected, d.Reason);
        Assert.False(d.WriteTgtFloor);
    }

    // 행7 — OFFLINE이 최우선: Hold·Ready·층 전부 무시, 쓰기 금지
    [Fact]
    public void Row7_Offline_OverridesEverything()
    {
        var d = DepositDecider.Decide(Snap(true, cur: OperFloor, tgt: 0, online: false), OperFloor, WcsHold.Full);
        Assert.False(d.Ready);
        Assert.Equal(DenyReason.Offline, d.Reason);
        Assert.False(d.WriteTgtFloor);
    }

    // C1: 행1 경계 — TgtFloor≠0(이동완료 후 잔류) 상태에서도 운영층 일치·Ready=1이면 Ready, 쓰기 없음
    [Fact]
    public void C1_Row1_TgtFloorResidual_StillReady()
    {
        // TgtFloor=운영층(잔류)이지만 이미 CurFloor==운영층, Ready=1 → Ready, TgtFloor 건드리지 않음
        var d = DepositDecider.Decide(Snap(ready: true, cur: OperFloor, tgt: OperFloor), OperFloor, WcsHold.None);
        Assert.True(d.Ready);
        Assert.Equal(DenyReason.None, d.Reason);
        Assert.False(d.WriteTgtFloor);
    }

    // C2: 행4/6/7 — Hold(Full/Paused) 또는 Offline이면 Ready=0·TgtFloor==0이어도 쓰기 금지
    [Theory]
    [InlineData(true,  WcsHold.Full,   DenyReason.Full)]
    [InlineData(true,  WcsHold.Paused, DenyReason.Paused)]
    [InlineData(false, WcsHold.None,   DenyReason.Offline)]
    public void C2_HoldOrOffline_BlocksTgtFloorWrite(bool online, WcsHold hold, DenyReason expected)
    {
        // ready:false, cur:1, tgt:0 → TgtFloor 쓰기 조건(TgtFloor==0 && Ready==0)을 충족하지만
        // Hold/Offline이 선행 우선순위로 차단 → WriteTgtFloor=false
        var snap = new PlcSnapshot(0, 0, 0, 0, CFlag: false, RFlag: false, Ready: false,
            CurFloor: 1, TgtFloor: 0, Online: online, At: DateTimeOffset.UtcNow);
        var d = DepositDecider.Decide(snap, OperFloor, hold);
        Assert.False(d.Ready);
        Assert.Equal(expected, d.Reason);
        Assert.False(d.WriteTgtFloor);
    }

    // C3: 행6 강한 경계 — 운영층 일치·Ready=1이어도 Hold가 우선, WriteTgtFloor=false
    [Fact]
    public void C3_HoldOverridesReadyAndFloorMatch()
    {
        // CurFloor==운영층, Ready=1, TgtFloor=0 → 행1 조건 충족하지만 Hold=Full이 우선
        var d = DepositDecider.Decide(Snap(ready: true, cur: OperFloor, tgt: 0), OperFloor, WcsHold.Full);
        Assert.False(d.Ready);
        Assert.Equal(DenyReason.Full, d.Reason);
        Assert.False(d.WriteTgtFloor);
    }

    // 와이어 포맷 — 내부 사유 문자열 고정(RCS 미전송, piece_event 기록용)
    [Fact]
    public void Wire_Strings_AreStable()
    {
        Assert.Null(DenyReason.None.ToWire());
        Assert.Equal("NOT_ALIGNED", DenyReason.NotAligned.ToWire());
        Assert.Equal("BUSY", DenyReason.Busy.ToWire());
        Assert.Equal("FULL", DenyReason.Full.ToWire());
        Assert.Equal("PAUSED", DenyReason.Paused.ToWire());
        Assert.Equal("OFFLINE", DenyReason.Offline.ToWire());
    }
}
