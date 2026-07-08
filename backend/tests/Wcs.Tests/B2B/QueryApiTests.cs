using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Data;
using Wcs.Data.B2B;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests.B2B;

// ════════════════════════════════════════════════════════════════════════════
// S-B2B-3a 조회 백엔드 통합 테스트 — E1~E6(로그·API호출·Excel·3-way 비교·박스) + 아카이브 필터.
//   B2bWebApplicationFactory(INSTANCE-level in-memory SQLite) 재사용. 테스트별 고유 bizDay 로 격리.
//   ★ 3-way 비교 Batch 포함 키 음성 대조(다른 batch 동일 barcode 미오매칭) — 이월 결함 미재현 입증.
//   ★ 아카이브 필터 3상태 + DB COUNT 불변(하드삭제 0) 재확인.
//   ★ Excel Phase1/Phase2 페어링·소요시간·인덕션 층매핑.
// ════════════════════════════════════════════════════════════════════════════
public class QueryApiTests : IClassFixture<B2bWebApplicationFactory>
{
    private readonly B2bWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly ITestOutputHelper _out;

    public QueryApiTests(B2bWebApplicationFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _client  = factory.CreateClient();
        _out     = output;
    }

    private sealed record ApiResp(string Status, string Message);

    // ── 시드 헬퍼(scope) ────────────────────────────────────────────────────────
    private long SeedTestData(string bizDay, string batch, string barcode, string chute, DateTime? receive = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        var td = new TestData
        {
            BizDay = bizDay, Batch = batch, Barcode = barcode, ChuteNo = chute,
            ReceiveTime = receive, CreatedAt = DateTime.Now,
        };
        db.B2bTestData.Add(td);
        db.SaveChanges();
        return td.Id;
    }

    private void SeedLog(string logType, string bizDay, string batch, string barcode,
        string? equip, DateTime? logTime, long? testDataId = null, string status = "OK",
        DateTime? archivedAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        db.B2bTestLogs.Add(new TestLog
        {
            LogType = logType, BizDay = bizDay, Batch = batch, Barcode = barcode,
            EquipmentNo = equip, Pid = "1", Status = status, LogTime = logTime,
            CreatedAt = DateTime.Now, TestDataId = testDataId, ArchivedAt = archivedAt,
        });
        db.SaveChanges();
    }

    private void SeedResult(string bizDay, string batch, string barcode, string? chute,
        DateTime? archivedAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        db.B2bWorkResults.Add(new WorkResult
        {
            BizDay = bizDay, Batch = batch, Barcode = barcode, ChuteNo = chute,
            CreatedAt = DateTime.Now, ArchivedAt = archivedAt,
        });
        db.SaveChanges();
    }

    // ════════════════════════════════════════════════════════════════════════
    // E1/E2 — 투입/분류 로그 조회 + 파생 필드(등록 슈트·수신시각) + 0건 []
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task E1_InputLogs_RawArray_WithDerivedChuteAndReceiveTime()
    {
        var recv = new DateTime(2026, 8, 1, 9, 0, 0);
        var id = SeedTestData("2026-08-01", "L1", "BC-E1", "007", recv);
        SeedLog("INPUT", "2026-08-01", "L1", "BC-E1", "3", new DateTime(2026, 8, 1, 10, 0, 0), id);

        var resp = await _client.GetAsync("/api/logs/input?bizDay=20260801");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        var row = doc.RootElement.EnumerateArray().Single(r => r.GetProperty("barcode").GetString() == "BC-E1");
        Assert.Equal("L1", row.GetProperty("batch").GetString());
        Assert.Equal("3", row.GetProperty("equipmentNo").GetString());      // INPUT=inductionNo
        Assert.Equal("007", row.GetProperty("chuteNo").GetString());        // 등록 test_data 파생
        Assert.Equal("OK", row.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("archivedAt").ValueKind);
        Assert.False(string.IsNullOrEmpty(row.GetProperty("receiveTime").GetString()));  // 파생 수신시각
    }

    [Fact]
    public async Task E2_SortLogs_RawArray_ChuteInEquipmentNo()
    {
        var id = SeedTestData("2026-08-02", "L2", "BC-E2", "005");
        SeedLog("SORT", "2026-08-02", "L2", "BC-E2", "005", new DateTime(2026, 8, 2, 10, 0, 0), id);

        var resp = await _client.GetAsync("/api/logs/sort?bizDay=20260802");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var row = doc.RootElement.EnumerateArray().Single(r => r.GetProperty("barcode").GetString() == "BC-E2");
        Assert.Equal("005", row.GetProperty("equipmentNo").GetString());   // SORT=chuteNo
    }

    [Fact]
    public async Task E1_DerivedFields_ComeFromSameRow_NoFrankenstein()
    {
        // ★ 코드리뷰 #1 회귀: 동일 barcode 두 test_data 행(다른 슈트·다른 수신시각).
        //   파생 슈트·수신시각은 반드시 SAME(최소 Id) 행에서 와야 함 — 두 필드가 서로 다른 행에서
        //   섞이면(Frankenstein) 실패. 단일 결정적 서브쿼리(OrderBy Id)로 교정.
        var id1 = SeedTestData("2026-08-12", "FR", "BC-FRANKEN", "001", new DateTime(2026, 8, 12, 8, 0, 0)); // 최소 Id
        SeedTestData("2026-08-12", "FR", "BC-FRANKEN", "002", new DateTime(2026, 8, 12, 9, 0, 0));           // 더 큰 Id
        SeedLog("INPUT", "2026-08-12", "FR", "BC-FRANKEN", "1", new DateTime(2026, 8, 12, 10, 0, 0), id1);

        var resp = await _client.GetAsync("/api/logs/input?bizDay=20260812");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var row = doc.RootElement.EnumerateArray().Single(r => r.GetProperty("barcode").GetString() == "BC-FRANKEN");
        // 슈트·수신시각 둘 다 최소 Id 행(001 / 08:00)에서 — 섞이지 않음.
        Assert.Equal("001", row.GetProperty("chuteNo").GetString());
        Assert.Equal(new DateTime(2026, 8, 12, 8, 0, 0), row.GetProperty("receiveTime").GetDateTime());
    }

    [Fact]
    public async Task E1_ZeroRows_EmptyArray()
    {
        var resp = await _client.GetAsync("/api/logs/input?bizDay=20990101");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task E1_InvalidCalendarDate_400_Message17()
    {
        var resp = await _client.GetAsync("/api/logs/input?bizDay=20261332");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ApiResp>();
        Assert.Equal("F", body!.Status);
        Assert.Equal("Invalid date: 20261332", body.Message);
    }

    // ════════════════════════════════════════════════════════════════════════
    // ★ 아카이브 필터 3상태(E1) + DB COUNT 불변(하드삭제 0)
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task E1_ArchiveFilter_ThreeStates_CountInvariant()
    {
        var id = SeedTestData("2026-08-03", "AR", "BC-ARC", "001");
        // 활성 로그 1 + 아카이브 로그 1(동일 barcode, 다른 시각)
        SeedLog("INPUT", "2026-08-03", "AR", "BC-ARC", "1", new DateTime(2026, 8, 3, 10, 0, 0), id);
        SeedLog("INPUT", "2026-08-03", "AR", "BC-ARC", "1", new DateTime(2026, 8, 3, 11, 0, 0), id,
            archivedAt: DateTime.Now);

        int CountRows(string archived)
        {
            var resp = _client.GetAsync($"/api/logs/input?bizDay=20260803&archived={archived}").Result;
            using var doc = JsonDocument.Parse(resp.Content.ReadAsStringAsync().Result);
            return doc.RootElement.EnumerateArray().Count();
        }

        Assert.Equal(1, CountRows("active"));         // 미아카이브만
        Assert.Equal(1, CountRows("archivedOnly"));   // 아카이브만
        Assert.Equal(2, CountRows("all"));            // 전부
        Assert.Equal(1, CountRows("bogus"));          // 미인식 → active

        // DB COUNT 불변(하드삭제 0) — 두 로그 모두 잔존.
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        Assert.Equal(2, db.B2bTestLogs.Count(l => l.BizDay == "2026-08-03" && l.Barcode == "BC-ARC"));
    }

    // ════════════════════════════════════════════════════════════════════════
    // E3 — API 호출 이력: date 필터 + 최대 500건 상한
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task E3_ApiCalls_DateFilter_And_500Cap()
    {
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            // 대상일 505건(상한 500 검증) + 타 일자 3건(날짜 필터 검증)
            for (var i = 0; i < 505; i++)
                db.B2bApiCallLogs.Add(new ApiCallLog
                {
                    Endpoint = "/api/v1/works/input", HttpMethod = "POST", ResponseStatus = "S",
                    HttpStatusCode = 200, DurationMs = 5, CalledAt = new DateTime(2026, 8, 4, 10, 0, i % 60),
                });
            for (var i = 0; i < 3; i++)
                db.B2bApiCallLogs.Add(new ApiCallLog
                {
                    Endpoint = "/api/v1/works/box", HttpMethod = "POST", ResponseStatus = "S",
                    HttpStatusCode = 200, DurationMs = 5, CalledAt = new DateTime(2026, 8, 5, 10, 0, 0),
                });
            db.SaveChanges();
        }

        var resp = await _client.GetAsync("/api/logs/api-calls?date=20260804");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var rows = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(500, rows.Count);   // 505 → 상한 500(AppConstants.ApiCallLogMaxItems)
        Assert.All(rows, r => Assert.Equal("/api/v1/works/input", r.GetProperty("endpoint").GetString()));  // 날짜 필터
        Assert.Equal("POST", rows[0].GetProperty("httpMethod").GetString());
    }

    // ════════════════════════════════════════════════════════════════════════
    // E4 — 투입+분류 통합 Excel: Phase1(TestDataId) / Phase2(폴백) / 소요시간 / 층매핑 / 미매칭 공백
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task E4_Export_Phase1_Duration_FloorMapping()
    {
        // 인덕션 1(→2층) INPUT + TestDataId 로 정밀 매칭되는 SORT(슈트 010), 소요 5초.
        var id = SeedTestData("2026-08-06", "X1", "BC-X1", "010");
        SeedLog("INPUT", "2026-08-06", "X1", "BC-X1", "1", new DateTime(2026, 8, 6, 10, 0, 0), id);
        SeedLog("SORT",  "2026-08-06", "X1", "BC-X1", "010", new DateTime(2026, 8, 6, 10, 0, 5), id);

        var resp = await _client.GetAsync("/api/logs/export?bizDay=20260806");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("input_sort_logs", resp.Content.Headers.ContentDisposition!.ToString());
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            resp.Content.Headers.ContentType!.MediaType);

        var bytes = await resp.Content.ReadAsByteArrayAsync();
        var r = ReadExportRow(bytes, "BC-X1");
        Assert.Equal("1", r["인덕션"]);
        Assert.Equal("2층", r["층"]);
        Assert.Equal("010", r["슈트"]);       // Phase1 매칭된 SORT chuteNo
        Assert.Equal("5.0", r["소요시간(초)"]);
    }

    [Fact]
    public async Task E4_Export_Phase2_Fallback_And_UnmatchedBlank()
    {
        // INPUT(TestDataId 있음, 인덕션 3→1층) + SORT(TestDataId 없음) → Phase1 실패, Phase2 (Batch,Barcode) 매칭.
        var id = SeedTestData("2026-08-07", "X2", "BC-X2", "020");
        SeedLog("INPUT", "2026-08-07", "X2", "BC-X2", "3", new DateTime(2026, 8, 7, 10, 0, 0), id);
        SeedLog("SORT",  "2026-08-07", "X2", "BC-X2", "020", new DateTime(2026, 8, 7, 10, 0, 3), testDataId: null);
        // 매칭 SORT 없는 INPUT(인덕션 5→공백) → 슈트/소요시간 공백.
        var id2 = SeedTestData("2026-08-07", "X2", "BC-X2B", "030");
        SeedLog("INPUT", "2026-08-07", "X2", "BC-X2B", "5", new DateTime(2026, 8, 7, 10, 1, 0), id2);

        var resp = await _client.GetAsync("/api/logs/export?bizDay=20260807&batch=X2");
        var bytes = await resp.Content.ReadAsByteArrayAsync();

        var matched = ReadExportRow(bytes, "BC-X2");
        Assert.Equal("1층", matched["층"]);       // 인덕션 3
        Assert.Equal("020", matched["슈트"]);      // Phase2 폴백 매칭
        Assert.Equal("3.0", matched["소요시간(초)"]);

        var unmatched = ReadExportRow(bytes, "BC-X2B");
        Assert.Equal("", unmatched["층"]);          // 인덕션 5 → 공백
        Assert.Equal("", unmatched["슈트"]);         // SORT 미매칭 → 공백
        Assert.Equal("", unmatched["소요시간(초)"]);
    }

    [Fact]
    public async Task E4_Export_MissingBizDay_400()
    {
        var resp = await _client.GetAsync("/api/logs/export");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ApiResp>();
        Assert.Equal("bizDay parameter is required.", body!.Message);
    }

    // Excel 바이트 → {헤더:값} 딕셔너리(지정 barcode 행).
    private static Dictionary<string, string> ReadExportRow(byte[] bytes, string barcode)
    {
        using var ms = new MemoryStream(bytes);
        using var wb = new XLWorkbook(ms);
        var ws = wb.Worksheet(1);
        var headers = ws.Row(1).CellsUsed().Select(c => c.GetString()).ToList();
        foreach (var row in ws.RowsUsed().Skip(1))
        {
            var map = new Dictionary<string, string>();
            for (var c = 0; c < headers.Count; c++)
                map[headers[c]] = ws.Cell(row.RowNumber(), c + 1).GetString();
            if (map["바코드"] == barcode) return map;
        }
        throw new Xunit.Sdk.XunitException($"Excel 에 barcode {barcode} 행이 없음");
    }

    // ════════════════════════════════════════════════════════════════════════
    // E5 — 3-way 비교: 일치/불일치/누락 + ★Batch 포함 키 음성 대조 + 아카이브 필터
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task E5_Comparison_Match_Mismatch_Missing()
    {
        // 일치: INPUT+SORT(슈트001)+RESULT(슈트001)
        var m1 = SeedTestData("2026-08-08", "C1", "BC-MATCH", "001");
        SeedLog("INPUT", "2026-08-08", "C1", "BC-MATCH", "1", new DateTime(2026, 8, 8, 10, 0, 0), m1);
        SeedLog("SORT",  "2026-08-08", "C1", "BC-MATCH", "001", new DateTime(2026, 8, 8, 10, 0, 5), m1);
        SeedResult("2026-08-08", "C1", "BC-MATCH", "001");
        // 불일치: SORT(002) ≠ RESULT(003)
        var m2 = SeedTestData("2026-08-08", "C1", "BC-MISM", "002");
        SeedLog("INPUT", "2026-08-08", "C1", "BC-MISM", "1", new DateTime(2026, 8, 8, 10, 1, 0), m2);
        SeedLog("SORT",  "2026-08-08", "C1", "BC-MISM", "002", new DateTime(2026, 8, 8, 10, 1, 5), m2);
        SeedResult("2026-08-08", "C1", "BC-MISM", "003");
        // 누락: INPUT 만
        var m3 = SeedTestData("2026-08-08", "C1", "BC-MISS", "004");
        SeedLog("INPUT", "2026-08-08", "C1", "BC-MISS", "1", new DateTime(2026, 8, 8, 10, 2, 0), m3);

        var resp = await _client.GetAsync("/api/test-data/comparison?bizDay=20260808");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var rows = doc.RootElement.EnumerateArray().ToList();

        var match = rows.Single(r => r.GetProperty("barcode").GetString() == "BC-MATCH");
        Assert.True(match.GetProperty("isMatch").GetBoolean());
        Assert.False(match.GetProperty("isMissing").GetBoolean());
        Assert.Equal("001", match.GetProperty("sortChuteNo").GetString());
        Assert.Equal("001", match.GetProperty("resultChuteNo").GetString());

        var mism = rows.Single(r => r.GetProperty("barcode").GetString() == "BC-MISM");
        Assert.False(mism.GetProperty("isMatch").GetBoolean());
        Assert.False(mism.GetProperty("isMissing").GetBoolean());   // 3자 존재하나 슈트 불일치
        Assert.True(mism.GetProperty("hasSort").GetBoolean());
        Assert.True(mism.GetProperty("hasResult").GetBoolean());

        var miss = rows.Single(r => r.GetProperty("barcode").GetString() == "BC-MISS");
        Assert.True(miss.GetProperty("isMissing").GetBoolean());
        Assert.False(miss.GetProperty("hasSort").GetBoolean());
        Assert.False(miss.GetProperty("hasResult").GetBoolean());
    }

    [Fact]
    public async Task E5_Comparison_BothChuteNull_NotMatch()
    {
        // ★ 코드리뷰 #5: INPUT+SORT(슈트 null)+RESULT(슈트 null) — 3자 존재하나 슈트값 없음.
        //   둘 다 null 이면 == 이 true 가 되던 오판정 방지 → isMatch=false, isMissing=false(3자 존재).
        var id = SeedTestData("2026-08-13", "CN", "BC-NULLCHUTE", "001");
        SeedLog("INPUT", "2026-08-13", "CN", "BC-NULLCHUTE", "1", new DateTime(2026, 8, 13, 10, 0, 0), id);
        SeedLog("SORT",  "2026-08-13", "CN", "BC-NULLCHUTE", null, new DateTime(2026, 8, 13, 10, 0, 5), id);   // equip null
        SeedResult("2026-08-13", "CN", "BC-NULLCHUTE", null);   // chute null

        var resp = await _client.GetAsync("/api/test-data/comparison?bizDay=20260813");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var row = doc.RootElement.EnumerateArray().Single(r => r.GetProperty("barcode").GetString() == "BC-NULLCHUTE");
        Assert.True(row.GetProperty("hasInput").GetBoolean());
        Assert.True(row.GetProperty("hasSort").GetBoolean());
        Assert.True(row.GetProperty("hasResult").GetBoolean());
        Assert.False(row.GetProperty("isMatch").GetBoolean());     // 둘 다 null → 불일치(오판정 방지)
        Assert.False(row.GetProperty("isMissing").GetBoolean());   // 3자 존재
    }

    [Fact]
    public async Task E5_Comparison_BatchIncludedKey_NegativeControl()
    {
        // 같은 bizDay·같은 barcode, 다른 batch. B1 에만 로그/결과. B2 test_data 는 로그 0.
        var b1 = SeedTestData("2026-08-09", "B1", "BC-DUP", "001");
        SeedLog("INPUT", "2026-08-09", "B1", "BC-DUP", "1", new DateTime(2026, 8, 9, 10, 0, 0), b1);
        SeedLog("SORT",  "2026-08-09", "B1", "BC-DUP", "001", new DateTime(2026, 8, 9, 10, 0, 5), b1);
        SeedResult("2026-08-09", "B1", "BC-DUP", "001");
        SeedTestData("2026-08-09", "B2", "BC-DUP", "001");   // 다른 batch, 로그 없음

        var resp = await _client.GetAsync("/api/test-data/comparison?bizDay=20260809");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var rows = doc.RootElement.EnumerateArray().ToList();

        var b2 = rows.Single(r => r.GetProperty("batch").GetString() == "B2" && r.GetProperty("barcode").GetString() == "BC-DUP");
        // Batch 포함 키 → B2 는 B1 의 로그를 오매칭하지 않음(이월 Barcode-only 결함 미재현).
        Assert.False(b2.GetProperty("hasInput").GetBoolean());
        Assert.False(b2.GetProperty("hasSort").GetBoolean());
        Assert.False(b2.GetProperty("hasResult").GetBoolean());
        Assert.True(b2.GetProperty("isMissing").GetBoolean());

        // 양성 대조: B1 은 정상 일치.
        var b1Row = rows.Single(r => r.GetProperty("batch").GetString() == "B1" && r.GetProperty("barcode").GetString() == "BC-DUP");
        Assert.True(b1Row.GetProperty("isMatch").GetBoolean());
    }

    [Fact]
    public async Task E5_Comparison_ArchiveFilter_ExcludesArchivedLogs()
    {
        var id = SeedTestData("2026-08-10", "CA", "BC-CARC", "001");
        SeedLog("INPUT", "2026-08-10", "CA", "BC-CARC", "1", new DateTime(2026, 8, 10, 10, 0, 0), id,
            archivedAt: DateTime.Now);   // 아카이브된 로그

        // active: 아카이브 로그 제외 → hasInput=false
        var active = await _client.GetAsync("/api/test-data/comparison?bizDay=20260810&archived=active");
        using (var doc = JsonDocument.Parse(await active.Content.ReadAsStringAsync()))
        {
            var row = doc.RootElement.EnumerateArray().Single(r => r.GetProperty("barcode").GetString() == "BC-CARC");
            Assert.False(row.GetProperty("hasInput").GetBoolean());
        }
        // archivedOnly: 아카이브 로그만 → hasInput=true
        var arch = await _client.GetAsync("/api/test-data/comparison?bizDay=20260810&archived=archivedOnly");
        using (var doc = JsonDocument.Parse(await arch.Content.ReadAsStringAsync()))
        {
            var row = doc.RootElement.EnumerateArray().Single(r => r.GetProperty("barcode").GetString() == "BC-CARC");
            Assert.True(row.GetProperty("hasInput").GetBoolean());
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // E6 — 박스 목록 + 내품: happy(POST 적재 → GET 되읽기) / batch 필터 / bizDay 누락 400 / 0건 []
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task E6_Boxes_RoundTrip_WithItems()
    {
        var box = new { bizDay = "20260811", batch = "BX", boxNo = "BOX-Q1", chuteNo = "3",
            items = new[] { new { barcode = "BC-B1", qty = 2 }, new { barcode = "BC-B2", qty = 1 } },
            endTime = "2026-08-11 10:00:00" };
        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync("/api/v1/works/box", box)).StatusCode);

        var resp = await _client.GetAsync("/api/boxes?bizDay=20260811&batch=BX");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var b = doc.RootElement.EnumerateArray().Single(x => x.GetProperty("boxNo").GetString() == "BOX-Q1");
        Assert.Equal("2026-08-11", b.GetProperty("bizDay").GetString());
        Assert.Equal("003", b.GetProperty("chuteNo").GetString());   // 3자리 정규화
        Assert.Equal("2026-08-11 10:00:00", b.GetProperty("endTime").GetString());
        var items = b.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i.GetProperty("barcode").GetString() == "BC-B1" && i.GetProperty("qty").GetInt32() == 2);
    }

    [Fact]
    public async Task E6_Boxes_MissingBizDay_400()
    {
        var resp = await _client.GetAsync("/api/boxes");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ApiResp>();
        Assert.Equal("bizDay parameter is required.", body!.Message);
    }

    [Fact]
    public async Task E6_Boxes_ZeroRows_EmptyArray()
    {
        var resp = await _client.GetAsync("/api/boxes?bizDay=20990202");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetArrayLength());
    }
}
