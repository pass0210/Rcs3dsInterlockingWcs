using Microsoft.Extensions.DependencyInjection;
using Wcs.Api;
using Wcs.Data;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// S-HARDENING-1 항목 1 — 슈트 복구 하트비트 검증 (DestinationStatusPusher 관찰 루프 확장)
//
// 배경: 슈트 push가 재시도 소진으로 실패(Acked≠Computed)하면, 종전엔 다음 슈트 이벤트
// (IF-05 예약/IF-10 투입/비움/운영자 전이)까지 stale로 남았다. 만재 슈트는 그 이벤트가
// 오지 않아 RCS가 "받을 수 있음"으로 무기한 오인(침묵 실패) — S-IF08 Code Review Minor #1.
//
// 하트비트: 기존 관찰 루프(SorterObserveIntervalMs cadence 재사용 — 신규 타이밍 상수 0)가
// **미동기(Acked≠Computed) 슈트만** 재평가·재발신한다. 동기된 슈트는 건드리지 않는다.
//
// 검증(가짜 RCS 수신 본문 ground-truth — 인메모리 GREEN 아님):
//   HB-1 (VS-1)  만재 슈트 2건 push 실패(RCS 다운) → 복구 → **후속 슈트 이벤트 0**으로
//                관찰 주기 경과만으로 최신 상태(2) 재도달. 재도달 후 재발신 정지(동기 완료).
//   HB-2 (VS-5)  동기된(Acked==Computed) 슈트는 관찰 주기 반복에도 재발신 0(성공·실패 시도
//                모두 0 — 폭주 금지) + 같은 chuteNo 모순(3↔2 교차) 발신 0.
// (VS-6 DORMANT 불변은 기존 PUSH8/CS_PUSH_6이 커버 — BaseUrl 미설정 시 관찰 루프 자체가
//  미기동이므로 하트비트 포함 발신 0. 이 파일은 활성 경로만 신규 검증.)
// ════════════════════════════════════════════════════════════════════════════

public class ChuteRecoveryPushHeartbeatTests
{
    private readonly ITestOutputHelper _out;
    public ChuteRecoveryPushHeartbeatTests(ITestOutputHelper output) => _out = output;

    // ── 헬퍼: 조건 폴링(RcsPushTests와 동형 — bare sleep 0) ────────────────────
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

    /// <summary>count()가 expected를 stableCount회 연속 반환할 때까지 폴링(추가 발신 없음=안정).</summary>
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

    // ════════════════════════════════════════════════════════════════════════
    // HB-1 (VS-1): 만재 슈트 2건 — RCS 다운 중 push 재시도 소진 → RCS 복구 →
    //   후속 슈트 이벤트 없이 관찰 주기 경과만으로 최신 상태(2) 재도달(자율 복구).
    //   재도달 후 그 슈트 재발신 정지 + 전 시스템 발신 시도 자체 정지(폭주 0).
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task HB1_ChuteRecoveryHeartbeat_FullChutes_Redelivered_WithoutSubsequentEvents()
    {
        await using var rcs = await FakeChuteStateServer.StartAsync();
        // 재시도 2회·짧은 백오프(테스트 속도) — 설정 경유(하드코딩 0). 관찰 주기 30ms(팩토리 설정).
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl, retryCount: 2, retryBaseDelayMs: 30);
        _ = factory.CreateClient();

        // 부트스트랩 정착 — 슈트 1·2(NORMAL) 각 1건(next_state 3).
        await WaitUntilAsync(() => rcs.CountFor(1) >= 1 && rcs.CountFor(2) >= 1, 8000, "부트스트랩 슈트1·2 수신");
        await WaitUntilExactAsync(() => rcs.CountFor(1) + rcs.CountFor(2), 2, stableCount: 5, timeoutMs: 4000,
            "부트스트랩 후 안정(슈트1·2 각 1건)");
        Assert.Equal(new[] { 3 }, rcs.LastFor(1)!.NextStates);
        Assert.Equal(new[] { 3 }, rcs.LastFor(2)!.NextStates);

        long dest1Id, dest2Id;
        int  full1,  full2;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            var d1 = db.Destinations.First(d => d.ChuteNo == 1 && d.DestType == Wcs.Data.DestType.CHUTE);
            var d2 = db.Destinations.First(d => d.ChuteNo == 2 && d.DestType == Wcs.Data.DestType.CHUTE);
            dest1Id = d1.Id;
            dest2Id = d2.Id;
            full1 = db.ChuteDetails.First(cd => cd.DestinationId == d1.Id).WorkFullQty;
            full2 = db.ChuteDetails.First(cd => cd.DestinationId == d2.Id).WorkFullQty;
        }

        // (a) RCS 다운(503 거부) 상태에서 만재 슈트 2건 전이(3→2) — push 재시도 소진(Acked stale).
        rcs.StartRejecting();
        using (var scope = factory.Services.CreateScope())
        {
            var capacity = scope.ServiceProvider.GetRequiredService<IChuteCapacityService>();
            capacity.OnReserved(dest1Id, full1);   // 슈트1 만재 진입 = 3→2 전이.
            capacity.OnReserved(dest2Id, full2);   // 슈트2 만재 진입 = 3→2 전이.
        }

        // 거부 중 실패 시도(Accepted=false)는 관측되나 성공 delivery는 불변(부트 1건뿐) —
        // 만재 상태(2)가 RCS에 미도달(stale)임을 확정.
        await WaitUntilAsync(
            () => rcs.All.Count(p => !p.Accepted && p.ChuteNumbers.Contains(1)) >= 1
               && rcs.All.Count(p => !p.Accepted && p.ChuteNumbers.Contains(2)) >= 1,
            5000, "거부 중 슈트1·2 발신 시도 관측(전부 실패)");
        Assert.Equal(1, rcs.CountFor(1));
        Assert.Equal(1, rcs.CountFor(2));
        _out.WriteLine($"[HB-1] RCS 다운 중 만재 전이 2건 — 성공 delivery 슈트1={rcs.CountFor(1)}·슈트2={rcs.CountFor(2)}(부트뿐·stale)");

        // (b) RCS 복구(수신 재개).
        rcs.StopRejecting();

        // (c) ★핵심★ 후속 슈트 이벤트 0 — 관찰 주기 경과만으로 하트비트가 만재(2)를 재발신·도달.
        //   (여기서 capacity/운영자/IF-* 아무것도 호출하지 않는다.)
        await WaitUntilAsync(() => rcs.CountFor(1) >= 2 && rcs.CountFor(2) >= 2, 5000,
            "복구 후 이벤트 없이 하트비트 재도달(만재 슈트 2건)");
        Assert.Equal(new[] { 2 }, rcs.LastFor(1)!.NextStates);   // 만재 → 수용불가(2) 재도달.
        Assert.Equal(new[] { 2 }, rcs.LastFor(2)!.NextStates);

        // 재도달은 슈트당 정확히 1건 — 동기 완료(Acked==Computed) 후 하트비트가 그 슈트를 더 이상
        // 재구동하지 않는다(전이당 1회 불변·폭주 0).
        await WaitUntilExactAsync(() => rcs.CountFor(1), 2, stableCount: 8, timeoutMs: 5000, "슈트1 재도달 1건 안정");
        await WaitUntilExactAsync(() => rcs.CountFor(2), 2, stableCount: 8, timeoutMs: 5000, "슈트2 재도달 1건 안정");

        // 전 목적지 동기 완료 → 발신 "시도" 자체가 정지(실패 시도 포함 0 — 하트비트 유휴).
        int frozen = rcs.All.Count;
        await WaitUntilExactAsync(() => rcs.All.Count, frozen, stableCount: 8, timeoutMs: 5000,
            "복구·동기 완료 후 전체 발신 시도 정지(하트비트 폭주 0)");

        _out.WriteLine($"[HB-1] 복구 후 이벤트 0으로 만재 2건 재도달(각 1건) — 이후 시도 정지(총 {frozen}건에서 stable)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // HB-2 (VS-5): 동기된(Acked==Computed) 슈트는 관찰 주기가 반복돼도 재발신 0
    //   (성공·실패 시도 모두 0 — 하트비트는 미동기 슈트만 재구동). 같은 chuteNo 모순 발신 0.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task HB2_SyncedChute_NoHeartbeatResend_NoContradiction()
    {
        await using var rcs     = await FakeChuteStateServer.StartAsync();
        await using var factory = new RcsPushWebApplicationFactory(rcs.BaseUrl);
        _ = factory.CreateClient();

        // 부트스트랩 정착(슈트 4 = NORMAL → 3, 1건).
        await WaitUntilAsync(() => rcs.CountFor(4) >= 1, 8000, "부트스트랩 슈트4 수신");
        await WaitUntilExactAsync(() => rcs.CountFor(4), 1, stableCount: 5, timeoutMs: 4000, "부트스트랩 안정");

        // 만재 전이(3→2) — 정상 delivery 1건(RCS 정상이므로 즉시 동기).
        using (var scope = factory.Services.CreateScope())
        {
            var db       = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            var capacity = scope.ServiceProvider.GetRequiredService<IChuteCapacityService>();
            var dest4    = db.Destinations.First(d => d.ChuteNo == 4 && d.DestType == Wcs.Data.DestType.CHUTE);
            var detail4  = db.ChuteDetails.First(cd => cd.DestinationId == dest4.Id);
            capacity.OnReserved(dest4.Id, detail4.WorkFullQty);
        }
        await WaitUntilAsync(() => rcs.CountFor(4) >= 2, 5000, "만재 전이(3→2) 발신");
        Assert.Equal(new[] { 2 }, rcs.LastFor(4)!.NextStates);

        // 동기 완료 후 관찰 주기(30ms) 수십 회 경과 — 슈트4 재발신 0(하트비트가 동기 슈트를 안 건드림).
        await WaitUntilExactAsync(() => rcs.CountFor(4), 2, stableCount: 10, timeoutMs: 5000,
            "동기 슈트4 재발신 0(관찰 주기 반복에도 stable)");

        // 전체 발신 "시도"(실패 포함)도 stable — 전 목적지 동기 상태에서 하트비트 유휴(폭주 0).
        int frozenAll = rcs.All.Count;
        await WaitUntilExactAsync(() => rcs.All.Count, frozenAll, stableCount: 10, timeoutMs: 5000,
            "전체 발신 시도 stable(하트비트 무발신)");

        // 무모순: 슈트4 수신 이력 = 정확히 [3(부트), 2(만재)] — 같은 chuteNo에 3↔2 교차 발신 0.
        var seq = rcs.All.Where(p => p.ChuteNumbers.Contains(4))
                         .Select(p => p.NextStates.Single())
                         .ToArray();
        Assert.Equal(new[] { 3, 2 }, seq);

        _out.WriteLine($"[HB-2] 동기 슈트 재발신 0·전체 시도 stable({frozenAll}건)·이력 [3,2] 무모순");
    }
}
