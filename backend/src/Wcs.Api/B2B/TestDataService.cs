using System.Globalization;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Wcs.Data;
using Wcs.Data.B2B;

namespace Wcs.Api.B2B;

// ════════════════════════════════════════════════════════════════════════════
// TestDataService — 프론트 전용 test-data 관리(수동생성·엑셀업로드·조회·초기화·삭제).
// 알고리즘 정본: docs/B2B-DATAGEN.md §2·§3. 실패 message: §1.2·§2(FailMessages).
//
// ★ 기록 아카이브(S-B2B-2a 핵심 · 사용자·사수 확정 2026-07-08):
//   reset/delete 시 연관 test_log·work_result 를 **하드삭제 금지 → archived_at 소프트삭제(보존)**.
//   원본의 barcode 키 광범위 하드 연관삭제를 이식하지 않고 **(BizDay,Batch,Barcode) 집합**(+test_log는
//   TestDataId)으로 스코프를 한정(배치 밖 미영향). RemoveRange 는 test_data(등록 원장)에만(delete).
//
// ⚠ created_at/archived_at 은 B2B 로컬타임(DateTime.Now) — 원본·B2B-1 호환.
// ⚠ WcsDbContext(Scoped) 주입 — HTTP 핸들러 스코프와 동일 수명.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>아카이브 조회 필터(§3.4) — detail 로그 매핑 대상 결정.</summary>
public enum ArchiveFilter
{
    /// <summary>기본 — archived_at == null 로그만 매핑(아카이브분 제외).</summary>
    Active,
    /// <summary>보관 포함 — 활성 + 아카이브 로그 모두 매핑.</summary>
    All,
    /// <summary>보관만 — archived_at != null 로그만 매핑.</summary>
    ArchivedOnly,
}

public interface ITestDataService
{
    /// <summary>수동 라운드로빈 생성(§2.1). 슈트 0개/개수≤0 → F.</summary>
    Task<B2BApiResponse> GenerateAsync(GenerateRequest req, CancellationToken ct = default);

    /// <summary>엑셀 업로드 파싱(§2.2) — 신/구양식 자동판별. 파일 검증(400)은 컨트롤러가 선행.</summary>
    Task<B2BApiResponse> UploadExcelAsync(Stream excelStream, CancellationToken ct = default);

    /// <summary>배치 요약(§2.3) — (bizDay,batch)별 건수·MAX(receive_time), bizDay/batch desc.</summary>
    Task<List<TestDataSummaryRow>> GetSummaryAsync(string? bizDay, CancellationToken ct = default);

    /// <summary>상세(§2.4) — 로그 조인 + 아카이브 필터. bizDay 비존재 날짜 → ArgumentException(#17).</summary>
    Task<List<TestDataDetailRow>> GetDetailAsync(
        string bizDay, string batch, ArchiveFilter archived = ArchiveFilter.Active, CancellationToken ct = default);

    /// <summary>수신시간 초기화(§3.3) — test_data.receive_time=null(행 유지) + 연관 로그/결과 아카이브.</summary>
    Task<B2BApiResponse> ResetReceiveTimeAsync(IReadOnlyList<long>? ids, CancellationToken ct = default);

    /// <summary>선택 삭제(§3.3) — test_data 하드삭제(등록 원장) + 연관 로그/결과 아카이브(하드삭제 0).</summary>
    Task<B2BApiResponse> DeleteAsync(IReadOnlyList<long>? ids, CancellationToken ct = default);
}

public sealed class TestDataService : ITestDataService
{
    private readonly WcsDbContext _db;

    public TestDataService(WcsDbContext db) => _db = db;

    /// <summary>문자열 archived 파라미터 → ArchiveFilter(기본 Active). 대소문자 무시.</summary>
    public static ArchiveFilter ParseArchiveFilter(string? s) =>
        (s?.Trim().ToLowerInvariant()) switch
        {
            "all"          => ArchiveFilter.All,
            "archivedonly" => ArchiveFilter.ArchivedOnly,
            _              => ArchiveFilter.Active,   // "active"·null·미인식 → 기본
        };

    // ── §2.1 수동 라운드로빈 생성 ──────────────────────────────────────────────
    public async Task<B2BApiResponse> GenerateAsync(GenerateRequest req, CancellationToken ct = default)
    {
        var chuteNos = AppUtils.ParseChuteNos(req.ChuteNos);
        if (chuteNos.Count == 0)
            return B2BApiResponse.Fail(FailMessages.InvalidChuteNumbers);   // §2.1
        if (req.BarcodeCount <= 0)
            return B2BApiResponse.Fail(FailMessages.InvalidBarcodeCount);   // §2.1

        // bizDay 정규화 필수 — raw 8자리 저장 시 unprocessed 조회 매칭 실패 회귀(§2.1).
        var bizDay = AppUtils.NormalizeBizDay(req.BizDay);
        var now = DateTime.Now;

        var rows = new List<TestData>(req.BarcodeCount);
        for (var i = 0; i < req.BarcodeCount; i++)
        {
            var chute = chuteNos[i % chuteNos.Count];   // 라운드로빈 배분
            rows.Add(new TestData
            {
                BizDay    = bizDay,
                Batch     = req.Batch,
                Barcode   = AppUtils.GenerateBarcode(),
                ChuteNo   = chute.ToString(AppConstants.ChuteNoFormat, CultureInfo.InvariantCulture), // 3자리 zero-pad
                CreatedAt = now,   // B2B 로컬타임
            });
        }

        _db.B2bTestData.AddRange(rows);
        await _db.SaveChangesAsync(ct);
        return B2BApiResponse.Ok();
    }

    // ── §2.2 엑셀 업로드 파싱(신/구양식 자동판별) ───────────────────────────────
    public async Task<B2BApiResponse> UploadExcelAsync(Stream excelStream, CancellationToken ct = default)
    {
        try
        {
            var parsed = new List<TestData>();
            var now = DateTime.Now;

            // ClosedXML 은 동기 API — 스트림 전체를 읽어 워크북 로드.
            using (var wb = new XLWorkbook(excelStream))
            {
                var ws = wb.Worksheet(1);
                var used = ws.RangeUsed();
                if (used is null)
                    return B2BApiResponse.Fail(FailMessages.ExcelNoData);   // 행 0개

                // zip-bomb/대용량 방어(코드리뷰 후속 #2): 압축 해제 후 사용 범위 행·열 상한을 값싸게 검사.
                // RowCount()/ColumnCount() 는 used 범위 경계 산술(O(1)) — 행 순회 전 조기 차단으로 팽창 폭주 방지.
                if (used.RowCount() > AppConstants.UploadMaxRows
                 || used.ColumnCount() > AppConstants.UploadMaxColumns)
                    return B2BApiResponse.Fail(FailMessages.ExcelTooLarge);

                var firstRow = used.FirstRow().RowNumber();
                var lastRow  = used.LastRow().RowNumber();
                var firstCol = used.FirstColumn().ColumnNumber();

                // 헤더 자동감지: 1행 1열이 날짜형이면 헤더 없음(startRow=0), 아니면 헤더 있음(startRow=1).
                var headerless = IsDateLike(ws.Cell(firstRow, firstCol));
                var dataStart = headerless ? firstRow : firstRow + 1;

                // 절대 셀 접근 — used 범위보다 좁아도 빈 셀 반환(예외 없음).
                for (var rn = dataStart; rn <= lastRow; rn++)
                {
                    string Col(int off) => ws.Cell(rn, firstCol + off).GetString().Trim();

                    var barcode = Col(2);   // 3열 barcode — 빈/공백 행 skip
                    if (string.IsNullOrWhiteSpace(barcode)) continue;

                    var bizDayRaw = Col(0);
                    var batch     = Col(1);
                    var col4      = Col(3);
                    var col5      = Col(4);

                    string? barcode2;
                    string  chuteRaw;
                    if (!string.IsNullOrWhiteSpace(col5))
                    {
                        // 5컬럼 신양식: barcode2 = col4(빈이면 null), chuteNo = col5.
                        barcode2 = string.IsNullOrWhiteSpace(col4) ? null : col4;
                        chuteRaw = col5;
                    }
                    else
                    {
                        // 4컬럼 구양식: barcode2 = null, chuteNo = col4.
                        barcode2 = null;
                        chuteRaw = col4;
                    }

                    parsed.Add(new TestData
                    {
                        BizDay    = AppUtils.NormalizeBizDay(bizDayRaw),   // 정규화(비존재 날짜 → 예외 → 아래 catch)
                        Batch     = batch,
                        Barcode   = barcode,
                        Barcode2  = barcode2,
                        ChuteNo   = AppUtils.NormalizeChuteNo(chuteRaw),   // int 성공 시 D3, 실패 시 원문
                        CreatedAt = now,
                    });
                }
            }

            if (parsed.Count == 0)
                return B2BApiResponse.Fail(FailMessages.NoValidDataToUpload);   // 유효행 0개

            _db.B2bTestData.AddRange(parsed);
            await _db.SaveChangesAsync(ct);
            return B2BApiResponse.Ok(FailMessages.UploadComplete(parsed.Count));
        }
        catch (Exception ex)
        {
            // 전체 try/catch — 파싱/정규화 예외 → F(§2.2).
            return B2BApiResponse.Fail(FailMessages.ExcelParsingError(ex.Message));
        }
    }

    // ── §2.3 배치 요약 ─────────────────────────────────────────────────────────
    public async Task<List<TestDataSummaryRow>> GetSummaryAsync(string? bizDay, CancellationToken ct = default)
    {
        var query = _db.B2bTestData.AsQueryable();
        if (!string.IsNullOrWhiteSpace(bizDay))
        {
            var nDay = AppUtils.NormalizeBizDay(bizDay);   // 비존재 날짜 → ArgumentException(#17)
            query = query.Where(d => d.BizDay == nDay);
        }

        var rows = await query
            .GroupBy(d => new { d.BizDay, d.Batch })
            .Select(g => new TestDataSummaryRow(
                g.Key.BizDay,
                g.Key.Batch,
                g.Count(),
                g.Max(x => x.ReceiveTime)))   // MAX(receive_time)
            .ToListAsync(ct);

        // 정렬 BizDay desc, Batch desc (문자열 Ordinal — DB 정렬 로케일 의존 회피).
        return rows
            .OrderByDescending(r => r.BizDay, StringComparer.Ordinal)
            .ThenByDescending(r => r.Batch, StringComparer.Ordinal)
            .ToList();
    }

    // ── §2.4 상세(로그 조인 + 아카이브 필터) ────────────────────────────────────
    public async Task<List<TestDataDetailRow>> GetDetailAsync(
        string bizDay, string batch, ArchiveFilter archived = ArchiveFilter.Active, CancellationToken ct = default)
    {
        var nDay = AppUtils.NormalizeBizDay(bizDay);   // 비존재 날짜 → ArgumentException(#17)

        var data = await _db.B2bTestData
            .Where(d => d.BizDay == nDay && d.Batch == batch)
            .ToListAsync(ct);

        // 정렬: Barcode → ChuteNo(int 파싱 우선, 실패 int.MaxValue) → ChuteNo 문자열.
        data = data
            .OrderBy(d => d.Barcode, StringComparer.Ordinal)
            .ThenBy(d => int.TryParse(d.ChuteNo, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? n : int.MaxValue)
            .ThenBy(d => d.ChuteNo, StringComparer.Ordinal)
            .ToList();

        // 관련 로그 로드(같은 bizDay/batch, INPUT/SORT) — 이후 아카이브 필터 + 행 매핑.
        var logs = await _db.B2bTestLogs
            .Where(l => l.BizDay == nDay && l.Batch == batch
                     && (l.LogType == "INPUT" || l.LogType == "SORT"))
            .ToListAsync(ct);

        // 아카이브 필터(§3.4) — active=미아카이브만 / archivedOnly=아카이브만 / all=전부.
        var filtered = archived switch
        {
            ArchiveFilter.Active       => logs.Where(l => l.ArchivedAt == null),
            ArchiveFilter.ArchivedOnly => logs.Where(l => l.ArchivedAt != null),
            _                          => logs.AsEnumerable(),   // All
        };
        var inputLogs = filtered.Where(l => l.LogType == "INPUT").ToList();
        var sortLogs  = filtered.Where(l => l.LogType == "SORT").ToList();

        // INPUT/SORT 매핑: TestDataId==d.Id 우선(LogTime desc first), 없으면 Barcode 폴백(TestDataId==null).
        static TestLog? Map(List<TestLog> pool, TestData d)
        {
            var byId = pool
                .Where(l => l.TestDataId == d.Id)
                .OrderByDescending(l => l.LogTime)
                .FirstOrDefault();
            if (byId is not null) return byId;
            return pool
                .Where(l => l.TestDataId == null && l.Barcode == d.Barcode)
                .OrderByDescending(l => l.LogTime)
                .FirstOrDefault();
        }

        return data.Select(d =>
        {
            var inLog   = Map(inputLogs, d);
            var sortLog = Map(sortLogs, d);
            return new TestDataDetailRow(
                d.Id, d.BizDay, d.Batch, d.Barcode, d.Barcode2, d.ChuteNo, d.ReceiveTime, d.CreatedAt,
                inLog?.Status, inLog?.LogTime, sortLog?.Status, sortLog?.LogTime);
        }).ToList();
    }

    // ── §3.3 수신시간 초기화 + 연관 아카이브 ─────────────────────────────────────
    public async Task<B2BApiResponse> ResetReceiveTimeAsync(IReadOnlyList<long>? ids, CancellationToken ct = default)
    {
        var idList = Normalize(ids);
        if (idList.Count == 0) return B2BApiResponse.Ok();

        var entities = await _db.B2bTestData.Where(d => idList.Contains(d.Id)).ToListAsync(ct);
        foreach (var e in entities)
            e.ReceiveTime = null;   // 미처리 복귀(행 유지)

        await ArchiveAssociatedAsync(entities, idList, ct);
        await _db.SaveChangesAsync(ct);
        return B2BApiResponse.Ok();
    }

    // ── §3.3 선택 삭제(하드) + 연관 아카이브(소프트) ─────────────────────────────
    public async Task<B2BApiResponse> DeleteAsync(IReadOnlyList<long>? ids, CancellationToken ct = default)
    {
        var idList = Normalize(ids);
        if (idList.Count == 0) return B2BApiResponse.Ok();

        var entities = await _db.B2bTestData.Where(d => idList.Contains(d.Id)).ToListAsync(ct);

        // 연관 로그/결과 아카이브 먼저(entities 키/ids 필요) → test_data 하드삭제.
        await ArchiveAssociatedAsync(entities, idList, ct);
        _db.B2bTestData.RemoveRange(entities);   // 등록 원장만 하드삭제(정당) — 로그/결과는 archived 보존.
        await _db.SaveChangesAsync(ct);
        return B2BApiResponse.Ok();
    }

    // ── §3.2 아카이브 스코핑(공통) — 하드삭제 절대 금지, archived_at UPDATE만 ──────
    private async Task ArchiveAssociatedAsync(List<TestData> entities, List<long> idList, CancellationToken ct)
    {
        if (entities.Count == 0) return;

        // 스코프 키: 선택 행의 (BizDay,Batch,Barcode) 조합 집합(원본 barcode-only 광범위 삭제 교정).
        // 값 튜플 집합 — 문자열 구분자 없이 정확한 3필드 동치(구분자 충돌 원천 회피).
        var keys     = new HashSet<(string, string, string)>(
            entities.Select(e => (e.BizDay, e.Batch, e.Barcode)));
        var barcodes = entities.Select(e => e.Barcode).Distinct().ToList();
        var now = DateTime.Now;

        // test_log 대상: TestDataId in ids  또는  (BizDay,Batch,Barcode) in keys — archived_at==null만.
        // barcode 광범위 로드 후 메모리에서 정밀 필터(배치 밖 동일 barcode 미영향).
        var candidateLogs = await _db.B2bTestLogs
            .Where(l => l.ArchivedAt == null
                     && ((l.TestDataId != null && idList.Contains(l.TestDataId.Value))
                         || barcodes.Contains(l.Barcode)))
            .ToListAsync(ct);
        foreach (var l in candidateLogs)
        {
            var matchById  = l.TestDataId is not null && idList.Contains(l.TestDataId.Value);
            var matchByKey = keys.Contains((l.BizDay, l.Batch, l.Barcode));
            if (matchById || matchByKey)
                l.ArchivedAt = now;   // 소프트삭제 — DELETE/RemoveRange 금지.
        }

        // work_result 대상: (BizDay,Batch,Barcode) in keys(work_result엔 TestDataId 없음).
        var candidateResults = await _db.B2bWorkResults
            .Where(w => w.ArchivedAt == null && barcodes.Contains(w.Barcode))
            .ToListAsync(ct);
        foreach (var w in candidateResults)
        {
            if (keys.Contains((w.BizDay, w.Batch, w.Barcode)))
                w.ArchivedAt = now;
        }
    }

    // ── 헬퍼 ─────────────────────────────────────────────────────────────────
    private static List<long> Normalize(IReadOnlyList<long>? ids) =>
        ids is null ? new List<long>() : ids.Distinct().ToList();

    /// <summary>헤더 자동감지 — 8자리 숫자 또는 YYYY-MM-DD(10자리) 또는 날짜형 셀이면 날짜(헤더 없음).</summary>
    private static bool IsDateLike(IXLCell cell)
    {
        if (cell.DataType == XLDataType.DateTime) return true;
        var s = cell.GetString().Trim();
        if (s.Length == 8 && s.All(char.IsDigit)) return true;
        return DateTime.TryParseExact(s, "yyyy-MM-dd",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
    }
}
