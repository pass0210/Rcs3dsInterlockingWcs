using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wcs.Api;
using Wcs.Data;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// S-AUDIT-D-HANDSHAKE-HARDENING D④ — RecordDeposit 원인 구분(DepositRecordResult) 단위 검증.
//
// 결함: 구 RecordDeposit 반환 bool false 가 3원인(진짜중복 / DENIED재보고 / 미존재chuteNo·무피스)을
//       합류시켜 컨트롤러가 전부 '멱등 OK' INFO 로 오도했다. 원인별 결과 타입으로 분리한다.
//
// 이 파일은 I/O 계층(EfDepositRecorder)의 '원인 판정'을 결정적 in-memory SQLite 로 격리 단언한다
//   (판정은 DB 상태 의존 → Repositories 계층. Wcs.Core 순수 #8 침범 없음). 컨트롤러 원인별 로깅/응답
//   보존은 ApiIntegrationTests(VS-3/4/5)가 커버.
// ════════════════════════════════════════════════════════════════════════════

public sealed class DepositRecorderCauseTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly WcsDbContext      _db;
    private readonly SqliteConnection  _conn;
    private readonly long              _chuteDestId;   // CHUTE chuteNo=1 destination.id
    private const int ChuteNo = 1;                     // 시드 CHUTE(TEST-BARCODE-1 → chuteNo=1)

    public DepositRecorderCauseTests(ITestOutputHelper output)
    {
        _out = output;
        _conn = new SqliteConnection($"Data Source=DepCause_{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
        _conn.Open();
        var opts = new DbContextOptionsBuilder<WcsDbContext>()
            .UseSqlite(_conn)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        _db = new WcsDbContext(opts);
        _db.Database.EnsureCreated();
        DbSeeder.Seed(_db, new Dictionary<string, int> { ["1"] = 1, ["2"] = 2 });
        _chuteDestId = _db.Destinations.First(d => d.ChuteNo == ChuteNo && d.DestType == DestType.CHUTE && d.IsActive).Id;
    }

    public void Dispose() { _db.Dispose(); _conn.Dispose(); }

    private Piece InsertPiece(int pId, PieceStatus status, long? destId)
    {
        var now = DateTime.UtcNow;
        var piece = new Piece
        {
            PId = pId, IsActive = true, Barcode = "TEST-BARCODE-1", Qty = 1,
            Status = status, DestinationId = destId,
            DepositedAt = status == PieceStatus.DEPOSITED ? now : null,
            CreatedAt = now, UpdatedAt = now,
        };
        _db.Pieces.Add(piece);
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
        return piece;
    }

    // ── 신규기록: RESERVED → DEPOSITED 전이 ─────────────────────────────────────
    [Fact]
    public void NewRecord_ReservedTransition_ReturnsNewRecord()
    {
        InsertPiece(31001, PieceStatus.RESERVED, _chuteDestId);
        var r = new EfDepositRecorder(_db).RecordDeposit(31001, "TEST-BARCODE-1", ChuteNo, 1, qty: 1, clientTs: null);
        Assert.Equal(DepositRecordResult.NewRecord, r);
        Assert.Equal(PieceStatus.DEPOSITED, _db.Pieces.Single(p => p.PId == 31001).Status);
    }

    // ── 신규기록: piece 없이 IF-10 먼저(유효 chuteNo) → 직삽입 ────────────────────
    [Fact]
    public void NewRecord_DirectInsert_NoPriorPiece_ReturnsNewRecord()
    {
        var r = new EfDepositRecorder(_db).RecordDeposit(31002, "TEST-BARCODE-1", ChuteNo, 1, qty: 1, clientTs: null);
        Assert.Equal(DepositRecordResult.NewRecord, r);
        Assert.Equal(PieceStatus.DEPOSITED, _db.Pieces.Single(p => p.PId == 31002).Status);
    }

    // ── 진짜중복: 이미 DEPOSITED → Duplicate ────────────────────────────────────
    [Fact]
    public void Duplicate_AlreadyDeposited_ReturnsDuplicate()
    {
        InsertPiece(31003, PieceStatus.DEPOSITED, _chuteDestId);
        var r = new EfDepositRecorder(_db).RecordDeposit(31003, "TEST-BARCODE-1", ChuteNo, 1, qty: 1, clientTs: null);
        Assert.Equal(DepositRecordResult.Duplicate, r);
        Assert.Equal(PieceStatus.DEPOSITED, _db.Pieces.Single(p => p.PId == 31003).Status); // 불변
    }

    // ── DENIED재보고: DENIED piece → DeniedReport(불변) ─────────────────────────
    [Fact]
    public void DeniedReport_DeniedPiece_ReturnsDeniedReport_StatusUnchanged()
    {
        InsertPiece(31004, PieceStatus.DENIED, destId: null);
        var r = new EfDepositRecorder(_db).RecordDeposit(31004, "TEST-BARCODE-1", ChuteNo, 1, qty: 1, clientTs: null);
        Assert.Equal(DepositRecordResult.DeniedReport, r);
        Assert.Equal(PieceStatus.DENIED, _db.Pieces.Single(p => p.PId == 31004).Status); // DENIED 불변
        Assert.Empty(_db.PieceEvents.Where(e => _db.Pieces.Any(p => p.Id == e.PieceId && p.PId == 31004))); // piece_event 무증가
    }

    // ── 미존재chuteNo·무피스: piece 없음 + 미존재 chuteNo → NoDestination ──────────
    [Fact]
    public void NoDestination_NoPiece_UnknownChuteNo_ReturnsNoDestination()
    {
        var r = new EfDepositRecorder(_db).RecordDeposit(31005, "TEST-BARCODE-1", chuteNo: 9999, agvNo: 1, qty: 1, clientTs: null);
        Assert.Equal(DepositRecordResult.NoDestination, r);
        Assert.Empty(_db.Pieces.Where(p => p.PId == 31005)); // piece 0
    }
}
