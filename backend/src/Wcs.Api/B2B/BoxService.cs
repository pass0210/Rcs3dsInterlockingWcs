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
}
