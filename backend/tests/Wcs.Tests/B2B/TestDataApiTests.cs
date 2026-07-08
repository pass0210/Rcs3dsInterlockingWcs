using System.Net;
using System.Net.Http.Headers;
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
// S-B2B-2a API 통합 테스트 — test-data 관리 6 엔드포인트(§1) + ★아카이브 시나리오(§3).
// B2bWebApplicationFactory(INSTANCE-level in-memory SQLite) 재사용. 테스트별 고유 bizDay 로 격리.
// ════════════════════════════════════════════════════════════════════════════
public class TestDataApiTests : IClassFixture<B2bWebApplicationFactory>
{
    private readonly B2bWebApplicationFactory _factory;
    private readonly HttpClient _client;
    private readonly ITestOutputHelper _out;

    public TestDataApiTests(B2bWebApplicationFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _client  = factory.CreateClient();
        _out     = output;
    }

    private sealed record ApiResp(string Status, string Message);

    // ── generate ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Generate_RoundTrip_Summary_Detail()
    {
        var req = new { bizDay = "20260720", batch = "G1", chuteNos = "1-2", barcodeCount = 4 };
        var gen = await _client.PostAsJsonAsync("/api/test-data/generate", req);
        Assert.Equal(HttpStatusCode.OK, gen.StatusCode);
        Assert.Equal("S", (await gen.Content.ReadFromJsonAsync<ApiResp>())!.Status);

        // summary — 배치 G1 4건
        var sum = await _client.GetAsync("/api/test-data/summary?bizDay=20260720");
        Assert.Equal(HttpStatusCode.OK, sum.StatusCode);
        using (var doc = JsonDocument.Parse(await sum.Content.ReadAsStringAsync()))
        {
            var g1 = doc.RootElement.EnumerateArray()
                .Single(e => e.GetProperty("batch").GetString() == "G1");
            Assert.Equal("2026-07-20", g1.GetProperty("bizDay").GetString());   // 정규화
            Assert.Equal(4, g1.GetProperty("count").GetInt32());
        }

        // detail — 4행, 라운드로빈 슈트(001×2, 002×2)
        var det = await _client.GetAsync("/api/test-data/detail?bizDay=20260720&batch=G1");
        Assert.Equal(HttpStatusCode.OK, det.StatusCode);
        using (var doc = JsonDocument.Parse(await det.Content.ReadAsStringAsync()))
        {
            var rows = doc.RootElement.EnumerateArray().ToList();
            Assert.Equal(4, rows.Count);
            Assert.Equal(2, rows.Count(r => r.GetProperty("chuteNo").GetString() == "001"));
            Assert.Equal(2, rows.Count(r => r.GetProperty("chuteNo").GetString() == "002"));
        }
    }

    [Fact]
    public async Task Generate_EmptyChutes_200F()
    {
        // 콤마·공백만 → 파싱 0개(정규식 통과, 서비스가 F).
        var req = new { bizDay = "20260720", batch = "GE", chuteNos = " , ", barcodeCount = 2 };
        var resp = await _client.PostAsJsonAsync("/api/test-data/generate", req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ApiResp>();
        Assert.Equal("F", body!.Status);
        Assert.Equal("Invalid chute numbers", body.Message);
    }

    [Fact]
    public async Task Generate_BarcodeCountOutOfRange_400_AllowlistFail()
    {
        // BarcodeCount=0 → DTO [Range] 실패 → allowlist(/api/test-data) → B2BApiResponse.Fail.
        var req = new { bizDay = "20260720", batch = "GR", chuteNos = "1", barcodeCount = 0 };
        var resp = await _client.PostAsJsonAsync("/api/test-data/generate", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ApiResp>();
        Assert.Equal("F", body!.Status);
        Assert.Equal("BarcodeCount must be between 1 and 10000.", body.Message);
    }

    [Fact]
    public async Task Generate_BadBizDayFormat_400_AllowlistFail()
    {
        var req = new { bizDay = "2026/07/20", batch = "GB", chuteNos = "1", barcodeCount = 1 };
        var resp = await _client.PostAsJsonAsync("/api/test-data/generate", req);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ApiResp>();
        Assert.Equal("F", body!.Status);
        Assert.Equal("BizDay must be in YYYYMMDD or YYYY-MM-DD format.", body.Message);
    }

    // ── upload ──────────────────────────────────────────────────────────────

    private static byte[] BuildXlsx(Action<IXLWorksheet> fill)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Sheet1");
        fill(ws);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static MultipartFormDataContent FileContent(byte[] bytes, string fileName, string mime)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(mime);
        content.Add(file, "file", fileName);
        return content;
    }

    [Fact]
    public async Task Upload_HappyPath_200S_NCount()
    {
        var bytes = BuildXlsx(ws =>
        {
            ws.Cell(1, 1).Value = "BizDay"; ws.Cell(1, 2).Value = "Batch"; ws.Cell(1, 3).Value = "Barcode";
            ws.Cell(1, 4).Value = "Barcode2"; ws.Cell(1, 5).Value = "ChuteNo";
            ws.Cell(2, 1).Value = "20260722"; ws.Cell(2, 2).Value = "U1"; ws.Cell(2, 3).Value = "BC-U1"; ws.Cell(2, 5).Value = "3";
            ws.Cell(3, 1).Value = "20260722"; ws.Cell(3, 2).Value = "U1"; ws.Cell(3, 3).Value = "BC-U2"; ws.Cell(3, 5).Value = "4";
        });
        using var content = FileContent(bytes, "up.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        var resp = await _client.PostAsync("/api/test-data/upload", content);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ApiResp>();
        Assert.Equal("S", body!.Status);
        Assert.Equal("2건 업로드 완료", body.Message);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        Assert.Equal(2, db.B2bTestData.Count(d => d.BizDay == "2026-07-22" && d.Batch == "U1"));
    }

    [Fact]
    public async Task Upload_EmptyFile_400_PleaseSelectFile()
    {
        // 잘 형성된 멀티파트 + 0바이트 file 파트 → file.Length==0 → "Please select a file."(#1).
        using var content = FileContent(Array.Empty<byte>(), "empty.xlsx",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        var resp = await _client.PostAsync("/api/test-data/upload", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ApiResp>();
        Assert.Equal("Please select a file.", body!.Message);
    }

    [Fact]
    public async Task Upload_WrongExtension_400_OnlyExcel()
    {
        using var content = FileContent(new byte[] { 1, 2, 3 }, "data.txt", "application/octet-stream");
        var resp = await _client.PostAsync("/api/test-data/upload", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ApiResp>();
        Assert.Equal("Only Excel (.xlsx, .xls) files can be uploaded.", body!.Message);
    }

    [Fact]
    public async Task Upload_WrongMime_400_InvalidFileFormat()
    {
        // 확장자는 .xlsx 통과, MIME 은 화이트리스트 밖 → Invalid file format.
        using var content = FileContent(new byte[] { 1, 2, 3 }, "data.xlsx", "text/plain");
        var resp = await _client.PostAsync("/api/test-data/upload", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ApiResp>();
        Assert.Equal("Invalid file format.", body!.Message);
    }

    // ── ★ 아카이브 시나리오(API) — reset/delete 하드삭제 0 ──────────────────────

    private long SeedOneWithLogAndResult(string bizDay, string batch, string barcode)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        var td = new TestData
        {
            BizDay = bizDay, Batch = batch, Barcode = barcode, ChuteNo = "001",
            ReceiveTime = DateTime.Now, CreatedAt = DateTime.Now,
        };
        db.B2bTestData.Add(td);
        db.SaveChanges();
        db.B2bTestLogs.Add(new TestLog
        {
            LogType = "INPUT", BizDay = bizDay, Batch = batch, Barcode = barcode,
            TestDataId = td.Id, Status = "OK", LogTime = DateTime.Now, CreatedAt = DateTime.Now,
        });
        db.B2bWorkResults.Add(new WorkResult
        {
            BizDay = bizDay, Batch = batch, Barcode = barcode, ChuteNo = "001", CreatedAt = DateTime.Now,
        });
        db.SaveChanges();
        return td.Id;
    }

    [Fact]
    public async Task Reset_Api_ArchivesLogs_NoHardDelete()
    {
        var id = SeedOneWithLogAndResult("2026-07-23", "R1", "BC-RST");
        var resp = await _client.PostAsJsonAsync("/api/test-data/reset", new[] { id });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("S", (await resp.Content.ReadFromJsonAsync<ApiResp>())!.Status);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        // 하드삭제 0: 로그/결과 잔존 + archived_at 세팅. test_data 잔존 + receive_time null.
        Assert.Null(db.B2bTestData.Single(d => d.Id == id).ReceiveTime);
        var log = db.B2bTestLogs.Single(l => l.Barcode == "BC-RST");
        Assert.NotNull(log.ArchivedAt);
        Assert.NotNull(db.B2bWorkResults.Single(w => w.Barcode == "BC-RST").ArchivedAt);

        // active 필터엔 미노출(로그), archivedOnly 엔 노출
        var active = await _client.GetAsync("/api/test-data/detail?bizDay=20260723&batch=R1&archived=active");
        using (var doc = JsonDocument.Parse(await active.Content.ReadAsStringAsync()))
        {
            var row = doc.RootElement.EnumerateArray().Single();
            Assert.Equal(JsonValueKind.Null, row.GetProperty("inputStatus").ValueKind);
        }
        var arch = await _client.GetAsync("/api/test-data/detail?bizDay=20260723&batch=R1&archived=archivedOnly");
        using (var doc = JsonDocument.Parse(await arch.Content.ReadAsStringAsync()))
        {
            var row = doc.RootElement.EnumerateArray().Single();
            Assert.Equal("OK", row.GetProperty("inputStatus").GetString());
        }
    }

    [Fact]
    public async Task Delete_Api_HardDeletesTestData_ArchivesLogs()
    {
        var id = SeedOneWithLogAndResult("2026-07-24", "D1", "BC-DEL");
        var req = new HttpRequestMessage(HttpMethod.Delete, "/api/test-data")
        {
            Content = JsonContent.Create(new[] { id }),
        };
        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal("S", (await resp.Content.ReadFromJsonAsync<ApiResp>())!.Status);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        Assert.Empty(db.B2bTestData.Where(d => d.Id == id));            // test_data 하드삭제
        Assert.NotNull(db.B2bTestLogs.Single(l => l.Barcode == "BC-DEL").ArchivedAt);   // 로그 archived(잔존)
        Assert.NotNull(db.B2bWorkResults.Single(w => w.Barcode == "BC-DEL").ArchivedAt);
    }
}
