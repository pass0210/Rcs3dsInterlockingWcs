using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api;
using Wcs.Data;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests.E2E;

// ════════════════════════════════════════════════════════════════════════════
// S-TWO-FLOOR-CONTROL 서브 스프린트 A — 인덕션 파생 층 폐루프 크로스레이어 E2E.
//
//   RCS IF-05(HTTP) → inductionNo→InductionFloorMap 파생 F → 소터별 pending-floor 큐 enqueue(상태) →
//   관측 루프(SorterFloorReturnService)가 TgtFloor==0 관측 → DepositDecider(Core 순수) 게이트 통과 시
//   소터별 단일 쓰기 큐 SetTgtFloor(PlcGateway) → Sim3ds(Modbus 슬레이브) 이동 → CurFloor=F 기입 →
//   WCS 폴 스냅샷 반영 → operation_log/piece_event(DB) 영속.
//
//   HTTP API ↔ 인메모리 큐 ↔ Modbus 게이트웨이 ↔ 실 Sim3ds ↔ 영속화를 횡단하는 폐루프.
//   절대규칙 #1(단일 쓰기 큐)·#3(WCS 클리어 0 — WCS는 D6에 1/2만 쓰고 0은 절대 안 씀) 준수 입증.
// ════════════════════════════════════════════════════════════════════════════

[Collection("RealSimSerial")]
public class E2EGroupK_TwoFloorReturnTests
{
    private readonly ITestOutputHelper _out;
    public E2EGroupK_TwoFloorReturnTests(ITestOutputHelper output) => _out = output;

    // ════════════════════════════════════════════════════════════════════════
    // K1: 단일 층 복귀(격리) — 1층·2층 각각 폐루프 동작. 초기 CurFloor에서 F로 이동·복귀.
    //   (초기 TgtFloor=0 → 단일 층 변경이라 잔류-TgtFloor 핑퐁 무관 — 결정적.)
    // ════════════════════════════════════════════════════════════════════════
    [Theory]
    [InlineData(2, 1, 1)]   // 초기 층2 · inductionNo=1→F=1 → CurFloor 1로 복귀.
    [InlineData(1, 2, 2)]   // 초기 층1 · inductionNo=2→F=2 → CurFloor 2로 복귀.
    public async Task K1_InductionDerivedFloor_ClosedLoop_ReturnsToFloor(int initialFloor, int inductionNo, int expectedFloor)
    {
        var factory = new E2EWebApplicationFactory(
            rcsBaseUrl: null, initialCurFloor: initialFloor,
            inductionFloorMap: new Dictionary<int, int> { [1] = 1, [2] = 2 });
        await factory.StartSimsAsync();
        await using var _f = factory;
        _ = factory.CreateClient();
        long destId = factory.PrimarySorter.DestinationId;
        int  chuteNo = factory.PrimarySorter.ChuteNo;

        await E2EWait.UntilAsync(() => factory.IsSorterOnline(destId), 5000, "소터 Online");
        await E2EWait.UntilAsync(() => factory.SorterSnapshot(destId)?.CurFloor == initialFloor, 3000, $"초기 CurFloor={initialFloor}");

        using var client = factory.CreateClient();

        // IF-05(소터, inductionNo→F) → 큐 enqueue만(OK 응답). TgtFloor 쓰기는 관측 루프가 트리거.
        var resp = await client.PostAsJsonAsync("/api/v1/destination-query",
            new { pId = 24800 + inductionNo, agvNo = 1, barcode = "TEST-BARCODE-3", inductionNo, qty = 1, timeStamp = (string?)null });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("OK", (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetString());

        // 관측 루프가 TgtFloor==0 관측 → F 기입(단일 쓰기 큐) → Sim 이동 → CurFloor=F 복귀·Ready=1.
        await E2EWait.UntilAsync(() => factory.SorterSnapshot(destId) is { CurFloor: var cf, Ready: true } && cf == expectedFloor,
            6000, $"F={expectedFloor} 복귀(CurFloor={expectedFloor})");

        // Modbus 기입 입증: Sim 타임라인에 D6=F 기입(WCS→Sim 실 트랜잭션).
        Assert.True(factory.Timeline.Any(l => l.Contains("WCS 쓰기 수신: D6") && l.Contains($"→{expectedFloor}")),
            $"D6={expectedFloor} 기입(Modbus 트랜잭션)");
        // 절대규칙 #3: WCS는 D6에 0을 쓰지 않는다 — 타임라인 D6 쓰기는 전부 1/2(클리어 0).
        Assert.DoesNotContain(factory.Timeline, l => l.Contains("WCS 쓰기 수신: D6") && l.Contains("→0"));

        // DB 크로스레이어: operation_log PLC_WRITE/SET_TGTFLOOR(floor=F) 영속.
        await E2EWait.UntilAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            return await db.OperationLogs.AnyAsync(l =>
                l.Category == OperationLogCategory.PLC_WRITE && l.Action == "SET_TGTFLOOR"
                && l.Detail!.Contains($"\"floor\":{expectedFloor}"));
        }, 5000, $"operation_log SET_TGTFLOOR floor={expectedFloor} 영속");

        _out.WriteLine($"[K1] induction={inductionNo}→F={expectedFloor}: CurFloor {initialFloor}→{expectedFloor} 폐루프(HTTP↔큐↔Modbus↔Sim↔DB)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // K2: 다층 FIFO — F=1,2,1 순으로 (피스별 투입 사이클로) CurFloor가 1→2→1 순으로 한 번에 하나씩 복귀.
    //   각 피스 IF-05→IF-09→IF-10(투입·분류) — PLC가 분류 시작 시 TgtFloor=0 클리어 → 다음 층 기입 가능.
    //   (연속 다른 층은 분류로 TgtFloor가 비워져야 진행 — 잔류-TgtFloor 핑퐁 차단의 정합 동작.)
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task K2_MultiFloor_Fifo_1_2_1_ReturnsOneAtATime_WithDeposits()
    {
        var factory = new E2EWebApplicationFactory(
            rcsBaseUrl: null, initialCurFloor: 2,
            inductionFloorMap: new Dictionary<int, int> { [1] = 1, [2] = 2 });
        await factory.StartSimsAsync();
        await using var _f = factory;
        _ = factory.CreateClient();
        long destId = factory.PrimarySorter.DestinationId;
        int  chuteNo = factory.PrimarySorter.ChuteNo;
        using var client = factory.CreateClient();

        await E2EWait.UntilAsync(() => factory.IsSorterOnline(destId), 5000, "소터 Online");
        await E2EWait.UntilAsync(() => factory.SorterSnapshot(destId)?.CurFloor == 2, 3000, "초기 CurFloor=2");

        // 피스별(F=1,2,1): ① IF-05(enqueue F) → 관측 루프가 F로 정렬(CurFloor=F 도착) → ② 정렬 완료 후
        //   IF-09(도착)+IF-10(투입) → 핸드셰이크·분류 → PLC가 TgtFloor=0 클리어. "열림(정렬) 후 투입" 순서를
        //   지켜(AGV는 열림 push 후 도착) C_Flag가 이동과 경합하지 않게 한다(현장 정합).
        var seq = new (int pId, int inductionNo, int expectedFloor)[]
        {
            (24101, 1, 1),   // F=1 → CurFloor 2→1
            (24102, 2, 2),   // F=2 → CurFloor 1→2
            (24103, 1, 1),   // F=1 → CurFloor 2→1
        };

        int completedExpected = 0;
        foreach (var (pId, inductionNo, expectedFloor) in seq)
        {
            // ① IF-05 — 소터 큐에 F enqueue(OK). TgtFloor 쓰기는 관측 루프 트리거.
            var if05 = await client.PostAsJsonAsync("/api/v1/destination-query",
                new { pId, agvNo = 1, barcode = "TEST-BARCODE-3", inductionNo, qty = 1, timeStamp = (string?)null });
            Assert.Equal(HttpStatusCode.OK, if05.StatusCode);
            Assert.Equal("OK", (await if05.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetString());

            // 관측 루프가 F로 정렬 → CurFloor=F 도착·Ready=1(이동 완료·정렬 안정) 대기(폐루프 — 한 번에 하나씩).
            await E2EWait.UntilAsync(() =>
            {
                var s = factory.SorterSnapshot(destId);
                return s is { Ready: true } && s.CurFloor == expectedFloor;
            }, 8000, $"pId={pId} F={expectedFloor} 정렬 완료(CurFloor={expectedFloor})");

            // ② 정렬(열림) 후 도착·투입 — 소터가 그 층에 있을 때 분류(C_Flag 이동 경합 없음).
            await client.PostAsJsonAsync("/api/v1/arrival-report",
                new { pId, chuteNo, agvNo = 1, timeStamp = (string?)null });
            await client.PostAsJsonAsync("/api/v1/deposit-report",
                new { pId, barcode = "TEST-BARCODE-3", chuteNo, agvNo = 1 });

            // 핸드셰이크 완료(분류 → PLC TgtFloor 클리어) 대기 — 다음 피스가 깨끗한 TgtFloor=0에서 시작.
            completedExpected++;
            int expected = completedExpected;
            await E2EWait.UntilAsync(async () =>
            {
                using var db = factory.CreateDbScope();
                return await db.SorterCommands.CountAsync(c => c.Status == SorterCommandStatus.COMPLETED) >= expected;
            }, 8000, $"pId={pId} 핸드셰이크 COMPLETED ≥{expected}");
            await E2EWait.UntilAsync(() => factory.SorterSnapshot(destId) is { TgtFloor: 0, Ready: true }, 5000,
                $"pId={pId} 분류 완료 후 TgtFloor=0·Ready=1");
        }

        // 최종 상태: 마지막 F=1 복귀. D6 기입에 1·2 모두 등장(양층 복귀).
        Assert.Equal(1, factory.SorterSnapshot(destId)!.CurFloor);
        Assert.True(factory.Timeline.Any(l => l.Contains("WCS 쓰기 수신: D6") && l.Contains("→1")), "D6=1 기입");
        Assert.True(factory.Timeline.Any(l => l.Contains("WCS 쓰기 수신: D6") && l.Contains("→2")), "D6=2 기입");
        // 절대규칙 #3: WCS는 D6=0을 쓰지 않는다(클리어는 PLC 몫).
        Assert.DoesNotContain(factory.Timeline, l => l.Contains("WCS 쓰기 수신: D6") && l.Contains("→0"));

        _out.WriteLine("[K2] FIFO F=1,2,1 → CurFloor 1→2→1 한 번에 하나씩 복귀(피스별 투입·분류 사이클)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // K3 [I-1 핵심]: 같은층 연속 + 다른층 [A,A,B] 다중 AGV — A 2건이 모두 분류 완료되기 전엔 소터가
    //   B로 이동하지 않음(2번째 A-AGV 고립 방지). pop=분류사이클(Ready 1→0→1) 단위 검증.
    //   A=1층(inductionNo=1) 2건 + B=2층(inductionNo=2) 1건을 먼저 전부 enqueue(큐 [1,1,2]) 후,
    //   A1만 투입·분류 → 소터가 여전히 1층 유지(큐 [1,2])임을 단언 → A2 투입·분류 후에야 B(2층)로 이동.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task K3_MultiAgv_SameFloorConsecutive_ThenOther_HoldsFloorUntilBothClassified()
    {
        // A=1층(induction 1), B=2층(induction 2). 소터 초기 CurFloor=1(A층) — A 정렬 이동 불요(깨끗한 검증).
        var factory = new E2EWebApplicationFactory(
            rcsBaseUrl: null, initialCurFloor: 1,
            inductionFloorMap: new Dictionary<int, int> { [1] = 1, [2] = 2 });
        await factory.StartSimsAsync();
        await using var _f = factory;
        _ = factory.CreateClient();
        long destId  = factory.PrimarySorter.DestinationId;
        int  chuteNo = factory.PrimarySorter.ChuteNo;
        var  queues  = factory.Services.GetRequiredService<SorterPendingFloorQueues>();
        using var client = factory.CreateClient();

        await E2EWait.UntilAsync(() => factory.IsSorterOnline(destId), 5000, "소터 Online");
        await E2EWait.UntilAsync(() => factory.SorterSnapshot(destId)?.CurFloor == 1, 3000, "초기 CurFloor=1(A층)");

        // ── [A,A,B] 전부 enqueue (IF-05 induction 1,1,2) — 큐 [1,1,2] ──────────────
        foreach (var (pId, inductionNo) in new[] { (24301, 1), (24302, 1), (24303, 2) })
        {
            var r = await client.PostAsJsonAsync("/api/v1/destination-query",
                new { pId, agvNo = 1, barcode = "TEST-BARCODE-3", inductionNo, qty = 1, timeStamp = (string?)null });
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            Assert.Equal("OK", (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetString());
        }

        // 소터는 이미 A(1층)에 있으므로 이동/pop 없이 큐 [1,1,2] 유지(조기 소비 0 — 구 버그면 즉시 [2]로 드레인).
        await E2EWait.UntilExactAsync(() => queues.Count(destId), 3, stableCount: 6, timeoutMs: 3000,
            "enqueue 후 큐 [1,1,2] 유지(조기 pop 0)");
        Assert.Equal(new[] { 1, 1, 2 }, queues.Snapshot(destId));
        Assert.Equal(1, factory.SorterSnapshot(destId)!.CurFloor);

        // ── A1만 투입·분류 → 사이클 완료 시 큐 머리 1건(A)만 pop → 큐 [1,2] ───────────
        await client.PostAsJsonAsync("/api/v1/arrival-report", new { pId = 24301, chuteNo, agvNo = 1, timeStamp = (string?)null });
        await client.PostAsJsonAsync("/api/v1/deposit-report", new { pId = 24301, barcode = "TEST-BARCODE-3", chuteNo, agvNo = 1 });
        await E2EWait.UntilAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            return await db.SorterCommands.CountAsync(c => c.Status == SorterCommandStatus.COMPLETED) >= 1;
        }, 8000, "A1 핸드셰이크 COMPLETED ≥1");

        // ★ 핵심 단언: A1 분류 후에도 소터는 **1층 유지**(B로 이동 안 함) — 2번째 A-AGV 미고립.
        //   큐는 [1,2]로 A 1건만 pop(전부 pop 아님). D6=2 기입은 아직 0(B 이동 미발생).
        await E2EWait.UntilAsync(() => queues.Count(destId) == 2, 5000, "A1 분류 후 큐 머리 1건만 pop([1,2])");
        Assert.Equal(new[] { 1, 2 }, queues.Snapshot(destId));
        await E2EWait.UntilExactAsync(() => factory.SorterSnapshot(destId)!.CurFloor, 1, stableCount: 6, timeoutMs: 3000,
            "A1 분류 후 소터 1층 유지(B로 이동 안 함 — A2 미투하 고립 방지)");
        Assert.False(factory.Timeline.Any(l => l.Contains("WCS 쓰기 수신: D6") && l.Contains("→2")),
            "A2 분류 전에는 B(2층) 기입 0(소터가 A층을 지킴)");

        // ── A2 투입·분류 → 큐 [2] → 이제서야 B(2층)로 이동 ────────────────────────
        await client.PostAsJsonAsync("/api/v1/arrival-report", new { pId = 24302, chuteNo, agvNo = 1, timeStamp = (string?)null });
        await client.PostAsJsonAsync("/api/v1/deposit-report", new { pId = 24302, barcode = "TEST-BARCODE-3", chuteNo, agvNo = 1 });
        await E2EWait.UntilAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            return await db.SorterCommands.CountAsync(c => c.Status == SorterCommandStatus.COMPLETED) >= 2;
        }, 8000, "A2 핸드셰이크 COMPLETED ≥2");

        // A 2건 모두 분류 완료 → 큐 [2] → 소터 B(2층)로 이동.
        await E2EWait.UntilAsync(() => factory.SorterSnapshot(destId) is { CurFloor: 2 }, 8000,
            "A 2건 분류 완료 후 B(2층) 이동");

        // ── B 투입·분류 → 큐 빔 ─────────────────────────────────────────────────
        await client.PostAsJsonAsync("/api/v1/arrival-report", new { pId = 24303, chuteNo, agvNo = 1, timeStamp = (string?)null });
        await client.PostAsJsonAsync("/api/v1/deposit-report", new { pId = 24303, barcode = "TEST-BARCODE-3", chuteNo, agvNo = 1 });
        await E2EWait.UntilAsync(() => queues.Count(destId) == 0, 8000, "B 분류 후 큐 빔");

        Assert.Equal(2, factory.SorterSnapshot(destId)!.CurFloor);
        Assert.DoesNotContain(factory.Timeline, l => l.Contains("WCS 쓰기 수신: D6") && l.Contains("→0"));  // 절대규칙 #3.
        _out.WriteLine("[K3] [A,A,B] — A 2건 모두 분류 완료 전 소터 A층 유지(2번째 A-AGV 미고립), 이후 B 이동");
    }
}
