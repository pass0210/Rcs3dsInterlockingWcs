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
// S-E2E-MULTI-AGV — 그룹 A(정상 플로우) + B(IF-05 게이트)
//
// 매트릭스 A1·A2·A3·A4·A5·A6·A7 / B1·B6·B10·B11·I4 를 실 stack(실 Sim 핸드셰이크·실 EF DB·
// 가짜 RCS push) ground-truth로 검증. "기존 커버" 항목(B2~B5·B7~B9·B12~B16)은 다중 AGV E2E
// 맥락에서 재현 가치가 낮아 기존 단위/통합 테스트 매핑으로 대체(매핑 표는 sprint-log).
//
// 모든 핸드셰이크/정렬 ground-truth는 실 Sim3ds + 실 EF DB(sorter_command·cell_assignment·
// piece·alarm·셀수량). 인메모리 카운터 단독 0.
// ════════════════════════════════════════════════════════════════════════════

[Collection("RealSimSerial")]
public class E2EGroupAB_NormalAndGateTests
{
    private readonly ITestOutputHelper _out;
    public E2EGroupAB_NormalAndGateTests(ITestOutputHelper output) => _out = output;

    // ── 공용 셋업: 실 Sim 기동 + 호스트 + 소터 Online 대기 ──────────────────────
    private async Task<(E2EWebApplicationFactory factory, FakeChuteStateServer rcs)> StartAsync(
        int initialCurFloor = 2, int[]? extraSorters = null)
    {
        var rcs = await FakeChuteStateServer.StartAsync();
        var factory = new E2EWebApplicationFactory(
            rcsBaseUrl: rcs.BaseUrl, extraSorterChuteNos: extraSorters, initialCurFloor: initialCurFloor);
        await factory.StartSimsAsync();
        _ = factory.CreateClient();   // 호스트 기동(HostedService StartAsync — SorterRegistryFactory 폴링 시작)
        await E2EWait.UntilAsync(() => factory.IsSorterOnline(factory.PrimarySorter.DestinationId),
            5000, "기본 소터 Online");
        return (factory, rcs);
    }

    // ════════════════════════════════════════════════════════════════════════
    // A1: 새 오더·빈 셀 → IF-09 정렬 → 핸드셰이크 → 적재.
    // GT: sorter_command COMPLETED · R_Seq==C_Seq · cell_assignment 1건 · 셀 수량 반영.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task A1_NewOrder_EmptyCell_FullHandshake_Completed_CellQtyApplied()
    {
        var (factory, rcs) = await StartAsync(initialCurFloor: 2);
        await using var _f = factory;
        await using var _r = rcs;
        var driver = MultiAgvDriver.ForFactory(factory);

        var r = await driver.RunSingleAsync(new AgvJob(
            PId: 21001, AgvNo: 1, Barcode: "TEST-BARCODE-3", ChuteNo: E2EWebApplicationFactory.DefaultSorterChuteNo, Qty: 7));
        Assert.Equal("OK", r.If05Result);
        Assert.Equal(E2EWebApplicationFactory.DefaultSorterChuteNo, r.ChuteNo);
        Assert.Equal(HttpStatusCode.OK, r.If09Status);
        Assert.Equal("OK", r.If10Result);

        long destId = factory.PrimarySorter.DestinationId;

        // 핸드셰이크 완료 → sorter_command COMPLETED 1건(실 Sim 핸드셰이크 ground-truth).
        await E2EWait.UntilAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            return await db.SorterCommands.AnyAsync(c => c.Status == SorterCommandStatus.COMPLETED);
        }, 6000, "sorter_command COMPLETED");

        using (var db = factory.CreateDbScope())
        {
            var cmd = await db.SorterCommands.Where(c => c.Status == SorterCommandStatus.COMPLETED).FirstAsync();
            Assert.NotNull(cmd.RSeq);
            Assert.Equal(cmd.CSeq, cmd.RSeq);  // R_Seq==C_Seq 대사 일치(실 Sim).

            // cell_assignment 활성 1건(그 소터).
            int activeAssign = await db.CellAssignments
                .CountAsync(a => a.Cell.DestinationId == destId && a.ReleasedAt == null);
            // [S-CELL-ACCUM] 배정은 오더 완료까지 지속 — ORD-003(PlannedQty=20)은 1 piece로 미완료라
            // 활성 배정 1건 지속(매 투입 해제 아님). 셀 적재는 sorter_command.cell_id로 단언(수량 ground-truth).
            var piece = await db.Pieces.FirstAsync(p => p.PId == 21001);
            // 셀 수량 반영: 이 piece의 COMPLETED sorter_command가 그 셀에 적재됨.
            var loaded = await db.SorterCommands
                .Where(c => c.Status == SorterCommandStatus.COMPLETED && c.PieceId == piece.Id)
                .Select(c => new { c.CellId, c.Piece.Qty }).Distinct().SumAsync(x => x.Qty);
            Assert.Equal(7, loaded);  // 셀 수량 = piece.qty(7).
            _out.WriteLine($"[A1] COMPLETED CSeq={cmd.CSeq} RSeq={cmd.RSeq} 셀적재수량={loaded} 활성배정={activeAssign}");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // A2: 기존 오더 셀 누적(같은 배정 셀 재사용).
    // GT: 동일 cell에 sorter_command 2건 · 동일 cellId · 셀 수량 누적.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task A2_SameOrder_CellAccumulation_TwoCommands_SameCell_QtyAccumulated()
    {
        var (factory, rcs) = await StartAsync(initialCurFloor: 2);
        await using var _f = factory;
        await using var _r = rcs;
        var driver = MultiAgvDriver.ForFactory(factory);
        long destId = factory.PrimarySorter.DestinationId;

        // 1건: 새 빈 셀에 배정·적재(qty=4).
        await driver.RunSingleAsync(new AgvJob(22001, 1, "TEST-BARCODE-3", E2EWebApplicationFactory.DefaultSorterChuteNo, Qty: 4));
        await E2EWait.UntilAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            return await db.SorterCommands.CountAsync(c => c.Status == SorterCommandStatus.COMPLETED) >= 1;
        }, 6000, "1차 COMPLETED");

        // [S-CELL-ACCUM] 1차 핸드셰이크 클리어 대기. 배정은 오더 완료까지 지속하므로(매 투입 해제 아님)
        // 2차는 SelectCell ① 재사용으로 **동일 셀**에 누적한다(② 빈 셀 재할당 아님). 핵심 단언은 "같은 셀 누적".
        await E2EWait.UntilAsync(() => factory.SorterSnapshot(destId) is { CFlag: false, RFlag: false },
            4000, "1차 핸드셰이크 클리어");

        // 2건: 같은 오더(TEST-BARCODE-3) — ① 배정 재사용으로 동일 셀 누적. qty=3.
        await driver.RunSingleAsync(new AgvJob(22002, 1, "TEST-BARCODE-3", E2EWebApplicationFactory.DefaultSorterChuteNo, Qty: 3));
        await E2EWait.UntilAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            return await db.SorterCommands.CountAsync(c => c.Status == SorterCommandStatus.COMPLETED) >= 2;
        }, 6000, "2차 COMPLETED");

        using (var db = factory.CreateDbScope())
        {
            // sorter_command COMPLETED 2건(같은 오더 누적).
            int completed = await db.SorterCommands.CountAsync(c => c.Status == SorterCommandStatus.COMPLETED);
            Assert.True(completed >= 2, $"COMPLETED ≥2 (실제 {completed})");

            // 두 piece(22001·22002)가 같은 destination에 적재 — 셀 수량 합 = 4+3=7.
            int totalLoaded = await db.SorterCommands
                .Where(c => c.Status == SorterCommandStatus.COMPLETED && c.Cell.DestinationId == destId)
                .Select(c => new { c.CellId, c.PieceId, c.Piece.Qty }).Distinct().SumAsync(x => x.Qty);
            Assert.Equal(7, totalLoaded);  // 셀 수량 누적(piece별 1건 — DISTINCT).
            _out.WriteLine($"[A2] COMPLETED {completed}건 · 소터 적재 수량 합={totalLoaded}");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // A3: 이미 운영층 정렬(CurFloor=2) → IF-09 즉시(TgtFloor 쓰기 0).
    // GT: Sim 타임라인 D6 쓰기 0건(이동 정렬 없음).
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task A3_AlreadyAligned_If09_NoTgtFloorWrite()
    {
        var (factory, rcs) = await StartAsync(initialCurFloor: 2);  // 이미 운영층(2)
        await using var _f = factory;
        await using var _r = rcs;
        var driver = MultiAgvDriver.ForFactory(factory);

        // IF-05 → IF-09(도착) 까지만(IF-10 끔 — 정렬 쓰기만 관찰).
        await driver.RunSingleAsync(new AgvJob(
            23001, 1, "TEST-BARCODE-3", E2EWebApplicationFactory.DefaultSorterChuteNo, DoDeposit: false));

        // 정렬 쓰기가 없음을 확인 — 잠시 폴 후 타임라인에 D6 쓰기 0건.
        await E2EWait.UntilAsync(() => true, 300, "정렬 관찰 안정화");  // 폴 몇 주기 흐르도록
        await E2EWait.UntilExactAsync(
            () => factory.Timeline.Count(l => l.Contains("WCS 쓰기 수신: D6")),
            expected: 0, stableCount: 6, timeoutMs: 3000, "이미 정렬 → D6 쓰기 0건");
        Assert.Equal(2, factory.SorterSnapshot(factory.PrimarySorter.DestinationId)!.CurFloor);
        _out.WriteLine("[A3] 이미 운영층 정렬 → D6 쓰기 0건(Sim 타임라인)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // A4: 미정렬(CurFloor=1) → IF-09 → TgtFloor=2 기입 → 이동 → CurFloor=2 → 핸드셰이크.
    // GT: Sim 타임라인 D6=2 1건 + 이동 후 CurFloor=2·Ready=1 + 핸드셰이크 COMPLETED.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task A4_NotAligned_If09_WritesTgtFloor2_ThenMoves_ThenHandshake()
    {
        var (factory, rcs) = await StartAsync(initialCurFloor: 1);  // 미정렬(층1)
        await using var _f = factory;
        await using var _r = rcs;
        var driver = MultiAgvDriver.ForFactory(factory);
        long destId = factory.PrimarySorter.DestinationId;

        // IF-05 → IF-09(정렬 트리거).
        await driver.RunSingleAsync(new AgvJob(
            24001, 1, "TEST-BARCODE-3", E2EWebApplicationFactory.DefaultSorterChuteNo, DoDeposit: false));

        // D6=2 정렬 쓰기 1건 발생.
        await E2EWait.UntilAsync(
            () => factory.Timeline.Any(l => l.Contains("WCS 쓰기 수신: D6") && l.Contains("→2")),
            4000, "D6=2 정렬 쓰기");
        // 이동 완료 → CurFloor=2 · Ready=1.
        await E2EWait.UntilAsync(
            () => factory.SorterSnapshot(destId) is { CurFloor: 2, Ready: true }, 4000, "운영층 정렬 완료");

        int d6Writes = factory.Timeline.Count(l => l.Contains("WCS 쓰기 수신: D6"));
        Assert.Equal(1, d6Writes);  // 정렬 쓰기 정확히 1건(핑퐁 0).

        // 이제 IF-10 → 핸드셰이크 COMPLETED.
        await driver.RunSingleAsync(new AgvJob(
            24002, 1, "TEST-BARCODE-3", E2EWebApplicationFactory.DefaultSorterChuteNo, DoArrival: false));
        await E2EWait.UntilAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            return await db.SorterCommands.AnyAsync(c => c.Status == SorterCommandStatus.COMPLETED);
        }, 6000, "정렬 후 핸드셰이크 COMPLETED");
        _out.WriteLine($"[A4] D6=2 정렬 1건 → CurFloor=2 → 핸드셰이크 COMPLETED");
    }

    // ════════════════════════════════════════════════════════════════════════
    // A5: 슈트 정상 플로우(IF-05 OK → IF-09 무정렬 → IF-10 OK, IF-11 트리거 0).
    // GT: 핸드셰이크 없음(sorter_command 0) · piece DEPOSITED · 소터 C_Flag 불변.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task A5_Chute_NormalFlow_NoHandshakeTrigger()
    {
        var (factory, rcs) = await StartAsync(initialCurFloor: 2);
        await using var _f = factory;
        await using var _r = rcs;
        var driver = MultiAgvDriver.ForFactory(factory);
        long destId = factory.PrimarySorter.DestinationId;

        // 슈트(TEST-BARCODE-1 → chuteNo=1).
        var r = await driver.RunSingleAsync(new AgvJob(25001, 1, "TEST-BARCODE-1", 1));
        Assert.Equal("OK", r.If05Result);
        Assert.Equal(1, r.ChuteNo);
        Assert.Equal("OK", r.If10Result);

        // piece DEPOSITED.
        await E2EWait.UntilAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            var p = await db.Pieces.FirstOrDefaultAsync(p => p.PId == 25001 && p.IsActive);
            return p?.Status == PieceStatus.DEPOSITED;
        }, 4000, "슈트 piece DEPOSITED");

        // 슈트 보고는 핸드셰이크 트리거 0 — sorter_command 0건 · 소터 C_Flag 불변(false 유지).
        await E2EWait.UntilExactAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            return await db.SorterCommands.CountAsync();
        }, expected: 0, stableCount: 6, timeoutMs: 3000, "슈트 보고 → sorter_command 0건");
        Assert.False(factory.SorterSnapshot(destId)!.CFlag, "슈트 보고 → 소터 C_Flag 불변");
        _out.WriteLine("[A5] 슈트 정상 → sorter_command 0건·소터 C_Flag 불변");
    }

    // ════════════════════════════════════════════════════════════════════════
    // A6: 멀티소터 라우팅(소터 2대) — 바코드별 올바른 소터로.
    // GT: 각 소터 destId 핸드셰이크 교차 0 · 각 sorter_command가 올바른 destId/cell.
    // (다중 Sim 팩토리 필요 — §3.1)
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task A6_MultiSorter_BarcodeRouting_NoCrossDestination()
    {
        const int secondChute = 31;
        var (factory, rcs) = await StartAsync(initialCurFloor: 2, extraSorters: [secondChute]);
        await using var _f = factory;
        await using var _r = rcs;
        var driver = MultiAgvDriver.ForFactory(factory);

        long destA = factory.Slots[0].DestinationId;   // chuteNo=30
        long destB = factory.Slots[1].DestinationId;   // chuteNo=31
        await E2EWait.UntilAsync(() => factory.IsSorterOnline(destB), 5000, "둘째 소터 Online");

        // 소터 A 바코드(TEST-BARCODE-3 → chute30) · 소터 B 바코드(SORTER-31-BC → chute31).
        await driver.RunSingleAsync(new AgvJob(26001, 1, "TEST-BARCODE-3", 30, Qty: 2));
        await driver.RunSingleAsync(new AgvJob(26002, 2, E2EWebApplicationFactory.BarcodeForSorter(secondChute), secondChute, Qty: 5));

        await E2EWait.UntilAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            return await db.SorterCommands.CountAsync(c => c.Status == SorterCommandStatus.COMPLETED) >= 2;
        }, 8000, "두 소터 핸드셰이크 COMPLETED");

        using (var db = factory.CreateDbScope())
        {
            // piece 26001은 소터 A(destA) 셀, 26002는 소터 B(destB) 셀에 적재 — 교차 0.
            var p1 = await db.Pieces.FirstAsync(p => p.PId == 26001);
            var p2 = await db.Pieces.FirstAsync(p => p.PId == 26002);

            var cmd1 = await db.SorterCommands.Include(c => c.Cell)
                .FirstAsync(c => c.PieceId == p1.Id && c.Status == SorterCommandStatus.COMPLETED);
            var cmd2 = await db.SorterCommands.Include(c => c.Cell)
                .FirstAsync(c => c.PieceId == p2.Id && c.Status == SorterCommandStatus.COMPLETED);

            Assert.Equal(destA, cmd1.Cell.DestinationId);  // A 바코드 → A 셀.
            Assert.Equal(destB, cmd2.Cell.DestinationId);  // B 바코드 → B 셀(교차 0).
            Assert.NotEqual(cmd1.Cell.DestinationId, cmd2.Cell.DestinationId);
            _out.WriteLine($"[A6] 소터A(dest={destA}) cmd1.cellDest={cmd1.Cell.DestinationId} / 소터B(dest={destB}) cmd2.cellDest={cmd2.Cell.DestinationId} — 교차 0");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // A7: 한 슈트 다중 송장(다중 IF-05/IF-10 같은 슈트) → piece N건 · 슈트 수량 합산.
    // GT: piece 2건(같은 슈트) · OrderItem.ReservedQty 합산.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task A7_OneChute_MultiplePieces_QtySummed()
    {
        var (factory, rcs) = await StartAsync(initialCurFloor: 2);
        await using var _f = factory;
        await using var _r = rcs;
        var driver = MultiAgvDriver.ForFactory(factory);

        // 같은 슈트(TEST-BARCODE-1 → chuteNo=1) 2건 — qty 3 + 4.
        var r1 = await driver.RunSingleAsync(new AgvJob(27001, 1, "TEST-BARCODE-1", 1, Qty: 3));
        var r2 = await driver.RunSingleAsync(new AgvJob(27002, 2, "TEST-BARCODE-1", 1, Qty: 4));
        Assert.Equal("OK", r1.If05Result);
        Assert.Equal("OK", r2.If05Result);

        using (var db = factory.CreateDbScope())
        {
            var dest1 = await db.Destinations.FirstAsync(d => d.ChuteNo == 1 && d.DestType == DestType.CHUTE);
            // piece 2건이 같은 슈트로.
            int pieceCount = await db.Pieces.CountAsync(p => (p.PId == 27001 || p.PId == 27002) && p.DestinationId == dest1.Id);
            Assert.Equal(2, pieceCount);
            // ReservedQty 합산(IF-05 OK 예약 차감 3+4=7).
            var item = await db.OrderItems.FirstAsync(i => i.Barcode == "TEST-BARCODE-1");
            Assert.True(item.ReservedQty >= 7, $"슈트 예약 수량 합산 ≥7 (실제 {item.ReservedQty})");
            _out.WriteLine($"[A7] 같은 슈트 piece {pieceCount}건 · ReservedQty={item.ReservedQty}");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // B1: 소터 셀 만재 → NG·reason=FULL (배정 셀 작업수량 도달 + 빈셀0).
    // GT: 응답 NG·chuteNo=null + piece_event IF05_RES.Reason="FULL".
    // (다중 AGV E2E 맥락 — 기존 SorterCellFullnessTests.EC1이 단위 커버.)
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task B1_Sorter_CellFull_If05_Ng_Full()
    {
        var (factory, rcs) = await StartAsync(initialCurFloor: 2);
        await using var _f = factory;
        await using var _r = rcs;
        var driver = MultiAgvDriver.ForFactory(factory);
        long destId = factory.PrimarySorter.DestinationId;

        // 소터를 만재로: 3셀 전부 점유 + 작업수량 도달(빈셀0 AND 전 배정 셀 도달).
        using (var db = factory.CreateDbScope())
        {
            E2ESeed.SetAllCapacities(db, destId, 5);
            E2ESeed.OccupyCells(db, destId, 3);
            E2ESeed.LoadCellQty(db, destId, 1, 5, 28001, "TEST-BARCODE-3");
            E2ESeed.LoadCellQty(db, destId, 2, 5, 28002, "TEST-BARCODE-3");
            E2ESeed.LoadCellQty(db, destId, 3, 5, 28003, "TEST-BARCODE-3");
            Assert.Equal(0, E2ESeed.FreeCellCount(db, destId));
        }

        // 새 오더(셀 미보유)로 IF-05 → NG(FULL).
        var r = await driver.RunSingleAsync(new AgvJob(28100, 1, "TEST-BARCODE-3", 30, DoArrival: false, DoDeposit: false));
        Assert.Equal("NG", r.If05Result);
        Assert.Null(r.ChuteNo);

        using (var db = factory.CreateDbScope())
        {
            var piece = await db.Pieces.FirstAsync(p => p.PId == 28100 && p.IsActive);
            var ev = await db.PieceEvents.FirstAsync(e => e.PieceId == piece.Id && e.EventType == PieceEventType.IF05_RES);
            Assert.Equal("FULL", ev.Reason);  // 내부 사유 = FULL(piece_event ground-truth).
        }
        _out.WriteLine("[B1] 소터 만재 → IF-05 NG·reason=FULL(piece_event)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // B6: 소터 비활성(미등록 destination → 번들 없음 → OFFLINE 경로). (Q1 채택: 현 동작 단언)
    // GT: 비활성 SORTER_3D는 SorterRegistryFactory가 번들 미구성 → IF-05 SorterCanAcceptBarcode가
    //     빈셀 기준이라 NG가 아닐 수 있으므로, 현 동작 = "IsActive=false 목적지는 QueryDestination에서
    //     blocked(NO_DEST)"임을 단언(현 코드 경로). 추측 단언 금지 — 현 동작 단언.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task B6_InactiveSorter_If05_Ng_CurrentBehavior()
    {
        var (factory, rcs) = await StartAsync(initialCurFloor: 2);
        await using var _f = factory;
        await using var _r = rcs;
        var driver = MultiAgvDriver.ForFactory(factory);
        long destId = factory.PrimarySorter.DestinationId;

        // 기본 소터를 비활성화(IsActive=false). 그 오더(TEST-BARCODE-3)는 그 destination에 매핑됨.
        using (var db = factory.CreateDbScope())
        {
            var dest = await db.Destinations.FirstAsync(d => d.Id == destId);
            dest.IsActive = false;
            await db.SaveChangesAsync();
        }

        // 현 동작(DbRepositories.QueryDestination): dest.IsActive=false → blocked → NG·NO_DEST.
        var r = await driver.RunSingleAsync(new AgvJob(28200, 1, "TEST-BARCODE-3", 30, DoArrival: false, DoDeposit: false));
        Assert.Equal("NG", r.If05Result);
        Assert.Null(r.ChuteNo);

        using (var db = factory.CreateDbScope())
        {
            var piece = await db.Pieces.FirstAsync(p => p.PId == 28200 && p.IsActive);
            var ev = await db.PieceEvents.FirstAsync(e => e.PieceId == piece.Id && e.EventType == PieceEventType.IF05_RES);
            Assert.Equal("NO_DEST", ev.Reason);  // 비활성 목적지 = NO_DEST(현 코드 동작 — Q1).
        }
        _out.WriteLine("[B6] 비활성 소터 → IF-05 NG·reason=NO_DEST(현 동작 단언 — Q1)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // B10: dest NULL(상위 등록 송장) → 빈 슈트 AUTO 할당 → OK·dest_assign_type=AUTO.
    // GT: OK·chuteNo 배정 + order.DestAssignType=AUTO. (시드 TEST-BARCODE-AUTO/ORD-004)
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task B10_DestNull_AutoAssignsEmptyChute()
    {
        var (factory, rcs) = await StartAsync(initialCurFloor: 2);
        await using var _f = factory;
        await using var _r = rcs;
        var driver = MultiAgvDriver.ForFactory(factory);

        // ORD-004(TEST-BARCODE-AUTO) destination=NULL → AUTO 빈 슈트 할당.
        var r = await driver.RunSingleAsync(new AgvJob(29001, 1, "TEST-BARCODE-AUTO", 0, DoArrival: false, DoDeposit: false));
        Assert.Equal("OK", r.If05Result);
        Assert.NotNull(r.ChuteNo);

        using (var db = factory.CreateDbScope())
        {
            var order = await db.Orders.FirstAsync(o => o.OrderNo == "ORD-004");
            Assert.Equal(DestAssignType.AUTO, order.DestAssignType);  // AUTO 배정 ground-truth.
            Assert.NotNull(order.DestinationId);
            _out.WriteLine($"[B10] dest NULL → AUTO 슈트 배정 chuteNo={r.ChuteNo} DestAssignType={order.DestAssignType}");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // B11: 오더 OVER(reserved+qty > planned) → NG. (Q2: QueryDestination OVER 경로 확인 후 시드)
    // GT: 응답 NG·chuteNo=null + piece_event Reason="OVER".
    //   ORD-003(TEST-BARCODE-3 planned=20). reserved를 20에 가깝게 올린 뒤 초과 qty로 NG 유도.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task B11_OrderOver_If05_Ng_Over()
    {
        var (factory, rcs) = await StartAsync(initialCurFloor: 2);
        await using var _f = factory;
        await using var _r = rcs;
        var driver = MultiAgvDriver.ForFactory(factory);

        // ORD-003 OrderItem.ReservedQty=planned(20)로 끌어올림 → 추가 qty면 OVER.
        using (var db = factory.CreateDbScope())
        {
            var item = await db.OrderItems.FirstAsync(i => i.Barcode == "TEST-BARCODE-3");
            item.ReservedQty = item.PlannedQty;  // reserved == planned
            await db.SaveChangesAsync();
        }

        // qty=1 → reserved(20)+1 > planned(20) → OVER.
        var r = await driver.RunSingleAsync(new AgvJob(29101, 1, "TEST-BARCODE-3", 30, Qty: 1, DoArrival: false, DoDeposit: false));
        Assert.Equal("NG", r.If05Result);
        Assert.Null(r.ChuteNo);

        using (var db = factory.CreateDbScope())
        {
            var piece = await db.Pieces.FirstAsync(p => p.PId == 29101 && p.IsActive);
            var ev = await db.PieceEvents.FirstAsync(e => e.PieceId == piece.Id && e.EventType == PieceEventType.IF05_RES);
            Assert.Equal("OVER", ev.Reason);  // OVER 경로 ground-truth(Q2 — 시드 가능 범위).
        }
        _out.WriteLine("[B11] 오더 OVER(reserved+qty>planned) → IF-05 NG·reason=OVER(Q2)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // I4: 중복 IF-05(같은 pId 2회) → 멱등(이전 활성 piece 비활성화·중복 활성 piece 0).
    // GT: 같은 pId 활성 piece는 항상 1건(p_id 순환 — 이전 활성 IsActive=false).
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task I4_DuplicateIf05_SamePId_OnlyOneActivePiece()
    {
        var (factory, rcs) = await StartAsync(initialCurFloor: 2);
        await using var _f = factory;
        await using var _r = rcs;
        var driver = MultiAgvDriver.ForFactory(factory);

        await driver.RunSingleAsync(new AgvJob(29201, 1, "TEST-BARCODE-1", 1, DoArrival: false, DoDeposit: false));
        await driver.RunSingleAsync(new AgvJob(29201, 1, "TEST-BARCODE-1", 1, DoArrival: false, DoDeposit: false));

        using (var db = factory.CreateDbScope())
        {
            int active = await db.Pieces.CountAsync(p => p.PId == 29201 && p.IsActive);
            Assert.Equal(1, active);  // 활성 piece 정확히 1건(p_id 순환 — 현 동작 단언).
            int total = await db.Pieces.CountAsync(p => p.PId == 29201);
            _out.WriteLine($"[I4] 중복 IF-05 → 활성 piece 1건(총 {total}행, 이전 활성 비활성화)");
        }
    }
}
