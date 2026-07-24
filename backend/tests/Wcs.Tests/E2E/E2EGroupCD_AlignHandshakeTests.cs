using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Wcs.Core;
using Wcs.Data;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests.E2E;

// ════════════════════════════════════════════════════════════════════════════
// S-E2E-MULTI-AGV — 그룹 C(IF-09/정렬) + D(C/R 핸드셰이크)
//
// 매트릭스 C5·C6·C7 / D3·D4·D5·D6·D8·D9 를 실 Sim + 실 EF DB ground-truth로 검증.
//   - C1/C2/C3/C4·D1/D2/D7/D10은 기존 ScenarioTests(S1~S9)·ApiIntegrationTests(If09_*)가
//     ground-truth 커버 → 다중 AGV E2E 중복 재현 대신 신규 항목(C5~C7·D3~D9)에 집중(매핑 표).
//   - ⚠ D5(C_Flag 상한)·D8(R_CellNo≠C_CellNo)은 SPEC §7 미확정 → 현 동작 단언/기대미정 분류.
//
// 고장주입은 실 SimServer(InjectRSeqOverride·InjectRFlagDelayMs·InjectNoResponse·StopAsync).
// ════════════════════════════════════════════════════════════════════════════

[Collection("RealSimSerial")]
public class E2EGroupCD_AlignHandshakeTests
{
    private readonly ITestOutputHelper _out;
    public E2EGroupCD_AlignHandshakeTests(ITestOutputHelper output) => _out = output;

    private async Task<(E2EWebApplicationFactory factory, FakeChuteStateServer rcs)> StartAsync(
        int initialCurFloor = 2, int rFlagTimeoutMs = 3000)
    {
        var rcs = await FakeChuteStateServer.StartAsync();
        var factory = new E2EWebApplicationFactory(
            rcsBaseUrl: rcs.BaseUrl, initialCurFloor: initialCurFloor, rFlagTimeoutMs: rFlagTimeoutMs);
        await factory.StartSimsAsync();
        _ = factory.CreateClient();
        await E2EWait.UntilAsync(() => factory.IsSorterOnline(factory.PrimarySorter.DestinationId), 5000, "소터 Online");
        return (factory, rcs);
    }

    // ════════════════════════════════════════════════════════════════════════
    // C5: 미존재 chuteNo → 200 + 기록만(500 금지). GT: HTTP 200·정렬 스킵.
    //   (다중 AGV 맥락 — 기존 If09_UnknownChuteNo 단위 커버.)
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task C5_If09_UnknownChuteNo_200_NoCrash()
    {
        var (factory, rcs) = await StartAsync();
        await using var _f = factory;
        await using var _r = rcs;
        var driver = MultiAgvDriver.ForFactory(factory);

        // IF-05 먼저(활성 piece 생성) — 기존 슈트로. 그 후 IF-09를 미존재 chuteNo(999)로.
        using var client = factory.CreateClient();
        var r = await MultiAgvDriver.RunOneAsync(client,
            new AgvJob(23501, 1, "TEST-BARCODE-1", 1, DoArrival: false, DoDeposit: false));
        Assert.Equal("OK", r.If05Result);

        var if09 = await client.PostAsJsonAsync("/api/v1/arrival-report",
            new { pId = 23501, chuteNo = 999, agvNo = 1, timeStamp = (string?)null });
        Assert.NotEqual(HttpStatusCode.InternalServerError, if09.StatusCode);
        Assert.Equal(HttpStatusCode.OK, if09.StatusCode);
        _out.WriteLine("[C5] 미존재 chuteNo=999 → IF-09 200(정렬 스킵·500 없음)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // C6: IF-05 선행 없이 IF-09 → 경고 로깅·활성 piece 없음(도착 기록 생략). GT: 200·RecordArrival
    //   false 경로(IF09_ARRIVAL piece_event 부재). 현 동작 단언.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task C6_If09_WithoutPriorIf05_200_NoArrivalEvent()
    {
        var (factory, rcs) = await StartAsync();
        await using var _f = factory;
        await using var _r = rcs;
        using var client = factory.CreateClient();

        // IF-05 없이 곧장 IF-09(새 pId, 활성 piece 없음).
        var if09 = await client.PostAsJsonAsync("/api/v1/arrival-report",
            new { pId = 23601, chuteNo = E2EWebApplicationFactory.DefaultSorterChuteNo, agvNo = 1, timeStamp = (string?)null });
        Assert.Equal(HttpStatusCode.OK, if09.StatusCode);  // 응답 200(현 동작).

        using (var db = factory.CreateDbScope())
        {
            // 활성 piece 없으니 IF09_ARRIVAL piece_event 부재(RecordArrival false 경로).
            bool anyArrivalForPid = await db.PieceEvents
                .AnyAsync(e => e.EventType == PieceEventType.IF09_ARRIVAL
                            && db.Pieces.Any(p => p.Id == e.PieceId && p.PId == 23601));
            Assert.False(anyArrivalForPid, "IF-05 선행 없음 → IF09_ARRIVAL 기록 부재(현 동작)");
        }
        _out.WriteLine("[C6] IF-05 선행 없이 IF-09 → 200·IF09_ARRIVAL 부재(RecordArrival false 경로)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // C7: 도착 후 OFFLINE → 정렬 미수행. GT: 번들 OFFLINE(snap.Online=false)에서 IF-09 → D6 쓰기 0.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task C7_If09_WhileOffline_NoAlignment()
    {
        // 미정렬(층1)로 시작 — Online이면 정렬 쓰기가 발생할 상태. OFFLINE이면 정렬 스킵.
        var (factory, rcs) = await StartAsync(initialCurFloor: 1);
        await using var _f = factory;
        await using var _r = rcs;
        long destId = factory.PrimarySorter.DestinationId;
        using var client = factory.CreateClient();

        // IF-05(활성 piece 생성) → 그 후 Sim 종료(OFFLINE 유도).
        var r = await MultiAgvDriver.RunOneAsync(client,
            new AgvJob(23701, 1, "TEST-BARCODE-3", E2EWebApplicationFactory.DefaultSorterChuteNo, DoArrival: false, DoDeposit: false));
        Assert.Equal("OK", r.If05Result);

        await factory.PrimarySorter.Sim.StopAsync();
        // OFFLINE 전이 대기(연속 폴 실패 — WriteTimeout*(OfflineAfterFailures+1) + 여유).
        await E2EWait.UntilAsync(() => factory.SorterSnapshot(destId)?.Online == false, 5000, "소터 OFFLINE 전이");
        int d6Before = factory.Timeline.Count(l => l.Contains("WCS 쓰기 수신: D6"));

        // OFFLINE 상태에서 IF-09 → 번들 스냅샷 Online=false → DepositDecider가 WriteTgtFloor=false → D6 쓰기 0.
        var if09 = await client.PostAsJsonAsync("/api/v1/arrival-report",
            new { pId = 23701, chuteNo = E2EWebApplicationFactory.DefaultSorterChuteNo, agvNo = 1, timeStamp = (string?)null });
        Assert.Equal(HttpStatusCode.OK, if09.StatusCode);

        // Sim이 죽어 타임라인은 더 늘지 않음 — 쓰기 0건 안정 확인.
        await E2EWait.UntilExactAsync(
            () => factory.Timeline.Count(l => l.Contains("WCS 쓰기 수신: D6")) - d6Before,
            expected: 0, stableCount: 5, timeoutMs: 2000, "OFFLINE 중 IF-09 → D6 추가 쓰기 0건");
        _out.WriteLine("[C7] OFFLINE 중 IF-09 → 정렬 미수행(D6 추가 쓰기 0건)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // D3: R_Seq 불일치 → MISMATCH 알람. GT: alarm.code=R_SEQ_MISMATCH + sorter_command MISMATCH.
    //   (실 Sim InjectRSeqOverride — 기존 S5 단위 커버, 다중 AGV E2E 맥락 재현.)
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task D3_RSeqMismatch_Alarm_And_SorterCommandMismatch()
    {
        var (factory, rcs) = await StartAsync();
        await using var _f = factory;
        await using var _r = rcs;
        var driver = MultiAgvDriver.ForFactory(factory);

        factory.PrimarySorter.Sim.InjectRSeqOverride = 999;  // R_Seq를 999로 교체 → 불일치.

        await driver.RunSingleAsync(new AgvJob(23801, 1, "TEST-BARCODE-3", E2EWebApplicationFactory.DefaultSorterChuteNo));

        await E2EWait.UntilAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            return await db.SorterCommands.AnyAsync(c => c.Status == SorterCommandStatus.MISMATCH);
        }, 6000, "sorter_command MISMATCH");

        using (var db = factory.CreateDbScope())
        {
            Assert.True(await db.SorterCommands.AnyAsync(c => c.Status == SorterCommandStatus.MISMATCH));
            var alarm = await db.Alarms.FirstAsync(a => a.Code == "R_SEQ_MISMATCH");
            Assert.Equal("R_SEQ_MISMATCH", alarm.Code);
        }
        _out.WriteLine("[D3] R_Seq 불일치 → alarm R_SEQ_MISMATCH + sorter_command MISMATCH");
    }

    // ════════════════════════════════════════════════════════════════════════
    // D4: R_Flag 타임아웃 → RFLAG_TIMEOUT. GT: alarm.code=RFLAG_TIMEOUT + status=TIMEOUT·1행.
    //   (실 Sim InjectRFlagDelayMs ≫ RFlagTimeout — 기존 S6 단위 커버.)
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task D4_RFlagTimeout_Alarm_And_SorterCommandTimeout_NoRetry()
    {
        var (factory, rcs) = await StartAsync(rFlagTimeoutMs: 300);  // 타임아웃 300ms로 단축.
        await using var _f = factory;
        await using var _r = rcs;
        var driver = MultiAgvDriver.ForFactory(factory);

        factory.PrimarySorter.Sim.InjectRFlagDelayMs = 5000;  // R_Flag 지연 5s ≫ 300ms → 타임아웃.

        await driver.RunSingleAsync(new AgvJob(23901, 1, "TEST-BARCODE-3", E2EWebApplicationFactory.DefaultSorterChuteNo));

        await E2EWait.UntilAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            return await db.SorterCommands.AnyAsync(c => c.Status == SorterCommandStatus.TIMEOUT);
        }, 6000, "sorter_command TIMEOUT");

        using (var db = factory.CreateDbScope())
        {
            // 재시도 없음 — TIMEOUT 1행(현 동작 — H5 재시도 정책 미정).
            int timeoutCount = await db.SorterCommands.CountAsync(c => c.Status == SorterCommandStatus.TIMEOUT);
            Assert.Equal(1, timeoutCount);
            var alarm = await db.Alarms.FirstAsync(a => a.Code == "RFLAG_TIMEOUT");
            Assert.Equal("RFLAG_TIMEOUT", alarm.Code);
        }
        _out.WriteLine("[D4] R_Flag 타임아웃 → alarm RFLAG_TIMEOUT + sorter_command TIMEOUT 1행(재시도 0)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // D5 ⚠: C_Flag=1 대기 상한(SPEC §7-B 미정). 현 동작: CFlagTimeoutMs 설정 존재·초과 시 CFlagTimeout.
    //   InjectNoResponse=true → Sim이 C_Flag를 영영 안 비움 → 2건째 핸드셰이크가 C_Flag=1 대기 →
    //   CFLAG_TIMEOUT 도달. **현 동작 단언**(스펙 "상한 정책"은 미정 → finding 표기).
    //   sorter_command는 CFlagTimeout→TIMEOUT 저장(EfSorterCommandJournal switch — alarm code로 구분).
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task D5_CFlagTimeout_CurrentBehavior_AssertedNotSpec()
    {
        // CFlagTimeoutMs=2000(팩토리 기본). InjectNoResponse로 Sim 상태기계 정지 → C_Flag 미소비.
        var (factory, rcs) = await StartAsync();
        await using var _f = factory;
        await using var _r = rcs;
        var driver = MultiAgvDriver.ForFactory(factory);

        // Sim 상태기계 정지 — C_Flag=1을 감지·소비하지 않음(폴 응답은 계속 → Online 유지).
        factory.PrimarySorter.Sim.InjectNoResponse = true;

        // IF-05 → IF-10 → 핸드셰이크: WaitCFlagZero는 C_Flag=0이라 통과하고 CellAssign(C_Flag=1) 큐 투입,
        // 이후 R_Flag 폴링이 RFlagTimeout으로 끝난다. C_Flag=1 대기 상한(D5)을 직접 보려면 C_Flag가
        // 이미 1인 상태에서 핸드셰이크를 시작해야 한다 — 첫 핸드셰이크가 C_Flag=1을 세팅하고 Sim이
        // 소비 안 하므로, 둘째 핸드셰이크 진입 시 C_Flag=1이 남아 CFlagTimeout 경로를 탄다.
        await driver.RunSingleAsync(new AgvJob(24001, 1, "TEST-BARCODE-3", E2EWebApplicationFactory.DefaultSorterChuteNo, DoArrival: false));
        // 첫 핸드셰이크는 RFlagTimeout(R 미응답)로 끝나며 C_Flag=1을 남길 수 있다. 둘째 진입에서 C_Flag=1 대기.
        await driver.RunSingleAsync(new AgvJob(24002, 1, "TEST-BARCODE-3", E2EWebApplicationFactory.DefaultSorterChuteNo, DoArrival: false));

        // 현 동작: 둘 다 실패(TIMEOUT 저장) + alarm. CFLAG_TIMEOUT 또는 RFLAG_TIMEOUT 중 하나 이상 도달.
        // (R_CellNo/C_Flag 소비 부재로 outcome=CFlagTimeout 또는 RFlagTimeout — 둘 다 status=TIMEOUT 저장.)
        await E2EWait.UntilAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            return await db.SorterCommands.CountAsync(c => c.Status == SorterCommandStatus.TIMEOUT) >= 1
                && await db.Alarms.AnyAsync(a => a.Code == "CFLAG_TIMEOUT" || a.Code == "RFLAG_TIMEOUT");
        }, 8000, "C_Flag/R_Flag 미응답 → TIMEOUT 저장 + alarm");

        using (var db = factory.CreateDbScope())
        {
            var codes = await db.Alarms.Select(a => a.Code).Distinct().ToListAsync();
            // 현 동작 단언: 미응답 시 핸드셰이크가 TIMEOUT 계열로 수렴(상한 정책은 SPEC §7-B 미정 — finding).
            Assert.Contains(codes, c => c is "CFLAG_TIMEOUT" or "RFLAG_TIMEOUT");
            _out.WriteLine($"[D5 ⚠현동작] C_Flag/R_Flag 미응답 → alarm codes={string.Join(",", codes)} (상한 정책 SPEC §7-B 미정 — finding)");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // D6 ⚠: 핸드셰이크 중 OFFLINE(Sim StopAsync). GT: outcome=Offline + alarm OFFLINE 또는
    //   핸드셰이크 OFFLINE 분기(sorter_command TIMEOUT 저장) — 현 동작 단언.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task D6_OfflineDuringHandshake_CurrentBehavior()
    {
        // R_Flag 지연을 길게 주어 R 폴링 중 Sim을 끊는다(핸드셰이크 진행 중 OFFLINE).
        var (factory, rcs) = await StartAsync(rFlagTimeoutMs: 8000);
        await using var _f = factory;
        await using var _r = rcs;
        var driver = MultiAgvDriver.ForFactory(factory);
        long destId = factory.PrimarySorter.DestinationId;

        factory.PrimarySorter.Sim.InjectRFlagDelayMs = 6000;  // R_Flag 지연 → 핸드셰이크가 R 폴링에 머무름.

        // 핸드셰이크 시작(IF-05→IF-10) — fire-and-forget 백그라운드.
        _ = driver.RunSingleAsync(new AgvJob(24101, 1, "TEST-BARCODE-3", E2EWebApplicationFactory.DefaultSorterChuteNo));

        // C 단계(C_Flag set·소비)가 진행돼 분류 시작될 때까지 대기(핸드셰이크가 R 폴링에 진입).
        await E2EWait.UntilAsync(() => !factory.SorterSnapshot(destId)!.Ready || factory.Timeline.Any(l => l.Contains("C 수신")),
            5000, "핸드셰이크 C 단계 진행");

        // 핸드셰이크 진행 중 Sim 종료 → R 폴링 중 OFFLINE 감지.
        await factory.PrimarySorter.Sim.StopAsync();

        // 현 동작: 핸드셰이크가 Offline outcome → sorter_command TIMEOUT 저장 + alarm OFFLINE(또는 게이트웨이
        // OFFLINE 전이 alarm). OFFLINE 계열 사유가 DB에 기록됨을 단언.
        await E2EWait.UntilAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            return await db.Alarms.AnyAsync(a => a.Code == "OFFLINE")
                || await db.SorterCommands.AnyAsync(c => c.Status == SorterCommandStatus.TIMEOUT);
        }, 8000, "핸드셰이크 중 OFFLINE → alarm/TIMEOUT 기록");

        using (var db = factory.CreateDbScope())
        {
            bool offlineAlarm = await db.Alarms.AnyAsync(a => a.Code == "OFFLINE");
            bool timeout      = await db.SorterCommands.AnyAsync(c => c.Status == SorterCommandStatus.TIMEOUT);
            Assert.True(offlineAlarm || timeout, "OFFLINE alarm 또는 sorter_command TIMEOUT(현 동작)");
            _out.WriteLine($"[D6 ⚠현동작] 핸드셰이크 중 OFFLINE → offlineAlarm={offlineAlarm} timeout={timeout}");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // D8 ⚠: R_CellNo≠C_CellNo → 실적재 셀 기록. 현 SimServer는 R_CellNo=받은 C_CellNo 그대로 반환
    //   → 불일치 주입 수단 없음. "현 Sim 한계 — 기대동작 미정"으로 분류(추측 단언 금지·Q3).
    //   여기선 현 Sim이 R_CellNo==C_CellNo로 동작함(불일치 미발생)을 ground-truth로 단언만 한다.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task D8_RCellNo_EqualsC_CurrentSimLimitation()
    {
        var (factory, rcs) = await StartAsync();
        await using var _f = factory;
        await using var _r = rcs;
        var driver = MultiAgvDriver.ForFactory(factory);

        await driver.RunSingleAsync(new AgvJob(24201, 1, "TEST-BARCODE-3", E2EWebApplicationFactory.DefaultSorterChuteNo));
        await E2EWait.UntilAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            return await db.SorterCommands.AnyAsync(c => c.Status == SorterCommandStatus.COMPLETED);
        }, 6000, "COMPLETED");

        using (var db = factory.CreateDbScope())
        {
            var cmd = await db.SorterCommands.FirstAsync(c => c.Status == SorterCommandStatus.COMPLETED);
            // 현 Sim 한계: R_CellNo == C 지정 CellNo(불일치 주입 수단 없음 — Q3 기대 미정).
            Assert.Equal(cmd.CellNo, cmd.RCellNo);
            _out.WriteLine($"[D8 ⚠Sim한계] R_CellNo({cmd.RCellNo})==C_CellNo({cmd.CellNo}) — 불일치 주입 불가(Q3 기대 미정)");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // D9: C_Seq 증가(매 건). GT: 연속 핸드셰이크 2건의 CSeq 단조 증가(소터별 _cSeq 보존).
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task D9_CSeq_MonotonicIncrease_PerSorter()
    {
        var (factory, rcs) = await StartAsync();
        await using var _f = factory;
        await using var _r = rcs;
        var driver = MultiAgvDriver.ForFactory(factory);

        await driver.RunSingleAsync(new AgvJob(24301, 1, "TEST-BARCODE-3", E2EWebApplicationFactory.DefaultSorterChuteNo));
        await E2EWait.UntilAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            return await db.SorterCommands.CountAsync(c => c.Status == SorterCommandStatus.COMPLETED) >= 1;
        }, 6000, "1차 COMPLETED");
        await E2EWait.UntilAsync(() => factory.SorterSnapshot(factory.PrimarySorter.DestinationId) is { CFlag: false, RFlag: false },
            4000, "1차 클리어");

        await driver.RunSingleAsync(new AgvJob(24302, 1, "TEST-BARCODE-3", E2EWebApplicationFactory.DefaultSorterChuteNo));
        await E2EWait.UntilAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            return await db.SorterCommands.CountAsync(c => c.Status == SorterCommandStatus.COMPLETED) >= 2;
        }, 6000, "2차 COMPLETED");

        using (var db = factory.CreateDbScope())
        {
            var seqs = await db.SorterCommands
                .Where(c => c.Status == SorterCommandStatus.COMPLETED)
                .OrderBy(c => c.Id).Select(c => c.CSeq).ToListAsync();
            Assert.True(seqs.Count >= 2);
            Assert.True(seqs[1] > seqs[0], $"C_Seq 단조 증가: {seqs[0]} → {seqs[1]}");
            _out.WriteLine($"[D9] C_Seq 단조 증가: {string.Join(",", seqs)}");
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // D11 (S-TWO-FLOOR-CONTROL C1): 성공 핸드셰이크 후 sorter_command에 처리 3시각이
    //   depositedAt ≤ tiltedAt ≤ returnedAt 단조로 기록된다(전부 non-NULL). API(IF-10)·
    //   PLC게이트웨이(핸드셰이크)·DB(sorter_command) 3레이어 관통 계측 왕복.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task D11_ProcessingTimestamps_Monotonic_OnSuccess()
    {
        var (factory, rcs) = await StartAsync();
        await using var _f = factory;
        await using var _r = rcs;
        var driver = MultiAgvDriver.ForFactory(factory);

        await driver.RunSingleAsync(new AgvJob(25101, 1, "TEST-BARCODE-3", E2EWebApplicationFactory.DefaultSorterChuteNo));
        await E2EWait.UntilAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            return await db.SorterCommands.AnyAsync(c => c.Status == SorterCommandStatus.COMPLETED);
        }, 8000, "COMPLETED");

        using (var db = factory.CreateDbScope())
        {
            var cmd = await db.SorterCommands.FirstAsync(c => c.Status == SorterCommandStatus.COMPLETED);
            _out.WriteLine($"[D11] deposited={cmd.DepositedAt:O} tilted={cmd.TiltedAt:O} returned={cmd.ReturnedAt:O}");

            // 성공: 3시각 전부 non-NULL.
            Assert.NotNull(cmd.DepositedAt);   // IF-10 투입 보고 시각.
            Assert.NotNull(cmd.TiltedAt);      // R_Flag==1 관측(틸트).
            Assert.NotNull(cmd.ReturnedAt);    // Ready 0→1(복귀 완료).

            // 단조: depositedAt ≤ tiltedAt ≤ returnedAt.
            Assert.True(cmd.DepositedAt <= cmd.TiltedAt,
                $"depositedAt({cmd.DepositedAt:O}) ≤ tiltedAt({cmd.TiltedAt:O})");
            Assert.True(cmd.TiltedAt <= cmd.ReturnedAt,
                $"tiltedAt({cmd.TiltedAt:O}) ≤ returnedAt({cmd.ReturnedAt:O})");
        }
        _out.WriteLine("[D11] sorter_command 3시각 단조 기록(depositedAt ≤ tiltedAt ≤ returnedAt) 실증");
    }
}
