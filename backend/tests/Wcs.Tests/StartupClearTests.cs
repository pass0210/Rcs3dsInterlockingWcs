using Wcs.Core;
using Wcs.PlcGateway;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// S-TWO-FLOOR-CONTROL C2 S1 — 콜드스타트 레지스터 클리어 (결정적 단위 검증)
//
//   기동 첫 유효 Online 폴에서 WCS 소유 레지스터(C 영역 D0/D1·R 영역 D2/D3·TgtFloor D6)를 0으로
//   위생 초기화하고, Ready(D4.2)·CurFloor(D5)는 보존함을 passive FakeModbusMasterForApi(상태기계 없음)로
//   레지스터 read-back 단언한다(실 Sim 상태기계 경합 배제 — 결정적).
//
//   커버: CC2(정확 레지스터만 0·Ready/CurFloor 보존·RMW 비트 격리) · CC7(단일 큐 #1 경유) ·
//         VS-1(happy) · VS-2(clean no-op·1회만) · VS-3(Ready/CurFloor 보존 경계) · VS-8(큐 경유).
// ════════════════════════════════════════════════════════════════════════════

public sealed class StartupClearTests
{
    private readonly ITestOutputHelper _out;
    public StartupClearTests(ITestOutputHelper output) => _out = output;

    private static PlcGatewayOptions GwOpt() => new()
    {
        Host = "127.0.0.1", Port = 1502,
        PollIntervalMs = 10, OfflineAfterFailures = 3, WriteTimeoutMs = 500,
        RFlagPollMs = 20, RFlagTimeoutMs = 3000, CFlagTimeoutMs = 2000,
    };

    // 폴링 기동 → StartupClear 완료 대기 → 콜백. 콜백엔 "현재 OnWrite 스냅샷" 함수를 넘긴다(라이브 조회).
    // 결정적 teardown(쓰기 큐 완료 후 종료 — teardown 채널 경쟁 회피).
    private async Task RunWithStartupClearAsync(
        FakeModbusMasterForApi master,
        Func<PlcPollingService, Func<IReadOnlyList<(string action, string detail)>>, Task> assert)
    {
        var queue  = new PlcWriteQueue();
        var gw     = new PlcPollingService(GwOpt(), queue, master);
        var writes = new List<(string, string)>();
        var wlock  = new object();
        gw.OnWrite += (a, d) => { lock (wlock) writes.Add((a, d)); };
        IReadOnlyList<(string, string)> Snapshot() { lock (wlock) return writes.ToList(); }

        await gw.StartAsync();
        try
        {
            var done = await Task.WhenAny(gw.StartupClearCompleted, Task.Delay(3000));
            Assert.True(done == gw.StartupClearCompleted, "StartupClearCompleted 타임아웃(3s)");
            await assert(gw, Snapshot);
        }
        finally
        {
            queue.Writer.TryComplete();
            await gw.StopAsync();
            await gw.DisposeAsync();
        }
    }

    // ── VS-1 (happy): 잔류 C/R/TgtFloor 주입 → 클리어 후 전부 0, Ready·CurFloor 보존 ──────────
    [Fact]
    public async Task VS1_Residue_ClearedToZero_ReadyAndCurFloorPreserved()
    {
        var master = new FakeModbusMasterForApi();
        // 잔류 주입: C_CellNo=7·C_Seq=42·C_Flag, R_CellNo=20·R_Seq=123·R_Flag, TgtFloor=2.
        // 보존 대상: Ready=1, CurFloor=2.
        master.SetRegister(RegisterMap.C_CellNo, 7);
        master.SetRegister(RegisterMap.C_Seq, 42);
        master.SetRegister(RegisterMap.R_CellNo, 20);
        master.SetRegister(RegisterMap.R_Seq, 123);
        master.SetRegister(RegisterMap.Flags,
            (ushort)(RegisterMap.D4.Ready | RegisterMap.D4.C_Flag | RegisterMap.D4.R_Flag));
        master.SetCurFloor(2);
        master.SetTgtFloor(2);

        await RunWithStartupClearAsync(master, (gw, snap) =>
        {
            Assert.Equal(0, master.GetRegister(RegisterMap.C_CellNo));   // D0=0
            Assert.Equal(0, master.GetRegister(RegisterMap.C_Seq));      // D1=0
            Assert.Equal(0, master.GetRegister(RegisterMap.R_CellNo));   // D2=0
            Assert.Equal(0, master.GetRegister(RegisterMap.R_Seq));      // D3=0
            Assert.Equal(0, master.GetTgtFloor());                       // D6=0

            ushort flags = master.GetRegister(RegisterMap.Flags);
            Assert.Equal(0, flags & RegisterMap.D4.C_Flag);              // C_Flag=0
            Assert.Equal(0, flags & RegisterMap.D4.R_Flag);              // R_Flag=0
            Assert.NotEqual(0, flags & RegisterMap.D4.Ready);            // Ready 보존=1
            Assert.Equal(2, master.GetRegister(RegisterMap.CurFloor));   // CurFloor(D5) 보존=2

            // CC7/VS-8: 클리어가 큐 컨슈머 경로로 나갔다(OnWrite는 ProcessWriteAsync에서만 발화).
            Assert.Contains(snap(), w => w.action == "STARTUP_CLEAR");
            return Task.CompletedTask;
        });
    }

    // ── VS-3 (경계): Ready=1·CurFloor=2 + 잔류 목표층 → RMW 비트 격리(Ready 보존) ────────────────
    [Fact]
    public async Task VS3_ReadyAndCurFloor_Preserved_WhileFlagsBitsCleared()
    {
        var master = new FakeModbusMasterForApi();
        master.SetRegister(RegisterMap.Flags,
            (ushort)(RegisterMap.D4.Ready | RegisterMap.D4.C_Flag | RegisterMap.D4.R_Flag));
        master.SetCurFloor(2);
        master.SetTgtFloor(1);

        await RunWithStartupClearAsync(master, (gw, snap) =>
        {
            ushort flags = master.GetRegister(RegisterMap.Flags);
            Assert.Equal(RegisterMap.D4.Ready, flags);                   // Ready만 남고 C/R_Flag=0.
            Assert.Equal(2, master.GetRegister(RegisterMap.CurFloor));   // CurFloor 불변.
            Assert.Equal(0, master.GetTgtFloor());                       // TgtFloor=0.
            return Task.CompletedTask;
        });
    }

    // ── VS-3b (경계): Ready=0(BUSY) 주입 시 클리어가 Ready를 set하지 않음(RMW clear-only) ────────
    [Fact]
    public async Task VS3b_ReadyZero_NotAlteredByClear()
    {
        var master = new FakeModbusMasterForApi();
        master.SetRegister(RegisterMap.Flags, RegisterMap.D4.C_Flag);   // Ready=0 + C_Flag 잔류.
        master.SetCurFloor(1);

        await RunWithStartupClearAsync(master, (gw, snap) =>
        {
            ushort flags = master.GetRegister(RegisterMap.Flags);
            Assert.Equal(0, flags & RegisterMap.D4.C_Flag);   // C_Flag clear.
            Assert.Equal(0, flags & RegisterMap.D4.Ready);    // Ready=0 그대로(set 안 함).
            return Task.CompletedTask;
        });
    }

    // ── VS-2 (clean no-op): 전부 0 기동 → 클리어 후에도 0, 기동 클리어는 정확히 1회(폴마다 폭주 0) ──
    [Fact]
    public async Task VS2_CleanStart_NoStateChange_SingleClearOnly()
    {
        var master = new FakeModbusMasterForApi();  // 기본: Ready=1, CurFloor=1, 나머지 0.

        await RunWithStartupClearAsync(master, async (gw, snap) =>
        {
            Assert.Equal(0, master.GetRegister(RegisterMap.C_CellNo));
            Assert.Equal(0, master.GetRegister(RegisterMap.C_Seq));
            Assert.Equal(0, master.GetRegister(RegisterMap.R_CellNo));
            Assert.Equal(0, master.GetRegister(RegisterMap.R_Seq));
            Assert.Equal(0, master.GetTgtFloor());
            Assert.NotEqual(0, master.GetRegister(RegisterMap.Flags) & RegisterMap.D4.Ready);
            Assert.Equal(1, master.GetRegister(RegisterMap.CurFloor));

            // 기동 클리어는 1회만 — 폴이 여러 번 돌아도 STARTUP_CLEAR는 정확히 1건(매 폴 폭주 아님).
            await Task.Delay(100);   // 여러 폴 주기(10ms) 경과.
            Assert.Equal(1, snap().Count(w => w.action == "STARTUP_CLEAR"));
        });
    }
}
