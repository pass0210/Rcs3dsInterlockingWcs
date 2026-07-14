using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Data;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests.B2C;

// ════════════════════════════════════════════════════════════════════════════
// S-B2C-FACILITY — 런타임 신설 슈트의 슈트 제어 → IF-08 UpdateChuteState 푸시 E2E.
//
// 설비 관리 API 로 만든 CHUTE(기동 시 DB 에 없던 목적지)가 운영 제어(pause/resume)에서
// IF-08 push 를 실제로 발신하는지 가짜 고객 서버(FakeChuteStateServer)로 입증한다.
// 이는 ChuteCapacityService.EnsureChuteRegistered + DestinationStatusPusher.RegisterDestination
// (런타임 등록)이 성립함을 검증 — 등록 누락 시 "미등록 → push 드롭"으로 RED.
//
// ChuteStatePushWebApplicationFactory 재사용(ChuteStatePush BaseUrl 활성·Fake 수신 서버).
// ════════════════════════════════════════════════════════════════════════════
public class B2cChutePushTests
{
    private readonly ITestOutputHelper _out;
    public B2cChutePushTests(ITestOutputHelper output) => _out = output;

    private sealed record MgmtResp(string Status, string Message, Dictionary<string, int>? Counts);

    [Fact]
    public async Task RuntimeCreatedChute_PauseResume_PushesState2Then3()
    {
        await using var srv     = await Wcs.Tests.FakeChuteStateServer.StartAsync();
        await using var factory = new Wcs.Tests.ChuteStatePushWebApplicationFactory(srv.BaseUrl);
        var client = factory.CreateClient();

        const int chuteNo = 50;   // 시드(1~6·소터 30) 밖 — 런타임 신설.

        // ① 설비 관리 API 로 슈트 생성 → 런타임 등록(capacity + pusher) → 부트스트랩 push(3).
        var create = await client.PostAsJsonAsync("/api/b2c/facility/destinations",
            new { chuteNo, destType = "CHUTE", workFullQty = 100, operatorName = "op" });
        Assert.Equal("S", (await create.Content.ReadFromJsonAsync<MgmtResp>())!.Status);

        long destId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            destId = db.Destinations.Single(d => d.ChuteNo == chuteNo && d.DestType == DestType.CHUTE).Id;
        }

        // 부트스트랩 수용상태(3) 도달 대기(신설 슈트 = NORMAL·비만재).
        await WaitUntilAsync(() => srv.LastFor(chuteNo) is { NextStates: [3] }, 6000, "신설 슈트 부트스트랩 push=3");
        int baseline = srv.CountFor(chuteNo);

        // ② 운영 제어 pause → IF-08 push next_state=2(수용 불가).
        var pause = await client.PostAsJsonAsync($"/api/ops/destinations/{destId}/pause", new { operatorName = "야간조" });
        Assert.Equal(System.Net.HttpStatusCode.OK, pause.StatusCode);
        await WaitUntilAsync(() => srv.CountFor(chuteNo) > baseline && srv.LastFor(chuteNo) is { NextStates: [2] },
            5000, "런타임 슈트 pause → push=2");

        // ③ 운영 제어 resume → IF-08 push next_state=3(수용 가능 복귀 — 미등록이면 여기서 2로 오발신되어 RED).
        var resume = await client.PostAsJsonAsync($"/api/ops/destinations/{destId}/resume", new { operatorName = "야간조" });
        Assert.Equal(System.Net.HttpStatusCode.OK, resume.StatusCode);
        await WaitUntilAsync(() => srv.LastFor(chuteNo) is { NextStates: [3] }, 5000, "런타임 슈트 resume → push=3");

        _out.WriteLine($"[B2C-PUSH] 런타임 슈트 {chuteNo}: 부트3 → pause2 → resume3 (총 {srv.CountFor(chuteNo)}건)");
    }

    // ── FIX ITER 3: 설비 관리 비활성화 → IF-08 push 수용불가(2) / 재활성화 → 수용가능(3) ──
    //   SetActiveAsync 가 인메모리 IsActive 를 반영 + OnChuteStateChanged 발화(레지스트리 일관성).
    //   반영 누락 시 GetHold 가 None 유지 → push 미발신(비활성인데 RCS 는 3 유지)으로 RED.
    [Fact]
    public async Task Deactivate_PushesState2_Reactivate_PushesState3()
    {
        await using var srv     = await Wcs.Tests.FakeChuteStateServer.StartAsync();
        await using var factory = new Wcs.Tests.ChuteStatePushWebApplicationFactory(srv.BaseUrl);
        var client = factory.CreateClient();

        const int chuteNo = 51;   // 시드(1~6·소터 30) 밖 — 런타임 신설.
        var create = await client.PostAsJsonAsync("/api/b2c/facility/destinations",
            new { chuteNo, destType = "CHUTE", workFullQty = 100, operatorName = "op" });
        Assert.Equal("S", (await create.Content.ReadFromJsonAsync<MgmtResp>())!.Status);

        long destId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            destId = db.Destinations.Single(d => d.ChuteNo == chuteNo && d.DestType == DestType.CHUTE).Id;
        }

        // 부트스트랩 수용가능(3) 도달.
        await WaitUntilAsync(() => srv.LastFor(chuteNo) is { NextStates: [3] }, 6000, "신설 슈트 부트스트랩 push=3");
        int baseline = srv.CountFor(chuteNo);

        // 비활성화(진행 중 없음 → force 불요) → IF-08 push 수용불가(2).
        var deact = await client.PostAsJsonAsync($"/api/b2c/facility/destinations/{destId}/activate",
            new { isActive = false, force = false, operatorName = "야간조" });
        Assert.Equal("S", (await deact.Content.ReadFromJsonAsync<MgmtResp>())!.Status);
        await WaitUntilAsync(() => srv.CountFor(chuteNo) > baseline && srv.LastFor(chuteNo) is { NextStates: [2] },
            5000, "비활성화 → push=2(수용불가)");

        // 재활성화 → IF-08 push 수용가능(3).
        var react = await client.PostAsJsonAsync($"/api/b2c/facility/destinations/{destId}/activate",
            new { isActive = true, force = false, operatorName = "야간조" });
        Assert.Equal("S", (await react.Content.ReadFromJsonAsync<MgmtResp>())!.Status);
        await WaitUntilAsync(() => srv.LastFor(chuteNo) is { NextStates: [3] }, 5000, "재활성화 → push=3(수용가능)");

        _out.WriteLine($"[B2C-PUSH] 런타임 슈트 {chuteNo}: 부트3 → 비활성2 → 재활성3 (총 {srv.CountFor(chuteNo)}건)");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs, string msg, int pollMs = 25)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTimeOffset.Now > deadline) Assert.Fail($"WaitUntil 타임아웃({timeoutMs}ms): {msg}");
            await Task.Delay(pollMs);
        }
    }
}
