using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using ClosedXML.Excel;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api.B2C;
using Wcs.Data;
using Wcs.Tests.B2B;   // TestDb + B2bWebApplicationFactory(in-memory SQLite 격리) 재사용.
using Xunit;

namespace Wcs.Tests.B2C;

// ════════════════════════════════════════════════════════════════════════════
// S-B2C-EXCEL-UPLOAD 테스트 — 엑셀 업로드(행=오더/바코드 1건, 미할당, 멱등 append, 오류 시 전체거부).
//   ① 순수 검증(ValidateUploadRows) 단위 — I/O 무의존(절대규칙 #8·테스트가 스펙).
//   ② 서비스(UploadExcelAsync) — TestDb(in-memory SQLite)에 xlsx 스트림 투입: 정상·롤백·멱등·상한·빈파일.
//   ③ API 왕복(multipart) — happy/400/200F.
//   ④ 정적 양식 라운드트립 — frontend/public 의 커밋된 .xlsx 를 파서에 재투입(헤더 정합·드리프트 0).
// ════════════════════════════════════════════════════════════════════════════

public class B2cUploadServiceTests
{
    // ── xlsx 빌더(헤더행 + 데이터행) — 셀은 전부 문자열(결정성). ─────────────────────
    private static byte[] BuildXlsx(IEnumerable<string[]> dataRows, bool header = true)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("업로드");
        int r = 1;
        if (header)
        {
            ws.Cell(1, 1).Value = "작업일자"; ws.Cell(1, 2).Value = "배치명"; ws.Cell(1, 3).Value = "차수";
            ws.Cell(1, 4).Value = "바코드"; ws.Cell(1, 5).Value = "수량";
            r = 2;
        }
        foreach (var row in dataRows)
        {
            for (int c = 0; c < row.Length; c++) ws.Cell(r, c + 1).Value = row[c];
            r++;
        }
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static B2cUploadRawRow Raw(int n, string d, string b, string w, string bc, string q) =>
        new(n, d, b, w, bc, q);

    // ════════════════════════════════════════════════════════════════════════
    // ① 순수 검증(ValidateUploadRows)
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Validate_ValidRows_ParsesNormalizedWithDefaults()
    {
        var (rows, errors) = B2cTestDataService.ValidateUploadRows(new[]
        {
            Raw(2, "20260726", "U-A", "",  "BC-1", ""),   // 차수/수량 공백 → 기본 1
            Raw(3, "2026-07-26", "U-A", "2", "BC-2", "5"),
        });
        Assert.Empty(errors);
        Assert.Equal(2, rows.Count);
        Assert.Equal("2026-07-26", rows[0].WorkDate);   // 정규화
        Assert.Equal(1, rows[0].WaveNo);
        Assert.Equal(1, rows[0].PlannedQty);
        Assert.Equal("BC-1", rows[0].Barcode);
        Assert.Equal(2, rows[1].WaveNo);
        Assert.Equal(5, rows[1].PlannedQty);
    }

    [Fact]
    public void Validate_MissingRequired_ReportsRowErrors()
    {
        var (rows, errors) = B2cTestDataService.ValidateUploadRows(new[]
        {
            Raw(2, "",         "U", "1", "BC-1", "1"),  // 작업일자 누락
            Raw(3, "20260726", "",  "1", "BC-2", "1"),  // 배치명 누락
            Raw(4, "20260726", "U", "1", "",     "1"),  // 바코드 누락
        });
        Assert.Empty(rows);
        Assert.Equal(3, errors.Count);
        Assert.Equal(2, errors[0].Row);
        Assert.Contains("작업일자", errors[0].Message);
        Assert.Contains("배치명", errors[1].Message);
        Assert.Contains("바코드", errors[2].Message);
    }

    [Fact]
    public void Validate_BadFormats_ReportsRowErrors()
    {
        var (rows, errors) = B2cTestDataService.ValidateUploadRows(new[]
        {
            Raw(2, "20261332", "U", "1", "BC-1", "1"),        // 비존재 날짜
            Raw(3, "20260726", "U", "0", "BC-2", "1"),        // 차수 범위
            Raw(4, "20260726", "U", "1", "BC 3;DROP", "1"),   // 바코드 인젝션 문자
            Raw(5, "20260726", "U", "1", "BC-4", "abc"),      // 수량 비정수
        });
        Assert.Empty(rows);
        Assert.Equal(4, errors.Count);
    }

    [Fact]
    public void Validate_DuplicateWithinFile_ReportsRowError()
    {
        var (rows, errors) = B2cTestDataService.ValidateUploadRows(new[]
        {
            Raw(2, "20260726", "U", "1", "BC-1", "1"),
            Raw(3, "2026-07-26", "U", "1", "BC-1", "1"),   // 동일 키(정규화 후) — 중복
        });
        Assert.Single(rows);
        Assert.Single(errors);
        Assert.Equal(3, errors[0].Row);
        Assert.Contains("중복", errors[0].Message);
    }

    // ════════════════════════════════════════════════════════════════════════
    // ② 서비스(UploadExcelAsync) — TestDb
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Upload_Valid_CreatesUnassignedOrders_BarcodeEqualsOrderNo()
    {
        await using var tdb = new TestDb();
        var log = new CapturingOperationLogger();
        var bytes = BuildXlsx(new[]
        {
            new[] { "20260726", "UP-A", "1", "BC-10", "1" },
            new[] { "20260726", "UP-A", "1", "BC-11", "3" },
        });

        await using (var db = tdb.Create())
        {
            var res = await new B2cTestDataService(db, log).UploadExcelAsync(new MemoryStream(bytes));
            Assert.Equal("S", res.Status);
            Assert.Equal(2, res.Counts!["ordersCreated"]);
            Assert.Equal(2, res.Counts["orderItemsCreated"]);
            Assert.Equal(1, res.Counts["batches"]);
            Assert.Equal(2, res.Counts["dataRows"]);
            Assert.Null(res.RowErrors);
        }
        await using (var db = tdb.Create())
        {
            var orders = db.Orders.Where(o => o.OrderNo.StartsWith("BC-1")).ToList();
            Assert.Equal(2, orders.Count);
            Assert.All(orders, o =>
            {
                Assert.Null(o.DestinationId);        // 미할당
                Assert.Null(o.DestAssignType);
                Assert.Equal(OrderStatus.RUNNING, o.Status);
            });
            var items = db.OrderItems.Where(i => i.Barcode.StartsWith("BC-1")).ToList();
            Assert.Equal(2, items.Count);
            Assert.Contains(items, i => i.Barcode == "BC-10" && i.PlannedQty == 1);
            Assert.Contains(items, i => i.Barcode == "BC-11" && i.PlannedQty == 3);   // 수량 컬럼 반영
        }
        Assert.Contains(log.Entries, e => e.Category == OperationLogCategory.STATE && e.Action == "B2C_UPLOAD");
    }

    [Fact]
    public async Task Upload_RowError_RejectsWholeFile_ZeroCreated()
    {
        await using var tdb = new TestDb();
        var bytes = BuildXlsx(new[]
        {
            new[] { "20260726", "UP-B", "1", "OK-1", "1" },   // 유효
            new[] { "20260726", "UP-B", "1", "",     "1" },   // 바코드 누락(오류)
        });

        await using (var db = tdb.Create())
        {
            var res = await new B2cTestDataService(db, new CapturingOperationLogger()).UploadExcelAsync(new MemoryStream(bytes));
            Assert.Equal("F", res.Status);
            Assert.NotNull(res.RowErrors);
            Assert.Single(res.RowErrors!);
            Assert.Equal(3, res.RowErrors![0].Row);   // 엑셀 3행(헤더 1 + 유효 2 + 오류 3)
        }
        // 원자성: 유효했던 OK-1 조차 커밋되지 않음(전체 롤백 = 0건).
        await using (var db = tdb.Create())
        {
            Assert.Equal(0, db.Orders.Count());
            Assert.Equal(0, db.OrderItems.Count());
            Assert.Equal(0, db.WorkBatches.Count());
        }
    }

    [Fact]
    public async Task Upload_Idempotent_ReuploadZeroNew_PreservesQty()
    {
        await using var tdb = new TestDb();
        var bytes = BuildXlsx(new[]
        {
            new[] { "20260726", "UP-C", "1", "IDEM-1", "1" },
            new[] { "20260726", "UP-C", "1", "IDEM-2", "1" },
        });

        await using (var db = tdb.Create())
            await new B2cTestDataService(db, new CapturingOperationLogger()).UploadExcelAsync(new MemoryStream(bytes));

        // 실적(reserved/sorted) 채움 → 재업로드가 클로버하지 않아야 함.
        await using (var db = tdb.Create())
        {
            var item = db.OrderItems.First(i => i.Barcode == "IDEM-1");
            item.ReservedQty = 1; item.SortedQty = 1;
            await db.SaveChangesAsync();
        }

        await using (var db = tdb.Create())
        {
            var res = await new B2cTestDataService(db, new CapturingOperationLogger()).UploadExcelAsync(new MemoryStream(bytes));
            Assert.Equal("S", res.Status);
            Assert.Equal(0, res.Counts!["ordersCreated"]);      // 멱등 — 신규 0
            Assert.Equal(0, res.Counts["orderItemsCreated"]);
        }
        await using (var db = tdb.Create())
        {
            Assert.Equal(2, db.Orders.Count());   // 중복 생성 0
            var item = db.OrderItems.First(i => i.Barcode == "IDEM-1");
            Assert.Equal(1, item.ReservedQty);    // 실적 보존
            Assert.Equal(1, item.SortedQty);
        }
    }

    [Fact]
    public async Task Upload_MultiBatch_GroupsByBatchKey()
    {
        await using var tdb = new TestDb();
        var bytes = BuildXlsx(new[]
        {
            new[] { "20260726", "MB-1", "1", "M-1", "1" },
            new[] { "20260726", "MB-1", "1", "M-2", "1" },
            new[] { "20260726", "MB-2", "1", "M-3", "1" },   // 다른 배치
        });
        await using (var db = tdb.Create())
        {
            var res = await new B2cTestDataService(db, new CapturingOperationLogger()).UploadExcelAsync(new MemoryStream(bytes));
            Assert.Equal("S", res.Status);
            Assert.Equal(3, res.Counts!["ordersCreated"]);
            Assert.Equal(2, res.Counts["batches"]);
        }
        await using (var db = tdb.Create())
            Assert.Equal(2, db.WorkBatches.Count());
    }

    [Fact]
    public async Task Upload_HeaderOnly_ReturnsF_NoValidData()
    {
        await using var tdb = new TestDb();
        var bytes = BuildXlsx(Array.Empty<string[]>());   // 헤더만
        await using var db = tdb.Create();
        var res = await new B2cTestDataService(db, new CapturingOperationLogger()).UploadExcelAsync(new MemoryStream(bytes));
        Assert.Equal("F", res.Status);
    }

    [Fact]
    public async Task Upload_HeaderMismatch_ReturnsF()
    {
        await using var tdb = new TestDb();
        // 헤더 없이 데이터 행이 1행에 옴 → 헤더 검증 실패.
        var bytes = BuildXlsx(new[] { new[] { "20260726", "X", "1", "BC-1", "1" } }, header: false);
        await using var db = tdb.Create();
        var res = await new B2cTestDataService(db, new CapturingOperationLogger()).UploadExcelAsync(new MemoryStream(bytes));
        Assert.Equal("F", res.Status);
    }

    [Fact]
    public async Task Upload_TooManyRows_ReturnsF()
    {
        await using var tdb = new TestDb();
        var rows = Enumerable.Range(1, 1001)
            .Select(i => new[] { "20260726", "BIG", "1", $"R-{i}", "1" });
        var bytes = BuildXlsx(rows);
        await using var db = tdb.Create();
        var res = await new B2cTestDataService(db, new CapturingOperationLogger()).UploadExcelAsync(new MemoryStream(bytes));
        Assert.Equal("F", res.Status);
        await using (var db2 = tdb.Create())
            Assert.Equal(0, db2.Orders.Count());   // 상한 초과 → 커밋 0
    }

    // ════════════════════════════════════════════════════════════════════════
    // ④ 정적 양식 라운드트립 — 커밋된 .xlsx 를 파서에 재투입(헤더 정합·예시행 파싱)
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task StaticTemplate_RoundTrips_ThroughParser()
    {
        var path = FindTemplatePath();
        await using var tdb = new TestDb();
        await using (var db = tdb.Create())
        {
            await using var fs = File.OpenRead(path);
            var res = await new B2cTestDataService(db, new CapturingOperationLogger()).UploadExcelAsync(fs);
            Assert.Equal("S", res.Status);   // 예시행이 파서가 기대하는 헤더/컬럼과 정합
            Assert.True(res.Counts!["ordersCreated"] >= 1, "템플릿 예시행이 파싱되지 않음(헤더 드리프트 의심)");
        }
        await using (var db = tdb.Create())
        {
            // 예시 바코드가 미할당 오더로 생성됨(orderNo==barcode).
            Assert.Contains(db.Orders, o => o.OrderNo == "BC-0001" && o.DestinationId == null);
            Assert.Contains(db.Orders, o => o.OrderNo == "BC-0002");
        }
    }

    // 리포 루트로 walk-up 하여 커밋된 정적 양식 경로 확정(bin 실행 위치 무관).
    private static string FindTemplatePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "frontend", "public", "b2c-order-upload-template.xlsx");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            "정적 양식(b2c-order-upload-template.xlsx)을 찾지 못함 — from " + AppContext.BaseDirectory);
    }
}

// ════════════════════════════════════════════════════════════════════════════
// ③ API 왕복(multipart) — B2bWebApplicationFactory(청정 in-memory SQLite·0 소터) 재사용.
// ════════════════════════════════════════════════════════════════════════════
public class B2cUploadApiTests : IClassFixture<B2bWebApplicationFactory>
{
    private readonly B2bWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public B2cUploadApiTests(B2bWebApplicationFactory factory)
    {
        _factory = factory;
        _client  = factory.CreateClient();
    }

    private sealed record RowErr(int Row, string Message);
    private sealed record UploadResp(string Status, string Message, Dictionary<string, int>? Counts, List<RowErr>? RowErrors);

    private const string XlsxMime = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    private static byte[] Xlsx(IEnumerable<string[]> dataRows, bool header = true)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("업로드");
        int r = 1;
        if (header)
        {
            ws.Cell(1, 1).Value = "작업일자"; ws.Cell(1, 2).Value = "배치명"; ws.Cell(1, 3).Value = "차수";
            ws.Cell(1, 4).Value = "바코드"; ws.Cell(1, 5).Value = "수량";
            r = 2;
        }
        foreach (var row in dataRows)
        {
            for (int c = 0; c < row.Length; c++) ws.Cell(r, c + 1).Value = row[c];
            r++;
        }
        var ms = new MemoryStream();
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
    public async Task Upload_Valid_200S_BatchesReflect_Unassigned()
    {
        var bytes = Xlsx(new[]
        {
            new[] { "20260726", "API-UP-1", "1", "AP-1", "1" },
            new[] { "20260726", "API-UP-1", "1", "AP-2", "2" },
        });
        using var content = FileContent(bytes, "up.xlsx", XlsxMime);
        var resp = await _client.PostAsync("/api/b2c/test-data/upload", content);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<UploadResp>();
        Assert.Equal("S", body!.Status);
        Assert.Equal(2, body.Counts!["ordersCreated"]);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            var orders = db.Orders.Where(o => o.OrderNo.StartsWith("AP-")).ToList();
            Assert.Equal(2, orders.Count);
            Assert.All(orders, o => Assert.Null(o.DestinationId));
        }

        var batches = await _client.GetAsync("/api/b2c/test-data/batches");
        using var doc = System.Text.Json.JsonDocument.Parse(await batches.Content.ReadAsStringAsync());
        var b = doc.RootElement.EnumerateArray().Single(e => e.GetProperty("batchNo").GetString() == "API-UP-1");
        Assert.Equal(2, b.GetProperty("orderTotal").GetInt32());
        Assert.Equal(2, b.GetProperty("orderUnassigned").GetInt32());
    }

    [Fact]
    public async Task Upload_RowError_200F_WithRowErrors_ZeroCreated()
    {
        var bytes = Xlsx(new[]
        {
            new[] { "20260726", "API-UP-2", "1", "ROWOK", "1" },
            new[] { "bad-date", "API-UP-2", "1", "ROWBAD", "1" },
        });
        using var content = FileContent(bytes, "up.xlsx", XlsxMime);
        var resp = await _client.PostAsync("/api/b2c/test-data/upload", content);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);   // 비즈니스 실패도 200
        var body = await resp.Content.ReadFromJsonAsync<UploadResp>();
        Assert.Equal("F", body!.Status);
        Assert.NotNull(body.RowErrors);
        Assert.Single(body.RowErrors!);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        Assert.False(db.Orders.Any(o => o.OrderNo == "ROWOK"));   // 전체 롤백
    }

    [Fact]
    public async Task Upload_XlsExtension_400()
    {
        var bytes = Xlsx(new[] { new[] { "20260726", "X", "1", "BC-1", "1" } });
        using var content = FileContent(bytes, "legacy.xls", "application/vnd.ms-excel");
        var resp = await _client.PostAsync("/api/b2c/test-data/upload", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Upload_EmptyFile_400()
    {
        using var content = FileContent(Array.Empty<byte>(), "empty.xlsx", XlsxMime);
        var resp = await _client.PostAsync("/api/b2c/test-data/upload", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Upload_WrongMime_400()
    {
        using var content = FileContent(new byte[] { 1, 2, 3 }, "data.xlsx", "text/plain");
        var resp = await _client.PostAsync("/api/b2c/test-data/upload", content);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
