using Wcs.Api;
using Wcs.Core;
using Xunit;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// S-TWO-FLOOR-CONTROL 서브 스프린트 A — 층 파생(순수) + 소터별 pending-floor 큐(상태) 단위 검증.
//   판정(DepositDecider)의 F 파라미터화는 DepositDeciderTests가 커버. 여기서는 층맵 파생과
//   FIFO 큐의 순서·pop 계약을 검증한다(테스트가 스펙).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>인덕션 → 층 파생 순수 함수(Wcs.Core.InductionFloorMap) — 매핑·미매핑·Fail-Loud null.</summary>
public class InductionFloorMapTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    public void DeriveFloor_MappedInduction_ReturnsFloor(int inductionNo, int expectedFloor)
    {
        // SPEC §5-A 예시 맵 {"1":1,"2":1,"3":2}.
        var map = new Dictionary<int, int> { [1] = 1, [2] = 1, [3] = 2 };
        Assert.Equal(expectedFloor, InductionFloorMap.DeriveFloor(map, inductionNo));
    }

    [Fact]
    public void DeriveFloor_UnmappedInduction_ReturnsNull_ForFailLoud()
    {
        // 미매핑 inductionNo는 null → 호출자(IF-05)가 NG + 경고(조용한 통과·기본층 폴백 금지, 확정 2026-07-22).
        var map = new Dictionary<int, int> { [1] = 1, [2] = 2 };
        Assert.Null(InductionFloorMap.DeriveFloor(map, inductionNo: 99));
    }

    [Fact]
    public void DeriveFloor_EmptyMap_ReturnsNull()
    {
        Assert.Null(InductionFloorMap.DeriveFloor(new Dictionary<int, int>(), inductionNo: 1));
    }

    [Fact]
    public void WcsOptions_FloorByInduction_ParsesStringKeys_SkipsUnparsable()
    {
        // 설정 바인딩은 문자열 키 — FloorByInduction이 int 키 맵으로 변환. 파싱 불가 키는 무시.
        var opt = new WcsOptions
        {
            InductionFloorMap = new Dictionary<string, int> { ["1"] = 1, ["3"] = 2, ["bad"] = 9 },
        };
        var map = opt.FloorByInduction;
        Assert.Equal(1, InductionFloorMap.DeriveFloor(map, 1));
        Assert.Equal(2, InductionFloorMap.DeriveFloor(map, 3));
        Assert.Equal(2, map.Count);   // "bad" 스킵.
    }
}

/// <summary>소터별 pending-floor FIFO 큐(SorterPendingFloorQueues) — 순서·peek/pop·격리·동시성.</summary>
public class SorterPendingFloorQueuesTests
{
    [Fact]
    public void Enqueue_PreservesFifoOrder_IncludingDuplicateFloors()
    {
        var q = new SorterPendingFloorQueues();
        // 연속 동일층도 매 피스 1건씩 stack(dedupe 안 함, 확정 2026-07-22).
        q.Enqueue(10, 1);
        q.Enqueue(10, 2);
        q.Enqueue(10, 1);
        q.Enqueue(10, 1);

        Assert.Equal(new[] { 1, 2, 1, 1 }, q.Snapshot(10));
        Assert.Equal(4, q.Count(10));
    }

    [Fact]
    public void TryPeek_DoesNotRemove_TryPop_Removes_InFifoOrder()
    {
        var q = new SorterPendingFloorQueues();
        q.Enqueue(10, 1);
        q.Enqueue(10, 2);

        Assert.True(q.TryPeek(10, out var head));
        Assert.Equal(1, head);
        Assert.Equal(2, q.Count(10));            // peek는 제거 안 함.

        Assert.True(q.TryPop(10, out var popped));
        Assert.Equal(1, popped);                 // FIFO: 머리 1 먼저.
        Assert.Equal(new[] { 2 }, q.Snapshot(10));

        Assert.True(q.TryPop(10, out popped));
        Assert.Equal(2, popped);
        Assert.False(q.TryPop(10, out _));       // 비면 false.
        Assert.False(q.TryPeek(10, out _));
        Assert.Equal(0, q.Count(10));
    }

    [Fact]
    public void EmptyOrUnknownDestination_PeekPop_ReturnFalse()
    {
        var q = new SorterPendingFloorQueues();
        Assert.False(q.TryPeek(999, out _));
        Assert.False(q.TryPop(999, out _));
        Assert.Equal(0, q.Count(999));
        Assert.Empty(q.Snapshot(999));
    }

    [Fact]
    public void MultipleDestinations_AreIsolated()
    {
        var q = new SorterPendingFloorQueues();
        q.Enqueue(10, 1);
        q.Enqueue(20, 2);
        q.Enqueue(10, 2);

        Assert.Equal(new[] { 1, 2 }, q.Snapshot(10));
        Assert.Equal(new[] { 2 },    q.Snapshot(20));
    }

    [Fact]
    public async Task ConcurrentEnqueue_SingleConsumer_NoLoss()
    {
        // 다중 AGV IF-05 동시 enqueue(생산자 다수) vs 관측 루프 dequeue(소비자 1개) — 손실 0 검증.
        var q = new SorterPendingFloorQueues();
        const int producers = 8;
        const int perProducer = 250;
        using var barrier = new Barrier(producers);

        var tasks = Enumerable.Range(0, producers).Select(_ => Task.Run(() =>
        {
            barrier.SignalAndWait();
            for (int i = 0; i < perProducer; i++)
                q.Enqueue(10, (i % 2) + 1);   // 층 1/2 교차 enqueue.
        })).ToArray();
        await Task.WhenAll(tasks);

        int total = producers * perProducer;
        int drained = 0;
        while (q.TryPop(10, out _)) drained++;   // 단일 소비자 드레인.
        Assert.Equal(total, drained);            // enqueue 전량 소비(손실·중복 0).
    }
}
