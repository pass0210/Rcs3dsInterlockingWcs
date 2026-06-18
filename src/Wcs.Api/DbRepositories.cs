using Microsoft.EntityFrameworkCore;
using Wcs.Data;
using Wcs.PlcGateway;
using DataOrderItem   = Wcs.Data.OrderItem;
using DataDestination = Wcs.Data.Destination;

namespace Wcs.Api;

// ════════════════════════════════════════════════════════════════════════════
// EF Core 리포지토리 구현체 — 4개 인터페이스를 DB 트랜잭션으로 교체.
// Wcs.Api namespace에 배치: 인터페이스(Wcs.Api)와 구현(EF, Wcs.Data 타입 사용)이
// 단방향 참조(Wcs.Api → Wcs.Data) 안에서 공존.
//
// 절대규칙:
//   - QueryDestination: 예약차감+piece삽입+IF05_REQ/RES(+AUTO) = 단일 DB 트랜잭션 (MINOR-6)
//   - RecordDeposit: piece 상태 전이 = 단일 트랜잭션, 멱등(부분 유니크 위반 catch → false)
//   - ICellSelector: cell_assignment 기반 점유/해제 = 트랜잭션
//   - IAgvFloorResolver: agv.floor DB 조회 — appsettings 런타임 조회 0
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// EF Core 기반 오더·목적지 리포지토리.
/// IF-05 OK 시: 예약차감 + piece 삽입 + IF05_REQ/IF05_RES piece_event + AUTO 슈트 배정(해당 시) = 단일 트랜잭션.
/// IF-05 NG 시: piece(status=DENIED, destination_id=NULL) 삽입 + IF05_REQ/IF05_RES piece_event = 단일 트랜잭션.
/// NG destination_id=NULL: 임의 fallback 제거(MINOR-5 nullable FK).
/// </summary>
public sealed class EfOrderRepository : IOrderRepository
{
    private readonly WcsDbContext _db;

    public EfOrderRepository(WcsDbContext db)
    {
        _db = db;
    }

    public (string Result, int? ChuteNo, string Reason, DestinationType? DestType, long? DestinationId) QueryDestination(
        int pId, int agvNo, string barcode, int inductionNo, int qty, string? clientTs)
    {
        // timeStamp 백필: 파싱 성공 → effective, 실패·누락 → UtcNow (Scope-3)
        var effective = ParseTimestamp(clientTs) ?? DateTime.UtcNow;

        // ── 오더 항목 조회 (바코드 → order_item → wcs_order → destination) ──
        var item = _db.OrderItems
            .Include(i => i.Order)
                .ThenInclude(o => o.Destination)
            .Where(i => i.Barcode == barcode
                     && i.Order.Status != OrderStatus.COMPLETED
                     && i.Order.Status != OrderStatus.CANCELLED)
            .FirstOrDefault();

        if (item is null)
        {
            RecordDenied(pId, agvNo, barcode, inductionNo, qty, "NO_DEST", clientTs, effective);
            return ("NG", null, "NO_DEST", null, null);
        }

        var order = item.Order;

        // ── 상태 판정 (우선순위 순) ────────────────────────────────────────
        if (order.Status == OrderStatus.COMPLETED)
        {
            RecordDenied(pId, agvNo, barcode, inductionNo, qty, "COMPLETED",
                clientTs, effective, item, order.Destination);
            return ("NG", null, "COMPLETED", ToDestType(order.Destination), null);
        }

        // PAUSED: destination status PAUSED
        if (order.Destination?.Status == DestStatus.PAUSED)
        {
            RecordDenied(pId, agvNo, barcode, inductionNo, qty, "PAUSED",
                clientTs, effective, item, order.Destination);
            return ("NG", null, "PAUSED", ToDestType(order.Destination), null);
        }

        // OVER: reserved_qty + qty > planned_qty
        if (item.ReservedQty + qty > item.PlannedQty)
        {
            RecordDenied(pId, agvNo, barcode, inductionNo, qty, "OVER",
                clientTs, effective, item, order.Destination);
            return ("NG", null, "OVER", ToDestType(order.Destination), null);
        }

        // ── 목적지 결정 ─────────────────────────────────────────────────────
        long? destId     = order.DestinationId;
        int?  chuteNo    = null;
        bool  autoAssign = false;
        Destination? dest;

        if (destId is null)
        {
            // AUTO 배정: 첫 피스에서 빈 슈트 자동 할당
            // 빈 슈트 = CHUTE + NORMAL + IsActive + 현재 RUNNING 오더가 점유하지 않는 슈트
            var usedDestIds = _db.Orders
                .Where(o => o.Status == OrderStatus.RUNNING && o.DestinationId != null)
                .Select(o => o.DestinationId!.Value)
                .ToHashSet();

            dest = _db.Destinations
                .Where(d => d.DestType == DestType.CHUTE
                         && d.Status   == DestStatus.NORMAL
                         && d.IsActive
                         && !usedDestIds.Contains(d.Id))
                .OrderBy(d => d.ChuteNo)
                .FirstOrDefault();

            if (dest is null)
            {
                RecordDenied(pId, agvNo, barcode, inductionNo, qty, "NO_DEST", clientTs, effective, item, null);
                return ("NG", null, "NO_DEST", null, null);
            }

            destId     = dest.Id;
            chuteNo    = dest.ChuteNo;
            autoAssign = true;
        }
        else
        {
            dest = order.Destination!;
            if (!dest.IsActive || dest.Status != DestStatus.NORMAL)
            {
                RecordDenied(pId, agvNo, barcode, inductionNo, qty, "NO_DEST",
                    clientTs, effective, item, dest);
                return ("NG", null, "NO_DEST", ToDestType(dest), null);
            }
            chuteNo = dest.ChuteNo;
        }

        var destApiType = ToDestType(dest);

        // ── 트랜잭션: 예약차감 + piece 삽입 + IF05_REQ/RES piece_event + AUTO 배정 반영 ─
        using var tx = _db.Database.BeginTransaction();
        try
        {
            var now = DateTime.UtcNow;

            // AUTO 배정이면 wcs_order.destination_id 갱신
            if (autoAssign)
            {
                order.DestinationId  = destId;
                order.DestAssignType = DestAssignType.AUTO;
                order.DestAssignedAt = now;
                if (order.Status == OrderStatus.WAITING)
                {
                    order.Status    = OrderStatus.RUNNING;
                    order.StartedAt = now;
                }
                order.UpdatedAt = now;
            }

            // 예약 차감
            item.ReservedQty += qty;
            item.UpdatedAt    = now;

            // p_id 순환: 기존 활성 piece 비활성화
            var prevActive = _db.Pieces.FirstOrDefault(p => p.PId == pId && p.IsActive);
            if (prevActive is not null)
            {
                prevActive.IsActive  = false;
                prevActive.UpdatedAt = now;
            }

            // agv / induction 조회
            var agv       = _db.Agvs.FirstOrDefault(a => a.AgvNo == agvNo);
            var induction = _db.Inductions.FirstOrDefault(i => i.InductionNo == inductionNo);

            // piece 삽입 (RESERVED)
            var piece = new Piece
            {
                PId           = pId,
                IsActive      = true,
                Barcode       = barcode,
                Qty           = qty,
                DepositedAt   = null,
                DestinationId = destId!.Value,
                OrderItemId   = item.Id,
                AgvId         = agv?.Id,
                InductionId   = induction?.Id,
                Status        = PieceStatus.RESERVED,
                ClientTs      = clientTs,          // RCS 원문 보존
                CreatedAt     = now,
                UpdatedAt     = now,
            };
            _db.Pieces.Add(piece);
            _db.SaveChanges();

            // piece_event: IF05_REQ + IF05_RES — 같은 트랜잭션 (MINOR-6)
            _db.PieceEvents.Add(new PieceEvent
            {
                PieceId   = piece.Id,
                EventType = PieceEventType.IF05_REQ,
                Reason    = "NORMAL",
                ClientTs  = clientTs,
                At        = effective,
            });
            _db.PieceEvents.Add(new PieceEvent
            {
                PieceId   = piece.Id,
                EventType = PieceEventType.IF05_RES,
                Reason    = "NORMAL",
                ClientTs  = clientTs,
                At        = now,
            });
            _db.SaveChanges();

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }

        return ("OK", chuteNo, "NORMAL", destApiType, destId);
    }

    // ── NG piece 삽입 헬퍼 (IF-16: NG여도 piece DENIED 기록) ──────────────
    // MINOR-5: destination_id=NULL(nullable FK) — 임의 fallback 제거.
    private void RecordDenied(int pId, int agvNo, string barcode, int inductionNo,
        int qty, string reason, string? clientTs, DateTime effective,
        DataOrderItem? item = null, DataDestination? dest = null)
    {
        using var tx = _db.Database.BeginTransaction();
        try
        {
            var now = DateTime.UtcNow;

            // 기존 활성 piece 비활성화
            var prevActive = _db.Pieces.FirstOrDefault(p => p.PId == pId && p.IsActive);
            if (prevActive is not null)
            {
                prevActive.IsActive  = false;
                prevActive.UpdatedAt = now;
            }

            var agv       = _db.Agvs.FirstOrDefault(a => a.AgvNo == agvNo);
            var induction = _db.Inductions.FirstOrDefault(i => i.InductionNo == inductionNo);

            // MINOR-5: destination_id nullable FK — dest 없으면 null(임의 fallback 제거)
            var piece = new Piece
            {
                PId           = pId,
                IsActive      = true,
                Barcode       = barcode,
                Qty           = qty,
                DepositedAt   = null,
                DestinationId = dest?.Id,   // MINOR-5: null 허용(NG DENIED)
                OrderItemId   = item?.Id,
                AgvId         = agv?.Id,
                InductionId   = induction?.Id,
                Status        = PieceStatus.DENIED,
                ClientTs      = clientTs,
                CreatedAt     = now,
                UpdatedAt     = now,
            };

            _db.Pieces.Add(piece);
            _db.SaveChanges();

            // piece_event: IF05_REQ + IF05_RES — 같은 트랜잭션 (MINOR-6)
            _db.PieceEvents.Add(new PieceEvent
            {
                PieceId   = piece.Id,
                EventType = PieceEventType.IF05_REQ,
                Reason    = reason,
                ClientTs  = clientTs,
                At        = effective,
            });
            _db.PieceEvents.Add(new PieceEvent
            {
                PieceId   = piece.Id,
                EventType = PieceEventType.IF05_RES,
                Reason    = reason,
                ClientTs  = clientTs,
                At        = now,
            });
            _db.SaveChanges();

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private static DestinationType? ToDestType(Destination? dest) =>
        dest?.DestType switch
        {
            DestType.CHUTE     => DestinationType.Chute,
            DestType.SORTER_3D => DestinationType.Sorter3D,
            _                  => null,
        };

    // ── timeStamp 백필 헬퍼 ────────────────────────────────────────────────
    /// <summary>
    /// RCS timeStamp 파싱 ("yyyy-MM-dd HH:mm:ss" 로컬).
    /// 성공 시 UTC 변환 반환, 실패·null → null(→ 호출자 UtcNow 사용).
    /// </summary>
    private static DateTime? ParseTimestamp(string? ts)
    {
        if (string.IsNullOrWhiteSpace(ts)) return null;
        return DateTime.TryParseExact(ts, "yyyy-MM-dd HH:mm:ss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var dt)
            ? dt.ToUniversalTime()
            : null;
    }
}

/// <summary>
/// EF Core 기반 투입 기록.
/// IF-10: piece 상태 전이(RESERVED→DEPOSITED) + piece_event = 단일 트랜잭션.
/// 멱등: 부분 유니크 위반(UniqueConstraintException) catch → false 반환.
/// static _recordLock 제거 — DB 레벨 부분 유니크로 진성 멱등 보장(MAJOR-1).
/// </summary>
public sealed class EfDepositRecorder : IDepositRecorder
{
    private readonly WcsDbContext _db;

    public EfDepositRecorder(WcsDbContext db)
    {
        _db = db;
    }

    public bool RecordDeposit(int pId, string barcode, int chuteNo, int agvNo, int? qty, string? clientTs)
    {
        // timeStamp 백필 (Scope-3): 파싱 성공 → effective, 실패·누락 → UtcNow
        var effective = ParseTimestamp(clientTs) ?? DateTime.UtcNow;

        // ── 멱등 체크 + 상태 전이 — 단일 트랜잭션 ─────────────────────────
        // static _recordLock 제거 — piece 부분 유니크 + catch로 진성 멱등 보장(MAJOR-1)
        using var tx = _db.Database.BeginTransaction();
        try
        {
            var piece = _db.Pieces
                .Where(p => p.PId == pId && p.IsActive)
                .OrderByDescending(p => p.Id)
                .FirstOrDefault();

            // 신규 pId(IF-05 없이 IF-10 먼저 도착 — 비정상이나 멱등 허용)
            if (piece is null)
            {
                var dest = _db.Destinations
                    .FirstOrDefault(d => d.ChuteNo == chuteNo && d.IsActive);
                if (dest is null)
                {
                    tx.Rollback();
                    return false; // 목적지 없으면 기록 불가
                }

                var now2 = DateTime.UtcNow;
                piece = new Piece
                {
                    PId           = pId,
                    IsActive      = true,
                    Barcode       = barcode,
                    Qty           = qty ?? 0,
                    DepositedAt   = effective,
                    DestinationId = dest.Id,
                    Status        = PieceStatus.DEPOSITED,
                    ClientTs      = clientTs,
                    CreatedAt     = now2,
                    UpdatedAt     = now2,
                };
                _db.Pieces.Add(piece);
                _db.SaveChanges();

                _db.PieceEvents.Add(new PieceEvent
                {
                    PieceId   = piece.Id,
                    EventType = PieceEventType.IF10_RES,
                    Reason    = "REPORTED_DIRECT",
                    ClientTs  = clientTs,
                    At        = effective,
                });
                _db.SaveChanges();
                tx.Commit();
                return true;
            }

            // 이미 DEPOSITED 이상 → 멱등(중복)
            if (piece.Status is PieceStatus.DEPOSITED or PieceStatus.CELL_ASSIGNED
                              or PieceStatus.LOADED)
            {
                tx.Rollback();
                return false;
            }

            // DENIED piece는 IF-10 도달 시도 → 멱등 false
            if (piece.Status == PieceStatus.DENIED)
            {
                tx.Rollback();
                return false;
            }

            // 상태 전이: RESERVED/QUERIED/PERMITTED → DEPOSITED
            var now3 = DateTime.UtcNow;
            piece.Status      = PieceStatus.DEPOSITED;
            piece.DepositedAt = effective;
            piece.ClientTs    = clientTs;   // 원문 보존
            piece.UpdatedAt   = now3;

            _db.PieceEvents.Add(new PieceEvent
            {
                PieceId   = piece.Id,
                EventType = PieceEventType.IF10_RES,
                ClientTs  = clientTs,
                At        = effective,
            });
            _db.SaveChanges();
            tx.Commit();
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // piece 부분 유니크 위반 → 신규 piece insert 경합만 백스톱(동시 동일 pId 1건만 전이)
            tx.Rollback();
            return false;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public bool HasDepositRecord(int pId) =>
        _db.Pieces.Any(p =>
            p.PId == pId &&
            p.IsActive &&
            (p.Status == PieceStatus.DEPOSITED ||
             p.Status == PieceStatus.CELL_ASSIGNED ||
             p.Status == PieceStatus.LOADED));

    // ── 헬퍼 ─────────────────────────────────────────────────────────────────

    /// <summary>timeStamp 파싱 — 실패·null → null.</summary>
    private static DateTime? ParseTimestamp(string? ts)
    {
        if (string.IsNullOrWhiteSpace(ts)) return null;
        return DateTime.TryParseExact(ts, "yyyy-MM-dd HH:mm:ss",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var dt)
            ? dt.ToUniversalTime()
            : null;
    }

    /// <summary>DbUpdateException이 유니크 제약 위반인지 확인. 에러 코드 기반(메시지 문자열 의존 금지).</summary>
    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        // SQLite: SqliteExtendedErrorCode == 2067 (SQLITE_CONSTRAINT_UNIQUE)
        if (ex.InnerException is Microsoft.Data.Sqlite.SqliteException sqe)
            return sqe.SqliteExtendedErrorCode == 2067;

        // SQL Server: SqlException.Number 2601(dup key row) 또는 2627(dup unique constraint)
        if (ex.InnerException is Microsoft.Data.SqlClient.SqlException sqse)
            return sqse.Number == 2601 || sqse.Number == 2627;

        return false;
    }
}

/// <summary>
/// EF Core 기반 셀 선택기.
/// 선택 우선순위: ①활성 cell_assignment 재사용(같은 오더) → ②빈 셀 할당 → ③없으면 null.
/// 점유 = released_at IS NULL.
/// </summary>
public sealed class EfCellSelector : ICellSelector
{
    private readonly WcsDbContext _db;

    public EfCellSelector(WcsDbContext db)
    {
        _db = db;
    }

    public int? SelectCell(int chuteNo, string barcode)
    {
        // ── 소터 목적지 조회 ────────────────────────────────────────────────
        var dest = _db.Destinations
            .FirstOrDefault(d => d.ChuteNo == chuteNo
                              && d.DestType == DestType.SORTER_3D
                              && d.IsActive);

        if (dest is null) return null;

        using var tx = _db.Database.BeginTransaction();
        try
        {
            var now = DateTime.UtcNow;

            // ① 같은 오더(바코드)의 활성 assignment 재사용
            var existingAssign = _db.CellAssignments
                .Include(a => a.Cell)
                .Include(a => a.Order)
                    .ThenInclude(o => o.Items)
                .Where(a => a.ReleasedAt == null
                         && a.Cell.DestinationId == dest.Id
                         && a.Order.Items.Any(i => i.Barcode == barcode))
                .FirstOrDefault();

            if (existingAssign is not null)
            {
                tx.Commit();
                return existingAssign.Cell.CellNo;
            }

            // ② 빈 셀 할당
            var occupiedCellIds = _db.CellAssignments
                .Where(a => a.Cell.DestinationId == dest.Id && a.ReleasedAt == null)
                .Select(a => a.CellId)
                .ToHashSet();

            var freeCell = _db.Cells
                .Where(c => c.DestinationId == dest.Id
                         && c.Enabled
                         && !occupiedCellIds.Contains(c.Id))
                .OrderBy(c => c.CellNo)
                .FirstOrDefault();

            if (freeCell is null)
            {
                tx.Commit();
                return null; // ③ 빈 셀 없음
            }

            // 배정할 오더 조회
            var order = _db.OrderItems
                .Include(i => i.Order)
                .Where(i => i.Barcode == barcode
                         && i.Order.DestinationId == dest.Id
                         && i.Order.Status == OrderStatus.RUNNING)
                .Select(i => i.Order)
                .FirstOrDefault();

            if (order is not null)
            {
                _db.CellAssignments.Add(new CellAssignment
                {
                    CellId     = freeCell.Id,
                    OrderId    = order.Id,
                    AssignedAt = now,
                    ReleasedAt = null,
                    CreatedAt  = now,
                });
                _db.SaveChanges();
            }

            tx.Commit();
            return freeCell.CellNo;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public void ReleaseCell(int cellNo)
    {
        using var tx = _db.Database.BeginTransaction();
        try
        {
            var now = DateTime.UtcNow;
            var assignments = _db.CellAssignments
                .Include(a => a.Cell)
                .Where(a => a.Cell.CellNo == cellNo && a.ReleasedAt == null)
                .ToList();

            foreach (var a in assignments)
                a.ReleasedAt = now;

            _db.SaveChanges();
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }
}

/// <summary>
/// EF Core 기반 agvFloor 조회 — agv.floor 단일 진실.
/// appsettings Floors:AgvNoToFloor는 시드 전용으로 강등(런타임 조회 경로 제거).
/// 매핑 없으면 null → 호출자가 400 Bad Request.
/// </summary>
public sealed class EfAgvFloorResolver : IAgvFloorResolver
{
    private readonly WcsDbContext _db;

    public EfAgvFloorResolver(WcsDbContext db)
    {
        _db = db;
    }

    /// <summary>agvNo → agv.floor DB 조회. 없으면 null → 400.</summary>
    public int? Resolve(int agvNo)
    {
        var agv = _db.Agvs.FirstOrDefault(a => a.AgvNo == agvNo && a.Enabled);
        return agv?.Floor;
    }
}

// ════════════════════════════════════════════════════════════════════════════
// EfAlarmSink — alarm 행 삽입 (S-M4-P3 갭 결선)
// API 계층 한정. 단일 WcsDbContext 스코프로 트랜잭션 기록.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// EF Core 기반 alarm 기록 구현.
/// code별 alarm 행을 단일 트랜잭션으로 삽입.
/// OFFLINE 전이당 1건·핸드셰이크 실패당 1건 — 중복 제어는 호출자(API 계층) 책임.
/// </summary>
public sealed class EfAlarmSink : IAlarmSink
{
    private readonly WcsDbContext _db;

    public EfAlarmSink(WcsDbContext db)
    {
        _db = db;
    }

    public void Append(string code, AlarmSeverity severity, long? pieceId, string message)
    {
        using var tx = _db.Database.BeginTransaction();
        try
        {
            var now = DateTime.UtcNow;
            _db.Alarms.Add(new Alarm
            {
                Code      = code,
                Severity  = severity,
                PieceId   = pieceId,
                Message   = message,
                RaisedAt  = now,
                AckedAt   = null,
                CreatedAt = now,
            });
            _db.SaveChanges();
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }
}

// ════════════════════════════════════════════════════════════════════════════
// EfSorterCommandJournal — sorter_command SENT/전이 (S-M4-P3 갭 결선)
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// EF Core 기반 sorter_command 저널 구현.
/// IF-10 핸드셰이크 시작(SENT 행 생성) + 결과 전이(COMPLETED/MISMATCH/TIMEOUT).
/// piece.status도 함께 전이: LOADED(Success) / MISMATCH / TIMEOUT.
/// sorter_command.cell_id는 cellNo로 조회한 실제 cell.id 사용.
/// </summary>
public sealed class EfSorterCommandJournal : ISorterCommandJournal
{
    private readonly WcsDbContext _db;

    public EfSorterCommandJournal(WcsDbContext db)
    {
        _db = db;
    }

    public long CreateSent(long pieceId, long cellId, int cSeq, int cellNo)
    {
        using var tx = _db.Database.BeginTransaction();
        try
        {
            var now = DateTime.UtcNow;
            var cmd = new SorterCommand
            {
                PieceId    = pieceId,
                CellId     = cellId,
                CSeq       = cSeq,
                CellNo     = cellNo,
                CWrittenAt = now,
                RSeq       = null,
                RCellNo    = null,
                RFlagAt    = null,
                Status     = SorterCommandStatus.SENT,
                CreatedAt  = now,
            };
            _db.SorterCommands.Add(cmd);
            _db.SaveChanges();
            tx.Commit();
            return cmd.Id;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    public void Finalize(long commandId, HandshakeResult result)
    {
        using var tx = _db.Database.BeginTransaction();
        try
        {
            var now = DateTime.UtcNow;

            // sorter_command 상태 전이
            // Offline·CFlagTimeout은 DB CHECK(4값: SENT/COMPLETED/MISMATCH/TIMEOUT) 제약상 TIMEOUT으로
            // 저장하되, message에 실제 사유(Outcome 이름)를 기록해 거짓 분류 방지.
            var cmd = _db.SorterCommands.Find(commandId);
            if (cmd is not null)
            {
                cmd.Status = result.Outcome switch
                {
                    HandshakeOutcome.Success      => SorterCommandStatus.COMPLETED,
                    HandshakeOutcome.RSeqMismatch => SorterCommandStatus.MISMATCH,
                    HandshakeOutcome.RFlagTimeout => SorterCommandStatus.TIMEOUT,
                    HandshakeOutcome.Offline      => SorterCommandStatus.TIMEOUT,  // OFFLINE → TIMEOUT 저장, alarm code로 구분
                    HandshakeOutcome.CFlagTimeout => SorterCommandStatus.TIMEOUT,  // CFLAG_TIMEOUT → TIMEOUT 저장, alarm code로 구분
                    _                             => SorterCommandStatus.TIMEOUT,
                };
                if (result.Outcome == HandshakeOutcome.Success)
                {
                    cmd.RSeq    = result.ReceivedRSeq;
                    cmd.RCellNo = result.ReceivedRCellNo;
                    cmd.RFlagAt = now;
                }
            }

            // piece.status 전이 (CELL_ASSIGNED/DEPOSITED → LOADED/MISMATCH/TIMEOUT)
            if (cmd is not null)
            {
                var piece = _db.Pieces.Find(cmd.PieceId);
                if (piece is not null)
                {
                    piece.Status    = result.Outcome switch
                    {
                        HandshakeOutcome.Success      => PieceStatus.LOADED,
                        HandshakeOutcome.RSeqMismatch => PieceStatus.MISMATCH,
                        HandshakeOutcome.RFlagTimeout => PieceStatus.TIMEOUT,
                        HandshakeOutcome.Offline      => PieceStatus.TIMEOUT,
                        HandshakeOutcome.CFlagTimeout => PieceStatus.TIMEOUT,
                        _                             => PieceStatus.TIMEOUT,
                    };
                    piece.UpdatedAt = now;
                }
            }

            _db.SaveChanges();
            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }
}
