using Microsoft.EntityFrameworkCore;
using Wcs.Data;
using Wcs.Data.B2B;

namespace Wcs.Api.B2B;

// ════════════════════════════════════════════════════════════════════════════
// LogService — 프론트 전용 조회(읽기 전용): 투입/분류 로그·API 호출 이력·3-way 비교.
// 알고리즘 정본: docs/PROGRAM_STRUCTURE.md §3.2.6(로그 N+1 회피)·§3.2.7(3-way)·§9.6(api-call).
//
// ★ 무접촉(S-B2B-3a): 순수 additive 조회 — RCS 쓰기(WorkService)·test-data 관리(TestDataService)·
//   엔티티·마이그레이션 0 변경. WcsDbContext(Scoped) 주입 — HTTP 핸들러 스코프 동일 수명.
// ★ SQL Server = prod provider — LINQ 는 provider-중립(상관 서브쿼리=OUTER APPLY, 집합 프리로드).
//   SQLite 전용 함수 미사용. 0건이면 빈 리스트(→ [] 직렬화).
// ★ 아카이브 필터(active|all|archivedOnly) 소비 — TestDataService.ParseArchiveFilter 공용(컨트롤러).
// ════════════════════════════════════════════════════════════════════════════

public interface ILogService
{
    /// <summary>E1 — 투입(INPUT) 로그. bizDay 옵션(비존재 날짜 → ArgumentException #17). 아카이브 필터.</summary>
    Task<List<TestLogRow>> GetInputLogsAsync(
        string? bizDay, ArchiveFilter archived = ArchiveFilter.Active, CancellationToken ct = default);

    /// <summary>E2 — 분류(SORT) 로그. bizDay 옵션. 아카이브 필터.</summary>
    Task<List<TestLogRow>> GetSortLogsAsync(
        string? bizDay, ArchiveFilter archived = ArchiveFilter.Active, CancellationToken ct = default);

    /// <summary>E3 — API 호출 이력. date 옵션(미지정=전체). 최신순 최대 500건(AppConstants).</summary>
    Task<List<ApiCallLogRow>> GetApiCallLogsAsync(string? date, CancellationToken ct = default);

    /// <summary>E5 — 투입/분류/결과 3-way 비교(§3.2.7). bizDay 옵션. 아카이브 필터. Batch 포함 매칭 키.</summary>
    Task<List<ComparisonRow>> GetResultComparisonAsync(
        string? bizDay, ArchiveFilter archived = ArchiveFilter.Active, CancellationToken ct = default);
}

public sealed class LogService : ILogService
{
    private readonly WcsDbContext _db;

    public LogService(WcsDbContext db) => _db = db;

    // ── E1/E2 로그 조회(§3.2.6) — 상관 서브쿼리로 파생 필드 인라인(N+1 회피·OUTER APPLY) ──────
    public Task<List<TestLogRow>> GetInputLogsAsync(
        string? bizDay, ArchiveFilter archived = ArchiveFilter.Active, CancellationToken ct = default)
        => GetLogsAsync("INPUT", bizDay, archived, ct);

    public Task<List<TestLogRow>> GetSortLogsAsync(
        string? bizDay, ArchiveFilter archived = ArchiveFilter.Active, CancellationToken ct = default)
        => GetLogsAsync("SORT", bizDay, archived, ct);

    private async Task<List<TestLogRow>> GetLogsAsync(
        string logType, string? bizDay, ArchiveFilter archived, CancellationToken ct)
    {
        var q = _db.B2bTestLogs.Where(l => l.LogType == logType);
        if (!string.IsNullOrWhiteSpace(bizDay))
        {
            var nDay = AppUtils.NormalizeBizDay(bizDay);   // 비존재 날짜 → ArgumentException(#17)
            q = q.Where(l => l.BizDay == nDay);
        }
        q = FilterArchive(q, archived);

        // 상관 서브쿼리(Barcode 단독 매칭) — 등록 test_data 의 슈트·수신시각 파생. 원본 §3.2.6 "Barcode 단독".
        // ★ 코드리뷰 #1: 슈트·수신시각을 **단일** 서브쿼리(OrderBy(Id) 결정적)로 묶어 **같은 행**에서 취득.
        //   동일 barcode 다중 test_data 허용(EntitiesB2B.cs) → 두 독립 서브쿼리는 서로 다른 행을 골라
        //   'Frankenstein' 행(슈트=A행·수신시각=B행)을 유발할 수 있음. 단일 OUTER APPLY(SQL Server) /
        //   단일 상관 서브쿼리(SQLite)로 번역 — N+1 회피·provider 중립·비결정성 제거.
        var raw = await q
            .OrderBy(l => l.LogTime).ThenBy(l => l.Id)
            .Select(l => new
            {
                l.Id, l.BizDay, l.Batch, l.Barcode, l.EquipmentNo, l.Pid, l.Status, l.Reason, l.LogTime, l.ArchivedAt,
                Td = _db.B2bTestData
                    .Where(d => d.Barcode == l.Barcode)
                    .OrderBy(d => d.Id)                              // 결정적(최소 Id 행)
                    .Select(d => new { d.ChuteNo, d.ReceiveTime })   // 두 필드를 동일 행에서
                    .FirstOrDefault(),
            })
            .ToListAsync(ct);

        return raw
            .Select(x => new TestLogRow(
                x.Id, x.BizDay, x.Batch, x.Barcode, x.EquipmentNo, x.Pid, x.Status, x.Reason, x.LogTime,
                x.Td != null ? x.Td.ChuteNo : null,
                x.Td != null ? x.Td.ReceiveTime : null,
                x.ArchivedAt))
            .ToList();
    }

    // ── E3 API 호출 이력(§9.6) — date 날짜 필터 + 최신순 최대 500건 ─────────────────────────
    public async Task<List<ApiCallLogRow>> GetApiCallLogsAsync(string? date, CancellationToken ct = default)
    {
        var q = _db.B2bApiCallLogs.AsQueryable();
        if (!string.IsNullOrWhiteSpace(date))
        {
            var day  = ParseDate(date);          // 비존재 날짜 → ArgumentException(#17)
            var next = day.AddDays(1);
            q = q.Where(l => l.CalledAt >= day && l.CalledAt < next);
        }

        // 최신순(called_at desc) 상한 500 — Take 가 SQL Server TOP / SQLite LIMIT 로 번역(provider 중립).
        return await q
            .OrderByDescending(l => l.CalledAt).ThenByDescending(l => l.Id)
            .Take(AppConstants.ApiCallLogMaxItems)
            .Select(l => new ApiCallLogRow(
                l.Id, l.Endpoint, l.HttpMethod, l.RequestBody, l.ResponseStatus, l.ResponseBody,
                l.HttpStatusCode, l.DurationMs, l.ClientIp, l.ErrorMessage, l.CalledAt))
            .ToListAsync(ct);
    }

    // ── E5 3-way 비교(§3.2.7) — test_data 기준행 순회 · TestDataId 우선 + (Batch,Barcode) 폴백 ──
    public async Task<List<ComparisonRow>> GetResultComparisonAsync(
        string? bizDay, ArchiveFilter archived = ArchiveFilter.Active, CancellationToken ct = default)
    {
        string? nDay = null;
        if (!string.IsNullOrWhiteSpace(bizDay))
            nDay = AppUtils.NormalizeBizDay(bizDay);   // 비존재 날짜 → ArgumentException(#17)

        // 기준행: test_data(archived_at 없음 — 항상 전체). 정렬로 결정적 매칭.
        var tdQuery = _db.B2bTestData.AsQueryable();
        if (nDay != null) tdQuery = tdQuery.Where(d => d.BizDay == nDay);
        var testData = await tdQuery
            .OrderBy(d => d.Batch).ThenBy(d => d.Barcode).ThenBy(d => d.Id)
            .ToListAsync(ct);

        // 로그(INPUT/SORT) 집합 프리로드 + 아카이브 필터.
        var logQuery = _db.B2bTestLogs.Where(l => l.LogType == "INPUT" || l.LogType == "SORT");
        if (nDay != null) logQuery = logQuery.Where(l => l.BizDay == nDay);
        var logs   = await FilterArchive(logQuery, archived).ToListAsync(ct);
        var inputs = logs.Where(l => l.LogType == "INPUT").ToList();
        var sorts  = logs.Where(l => l.LogType == "SORT").ToList();

        // 결과 집합 프리로드 + 아카이브 필터.
        var resultQuery = _db.B2bWorkResults.AsQueryable();
        if (nDay != null) resultQuery = resultQuery.Where(w => w.BizDay == nDay);
        var results = await FilterArchive(resultQuery, archived).ToListAsync(ct);

        var usedInput  = new HashSet<long>();
        var usedSort   = new HashSet<long>();
        var usedResult = new HashSet<long>();
        var rows = new List<ComparisonRow>(testData.Count);
        foreach (var d in testData)
        {
            var input  = MatchLog(inputs, d, usedInput);
            var sort   = MatchLog(sorts, d, usedSort);
            var result = MatchResult(results, d, sort, usedResult);

            var hasInput  = input  is not null;
            var hasSort   = sort   is not null;
            var hasResult = result is not null;
            // IsMatch: 3자 존재 + SORT.chuteNo == RESULT.chuteNo(둘 다 3자리 정규화 저장).
            // ★ 코드리뷰 #5: 둘 다 null 이면 == 이 true 가 되므로 EquipmentNo 비-null 을 명시 요구
            //   (슈트값 없는 행끼리 '일치'로 오판정 방지 — 비교하려면 실제 슈트값이 있어야 함).
            var isMatch   = hasInput && hasSort && hasResult
                            && sort!.EquipmentNo is not null
                            && sort.EquipmentNo == result!.ChuteNo;
            var isMissing = !(hasInput && hasSort && hasResult);

            rows.Add(new ComparisonRow(
                d.BizDay, d.Batch, d.Barcode, d.ChuteNo,
                hasInput, hasSort, hasResult,
                input?.Status, input?.LogTime,
                sort?.EquipmentNo, sort?.Status, sort?.LogTime,
                result?.ChuteNo,
                isMatch, isMissing));
        }
        return rows;
    }

    // ── 매칭 헬퍼(§3.2.7) — TestDataId 우선, (Batch,Barcode) 폴백. 사용된 id 재사용 금지 ────────
    private static TestLog? MatchLog(List<TestLog> pool, TestData d, HashSet<long> used)
    {
        // TestDataId 정밀 매칭 우선.
        var byId = pool
            .Where(l => l.TestDataId == d.Id && !used.Contains(l.Id))
            .OrderBy(l => l.LogTime).ThenBy(l => l.Id)
            .FirstOrDefault();
        if (byId is not null) { used.Add(byId.Id); return byId; }

        // 폴백: (Batch, Barcode) — Batch 포함(이월 Barcode-only 결함 교정). 사용된 로그 id 배제.
        var byKey = pool
            .Where(l => l.Batch == d.Batch && l.Barcode == d.Barcode && !used.Contains(l.Id))
            .OrderBy(l => l.LogTime).ThenBy(l => l.Id)
            .FirstOrDefault();
        if (byKey is not null) { used.Add(byKey.Id); return byKey; }

        return null;
    }

    private static WorkResult? MatchResult(List<WorkResult> pool, TestData d, TestLog? sort, HashSet<long> used)
    {
        // (Batch, Barcode) 후보 — Batch 포함(이월 결함 교정). 사용된 결과 id 배제.
        var candidates = pool
            .Where(w => w.Batch == d.Batch && w.Barcode == d.Barcode && !used.Contains(w.Id))
            .ToList();
        if (candidates.Count == 0) return null;

        // SORT.chuteNo 와 동일한 결과 우선, 없으면 미사용 첫 번째.
        WorkResult? chosen = null;
        if (!string.IsNullOrEmpty(sort?.EquipmentNo))
            chosen = candidates.Where(w => w.ChuteNo == sort!.EquipmentNo).OrderBy(w => w.Id).FirstOrDefault();
        chosen ??= candidates.OrderBy(w => w.Id).FirstOrDefault();

        if (chosen is not null) used.Add(chosen.Id);
        return chosen;
    }

    // ── 아카이브 필터(§3.4) — active=미아카이브 / archivedOnly=아카이브만 / all=전부 ────────────
    private static IQueryable<TestLog> FilterArchive(IQueryable<TestLog> q, ArchiveFilter f) => f switch
    {
        ArchiveFilter.Active       => q.Where(l => l.ArchivedAt == null),
        ArchiveFilter.ArchivedOnly => q.Where(l => l.ArchivedAt != null),
        _                          => q,   // All
    };

    private static IQueryable<WorkResult> FilterArchive(IQueryable<WorkResult> q, ArchiveFilter f) => f switch
    {
        ArchiveFilter.Active       => q.Where(w => w.ArchivedAt == null),
        ArchiveFilter.ArchivedOnly => q.Where(w => w.ArchivedAt != null),
        _                          => q,   // All
    };

    // ── date(E3) 파싱 — NormalizeBizDay 검증 재사용(중복 금지) 후 date-only DateTime 반환 ────────
    private static DateTime ParseDate(string date)
    {
        var normalized = AppUtils.NormalizeBizDay(date);   // "YYYY-MM-DD" 또는 ArgumentException(#17)
        return DateTime.ParseExact(normalized, "yyyy-MM-dd",
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
