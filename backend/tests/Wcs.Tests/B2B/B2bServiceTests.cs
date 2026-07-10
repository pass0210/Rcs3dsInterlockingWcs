using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wcs.Api.B2B;
using Wcs.Data;
using Wcs.Data.B2B;
using Xunit;

namespace Wcs.Tests.B2B;

// ════════════════════════════════════════════════════════════════════════════
// S-B2B-1 서비스 단위 테스트 — WorkService/BoxService 순수 로직(§6) + 실패 message(§4).
// in-memory SQLite(named shared)에 B2B 6테이블 EnsureCreated. test_data 를 직접 시드.
// (B2B-1 은 test_data 생성 미포함 — 외부/실DB가 채움. 여기선 소비 로직만 검증.)
// ════════════════════════════════════════════════════════════════════════════

/// <summary>격리된 in-memory SQLite DB — 테스트당 1개. B2B 스키마 EnsureCreated.</summary>
internal sealed class TestDb : IAsyncDisposable
{
    private readonly SqliteConnection _anchor;
    private readonly string _connStr;

    public TestDb()
    {
        _connStr = $"Data Source=b2b_{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        _anchor = new SqliteConnection(_connStr);
        _anchor.Open();
        using var db = Create();
        db.Database.EnsureCreated();
    }

    public WcsDbContext Create()
    {
        var opts = new DbContextOptionsBuilder<WcsDbContext>()
            .UseSqlite(_connStr)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        return new WcsDbContext(opts);
    }

    public ValueTask DisposeAsync()
    {
        _anchor.Dispose();
        return ValueTask.CompletedTask;
    }
}

public class B2bServiceTests
{
    private static TestData Td(string bizDay, string batch, string barcode, string chute) => new()
    {
        BizDay = bizDay, Batch = batch, Barcode = barcode, ChuteNo = chute,
        CreatedAt = DateTime.Now,
    };

    // ── unprocessed: 그룹핑(Batch→Barcode+ChuteNo, qty=COUNT) + 부수효과 ──────────
    [Fact]
    public async Task Unprocessed_Groups_ByBatchThenBarcodeChute_QtyIsCount()
    {
        await using var tdb = new TestDb();
        using var db = tdb.Create();
        // batch 001: A/001 x3, B/002 x1 ; batch 002: C/003 x2
        db.B2bTestData.AddRange(
            Td("2026-07-08", "001", "A", "001"), Td("2026-07-08", "001", "A", "001"), Td("2026-07-08", "001", "A", "001"),
            Td("2026-07-08", "001", "B", "002"),
            Td("2026-07-08", "002", "C", "003"), Td("2026-07-08", "002", "C", "003"));
        await db.SaveChangesAsync();

        var svc = new WorkService(db);
        var groups = await svc.GetUnprocessedAsync("20260708");

        Assert.Equal(2, groups.Count);
        var g1 = groups.Single(g => g.Batch == "001");
        Assert.Equal("2026-07-08", g1.BizDay);   // 정규화 형태
        Assert.Equal(2, g1.Items.Count);
        Assert.Equal(new UnprocessedItem("A", "001", 3), g1.Items[0]);   // chute 001 먼저
        Assert.Equal(new UnprocessedItem("B", "002", 1), g1.Items[1]);
        var g2 = groups.Single(g => g.Batch == "002");
        Assert.Equal(new UnprocessedItem("C", "003", 2), g2.Items.Single());
    }

    // ── unprocessed 부수효과: 2회차 빈 배열(receive_time 마킹) ──────────────────────
    [Fact]
    public async Task Unprocessed_SecondCall_ReturnsEmpty_AfterReceiveTimeMarked()
    {
        await using var tdb = new TestDb();
        using var db = tdb.Create();
        db.B2bTestData.Add(Td("2026-07-08", "001", "A", "001"));
        await db.SaveChangesAsync();

        var svc = new WorkService(db);
        var first = await svc.GetUnprocessedAsync("2026-07-08");
        Assert.Single(first);

        // 부수효과 확인: receive_time 마킹됨
        using (var verify = tdb.Create())
            Assert.All(verify.B2bTestData.ToList(), t => Assert.NotNull(t.ReceiveTime));

        var second = await svc.GetUnprocessedAsync("2026-07-08");
        Assert.Empty(second);   // 2회차 빈 배열(자동생성 트리거 없음)
    }

    // ── unprocessed 0건 → 빈 배열(F 아님) ────────────────────────────────────────
    [Fact]
    public async Task Unprocessed_ZeroRows_ReturnsEmptyArray_NoTrigger()
    {
        await using var tdb = new TestDb();
        using var db = tdb.Create();
        var svc = new WorkService(db);
        var groups = await svc.GetUnprocessedAsync("2026-07-08");
        Assert.Empty(groups);
    }

    // ── input: 가용<qty 전량거부(#2) — 부분 처리 없음 ─────────────────────────────
    [Fact]
    public async Task Input_AvailableLessThanQty_RejectsAll_NoInsert()
    {
        await using var tdb = new TestDb();
        using var db = tdb.Create();
        db.B2bTestData.AddRange(Td("2026-07-08", "001", "A", "001"), Td("2026-07-08", "001", "A", "001"));
        await db.SaveChangesAsync();

        var svc = new WorkService(db);
        var res = await svc.ProcessInputAsync(new InputRequest
        {
            BizDay = "20260708", Batch = "001", Barcode = "A", ChuteNo = "001",
            InductionNo = 1, PId = 12345, Status = "OK", InTime = "2026-07-08 10:00:00", Qty = 3,
        });

        Assert.Equal("F", res.Status);
        Assert.Equal("Not enough unprocessed rows: requested 3, available 2.", res.Message);   // #2 verbatim
        using var verify = tdb.Create();
        Assert.Equal(0, verify.B2bTestLogs.Count());   // 전량거부 — INSERT 0
    }

    // ── input: happy — qty개 INPUT 로그 + pId/inductionNo 미검증 그대로 저장 ─────────
    [Fact]
    public async Task Input_HappyPath_InsertsQtyLogs_PidStoredUnverified()
    {
        await using var tdb = new TestDb();
        using var db = tdb.Create();
        db.B2bTestData.AddRange(Td("2026-07-08", "001", "A", "001"), Td("2026-07-08", "001", "A", "001"));
        await db.SaveChangesAsync();

        var svc = new WorkService(db);
        var res = await svc.ProcessInputAsync(new InputRequest
        {
            BizDay = "2026-07-08", Batch = "001", Barcode = "A", ChuteNo = "001",
            InductionNo = 7, PId = 2147480000, Status = "OK", InTime = "2026-07-08 10:00:00", Qty = 2,
        });

        Assert.Equal("S", res.Status);
        Assert.Equal("Success", res.Message);
        using var verify = tdb.Create();
        var logs = verify.B2bTestLogs.Where(l => l.LogType == "INPUT").ToList();
        Assert.Equal(2, logs.Count);
        Assert.All(logs, l => Assert.Equal("2147480000", l.Pid));   // pId 미검증 그대로 저장
        Assert.All(logs, l => Assert.Equal("7", l.EquipmentNo));    // inductionNo → equipment_no
    }

    // ── input: barcode 미등록 → F(#1) ────────────────────────────────────────────
    [Fact]
    public async Task Input_UnknownBarcode_FailMessage1()
    {
        await using var tdb = new TestDb();
        using var db = tdb.Create();
        var svc = new WorkService(db);
        var res = await svc.ProcessInputAsync(new InputRequest
        {
            BizDay = "2026-07-08", Batch = "001", Barcode = "NOPE", ChuteNo = "001",
            InductionNo = 1, PId = 1, Status = "OK", InTime = "2026-07-08 10:00:00", Qty = 1,
        });
        Assert.Equal("F", res.Status);
        Assert.Equal("Barcode not found, or bizDay/batch does not match the registered data.", res.Message);
    }

    // ── classification: chute mismatch(#3) ──────────────────────────────────────
    [Fact]
    public async Task Classification_ChuteMismatch_FailMessage3_WithHints()
    {
        await using var tdb = new TestDb();
        using var db = tdb.Create();
        db.B2bTestData.AddRange(Td("2026-07-08", "001", "A", "001"), Td("2026-07-08", "001", "A", "002"));
        await db.SaveChangesAsync();

        var svc = new WorkService(db);
        var res = await svc.ProcessClassificationAsync(new ClassificationRequest
        {
            BizDay = "2026-07-08", Batch = "001", Barcode = "A", ChuteNo = "9",  // 9→"009" 불일치
            PId = 1, Status = "OK", SortTime = "2026-07-08 10:00:00", Qty = 1,
        });
        Assert.Equal("F", res.Status);
        Assert.Equal("Chute mismatch: barcode A expected chute(s) [001, 002], received 009.", res.Message);
    }

    // ── classification: chute 매칭 성공 → SORT 로그(equipment_no=정규화 chute) ────────
    [Fact]
    public async Task Classification_MatchingChute_Success_SortLogWithNormalizedChute()
    {
        await using var tdb = new TestDb();
        using var db = tdb.Create();
        db.B2bTestData.Add(Td("2026-07-08", "001", "A", "003"));
        await db.SaveChangesAsync();

        var svc = new WorkService(db);
        var res = await svc.ProcessClassificationAsync(new ClassificationRequest
        {
            BizDay = "2026-07-08", Batch = "001", Barcode = "A", ChuteNo = "3",  // 3→"003" 일치
            PId = 55, Status = "OK", SortTime = "2026-07-08 10:00:00", Qty = 1,
        });
        Assert.Equal("S", res.Status);
        using var verify = tdb.Create();
        var log = verify.B2bTestLogs.Single(l => l.LogType == "SORT");
        Assert.Equal("003", log.EquipmentNo);
        Assert.Equal("55", log.Pid);
    }

    // ── classification: 이미 전부 분류됨(#4) ──────────────────────────────────────
    [Fact]
    public async Task Classification_AlreadyFullyClassified_FailMessage4()
    {
        await using var tdb = new TestDb();
        using var db = tdb.Create();
        db.B2bTestData.Add(Td("2026-07-08", "001", "A", "001"));
        await db.SaveChangesAsync();

        var svc = new WorkService(db);
        var first = await svc.ProcessClassificationAsync(new ClassificationRequest
        {
            BizDay = "2026-07-08", Batch = "001", Barcode = "A", ChuteNo = "001",
            PId = 1, Status = "OK", SortTime = "2026-07-08 10:00:00", Qty = 1,
        });
        Assert.Equal("S", first.Status);

        var second = await svc.ProcessClassificationAsync(new ClassificationRequest
        {
            BizDay = "2026-07-08", Batch = "001", Barcode = "A", ChuteNo = "001",
            PId = 2, Status = "OK", SortTime = "2026-07-08 10:00:00", Qty = 1,
        });
        Assert.Equal("F", second.Status);
        Assert.Equal("Barcode A in chute 001 has already been fully classified.", second.Message);
    }

    // ── results: 사전 존재검증 전체거부(#6) — 미등록 barcode 하나라도 → INSERT 0 ──────
    [Fact]
    public async Task Results_OneUnregisteredBarcode_RejectsAll_NoInsert()
    {
        await using var tdb = new TestDb();
        using var db = tdb.Create();
        db.B2bTestData.Add(Td("2026-07-08", "001", "A", "001"));   // A 등록, B 미등록
        await db.SaveChangesAsync();

        var svc = new WorkService(db);
        var res = await svc.ProcessResultsAsync(new List<ResultRequestGroup>
        {
            new() { BizDay = "2026-07-08", Batch = "001", Items = new()
            {
                new ResultItem { Barcode = "A", ChuteNo = "001", Qty = 1 },
                new ResultItem { Barcode = "B", ChuteNo = "002", Qty = 1 },
            } }
        });
        Assert.Equal("F", res.Status);
        Assert.Equal("Barcode 'B' not found, or bizDay/batch does not match the registered data.", res.Message);
        using var verify = tdb.Create();
        Assert.Equal(0, verify.B2bWorkResults.Count());   // 전체거부 — 부분 INSERT 0
    }

    // ── results: happy — item.qty 만큼 work_result 생성 + chuteNo 미검증 정규화 ────────
    [Fact]
    public async Task Results_HappyPath_ExpandsByQty_ChuteNormalizedNotValidated()
    {
        await using var tdb = new TestDb();
        using var db = tdb.Create();
        db.B2bTestData.Add(Td("2026-07-08", "001", "A", "001"));
        await db.SaveChangesAsync();

        var svc = new WorkService(db);
        // chuteNo="77" 은 등록 슈트(001)와 다르지만 results 는 chuteNo 미검증 → 그대로 저장(정규화만)
        var res = await svc.ProcessResultsAsync(new List<ResultRequestGroup>
        {
            new() { BizDay = "20260708", Batch = "001", Items = new()
            {
                new ResultItem { Barcode = "A", ChuteNo = "77", Qty = 3 },
            } }
        });
        Assert.Equal("S", res.Status);
        using var verify = tdb.Create();
        var rows = verify.B2bWorkResults.ToList();
        Assert.Equal(3, rows.Count);                       // qty=3 → 3행
        Assert.All(rows, r => Assert.Equal("077", r.ChuteNo));  // 미검증·정규화("77"→"077")
    }

    // ── results: null/빈 배열(#5), 유효 item 0(#7) ─────────────────────────────────
    [Fact]
    public async Task Results_NullOrEmpty_FailMessage5()
    {
        await using var tdb = new TestDb();
        using var db = tdb.Create();
        var svc = new WorkService(db);
        Assert.Equal("No data to process.", (await svc.ProcessResultsAsync(null)).Message);
        Assert.Equal("No data to process.", (await svc.ProcessResultsAsync(new List<ResultRequestGroup>())).Message);
    }

    [Fact]
    public async Task Results_AllEmptyBarcodes_NoValidData_Message7()
    {
        await using var tdb = new TestDb();
        using var db = tdb.Create();
        var svc = new WorkService(db);
        var res = await svc.ProcessResultsAsync(new List<ResultRequestGroup>
        {
            new() { BizDay = "2026-07-08", Batch = "001", Items = new()
            {
                new ResultItem { Barcode = "", ChuteNo = "001", Qty = 1 },   // 빈 barcode skip
            } }
        });
        Assert.Equal("F", res.Status);
        Assert.Equal("No valid data to process.", res.Message);
    }

    // ── box: 중복거부(#8) — barcode 미검증 ────────────────────────────────────────
    [Fact]
    public async Task Box_DuplicateBizDayBatchBoxNo_FailMessage8()
    {
        await using var tdb = new TestDb();
        using var db = tdb.Create();
        var svc = new BoxService(db);
        var req = new BoxRequest
        {
            BizDay = "2026-07-08", Batch = "001", BoxNo = "BOX-1", ChuteNo = "3",
            Items = new() { new BoxItemDto { Barcode = "UNREG-BC", Qty = 2 } },   // barcode 미검증
        };
        Assert.Equal("S", (await svc.ProcessBoxAsync(req)).Status);   // 1차 성공(barcode 존재검증 없음)

        var dup = await svc.ProcessBoxAsync(new BoxRequest
        {
            BizDay = "20260708", Batch = "001", BoxNo = "BOX-1", ChuteNo = "3",  // 정규화 후 동일 키
            Items = new() { new BoxItemDto { Barcode = "X", Qty = 1 } },
        });
        Assert.Equal("F", dup.Status);
        Assert.Equal("Box already exists for the given bizDay/batch/boxNo.", dup.Message);

        using var verify = tdb.Create();
        Assert.Equal(1, verify.B2bBoxes.Count());   // 재전송 INSERT 0
        var box = verify.B2bBoxes.Include(b => b.Items).Single();
        Assert.Equal("003", box.ChuteNo);            // chuteNo 3자리 정규화
        Assert.Single(box.Items);
    }

    // ── box: 빈 barcode item 필터 ─────────────────────────────────────────────────
    [Fact]
    public async Task Box_FiltersEmptyBarcodeItems()
    {
        await using var tdb = new TestDb();
        using var db = tdb.Create();
        var svc = new BoxService(db);
        var res = await svc.ProcessBoxAsync(new BoxRequest
        {
            BizDay = "2026-07-08", Batch = "001", BoxNo = "BOX-2", ChuteNo = "003",
            Items = new()
            {
                new BoxItemDto { Barcode = "A", Qty = 1 },
                new BoxItemDto { Barcode = "",  Qty = 1 },   // 빈 barcode 필터
                new BoxItemDto { Barcode = null, Qty = 1 },  // null 필터
            },
        });
        Assert.Equal("S", res.Status);
        using var verify = tdb.Create();
        var box = verify.B2bBoxes.Include(b => b.Items).Single();
        Assert.Single(box.Items);
        Assert.Equal("A", box.Items.First().Barcode);
    }

    // ── AppUtils.NormalizeBizDay: 비존재 날짜 → ArgumentException(#17) ──────────────
    [Fact]
    public void NormalizeBizDay_InvalidCalendarDate_Throws()
    {
        Assert.Equal("2026-03-27", AppUtils.NormalizeBizDay("20260327"));
        Assert.Equal("2026-03-27", AppUtils.NormalizeBizDay("2026-03-27"));
        Assert.Equal("", AppUtils.NormalizeBizDay(""));   // 빈 문자열 그대로
        var ex = Assert.Throws<ArgumentException>(() => AppUtils.NormalizeBizDay("20261332"));
        Assert.Equal("Invalid date: 20261332", ex.Message);   // #17 verbatim
    }
}
