using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Data;
using Wcs.Tests.B2B;   // B2bWebApplicationFactory(in-memory SQLite 청정 호스트·0 소터) 재사용.
using Xunit;

namespace Wcs.Tests.B2C;

// ════════════════════════════════════════════════════════════════════════════
// S-B2C-FACILITY 생성(2a 슬림) API 통합 테스트 — /api/b2c/test-data/generate·batches·detail.
// 슬림 계약: 생성은 미할당 오더/바코드만 만든다(목적지 미할당). 소터/셀 자동생성·N↔N 제거.
// B2bWebApplicationFactory 재사용(청정 in-memory SQLite·0 소터). 테스트별 고유 배치명으로 격리.
// (설비 관리·오더 할당·혼합 라우팅·reset E2E 는 B2cFacilityApiTests.)
// ════════════════════════════════════════════════════════════════════════════
public class B2cApiTests : IClassFixture<B2bWebApplicationFactory>
{
    private readonly B2bWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public B2cApiTests(B2bWebApplicationFactory factory)
    {
        _factory = factory;
        _client  = factory.CreateClient();
    }

    private sealed record MgmtResp(string Status, string Message, Dictionary<string, int>? Counts);

    private static object GenBody(int count = 3, string prefix = "P", string batch = "B", int wave = 1) => new
    {
        workDate = "2026-07-13", batchNo = batch, waveNo = wave, plannedQty = count, barcodePrefix = prefix,
    };

    // ── generate → 미할당 오더 생성 + batches view 반영 ───────────────────────────
    [Fact]
    public async Task Generate_CreatesUnassignedOrders_BatchesReflect()
    {
        var gen = await _client.PostAsJsonAsync("/api/b2c/test-data/generate", GenBody(count: 4, prefix: "R11", batch: "BR11"));
        Assert.Equal(HttpStatusCode.OK, gen.StatusCode);
        var body = await gen.Content.ReadFromJsonAsync<MgmtResp>();
        Assert.Equal("S", body!.Status);
        Assert.Equal(4, body.Counts!["ordersCreated"]);
        Assert.Equal(4, body.Counts["orderItemsCreated"]);

        // 미할당 오더가 DB에 실재(DestinationId=null).
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            var orders = db.Orders.Where(o => o.OrderNo.StartsWith("R11-")).ToList();
            Assert.Equal(4, orders.Count);
            Assert.All(orders, o => Assert.Null(o.DestinationId));
        }

        // batches view — 미할당 오더 수 노출.
        var batches = await _client.GetAsync("/api/b2c/test-data/batches");
        Assert.Equal(HttpStatusCode.OK, batches.StatusCode);
        using var doc = JsonDocument.Parse(await batches.Content.ReadAsStringAsync());
        var b = doc.RootElement.EnumerateArray().Single(e => e.GetProperty("batchNo").GetString() == "BR11");
        Assert.Equal(4, b.GetProperty("orderTotal").GetInt32());
        Assert.Equal(4, b.GetProperty("orderUnassigned").GetInt32());
    }

    // ── generate 검증 400(형식 실패) → {status:F} ─────────────────────────────
    [Fact]
    public async Task Generate_InvalidWorkDate_400_FailBody()
    {
        var bad = new { workDate = "not-a-date", batchNo = "B", waveNo = 1, plannedQty = 3, barcodePrefix = "P" };
        var res = await _client.PostAsJsonAsync("/api/b2c/test-data/generate", bad);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("F", (await res.Content.ReadFromJsonAsync<MgmtResp>())!.Status);
    }

    // ── generate 검증 400(생성 개수 범위) ─────────────────────────────────────
    [Fact]
    public async Task Generate_CountOutOfRange_400()
    {
        var bad = new { workDate = "2026-07-13", batchNo = "B", waveNo = 1, plannedQty = 0, barcodePrefix = "P" };
        var res = await _client.PostAsJsonAsync("/api/b2c/test-data/generate", bad);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ── generate 검증 400(barcodePrefix 인젝션 문자) ──────────────────────────
    [Fact]
    public async Task Generate_InjectionPrefix_400()
    {
        var bad = new { workDate = "2026-07-13", batchNo = "B", waveNo = 1, plannedQty = 2, barcodePrefix = "a; DROP TABLE" };
        var res = await _client.PostAsJsonAsync("/api/b2c/test-data/generate", bad);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ── detail 필수 파라미터 누락 400 ─────────────────────────────────────────
    [Fact]
    public async Task Detail_MissingSorterChuteNo_400()
    {
        var res = await _client.GetAsync("/api/b2c/test-data/detail");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ── reset 대상 0(미존재 소터) → 200 F ─────────────────────────────────────
    [Fact]
    public async Task Reset_UnknownSorter_200F()
    {
        var res = await _client.PostAsJsonAsync("/api/b2c/test-data/reset", new { sorterChuteNo = 987, force = false });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("F", (await res.Content.ReadFromJsonAsync<MgmtResp>())!.Status);
    }
}
