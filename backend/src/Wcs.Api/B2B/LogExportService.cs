using System.Globalization;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Wcs.Data;
using Wcs.Data.B2B;

namespace Wcs.Api.B2B;

// ════════════════════════════════════════════════════════════════════════════
// LogExportService — E4 투입+분류 통합 Excel 내보내기(하이브리드 페어링).
// 알고리즘 정본: docs/PROGRAM_STRUCTURE.md §3.2.8(Phase1/Phase2)·§3.2.9(소요시간·층매핑).
//
// ★ Phase 1(정밀): TestDataId 로 INPUT↔SORT 1:1(LogTime→Id 순 Queue).
// ★ Phase 2(폴백):  Phase1 미매칭 INPUT ← 미사용 SORT 를 (Batch,Barcode) 그룹 LogTime 오름차순 zip.
// ★ 소요시간 = (SORT.LogTime − INPUT.LogTime) 초, "F1" 포맷, span≥0 일 때만.
// ★ 인덕션→층 매핑(1·2=2층, 3·4=1층, 그 외 공백) — 설비 고정 물리 규칙(문서화 하드코딩·시간값 아님).
// ★ 기본 active 로그만(archived 제외 — 삭제/초기화된 데이터는 내보내지 않음).
// ★ 출력은 INPUT LogTime 오름차순. SORT 미매칭 시 슈트/소요시간 칸 공백. ClosedXML(기존 의존성) 재사용.
// ════════════════════════════════════════════════════════════════════════════

public interface ILogExportService
{
    /// <summary>투입+분류 통합 xlsx 바이너리 + 파일명 생성. bizDay 필수(비존재 → ArgumentException #17).</summary>
    Task<(byte[] Content, string FileName)> ExportAsync(
        string bizDay, string? batch, CancellationToken ct = default);
}

public sealed class LogExportService : ILogExportService
{
    private readonly WcsDbContext _db;

    public LogExportService(WcsDbContext db) => _db = db;

    private static readonly string[] Headers =
    {
        "업무일자", "배치", "바코드", "인덕션", "층",
        "투입상태", "투입시각", "슈트", "분류상태", "분류시각", "소요시간(초)",
    };

    public async Task<(byte[] Content, string FileName)> ExportAsync(
        string bizDay, string? batch, CancellationToken ct = default)
    {
        var nDay = AppUtils.NormalizeBizDay(bizDay);   // 비존재 날짜 → ArgumentException(#17)

        // active 로그만(기본). batch 옵션 필터. INPUT/SORT 집합 프리로드(N+1 회피).
        var logQuery = _db.B2bTestLogs
            .Where(l => l.BizDay == nDay && l.ArchivedAt == null
                     && (l.LogType == "INPUT" || l.LogType == "SORT"));
        if (!string.IsNullOrWhiteSpace(batch))
            logQuery = logQuery.Where(l => l.Batch == batch);

        var logs   = await logQuery.ToListAsync(ct);
        var inputs = logs.Where(l => l.LogType == "INPUT").ToList();
        var sorts  = logs.Where(l => l.LogType == "SORT").ToList();

        // 출력 순서 = INPUT LogTime 오름차순(§3.2.8). Phase 2 zip 순서와도 일치.
        var orderedInputs = inputs.OrderBy(i => i.LogTime).ThenBy(i => i.Id).ToList();

        var usedSortIds  = new HashSet<long>();
        var matchedSort  = new Dictionary<long, TestLog?>(orderedInputs.Count);   // inputId → sort

        // ── Phase 1(정밀): TestDataId 그룹 Queue(LogTime→Id 순) 1:1 dequeue ──────────────────
        var byTestData = sorts
            .Where(s => s.TestDataId is not null)
            .GroupBy(s => s.TestDataId!.Value)
            .ToDictionary(
                g => g.Key,
                g => new Queue<TestLog>(g.OrderBy(s => s.LogTime).ThenBy(s => s.Id)));

        foreach (var inp in orderedInputs)
        {
            TestLog? sort = null;
            if (inp.TestDataId is not null
             && byTestData.TryGetValue(inp.TestDataId.Value, out var q) && q.Count > 0)
            {
                sort = q.Dequeue();
                usedSortIds.Add(sort.Id);
            }
            matchedSort[inp.Id] = sort;
        }

        // ── Phase 2(폴백): 미매칭 INPUT ← 미사용 SORT 를 (Batch,Barcode) LogTime 오름차순 zip ──
        var byKey = sorts
            .Where(s => !usedSortIds.Contains(s.Id))
            .GroupBy(s => (s.Batch, s.Barcode))
            .ToDictionary(
                g => g.Key,
                g => new Queue<TestLog>(g.OrderBy(s => s.LogTime).ThenBy(s => s.Id)));

        foreach (var inp in orderedInputs)
        {
            if (matchedSort[inp.Id] is not null) continue;   // Phase 1 에서 이미 매칭
            if (byKey.TryGetValue((inp.Batch, inp.Barcode), out var q) && q.Count > 0)
            {
                var sort = q.Dequeue();
                usedSortIds.Add(sort.Id);
                matchedSort[inp.Id] = sort;
            }
        }

        // ── Excel 생성(ClosedXML) ────────────────────────────────────────────────────────────
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Logs");
        for (var c = 0; c < Headers.Length; c++)
            ws.Cell(1, c + 1).Value = Headers[c];

        var row = 2;
        foreach (var inp in orderedInputs)
        {
            var sort = matchedSort[inp.Id];

            ws.Cell(row, 1).Value = inp.BizDay;
            ws.Cell(row, 2).Value = inp.Batch;
            ws.Cell(row, 3).Value = inp.Barcode;
            ws.Cell(row, 4).Value = inp.EquipmentNo ?? string.Empty;   // INPUT: inductionNo
            ws.Cell(row, 5).Value = MapFloor(inp.EquipmentNo);         // 인덕션→층
            ws.Cell(row, 6).Value = inp.Status ?? string.Empty;
            if (inp.LogTime is DateTime it) ws.Cell(row, 7).Value = it;

            if (sort is not null)
            {
                ws.Cell(row, 8).Value = sort.EquipmentNo ?? string.Empty;   // SORT: chuteNo
                ws.Cell(row, 9).Value = sort.Status ?? string.Empty;
                if (sort.LogTime is DateTime st) ws.Cell(row, 10).Value = st;
                var elapsed = Elapsed(inp.LogTime, sort.LogTime);
                if (elapsed is not null) ws.Cell(row, 11).Value = elapsed;   // "F1"(span≥0만)
            }
            // SORT 미매칭 시 8~11 칸 공백(§3.2.8).
            row++;
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);

        var fileName = string.IsNullOrWhiteSpace(batch)
            ? $"input_sort_logs_{nDay}.xlsx"
            : $"input_sort_logs_{nDay}_{batch}.xlsx";
        return (ms.ToArray(), fileName);
    }

    /// <summary>인덕션→층 매핑(§3.2.9) — 1·2=2층, 3·4=1층, 그 외/파싱불가 공백. 설비 고정 물리 규칙.</summary>
    private static string MapFloor(string? inductionNo)
    {
        if (int.TryParse(inductionNo, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
        {
            if (n is 1 or 2) return "2층";
            if (n is 3 or 4) return "1층";
        }
        return string.Empty;
    }

    /// <summary>소요시간(§3.2.9) — (SORT−INPUT) 초, "F1". 양쪽 LogTime 존재 + span≥0 일 때만.</summary>
    private static string? Elapsed(DateTime? inTime, DateTime? sortTime)
    {
        if (inTime is DateTime i && sortTime is DateTime s)
        {
            var span = (s - i).TotalSeconds;
            if (span >= 0) return span.ToString("F1", CultureInfo.InvariantCulture);
        }
        return null;
    }
}
