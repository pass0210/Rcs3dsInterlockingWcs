using Microsoft.EntityFrameworkCore;
using Wcs.Data;

namespace Wcs.Api.B2C;

// ════════════════════════════════════════════════════════════════════════════
// B2cFacilityService — B2C 설비 관리(목적지 구성·셀 설정·오더 할당) — docs/B2C-FACILITY.md.
//
// 무접촉 경계: Wcs.PlcGateway·Wcs.Core 무의존. 판정/Modbus 직접 호출 0(절대규칙 #1·#8).
//   슈트 제어(clear/pause/resume)는 기존 OpsController API 를 프론트가 호출 — 이 서비스는 무관.
//   런타임 신설 CHUTE 는 ChuteCapacityService.EnsureChuteRegistered + DestinationStatusPusher.
//   RegisterDestination 으로 인메모리·푸시 추적에 편입(기동 후에도 GetHold·pause/resume·push 정상).
//
// 파괴/변경 안전(사용자 게이트):
//   OQ-1 = UI 셀 생성기(행×열 → 순차 cellNo, 스키마 무변경).
//   OQ-2 = 비활성화는 진행 중(in-flight/활성 배정) 있으면 거부·force 로만. chuteNo/타입 수정 불가.
//   OQ-3 = 미시작 오더(reserved=0·sorted=0·활성 피스 0)만 목적지 변경(할당/재배정/해제) 허용.
//   모든 액션(성공·실패·거부)을 operation_log STATE 로 전수 감사(마이그레이션 0 — 기존 STATE 재사용).
// ════════════════════════════════════════════════════════════════════════════

public interface IB2cFacilityService
{
    Task<B2cManagementResponse> CreateDestinationAsync(B2cCreateDestinationRequest req, CancellationToken ct = default);
    Task<B2cManagementResponse> SetActiveAsync(long destinationId, B2cActivateRequest req, CancellationToken ct = default);
    Task<B2cManagementResponse> UpdateDestinationAsync(long destinationId, B2cUpdateDestinationRequest req, CancellationToken ct = default);
    Task<B2cManagementResponse> ConfigureCellsAsync(long destinationId, B2cCellBulkRequest req, CancellationToken ct = default);
    Task<List<B2cOrderDto>>     GetOrdersAsync(bool? assigned, long? batchId, int take, CancellationToken ct = default);
    Task<B2cManagementResponse> AssignOrderAsync(B2cAssignOrderRequest req, CancellationToken ct = default);
    Task<B2cManagementResponse> UnassignOrderAsync(B2cUnassignOrderRequest req, CancellationToken ct = default);
}

public sealed class B2cFacilityService : IB2cFacilityService
{
    private readonly WcsDbContext          _db;
    private readonly IOperationLogger      _opLog;
    private readonly IChuteCapacityService _chuteCapacity;
    private readonly DestinationStatusPusher _pusher;   // 싱글톤 — 런타임 목적지 등록(push 추적).

    public B2cFacilityService(
        WcsDbContext            db,
        IOperationLogger        opLog,
        IChuteCapacityService   chuteCapacity,
        DestinationStatusPusher pusher)
    {
        _db            = db;
        _opLog         = opLog;
        _chuteCapacity = chuteCapacity;
        _pusher        = pusher;
    }

    // 진행 중(in-flight) piece 판정 — OQ-2/OQ-3 가드 공용(archived 제외·값변환 enum OR 표현).
    private static readonly System.Linq.Expressions.Expression<Func<Piece, bool>> IsInFlight =
        p => p.IsActive
          && p.ArchivedAt == null
          && (p.Status == PieceStatus.QUERIED
           || p.Status == PieceStatus.RESERVED
           || p.Status == PieceStatus.PERMITTED
           || p.Status == PieceStatus.CELL_ASSIGNED
           || p.Status == PieceStatus.LOADED);

    // ── 목적지 생성(소터/슈트) ───────────────────────────────────────────────────
    public async Task<B2cManagementResponse> CreateDestinationAsync(B2cCreateDestinationRequest req, CancellationToken ct = default)
    {
        if (!Enum.TryParse<DestType>(req.DestType, ignoreCase: true, out var destType)
            || !(destType == DestType.CHUTE || destType == DestType.SORTER_3D))
        {
            var msg = $"destType 는 CHUTE 또는 SORTER_3D 여야 합니다(입력='{req.DestType}').";
            Audit("B2C_DEST_CREATE", OperationLogLevel.WARN, req.ChuteNo, null, req.OperatorName, msg);
            return B2cManagementResponse.Fail(msg);
        }

        var op  = Op(req.OperatorName);
        var now = DateTime.UtcNow;

        await using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            // chuteNo 중복 차단(UNIQUE) — 어떤 타입이든 이미 있으면 F.
            var dup = await _db.Destinations.FirstOrDefaultAsync(d => d.ChuteNo == req.ChuteNo, ct).ConfigureAwait(false);
            if (dup is not null)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                var msg = $"chuteNo={req.ChuteNo} 는 이미 존재합니다({dup.DestType}).";
                Audit("B2C_DEST_CREATE", OperationLogLevel.WARN, req.ChuteNo, dup.Id, op, msg);
                return B2cManagementResponse.Fail(msg);
            }

            int? floor    = destType == DestType.CHUTE ? req.Floor : null;   // 소터는 층 무관(NULL).
            int workFull  = req.WorkFullQty ?? B2cConstants.DefaultWorkFullQty;

            var dest = new Destination
            {
                ChuteNo   = req.ChuteNo,
                DestType  = destType,
                Floor     = floor,
                Status    = DestStatus.NORMAL,
                IsActive  = true,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.Destinations.Add(dest);
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            int chuteDetailCreated = 0;
            if (destType == DestType.CHUTE)
            {
                _db.ChuteDetails.Add(new ChuteDetail
                {
                    DestinationId  = dest.Id,
                    DefaultFullQty = workFull,
                    WorkFullQty    = workFull,
                    PrinterId      = null,
                    LastClearedAt  = null,
                    Zone           = null,
                    CreatedAt      = now,
                    UpdatedAt      = now,
                });
                await _db.SaveChangesAsync(ct).ConfigureAwait(false);
                chuteDetailCreated = 1;
            }

            // destination_event(감사 정본 — operator 컬럼 보유). 신설 이벤트 타입은 없으므로 상세 JSON 로.
            _db.DestinationEvents.Add(new DestinationEvent
            {
                DestinationId = dest.Id,
                EventType     = DestinationEventType.FULL_QTY_CHANGED,   // 신설: workFullQty 확정 이벤트로 대용(마이그레이션 0).
                OperatorId    = op,
                DetailJson    = $"{{\"action\":\"CREATE\",\"destType\":\"{destType}\",\"chuteNo\":{req.ChuteNo},\"workFullQty\":{workFull}}}",
                At            = now,
            });
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);

            await tx.CommitAsync(ct).ConfigureAwait(false);

            // ── 런타임 반영(CHUTE) — 기동 후에도 GetHold·pause/resume·IF-08 push 정상 동작 ──
            //   순서: capacity 먼저(GetHold 재료) → pusher(부트스트랩 push 가 올바른 accept 산출).
            if (destType == DestType.CHUTE)
            {
                _chuteCapacity.EnsureChuteRegistered(dest.Id, workFull, isActive: true, isPaused: false);
                _pusher.RegisterDestination(dest.Id, dest.ChuteNo, DestType.CHUTE);
            }
            // SORTER_3D: 폴링/핸드셰이크는 기동 시 appsettings Sorters[] ∩ DB 로 구성 →
            //   런타임 신설 소터는 **재기동 + appsettings 항목** 후에야 폴링 시작(DB 레코드 수준까지만 즉시 반영).

            var counts = new Dictionary<string, int>
            {
                ["destinationCreated"] = 1,
                ["chuteDetailCreated"] = chuteDetailCreated,
            };
            var note = destType == DestType.SORTER_3D
                ? " (소터 폴링은 appsettings Sorters[] 항목 추가 + 재기동 후 시작)"
                : "";
            Audit("B2C_DEST_CREATE", OperationLogLevel.INFO, req.ChuteNo, dest.Id, op,
                $"{{\"destType\":\"{destType}\",\"chuteNo\":{req.ChuteNo},\"workFullQty\":{workFull}}}");
            return B2cManagementResponse.Ok(
                $"목적지 생성 완료 — {destType} chuteNo={req.ChuteNo}{note}.", counts);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    // ── 목적지 활성/비활성 토글 (OQ-2 비활성화 가드) ──────────────────────────────
    public async Task<B2cManagementResponse> SetActiveAsync(long destinationId, B2cActivateRequest req, CancellationToken ct = default)
    {
        var op   = Op(req.OperatorName);
        var dest = await _db.Destinations.FirstOrDefaultAsync(d => d.Id == destinationId, ct).ConfigureAwait(false);
        if (dest is null)
        {
            var msg = $"목적지(id={destinationId})를 찾을 수 없습니다.";
            Audit("B2C_DEST_ACTIVATE", OperationLogLevel.WARN, null, destinationId, op, msg);
            return B2cManagementResponse.Fail(msg);
        }

        var action = req.IsActive ? "B2C_DEST_ACTIVATE" : "B2C_DEST_DEACTIVATE";

        await using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            // ── 비활성화 가드(OQ-2) — 진행 중이면 거부·force 로만 강행. 활성화는 무가드 ──
            if (!req.IsActive)
            {
                int inFlight = await _db.Pieces
                    .Where(p => p.DestinationId == destinationId).Where(IsInFlight)
                    .CountAsync(ct).ConfigureAwait(false);
                int activeAssignments = await _db.CellAssignments
                    .CountAsync(a => a.ReleasedAt == null && a.Cell.DestinationId == destinationId, ct)
                    .ConfigureAwait(false);

                if ((inFlight > 0 || activeAssignments > 0) && !req.Force)
                {
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    var msg = $"진행 중 작업이 있어 비활성화를 거부했습니다(in-flight {inFlight}·활성 배정 {activeAssignments}). "
                            + "강제 비활성화하려면 force=true 로 재요청하세요.";
                    Audit(action, OperationLogLevel.WARN, dest.ChuteNo, destinationId, op,
                        $"{{\"refused\":true,\"inFlight\":{inFlight},\"activeAssignments\":{activeAssignments},\"force\":false}}");
                    return B2cManagementResponse.Fail(msg,
                        new Dictionary<string, int> { ["inFlight"] = inFlight, ["activeAssignments"] = activeAssignments });
                }
            }

            if (dest.IsActive == req.IsActive)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                Audit(action, OperationLogLevel.INFO, dest.ChuteNo, destinationId, op, "{\"noop\":true}");
                return B2cManagementResponse.Ok($"이미 {(req.IsActive ? "활성" : "비활성")} 상태입니다(변경 없음).");
            }

            bool wasForced = !req.IsActive && req.Force;
            dest.IsActive  = req.IsActive;
            dest.UpdatedAt = DateTime.UtcNow;

            _db.DestinationEvents.Add(new DestinationEvent
            {
                DestinationId = destinationId,
                EventType     = DestinationEventType.CLOSED,   // 활성 전이 감사(마이그레이션 0 — 기존 타입 재사용).
                OperatorId    = op,
                DetailJson    = $"{{\"isActive\":{(req.IsActive ? "true" : "false")},\"force\":{(wasForced ? "true" : "false")}}}",
                At            = dest.UpdatedAt,
            });
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);

            // ── FIX ITER 3: 커밋 후 인메모리 IsActive 반영(CHUTE) — 레지스트리 일관성 ──
            //   비활성화 → GetHold=Paused(투입 차단) + IF-08 push 수용불가(2). 활성화 → 수용가능(3).
            //   S-IF08 단일-emit 불변 보존: OnChuteStateChanged → Pusher Observe→Pump 단일 경로 경유.
            //   소터는 no-op(DestinationStatusService.ComputeSorter 가 DB IsActive 직독 + 관찰 타이머 재평가).
            if (dest.DestType == DestType.CHUTE)
                _chuteCapacity.ApplyActiveStateInMemory(destinationId, req.IsActive);

            Audit(action, wasForced ? OperationLogLevel.WARN : OperationLogLevel.INFO, dest.ChuteNo, destinationId, op,
                $"{{\"isActive\":{(req.IsActive ? "true" : "false")},\"force\":{(wasForced ? "true" : "false")}}}");
            return B2cManagementResponse.Ok(
                $"목적지 chuteNo={dest.ChuteNo} {(req.IsActive ? "활성화" : "비활성화")} 완료{(wasForced ? " (강제)" : "")}."
              + " (비활성 목적지는 IF-05 라우팅에서 차단됩니다.)");
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    // ── 목적지 수정(status/floor/workFullQty) ────────────────────────────────────
    public async Task<B2cManagementResponse> UpdateDestinationAsync(long destinationId, B2cUpdateDestinationRequest req, CancellationToken ct = default)
    {
        var op   = Op(req.OperatorName);
        var dest = await _db.Destinations.FirstOrDefaultAsync(d => d.Id == destinationId, ct).ConfigureAwait(false);
        if (dest is null)
        {
            var msg = $"목적지(id={destinationId})를 찾을 수 없습니다.";
            Audit("B2C_DEST_UPDATE", OperationLogLevel.WARN, null, destinationId, op, msg);
            return B2cManagementResponse.Fail(msg);
        }

        DestStatus? newStatus = null;
        if (!string.IsNullOrWhiteSpace(req.Status))
        {
            if (!Enum.TryParse<DestStatus>(req.Status, ignoreCase: true, out var parsed))
                return B2cManagementResponse.Fail($"status 는 NORMAL 또는 PAUSED 여야 합니다(입력='{req.Status}').");
            newStatus = parsed;
        }

        var now = DateTime.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            if (newStatus.HasValue) dest.Status = newStatus.Value;
            if (req.Floor.HasValue && dest.DestType == DestType.CHUTE) dest.Floor = req.Floor.Value;
            dest.UpdatedAt = now;

            int workFullUpdated = 0;
            if (req.WorkFullQty.HasValue && dest.DestType == DestType.CHUTE)
            {
                var detail = await _db.ChuteDetails.FirstOrDefaultAsync(cd => cd.DestinationId == destinationId, ct).ConfigureAwait(false);
                if (detail is not null)
                {
                    detail.WorkFullQty = req.WorkFullQty.Value;
                    detail.UpdatedAt   = now;
                    workFullUpdated = 1;
                }
            }

            _db.DestinationEvents.Add(new DestinationEvent
            {
                DestinationId = destinationId,
                EventType     = DestinationEventType.FULL_QTY_CHANGED,
                OperatorId    = op,
                DetailJson    = $"{{\"action\":\"UPDATE\",\"status\":\"{dest.Status}\",\"floor\":{(dest.Floor?.ToString() ?? "null")},\"workFullUpdated\":{workFullUpdated}}}",
                At            = now,
            });
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);

            Audit("B2C_DEST_UPDATE", OperationLogLevel.INFO, dest.ChuteNo, destinationId, op,
                $"{{\"status\":\"{dest.Status}\",\"workFullUpdated\":{workFullUpdated}}}");
            var note = req.WorkFullQty.HasValue
                ? " (만재 임계 변경은 재기동 후 인메모리 집계에 완전 반영됩니다.)" : "";
            return B2cManagementResponse.Ok($"목적지 chuteNo={dest.ChuteNo} 수정 완료.{note}",
                new Dictionary<string, int> { ["workFullUpdated"] = workFullUpdated });
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    // ── 소터 셀 벌크 설정(행×열 → 순차 cellNo · OQ-1) ────────────────────────────
    public async Task<B2cManagementResponse> ConfigureCellsAsync(long destinationId, B2cCellBulkRequest req, CancellationToken ct = default)
    {
        var op   = Op(req.OperatorName);
        var dest = await _db.Destinations.FirstOrDefaultAsync(d => d.Id == destinationId, ct).ConfigureAwait(false);
        if (dest is null || dest.DestType != DestType.SORTER_3D)
        {
            var msg = $"SORTER_3D 목적지(id={destinationId})를 찾을 수 없습니다(셀 설정은 소터 전용).";
            Audit("B2C_SORTER_CELLS", OperationLogLevel.WARN, dest?.ChuteNo, destinationId, op, msg);
            return B2cManagementResponse.Fail(msg);
        }

        int total = req.Rows * req.Cols;
        if (total < 1 || total > B2cConstants.CellCountMax)
            return B2cManagementResponse.Fail($"행×열 총 셀 수는 1~{B2cConstants.CellCountMax} 여야 합니다(요청 {req.Rows}×{req.Cols}={total}).");

        int capacity = req.Capacity ?? B2cConstants.DefaultCellCapacity;
        var now = DateTime.UtcNow;

        await using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var existing = await _db.Cells
                .Where(c => c.DestinationId == destinationId)
                .ToDictionaryAsync(c => c.CellNo, ct).ConfigureAwait(false);

            int created = 0, updated = 0;
            for (int cellNo = 1; cellNo <= total; cellNo++)
            {
                if (existing.TryGetValue(cellNo, out var cell))
                {
                    if (cell.Capacity != capacity || cell.Enabled != req.Enabled)
                    {
                        cell.Capacity = capacity;
                        cell.Enabled  = req.Enabled;
                        updated++;
                    }
                }
                else
                {
                    _db.Cells.Add(new Cell
                    {
                        DestinationId = destinationId, CellNo = cellNo,
                        Capacity = capacity, Enabled = req.Enabled, CreatedAt = now,
                    });
                    created++;
                }
            }
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);

            int cellTotal = await _db.Cells.CountAsync(c => c.DestinationId == destinationId, ct).ConfigureAwait(false);
            var counts = new Dictionary<string, int>
            {
                ["cellsCreated"] = created,
                ["cellsUpdated"] = updated,
                ["cellTotal"]    = cellTotal,
            };
            Audit("B2C_SORTER_CELLS", OperationLogLevel.INFO, dest.ChuteNo, destinationId, op,
                $"{{\"rows\":{req.Rows},\"cols\":{req.Cols},\"total\":{total},\"created\":{created},\"updated\":{updated},\"capacity\":{capacity},\"enabled\":{(req.Enabled ? "true" : "false")}}}");
            return B2cManagementResponse.Ok(
                $"셀 설정 완료 — 소터 chuteNo={dest.ChuteNo}: {req.Rows}행×{req.Cols}열={total}셀(신규 {created}·보정 {updated}, 총 {cellTotal}).", counts);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    // ── 오더 목록(할당 UI 소스) ──────────────────────────────────────────────────
    public async Task<List<B2cOrderDto>> GetOrdersAsync(bool? assigned, long? batchId, int take, CancellationToken ct = default)
    {
        var q = _db.Orders.AsNoTracking().AsQueryable();
        if (assigned == true)  q = q.Where(o => o.DestinationId != null);
        if (assigned == false) q = q.Where(o => o.DestinationId == null);
        if (batchId.HasValue)  q = q.Where(o => o.WorkBatchId == batchId.Value);

        // 값변환 enum(Status·DestType·AssignType)·집계는 익명형으로 materialize 후 C#에서 조립.
        var rows = await q
            .OrderByDescending(o => o.Id)
            .Take(take)
            .Select(o => new
            {
                o.Id,
                o.OrderNo,
                o.WorkBatchId,
                BatchWorkDate = o.WorkBatch.WorkDate,
                BatchNo       = o.WorkBatch.BatchNo,
                BatchWave     = o.WorkBatch.WaveNo,
                o.Status,
                o.DestinationId,
                DestChuteNo   = o.Destination != null ? (int?)o.Destination.ChuteNo : null,
                DestType      = o.Destination != null ? (DestType?)o.Destination.DestType : null,
                o.DestAssignType,
                Barcode  = o.Items.Select(i => i.Barcode).FirstOrDefault(),
                Planned  = o.Items.Sum(i => (int?)i.PlannedQty)  ?? 0,
                Reserved = o.Items.Sum(i => (int?)i.ReservedQty) ?? 0,
                Sorted   = o.Items.Sum(i => (int?)i.SortedQty)   ?? 0,
                // 활성 배정 셀(소터) — released_at IS NULL.
                AssignedCellNo = o.CellAssignments
                    .Where(a => a.ReleasedAt == null)
                    .Select(a => (int?)a.Cell.CellNo)
                    .FirstOrDefault(),
                // 차단 piece 존재 — order_item 경유. OQ-3 보완: DENIED(물리 라우팅 0)는 제외(재할당 허용).
                HasActivePiece = o.Items.Any(i => i.Pieces.Any(p =>
                    p.IsActive && p.ArchivedAt == null && p.Status != PieceStatus.DENIED)),
            })
            .ToListAsync(ct).ConfigureAwait(false);

        return rows.Select(o =>
        {
            bool canReassign = o.Reserved == 0 && o.Sorted == 0 && !o.HasActivePiece;
            return new B2cOrderDto(
                o.Id, o.OrderNo, o.WorkBatchId,
                $"{o.BatchWorkDate:yyyy-MM-dd} / {o.BatchNo} #{o.BatchWave}",
                o.Barcode ?? o.OrderNo,
                o.Planned, o.Reserved, o.Sorted,
                o.Status.ToString(),
                o.DestinationId, o.DestChuteNo, o.DestType?.ToString(), o.DestAssignType?.ToString(),
                o.AssignedCellNo, o.HasActivePiece, canReassign);
        }).ToList();
    }

    // ── 오더 → 목적지(+셀) 할당/재배정 (OQ-3 미시작 가드) ─────────────────────────
    public async Task<B2cManagementResponse> AssignOrderAsync(B2cAssignOrderRequest req, CancellationToken ct = default)
    {
        var op = Op(req.OperatorName);

        var order = await _db.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == req.OrderId, ct).ConfigureAwait(false);
        if (order is null)
        {
            var msg = $"오더(id={req.OrderId})를 찾을 수 없습니다.";
            Audit("B2C_ORDER_ASSIGN", OperationLogLevel.WARN, null, null, op, msg);
            return B2cManagementResponse.Fail(msg);
        }

        var dest = await _db.Destinations.FirstOrDefaultAsync(d => d.Id == req.DestinationId, ct).ConfigureAwait(false);
        if (dest is null)
        {
            var msg = $"목적지(id={req.DestinationId})를 찾을 수 없습니다.";
            Audit("B2C_ORDER_ASSIGN", OperationLogLevel.WARN, null, req.DestinationId, op, msg);
            return B2cManagementResponse.Fail(msg);
        }
        if (!dest.IsActive)
            return B2cManagementResponse.Fail($"목적지 chuteNo={dest.ChuteNo} 가 비활성 상태입니다(활성화 후 배정).");

        // CHUTE 는 셀 지정 불가.
        if (dest.DestType == DestType.CHUTE && req.CellNo.HasValue)
            return B2cManagementResponse.Fail("슈트(CHUTE)에는 셀을 지정할 수 없습니다(cellNo 제거).");

        await using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            // ── OQ-3 미시작 가드 — 트랜잭션 안(TOCTOU 협착·B2C-DATAGEN 선례) ──
            var (reserved, sorted, hasPiece) = await OrderProgressAsync(order.Id, ct).ConfigureAwait(false);
            if (reserved != 0 || sorted != 0 || hasPiece)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                var msg = $"진행 중 오더는 재배정할 수 없습니다(예약 {reserved}·분류 {sorted}·활성 피스 {(hasPiece ? "있음" : "없음")}). 미시작 오더만 배정 가능합니다(OQ-3).";
                Audit("B2C_ORDER_ASSIGN", OperationLogLevel.WARN, dest.ChuteNo, dest.Id, op,
                    $"{{\"refused\":true,\"orderId\":{order.Id},\"reserved\":{reserved},\"sorted\":{sorted},\"hasPiece\":{(hasPiece ? "true" : "false")}}}");
                return B2cManagementResponse.Fail(msg,
                    new Dictionary<string, int> { ["reserved"] = reserved, ["sorted"] = sorted });
            }

            long? cellId = null;
            if (dest.DestType == DestType.SORTER_3D && req.CellNo.HasValue)
            {
                var cell = await _db.Cells
                    .FirstOrDefaultAsync(c => c.DestinationId == dest.Id && c.CellNo == req.CellNo.Value, ct).ConfigureAwait(false);
                if (cell is null)
                {
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    return B2cManagementResponse.Fail($"소터 chuteNo={dest.ChuteNo} 에 셀 {req.CellNo} 이(가) 없습니다.");
                }
                // 그 셀에 다른 오더의 활성 배정이 있으면 F(부분 유니크 (cell_id) WHERE released_at IS NULL).
                bool occupiedByOther = await _db.CellAssignments
                    .AnyAsync(a => a.CellId == cell.Id && a.ReleasedAt == null && a.OrderId != order.Id, ct).ConfigureAwait(false);
                if (occupiedByOther)
                {
                    await tx.RollbackAsync(ct).ConfigureAwait(false);
                    return B2cManagementResponse.Fail($"셀 {req.CellNo} 은(는) 다른 오더가 점유 중입니다.");
                }
                cellId = cell.Id;
            }

            var now = DateTime.UtcNow;

            // 기존 활성 배정 해제(재배정) — 미시작 오더라 적재 이력 없음(안전).
            int released = 0;
            var activeAssignments = await _db.CellAssignments
                .Where(a => a.OrderId == order.Id && a.ReleasedAt == null)
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var a in activeAssignments)
            {
                if (cellId.HasValue && a.CellId == cellId.Value) continue;   // 같은 셀 재배정은 유지.
                a.ReleasedAt = now;
                released++;
            }

            order.DestinationId  = dest.Id;
            order.DestAssignType = DestAssignType.MANUAL;
            order.DestAssignedAt = now;
            order.UpdatedAt      = now;

            int cellAssigned = 0;
            if (cellId.HasValue)
            {
                bool alreadyAssignedSameCell = activeAssignments.Any(a => a.CellId == cellId.Value && a.ReleasedAt == null);
                if (!alreadyAssignedSameCell)
                {
                    _db.CellAssignments.Add(new CellAssignment
                    {
                        CellId = cellId.Value, OrderId = order.Id, AssignedAt = now, ReleasedAt = null, CreatedAt = now,
                    });
                    cellAssigned = 1;
                }
            }

            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);

            var counts = new Dictionary<string, int>
            {
                ["assigned"]              = 1,
                ["cellAssignmentCreated"] = cellAssigned,
                ["releasedAssignments"]   = released,
            };
            Audit("B2C_ORDER_ASSIGN", OperationLogLevel.INFO, dest.ChuteNo, dest.Id, op,
                $"{{\"orderId\":{order.Id},\"orderNo\":\"{Esc(order.OrderNo)}\",\"destType\":\"{dest.DestType}\",\"cellNo\":{(req.CellNo?.ToString() ?? "null")}}}");
            var cellNote = cellId.HasValue ? $" · 셀 {req.CellNo}" : "";
            return B2cManagementResponse.Ok(
                $"오더 {order.OrderNo} → {dest.DestType} chuteNo={dest.ChuteNo}{cellNote} 배정 완료.", counts);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    // ── 오더 목적지 할당 해제 (OQ-3 미시작 가드) ──────────────────────────────────
    public async Task<B2cManagementResponse> UnassignOrderAsync(B2cUnassignOrderRequest req, CancellationToken ct = default)
    {
        var op = Op(req.OperatorName);
        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == req.OrderId, ct).ConfigureAwait(false);
        if (order is null)
        {
            var msg = $"오더(id={req.OrderId})를 찾을 수 없습니다.";
            Audit("B2C_ORDER_UNASSIGN", OperationLogLevel.WARN, null, null, op, msg);
            return B2cManagementResponse.Fail(msg);
        }
        if (order.DestinationId is null)
            return B2cManagementResponse.Ok("이미 미할당 상태입니다(변경 없음).");

        await using var tx = await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        try
        {
            var (reserved, sorted, hasPiece) = await OrderProgressAsync(order.Id, ct).ConfigureAwait(false);
            if (reserved != 0 || sorted != 0 || hasPiece)
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                var msg = $"진행 중 오더는 할당 해제할 수 없습니다(예약 {reserved}·분류 {sorted}). 미시작 오더만 가능합니다(OQ-3).";
                Audit("B2C_ORDER_UNASSIGN", OperationLogLevel.WARN, null, order.DestinationId, op,
                    $"{{\"refused\":true,\"orderId\":{order.Id},\"reserved\":{reserved},\"sorted\":{sorted}}}");
                return B2cManagementResponse.Fail(msg,
                    new Dictionary<string, int> { ["reserved"] = reserved, ["sorted"] = sorted });
            }

            var now = DateTime.UtcNow;
            int released = 0;
            var activeAssignments = await _db.CellAssignments
                .Where(a => a.OrderId == order.Id && a.ReleasedAt == null)
                .ToListAsync(ct).ConfigureAwait(false);
            foreach (var a in activeAssignments) { a.ReleasedAt = now; released++; }

            long? prevDest = order.DestinationId;
            order.DestinationId  = null;
            order.DestAssignType = null;
            order.DestAssignedAt = null;
            order.UpdatedAt      = now;

            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);

            var counts = new Dictionary<string, int> { ["unassigned"] = 1, ["releasedAssignments"] = released };
            Audit("B2C_ORDER_UNASSIGN", OperationLogLevel.INFO, null, prevDest, op,
                $"{{\"orderId\":{order.Id},\"orderNo\":\"{Esc(order.OrderNo)}\",\"released\":{released}}}");
            return B2cManagementResponse.Ok($"오더 {order.OrderNo} 할당 해제 완료.", counts);
        }
        catch
        {
            await tx.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }

    // ── 헬퍼 ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// 오더 진행도(예약 합·분류 합·차단 피스 존재) — OQ-3 가드 공용.
    /// ★ OQ-3 보완(사용자 게이트 2026-07-14): DENIED 피스는 물리 라우팅이 0(RCS 선조회 NO_DEST 등 —
    ///   실제 목적지로 이동/적재된 적 없음)이므로 "피스 이력"에 카운트하지 않는다. DENIED 기록만 있는
    ///   오더는 후할당/재할당을 허용한다(선조회 NG → 운영자 수동 배정 복구 흐름 성립). 예약/적재/진행 등
    ///   비-DENIED 활성 피스가 있으면 기존대로 차단.
    /// </summary>
    private async Task<(int Reserved, int Sorted, bool HasActivePiece)> OrderProgressAsync(long orderId, CancellationToken ct)
    {
        int reserved = await _db.OrderItems.Where(i => i.OrderId == orderId)
            .SumAsync(i => (int?)i.ReservedQty, ct).ConfigureAwait(false) ?? 0;
        int sorted = await _db.OrderItems.Where(i => i.OrderId == orderId)
            .SumAsync(i => (int?)i.SortedQty, ct).ConfigureAwait(false) ?? 0;
        bool hasPiece = await _db.Pieces
            .AnyAsync(p => p.OrderItem != null && p.OrderItem.OrderId == orderId
                        && p.IsActive && p.ArchivedAt == null
                        && p.Status != PieceStatus.DENIED, ct).ConfigureAwait(false);
        return (reserved, sorted, hasPiece);
    }

    private static string Op(string? operatorName) =>
        string.IsNullOrWhiteSpace(operatorName) ? "(unspecified)" : operatorName.Trim();

    // 감사(operation_log STATE) — ★ 작업자(op)를 **모든** detail JSON 에 항상 병합한다(계약 §3/§11.5 —
    //   성공·거부·감사 전수 작업자 귀속). operation_log 는 논블로킹 채널이라 트랜잭션 롤백(거부)과 무관하게
    //   기록된다 → 거부 감사도 op 를 실어 보존. destination_event(operator_id 컬럼)는 목적지 라이프사이클
    //   전이(create/activate/update)의 정규 감사이나 트랜잭션 참여라 거부 시 롤백되고, cells/assign/unassign
    //   은 대응 DestinationEventType 부재로 append 하지 않으므로, **operation_log 가 이들의 정본 작업자 감사**다.
    private void Audit(string action, OperationLogLevel level, int? sorterChuteNo, long? destinationId, string op, string detail)
        => _opLog.Log(OperationLogCategory.STATE, action, level,
            sorterChuteNo: sorterChuteNo, destinationId: destinationId,
            detail: MergeOp(op, detail));

    /// <summary>detail 에 "op" 를 항상 병합. JSON 객체면 선두 삽입, 평문이면 {op,msg} 로 래핑.</summary>
    private static string MergeOp(string op, string detail)
    {
        var trimmed = detail.TrimStart();
        if (!trimmed.StartsWith("{"))
            return $"{{\"op\":\"{Esc(op)}\",\"msg\":\"{Esc(detail)}\"}}";

        var inner = trimmed.Substring(1).TrimStart();   // '{' 제거 후 나머지
        // 빈 객체("{}") → {"op":"..."} / 그 외 → {"op":"...",<나머지>}
        return inner.StartsWith("}")
            ? $"{{\"op\":\"{Esc(op)}\"{inner}"
            : $"{{\"op\":\"{Esc(op)}\",{inner}";
    }

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
