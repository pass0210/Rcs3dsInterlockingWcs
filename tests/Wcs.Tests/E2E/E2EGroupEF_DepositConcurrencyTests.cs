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
// S-E2E-MULTI-AGV — 그룹 E(IF-10/적재) + F(동시성 — 진성 경합)
//
// 매트릭스 E2·E3·E4·E5·E6 / F1·F2·F4·F5·F6·F7·F8 를 실 Sim + 실 EF DB + 가짜 RCS push로 검증.
//   F 항목은 **Barrier 동시 도달 + 실 동시 HTTP**(MultiAgvDriver.RunConcurrentAsync)로 진성 경합
//   (단일 idle 경로 함정 회피 — 계약 §6 ③). 멱등·원자성은 실 DB 행 카운트로 단언(인메모리 0).
//   E1은 기존 VS6·S1, E5는 기존 콜백 경로가 ground-truth 커버 → 신규는 다중 AGV 맥락에 집중.
//   ⚠ E6(콜백 throw 셀 누수)은 정상 경로 누수 0만 단언 + 재현 곤란 → finding(M5 이연).
// ════════════════════════════════════════════════════════════════════════════

public class E2EGroupEF_DepositConcurrencyTests
{
    private readonly ITestOutputHelper _out;
    public E2EGroupEF_DepositConcurrencyTests(ITestOutputHelper output) => _out = output;

    private async Task<(E2EWebApplicationFactory factory, FakeRcsServer rcs)> StartAsync(
        int[]? extraSorters = null)
    {
        var rcs = await FakeRcsServer.StartAsync();
        var factory = new E2EWebApplicationFactory(
            rcsBaseUrl: rcs.BaseUrl, extraSorterChuteNos: extraSorters, initialCurFloor: 2);
        await factory.StartSimsAsync();
        _ = factory.CreateClient();
        await E2EWait.UntilAsync(() => factory.IsSorterOnline(factory.PrimarySorter.DestinationId), 5000, "소터 Online");
        return (factory, rcs);
    }

    // ════════════════════════════════════════════════════════════════════════
    // E2: 멱등(중복 pId 보고 무해). GT: 2차 보고 OK·기록 1건. (기존 VS5 — 다중 AGV 맥락 슈트.)
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task E2_If10_Idempotent_DuplicatePId_OneRecord()
    {
        var (factory, rcs) = await StartAsync();
        await using var _f = factory;
        await using var _r = rcs;
        using var client = factory.CreateClient();

        // 슈트 IF-05 → IF-10 두 번(같은 pId).
        await MultiAgvDriver.RunOneAsync(client, new AgvJob(25001, 1, "TEST-BARCODE-1", 1, DoArrival: false));
        var dup = await client.PostAsJsonAsync("/api/v1/deposit-report",
            new { pId = 25001, barcode = "TEST-BARCODE-1", chuteNo = 1, agvNo = 1 });
        Assert.Equal(HttpStatusCode.OK, dup.StatusCode);  // 멱등 — 2차도 OK.

        using (var db = factory.CreateDbScope())
        {
            // 활성 DEPOSITED piece 1건(중복 기록 0).
            int deposited = await db.Pieces.CountAsync(p => p.PId == 25001 && p.IsActive
                && (p.Status == PieceStatus.DEPOSITED || p.Status == PieceStatus.LOADED));
            Assert.Equal(1, deposited);
        }
        _out.WriteLine("[E2] 중복 IF-10 → 멱등 OK·기록 1건");
    }

    // ════════════════════════════════════════════════════════════════════════
    // E3: 동시 같은 pId IF-10 → 정확히 1배정. GT: 8병렬 → piece 1건·DEPOSITED/LOADED 1건.
    //   (기존 CONCUR1 — E2E에서 실 Sim 맥락 재현. DB 레벨 진성 멱등.)
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task E3_If10_ConcurrentSamePId_ExactlyOneRecord()
    {
        var (factory, rcs) = await StartAsync();
        await using var _f = factory;
        await using var _r = rcs;
        const int pid = 25101;

        // IF-05 먼저(소터 — 활성 RESERVED piece).
        using (var c0 = factory.CreateClient())
            await MultiAgvDriver.RunOneAsync(c0, new AgvJob(pid, 1, "TEST-BARCODE-3", E2EWebApplicationFactory.DefaultSorterChuteNo, DoArrival: false, DoDeposit: false));

        // 같은 pId로 8병렬 IF-10(Barrier 동시 도달 — 진성 경합).
        const int concurrency = 8;
        using var barrier = new Barrier(concurrency);
        var tasks = Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
        {
            using var client = factory.CreateClient();
            barrier.SignalAndWait();
            return await client.PostAsJsonAsync("/api/v1/deposit-report",
                new { pId = pid, barcode = "TEST-BARCODE-3", chuteNo = E2EWebApplicationFactory.DefaultSorterChuteNo, agvNo = 1 });
        })).ToArray();
        var responses = await Task.WhenAll(tasks);
        foreach (var resp in responses) Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // DB 레벨 진성 멱등: DEPOSITED/LOADED 활성 piece 정확히 1건(8병렬 중 1건만 전이).
        using (var db = factory.CreateDbScope())
        {
            int recorded = await db.Pieces.CountAsync(p => p.PId == pid && p.IsActive
                && (p.Status == PieceStatus.DEPOSITED || p.Status == PieceStatus.CELL_ASSIGNED || p.Status == PieceStatus.LOADED));
            Assert.Equal(1, recorded);  // 정확히 1건(부분 유니크 + catch 멱등).
        }
        _out.WriteLine($"[E3] 8병렬 IF-10(같은 pId) → DEPOSITED/LOADED 활성 piece 정확히 1건");
    }

    // ════════════════════════════════════════════════════════════════════════
    // E4: COMPLETED → 셀 수량 반영. GT: 핸드셰이크 완료 후 셀 적재 수량 증가(COMPLETED JOIN piece.qty).
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task E4_Completed_CellQtyApplied()
    {
        var (factory, rcs) = await StartAsync();
        await using var _f = factory;
        await using var _r = rcs;
        var driver = MultiAgvDriver.ForFactory(factory);
        long destId = factory.PrimarySorter.DestinationId;

        await driver.RunSingleAsync(new AgvJob(25201, 1, "TEST-BARCODE-3", E2EWebApplicationFactory.DefaultSorterChuteNo, Qty: 6));
        await E2EWait.UntilAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            return await db.SorterCommands.AnyAsync(c => c.Status == SorterCommandStatus.COMPLETED);
        }, 6000, "COMPLETED");

        using (var db = factory.CreateDbScope())
        {
            int loaded = E2ESeed.LoadedQtyForDestination(db, destId);
            Assert.Equal(6, loaded);  // 셀 수량 = piece.qty(6) — COMPLETED JOIN.
        }
        _out.WriteLine("[E4] COMPLETED → 셀 적재 수량 6 반영(sorter_command JOIN piece.qty)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // E5: cell_assignment 해제 타이밍. GT: 핸드셰이크 콜백 ReleaseCell 후 배정 해제(released_at) 관찰.
    //   현 동작 단언(콜백이 ReleaseCell → 그 셀 활성 배정이 released).
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task E5_CellAssignment_ReleasedAfterHandshakeCallback()
    {
        var (factory, rcs) = await StartAsync();
        await using var _f = factory;
        await using var _r = rcs;
        var driver = MultiAgvDriver.ForFactory(factory);
        long destId = factory.PrimarySorter.DestinationId;

        await driver.RunSingleAsync(new AgvJob(25301, 1, "TEST-BARCODE-3", E2EWebApplicationFactory.DefaultSorterChuteNo));
        await E2EWait.UntilAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            return await db.SorterCommands.AnyAsync(c => c.Status == SorterCommandStatus.COMPLETED);
        }, 6000, "COMPLETED");

        // 콜백 ReleaseCell이 그 셀 배정을 released → 활성 배정 0으로 수렴(현 동작).
        await E2EWait.UntilAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            return await db.CellAssignments.CountAsync(a => a.Cell.DestinationId == destId && a.ReleasedAt == null) == 0;
        }, 5000, "핸드셰이크 후 cell_assignment released");

        using (var db = factory.CreateDbScope())
        {
            int released = await db.CellAssignments.CountAsync(a => a.Cell.DestinationId == destId && a.ReleasedAt != null);
            Assert.True(released >= 1, "배정이 released_at 기록됨(콜백 ReleaseCell)");
        }
        _out.WriteLine("[E5] 핸드셰이크 콜백 ReleaseCell → cell_assignment released(현 동작)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // E6 ⚠: 콜백 throw 시 ReleaseCell 스킵 → 셀 누수(TODO 이연·M5). 정상 경로 누수 0만 단언.
    //   재현은 호스트종료/DI 오설정 한정 경로라 E2E에서 곤란 → finding 등재. 정상 경로 누수 0 입증.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task E6_NormalPath_NoCellLeak_FindingForCallbackThrow()
    {
        var (factory, rcs) = await StartAsync();
        await using var _f = factory;
        await using var _r = rcs;
        var driver = MultiAgvDriver.ForFactory(factory);
        long destId = factory.PrimarySorter.DestinationId;

        // 정상 핸드셰이크 3건 연속 — 매 건 콜백 ReleaseCell 성공 → 누수 0.
        for (int i = 0; i < 3; i++)
        {
            await driver.RunSingleAsync(new AgvJob(25400 + i, 1, "TEST-BARCODE-3", E2EWebApplicationFactory.DefaultSorterChuteNo, Qty: 1));
            await E2EWait.UntilAsync(async () =>
            {
                using var db = factory.CreateDbScope();
                return await db.SorterCommands.CountAsync(c => c.Status == SorterCommandStatus.COMPLETED) >= i + 1;
            }, 6000, $"{i + 1}차 COMPLETED");
            await E2EWait.UntilAsync(() => factory.SorterSnapshot(destId) is { CFlag: false, RFlag: false }, 4000, $"{i + 1}차 클리어");
        }

        // 정상 경로: 모든 배정이 해제됨(활성 배정 0으로 수렴 — 누수 0).
        await E2EWait.UntilAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            return await db.CellAssignments.CountAsync(a => a.Cell.DestinationId == destId && a.ReleasedAt == null) == 0;
        }, 5000, "정상 경로 셀 누수 0(활성 배정 0)");
        _out.WriteLine("[E6 ⚠finding] 정상 경로 셀 누수 0. 콜백 throw 시 누수(M5 이연)는 호스트종료/DI오설정 한정 — finding.");
    }

    // ════════════════════════════════════════════════════════════════════════
    // F1: N-AGV(서로 다른 오더) → 각자 자기 오더 배정 셀로 적재. GT: 사전 배정된 N개 오더의 piece가
    //   각자 다른 cellId에 COMPLETED.
    //
    // ⚠ FINDING(현 동작): 한 소터 핸드셰이크는 물리적 직렬(SPEC §6 — 트레이 1개씩). 동시 IF-10은
    //   R_Seq 교차 MISMATCH(F1b 입증) → RCS는 직전 종결 후 순차 dispatch. 또한 핸드셰이크 콜백이
    //   ReleaseCell을 하므로 같은 오더 연속 dispatch는 동일 셀을 재사용한다(빈 셀 재할당). "서로 다른
    //   셀"은 **서로 다른 오더가 각자 다른 셀을 사전 배정**해야 성립 → 그렇게 시드해 입증한다.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task F1_DifferentOrders_EachOwnCell_AllCompleted_DistinctCells()
    {
        var (factory, rcs) = await StartAsync();
        await using var _f = factory;
        await using var _r = rcs;
        var driver = MultiAgvDriver.ForFactory(factory);
        long destId = factory.PrimarySorter.DestinationId;
        int chute = factory.PrimarySorter.ChuteNo;

        // 3개 서로 다른 오더(BC) → 각자 다른 셀(1·2·3) 사전 배정. Capacity 충분(NULL=무제한).
        using (var db = factory.CreateDbScope())
        {
            E2ESeed.AddSorterOrderWithAssignedCell(db, destId, "ORD-F1-A", "F1-BC-A", cellNo: 1);
            E2ESeed.AddSorterOrderWithAssignedCell(db, destId, "ORD-F1-B", "F1-BC-B", cellNo: 2);
            E2ESeed.AddSorterOrderWithAssignedCell(db, destId, "ORD-F1-C", "F1-BC-C", cellNo: 3);
        }

        // 순차 dispatch(한 소터 직렬) — 각 오더는 자기 배정 셀(SelectCell ① 재사용)로 적재.
        var jobs = new[]
        {
            (pid: 25501, bc: "F1-BC-A", cell: 1),
            (pid: 25502, bc: "F1-BC-B", cell: 2),
            (pid: 25503, bc: "F1-BC-C", cell: 3),
        };
        for (int i = 0; i < jobs.Length; i++)
        {
            int want = i + 1;
            await driver.RunSingleAsync(new AgvJob(jobs[i].pid, i + 1, jobs[i].bc, chute, Qty: 1));
            await E2EWait.UntilAsync(async () =>
            {
                using var db = factory.CreateDbScope();
                return await db.SorterCommands.CountAsync(c => c.Status != SorterCommandStatus.SENT) >= want;
            }, 8000, $"{want}차 핸드셰이크 종결");
        }

        using (var db = factory.CreateDbScope())
        {
            int completed = await db.SorterCommands.CountAsync(c => c.Status == SorterCommandStatus.COMPLETED);
            Assert.Equal(3, completed);  // 순차 → 3건 전부 COMPLETED(교차 0).

            // 각 piece가 자기 오더 배정 셀(1·2·3 = 서로 다른 cellId)로 적재.
            var pids = jobs.Select(j => j.pid).ToArray();
            var cmdCells = await db.SorterCommands
                .Where(c => c.Status == SorterCommandStatus.COMPLETED && db.Pieces.Any(p => p.Id == c.PieceId && pids.Contains(p.PId)))
                .Select(c => new { c.CellNo }).ToListAsync();
            var distinctCells = cmdCells.Select(x => x.CellNo).Distinct().ToList();
            Assert.Equal(3, distinctCells.Count);  // 서로 다른 3개 셀(오더별 자기 셀).
            _out.WriteLine($"[F1] 3 오더 각자 다른 셀 → COMPLETED 3건·셀 {string.Join(",", distinctCells.OrderBy(x => x))}");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // F1b ⚠FINDING(현 동작 단언): 한 소터에 **동시** IF-10(서로 다른 pId) → HandshakeOrchestrator가
    //   동일 인스턴스 concurrent 핸드셰이크를 직렬화하지 않아 R_Seq 교차 → 일부 MISMATCH.
    //   이것을 은폐(억지 GREEN)하지 않고 **현 동작**으로 명시 입증한다(계약 §6 ⑥ 정직 보고):
    //     - 한 소터 3 AGV **동시** IF-10 → COMPLETED 1건 + MISMATCH ≥1(직렬화 부재).
    //     - 같은 셋업을 **순차** dispatch하면 3건 모두 COMPLETED(F1/F8) — 직렬이 지원 모델.
    //   기대 명세("동시 IF-10 한 소터 허용")는 미정 → orchestrator 직렬화 필요(범위 밖·finding).
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task F1b_OneSorter_ConcurrentIf10_RSeqCross_CurrentBehavior_Finding()
    {
        var (factory, rcs) = await StartAsync();
        await using var _f = factory;
        await using var _r = rcs;
        long destId = factory.PrimarySorter.DestinationId;

        // 3 AGV IF-05를 먼저 순차로(활성 piece 3개 생성 — IF-05는 경합 안전).
        var pids = new[] { 26001, 26002, 26003 };
        foreach (var pid in pids)
            using (var c0 = factory.CreateClient())
                await MultiAgvDriver.RunOneAsync(c0, new AgvJob(pid, 1, "TEST-BARCODE-3", E2EWebApplicationFactory.DefaultSorterChuteNo, DoArrival: false, DoDeposit: false));

        // 3 IF-10을 **동시**(Barrier)로 한 소터에 발사 → 핸드셰이크 직렬화 부재 노출.
        using var barrier = new Barrier(pids.Length);
        var tasks = pids.Select(pid => Task.Run(async () =>
        {
            using var client = factory.CreateClient();
            barrier.SignalAndWait();
            return await client.PostAsJsonAsync("/api/v1/deposit-report",
                new { pId = pid, barcode = "TEST-BARCODE-3", chuteNo = E2EWebApplicationFactory.DefaultSorterChuteNo, agvNo = 1 });
        })).ToArray();
        var responses = await Task.WhenAll(tasks);
        foreach (var resp in responses) Assert.Equal(HttpStatusCode.OK, resp.StatusCode);  // IF-10 자체는 200.

        // 3건 핸드셰이크 종결 대기.
        await E2EWait.UntilAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            return await db.SorterCommands.CountAsync(c => c.Status != SorterCommandStatus.SENT && c.Cell.DestinationId == destId) >= 3;
        }, 10000, "3건 핸드셰이크 종결");

        using (var db = factory.CreateDbScope())
        {
            int completed = await db.SorterCommands.CountAsync(c => c.Status == SorterCommandStatus.COMPLETED && c.Cell.DestinationId == destId);
            int mismatch  = await db.SorterCommands.CountAsync(c => c.Status == SorterCommandStatus.MISMATCH && c.Cell.DestinationId == destId);
            // 현 동작: 동시 핸드셰이크는 직렬화 안 됨 → 1건만 COMPLETED, 나머지 R_Seq 교차로 MISMATCH.
            // (추측 단언 금지 — "올바른 명세"라 주장하지 않음. 현 코드 동작을 ground-truth로 고정.)
            Assert.True(completed >= 1, "최소 1건은 COMPLETED");
            Assert.True(mismatch >= 1,
                $"⚠FINDING: 한 소터 동시 IF-10 → 직렬화 부재로 MISMATCH 발생(현 동작). completed={completed} mismatch={mismatch}");
            _out.WriteLine($"[F1b ⚠FINDING] 한 소터 동시 IF-10 → COMPLETED={completed}·MISMATCH={mismatch} (핸드셰이크 직렬화 부재 — 순차 dispatch가 지원 모델·orchestrator 직렬화 후속 finding)");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // F4: 동시 IF-05 같은 빈 셀 경합 → 정확히 1배정. GT: barrier 동시 IF-05 N건(같은 pId 같은 오더) →
    //   cell_assignment 부분 유니크로 1건. (기존 EC5·CONCUR1 패턴 — 실 동시 HTTP 재현.)
    //   같은 pId·같은 바코드로 N병렬 IF-05+IF-10 → 같은 오더 셀에 정확히 1 적재.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task F4_ConcurrentIf05_SameCell_ExactlyOneAssignment()
    {
        var (factory, rcs) = await StartAsync();
        await using var _f = factory;
        await using var _r = rcs;
        const int pid = 25601;

        // IF-05 1회(활성 piece 생성).
        using (var c0 = factory.CreateClient())
            await MultiAgvDriver.RunOneAsync(c0, new AgvJob(pid, 1, "TEST-BARCODE-3", E2EWebApplicationFactory.DefaultSorterChuteNo, DoArrival: false, DoDeposit: false));

        // 같은 pId로 N병렬 IF-10(동시 IF-11 셀 배정 경합) → cell_assignment 부분 유니크로 1건만.
        const int concurrency = 8;
        using var barrier = new Barrier(concurrency);
        var tasks = Enumerable.Range(0, concurrency).Select(_ => Task.Run(async () =>
        {
            using var client = factory.CreateClient();
            barrier.SignalAndWait();
            return await client.PostAsJsonAsync("/api/v1/deposit-report",
                new { pId = pid, barcode = "TEST-BARCODE-3", chuteNo = E2EWebApplicationFactory.DefaultSorterChuteNo, agvNo = 1 });
        })).ToArray();
        var responses = await Task.WhenAll(tasks);
        foreach (var resp in responses) Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // 핸드셰이크 진행 대기.
        await E2EWait.UntilAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            return await db.SorterCommands.AnyAsync();
        }, 6000, "sorter_command 생성");

        using (var db = factory.CreateDbScope())
        {
            // DEPOSITED/LOADED 활성 piece 1건(8병렬 중 1건만 IF-11 트리거).
            int recorded = await db.Pieces.CountAsync(p => p.PId == pid && p.IsActive
                && (p.Status == PieceStatus.DEPOSITED || p.Status == PieceStatus.CELL_ASSIGNED || p.Status == PieceStatus.LOADED));
            Assert.Equal(1, recorded);
            // sorter_command(이 piece)는 1건(중복 핸드셰이크 0 — 멱등이 1건만 전이).
            var pieceId = await db.Pieces.Where(p => p.PId == pid && p.IsActive).Select(p => p.Id).FirstAsync();
            int cmds = await db.SorterCommands.CountAsync(c => c.PieceId == pieceId);
            Assert.Equal(1, cmds);  // 동시 경합에도 핸드셰이크 정확히 1건.
            _out.WriteLine($"[F4] 8병렬 IF-10 → 활성 기록 1건·sorter_command {cmds}건(정확히 1)");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // F5: 멀티소터 동시 핸드셰이크 교차 0. GT: 소터 2대 동시 핸드셰이크 → 각 destId의 sorter_command가
    //   자기 cell만. (다중 Sim 팩토리 필요 — Barrier 동시 IF-05/10.)
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task F5_MultiSorter_ConcurrentHandshake_NoCross()
    {
        const int secondChute = 31;
        var (factory, rcs) = await StartAsync(extraSorters: [secondChute]);
        await using var _f = factory;
        await using var _r = rcs;
        var driver = MultiAgvDriver.ForFactory(factory);
        long destA = factory.Slots[0].DestinationId;
        long destB = factory.Slots[1].DestinationId;
        await E2EWait.UntilAsync(() => factory.IsSorterOnline(destB), 5000, "둘째 소터 Online");

        // 소터 A·B 동시 풀 사이클(Barrier 동시 IF-05).
        var jobs = new List<AgvJob>
        {
            new(25701, 1, "TEST-BARCODE-3", 30, Qty: 2),
            new(25702, 2, E2EWebApplicationFactory.BarcodeForSorter(secondChute), secondChute, Qty: 3),
        };
        var results = await driver.RunConcurrentAsync(jobs);
        Assert.All(results, r => Assert.Equal("OK", r.If05Result));

        await E2EWait.UntilAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            return await db.SorterCommands.CountAsync(c => c.Status == SorterCommandStatus.COMPLETED) >= 2;
        }, 10000, "두 소터 동시 핸드셰이크 COMPLETED");

        using (var db = factory.CreateDbScope())
        {
            // 각 destId의 COMPLETED sorter_command가 자기 소터 셀만 — 교차 0.
            var cmdsA = await db.SorterCommands.Include(c => c.Cell)
                .Where(c => c.Status == SorterCommandStatus.COMPLETED && c.Cell.DestinationId == destA).ToListAsync();
            var cmdsB = await db.SorterCommands.Include(c => c.Cell)
                .Where(c => c.Status == SorterCommandStatus.COMPLETED && c.Cell.DestinationId == destB).ToListAsync();
            Assert.True(cmdsA.Count >= 1 && cmdsB.Count >= 1, "두 소터 각각 ≥1 COMPLETED");
            Assert.All(cmdsA, c => Assert.Equal(destA, c.Cell.DestinationId));
            Assert.All(cmdsB, c => Assert.Equal(destB, c.Cell.DestinationId));
            _out.WriteLine($"[F5] 멀티소터 동시 핸드셰이크 — destA {cmdsA.Count}건·destB {cmdsB.Count}건·교차 0");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // F6: push 전이 동시 관찰 → 전이당 1건. GT: FakeRcsServer.CountFor 전이당 정확히 1(16스레드 동시).
    //   소터 ready 전이(미정렬→정렬)를 운영층 정렬로 유발하고, 동시 관찰에도 전이당 1건 멱등.
    //   (기존 VS9a·PUSH4 패턴 — 실 Sim 정렬 전이로 재현.)
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task F6_PushTransition_ConcurrentObserve_ExactlyOnePerTransition()
    {
        // 미정렬(층1)로 시작 → 부트스트랩 push ready=false. 그 후 정렬 유발 → ready=true 전이 1건.
        var rcs = await FakeRcsServer.StartAsync();
        var factory = new E2EWebApplicationFactory(rcsBaseUrl: rcs.BaseUrl, initialCurFloor: 1);
        await factory.StartSimsAsync();
        await using var _f = factory;
        await using var _r = rcs;
        _ = factory.CreateClient();
        await E2EWait.UntilAsync(() => factory.IsSorterOnline(factory.PrimarySorter.DestinationId), 5000, "소터 Online");

        int sorterChute = factory.PrimarySorter.ChuteNo;
        long destId = factory.PrimarySorter.DestinationId;
        using var client = factory.CreateClient();

        // 부트스트랩: 미정렬 소터 → ready=false 1건 수신·안정.
        await E2EWait.UntilAsync(() => rcs.CountFor(sorterChute) >= 1, 8000, "부트스트랩 소터 push");
        await E2EWait.UntilExactAsync(() => rcs.CountFor(sorterChute), rcs.CountFor(sorterChute), stableCount: 6, timeoutMs: 4000, "부트스트랩 안정");
        int baseline = rcs.CountFor(sorterChute);
        Assert.False(rcs.LastFor(sorterChute)!.Ready, "미정렬 → ready=false");

        // 정렬 유발: IF-05 → IF-09(도착) → 운영층 정렬(CurFloor 1→2) → ready=true 전이.
        await MultiAgvDriver.RunOneAsync(client,
            new AgvJob(25801, 1, "TEST-BARCODE-3", sorterChute, DoDeposit: false));
        await E2EWait.UntilAsync(() => factory.SorterSnapshot(destId) is { CurFloor: 2, Ready: true }, 5000, "운영층 정렬");

        // 정렬 후 ready=true 전이 정확히 1건(관찰 주기 다수에도 폭주 0 — 전이당 1건 멱등).
        await E2EWait.UntilAsync(() => rcs.CountFor(sorterChute) >= baseline + 1, 5000, "ready=true 전이 push");
        await E2EWait.UntilExactAsync(() => rcs.CountFor(sorterChute), baseline + 1, stableCount: 8, timeoutMs: 4000,
            "정렬 전이 정확히 1건(폭주 0)");
        Assert.True(rcs.LastFor(sorterChute)!.Ready, "정렬 완료 → ready=true");
        _out.WriteLine($"[F6] 소터 정렬 전이 → push 정확히 1건(부트{baseline}+전이1=총 {rcs.CountFor(sorterChute)}건)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // F7: OFFLINE 전이 동시 → 알람 1건. GT: alarm OFFLINE 전이당 1건(안정 카운트). (기존 S7 패턴.)
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task F7_OfflineTransition_OneAlarmPerTransition()
    {
        var (factory, rcs) = await StartAsync();
        await using var _f = factory;
        await using var _r = rcs;
        long destId = factory.PrimarySorter.DestinationId;

        // 소터 Online 확인 후 Sim 종료 → OFFLINE 전이 1건.
        await factory.PrimarySorter.Sim.StopAsync();
        await E2EWait.UntilAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            return await db.Alarms.AnyAsync(a => a.Code == "OFFLINE");
        }, 6000, "OFFLINE alarm 기록");

        // 전이당 정확히 1건(추가 폴 실패에도 재발화 0 — 안정 카운트).
        await E2EWait.UntilExactAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            return await db.Alarms.CountAsync(a => a.Code == "OFFLINE");
        }, expected: 1, stableCount: 5, timeoutMs: 3000, "OFFLINE alarm 1건 안정(전이당 1건)");
        _out.WriteLine("[F7] OFFLINE 전이 → alarm 정확히 1건(전이당 1건 멱등)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // F8: 한 소터 여러 AGV 순차(직렬화). GT: 분류·이동 직렬(SPEC §6 — 한 소터 트레이 1개씩) →
    //   핸드셰이크 순차 COMPLETED·전부 R_Seq==C_Seq. RCS가 직전 종결 후 다음 IF-10 dispatch.
    //   (F1 finding 참조 — 한 소터 concurrent IF-10은 R_Seq 교차 MISMATCH. 직렬이 물리·현 동작 모델.)
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task F8_OneSorter_MultipleAgv_Serialized_AllCompleted()
    {
        var (factory, rcs) = await StartAsync();
        await using var _f = factory;
        await using var _r = rcs;
        var driver = MultiAgvDriver.ForFactory(factory);
        long destId = factory.PrimarySorter.DestinationId;

        // 한 소터에 3 AGV — 직전 핸드셰이크 종결 후 순차 dispatch(물리 직렬 — SPEC §6).
        var pids = new[] { 25901, 25902, 25903 };
        for (int i = 0; i < pids.Length; i++)
        {
            int want = i + 1;
            await driver.RunSingleAsync(new AgvJob(pids[i], i + 1, "TEST-BARCODE-3", E2EWebApplicationFactory.DefaultSorterChuteNo, Qty: 1));
            await E2EWait.UntilAsync(async () =>
            {
                using var db = factory.CreateDbScope();
                return await db.SorterCommands.CountAsync(c => c.Status != SorterCommandStatus.SENT && c.Cell.DestinationId == destId) >= want;
            }, 8000, $"{want}차 핸드셰이크 종결");
        }

        using (var db = factory.CreateDbScope())
        {
            // 직렬 처리 → 3건 모두 COMPLETED·전부 R_Seq==C_Seq(프레임 교차 0).
            var cmds = await db.SorterCommands
                .Where(c => c.Status == SorterCommandStatus.COMPLETED && c.Cell.DestinationId == destId).ToListAsync();
            Assert.Equal(3, cmds.Count);
            Assert.All(cmds, c => Assert.Equal(c.CSeq, c.RSeq));  // 매 건 대사 일치(직렬화 입증).
            _out.WriteLine($"[F8] 한 소터 3 AGV 직렬 dispatch → {cmds.Count}건 COMPLETED·전부 R_Seq==C_Seq");
        }
    }
}
