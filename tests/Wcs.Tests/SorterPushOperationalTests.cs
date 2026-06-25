using System.Net;
using System.Net.Http.Json;
using System.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api;
using Wcs.Core;
using Wcs.Data;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// S-소터push운영상태 — 소터 IF-08 push ready를 운영상태로 좁힌 결과 검증.
//
// 확정 모델(2단계 게이트 분리):
//   - push ready(IF-08) = decision.Ready = online && CurFloor==운영층 && Ready==1 (운영상태만).
//     ★ SorterFull·PAUSED는 push ready에 영향 없음(만재·정지여도 운영상태 OK면 push ready=true).
//   - IF-05 dispatch = r.Paused + SorterCanAcceptBarcode(셀 기준). r.Ready(운영상태) 미소비.
//
// ground-truth: 실 DB seed(인메모리 SQLite) + 게이트웨이 snapshot(FakeMaster 레지스터) +
//   가짜 RCS 수신 본문(FakeRcsServer push payload). 인메모리 카운터 단독 금지(메타교훈).
//
// 시나리오(계약 §Verification Scenarios):
//   VS-1 online·정렬·Ready=1 → push ready=true
//   VS-2 busy(Ready==0 / 미정렬) → push ready=false (두 하위 케이스)
//   VS-3 offline → push ready=false
//   VS-4 [핵심회귀] 셀 만재(SorterFull=true)인데 운영상태 OK → push ready=true (Full 산출은 유지)
//   VS-5 [핵심회귀] PAUSED인데 운영상태 OK → push ready=true (Paused 산출은 유지)
//   VS-7 IF-05 소터 3축: (a)셀 있으면 offline이어도 OK (b)paused면 NG (c)만재(셀없음)면 NG
//   VS-9 push 멱등 + 만재/paused 전이 무발화: (a)운영상태 전이를 N스레드 동시관찰해도 1건
//        (b)만재·paused 전이만으로는 소터 push 0건(무발화·no-flood)
// ════════════════════════════════════════════════════════════════════════════

public class SorterPushOperationalTests
{
    private readonly ITestOutputHelper _out;
    public SorterPushOperationalTests(ITestOutputHelper output) => _out = output;

    // ── 헬퍼: 조건 폴링(RcsPushTests·SorterCellFullnessTests와 동형) ────────────
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs, string msg, int pollMs = 20)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTimeOffset.Now > deadline)
                Assert.Fail($"WaitUntil 타임아웃({timeoutMs}ms): {msg}");
            await Task.Delay(pollMs);
        }
    }

    private static async Task WaitUntilExactAsync(
        Func<int> countFunc, int expected, int stableCount,
        int timeoutMs, string msg, int pollMs = 30)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        int consecutive = 0;
        while (consecutive < stableCount)
        {
            if (DateTimeOffset.Now > deadline)
                Assert.Fail($"WaitUntilExact 타임아웃({timeoutMs}ms): {msg} (현재={countFunc()}, 기대={expected})");
            if (countFunc() == expected) consecutive++;
            else                         consecutive = 0;
            await Task.Delay(pollMs);
        }
    }

    private static async Task WaitForSnapshotAsync(
        RcsPushWebApplicationFactory factory, Func<PlcSnapshot, bool> condition, int timeoutMs, int pollMs = 20)
    {
        var polling = factory.FakePolling!;
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        while (!condition(polling.Latest))
        {
            if (DateTimeOffset.Now > deadline) Assert.Fail($"WaitForSnapshot 타임아웃({timeoutMs}ms)");
            await Task.Delay(pollMs);
        }
    }

    // ── 헬퍼: 소터 운영상태 정렬(online·CurFloor=운영층·Ready=1) ─────────────────
    private static async Task AlignSorterAsync(RcsPushWebApplicationFactory factory)
    {
        factory.FakeMaster.SetReady(true);
        factory.FakeMaster.SetCurFloor(2);
        factory.FakeMaster.SetTgtFloor(0);
        await WaitForSnapshotAsync(factory, s => s.Online && s.CurFloor == 2 && s.Ready, 5000);
    }

    // ── 헬퍼: 셀 작업수량 도달시켜 SorterFull=true 만들기(SorterCellFullnessTests 헬퍼 재현) ──
    private static void MakeSorterFull(WcsDbContext db, long sorterId)
    {
        var order = db.Orders.First(o => o.DestinationId == sorterId && o.Status == OrderStatus.RUNNING);
        var now   = DateTime.UtcNow;

        foreach (var c in db.Cells.Where(c => c.DestinationId == sorterId).ToList())
            c.Capacity = 5;
        db.SaveChanges();

        // 3 enabled 셀 전부 점유 + 각 작업수량 도달.
        var freeCells = db.Cells
            .Where(c => c.DestinationId == sorterId && c.Enabled
                     && !db.CellAssignments.Any(a => a.CellId == c.Id && a.ReleasedAt == null))
            .ToList();
        foreach (var cell in freeCells)
            db.CellAssignments.Add(new CellAssignment
            {
                CellId = cell.Id, OrderId = order.Id, AssignedAt = now, ReleasedAt = null, CreatedAt = now,
            });
        db.SaveChanges();

        int pid = 60000;
        foreach (var cell in db.Cells.Where(c => c.DestinationId == sorterId && c.Enabled).ToList())
        {
            var piece = new Piece
            {
                PId = ++pid, IsActive = false, Barcode = "FULL-FILL", Qty = 5,
                DepositedAt = now, DestinationId = sorterId, Status = PieceStatus.LOADED,
                CreatedAt = now, UpdatedAt = now,
            };
            db.Pieces.Add(piece);
            db.SaveChanges();
            db.SorterCommands.Add(new SorterCommand
            {
                PieceId = piece.Id, CellId = cell.Id, CSeq = 1, CellNo = cell.CellNo,
                CWrittenAt = now, Status = SorterCommandStatus.COMPLETED, CreatedAt = now,
            });
        }
        db.SaveChanges();
    }

    // ════════════════════════════════════════════════════════════════════════
    // VS-1: 소터 online·정렬·Ready=1 → push ready=true
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task VS1_Sorter_OnlineAlignedReady_PushReadyTrue()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        _ = factory.CreateClient();

        long sorterId    = factory.SorterDestinationId;
        int  sorterChute = factory.SorterChuteNo;
        var  status      = factory.Services.GetRequiredService<IDestinationStatusService>();

        await AlignSorterAsync(factory);

        // 산출 ground-truth: 운영상태 OK → Ready=true·Reason=None.
        await WaitUntilAsync(() => status.Compute(sorterId, DestType.SORTER_3D).Ready, 5000, "운영상태 ready");
        var r = status.Compute(sorterId, DestType.SORTER_3D);
        Assert.True(r.Ready);
        Assert.True(r.Online);
        Assert.Equal(DenyReason.None, r.Reason);

        // push payload(가짜 RCS 수신 본문) ready=true 도달.
        await WaitUntilAsync(() => rcs.LastFor(sorterChute) is { Ready: true }, 6000, "소터 push ready=true 수신");
        Assert.True(rcs.LastFor(sorterChute)!.Ready);
        _out.WriteLine("[VS-1] online·정렬·Ready=1 → push ready=true");
    }

    // ════════════════════════════════════════════════════════════════════════
    // VS-2: 소터 busy → push ready=false. (a)Ready==0 (b)CurFloor≠운영층(미정렬)
    // ════════════════════════════════════════════════════════════════════════
    [Theory]
    [InlineData("ready0")]    // (a) Ready==0(분류 중·이동 중)
    [InlineData("misalign")]  // (b) CurFloor≠운영층(미정렬)
    public async Task VS2_Sorter_Busy_PushReadyFalse(string mode)
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        _ = factory.CreateClient();

        long sorterId    = factory.SorterDestinationId;
        int  sorterChute = factory.SorterChuteNo;
        var  status      = factory.Services.GetRequiredService<IDestinationStatusService>();

        // 먼저 정렬해 ready=true로 만든 뒤 busy 전이를 일으켜야 push 전이(true→false)가 관찰됨.
        await AlignSorterAsync(factory);
        await WaitUntilAsync(() => rcs.LastFor(sorterChute) is { Ready: true }, 6000, "정렬 후 ready=true");

        if (mode == "ready0")
        {
            factory.FakeMaster.SetReady(false);  // 분류 중·이동 중
            await WaitForSnapshotAsync(factory, s => s.Online && !s.Ready, 5000);
        }
        else
        {
            factory.FakeMaster.SetCurFloor(1);   // 미정렬(운영층 2 아님)
            await WaitForSnapshotAsync(factory, s => s.Online && s.CurFloor == 1, 5000);
        }

        await WaitUntilAsync(() => !status.Compute(sorterId, DestType.SORTER_3D).Ready, 5000, "busy → 산출 ready=false");
        var r = status.Compute(sorterId, DestType.SORTER_3D);
        Assert.False(r.Ready);
        Assert.Equal(mode == "ready0" ? DenyReason.Busy : DenyReason.NotAligned, r.Reason);

        await WaitUntilAsync(() => rcs.LastFor(sorterChute) is { Ready: false }, 5000, "busy → push ready=false 수신");
        Assert.False(rcs.LastFor(sorterChute)!.Ready);
        _out.WriteLine($"[VS-2/{mode}] busy → push ready=false (reason={r.Reason})");
    }

    // ════════════════════════════════════════════════════════════════════════
    // VS-3: 소터 offline → push ready=false (Reason=Offline)
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task VS3_Sorter_Offline_PushReadyFalse()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        _ = factory.CreateClient();

        long sorterId    = factory.SorterDestinationId;
        int  sorterChute = factory.SorterChuteNo;
        var  status      = factory.Services.GetRequiredService<IDestinationStatusService>();

        // 정렬해 ready=true → offline 전이로 push ready=false 전이 관찰.
        await AlignSorterAsync(factory);
        await WaitUntilAsync(() => rcs.LastFor(sorterChute) is { Ready: true }, 6000, "정렬 후 ready=true");

        // 게이트웨이 OFFLINE: 읽기 실패 주입 → 연속 실패/HardEx로 snap.Online=false 전이.
        factory.FakeMaster.SetFailReads(true);
        await WaitForSnapshotAsync(factory, s => !s.Online, 6000);

        await WaitUntilAsync(() => !status.Compute(sorterId, DestType.SORTER_3D).Online, 5000, "offline 산출");
        var r = status.Compute(sorterId, DestType.SORTER_3D);
        Assert.False(r.Online);
        Assert.False(r.Ready);
        Assert.Equal(DenyReason.Offline, r.Reason);

        await WaitUntilAsync(() => rcs.LastFor(sorterChute) is { Ready: false }, 5000, "offline → push ready=false 수신");
        Assert.False(rcs.LastFor(sorterChute)!.Ready);
        _out.WriteLine("[VS-3] offline → push ready=false (Reason=Offline)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // VS-4 [핵심회귀]: 셀 만재(SorterFull=true)인데 운영상태 OK → push ready=true.
    //   Compute().Full=true(IF-05/내부 사유 산출 유지) AND Compute().Ready=true(만재가 push에 무영향).
    //   실 sorter_command(COMPLETED) JOIN piece.qty로 만재 ground-truth 구성.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task VS4_Sorter_CellFull_OperationalReady_PushReadyTrue()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        _ = factory.CreateClient();

        long sorterId    = factory.SorterDestinationId;
        int  sorterChute = factory.SorterChuteNo;
        var  status      = factory.Services.GetRequiredService<IDestinationStatusService>();

        await AlignSorterAsync(factory);

        // 만재 ground-truth: 빈셀0 + 전 배정 셀 작업수량 도달.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            MakeSorterFull(db, sorterId);
        }

        await WaitUntilAsync(() => status.Compute(sorterId, DestType.SORTER_3D).Full, 5000, "SorterFull=true ground-truth");
        var r = status.Compute(sorterId, DestType.SORTER_3D);
        Assert.True(r.Full,   "셀 만재 → Full=true(IF-05/내부 사유 산출 유지)");
        Assert.True(r.Ready,  "만재여도 운영상태 OK → push ready=true(핵심 회귀)");
        Assert.Equal(DenyReason.None, r.Reason);

        // push payload ready=true 도달(만재가 ready를 false로 만들지 않음).
        await WaitUntilAsync(() => rcs.LastFor(sorterChute) is { Ready: true }, 6000, "만재여도 push ready=true 수신");
        Assert.True(rcs.LastFor(sorterChute)!.Ready);
        _out.WriteLine("[VS-4] 셀 만재(Full=true)인데 운영상태 OK → push ready=true (Full 산출은 유지)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // VS-5 [핵심회귀]: PAUSED인데 운영상태 OK → push ready=true.
    //   Compute().Paused=true(산출 유지) AND Compute().Ready=true(paused가 push에 무영향).
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task VS5_Sorter_Paused_OperationalReady_PushReadyTrue()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        _ = factory.CreateClient();

        long sorterId    = factory.SorterDestinationId;
        int  sorterChute = factory.SorterChuteNo;
        var  status      = factory.Services.GetRequiredService<IDestinationStatusService>();

        await AlignSorterAsync(factory);

        // PAUSED ground-truth: destination.Status=PAUSED.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            db.Destinations.First(d => d.Id == sorterId).Status = DestStatus.PAUSED;
            db.SaveChanges();
        }

        await WaitUntilAsync(() => status.Compute(sorterId, DestType.SORTER_3D).Paused, 5000, "Paused=true ground-truth");
        var r = status.Compute(sorterId, DestType.SORTER_3D);
        Assert.True(r.Paused, "PAUSED → Paused=true(산출 유지)");
        Assert.True(r.Ready,  "paused여도 운영상태 OK → push ready=true(핵심 회귀)");
        Assert.Equal(DenyReason.None, r.Reason);

        await WaitUntilAsync(() => rcs.LastFor(sorterChute) is { Ready: true }, 6000, "paused여도 push ready=true 수신");
        Assert.True(rcs.LastFor(sorterChute)!.Ready);
        _out.WriteLine("[VS-5] PAUSED인데 운영상태 OK → push ready=true (Paused 산출은 유지)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // VS-7 IF-05 소터 회귀(3축): r.Ready(운영상태) 변경이 IF-05 결과에 무영향임을 입증.
    //   IF-05는 r.Paused + SorterCanAcceptBarcode만 소비(r.Ready 미소비).
    //   (a) 셀 있으면 offline이어도 OK (b) paused면 NG (c) 만재(셀 없음)면 NG.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task VS7_If05_Sorter_ThreeAxis_UnaffectedByOperationalReady()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        var client = factory.CreateClient();

        long sorterId = factory.SorterDestinationId;

        // ── (a) 셀 있으면 offline이어도 IF-05 OK (IF-05는 online을 보지 않음 — 셀 기준) ──
        //   미정렬·offline 상태(운영상태 ready=false)지만 빈 셀 ≥1 → SorterCanAcceptBarcode=true → OK.
        factory.FakeMaster.SetFailReads(true);
        await WaitForSnapshotAsync(factory, s => !s.Online, 6000);
        var status = factory.Services.GetRequiredService<IDestinationStatusService>();
        Assert.False(status.Compute(sorterId, DestType.SORTER_3D).Ready, "offline → 운영상태 ready=false");

        var respA = await client.PostAsJsonAsync("/api/v1/destination-query",
            new { pId = 4101, agvNo = 1, barcode = "TEST-BARCODE-3", inductionNo = 1, qty = 1, timeStamp = (string?)null });
        var bodyA = await respA.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.Equal("OK", bodyA!.Result);
        Assert.Equal(factory.SorterChuteNo, bodyA.ChuteNo);

        // ── (b) paused면 NG (소터 paused 차단 우선) ──
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            db.Destinations.First(d => d.Id == sorterId).Status = DestStatus.PAUSED;
            db.SaveChanges();
        }
        var respB = await client.PostAsJsonAsync("/api/v1/destination-query",
            new { pId = 4102, agvNo = 1, barcode = "TEST-BARCODE-3", inductionNo = 1, qty = 1, timeStamp = (string?)null });
        var bodyB = await respB.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.Equal("NG", bodyB!.Result);
        Assert.Null(bodyB.ChuteNo);

        // paused 해제(만재 셋업 전 정상 복귀).
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            db.Destinations.First(d => d.Id == sorterId).Status = DestStatus.NORMAL;
            db.SaveChanges();
        }

        // ── (c) 만재(셀 없음)면 NG ──
        //   온라인 정렬로 운영상태 ready=true로 만들어도 IF-05는 셀 기준이라 NG여야 함(r.Ready 무영향 입증).
        factory.FakeMaster.SetFailReads(false);
        await AlignSorterAsync(factory);
        Assert.True(status.Compute(sorterId, DestType.SORTER_3D).Ready, "정렬 → 운영상태 ready=true(그래도 IF-05는 셀 기준)");

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            MakeSorterFull(db, sorterId);
        }
        await WaitUntilAsync(() => status.Compute(sorterId, DestType.SORTER_3D).Full, 5000, "SorterFull=true");
        // 만재 후 TEST-BARCODE-3(ORD-003)의 배정 셀 전부 작업수량 도달·빈 셀 0 → SorterCanAcceptBarcode=false.
        Assert.False(status.SorterCanAcceptBarcode(sorterId, "TEST-BARCODE-3"), "만재 → 셀 못 받음(NG 전제)");

        var respC = await client.PostAsJsonAsync("/api/v1/destination-query",
            new { pId = 4103, agvNo = 1, barcode = "TEST-BARCODE-3", inductionNo = 1, qty = 1, timeStamp = (string?)null });
        var bodyC = await respC.Content.ReadFromJsonAsync<DestinationQueryResponse>();
        Assert.Equal("NG", bodyC!.Result);
        Assert.Null(bodyC.ChuteNo);

        _out.WriteLine("[VS-7] IF-05 소터 3축: (a)offline+셀있음 OK (b)paused NG (c)만재 NG — r.Ready(운영상태) 무영향");
    }

    // ════════════════════════════════════════════════════════════════════════
    // VS-9(a): 운영상태 전이를 N스레드가 동시 관찰해도 push 정확히 1건(클레임 경합 멱등).
    //   barrier 동시관찰 프로브 — 중복억제 경로만으로는 불충분(S-RCS-IF-REDESIGN-P2 교훈).
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task VS9a_Sorter_OperationalTransition_ConcurrentObserve_ExactlyOncePush()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        _ = factory.CreateClient();

        int sorterChute = factory.SorterChuteNo;
        var pusher = factory.Services.GetRequiredService<DestinationStatusPusher>();

        // 부트스트랩 정착(미정렬 → ready=false 1건).
        await WaitUntilAsync(() => rcs.CountFor(sorterChute) >= 1, 8000, "부트스트랩 소터 수신");
        await WaitUntilExactAsync(() => rcs.CountFor(sorterChute), 1, stableCount: 6, timeoutMs: 4000, "부트스트랩 안정");
        Assert.False(rcs.LastFor(sorterChute)!.Ready);

        // 운영상태 전이(미정렬→정렬, ready false→true) 유발.
        await AlignSorterAsync(factory);

        // 같은 전이를 N스레드가 동시에 관찰(NotifyChuteChanged = 슈트 콜백 경로지만 소터 destId도 Observe로 수렴).
        const int concurrency = 16;
        using var barrier = new Barrier(concurrency);
        var tasks = Enumerable.Range(0, concurrency).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            pusher.NotifyChuteChanged(factory.SorterDestinationId);  // 같은 전이를 동시 관찰
        })).ToArray();
        await Task.WhenAll(tasks);

        // 전이는 1회(false→true)뿐 — 부트스트랩 1 + 전이 1 = 정확히 2건. 동시 관찰에도 중복 0.
        await WaitUntilAsync(() => rcs.CountFor(sorterChute) >= 2, 5000, "운영상태 전이 1건 도달");
        await WaitUntilExactAsync(() => rcs.CountFor(sorterChute), 2, stableCount: 8, timeoutMs: 5000,
            "동시 16관찰에도 운영상태 전이당 정확히 1건(중복 0)");
        Assert.Equal(2, rcs.CountFor(sorterChute));
        Assert.True(rcs.LastFor(sorterChute)!.Ready);
        _out.WriteLine($"[VS-9a] 동시 {concurrency}관찰 → 운영상태 전이당 1건(총 {rcs.CountFor(sorterChute)}건=부트1+전이1)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // VS-9(b): 만재·paused 전이만으로는 소터 push 0건(무발화·no-flood).
    //   운영상태 불변(정렬·online·Ready=1)인 채 SorterFull·PAUSED를 토글해도 push ready 불변 → 전이 0.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task VS9b_Sorter_FullAndPausedTransition_NoPush()
    {
        await using var rcs     = await FakeRcsServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        _ = factory.CreateClient();

        long sorterId    = factory.SorterDestinationId;
        int  sorterChute = factory.SorterChuteNo;
        var  status      = factory.Services.GetRequiredService<IDestinationStatusService>();

        // 운영상태 정렬 → push ready=true 1건(부트스트랩 false + 전이 true = 2건).
        await AlignSorterAsync(factory);
        await WaitUntilAsync(() => rcs.LastFor(sorterChute) is { Ready: true }, 6000, "정렬 → ready=true");
        int baseReady = rcs.CountFor(sorterChute);
        await WaitUntilExactAsync(() => rcs.CountFor(sorterChute), baseReady, stableCount: 6, timeoutMs: 4000, "ready=true 안정");

        // 만재 전이(빈셀→만재) — 운영상태 불변이므로 push 0건.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            MakeSorterFull(db, sorterId);
        }
        await WaitUntilAsync(() => status.Compute(sorterId, DestType.SORTER_3D).Full, 5000, "SorterFull=true");

        // paused 전이 — 역시 운영상태 불변이므로 push 0건.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            db.Destinations.First(d => d.Id == sorterId).Status = DestStatus.PAUSED;
            db.SaveChanges();
        }
        await WaitUntilAsync(() => status.Compute(sorterId, DestType.SORTER_3D).Paused, 5000, "Paused=true");

        // 관찰 주기 다수에도 push 무발화(만재·paused 전이는 push에 무영향).
        await WaitUntilExactAsync(() => rcs.CountFor(sorterChute), baseReady, stableCount: 10, timeoutMs: 5000,
            "만재·paused 전이만으로는 소터 push 0건(무발화·no-flood)");
        Assert.True(rcs.LastFor(sorterChute)!.Ready, "마지막 푸시는 여전히 ready=true(만재·paused가 false로 안 바꿈)");

        // 산출 ground-truth: Full=true·Paused=true이지만 운영상태 OK → Ready=true.
        var r = status.Compute(sorterId, DestType.SORTER_3D);
        Assert.True(r.Full);
        Assert.True(r.Paused);
        Assert.True(r.Ready, "만재+paused여도 운영상태 OK → ready=true");
        _out.WriteLine($"[VS-9b] 만재·paused 전이 중 소터 push 0건(총 {rcs.CountFor(sorterChute)}건) — 무발화");
    }
}
