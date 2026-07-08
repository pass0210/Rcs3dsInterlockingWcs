using ClosedXML.Excel;
using Wcs.Api.B2B;
using Wcs.Data.B2B;
using Xunit;

namespace Wcs.Tests.B2B;

// ════════════════════════════════════════════════════════════════════════════
// S-B2B-2a 서비스 단위 테스트 — TestDataService(§2 알고리즘·§3 아카이브).
// TestDb(in-memory SQLite named shared, B2B 스키마 EnsureCreated) 재사용(B2bServiceTests 와 동일).
//
// ★ 아카이브 핵심(계약 검증 3): reset/delete 후 test_log·work_result 가 DB 에서 사라지지 않고
//   archived_at 만 세팅됨(하드삭제 0) + 스코프 한정((BizDay,Batch,Barcode)) — 배치 밖 미영향.
// ════════════════════════════════════════════════════════════════════════════
public class TestDataServiceTests
{
    private static TestData Td(string bizDay, string batch, string barcode, string chute,
                               DateTime? receive = null) => new()
    {
        BizDay = bizDay, Batch = batch, Barcode = barcode, ChuteNo = chute,
        ReceiveTime = receive, CreatedAt = DateTime.Now,
    };

    private static TestLog Log(string type, string bizDay, string batch, string barcode,
                               long? testDataId, string status = "OK") => new()
    {
        LogType = type, BizDay = bizDay, Batch = batch, Barcode = barcode,
        TestDataId = testDataId, Status = status, LogTime = DateTime.Now, CreatedAt = DateTime.Now,
    };

    // xlsx 스트림 빌더 — 셀은 전부 문자열로(테스트 결정성).
    private static Stream MakeXlsx(Action<IXLWorksheet> fill)
    {
        var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Sheet1");
        fill(ws);
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return ms;
    }

    // ════════════════════════════════════════════════════════════════════════
    // §2.1 수동 라운드로빈 생성
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Generate_RoundRobin_DistributesChutes_And_NormalizesBizDay()
    {
        await using var tdb = new TestDb();
        using var db = tdb.Create();
        var svc = new TestDataService(db);

        // 슈트 "1-2"(→[1,2]) · 개수 5 → 라운드로빈 001,002,001,002,001 = 001×3, 002×2.
        var res = await svc.GenerateAsync(new GenerateRequest
        {
            BizDay = "20260708", Batch = "001", ChuteNos = "1-2", BarcodeCount = 5,
        });
        Assert.Equal("S", res.Status);

        using var verify = tdb.Create();
        var rows = verify.B2bTestData.ToList();
        Assert.Equal(5, rows.Count);
        Assert.All(rows, r => Assert.Equal("2026-07-08", r.BizDay));       // 정규화 저장
        Assert.All(rows, r => Assert.StartsWith("BC", r.Barcode));        // GenerateBarcode 채번
        Assert.Equal(3, rows.Count(r => r.ChuteNo == "001"));             // 라운드로빈 001×3
        Assert.Equal(2, rows.Count(r => r.ChuteNo == "002"));             // 002×2
    }

    [Fact]
    public async Task Generate_RangeAndSingles_Parsed_Deduped_Sorted()
    {
        await using var tdb = new TestDb();
        using var db = tdb.Create();
        var svc = new TestDataService(db);

        // "1-3, 5, 5, 2" → {1,2,3,5} 정렬·중복제거. 개수 4 → 001,002,003,005 각 1.
        var res = await svc.GenerateAsync(new GenerateRequest
        {
            BizDay = "2026-07-08", Batch = "B", ChuteNos = "1-3, 5, 5, 2", BarcodeCount = 4,
        });
        Assert.Equal("S", res.Status);

        using var verify = tdb.Create();
        var chutes = verify.B2bTestData.Select(r => r.ChuteNo).OrderBy(c => c).ToList();
        Assert.Equal(new[] { "001", "002", "003", "005" }, chutes);
    }

    [Fact]
    public async Task Generate_EmptyChutes_Fail_InvalidChuteNumbers()
    {
        await using var tdb = new TestDb();
        using var db = tdb.Create();
        var svc = new TestDataService(db);
        var res = await svc.GenerateAsync(new GenerateRequest
        {
            BizDay = "20260708", Batch = "001", ChuteNos = " , ", BarcodeCount = 3,
        });
        Assert.Equal("F", res.Status);
        Assert.Equal("Invalid chute numbers", res.Message);
        using var verify = tdb.Create();
        Assert.Equal(0, verify.B2bTestData.Count());   // 생성 0
    }

    [Fact]
    public async Task Generate_ZeroCount_Fail_InvalidBarcodeCount()
    {
        await using var tdb = new TestDb();
        using var db = tdb.Create();
        var svc = new TestDataService(db);
        var res = await svc.GenerateAsync(new GenerateRequest
        {
            BizDay = "20260708", Batch = "001", ChuteNos = "1", BarcodeCount = 0,
        });
        Assert.Equal("F", res.Status);
        Assert.Equal("Invalid barcode count", res.Message);
    }

    // ════════════════════════════════════════════════════════════════════════
    // §2.2 엑셀 업로드 — 신/구양식·헤더 자동감지·검증
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Upload_NewFormat5Col_WithHeader_ParsesBarcode2AndChute()
    {
        await using var tdb = new TestDb();
        using var db = tdb.Create();
        var svc = new TestDataService(db);

        // 헤더 있음(1행 1열 "BizDay") + 5컬럼 신양식.
        await using var xlsx = MakeXlsx(ws =>
        {
            ws.Cell(1, 1).Value = "BizDay"; ws.Cell(1, 2).Value = "Batch"; ws.Cell(1, 3).Value = "Barcode";
            ws.Cell(1, 4).Value = "Barcode2"; ws.Cell(1, 5).Value = "ChuteNo";
            ws.Cell(2, 1).Value = "20260708"; ws.Cell(2, 2).Value = "001"; ws.Cell(2, 3).Value = "BC-1";
            ws.Cell(2, 4).Value = "BC-1B"; ws.Cell(2, 5).Value = "7";
        });
        var res = await svc.UploadExcelAsync(xlsx);
        Assert.Equal("S", res.Status);
        Assert.Equal("1건 업로드 완료", res.Message);

        using var verify = tdb.Create();
        var row = verify.B2bTestData.Single();
        Assert.Equal("2026-07-08", row.BizDay);   // 정규화
        Assert.Equal("BC-1", row.Barcode);
        Assert.Equal("BC-1B", row.Barcode2);      // 신양식 col4 = barcode2
        Assert.Equal("007", row.ChuteNo);         // col5 = chute, D3 정규화
    }

    [Fact]
    public async Task Upload_OldFormat4Col_Headerless_ChuteFromCol4_NoBarcode2()
    {
        await using var tdb = new TestDb();
        using var db = tdb.Create();
        var svc = new TestDataService(db);

        // 헤더 없음(1행 1열 "20260708" 날짜형) + 4컬럼 구양식(col5 빔).
        await using var xlsx = MakeXlsx(ws =>
        {
            ws.Cell(1, 1).Value = "20260708"; ws.Cell(1, 2).Value = "002"; ws.Cell(1, 3).Value = "BC-OLD";
            ws.Cell(1, 4).Value = "9";
        });
        var res = await svc.UploadExcelAsync(xlsx);
        Assert.Equal("S", res.Status);

        using var verify = tdb.Create();
        var row = verify.B2bTestData.Single();
        Assert.Equal("2026-07-08", row.BizDay);
        Assert.Equal("BC-OLD", row.Barcode);
        Assert.Null(row.Barcode2);        // 구양식 barcode2 없음
        Assert.Equal("009", row.ChuteNo); // col4 = chute
    }

    [Fact]
    public async Task Upload_EmptyBarcodeRowsSkipped_NoValidData()
    {
        await using var tdb = new TestDb();
        using var db = tdb.Create();
        var svc = new TestDataService(db);

        // 헤더 + barcode(3열) 빈 행만 → 유효행 0.
        await using var xlsx = MakeXlsx(ws =>
        {
            ws.Cell(1, 1).Value = "BizDay"; ws.Cell(1, 2).Value = "Batch"; ws.Cell(1, 3).Value = "Barcode";
            ws.Cell(2, 1).Value = "20260708"; ws.Cell(2, 2).Value = "001"; ws.Cell(2, 3).Value = "";
        });
        var res = await svc.UploadExcelAsync(xlsx);
        Assert.Equal("F", res.Status);
        Assert.Equal("No valid data to upload.", res.Message);
    }

    [Fact]
    public async Task Upload_EmptyWorkbook_ExcelNoData()
    {
        await using var tdb = new TestDb();
        using var db = tdb.Create();
        var svc = new TestDataService(db);
        await using var xlsx = MakeXlsx(_ => { });   // 빈 시트
        var res = await svc.UploadExcelAsync(xlsx);
        Assert.Equal("F", res.Status);
        Assert.Equal("Excel file contains no data.", res.Message);
    }

    // zip-bomb/대용량 방어(코드리뷰 후속 #2): 사용 범위 열이 상한(64) 초과 → 조기 F(행 순회 전 차단).
    [Fact]
    public async Task Upload_ExceedsColumnLimit_TooLarge()
    {
        await using var tdb = new TestDb();
        using var db = tdb.Create();
        var svc = new TestDataService(db);

        // 65개 열 사용(UploadMaxColumns=64 초과) → 조기 차단(파싱 루프 진입 전).
        await using var xlsx = MakeXlsx(ws =>
        {
            for (var c = 1; c <= 65; c++)
                ws.Cell(1, c).Value = $"h{c}";
        });
        var res = await svc.UploadExcelAsync(xlsx);
        Assert.Equal("F", res.Status);
        Assert.Equal("Excel file is too large.", res.Message);
        using var verify = tdb.Create();
        Assert.Equal(0, verify.B2bTestData.Count());   // 조기 차단 — INSERT 0
    }

    // ════════════════════════════════════════════════════════════════════════
    // §2.3 요약 · §2.4 상세
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Summary_GroupsByBatch_CountAndMaxReceiveTime_OrderedDesc()
    {
        await using var tdb = new TestDb();
        using var db = tdb.Create();
        var t1 = new DateTime(2026, 7, 8, 9, 0, 0);
        var t2 = new DateTime(2026, 7, 8, 10, 0, 0);
        db.B2bTestData.AddRange(
            Td("2026-07-08", "001", "A", "001", t1),
            Td("2026-07-08", "001", "B", "002", t2),   // batch 001: 2건, MAX=t2
            Td("2026-07-08", "002", "C", "003", null));// batch 002: 1건, MAX=null
        await db.SaveChangesAsync();

        var svc = new TestDataService(db);
        var rows = await svc.GetSummaryAsync("20260708");

        Assert.Equal(2, rows.Count);
        Assert.Equal("002", rows[0].Batch);   // Batch desc → 002 먼저
        Assert.Equal("001", rows[1].Batch);
        var b001 = rows.Single(r => r.Batch == "001");
        Assert.Equal(2, b001.Count);
        Assert.Equal(t2, b001.ReceiveTime);    // MAX(receive_time)
    }

    [Fact]
    public async Task Detail_SortsAndMapsInputSortLogs()
    {
        await using var tdb = new TestDb();
        using var db = tdb.Create();
        db.B2bTestData.AddRange(
            Td("2026-07-08", "001", "BC-B", "010"),
            Td("2026-07-08", "001", "BC-A", "002"));   // 정렬 후 BC-A 먼저
        await db.SaveChangesAsync();
        var aId = db.B2bTestData.Single(d => d.Barcode == "BC-A").Id;
        db.B2bTestLogs.AddRange(
            Log("INPUT", "2026-07-08", "001", "BC-A", aId, "OK"),
            Log("SORT", "2026-07-08", "001", "BC-A", aId, "NG"));
        await db.SaveChangesAsync();

        var svc = new TestDataService(db);
        var rows = await svc.GetDetailAsync("2026-07-08", "001");

        Assert.Equal(2, rows.Count);
        Assert.Equal("BC-A", rows[0].Barcode);         // Barcode 정렬
        Assert.Equal("OK", rows[0].InputStatus);       // INPUT 매핑
        Assert.Equal("NG", rows[0].SortStatus);        // SORT 매핑
        Assert.Null(rows[1].InputStatus);              // BC-B 로그 없음
    }

    // ════════════════════════════════════════════════════════════════════════
    // §3 ★ 아카이브 핵심 — reset/delete 하드삭제 0 + 스코프 한정
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>reset: test_data.receive_time=null(행 유지) + 연관 로그/결과 archived_at 세팅(하드삭제 0).</summary>
    [Fact]
    public async Task Reset_ArchivesAssociatedLogsAndResults_NoHardDelete()
    {
        await using var tdb = new TestDb();
        using (var db = tdb.Create())
        {
            db.B2bTestData.Add(Td("2026-07-08", "001", "BC-1", "001", new DateTime(2026, 7, 8, 9, 0, 0)));
            await db.SaveChangesAsync();
            var id = db.B2bTestData.Single().Id;
            db.B2bTestLogs.AddRange(
                Log("INPUT", "2026-07-08", "001", "BC-1", id),
                Log("SORT", "2026-07-08", "001", "BC-1", null));   // barcode-폴백(TestDataId null)
            db.B2bWorkResults.Add(new WorkResult
            {
                BizDay = "2026-07-08", Batch = "001", Barcode = "BC-1", ChuteNo = "001", CreatedAt = DateTime.Now,
            });
            await db.SaveChangesAsync();

            var svc = new TestDataService(db);
            var res = await svc.ResetReceiveTimeAsync(new[] { id });
            Assert.Equal("S", res.Status);
        }

        using (var verify = tdb.Create())
        {
            // (a) test_data 행 유지 + receive_time 초기화
            var td = verify.B2bTestData.Single();
            Assert.Null(td.ReceiveTime);
            // (b) 로그/결과 DB 잔존(하드삭제 0) + archived_at 세팅
            Assert.Equal(2, verify.B2bTestLogs.Count());       // COUNT 불변(하드삭제 0)
            Assert.All(verify.B2bTestLogs.ToList(), l => Assert.NotNull(l.ArchivedAt));
            Assert.Equal(1, verify.B2bWorkResults.Count());
            Assert.NotNull(verify.B2bWorkResults.Single().ArchivedAt);
        }

        using (var db2 = tdb.Create())
        {
            var svc = new TestDataService(db2);
            // (c) active 필터 → 아카이브 로그 미노출(InputStatus/SortStatus null)
            var active = await svc.GetDetailAsync("2026-07-08", "001", ArchiveFilter.Active);
            Assert.Single(active);
            Assert.Null(active[0].InputStatus);
            Assert.Null(active[0].SortStatus);
            // (d) archivedOnly 필터 → 아카이브 로그 노출
            var arch = await svc.GetDetailAsync("2026-07-08", "001", ArchiveFilter.ArchivedOnly);
            Assert.Single(arch);
            Assert.Equal("OK", arch[0].InputStatus);
            Assert.NotNull(arch[0].SortStatus);
        }
    }

    /// <summary>delete: test_data 하드삭제 + 연관 로그/결과는 삭제되지 않고 archived.</summary>
    [Fact]
    public async Task Delete_HardDeletesTestData_ButArchivesLogsAndResults()
    {
        await using var tdb = new TestDb();
        long id;
        using (var db = tdb.Create())
        {
            db.B2bTestData.Add(Td("2026-07-08", "001", "BC-D", "001"));
            await db.SaveChangesAsync();
            id = db.B2bTestData.Single().Id;
            db.B2bTestLogs.Add(Log("INPUT", "2026-07-08", "001", "BC-D", id));
            db.B2bWorkResults.Add(new WorkResult
            {
                BizDay = "2026-07-08", Batch = "001", Barcode = "BC-D", ChuteNo = "001", CreatedAt = DateTime.Now,
            });
            await db.SaveChangesAsync();

            var svc = new TestDataService(db);
            Assert.Equal("S", (await svc.DeleteAsync(new[] { id })).Status);
        }

        using var verify = tdb.Create();
        Assert.Equal(0, verify.B2bTestData.Count());              // test_data 하드삭제(등록 원장)
        Assert.Equal(1, verify.B2bTestLogs.Count());              // 로그 잔존(하드삭제 0)
        Assert.NotNull(verify.B2bTestLogs.Single().ArchivedAt);   // archived
        Assert.Equal(1, verify.B2bWorkResults.Count());           // 결과 잔존
        Assert.NotNull(verify.B2bWorkResults.Single().ArchivedAt);
    }

    /// <summary>스코프 한정(원본 결함 교정): 다른 배치의 동일 barcode 로그/결과는 미영향.</summary>
    [Fact]
    public async Task Delete_ScopeLimited_OtherBatchSameBarcode_Untouched()
    {
        await using var tdb = new TestDb();
        long id001;
        using (var db = tdb.Create())
        {
            db.B2bTestData.AddRange(
                Td("2026-07-08", "001", "BC-X", "001"),
                Td("2026-07-08", "002", "BC-X", "001"));   // 다른 batch, 동일 barcode
            await db.SaveChangesAsync();
            id001 = db.B2bTestData.Single(d => d.Batch == "001").Id;
            var id002 = db.B2bTestData.Single(d => d.Batch == "002").Id;
            db.B2bTestLogs.AddRange(
                Log("INPUT", "2026-07-08", "001", "BC-X", id001),
                Log("INPUT", "2026-07-08", "002", "BC-X", id002));   // batch 002 로그
            db.B2bWorkResults.AddRange(
                new WorkResult { BizDay = "2026-07-08", Batch = "001", Barcode = "BC-X", CreatedAt = DateTime.Now },
                new WorkResult { BizDay = "2026-07-08", Batch = "002", Barcode = "BC-X", CreatedAt = DateTime.Now });
            await db.SaveChangesAsync();

            var svc = new TestDataService(db);
            Assert.Equal("S", (await svc.DeleteAsync(new[] { id001 })).Status);   // batch 001만 삭제
        }

        using var verify = tdb.Create();
        // batch 001 로그·결과 → archived. batch 002 → 미영향(archived_at null).
        var log001 = verify.B2bTestLogs.Single(l => l.Batch == "001");
        var log002 = verify.B2bTestLogs.Single(l => l.Batch == "002");
        Assert.NotNull(log001.ArchivedAt);
        Assert.Null(log002.ArchivedAt);   // ★ 배치 밖 미영향(원본 barcode-only 결함 미재현)
        var wr001 = verify.B2bWorkResults.Single(w => w.Batch == "001");
        var wr002 = verify.B2bWorkResults.Single(w => w.Batch == "002");
        Assert.NotNull(wr001.ArchivedAt);
        Assert.Null(wr002.ArchivedAt);
    }
}
