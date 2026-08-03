using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
        int pId, int agvNo, string barcode, int inductionNo, int qty, string? clientTs,
        Func<long, DestinationType, DestinationBlock> availability)
    {
        // timeStamp 백필: 파싱 성공 → effective, 실패·누락 → UtcNow (Scope-3)
        var effective = ParseTimestamp(clientTs) ?? DateTime.UtcNow;

        // ── 오더 항목 조회 (바코드 → order_item → wcs_order → destination) ──
        // Fix 2(S-B2C-BARCODE-MULTI-FIX): 한 바코드가 여러 order_item 에 매칭될 때(교차-배치 중복
        //   업로드로 발생) 정렬 없는 .FirstOrDefault() 는 **비결정적**으로 미배정 오더를 골라 NG/NO_DEST 를
        //   반환할 수 있었다. 후보를 전량 materialize 한 뒤 **순수 선택 규칙**(BarcodeDestinationSelector,
        //   절대규칙 #8 — Wcs.Core·EF 무의존)으로 배정-우선·결정적으로 1건 선택한다. 단건 1:1 매치는
        //   그 후보를 그대로 반환 → 기존 동작과 동일(회귀 0). 선택 이후의 상태판정·예약차감·piece 삽입·
        //   트랜잭션·RecordDenied 경로는 전부 불변(선택만 결정적으로 교체).
        var candidateItems = _db.OrderItems
            .Include(i => i.Order)
                .ThenInclude(o => o.Destination)
            .Where(i => i.Barcode == barcode
                     && i.Order.Status != OrderStatus.COMPLETED
                     && i.Order.Status != OrderStatus.CANCELLED)
            .ToList();

        if (candidateItems.Count == 0)
        {
            RecordDenied(pId, agvNo, barcode, inductionNo, qty, "NO_DEST", clientTs, effective);
            return ("NG", null, "NO_DEST", null, null);
        }

        // 순수 선택 규칙: EF 엔티티 → 최소 projection → 결정적 선택(배정-우선·tiebreak). 후보≥1 → non-null.
        var projections = candidateItems
            .Select(i => new Wcs.Core.BarcodeDestinationCandidate(
                i.Id, i.OrderId, i.Order.DestinationId != null, i.Order.DestAssignedAt))
            .ToList();
        var chosen = Wcs.Core.BarcodeDestinationSelector.Select(projections)!;
        var item   = candidateItems.First(i => i.Id == chosen.OrderItemId);

        var order = item.Order;

        // ── 상태 판정 (우선순위 순) ────────────────────────────────────────
        if (order.Status == OrderStatus.COMPLETED)
        {
            RecordDenied(pId, agvNo, barcode, inductionNo, qty, "COMPLETED",
                clientTs, effective, item, order.Destination);
            return ("NG", null, "COMPLETED", ToDestType(order.Destination), null);
        }

        // PAUSED: destination status PAUSED — dest 타입 분기(확정4).
        //   소터(SORTER_3D)는 PAUSED → NG 유지(곧 안 풀림).
        //   슈트(CHUTE)는 PAUSED여도 통과(OK) — 곧 비워지니 보내고 대기. PAUSED 차단을 IF-05에서
        //   하지 않는다(슈트 readiness는 IF-08 푸시로 별도 전달 — IF-05 dispatch와 분리 채널).
        //   목적지 미할당(AUTO 배정 — order.Destination==null)은 NORMAL 슈트만 자동 할당되므로 무관.
        if (order.Destination?.Status == DestStatus.PAUSED
            && order.Destination.DestType == DestType.SORTER_3D)
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
            // 비활성(IsActive=false)은 슈트·소터 공통 차단(목적지 자체가 비운영 — "목적지 활성" 전제, 확정4).
            // PAUSED(Status!=NORMAL) 차단은 소터에만 적용 — 슈트 PAUSED는 통과(OK). 슈트 PAUSED는
            // 위 조기 차단(소터 한정)을 이미 통과했고 여기서도 통과시켜 OK로 배정한다.
            bool blocked = !dest.IsActive
                        || (dest.DestType == DestType.SORTER_3D && dest.Status != DestStatus.NORMAL);
            if (blocked)
            {
                RecordDenied(pId, agvNo, barcode, inductionNo, qty, "NO_DEST",
                    clientTs, effective, item, dest);
                return ("NG", null, "NO_DEST", ToDestType(dest), null);
            }
            chuteNo = dest.ChuteNo;
        }

        var destApiType = ToDestType(dest);

        // ── IF-05 상류 FULL/PAUSED 필터 (재설계 Phase 1, Scope B) ───────────────
        // FULL/PAUSED 차단을 도착 시점(폐지된 IF-08)에서 배정 시점(IF-05)으로 상류 이동.
        // 산출원은 DestinationStatusService(슈트·소터 공용) — availability 델리게이트로 주입.
        // BUSY(분류·이동 중)는 차단하지 않는다(OK·이동) — availability는 Full/Paused만 반환.
        // FULL/PAUSED면 예약하지 않고 DENIED로 기록 후 NG.
        if (destApiType.HasValue && destId.HasValue)
        {
            var block = availability(destId.Value, destApiType.Value);
            if (block != DestinationBlock.None)
            {
                var blockReason = block switch
                {
                    DestinationBlock.Full     => "FULL",
                    DestinationBlock.Paused   => "PAUSED",
                    DestinationBlock.Unmapped => "NO_FLOOR",   // 미매핑 inductionNo(층 파생 불가) — fail-loud.
                    _                         => "PAUSED",
                };
                RecordDenied(pId, agvNo, barcode, inductionNo, qty, blockReason,
                    clientTs, effective, item, dest);
                return ("NG", null, blockReason, destApiType, null);
            }
        }

        // ── 트랜잭션: 원자 예약차감 + piece 삽입 + IF05_REQ/RES piece_event + AUTO 배정 반영 ─
        // 항목①(S-AUDIT-C): 예약 차감을 추적 RMW(item.ReservedQty += qty; SaveChanges)에서
        //   **원자 조건부 UPDATE**로 교체 — `reserved_qty += qty WHERE reserved_qty + qty <= planned_qty`,
        //   영향행 0 = OVER(초과예약 차단). Finalize의 ExecuteUpdate(원자 증가) 선례(§SortedQty)를 재사용한다.
        //   효과: SQL Server rowversion 패자=미처리 500 + SQLite lost-update를 동시 해소하고,
        //         tx-밖 pre-OVER(위 :96) ↔ tx-안 차감 사이의 TOCTOU 창을 닫는다(원자 UPDATE의 WHERE가 최종 권위).
        //         재시도 불요(원자 1회 — 절대규칙 #7 하드코딩/재시도상수 0).
        // 항목②(S-AUDIT-C): 기존 활성 piece 비활성화를 FirstOrDefault 1행 → **전건**(활성·미아카이브 전체)으로.
        bool overReserved = false;
        using (var tx = _db.Database.BeginTransaction())
        {
            try
            {
                var now = DateTime.UtcNow;

                // 원자 조건부 예약 차감 — tx의 최초 write(SQLite: 쓰기락 즉시 취득 → shared→write 승격 데드락 회피).
                int affected = _db.OrderItems
                    .Where(i => i.Id == item.Id && i.ReservedQty + qty <= i.PlannedQty)
                    .ExecuteUpdate(s => s
                        .SetProperty(x => x.ReservedQty, x => x.ReservedQty + qty)
                        .SetProperty(x => x.UpdatedAt,   now));

                if (affected == 0)
                {
                    // OVER — 원자 UPDATE가 초과예약을 거부(동시 경합 패자 또는 실제 초과). tx 롤백 후
                    //   DENIED 감사기록(하드 계약)을 tx 밖에서 남긴다(RecordDenied가 자체 tx 사용 — 중첩 방지).
                    tx.Rollback();
                    overReserved = true;
                }
                else
                {
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

                    // p_id 순환: 기존 활성 piece **전건** 비활성화(항목② — 잔존 활성 piece가 IF-10을 부분유니크
                    //   위반→'멱등 OK' 위장유실로 몰던 결함 차단). S-B2C-DATAGEN: 아카이브 행 제외.
                    var prevActives = _db.Pieces
                        .Where(p => p.PId == pId && p.IsActive && p.ArchivedAt == null)
                        .ToList();
                    foreach (var pa in prevActives)
                    {
                        pa.IsActive  = false;
                        pa.UpdatedAt = now;
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
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        // OVER(원자 갱신 거부) — tx 종료 후 DENIED 감사기록(중첩 tx 방지). 어느 동시 요청도 감사 없이 500 소실 금지.
        if (overReserved)
        {
            RecordDenied(pId, agvNo, barcode, inductionNo, qty, "OVER", clientTs, effective, item, dest);
            return ("NG", null, "OVER", destApiType, null);
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

            // 기존 활성 piece **전건** 비활성화 (항목②: FirstOrDefault 1행→전건 — 잔존 활성 잔류 차단).
            //   S-B2C-DATAGEN: 아카이브 행 제외.
            var prevActives = _db.Pieces
                .Where(p => p.PId == pId && p.IsActive && p.ArchivedAt == null)
                .ToList();
            foreach (var pa in prevActives)
            {
                pa.IsActive  = false;
                pa.UpdatedAt = now;
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

// ════════════════════════════════════════════════════════════════════════════
// EfArrivalRecorder — IF-09 도착 보고 기록 (재설계 Phase 1, Scope C)
// piece_event(IF09_ARRIVAL) append-only. piece 상태 전이 없음(사용자 확정).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// EF Core 기반 IF-09 도착 보고 기록.
/// 활성 piece에 piece_event(IF09_ARRIVAL) 1행을 단일 트랜잭션으로 append.
/// piece.status는 그대로 유지(RESERVED/PERMITTED) — 도착은 기록만 남긴다.
/// </summary>
public sealed class EfArrivalRecorder : IArrivalRecorder
{
    private readonly WcsDbContext _db;

    public EfArrivalRecorder(WcsDbContext db)
    {
        _db = db;
    }

    public bool RecordArrival(int pId, int chuteNo, int agvNo, string? clientTs)
    {
        var effective = ParseTimestamp(clientTs) ?? DateTime.UtcNow;

        // 활성 piece 조회 (IF-05에서 생성된 RESERVED/PERMITTED piece).
        // S-B2C-DATAGEN: 아카이브(재테스트 초기화) 행 제외 — 옛 piece에 도착 이벤트가 붙지 않게.
        var piece = _db.Pieces
            .Where(p => p.PId == pId && p.IsActive && p.ArchivedAt == null)
            .OrderByDescending(p => p.Id)
            .FirstOrDefault();

        // IF-05 없이 IF-09 먼저 도착(비정상) — 기록 생략(상태 전이도 없음).
        if (piece is null)
            return false;

        using var tx = _db.Database.BeginTransaction();
        try
        {
            _db.PieceEvents.Add(new PieceEvent
            {
                PieceId   = piece.Id,
                EventType = PieceEventType.IF09_ARRIVAL,
                Reason    = $"chuteNo={chuteNo} agvNo={agvNo}",
                ClientTs  = clientTs,
                At        = effective,
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

    public DepositRecordResult RecordDeposit(int pId, string barcode, int chuteNo, int agvNo, int? qty, string? clientTs)
    {
        // timeStamp 백필 (Scope-3): 파싱 성공 → effective, 실패·누락 → UtcNow
        var effective = ParseTimestamp(clientTs) ?? DateTime.UtcNow;

        // ── 멱등 체크 + 상태 전이 — 단일 트랜잭션 ─────────────────────────
        // static _recordLock 제거 — piece 부분 유니크 + catch로 진성 멱등 보장(MAJOR-1)
        using var tx = _db.Database.BeginTransaction();
        try
        {
            // S-B2C-DATAGEN: 아카이브(재테스트 초기화) 행 제외 — 옛 LOADED piece를 새 IF-10이 "중복"으로 오판하지 않게.
            var piece = _db.Pieces
                .Where(p => p.PId == pId && p.IsActive && p.ArchivedAt == null)
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
                    return DepositRecordResult.NoDestination; // 활성 piece 없음 + chuteNo 목적지 없음 → 기록 불가(WARN)
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
                return DepositRecordResult.NewRecord; // 신규 pId 직삽입(IF-05 없이 IF-10 먼저) — 후속 트리거 진행
            }

            // 이미 DEPOSITED 이상 → 진짜 중복(멱등 OK·현행 INFO 유지)
            if (piece.Status is PieceStatus.DEPOSITED or PieceStatus.CELL_ASSIGNED
                              or PieceStatus.LOADED)
            {
                tx.Rollback();
                return DepositRecordResult.Duplicate;
            }

            // DENIED piece는 IF-10 도달 시도 → DENIED 재보고(차단 유지·WARN)
            if (piece.Status == PieceStatus.DENIED)
            {
                tx.Rollback();
                return DepositRecordResult.DeniedReport;
            }

            // 상태 전이 → DEPOSITED (catch-all else — 위 DEPOSITED/CELL_ASSIGNED/LOADED·DENIED 게이트를
            //   통과한 그 외 '모든' 상태가 여기로 온다). 정상 경로는 RESERVED/QUERIED/PERMITTED이나, 이 else는
            //   ⚠ 비정상 종단 상태(MISMATCH/TIMEOUT/CANCELLED 등)도 포함하며 그런 piece를 DEPOSITED로 '부활'시켜
            //   NewRecord로 반환한다. 이는 이 스프린트 이전부터의 선재(先在) 동작으로 이번엔 바이트 보존만 한다
            //   (반환 타입 bool→enum만 변경). 종단 상태 재보고를 별도 차단/원인 분리하는 실 가드는 후속 과제.
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
            return DepositRecordResult.NewRecord; // 위 게이트 밖 그 외 상태 → DEPOSITED 전이 성공(catch-all)
        }
        catch (DbUpdateException ex) when (IsUniqueConstraintViolation(ex))
        {
            // piece 부분 유니크 위반 → 신규 piece insert 경합(동시 동일 pId 1건만 전이) = 진짜 중복 백스톱
            tx.Rollback();
            return DepositRecordResult.Duplicate;
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
            p.ArchivedAt == null &&   // S-B2C-DATAGEN: 아카이브(재테스트 초기화) 행 제외.
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
/// EF Core 기반 셀 선택기 (S-CELL-ACCUM — one-order-one-cell, no overflow).
/// 배정 유무 분기:
///   ①오더가 이 소터에 활성 cell_assignment 보유 → 그 배정 셀 중 **여유 있는 셀** 재사용.
///     보유 셀이 전부 작업수량 도달이면 **null(NG)** — 빈 셀로 오버플로하지 않는다(자기 셀 국한).
///   ②배정 없음(진짜 신규 오더) → 빈 enabled 셀 신규 할당(cell_assignment 생성).
///   ③빈 셀 없음 → null.
/// 점유 = released_at IS NULL. 여유 = 현재 투입 수량(배정-기간 COMPLETED qty 합) &lt; cell.Capacity(NULL/≤0=무제한).
/// 셀 수량·용량·배정-분기 산출은 SorterCellQty 공유 — IF-05 SorterCanAcceptBarcode와 **동형**.
/// 배정은 오더 완료(SortedQty==PlannedQty, EfSorterCommandJournal.Finalize)까지 지속 — 매 투입 해제하지 않는다.
/// </summary>
public sealed class EfCellSelector : ICellSelector
{
    private readonly WcsDbContext _db;
    // 항목⑤(S-AUDIT-C): 빈 셀 분기 미매칭 시 fail-loud alarm(CELL_ORDER_UNMATCHED)를 남기기 위한 의존.
    //   IAlarmSink=EfAlarmSink(동일 스코프 WcsDbContext) — alarm 은 SelectCell 트랜잭션 종료 후 기록(중첩 tx 방지).
    private readonly IAlarmSink _alarm;
    private readonly ILogger<EfCellSelector> _log;

    public EfCellSelector(WcsDbContext db, IAlarmSink alarm, ILogger<EfCellSelector> log)
    {
        _db    = db;
        _alarm = alarm;
        _log   = log;
    }

    public int? SelectCell(int chuteNo, string barcode)
    {
        // ── 소터 목적지 조회 ────────────────────────────────────────────────
        var dest = _db.Destinations
            .FirstOrDefault(d => d.ChuteNo == chuteNo
                              && d.DestType == DestType.SORTER_3D
                              && d.IsActive);

        if (dest is null) return null;

        // 항목⑤(S-AUDIT-C): ② 빈 셀 분기에서 매칭 RUNNING 오더가 없으면 셀을 조용히 반환하던 혼적 벡터를
        //   fail-loud로 전환한다 — 셀 반환 거부(null)+WARN+alarm(CELL_ORDER_UNMATCHED). 물리 상품은 틸트 명령
        //   없이 대기(IF-11 미트리거). alarm/로그는 tx 종료 **후** 기록한다(EfAlarmSink가 자체 tx를 열므로
        //   SelectCell 트랜잭션과 중첩 방지 — 동일 스코프 WcsDbContext).
        bool unmatched = false;
        int? selected  = null;
        using (var tx = _db.Database.BeginTransaction())
        {
            try
            {
                var now = DateTime.UtcNow;

                // ① 오더가 이 소터에 활성 배정 보유 → 그 배정 셀 중 여유 있는 셀 재사용(no-overflow).
                //   SorterCellQty 공유(byte-consistent) — CellNo 오름차순 중 여유 있는 첫 셀.
                //   보유 셀 전부 작업수량 도달이면 null(NG) — ②로 폴백하지 않는다(오더는 자기 셀 하나에 국한).
                //   → IF-05 SorterCanAcceptBarcode(배정 보유 → 그 셀 여유만)와 정확히 동형.
                var assignedCells = SorterCellQty.AssignedCellsForBarcode(_db, dest.Id, barcode);
                if (assignedCells.Count > 0)
                {
                    var roomCell = SorterCellQty.FirstAssignedCellWithRoom(_db, dest.Id, assignedCells);
                    tx.Commit();
                    return roomCell?.CellNo;   // 여유 셀 재사용 / 전부 full이면 null(오버플로 금지).
                }

                // ② 배정 없음(진짜 신규) → 빈 enabled 셀 신규 할당.
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
                    return null; // ③ 빈 셀 없음(3DS FULL) — 정상 게이트(alarm 없음).
                }

                // 배정할 오더 조회
                var order = _db.OrderItems
                    .Include(i => i.Order)
                    .Where(i => i.Barcode == barcode
                             && i.Order.DestinationId == dest.Id
                             && i.Order.Status == OrderStatus.RUNNING)
                    .Select(i => i.Order)
                    .FirstOrDefault();

                if (order is null)
                {
                    // ⑤ 미매칭 fail-loud — 배정 생성 금지(빈 tx 커밋)·셀 반환 거부. alarm/로그는 tx 종료 후.
                    tx.Commit();
                    unmatched = true;
                    selected  = null;
                }
                else
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
                    tx.Commit();
                    selected = freeCell.CellNo;
                }
            }
            catch
            {
                tx.Rollback();
                throw;
            }
        }

        // ⑤ 미매칭 fail-loud 기록(tx 종료 후 — 중첩 tx 방지). ③ 빈 셀 없음(정상 FULL)과 명확히 구분:
        //   이 경로는 "빈 셀은 있으나 매칭 RUNNING 오더가 없음"(오타·REPORTED_DIRECT) → 혼적 차단.
        if (unmatched)
        {
            _log.LogWarning(
                "[IF-11] 셀 선택 미매칭 fail-loud: chuteNo={ChuteNo} barcode={Barcode} — 매칭 RUNNING 오더 없음 → 셀 반환 거부(IF-11/틸트 미트리거)",
                chuteNo, barcode);
            _alarm.Append("CELL_ORDER_UNMATCHED", AlarmSeverity.WARN, null,
                $"chuteNo={chuteNo} barcode={barcode} — 물리 투입 보고되었으나 매칭 RUNNING 오더 없음(혼적 차단·셀 미배정)");
        }

        return selected;
    }

    // ── OFFLINE 등 물리 적재 불가 시 방금 만든 신규(빈) 배정만 롤백 (S-CELL-ACCUM Scope 5) ─────
    // 그 오더(barcode)가 cellNo 셀에 보유한 활성 배정을, **현재-기간 적재가 0일 때만** release한다.
    //   · ② 신규 배정(적재 0) → orphan → release(잔존 0).
    //   · ① 누적 진행 배정(적재 ≥1) → 파기 금지(다음 piece가 같은 셀 누적).
    // destination(chuteNo) 스코프 — CellNo만으로 전 소터를 해제하던 A-7 회귀 차단(교차 소터 해제 0).
    public void ReleaseEmptyAssignment(int chuteNo, string barcode, int cellNo)
    {
        var dest = _db.Destinations
            .FirstOrDefault(d => d.ChuteNo == chuteNo
                              && d.DestType == DestType.SORTER_3D
                              && d.IsActive);
        if (dest is null) return;

        using var tx = _db.Database.BeginTransaction();
        try
        {
            var assign = _db.CellAssignments
                .Include(a => a.Cell)
                .Where(a => a.ReleasedAt == null
                         && a.Cell.DestinationId == dest.Id
                         && a.Cell.CellNo == cellNo
                         && a.Order.Items.Any(i => i.Barcode == barcode))
                .OrderByDescending(a => a.AssignedAt)
                .FirstOrDefault();

            if (assign is not null)
            {
                var loaded = SorterCellQty.LoadedQtyByCell(_db, dest.Id, new[] { assign.CellId });
                if (loaded.GetValueOrDefault(assign.CellId, 0) == 0)  // 현재-기간 적재 0 = 빈 orphan.
                {
                    assign.ReleasedAt = DateTime.UtcNow;
                    _db.SaveChanges();
                }
            }

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

    public long CreateSent(long pieceId, long cellId, int cSeq, int cellNo, DateTime? depositedAt)
    {
        using var tx = _db.Database.BeginTransaction();
        try
        {
            var now = DateTime.UtcNow;
            var cmd = new SorterCommand
            {
                PieceId     = pieceId,
                CellId      = cellId,
                CSeq        = cSeq,
                CellNo      = cellNo,
                CWrittenAt  = now,
                RSeq        = null,
                RCellNo     = null,
                // C1 처리 3시각: 투입(IF-10 보고 시각)은 행 생성 시 유입, 틸트·복귀는 Finalize(HandshakeResult).
                DepositedAt = depositedAt,
                TiltedAt    = null,
                ReturnedAt  = null,
                Status      = SorterCommandStatus.SENT,
                CreatedAt   = now,
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
                // C1 처리 시각: sortStartedAt(Ready 1→0)·tiltedAt(R_Flag==1 관측)·returnedAt(Ready 0→1)은
                //   HandshakeResult에서 유입(관측만 추가·EF 기입).
                //   sortStartedAt = 분류 시작 관측(성공·불일치 에지 관측 시 non-NULL / 미관측·타임아웃·OFFLINE NULL).
                //     → 평균 사이클 시간 = avg(returnedAt − sortStartedAt), 둘 다 non-NULL 행만 n에 포함.
                //   tiltedAt = 성공·불일치 non-NULL / 타임아웃·OFFLINE NULL(result가 규칙대로 담아 옴).
                //   returnedAt = 성공(복귀 관측)만 non-NULL / 복귀 타임아웃·그 외 NULL.
                cmd.SortStartedAt = result.SortStartedAt;
                cmd.TiltedAt      = result.TiltedAt;
                cmd.ReturnedAt    = result.ReturnedAt;
                if (result.Outcome == HandshakeOutcome.Success)
                {
                    cmd.RSeq    = result.ReceivedRSeq;
                    cmd.RCellNo = result.ReceivedRCellNo;
                }
            }

            // piece.status 전이 (CELL_ASSIGNED/DEPOSITED → LOADED/MISMATCH/TIMEOUT)
            //   + 오더완료 release 훅(S-CELL-ACCUM): Success로 **새로** LOADED된 piece만 그 오더의
            //     order_item.SortedQty를 가산하고, 오더 전량 분류 완료(전 항목 SortedQty>=PlannedQty)면
            //     OrderStatus.COMPLETED 전이 + 그 오더의 활성 cell_assignment(들)을 release한다.
            //     같은 트랜잭션(원자) — 매 투입 무조건 해제(구 콜백)를 대체. 오더 미완료 중엔 배정 지속
            //     → 다음 piece가 같은 셀 누적. 실패(MISMATCH/TIMEOUT/Offline)는 가산·release 없음(배정 유지).
            if (cmd is not null)
            {
                var piece = _db.Pieces.Find(cmd.PieceId);
                if (piece is not null)
                {
                    bool wasAlreadyLoaded = piece.Status == PieceStatus.LOADED;  // 재-Finalize 중복 가산 가드.
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

                    if (result.Outcome == HandshakeOutcome.Success
                        && !wasAlreadyLoaded
                        && piece.OrderItemId is long itemId)
                    {
                        int addQty = piece.Qty;

                        // DB-side 원자 증가 — RMW(읽고-더하고-저장) + RowVersion 충돌이 Finalize 전체를
                        //   롤백시키던 증폭 제거. 명시 트랜잭션(_db.Database.BeginTransaction)에 참여한다.
                        //   (one-order-one-cell 하에선 이 SortedQty가 배정-기간 셀 적재량과 동치 — 단, 한 오더가
                        //    여러 항목으로 한 셀을 공유하면 SortedQty는 항목별이므로 셀 적재량 == 그 오더 항목 합.)
                        _db.OrderItems
                            .Where(i => i.Id == itemId)
                            .ExecuteUpdate(s => s
                                .SetProperty(x => x.SortedQty, x => x.SortedQty + addQty)
                                .SetProperty(x => x.UpdatedAt, now));

                        // ExecuteUpdate는 추적 우회 → 완료 판정 위해 재-read(방금 원자 증가 반영·같은 tx/연결).
                        long orderId = _db.OrderItems.Where(i => i.Id == itemId).Select(i => i.OrderId).First();
                        // 오더 전량 분류 완료 = 그 오더의 모든 항목이 SortedQty >= PlannedQty.
                        bool orderComplete = !_db.OrderItems
                            .Where(i => i.OrderId == orderId)
                            .Any(i => i.SortedQty < i.PlannedQty);

                        if (orderComplete)
                        {
                            var order = _db.Orders.Find(orderId);
                            if (order is not null && order.Status != OrderStatus.COMPLETED)
                            {
                                order.Status    = OrderStatus.COMPLETED;
                                order.ClosedAt  = now;
                                order.UpdatedAt = now;
                            }

                            // 오더 스코프 release — 그 오더의 활성 배정만(orderId가 destination을 함의
                            //   → 교차 소터 해제 0, A-7 회귀 차단). 셀은 다른 오더가 재사용 가능해진다.
                            foreach (var a in _db.CellAssignments
                                         .Where(a => a.OrderId == orderId && a.ReleasedAt == null)
                                         .ToList())
                                a.ReleasedAt = now;
                        }
                    }
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
