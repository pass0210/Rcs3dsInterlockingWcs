using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Data;
using Wcs.Tests.B2B;   // B2bWebApplicationFactory(in-memory SQLite 청정 호스트·0 소터) 재사용.
using Xunit;

namespace Wcs.Tests.B2C;

// ════════════════════════════════════════════════════════════════════════════
// S-B2C-DATAGEN API 통합 + E2E 테스트 — /api/b2c/test-data/* + 생성→IF-05 소비→초기화→재투입.
// B2bWebApplicationFactory 재사용(청정 in-memory SQLite·소터 게이트웨이 0 — IF-05 소터 가부는
// SorterCanAcceptBarcode(DB) + status.Compute(bundle null → paused=false)라 라이브 게이트웨이 불요).
// 테스트별 고유 sorterChuteNo 로 격리(공유 DB).
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

    private static object GenBody(int chute, int cells = 3, string prefix = "P", string batch = "B") => new
    {
        sorterChuteNo = chute, workDate = "2026-07-13", batchNo = batch, waveNo = 1,
        cellCount = cells, cellCapacity = 3, plannedQty = 3, orderPrefix = prefix,
    };

    // ── generate → summary → detail 왕복 ─────────────────────────────────────
    [Fact]
    public async Task Generate_RoundTrip_SummaryDetail()
    {
        var gen = await _client.PostAsJsonAsync("/api/b2c/test-data/generate", GenBody(11, cells: 4, prefix: "R11"));
        Assert.Equal(HttpStatusCode.OK, gen.StatusCode);
        Assert.Equal("S", (await gen.Content.ReadFromJsonAsync<MgmtResp>())!.Status);

        var sum = await _client.GetAsync("/api/b2c/test-data/summary?sorterChuteNo=11");
        Assert.Equal(HttpStatusCode.OK, sum.StatusCode);
        using (var doc = JsonDocument.Parse(await sum.Content.ReadAsStringAsync()))
        {
            var s = doc.RootElement.EnumerateArray().Single(e => e.GetProperty("chuteNo").GetInt32() == 11);
            Assert.Equal(4, s.GetProperty("cellTotal").GetInt32());
            Assert.Equal(4, s.GetProperty("cellAssigned").GetInt32());
            Assert.Equal(12, s.GetProperty("plannedSum").GetInt32());   // 4 × 3
        }

        var det = await _client.GetAsync("/api/b2c/test-data/detail?sorterChuteNo=11");
        Assert.Equal(HttpStatusCode.OK, det.StatusCode);
        using (var doc = JsonDocument.Parse(await det.Content.ReadAsStringAsync()))
        {
            var rows = doc.RootElement.EnumerateArray().ToList();
            Assert.Equal(4, rows.Count);
            Assert.All(rows, r => Assert.Equal(0, r.GetProperty("currentQty").GetInt32()));
            Assert.Equal("R11-01", rows[0].GetProperty("assignedOrderNo").GetString());
        }
    }

    // ── generate 검증 400(형식 실패) → {status:F} ─────────────────────────────
    [Fact]
    public async Task Generate_InvalidWorkDate_400_FailBody()
    {
        var bad = new { sorterChuteNo = 12, workDate = "not-a-date", batchNo = "B",
                        waveNo = 1, cellCount = 3, cellCapacity = 3, plannedQty = 3, orderPrefix = "P" };
        var res = await _client.PostAsJsonAsync("/api/b2c/test-data/generate", bad);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("F", (await res.Content.ReadFromJsonAsync<MgmtResp>())!.Status);
    }

    // ── generate 검증 400(셀 개수 범위) ───────────────────────────────────────
    [Fact]
    public async Task Generate_CellCountOutOfRange_400()
    {
        var bad = new { sorterChuteNo = 13, workDate = "2026-07-13", batchNo = "B",
                        waveNo = 1, cellCount = 0, cellCapacity = 3, plannedQty = 3, orderPrefix = "P" };
        var res = await _client.PostAsJsonAsync("/api/b2c/test-data/generate", bad);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ── generate 비즈니스 200 F: chuteNo 가 CHUTE 로 점유됨 ────────────────────
    [Fact]
    public async Task Generate_ChuteOccupied_200F()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            db.Destinations.Add(new Destination
            {
                ChuteNo = 14, DestType = DestType.CHUTE, Status = DestStatus.NORMAL, IsActive = true,
                CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var res = await _client.PostAsJsonAsync("/api/b2c/test-data/generate", GenBody(14));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("F", (await res.Content.ReadFromJsonAsync<MgmtResp>())!.Status);
    }

    // ── detail 필수 파라미터 누락 400 ─────────────────────────────────────────
    [Fact]
    public async Task Detail_MissingSorterChuteNo_400()
    {
        var res = await _client.GetAsync("/api/b2c/test-data/detail");
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    // ── ★ E2E: 생성 → IF-05 소비(예약) → 초기화(force) → 재차 IF-05(재예약) ───────
    [Fact]
    public async Task E2E_Generate_IF05_Reserve_Reset_ReInject()
    {
        const int chute = 21;
        const int pId   = 21001;
        const string barcode = "E2E21-01";

        // (1) 생성.
        var gen = await _client.PostAsJsonAsync("/api/b2c/test-data/generate", GenBody(chute, cells: 3, prefix: "E2E21"));
        Assert.Equal("S", (await gen.Content.ReadFromJsonAsync<MgmtResp>())!.Status);

        // (2) IF-05 — 생성 데이터가 유효 오더로 소비되어 예약(RESERVED) + order_item.reserved += qty.
        var if05 = await PostIf05(pId, barcode, qty: 2);
        Assert.Equal("OK", if05);
        Assert.Equal(2, await ReservedOf(chute, barcode));
        Assert.Equal(1, await ActivePieceCount(pId));

        // (3) 초기화 — in-flight(RESERVED) 존재 → force 없이는 거부, force 로 아카이브.
        var refused = await _client.PostAsJsonAsync("/api/b2c/test-data/reset", new { sorterChuteNo = chute, force = false });
        var refusedBody = await refused.Content.ReadFromJsonAsync<MgmtResp>();
        Assert.Equal("F", refusedBody!.Status);
        Assert.True(refusedBody.Counts!["inFlight"] >= 1);

        var reset = await _client.PostAsJsonAsync("/api/b2c/test-data/reset", new { sorterChuteNo = chute, force = true });
        Assert.Equal("S", (await reset.Content.ReadFromJsonAsync<MgmtResp>())!.Status);
        Assert.Equal(0, await ReservedOf(chute, barcode));   // 수량 리셋.

        // (4) 재차 IF-05(같은 바코드·같은 pId) — 아카이브 dedup 제외 덕에 재예약 성공(재테스트 가능).
        var if05b = await PostIf05(pId, barcode, qty: 2);
        Assert.Equal("OK", if05b);
        Assert.Equal(2, await ReservedOf(chute, barcode));   // 이중 아님 — 정확히 qty.

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        // 하드삭제 0(OQ1=B): 옛 piece 는 archived_at 세팅되어 보존 + 새 활성 piece 1건.
        var piecesForPid = db.Pieces.Where(p => p.PId == pId).ToList();
        Assert.Equal(2, piecesForPid.Count);
        Assert.Equal(1, piecesForPid.Count(p => p.ArchivedAt != null));
        Assert.Equal(1, piecesForPid.Count(p => p.ArchivedAt == null && p.IsActive));
    }

    // ── reset 대상 0(미존재 소터) → 200 F ─────────────────────────────────────
    [Fact]
    public async Task Reset_UnknownSorter_200F()
    {
        var res = await _client.PostAsJsonAsync("/api/b2c/test-data/reset", new { sorterChuteNo = 987, force = false });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("F", (await res.Content.ReadFromJsonAsync<MgmtResp>())!.Status);
    }

    // ── 헬퍼 ──────────────────────────────────────────────────────────────────
    private async Task<string> PostIf05(int pId, string barcode, int qty)
    {
        var res = await _client.PostAsJsonAsync("/api/v1/destination-query", new
        {
            pId, agvNo = 1, barcode, inductionNo = 1, qty, timeStamp = "2026-07-13 10:00:00",
        });
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("result").GetString()!;
    }

    private async Task<int> ReservedOf(int chute, string barcode)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        return await Task.FromResult(db.OrderItems
            .Where(i => i.Barcode == barcode
                     && db.Orders.Any(o => o.Id == i.OrderId && db.Destinations.Any(d => d.Id == o.DestinationId && d.ChuteNo == chute)))
            .Sum(i => i.ReservedQty));
    }

    private async Task<int> ActivePieceCount(int pId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        return await Task.FromResult(db.Pieces.Count(p => p.PId == pId && p.IsActive && p.ArchivedAt == null));
    }
}
