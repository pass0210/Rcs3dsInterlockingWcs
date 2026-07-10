using Microsoft.EntityFrameworkCore;
using Wcs.Data;
using Wcs.Data.B2B;

namespace Wcs.Api.B2B;

// ════════════════════════════════════════════════════════════════════════════
// BoxService — 박스 마감 데이터 수신/저장(§6.5).
// (bizDay,batch,boxNo) 중복거부 · barcode 미검증 · 빈 barcode item 필터 · 트랜잭션 원자 저장.
// ⚠ created_at 은 B2B 로컬타임(DateTime.Now) — 원본 호환(사용자 확정 Q3).
// ════════════════════════════════════════════════════════════════════════════

public interface IBoxService
{
    /// <summary>박스 마감 — (bizDay,batch,boxNo) 중복이면 F, 아니면 Box+Items 원자 저장.</summary>
    Task<B2BApiResponse> ProcessBoxAsync(BoxRequest req, CancellationToken ct = default);

    /// <summary>
    /// E6(S-B2B-3a) — 박스 목록 + 내품 조회(§2.3). bizDay 필수(비존재 → ArgumentException #17),
    /// batch 옵션(생략 시 해당 bizDay 전체). 읽기 전용 · 기존 ProcessBoxAsync 무접촉(additive).
    /// </summary>
    Task<List<BoxRow>> GetBoxesAsync(string bizDay, string? batch, CancellationToken ct = default);
}

public sealed class BoxService : IBoxService
{
    private readonly WcsDbContext _db;

    public BoxService(WcsDbContext db) => _db = db;

    public async Task<B2BApiResponse> ProcessBoxAsync(BoxRequest req, CancellationToken ct = default)
    {
        var bizDay = AppUtils.NormalizeBizDay(req.BizDay);
        var chute  = AppUtils.NormalizeChuteNo(req.ChuteNo);

        // (bizDay,batch,boxNo) 중복 재전송 거부(재전송 방지 — UNIQUE 인덱스 백스톱).
        var exists = await _db.B2bBoxes.AnyAsync(
            b => b.BizDay == bizDay && b.Batch == req.Batch && b.BoxNo == req.BoxNo, ct);
        if (exists)
            return B2BApiResponse.Fail(FailMessages.BoxAlreadyExists);   // #8

        // 빈 barcode item 필터(barcode 존재검증 없음 — 박스는 출고 기록 저장 목적).
        var items = req.Items
            .Where(i => !string.IsNullOrWhiteSpace(i.Barcode))
            .Select(i => new BoxItem { Barcode = i.Barcode!.Trim(), Qty = i.Qty })
            .ToList();

        var box = new Box
        {
            BizDay    = bizDay,
            Batch     = req.Batch,
            BoxNo     = req.BoxNo,
            ChuteNo   = chute,
            EndTime   = req.EndTime,        // 클라이언트 문자열 그대로 저장
            CreatedAt = DateTime.Now,       // B2B 로컬타임
            Items     = items,
        };

        // Box + BoxItems 트랜잭션 원자 저장(CASCADE 관계로 items 함께 INSERT).
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        _db.B2bBoxes.Add(box);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return B2BApiResponse.Ok();
    }

    // ── E6(S-B2B-3a) 박스 목록 + 내품 조회 — 읽기 전용 additive(§2.3) ────────────────────────
    public async Task<List<BoxRow>> GetBoxesAsync(string bizDay, string? batch, CancellationToken ct = default)
    {
        var nDay = AppUtils.NormalizeBizDay(bizDay);   // 비존재 날짜 → ArgumentException(#17)

        var q = _db.B2bBoxes.Where(b => b.BizDay == nDay);
        if (!string.IsNullOrWhiteSpace(batch))
            q = q.Where(b => b.Batch == batch);

        // 내품(Items)을 컬렉션 프로젝션으로 프리로드 — N+1 회피(단일 조회로 부모-자식 로드).
        return await q
            .OrderBy(b => b.Batch).ThenBy(b => b.BoxNo).ThenBy(b => b.Id)
            .Select(b => new BoxRow(
                b.Id, b.BizDay, b.Batch, b.BoxNo, b.ChuteNo, b.EndTime, b.CreatedAt,
                b.Items
                    .OrderBy(i => i.Id)
                    .Select(i => new BoxItemRow(i.Barcode, i.Qty))
                    .ToList()))
            .ToListAsync(ct);
    }
}
