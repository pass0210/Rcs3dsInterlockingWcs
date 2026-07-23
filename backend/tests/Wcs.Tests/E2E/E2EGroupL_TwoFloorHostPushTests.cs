using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Wcs.Api;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests.E2E;

// ════════════════════════════════════════════════════════════════════════════
// S-TWO-FLOOR-CONTROL 서브 스프린트 B — VS-E1: PLC 스냅샷 CurFloor 전이 → 층별 호스트 HTTP push.
//
//   실 Sim3ds(Modbus TCP) 소터를 1층 정렬(CurFloor=1)→재정렬(CurFloor=2)로 물리 전이시키고, 흐름:
//     게이트웨이 폴링 스냅샷 → DestinationStatusService.Compute(CurFloor 기준 readiness) →
//     DestinationStatusPusher 층별 라우팅 → 층별 HTTP 클라이언트 → fake RCS 서버 A(1층)/B(2층).
//   단언: CurFloor=1 구간 A=3·B=2, 재정렬 후 A=2·B=3 가 각 호스트 수신 이력에 순서대로 나타남.
//   (레이어: Modbus 스냅샷 → 판정 서비스 → push 라우팅 → HTTP — 크로스레이어 폐루프.)
// ════════════════════════════════════════════════════════════════════════════
[Collection("RealSimSerial")]
public class E2EGroupL_TwoFloorHostPushTests
{
    private readonly ITestOutputHelper _out;
    public E2EGroupL_TwoFloorHostPushTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task L1_SorterCurFloorTransition_1to2_RoutesPerFloorHost()
    {
        await using var srvA = await FakeChuteStateServer.StartAsync();   // 1층 호스트
        await using var srvB = await FakeChuteStateServer.StartAsync();   // 2층 호스트

        // 실 Sim3ds — 초기 CurFloor=1. 층 호스트 맵 {1:srvA, 2:srvB}. inductionMap 1→1·2→2.
        var factory = new E2EWebApplicationFactory(
            initialCurFloor: 1,
            inductionFloorMap: new Dictionary<int, int> { [1] = 1, [2] = 2 },
            floorHosts: new Dictionary<int, string> { [1] = srvA.BaseUrl, [2] = srvB.BaseUrl });
        await factory.StartSimsAsync();
        await using var _f = factory;
        var client = factory.CreateClient();

        long destId  = factory.PrimarySorter.DestinationId;
        int  chuteNo = factory.PrimarySorter.ChuteNo;

        await E2EWait.UntilAsync(() => factory.IsSorterOnline(destId), 6000, "소터 Online");
        await E2EWait.UntilAsync(() => factory.SorterSnapshot(destId)?.CurFloor == 1, 4000, "초기 CurFloor=1");

        // ── CurFloor=1 구간: 1층 호스트=3(수용) · 2층 호스트=2(그 층 서비스 안 함) ──
        await E2EWait.UntilAsync(() => srvA.LastFor(chuteNo) is { NextStates: [3] }, 8000, "1층 호스트 next_state 3");
        await E2EWait.UntilAsync(() => srvB.LastFor(chuteNo) is { NextStates: [2] }, 8000, "2층 호스트 next_state 2");
        _out.WriteLine("[VS-E1] CurFloor=1 → 1층=3 · 2층=2");

        // ── IF-05(inductionNo=2 → F=2) → 관측 루프가 TgtFloor=2 기입 → Sim 재정렬 → CurFloor=2 ──
        var resp = await client.PostAsJsonAsync("/api/v1/destination-query",
            new { pId = 26001, agvNo = 1, barcode = "TEST-BARCODE-3", inductionNo = 2, qty = 1, timeStamp = (string?)null });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("OK", (await resp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("result").GetString());

        await E2EWait.UntilAsync(() => factory.SorterSnapshot(destId) is { CurFloor: 2, Ready: true }, 8000, "CurFloor=2 재정렬");

        // ── 재정렬 후: 1층 호스트=2(서비스 중단) · 2층 호스트=3(새 서비스) ──
        await E2EWait.UntilAsync(() => srvA.LastFor(chuteNo) is { NextStates: [2] }, 6000, "재정렬 후 1층=2");
        await E2EWait.UntilAsync(() => srvB.LastFor(chuteNo) is { NextStates: [3] }, 6000, "재정렬 후 2층=3");

        // 수신 이력에 순서대로 3→2(1층)·2→3(2층)이 나타남(각 호스트 최소 두 상태).
        var aStates = srvA.All.Where(p => p.Accepted && p.ChuteNumbers.Contains(chuteNo)).Select(p => p.NextStates[0]).ToList();
        var bStates = srvB.All.Where(p => p.Accepted && p.ChuteNumbers.Contains(chuteNo)).Select(p => p.NextStates[0]).ToList();
        Assert.Contains(3, aStates);   // 1층: CurFloor=1 구간 3
        Assert.Equal(2, aStates[^1]);  // 1층: 최종 2(재정렬 후)
        Assert.Contains(2, bStates);   // 2층: CurFloor=1 구간 2
        Assert.Equal(3, bStates[^1]);  // 2층: 최종 3(재정렬 후)

        _out.WriteLine($"[VS-E1] CurFloor 1→2 재정렬 → 1층 {string.Join(",", aStates)} · 2층 {string.Join(",", bStates)} (층별 호스트 라우팅)");
    }
}
