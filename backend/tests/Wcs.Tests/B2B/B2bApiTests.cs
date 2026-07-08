using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Data;
using Wcs.Data.B2B;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests.B2B;

// ════════════════════════════════════════════════════════════════════════════
// S-B2B-1 API 통합 테스트 — 5 엔드포인트 계약(§3)·실패 message(§4)·부수효과·HTTP 코드.
// 기존 FakeModbusWebApplicationFactory(in-memory SQLite + 전체 호스트) 재사용.
// B2B 6테이블은 WcsDbContext EnsureCreated 로 자동 생성. test_data 는 scope 로 직접 시드.
// IClassFixture 공유 DB → 테스트별 고유 bizDay/batch/barcode 로 격리(기존 패턴 준용).
// ════════════════════════════════════════════════════════════════════════════
public class B2bApiTests : IClassFixture<B2bWebApplicationFactory>
{
    private readonly B2bWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly ITestOutputHelper _out;

    public B2bApiTests(B2bWebApplicationFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _client  = factory.CreateClient();
        _out     = output;
    }

    private sealed record ApiResp(string Status, string Message);

    private void SeedTestData(params (string bizDay, string batch, string barcode, string chute)[] rows)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        foreach (var r in rows)
            db.B2bTestData.Add(new TestData
            {
                BizDay = r.bizDay, Batch = r.batch, Barcode = r.barcode, ChuteNo = r.chute,
                CreatedAt = DateTime.Now,
            });
        db.SaveChanges();
    }

    // ════════════════════════════════════════════════════════════════════════
    // 1. unprocessed — 부수효과(2회차 빈배열) + 0건 [] + bizDay 누락 400(#16)
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Unprocessed_MarksReceiveTime_SecondCallEmpty_AndZeroRowsEmptyArray()
    {
        SeedTestData(
            ("2026-07-11", "001", "BC-A", "001"), ("2026-07-11", "001", "BC-A", "001"),
            ("2026-07-11", "001", "BC-B", "002"));

        var r1 = await _client.GetAsync("/api/v1/works/unprocessed?bizDay=20260711");
        Assert.Equal(HttpStatusCode.OK, r1.StatusCode);
        using (var doc = JsonDocument.Parse(await r1.Content.ReadAsStringAsync()))
        {
            Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
            var group = doc.RootElement.EnumerateArray().Single();
            Assert.Equal("2026-07-11", group.GetProperty("bizDay").GetString());
            Assert.Equal("001", group.GetProperty("batch").GetString());
            var items = group.GetProperty("items");
            Assert.Equal(2, items.GetArrayLength());
            var first = items[0];
            Assert.Equal("BC-A", first.GetProperty("barcode").GetString());
            Assert.Equal("001", first.GetProperty("chuteNo").GetString());
            Assert.Equal(2, first.GetProperty("qty").GetInt32());   // qty=COUNT
        }

        // 부수효과: 2회차는 빈 배열(receive_time 마킹됨)
        var r2 = await _client.GetAsync("/api/v1/works/unprocessed?bizDay=20260711");
        using (var doc = JsonDocument.Parse(await r2.Content.ReadAsStringAsync()))
            Assert.Equal(0, doc.RootElement.GetArrayLength());

        // 0건(미시드 날짜) → [] (F 아님)
        var r3 = await _client.GetAsync("/api/v1/works/unprocessed?bizDay=20991231");
        Assert.Equal(HttpStatusCode.OK, r3.StatusCode);
        using (var doc = JsonDocument.Parse(await r3.Content.ReadAsStringAsync()))
            Assert.Equal(0, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task Unprocessed_MissingBizDay_Returns400_Message16()
    {
        var resp = await _client.GetAsync("/api/v1/works/unprocessed");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ApiResp>();
        Assert.Equal("F", body!.Status);
        Assert.Equal("bizDay parameter is required.", body.Message);
    }

    // ════════════════════════════════════════════════════════════════════════
    // 2. input — qty 묶음 가용부족 전량거부(200+F #2) + pId 미검증 저장
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Input_NotEnough_Returns200F_Message2()
    {
        SeedTestData(("2026-07-12", "001", "BC-IN", "001"));   // 가용 1
        var req = new { bizDay = "20260712", batch = "001", inductionNo = 1, chuteNo = "001",
            pId = 12345, barcode = "BC-IN", status = "OK", reason = "", inTime = "2026-07-12 10:00:00", qty = 2 };

        var resp = await _client.PostAsJsonAsync("/api/v1/works/input", req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);   // 비즈니스 실패 = 200
        var body = await resp.Content.ReadFromJsonAsync<ApiResp>();
        Assert.Equal("F", body!.Status);
        Assert.Equal("Not enough unprocessed rows: requested 2, available 1.", body.Message);
    }

    [Fact]
    public async Task Input_HappyPath_200S_PidStoredUnverified()
    {
        SeedTestData(("2026-07-13", "001", "BC-P", "001"));
        var req = new { bizDay = "20260713", batch = "001", inductionNo = 9, chuteNo = "001",
            pId = 2100000000, barcode = "BC-P", status = "OK", inTime = "2026-07-13 10:00:00", qty = 1 };

        var resp = await _client.PostAsJsonAsync("/api/v1/works/input", req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ApiResp>();
        Assert.Equal("S", body!.Status);
        Assert.Equal("Success", body.Message);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        var log = db.B2bTestLogs.Single(l => l.LogType == "INPUT" && l.BizDay == "2026-07-13");
        Assert.Equal("2100000000", log.Pid);      // pId 미검증 그대로 저장
        Assert.Equal("9", log.EquipmentNo);        // inductionNo → equipment_no
    }

    [Fact]
    public async Task Input_InvalidStatus_Returns400_Message12()
    {
        var req = new { bizDay = "20260713", batch = "001", inductionNo = 1, chuteNo = "001",
            pId = 1, barcode = "X", status = "BAD", inTime = "2026-07-13 10:00:00", qty = 1 };
        var resp = await _client.PostAsJsonAsync("/api/v1/works/input", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ApiResp>();
        Assert.Equal("F", body!.Status);
        Assert.Equal("Status must be 'OK' or 'NG'.", body.Message);
    }

    [Fact]
    public async Task Input_QtyOutOfRange_Returns400_Message13()
    {
        var req = new { bizDay = "20260713", batch = "001", inductionNo = 1, chuteNo = "001",
            pId = 1, barcode = "X", status = "OK", inTime = "2026-07-13 10:00:00", qty = 0 };
        var resp = await _client.PostAsJsonAsync("/api/v1/works/input", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ApiResp>();
        Assert.Equal("The field Qty must be between 1 and 9999.", body!.Message);
    }

    [Fact]
    public async Task Input_BadBizDayFormat_Returns400_Message10()
    {
        var req = new { bizDay = "2026/07/13", batch = "001", inductionNo = 1, chuteNo = "001",
            pId = 1, barcode = "X", status = "OK", inTime = "2026-07-13 10:00:00", qty = 1 };
        var resp = await _client.PostAsJsonAsync("/api/v1/works/input", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ApiResp>();
        Assert.Equal("BizDay must be in YYYYMMDD or YYYY-MM-DD format.", body!.Message);
    }

    [Fact]
    public async Task Input_InvalidCalendarDate_Returns400_Message17()
    {
        // 20261332 는 형식(#10) 통과하나 달력 무효 → NormalizeBizDay ArgumentException → 400(#17)
        var req = new { bizDay = "20261332", batch = "001", inductionNo = 1, chuteNo = "001",
            pId = 1, barcode = "X", status = "OK", inTime = "2026-07-13 10:00:00", qty = 1 };
        var resp = await _client.PostAsJsonAsync("/api/v1/works/input", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ApiResp>();
        Assert.Equal("Invalid date: 20261332", body!.Message);
    }

    // ════════════════════════════════════════════════════════════════════════
    // 문자열 길이 검증(FIX iter2 #1) — SQL Server 500 방지. 길이 초과 → 400 + ApiResponse F.
    // (SQLite 더블은 컬럼 길이를 강제하지 않아 은폐하지만, DataAnnotations 는 컨트롤러 진입 시 차단.)
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Input_BatchTooLong_Returns400_ApiResponseF()
    {
        // batch 11자(계약 1~10 초과) → 400 + F (biz*.batch nvarchar(10) 초과 500 방지)
        var req = new { bizDay = "20260713", batch = "12345678901", inductionNo = 1, chuteNo = "001",
            pId = 1, barcode = "X", status = "OK", inTime = "2026-07-13 10:00:00", qty = 1 };
        var resp = await _client.PostAsJsonAsync("/api/v1/works/input", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ApiResp>();
        Assert.Equal("F", body!.Status);
        Assert.False(string.IsNullOrWhiteSpace(body.Message));
        _out.WriteLine($"[len] batch 11자 → 400 F: {body.Message}");
    }

    [Fact]
    public async Task Input_BarcodeTooLong_Returns400_ApiResponseF()
    {
        // barcode 51자(test_data/test_log.barcode nvarchar(50) 초과) → 400 + F
        var req = new { bizDay = "20260713", batch = "001", inductionNo = 1, chuteNo = "001",
            pId = 1, barcode = new string('A', 51), status = "OK", inTime = "2026-07-13 10:00:00", qty = 1 };
        var resp = await _client.PostAsJsonAsync("/api/v1/works/input", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ApiResp>();
        Assert.Equal("F", body!.Status);
    }

    [Fact]
    public async Task Box_ItemBarcodeTooLong_Returns400_ApiResponseF()
    {
        // box_item.barcode nvarchar(100) — 101자 → 400 + F
        var req = new { bizDay = "20260618", batch = "001", boxNo = "BOX-LEN", chuteNo = "3",
            items = new[] { new { barcode = new string('B', 101), qty = 1 } } };
        var resp = await _client.PostAsJsonAsync("/api/v1/works/box", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ApiResp>();
        Assert.Equal("F", body!.Status);
    }

    [Fact]
    public async Task Results_ItemChuteNoTooLong_Returns400_ApiResponseF()
    {
        // work_result.chute_no nvarchar(20) — 21자 → 400 + F (FIX iter3 잔여 필드)
        var body = new[] { new { bizDay = "20260717", batch = "001", items = new[]
        {
            new { barcode = "BC-R2", chuteNo = new string('9', 21), qty = 1 },
        } } };
        var resp = await _client.PostAsJsonAsync("/api/v1/works/results", body);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var api = await resp.Content.ReadFromJsonAsync<ApiResp>();
        Assert.Equal("F", api!.Status);
    }

    [Fact]
    public async Task Box_EndTimeTooLong_Returns400_ApiResponseF()
    {
        // box.end_time nvarchar(50) — 51자 → 400 + F (FIX iter3 잔여 필드)
        var req = new { bizDay = "20260618", batch = "001", boxNo = "BOX-ET", chuteNo = "3",
            items = new[] { new { barcode = "BC-1", qty = 1 } }, endTime = new string('T', 51) };
        var resp = await _client.PostAsJsonAsync("/api/v1/works/box", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ApiResp>();
        Assert.Equal("F", body!.Status);
    }

    // ════════════════════════════════════════════════════════════════════════
    // 3. classification — chute mismatch(#3) + 이미분류(#4)
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Classification_ChuteMismatch_200F_Message3()
    {
        SeedTestData(("2026-07-14", "001", "BC-C", "005"));
        var req = new { bizDay = "20260714", batch = "001", chuteNo = "1", pId = 1,
            barcode = "BC-C", status = "OK", sortTime = "2026-07-14 10:00:00", qty = 1 };
        var resp = await _client.PostAsJsonAsync("/api/v1/works/classification", req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ApiResp>();
        Assert.Equal("F", body!.Status);
        Assert.Equal("Chute mismatch: barcode BC-C expected chute(s) [005], received 001.", body.Message);
    }

    [Fact]
    public async Task Classification_AlreadyClassified_200F_Message4()
    {
        SeedTestData(("2026-07-15", "001", "BC-D", "001"));
        var req = new { bizDay = "20260715", batch = "001", chuteNo = "001", pId = 1,
            barcode = "BC-D", status = "OK", sortTime = "2026-07-15 10:00:00", qty = 1 };
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/api/v1/works/classification", req)).StatusCode);

        var resp2 = await _client.PostAsJsonAsync("/api/v1/works/classification", req);
        var body2 = await resp2.Content.ReadFromJsonAsync<ApiResp>();
        Assert.Equal("F", body2!.Status);
        Assert.Equal("Barcode BC-D in chute 001 has already been fully classified.", body2.Message);
    }

    // ════════════════════════════════════════════════════════════════════════
    // 4. results — 최상위 배열 + 존재검증 전체거부(#6) + chuteNo 미검증 저장 + happy
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Results_TopLevelArray_UnregisteredBarcode_RejectsAll_Message6()
    {
        SeedTestData(("2026-07-16", "001", "BC-R", "001"));
        var body = new[] { new { bizDay = "20260716", batch = "001", items = new[]
        {
            new { barcode = "BC-R", chuteNo = "001", qty = 1 },
            new { barcode = "BC-UNREG", chuteNo = "002", qty = 1 },   // 미등록
        } } };
        var resp = await _client.PostAsJsonAsync("/api/v1/works/results", body);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var api = await resp.Content.ReadFromJsonAsync<ApiResp>();
        Assert.Equal("F", api!.Status);
        Assert.Equal("Barcode 'BC-UNREG' not found, or bizDay/batch does not match the registered data.", api.Message);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        Assert.Equal(0, db.B2bWorkResults.Count(w => w.BizDay == "2026-07-16"));   // 전체거부·부분 INSERT 0
    }

    [Fact]
    public async Task Results_HappyPath_TopLevelArray_ExpandsByQty_ChuteNotValidated()
    {
        SeedTestData(("2026-07-17", "001", "BC-R2", "001"));
        var body = new[] { new { bizDay = "20260717", batch = "001", items = new[]
        {
            new { barcode = "BC-R2", chuteNo = "88", qty = 2 },   // 88≠등록001, results 는 chuteNo 미검증
        } } };
        var resp = await _client.PostAsJsonAsync("/api/v1/works/results", body);
        var api = await resp.Content.ReadFromJsonAsync<ApiResp>();
        Assert.Equal("S", api!.Status);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        var rows = db.B2bWorkResults.Where(w => w.BizDay == "2026-07-17").ToList();
        Assert.Equal(2, rows.Count);                        // qty=2 → 2행
        Assert.All(rows, r => Assert.Equal("088", r.ChuteNo));   // 미검증·정규화
    }

    [Fact]
    public async Task Results_EmptyArray_200F_Message5()
    {
        var resp = await _client.PostAsJsonAsync("/api/v1/works/results", Array.Empty<object>());
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var api = await resp.Content.ReadFromJsonAsync<ApiResp>();
        Assert.Equal("F", api!.Status);
        Assert.Equal("No data to process.", api.Message);
    }

    // ════════════════════════════════════════════════════════════════════════
    // 5. box — 중복거부(#8) + chuteNo 정규화 저장 + items 빈배열 400(#14)
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Box_Duplicate_200F_Message8()
    {
        var req = new { bizDay = "20260618", batch = "001", boxNo = "BOX-D", chuteNo = "3",
            items = new[] { new { barcode = "BC-1", qty = 2 } }, endTime = "2026-06-18 10:00:00" };
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/api/v1/works/box", req)).StatusCode);

        var resp2 = await _client.PostAsJsonAsync("/api/v1/works/box", req);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var body2 = await resp2.Content.ReadFromJsonAsync<ApiResp>();
        Assert.Equal("F", body2!.Status);
        Assert.Equal("Box already exists for the given bizDay/batch/boxNo.", body2.Message);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        var box = db.B2bBoxes.Single(b => b.BoxNo == "BOX-D");
        Assert.Equal("003", box.ChuteNo);   // 3자리 정규화
    }

    [Fact]
    public async Task Box_EmptyItems_Returns400_Message14()
    {
        var req = new { bizDay = "20260618", batch = "001", boxNo = "BOX-E", chuteNo = "3",
            items = Array.Empty<object>() };
        var resp = await _client.PostAsJsonAsync("/api/v1/works/box", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ApiResp>();
        Assert.Equal("Items must contain at least one entry.", body!.Message);
    }

    // ════════════════════════════════════════════════════════════════════════
    // api_call_log 경로 한정 — /api/v1/works/ 기록 O, 기존 /api/v1/destination-query 기록 X
    // (Q1 무접촉 증거). 배경 writer 비동기 → 폴링으로 결정적 확인.
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ApiCallLog_RecordsWorksPath_ButNotExistingRcsEndpoint()
    {
        // 기존 RCS 엔드포인트 호출(기록되면 안 됨)
        await _client.PostAsJsonAsync("/api/v1/destination-query",
            new { pId = 12321, agvNo = 1, barcode = "TEST-BARCODE-1", inductionNo = 1, qty = 1, timeStamp = "2026-07-18 10:00:00" });
        // works 경로 호출(기록돼야 함) — 고유 boxNo
        await _client.PostAsJsonAsync("/api/v1/works/box",
            new { bizDay = "20260718", batch = "001", boxNo = "BOX-LOG", chuteNo = "3",
                  items = new[] { new { barcode = "BC-LOG", qty = 1 } } });

        // 배경 writer flush 폴링(최대 5초)
        bool worksLogged = false;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
                worksLogged = db.B2bApiCallLogs.Any(l => l.Endpoint == "/api/v1/works/box");
            }
            if (worksLogged) break;
            await Task.Delay(50);
        }
        Assert.True(worksLogged, "works 경로(/api/v1/works/box)는 api_call_log 에 기록돼야 함");

        using var s = _factory.Services.CreateScope();
        var db2 = s.ServiceProvider.GetRequiredService<WcsDbContext>();
        Assert.False(db2.B2bApiCallLogs.Any(l => l.Endpoint == "/api/v1/destination-query"),
            "기존 RCS 엔드포인트는 api_call_log 미기록(무접촉·Q1 경로 한정)");
        _out.WriteLine("[api_call_log] works 기록 O · destination-query 기록 X (경로 한정 확인)");
    }
}
