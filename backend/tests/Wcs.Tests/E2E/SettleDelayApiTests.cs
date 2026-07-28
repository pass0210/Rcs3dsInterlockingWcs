using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wcs.Data;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests.E2E;

// ════════════════════════════════════════════════════════════════════════════
// S-IF10-CWRITE-SETTLE-DELAY — API 비블로킹 실증(계약 Completion #3).
//
//   IF-10(3D) HTTP 200 응답 왕복이 SettleDelayMs와 독립(즉시 ack·fire-and-forget)이고,
//   C(CellAssign) 기입/핸드셰이크는 그 뒤 백그라운드에서 안착 지연만큼 늦게 발생함을
//   실 Sim + 실 SorterRegistryFactory + 실 EF DB ground-truth로 검증한다.
//
//   폐기된 S-IF10-CWRITE-WAIT("C 완료 대기 후 응답") 부활 방지 — 응답 시간이 SettleDelayMs에
//   영향받지 않음을 자동화로 실증(코드 리뷰 대체 금지).
// ════════════════════════════════════════════════════════════════════════════

[Collection("RealSimSerial")]
public sealed class SettleDelayApiTests
{
    private readonly ITestOutputHelper _out;
    public SettleDelayApiTests(ITestOutputHelper output) => _out = output;

    // ─────────────────────────────────────────────────────────────────────────
    // SettleDelayMs≫응답시간(=1000ms)로 결선 → IF-10 왕복은 그보다 훨씬 빠르고(응답 지연 0),
    //   응답 직후엔 핸드셰이크 미완료(sorter_command 부재)이며, sorter_command 완료는 IF-10
    //   수신 후 최소 SettleDelayMs 경과 뒤에 나타난다(C가 안착 지연 뒤로 밀림).
    // ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task IF10_Response_Immediate_Independent_Of_SettleDelay_CWriteDeferred()
    {
        const int settle = 1000;
        var factory = new E2EWebApplicationFactory(settleDelayMs: settle, initialCurFloor: 2, rFlagTimeoutMs: 6000);
        await using var _f = factory;
        await factory.StartSimsAsync();
        using var client = factory.CreateClient();
        long destId = factory.PrimarySorter.DestinationId;
        await E2EWait.UntilAsync(() => factory.IsSorterOnline(destId), 5000, "소터 Online");

        string barcode = E2EWebApplicationFactory.BarcodeForSorter(E2EWebApplicationFactory.DefaultSorterChuteNo);
        const int pId = 27101;

        // ── IF-05: 목적지 조회(활성 piece·예약 생성) → chuteNo 확보 ──────────────────
        var if05 = await client.PostAsJsonAsync("/api/v1/destination-query",
            new { pId, agvNo = 1, barcode, inductionNo = 1, qty = 1, timeStamp = (string?)null });
        Assert.Equal(HttpStatusCode.OK, if05.StatusCode);
        var if05Body = await if05.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("OK", if05Body.GetProperty("result").GetString());
        int chuteNo = if05Body.GetProperty("chuteNo").GetInt32();

        // ── IF-10: 투입 보고 — 응답 왕복 시간을 계측(백그라운드 핸드셰이크가 안착 지연을 흡수) ──
        var sw = Stopwatch.StartNew();
        var if10 = await client.PostAsJsonAsync("/api/v1/deposit-report",
            new { pId, barcode, chuteNo, agvNo = 1 });
        long respMs = sw.ElapsedMilliseconds;

        Assert.Equal(HttpStatusCode.OK, if10.StatusCode);
        var if10Body = await if10.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("OK", if10Body.GetProperty("result").GetString());

        _out.WriteLine($"[API] IF-10 응답 왕복={respMs}ms (SettleDelayMs={settle})");

        // (a) 응답은 SettleDelayMs와 독립 — 훨씬 빠름(즉시 ack·fire-and-forget). 폐기된 WAIT 접근 부활 0.
        Assert.True(respMs < 500, $"IF-10 응답이 SettleDelayMs({settle})와 독립(즉시 ack): {respMs}ms < 500ms");

        // (b) 응답 직후엔 핸드셰이크 미완료 — sorter_command 부재(C가 안착 지연 뒤로 밀려 진행 중).
        using (var db = factory.CreateDbScope())
        {
            int cmdCount = await db.SorterCommands.CountAsync();
            Assert.Equal(0, cmdCount);  // 응답 시점(~수십 ms)엔 아직 지연(1000ms) 진행 중 → 완료 0.
        }

        // (c) C는 그 후 지연 — sorter_command 완료가 IF-10 수신 후 최소 SettleDelayMs 경과 뒤 등장.
        await E2EWait.UntilAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            return await db.SorterCommands.AnyAsync(c => c.Status == SorterCommandStatus.COMPLETED);
        }, 12000, "sorter_command COMPLETED");
        long completedMs = sw.ElapsedMilliseconds;

        _out.WriteLine($"[API] sorter_command 완료까지 경과={completedMs}ms (SettleDelayMs={settle})");
        Assert.True(completedMs >= settle - 100,
            $"C(핸드셰이크)가 안착 지연 뒤로 밀림: 완료 {completedMs}ms >= {settle - 100}ms");
    }
}
