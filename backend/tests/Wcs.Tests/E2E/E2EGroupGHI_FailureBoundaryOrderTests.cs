using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api;
using Wcs.Core;
using Wcs.Data;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests.E2E;

// ════════════════════════════════════════════════════════════════════════════
// S-E2E-MULTI-AGV — 그룹 G(장애 전이) + H(경계) + I(순서/멱등)
//
// 매트릭스 G1·G2·G3·G5·G6 / H1·H4·H6 / I1·I2·I3 를 실 Sim + 실 EF DB + 가짜 RCS push로 검증.
//   - G4(슈트 full→비움 재푸시)·H2·H3·H5는 기존 PUSH1·EC4·EC8·S6가 ground-truth 커버.
//   - ⚠ G6(슈트 복구 재푸시 비대칭)·H4(TgtFloor 잔류)·H5(R_Flag 재시도)는 SPEC §7 미확정 →
//     현 동작 단언 + finding(추측 단언 금지).
// ════════════════════════════════════════════════════════════════════════════

public class E2EGroupGHI_FailureBoundaryOrderTests
{
    private readonly ITestOutputHelper _out;
    public E2EGroupGHI_FailureBoundaryOrderTests(ITestOutputHelper output) => _out = output;

    private async Task<(E2EWebApplicationFactory factory, FakeChuteStateServer rcs)> StartAsync(int initialCurFloor = 2)
    {
        var rcs = await FakeChuteStateServer.StartAsync();
        var factory = new E2EWebApplicationFactory(rcsBaseUrl: rcs.BaseUrl, initialCurFloor: initialCurFloor);
        await factory.StartSimsAsync();
        _ = factory.CreateClient();
        await E2EWait.UntilAsync(() => factory.IsSorterOnline(factory.PrimarySorter.DestinationId), 5000, "소터 Online");
        return (factory, rcs);
    }

    // ════════════════════════════════════════════════════════════════════════
    // G1: OFFLINE → push false. GT: Sim StopAsync → push ready=false 수신. (기존 VS3 패턴.)
    //   소터를 정렬(ready=true)로 만든 뒤 Sim 종료 → OFFLINE → push ready=false 전이.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task G1_Offline_PushReadyFalse()
    {
        var (factory, rcs) = await StartAsync(initialCurFloor: 2);  // 정렬 상태 → 부트스트랩 ready=true
        await using var _f = factory;
        await using var _r = rcs;
        int chute = factory.PrimarySorter.ChuteNo;
        long destId = factory.PrimarySorter.DestinationId;

        // 부트스트랩: 정렬·online·Ready=1 → push ready=true.
        await E2EWait.UntilAsync(() => rcs.LastFor(chute) is { Ready: true }, 8000, "부트스트랩 ready=true");
        int baseline = rcs.CountFor(chute);

        // Sim 종료 → OFFLINE → push ready=false 전이.
        await factory.PrimarySorter.Sim.StopAsync();
        await E2EWait.UntilAsync(() => factory.SorterSnapshot(destId)?.Online == false, 5000, "OFFLINE 전이");
        await E2EWait.UntilAsync(() => rcs.CountFor(chute) >= baseline + 1 && rcs.LastFor(chute)!.Ready == false,
            6000, "OFFLINE → push ready=false");
        Assert.False(rcs.LastFor(chute)!.Ready);
        _out.WriteLine($"[G1] OFFLINE → push ready=false (총 {rcs.CountFor(chute)}건)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // G2: 복구 → 자동 재평가. GT: Sim 재기동 → online 복구 → push ready 재전이.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task G2_Recovery_AutoReevaluate_PushReadyAgain()
    {
        var (factory, rcs) = await StartAsync(initialCurFloor: 2);
        await using var _f = factory;
        await using var _r = rcs;
        int chute = factory.PrimarySorter.ChuteNo;
        long destId = factory.PrimarySorter.DestinationId;

        await E2EWait.UntilAsync(() => rcs.LastFor(chute) is { Ready: true }, 8000, "부트스트랩 ready=true");

        // OFFLINE.
        await factory.PrimarySorter.Sim.StopAsync();
        await E2EWait.UntilAsync(() => rcs.LastFor(chute)!.Ready == false, 6000, "OFFLINE push ready=false");

        // 재기동(같은 포트) → online 복구 → push ready=true 재전이(관찰 타이머 자동 재평가).
        await factory.RestartSimAsync(0);
        await E2EWait.UntilAsync(() => factory.IsSorterOnline(destId), 5000, "online 복구");
        await E2EWait.UntilAsync(() => rcs.LastFor(chute) is { Ready: true }, 6000, "복구 후 push ready=true 재전이");
        Assert.True(rcs.LastFor(chute)!.Ready);
        _out.WriteLine("[G2] OFFLINE→복구 → push ready 재전이(true) — 관찰 타이머 자동 재평가");
    }

    // ════════════════════════════════════════════════════════════════════════
    // G3: busy→ready 투입 가능 전이. GT: Ready 0→1 전이 → push ready=true 1건. (기존 PUSH2_3 패턴.)
    //   미정렬(층1·운영층2 불일치)로 시작 → push ready=false. IF-09 정렬로 CurFloor=2·Ready=1 → ready=true.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task G3_BusyToReady_Transition_PushReadyTrue()
    {
        var (factory, rcs) = await StartAsync(initialCurFloor: 1);  // 미정렬 → push ready=false
        await using var _f = factory;
        await using var _r = rcs;
        int chute = factory.PrimarySorter.ChuteNo;
        long destId = factory.PrimarySorter.DestinationId;
        using var client = factory.CreateClient();

        await E2EWait.UntilAsync(() => rcs.CountFor(chute) >= 1, 8000, "부트스트랩 push");
        await E2EWait.UntilAsync(() => rcs.LastFor(chute)!.Ready == false, 4000, "미정렬 ready=false");
        int baseline = rcs.CountFor(chute);

        // IF-05 → IF-09 정렬 → CurFloor 1→2·Ready=1 → push ready=true 전이.
        await MultiAgvDriver.RunOneAsync(client, new AgvJob(27001, 1, "TEST-BARCODE-3", chute, DoDeposit: false));
        await E2EWait.UntilAsync(() => factory.SorterSnapshot(destId) is { CurFloor: 2, Ready: true }, 5000, "정렬 완료");
        await E2EWait.UntilAsync(() => rcs.CountFor(chute) >= baseline + 1 && rcs.LastFor(chute)!.Ready, 5000, "ready=true 전이");
        Assert.True(rcs.LastFor(chute)!.Ready);
        _out.WriteLine("[G3] busy(미정렬)→ready(정렬) 전이 → push ready=true");
    }

    // ════════════════════════════════════════════════════════════════════════
    // G5: PAUSED 발신 합성·IF-05 정합. GT: 소터 PAUSED → 발신 next_state 2(paused 접힘) AND IF-05 NG.
    //   발신(ready∧!paused)과 IF-05 dispatch(r.Paused) 둘 다 paused를 반영 → 양쪽 정합. 다중 AGV E2E 재현.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task G5_Paused_PushState2_And_If05Ng()
    {
        var (factory, rcs) = await StartAsync(initialCurFloor: 2);
        await using var _f = factory;
        await using var _r = rcs;
        var driver = MultiAgvDriver.ForFactory(factory);
        int chute = factory.PrimarySorter.ChuteNo;
        long destId = factory.PrimarySorter.DestinationId;

        // 부트스트랩: 정렬·online → 발신 next_state 3.
        await E2EWait.UntilAsync(() => rcs.LastFor(chute) is { Ready: true }, 8000, "부트스트랩 next_state 3");
        int baseline = rcs.CountFor(chute);

        // 소터 PAUSED 설정(직접 DB — 관찰 타이머가 재평가로 감지).
        using (var db = factory.CreateDbScope())
        {
            var dest = await db.Destinations.FirstAsync(d => d.Id == destId);
            dest.Status = DestStatus.PAUSED;
            await db.SaveChangesAsync();
        }

        // 발신 합성 = ready ∧ !paused → PAUSED면 next_state 2로 전이(운영상태 ready여도 paused 접힘).
        await E2EWait.UntilAsync(() => rcs.CountFor(chute) >= baseline + 1 && rcs.LastFor(chute)!.Ready == false,
            6000, "PAUSED → push next_state 2");
        Assert.False(rcs.LastFor(chute)!.Ready, "PAUSED → 발신 2(paused 접힘)");

        // IF-05 dispatch도 r.Paused 소비 → NG(발신·dispatch 양쪽 정합).
        var r = await driver.RunSingleAsync(new AgvJob(27101, 1, "TEST-BARCODE-3", chute, DoArrival: false, DoDeposit: false));
        Assert.Equal("NG", r.If05Result);
        _out.WriteLine("[G5] 소터 PAUSED → 발신 next_state 2(paused 접힘) AND IF-05 NG(r.Paused) — 양쪽 정합");
    }

    // ════════════════════════════════════════════════════════════════════════
    // G6 ⚠: RCS 다운 복구 시 소터 자동 재푸시 / 슈트 stale 이연(SPEC §7·TODO 비대칭). 현 동작 단언.
    //   소터: RCS 거부 중 전이 → 미알림 → 거부 해제 후 관찰 타이머 재평가로 복구 재푸시.
    //   슈트: 다음 이벤트 전까지 stale = 현 동작(비대칭을 결함 아닌 현 명세로 고정 + finding 표기).
    //   여기선 **소터 복구 재푸시**를 입증(현 동작) — 슈트 비대칭은 finding 문서화.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task G6_RcsDown_Recovery_SorterAutoRepush_CurrentBehavior_ChuteAsymmetryFinding()
    {
        var (factory, rcs) = await StartAsync(initialCurFloor: 1);  // 미정렬 → ready=false
        await using var _f = factory;
        await using var _r = rcs;
        int chute = factory.PrimarySorter.ChuteNo;
        long destId = factory.PrimarySorter.DestinationId;
        using var client = factory.CreateClient();

        await E2EWait.UntilAsync(() => rcs.CountFor(chute) >= 1, 8000, "부트스트랩");
        await E2EWait.UntilAsync(() => rcs.LastFor(chute)!.Ready == false, 4000, "미정렬 ready=false");
        int baseline = rcs.CountFor(chute);

        // RCS 거부 모드(다운 시뮬레이션).
        rcs.StartRejecting();

        // 소터 정렬 전이 유발(IF-09) → ready=true 전이지만 거부 중이라 미도달.
        await MultiAgvDriver.RunOneAsync(client, new AgvJob(27201, 1, "TEST-BARCODE-3", chute, DoDeposit: false));
        await E2EWait.UntilAsync(() => factory.SorterSnapshot(destId) is { CurFloor: 2, Ready: true }, 5000, "정렬 완료");
        // 거부 중이므로 수신 카운트는 baseline 유지(미알림).
        await Task.Delay(400);
        Assert.Equal(baseline, rcs.CountFor(chute));

        // RCS 복구 — 거부 해제. 소터 관찰 타이머가 매 주기 재평가 → Computed(true)≠Acked(false) → 재푸시.
        rcs.StopRejecting();
        await E2EWait.UntilAsync(() => rcs.CountFor(chute) >= baseline + 1 && rcs.LastFor(chute)!.Ready, 6000,
            "복구 후 소터 자동 재푸시(ready=true)");
        Assert.True(rcs.LastFor(chute)!.Ready);
        _out.WriteLine("[G6 ⚠현동작] 소터: RCS 복구 시 관찰 타이머 자동 재푸시. 슈트: 다음 이벤트까지 stale(비대칭 — SPEC §7/TODO finding)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // H1: qty 경계 −1/0/+1. GT: qty≤0 → 400 / qty≥1 → 정상. (기존 MINOR1 — 다중 AGV 맥락.)
    // ════════════════════════════════════════════════════════════════════════
    [Theory]
    [InlineData(-1, HttpStatusCode.BadRequest)]
    [InlineData(0,  HttpStatusCode.BadRequest)]
    [InlineData(1,  HttpStatusCode.OK)]
    public async Task H1_QtyBoundary(int qty, HttpStatusCode expected)
    {
        var (factory, rcs) = await StartAsync();
        await using var _f = factory;
        await using var _r = rcs;
        using var client = factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/v1/destination-query",
            new { pId = 27300 + (qty + 1), agvNo = 1, barcode = "TEST-BARCODE-1", inductionNo = 1, qty, timeStamp = (string?)null });
        Assert.Equal(expected, resp.StatusCode);
        _out.WriteLine($"[H1] qty={qty} → {(int)resp.StatusCode} (기대 {(int)expected})");
    }

    // ════════════════════════════════════════════════════════════════════════
    // H4 ⚠: TgtFloor 잔류 미해결(SPEC §7-B·TODO). GT: 이동만·투입 없이 이탈 시 TgtFloor≠0 잔류 →
    //   현 동작(WCS 클리어 안 함·절대규칙 #3) 단언 + "해소책 미정" finding.
    //   미정렬 소터 IF-09 정렬 → TgtFloor=2 기입·이동 후 CurFloor=2(TgtFloor 유지). 투입 없이 관찰 →
    //   WCS는 TgtFloor를 클리어하지 않음(PLC가 분류 시작 시만 클리어 — 투입 없으면 분류 없음).
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task H4_TgtFloorResidual_WcsNeverClears_CurrentBehavior_Finding()
    {
        var (factory, rcs) = await StartAsync(initialCurFloor: 1);  // 미정렬
        await using var _f = factory;
        await using var _r = rcs;
        long destId = factory.PrimarySorter.DestinationId;
        using var client = factory.CreateClient();

        // IF-05 → IF-09 정렬(TgtFloor=2 기입·이동). 투입(IF-10) 없음 → 분류 없음 → TgtFloor 유지.
        await MultiAgvDriver.RunOneAsync(client, new AgvJob(27401, 1, "TEST-BARCODE-3", factory.PrimarySorter.ChuteNo, DoDeposit: false));
        await E2EWait.UntilAsync(() => factory.SorterSnapshot(destId) is { CurFloor: 2, Ready: true }, 5000, "이동 완료 CurFloor=2");

        // SPEC §6: Sim 이동 완료 시 "CurFloor=TgtFloor, TgtFloor 유지(!)". 투입 없으니 분류 클리어도 없음.
        // WCS는 절대규칙 #3으로 TgtFloor를 절대 클리어하지 않음 → TgtFloor=2 잔류(현 동작).
        await E2EWait.UntilExactAsync(
            () => factory.SorterSnapshot(destId)!.TgtFloor, expected: 2, stableCount: 6, timeoutMs: 3000,
            "투입 없이 TgtFloor=2 잔류(WCS 클리어 안 함)");
        Assert.Equal(2, factory.SorterSnapshot(destId)!.TgtFloor);
        _out.WriteLine("[H4 ⚠현동작] 이동만·투입 없음 → TgtFloor=2 잔류(WCS 클리어 안 함·절대규칙 #3). 해소책 SPEC §7-B 미정 — finding");
    }

    // ════════════════════════════════════════════════════════════════════════
    // H6: 2층 고정 운영. GT: OperationalFloor=2 설정 경유(하드코딩 0)·정렬 항상 2층.
    //   미정렬(층1) → IF-09 → 항상 2층으로 정렬(설정값 OperationalFloor 경유).
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task H6_OperationalFloor2_FromConfig_AlwaysAligns2()
    {
        var (factory, rcs) = await StartAsync(initialCurFloor: 1);
        await using var _f = factory;
        await using var _r = rcs;
        long destId = factory.PrimarySorter.DestinationId;
        using var client = factory.CreateClient();

        await MultiAgvDriver.RunOneAsync(client, new AgvJob(27501, 1, "TEST-BARCODE-3", factory.PrimarySorter.ChuteNo, DoDeposit: false));
        // 항상 2층 정렬(설정 Wcs:OperationalFloor=2 — 하드코딩 0).
        await E2EWait.UntilAsync(() => factory.SorterSnapshot(destId)?.CurFloor == 2, 5000, "운영층(2) 정렬");
        Assert.Equal(2, factory.SorterSnapshot(destId)!.CurFloor);
        // D6 쓰기가 2였음(설정값 경유).
        Assert.Contains(factory.Timeline, l => l.Contains("WCS 쓰기 수신: D6") && l.Contains("→2"));
        _out.WriteLine("[H6] OperationalFloor=2(설정 경유) → 항상 2층 정렬");
    }

    // ════════════════════════════════════════════════════════════════════════
    // I1: IF-09 선행(도착 전 정렬 안 함). GT: IF-09 없이 IF-10 → 정렬 미수행 상태에서도 핸드셰이크 동작.
    //   미정렬(층1)에서 IF-09 생략하고 IF-10만 → 정렬은 안 됐어도 핸드셰이크는 trigger됨(현 동작).
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task I1_If10_WithoutIf09_HandshakeStillTriggers_CurrentBehavior()
    {
        var (factory, rcs) = await StartAsync(initialCurFloor: 1);  // 미정렬 — IF-09 생략 시 미정렬 유지
        await using var _f = factory;
        await using var _r = rcs;
        var driver = MultiAgvDriver.ForFactory(factory);
        long destId = factory.PrimarySorter.DestinationId;

        // IF-09 생략(DoArrival:false) — 정렬 트리거 없음. IF-10만 → 핸드셰이크 trigger.
        await driver.RunSingleAsync(new AgvJob(27601, 1, "TEST-BARCODE-3", factory.PrimarySorter.ChuteNo, DoArrival: false));

        // 현 동작: IF-10은 정렬 완료를 강제 대기하지 않음 — 핸드셰이크가 trigger돼 sorter_command 생성.
        // (미정렬이어도 C/R 핸드셰이크 자체는 진행 — IF-10 SelectCell→ExecuteHandshake.)
        await E2EWait.UntilAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            return await db.SorterCommands.AnyAsync();
        }, 6000, "IF-09 없이 IF-10 → 핸드셰이크 trigger(sorter_command 생성)");
        _out.WriteLine("[I1] IF-09 선행 없이 IF-10 → 핸드셰이크 trigger됨(현 동작 — IF-10은 정렬 강제 대기 안 함)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // I2: IF-10이 핸드셰이크 전(IF-09/정렬 미완) → 현 동작 단언(IF-10은 정렬 완료를 강제 대기하지 않음).
    //   I1과 동형 관점 — IF-10 응답은 즉시 200(핸드셰이크는 백그라운드 fire-and-forget).
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task I2_If10_ReturnsImmediately_HandshakeBackground_CurrentBehavior()
    {
        var (factory, rcs) = await StartAsync(initialCurFloor: 1);
        await using var _f = factory;
        await using var _r = rcs;
        using var client = factory.CreateClient();

        // IF-05만(소터 piece 생성).
        await MultiAgvDriver.RunOneAsync(client, new AgvJob(27701, 1, "TEST-BARCODE-3", factory.PrimarySorter.ChuteNo, DoArrival: false, DoDeposit: false));

        // IF-10 응답은 즉시 200(정렬/핸드셰이크 완료를 기다리지 않음 — fire-and-forget).
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var if10 = await client.PostAsJsonAsync("/api/v1/deposit-report",
            new { pId = 27701, barcode = "TEST-BARCODE-3", chuteNo = factory.PrimarySorter.ChuteNo, agvNo = 1 });
        sw.Stop();
        Assert.Equal(HttpStatusCode.OK, if10.StatusCode);
        // 핸드셰이크(분류 100ms+이동 등)를 동기 대기하지 않으므로 응답이 빠름(3s API 한계 내·현 동작).
        Assert.True(sw.ElapsedMilliseconds < 3000, $"IF-10 응답 빠름(핸드셰이크 비동기) — {sw.ElapsedMilliseconds}ms");
        _out.WriteLine($"[I2] IF-10 즉시 200({sw.ElapsedMilliseconds}ms) — 핸드셰이크 백그라운드(정렬 강제 대기 안 함)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // I3: 재시도 중복 수량 0(DISTINCT piece). GT: 재시도=새 sorter_command 행이어도 셀 수량은 piece별
    //   1건만 합산(SorterCellQty DISTINCT). 같은 piece에 COMPLETED sorter_command 2행 → 수량 1배만.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task I3_RetryDuplicate_CellQty_DistinctPiece()
    {
        var (factory, rcs) = await StartAsync();
        await using var _f = factory;
        await using var _r = rcs;
        long destId = factory.PrimarySorter.DestinationId;

        // 같은 piece에 COMPLETED sorter_command 2행(재시도 시뮬레이션) — 셀 수량은 piece별 1건만 합산.
        using (var db = factory.CreateDbScope())
        {
            var now = DateTime.UtcNow;
            var cell = await db.Cells.FirstAsync(c => c.DestinationId == destId && c.CellNo == 1);
            var piece = new Piece
            {
                PId = 27801, IsActive = true, Barcode = "TEST-BARCODE-3", Qty = 5, DepositedAt = now,
                DestinationId = destId, Status = PieceStatus.LOADED, CreatedAt = now, UpdatedAt = now,
            };
            db.Pieces.Add(piece);
            await db.SaveChangesAsync();
            // 같은 piece에 COMPLETED 2행(재시도) — DISTINCT piece이므로 수량은 5(2배 아님).
            for (int seq = 1; seq <= 2; seq++)
                db.SorterCommands.Add(new SorterCommand
                {
                    PieceId = piece.Id, CellId = cell.Id, CSeq = seq, CellNo = 1, CWrittenAt = now,
                    RSeq = seq, RCellNo = 1, RFlagAt = now, Status = SorterCommandStatus.COMPLETED, CreatedAt = now,
                });
            await db.SaveChangesAsync();
        }

        using (var db = factory.CreateDbScope())
        {
            int loaded = E2ESeed.LoadedQtyForDestination(db, destId);
            Assert.Equal(5, loaded);  // piece별 1건 DISTINCT → 5(2행이어도 중복 합산 0).
            int cmdRows = await db.SorterCommands.CountAsync(c => db.Pieces.Any(p => p.Id == c.PieceId && p.PId == 27801));
            Assert.Equal(2, cmdRows);  // sorter_command는 2행(재시도) 존재.
        }
        _out.WriteLine("[I3] 재시도=COMPLETED 2행이어도 셀 수량 = piece.qty 1배(DISTINCT piece — 중복 합산 0)");
    }
}
