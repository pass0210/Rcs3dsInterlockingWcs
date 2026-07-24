using Microsoft.Extensions.DependencyInjection;
using Wcs.Api;
using Wcs.Core;
using Wcs.Data;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests.E2E;

// ════════════════════════════════════════════════════════════════════════════
// S-TWO-FLOOR-CONTROL C2 — E2E-1: 콜드스타트 복구 크로스레이어 (실 Sim3ds Modbus TCP)
//
//   실 스택(production SorterRegistryFactory + 실 Sim3ds + 실 EF DB + 실 DestinationStatusPusher →
//   FakeChuteStateServer)으로 콜드스타트 복구 2건을 관통 검증한다:
//     M1  S1 레지스터 클리어가 쓰기 큐→Modbus→실 Sim 에 도달(R 잔류·TgtFloor 0, Ready·CurFloor 보존) +
//         그 클리어가 IF-08 부트스트랩 push 보다 먼저(push 수신 시점엔 이미 클리어됨 — 배리어 실증).
//     M2  S2 I-3 재파생: 미완료 SORTER_3D piece 시드 → 기동 시 큐 재파생 → 관측 루프가 그 층으로 정렬
//         (DB piece → 복원 큐 → 관측 루프 → TgtFloor write → 실 Sim 이동). 복원 before 관측.
//
//   (결정적 레지스터-단위 클리어 검증은 StartupClearTests, 재파생 집합·순서는 PendingFloorQueueRestorerTests
//    가 담당. 여기서는 실 Modbus·실 이동으로 크로스레이어 데이터 흐름을 실증한다.)
// ════════════════════════════════════════════════════════════════════════════
[Collection("RealSimSerial")]
public class E2EGroupM_ColdStartRecoveryTests
{
    private readonly ITestOutputHelper _out;
    public E2EGroupM_ColdStartRecoveryTests(ITestOutputHelper output) => _out = output;

    // ── M1: 기동 레지스터 클리어가 실 Sim 에 도달 + 클리어 before IF-08 부트스트랩 push ──────────
    [Fact]
    public async Task M1_StartupClear_ReachesRealSim_BeforeBootstrapPush()
    {
        await using var rcs = await FakeChuteStateServer.StartAsync();

        // 실 Sim3ds — 초기 CurFloor=2, 레거시 단일 호스트 push 활성.
        var factory = new E2EWebApplicationFactory(
            rcsBaseUrl: rcs.BaseUrl,
            initialCurFloor: 2,
            inductionFloorMap: new Dictionary<int, int> { [1] = 1, [2] = 2 });
        await factory.StartSimsAsync();
        await using var _f = factory;

        // ── 잔류 주입(호스트 기동 전): R 잔류(R_CellNo=20·R_Seq=123·R_Flag=1) + TgtFloor=2(==CurFloor, 무이동) ──
        var sim = factory.PrimarySorter.Sim;
        sim.SetRResidue(20, 123);
        sim.SetTgtFloor(2);
        var before = sim.ReadSnapshot();
        Assert.True(before.RFlag && before.RCellNo == 20 && before.TgtFloor == 2, "잔류 주입 확인");

        // ── 호스트 기동(콜드스타트) ──────────────────────────────────────────────
        var client  = factory.CreateClient();
        long destId = factory.PrimarySorter.DestinationId;
        int  chuteNo = factory.PrimarySorter.ChuteNo;

        await E2EWait.UntilAsync(() => factory.IsSorterOnline(destId), 6000, "소터 Online");

        // ── (1) 기동 클리어가 쓰기 큐→Modbus→실 Sim 서버 버퍼에 반영됨을 WCS 폴 스냅샷 read-back 으로 확인 ──
        //   (실 Modbus 읽기 — 크로스레이어. Sim 섀도(_hr)는 SimLoopMs 지연이 있어 WCS 폴 스냅샷으로 관측.)
        //   잔류 R(20/123)·TgtFloor(2)가 0으로, Ready(D4.2)·CurFloor(D5)는 보존. 빈 큐라 관측 루프는 미기입.
        await E2EWait.UntilAsync(() =>
        {
            var s = factory.SorterSnapshot(destId);
            return s is not null && s.Online && !s.RFlag && s.RCellNo == 0 && s.RSeq == 0
                && s.TgtFloor == 0 && s.Ready && s.CurFloor == 2;
        }, 8000, "기동 클리어가 실 Sim 에 반영(R·TgtFloor=0, Ready·CurFloor 보존)");

        // ── (2) IF-08 부트스트랩 push 수신 확인 ─────────────────────────────────────
        //   DestinationStatusPusher 는 StartupClearCompleted(=클리어가 쓰기 컨슈머에서 처리 완료)를 부트스트랩
        //   Observe 직전에 await 한다 → 부트스트랩 push 는 구조적으로 클리어 이후에만 나간다(계약 S3/CC3).
        //   (배리어는 StartupClearTests·RcsPushTests 가 결정적으로 커버. 여기선 실 스택 push 수신을 확인.)
        await E2EWait.UntilAsync(() => rcs.CountFor(chuteNo) >= 1, 8000, "IF-08 부트스트랩 push 수신(클리어 이후)");

        var final = factory.SorterSnapshot(destId)!;
        _out.WriteLine($"[M1] 클리어 반영 + push 수신 — Sim(폴): R_CellNo={final.RCellNo} R_Seq={final.RSeq} " +
                       $"R_Flag={final.RFlag} TgtFloor={final.TgtFloor} Ready={final.Ready} CurFloor={final.CurFloor}");
        Assert.False(final.RFlag);
        Assert.Equal(0, final.TgtFloor);
        Assert.True(final.Ready);
        Assert.Equal(2, final.CurFloor);
    }

    // ── M2: I-3 재파생 — 미완료 piece → 복원 큐 → 관측 루프 → 실 Sim 정렬(복원 before 관측) ───────────
    [Fact]
    public async Task M2_IncompletePieceReDerived_DrivesObserveAlignment_OnRealSim()
    {
        await using var rcs = await FakeChuteStateServer.StartAsync();

        // 실 Sim3ds — 초기 CurFloor=2(미정렬 대상: 재파생 층 F=1). induction 1→층1.
        // seedExtra: 호스트 기동 전에 미완료(RESERVED) SORTER_3D piece 시드(inductionNo=1 → F=1).
        var factory = new E2EWebApplicationFactory(
            rcsBaseUrl: rcs.BaseUrl,
            initialCurFloor: 2,
            inductionFloorMap: new Dictionary<int, int> { [1] = 1, [2] = 2 },
            seedExtra: (db, slots) =>
            {
                long sorterId = slots[0].DestinationId;                 // chuteNo=30
                var  ind1     = db.Inductions.First(i => i.InductionNo == 1);
                db.Pieces.Add(new Piece
                {
                    PId = 27001, IsActive = true, Barcode = "TEST-BARCODE-3", Qty = 1,
                    DestinationId = sorterId, InductionId = ind1.Id, Status = PieceStatus.RESERVED,
                    CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
                });
                db.SaveChanges();
            });
        await factory.StartSimsAsync();
        await using var _f = factory;

        var client  = factory.CreateClient();
        long destId = factory.PrimarySorter.DestinationId;

        await E2EWait.UntilAsync(() => factory.IsSorterOnline(destId), 6000, "소터 Online");

        // ── 재파생 큐가 복원됨(관측 루프가 소비 전 재구성). IF-05 를 한 건도 보내지 않았음에도 큐에 F=1 존재 ──
        var queues = factory.Services.GetRequiredService<SorterPendingFloorQueues>();
        await E2EWait.UntilAsync(() => queues.Snapshot(destId).Count > 0, 6000, "I-3 재파생 큐 복원");
        Assert.Contains(1, queues.Snapshot(destId));   // 복원된 층 F=1(induction 1)

        // ── 관측 루프가 복원된 큐 머리(F=1)로 정렬 → 실 Sim 이동(CurFloor 2→1). 크로스레이어 폐루프 실증 ──
        await E2EWait.UntilAsync(
            () => factory.SorterSnapshot(destId) is { CurFloor: 1, Ready: true }, 8000,
            "재파생 큐가 관측 루프를 구동해 CurFloor=1 정렬");

        _out.WriteLine("[M2] DB RESERVED piece → 재파생 큐[1] → 관측 루프 TgtFloor=1 기입 → 실 Sim CurFloor=1 정렬");
    }
}
