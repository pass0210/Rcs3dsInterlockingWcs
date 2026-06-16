using Microsoft.EntityFrameworkCore;
using Wcs.Data;
using DataOrderItem   = Wcs.Data.OrderItem;
using DataDestination = Wcs.Data.Destination;

namespace Wcs.Api;

// ════════════════════════════════════════════════════════════════════════════
// EF Core 리포지토리 구현체 — 4개 인터페이스를 DB 트랜잭션으로 교체.
// Wcs.Api namespace에 배치: 인터페이스(Wcs.Api)와 구현(EF, Wcs.Data 타입 사용)이
// 단방향 참조(Wcs.Api → Wcs.Data) 안에서 공존.
//
// 절대규칙:
//   - QueryDestination: 예약차감+piece삽입(+AUTO) = 단일 DB 트랜잭션
//   - RecordDeposit: piece 상태 전이 = 단일 트랜잭션, 멱등(중복 pId 무해)
//   - ICellSelector: cell_assignment 기반 점유/해제 = 트랜잭션
//   - IAgvFloorResolver: agv.floor DB 조회 — appsettings 런타임 조회 0
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// EF Core 기반 오더·목적지 리포지토리.
/// IF-05 OK 시: 예약차감 + piece 삽입 + AUTO 슈트 배정(해당 시) = 단일 트랜잭션.
/// IF-05 NG 시: piece(status=DENIED) 삽입 + piece_event = 단일 트랜잭션 (예약 차감 0).
/// </summary>
public sealed class EfOrderRepository : IOrderRepository
{
    private readonly WcsDbContext _db;

    public EfOrderRepository(WcsDbContext db)
    {
        _db = db;
    }

    public (string Result, int? ChuteNo, string Reason, DestinationType? DestType) QueryDestination(
        int pId, int agvNo, string barcode, int inductionNo, int qty)
    {
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
            RecordDenied(pId, agvNo, barcode, inductionNo, qty, "NO_DEST");
            return ("NG", null, "NO_DEST", null);
        }

        var order = item.Order;

        // ── 상태 판정 (우선순위 순) ────────────────────────────────────────
        if (order.Status == OrderStatus.COMPLETED)
        {
            RecordDenied(pId, agvNo, barcode, inductionNo, qty, "COMPLETED",
                item, order.Destination);
            return ("NG", null, "COMPLETED", ToDestType(order.Destination));
        }

        // PAUSED: destination status PAUSED
        if (order.Destination?.Status == DestStatus.PAUSED)
        {
            RecordDenied(pId, agvNo, barcode, inductionNo, qty, "PAUSED",
                item, order.Destination);
            return ("NG", null, "PAUSED", ToDestType(order.Destination));
        }

        // OVER: reserved_qty + qty > planned_qty
        if (item.ReservedQty + qty > item.PlannedQty)
        {
            RecordDenied(pId, agvNo, barcode, inductionNo, qty, "OVER",
                item, order.Destination);
            return ("NG", null, "OVER", ToDestType(order.Destination));
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
                RecordDenied(pId, agvNo, barcode, inductionNo, qty, "NO_DEST", item, null);
                return ("NG", null, "NO_DEST", null);
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
                    item, dest);
                return ("NG", null, "NO_DEST", ToDestType(dest));
            }
            chuteNo = dest.ChuteNo;
        }

        var destApiType = ToDestType(dest);

        // ── 트랜잭션: 예약차감 + piece 삽입 + AUTO 배정 반영 ────────────────
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
                CreatedAt     = now,
                UpdatedAt     = now,
            };
            _db.Pieces.Add(piece);
            _db.SaveChanges();

            // piece_event: IF05_RES
            _db.PieceEvents.Add(new PieceEvent
            {
                PieceId   = piece.Id,
                EventType = PieceEventType.IF05_RES,
                Reason    = "NORMAL",
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

        return ("OK", chuteNo, "NORMAL", destApiType);
    }

    public DestinationType? GetDestType(int pId)
    {
        var piece = _db.Pieces
            .Include(p => p.Destination)
            .Where(p => p.PId == pId && p.IsActive)
            .OrderByDescending(p => p.Id)
            .FirstOrDefault();

        return ToDestType(piece?.Destination);
    }

    // ── NG piece 삽입 헬퍼 (IF-16: NG여도 piece DENIED 기록) ──────────────
    private void RecordDenied(int pId, int agvNo, string barcode, int inductionNo,
        int qty, string reason, DataOrderItem? item = null, DataDestination? dest = null)
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

            // dest가 없으면 첫 번째 활성 목적지로 fallback (스키마 NOT NULL 제약)
            long destId = dest?.Id
                ?? item?.Order.DestinationId
                ?? _db.Destinations.OrderBy(d => d.Id).First().Id;

            var piece = new Piece
            {
                PId           = pId,
                IsActive      = true,
                Barcode       = barcode,
                Qty           = qty,
                DepositedAt   = null,
                DestinationId = destId,
                OrderItemId   = item?.Id,
                AgvId         = agv?.Id,
                InductionId   = induction?.Id,
                Status        = PieceStatus.DENIED,
                CreatedAt     = now,
                UpdatedAt     = now,
            };
            _db.Pieces.Add(piece);
            _db.SaveChanges();

            // piece_event: IF05_RES (NG)
            _db.PieceEvents.Add(new PieceEvent
            {
                PieceId   = piece.Id,
                EventType = PieceEventType.IF05_RES,
                Reason    = reason,
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
}

/// <summary>
/// EF Core 기반 투입 기록.
/// IF-10: piece 상태 전이(RESERVED→DEPOSITED) + piece_event = 단일 트랜잭션.
/// 멱등: 이미 DEPOSITED 이상이면 false 반환(무해).
///
/// SQLite 동시성 주의: SQLite는 단일 writer만 허용한다.
/// 테스트 환경에서 in-memory 공유 연결로 병렬 IF-10이 들어오면 BUSY 오류 발생.
/// _recordLock으로 RecordDeposit 직렬화 — M3 InMemoryDepositRecorder의 lock(_lock) 패턴 유지.
/// 프로덕션(SQL Server)에서는 DB 레벨 트랜잭션 격리로 충분하지만 lock을 추가해도 무해.
/// </summary>
public sealed class EfDepositRecorder : IDepositRecorder
{
    private readonly WcsDbContext _db;

    // SQLite in-memory 공유 연결에서 병렬 IF-10 동시 쓰기 시 BUSY 오류 방지.
    // M3 InMemoryDepositRecorder의 lock(_lock) 패턴과 동일.
    private static readonly object _recordLock = new();

    public EfDepositRecorder(WcsDbContext db)
    {
        _db = db;
    }

    public void RecordDestinationQuery(
        int pId, int agvNo, string barcode, int inductionNo,
        int? chuteNo, int qty, DepositStatus status, string reason)
    {
        // EfOrderRepository.QueryDestination에서 이미 piece + piece_event(IF05_RES) 삽입됨.
        // 이 메서드는 호출자(Program.cs)의 계약을 충족하기 위해 존재.
        // piece가 있으면 IF05_REQ 이벤트 추가 — 없으면 무시.
        var piece = _db.Pieces
            .Where(p => p.PId == pId && p.IsActive)
            .OrderByDescending(p => p.Id)
            .FirstOrDefault();

        if (piece is null) return;

        // 이미 IF05_REQ가 있으면 중복 방지
        if (_db.PieceEvents.Any(e => e.PieceId == piece.Id && e.EventType == PieceEventType.IF05_REQ))
            return;

        var now = DateTime.UtcNow;
        _db.PieceEvents.Add(new PieceEvent
        {
            PieceId   = piece.Id,
            EventType = PieceEventType.IF05_REQ,
            Reason    = reason,
            At        = now,
        });
        _db.SaveChanges();
    }

    public bool RecordDeposit(int pId, string barcode, int chuteNo, int agvNo, int? qty)
    {
        // SQLite 단일 writer 제약: 병렬 IF-10 직렬화 (M3 lock(_lock) 패턴 유지).
        lock (_recordLock)
        {
            // ── 멱등 체크 + 상태 전이 — 단일 트랜잭션 ─────────────────────────
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
                        DepositedAt   = now2,
                        DestinationId = dest.Id,
                        Status        = PieceStatus.DEPOSITED,
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
                        At        = now2,
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
                piece.DepositedAt = now3;
                piece.UpdatedAt   = now3;

                _db.PieceEvents.Add(new PieceEvent
                {
                    PieceId   = piece.Id,
                    EventType = PieceEventType.IF10_RES,
                    At        = now3,
                });
                _db.SaveChanges();
                tx.Commit();
                return true;
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }
    }

    public bool HasDepositRecord(int pId) =>
        _db.Pieces.Any(p =>
            p.PId == pId &&
            p.IsActive &&
            (p.Status == PieceStatus.DEPOSITED ||
             p.Status == PieceStatus.CELL_ASSIGNED ||
             p.Status == PieceStatus.LOADED));

    /// <summary>pId에 해당하는 목적지 종류 반환 — IF-10 → IF-11 판단용.</summary>
    public DestinationType? GetDestType(int pId)
    {
        var piece = _db.Pieces
            .Include(p => p.Destination)
            .Where(p => p.PId == pId && p.IsActive)
            .OrderByDescending(p => p.Id)
            .FirstOrDefault();

        return piece?.Destination?.DestType switch
        {
            DestType.CHUTE     => DestinationType.Chute,
            DestType.SORTER_3D => DestinationType.Sorter3D,
            _                  => null,
        };
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
