using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Wcs.Data;
using Wcs.Data.B2B;

namespace Wcs.Api.B2B;

// ════════════════════════════════════════════════════════════════════════════
// WorkService — RCS 4대 API(미작업조회/투입/분류/결과) 처리.
// 알고리즘 정본: docs/B2B-SCHEMA.md §6. 실패 message: §4(FailMessages).
//
// ⚠ 이식 변경: unprocessed 0건 자동생성(AutoGenerate) 블록 제거 — 0건이면 빈 배열 반환(트리거 없음).
// ⚠ created_at 은 B2B 로컬타임(DateTime.Now) — 원본 호환(사용자 확정 Q3).
// pId·inductionNo 는 RCS 자체생성 정수 — 서버 미검증, .ToString()으로 문자열 컬럼에 그대로 저장.
// ════════════════════════════════════════════════════════════════════════════

public interface IWorkService
{
    /// <summary>미작업 조회(부수효과: receive_time 일괄 마킹). 0건이면 빈 배열.</summary>
    Task<List<UnprocessedGroupResponse>> GetUnprocessedAsync(string? bizDay, CancellationToken ct = default);

    /// <summary>투입 로그(INPUT) — qty 묶음(가용&lt;qty 전량거부).</summary>
    Task<B2BApiResponse> ProcessInputAsync(InputRequest req, CancellationToken ct = default);

    /// <summary>분류 로그(SORT) — chute 매칭·이미분류·qty 묶음.</summary>
    Task<B2BApiResponse> ProcessClassificationAsync(ClassificationRequest req, CancellationToken ct = default);

    /// <summary>전체 작업 결과 — 사전 존재검증(전체거부)·item.qty 반복 생성.</summary>
    Task<B2BApiResponse> ProcessResultsAsync(List<ResultRequestGroup>? groups, CancellationToken ct = default);
}

public sealed class WorkService : IWorkService
{
    private readonly WcsDbContext _db;

    public WorkService(WcsDbContext db) => _db = db;

    // ── 6.1 미작업 조회(부수효과 있는 GET) ────────────────────────────────────
    public async Task<List<UnprocessedGroupResponse>> GetUnprocessedAsync(
        string? bizDay, CancellationToken ct = default)
    {
        var normalized = AppUtils.NormalizeBizDay(bizDay);  // 비존재 날짜 → ArgumentException(#17)

        var rows = await _db.B2bTestData
            .Where(d => d.BizDay == normalized && d.ReceiveTime == null)
            .OrderBy(d => d.Batch).ThenBy(d => d.ChuteNo).ThenBy(d => d.Barcode)
            .ToListAsync(ct);

        // [이식 변경] 원본은 0건이면 AutoGenerate 호출 → B2B-1 은 삭제. 0건이면 빈 배열 반환.
        if (rows.Count == 0)
            return new List<UnprocessedGroupResponse>();

        // 부수효과: 조회 행 receive_time 일괄 마킹(재조회 방지). B2B 로컬타임.
        var now = DateTime.Now;
        foreach (var r in rows)
            r.ReceiveTime = now;
        await _db.SaveChangesAsync(ct);

        // 2단계 그룹핑: Batch → (Barcode, ChuteNo), qty = COUNT. 정렬 ChuteNo→Barcode.
        return rows
            .GroupBy(r => r.Batch)
            .OrderBy(bg => bg.Key, StringComparer.Ordinal)
            .Select(bg => new UnprocessedGroupResponse(
                normalized,
                bg.Key,
                bg.GroupBy(x => new { x.Barcode, x.ChuteNo })
                  .Select(ig => new UnprocessedItem(ig.Key.Barcode, ig.Key.ChuteNo, ig.Count()))
                  .OrderBy(i => i.ChuteNo, StringComparer.Ordinal)
                  .ThenBy(i => i.Barcode, StringComparer.Ordinal)
                  .ToList()))
            .ToList();
    }

    // ── 6.2 투입(INPUT) ────────────────────────────────────────────────────────
    public async Task<B2BApiResponse> ProcessInputAsync(InputRequest req, CancellationToken ct = default)
    {
        var bizDay = AppUtils.NormalizeBizDay(req.BizDay);

        var candidates = await _db.B2bTestData
            .Where(d => d.Barcode == req.Barcode && d.BizDay == bizDay && d.Batch == req.Batch)
            .OrderBy(d => d.Id)
            .ToListAsync(ct);
        if (candidates.Count == 0)
            return B2BApiResponse.Fail(FailMessages.BarcodeNotFound);   // #1

        // 이미 INPUT 로그 연결된 후보 제외 → 가용
        var candidateIds = candidates.Select(c => c.Id).ToList();
        var usedSet = (await _db.B2bTestLogs
            .Where(l => l.LogType == "INPUT" && l.TestDataId != null && candidateIds.Contains(l.TestDataId.Value))
            .Select(l => l.TestDataId!.Value)
            .ToListAsync(ct)).ToHashSet();
        var available = candidates.Where(c => !usedSet.Contains(c.Id)).ToList();

        if (available.Count < req.Qty)
            return B2BApiResponse.Fail(FailMessages.NotEnoughRows(req.Qty, available.Count)); // #2 (전량거부)

        var logTime = ParseTimeOrNow(req.InTime);
        var now = DateTime.Now;
        foreach (var td in available.Take(req.Qty))
        {
            _db.B2bTestLogs.Add(new TestLog
            {
                LogType     = "INPUT",
                BizDay      = bizDay,
                Batch       = req.Batch,
                Barcode     = req.Barcode ?? string.Empty,
                EquipmentNo = req.InductionNo.ToString(CultureInfo.InvariantCulture), // 미검증 그대로 저장
                Pid         = req.PId.ToString(CultureInfo.InvariantCulture),          // 미검증 그대로 저장
                Status      = req.Status,
                Reason      = req.Reason,
                LogTime     = logTime,
                CreatedAt   = now,          // B2B 로컬타임
                TestDataId  = td.Id,
            });
        }
        await _db.SaveChangesAsync(ct);
        return B2BApiResponse.Ok();
    }

    // ── 6.3 분류(SORT) ─────────────────────────────────────────────────────────
    public async Task<B2BApiResponse> ProcessClassificationAsync(
        ClassificationRequest req, CancellationToken ct = default)
    {
        var bizDay = AppUtils.NormalizeBizDay(req.BizDay);

        var candidates = await _db.B2bTestData
            .Where(d => d.Barcode == req.Barcode && d.BizDay == bizDay && d.Batch == req.Batch)
            .OrderBy(d => d.Id)
            .ToListAsync(ct);
        if (candidates.Count == 0)
            return B2BApiResponse.Fail(FailMessages.BarcodeNotFound);   // #1

        var reqChute = AppUtils.NormalizeChuteNo(req.ChuteNo);
        var matched  = candidates.Where(c => c.ChuteNo == reqChute).ToList();
        if (matched.Count == 0)
        {
            // 후보 chute_no distinct 정렬 힌트
            var validChutes = candidates
                .Select(c => c.ChuteNo)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(c => c, StringComparer.Ordinal)
                .ToList();
            return B2BApiResponse.Fail(
                FailMessages.ChuteMismatch(req.Barcode, string.Join(", ", validChutes), reqChute)); // #3
        }

        var matchedIds = matched.Select(c => c.Id).ToList();
        var usedSet = (await _db.B2bTestLogs
            .Where(l => l.LogType == "SORT" && l.TestDataId != null && matchedIds.Contains(l.TestDataId.Value))
            .Select(l => l.TestDataId!.Value)
            .ToListAsync(ct)).ToHashSet();
        var available = matched.Where(c => !usedSet.Contains(c.Id)).ToList();

        if (available.Count == 0)
            return B2BApiResponse.Fail(FailMessages.AlreadyClassified(req.Barcode, reqChute)); // #4
        if (available.Count < req.Qty)
            return B2BApiResponse.Fail(FailMessages.NotEnoughRows(req.Qty, available.Count));   // #2

        var logTime = ParseTimeOrNow(req.SortTime);
        var now = DateTime.Now;
        foreach (var td in available.Take(req.Qty))
        {
            _db.B2bTestLogs.Add(new TestLog
            {
                LogType     = "SORT",
                BizDay      = bizDay,
                Batch       = req.Batch,
                Barcode     = req.Barcode,
                EquipmentNo = reqChute,     // SORT: chuteNo(3자리 정규화)
                Pid         = req.PId.ToString(CultureInfo.InvariantCulture),
                Status      = req.Status,
                Reason      = req.Reason,
                LogTime     = logTime,
                CreatedAt   = now,          // B2B 로컬타임
                TestDataId  = td.Id,
            });
        }
        await _db.SaveChangesAsync(ct);
        return B2BApiResponse.Ok();
    }

    // ── 6.4 전체 작업 결과 ─────────────────────────────────────────────────────
    public async Task<B2BApiResponse> ProcessResultsAsync(
        List<ResultRequestGroup>? groups, CancellationToken ct = default)
    {
        if (groups is null || groups.Count == 0)
            return B2BApiResponse.Fail(FailMessages.NoDataToProcess);   // #5

        // bizDay 정규화(그룹별)
        var normalized = groups
            .Select(g => (BizDay: AppUtils.NormalizeBizDay(g.BizDay), g.Batch, g.Items))
            .ToList();

        // N+1 회피: (bizDay,batch) distinct 조합당 1회 등록 barcode HashSet 캐시
        var cache = new Dictionary<(string, string), HashSet<string>>();
        foreach (var key in normalized.Select(g => (g.BizDay, g.Batch)).Distinct())
        {
            if (cache.ContainsKey(key)) continue;
            var barcodes = await _db.B2bTestData
                .Where(d => d.BizDay == key.Item1 && d.Batch == key.Item2)
                .Select(d => d.Barcode)
                .ToListAsync(ct);
            cache[key] = new HashSet<string>(barcodes, StringComparer.Ordinal);
        }

        // 사전 존재검증(트랜잭션 진입 전 전체거부): 비어있지 않은 barcode 중 미등록 하나라도 → F
        foreach (var g in normalized)
        {
            var set = cache[(g.BizDay, g.Batch)];
            foreach (var item in g.Items)
            {
                if (string.IsNullOrEmpty(item.Barcode)) continue;   // 빈 barcode skip
                if (!set.Contains(item.Barcode))
                    return B2BApiResponse.Fail(FailMessages.ResultBarcodeNotFound(item.Barcode)); // #6
            }
        }

        // 엔티티 확장 생성(빈 barcode skip, chuteNo 3자리 정규화, item.qty 반복)
        var now = DateTime.Now;
        var entities = new List<WorkResult>();
        foreach (var g in normalized)
        {
            foreach (var item in g.Items)
            {
                if (string.IsNullOrEmpty(item.Barcode)) continue;
                var chute = string.IsNullOrWhiteSpace(item.ChuteNo)
                    ? null
                    : AppUtils.NormalizeChuteNo(item.ChuteNo);
                for (int i = 0; i < item.Qty; i++)
                {
                    entities.Add(new WorkResult
                    {
                        BizDay    = g.BizDay,
                        Batch     = g.Batch,
                        Barcode   = item.Barcode,
                        ChuteNo   = chute,
                        CreatedAt = now,        // B2B 로컬타임
                    });
                }
            }
        }

        if (entities.Count == 0)
            return B2BApiResponse.Fail(FailMessages.NoValidDataToProcess);   // #7

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        _db.B2bWorkResults.AddRange(entities);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return B2BApiResponse.Ok();
    }

    // ── 로그 시각 파싱(실패 시 now) ─────────────────────────────────────────────
    private static DateTime ParseTimeOrNow(string? s)
    {
        if (!string.IsNullOrWhiteSpace(s) &&
            DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            return dt;
        return DateTime.Now;   // 파싱 실패 시 서버 로컬 현재시각
    }
}
