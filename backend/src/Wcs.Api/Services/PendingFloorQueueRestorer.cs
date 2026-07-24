using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wcs.Core;
using Wcs.Data;

namespace Wcs.Api;

// ════════════════════════════════════════════════════════════════════════════
// PendingFloorQueueRestorer — I-3 소터별 pending-floor 큐 재파생 복원 (S-TWO-FLOOR-CONTROL C2 S2)
//
//   배경: SorterPendingFloorQueues 는 순수 인메모리라 WCS 재시작 시 in-flight 큐 항목이 유실된다.
//     A(PR #76)에서 재시작 복원(I-3)은 C 이연으로 문서화만 했고, C2 에서 코드 결선한다.
//
//   방식(확정 (c) A안 — piece 재파생·읽기 전용): 재시작 시 미완료 SORTER_3D piece 에서 소터별 큐를
//     재구성한다. piece 가 단일 진실(ERD) — **piece 상태를 변경하지 않는다**(순수 읽기 재구성). 큐 자체를
//     DB 로 영속화하지 않으므로 스키마 변경·마이그레이션 0(이중 진실 회피).
//
//   재파생 술어(확정 (d)): destination.DestType==SORTER_3D ∧ piece.IsActive ∧ piece.ArchivedAt==null ∧
//     Status ∈ {RESERVED, PERMITTED, DEPOSITED, CELL_ASSIGNED}(= IF-05 수용 확정 & 아직 LOADED 아님).
//     제외: DENIED(물리 라우팅 0)·LOADED/MISMATCH/TIMEOUT/CANCELLED(종료)·QUERIED(예약 전 전이상태 —
//     실 enqueue 미도달)·ArchivedAt≠null(재테스트 초기화분).
//   순서: 소터(destinationId)별 IF-05 순서(piece.CreatedAt → Id 오름차순)로 재-enqueue → 큐 머리=최선착.
//   층 F: piece → induction → inductionNo → InductionFloorMap(설정, 절대규칙 #7) 파생. 미매핑이면
//     **재편입하지 않고 경보(Fail Loud)** — 조용한 통과·기본층 폴백 금지(IF-05 IF05_NO_FLOOR 정합, 확정 (d-iii)).
//
//   기동 순서(S3): 관측 루프(SorterFloorReturnService)가 큐를 소비하기 **전에** 복원 완료해야 한다.
//     SorterFloorReturnService.StartAsync 가 관측 루프를 띄우기 전에 이 RestoreAsync 를 await 한다(복원 before 관측).
//
//   관심사 분리(절대규칙 #8): 층 파생은 순수 함수(InductionFloorMap.DeriveFloor) 그대로 재사용 — 이 클래스는
//     I/O(DB 조회)·상태 기입(큐 enqueue)만 담당하는 호출자. 순수부 diff 0.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// I-3 재파생 복원 진입점 — 관측 루프(SorterFloorReturnService)가 소비 전에 1회 호출한다.
/// 관측 루프 단위 테스트가 DB 없이 no-op 복원기를 주입할 수 있도록 인터페이스로 분리(관심사 분리).
/// </summary>
public interface IPendingFloorQueueRestorer
{
    /// <summary>미완료 SORTER_3D piece 에서 소터별 pending-floor 큐를 재구성한다(순수 읽기). 재편입 건수 반환.</summary>
    Task<int> RestoreAsync(CancellationToken ct = default);
}

/// <summary>
/// 미완료 SORTER_3D piece 에서 소터별 pending-floor 큐를 재파생(읽기 전용)해 재시작 시 유실 큐를 복원한다.
/// 싱글톤 — scoped WcsDbContext 는 IServiceScopeFactory 로 스코프 생성해 취득(captive 회피).
/// </summary>
public sealed class PendingFloorQueueRestorer : IPendingFloorQueueRestorer
{
    private readonly IServiceScopeFactory     _scopeFactory;
    private readonly SorterPendingFloorQueues _queues;
    private readonly WcsOptions               _wcsOptions;
    private readonly ILogger<PendingFloorQueueRestorer> _log;

    public PendingFloorQueueRestorer(
        IServiceScopeFactory     scopeFactory,
        SorterPendingFloorQueues queues,
        IOptions<WcsOptions>     wcsOptions,
        ILogger<PendingFloorQueueRestorer> log)
    {
        _scopeFactory = scopeFactory;
        _queues       = queues;
        _wcsOptions   = wcsOptions.Value;
        _log          = log;
    }

    /// <summary>
    /// 미완료 SORTER_3D piece 에서 소터별 pending-floor 큐를 재구성한다(관측 루프 소비 전 1회).
    /// piece 상태는 변경하지 않는다(순수 읽기). 미매핑 inductionNo piece 는 skip + 경보(Fail Loud).
    /// </summary>
    /// <returns>재편입한 큐 항목 수(진단·테스트).</returns>
    public async Task<int> RestoreAsync(CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db     = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        var opLog  = scope.ServiceProvider.GetService<IOperationLogger>();

        // ── 수용 확정·미LOADED SORTER_3D piece 조회 (IF-05 순서 = CreatedAt → Id 오름차순) ──────
        // 층 파생은 인메모리에서 하므로 여기선 inductionNo 만 함께 사영(navigation → 조인). induction 미연결
        // piece 는 inductionNo=null 로 사영돼 아래에서 미매핑과 동일하게 skip + 경보된다.
        var rows = await db.Pieces
            .Where(p => p.IsActive
                     && p.ArchivedAt == null
                     && p.DestinationId != null
                     && p.Destination!.DestType == DestType.SORTER_3D
                     && (p.Status == PieceStatus.RESERVED
                      || p.Status == PieceStatus.PERMITTED
                      || p.Status == PieceStatus.DEPOSITED
                      || p.Status == PieceStatus.CELL_ASSIGNED))
            .OrderBy(p => p.DestinationId).ThenBy(p => p.CreatedAt).ThenBy(p => p.Id)
            .Select(p => new RestoreRow(
                p.Id,
                p.PId,
                p.DestinationId!.Value,
                p.Induction != null ? (int?)p.Induction.InductionNo : null))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var map        = _wcsOptions.FloorByInduction;   // inductionNo → floor(설정, 절대규칙 #7)
        int restored   = 0;
        int unmapped   = 0;

        foreach (var r in rows)
        {
            // 층 F 재파생 — 순수 함수(InductionFloorMap.DeriveFloor). 미매핑/미연결이면 null.
            int? floor = r.InductionNo is int ind
                ? InductionFloorMap.DeriveFloor(map, ind)
                : null;

            if (floor is not int f)
            {
                // Fail Loud(확정 (d-iii)) — 재편입하지 않고 경보(WARN + operation_log). 조용한 폴백 금지.
                unmapped++;
                _log.LogWarning(
                    "[I-3복원] pId={PId} destId={DestId} inductionNo={Ind} 미매핑(InductionFloorMap 없음) — " +
                    "큐 재편입 skip(Fail-Loud)", r.PId, r.DestinationId, (object?)r.InductionNo ?? "null");
                opLog?.Log(OperationLogCategory.STATE, "I3_RESTORE_NO_FLOOR",
                    level: OperationLogLevel.WARN,
                    destinationId: r.DestinationId, pId: r.PId,
                    detail: $"{{\"inductionNo\":{(r.InductionNo.HasValue ? r.InductionNo.Value.ToString() : "null")}}}");
                continue;
            }

            _queues.Enqueue(r.DestinationId, f);
            restored++;
        }

        if (restored > 0 || unmapped > 0)
            _log.LogInformation(
                "[I-3복원] 소터 pending-floor 큐 재파생 완료 — 재편입 {Restored}건 / 미매핑 skip {Unmapped}건 " +
                "(대상 piece {Total}건, piece 상태 변경 0·읽기 재구성)",
                restored, unmapped, rows.Count);
        else
            _log.LogInformation("[I-3복원] 미완료 SORTER_3D piece 없음 — 재파생 큐 항목 0(빈 큐로 관측 시작)");

        opLog?.Log(OperationLogCategory.STATE, "I3_RESTORE",
            level: OperationLogLevel.INFO,
            detail: $"{{\"restored\":{restored},\"unmapped\":{unmapped},\"pieces\":{rows.Count}}}");

        return restored;
    }

    // piece 사영 행(EF 프로젝션 대상 — struct record).
    private readonly record struct RestoreRow(long Id, int PId, long DestinationId, int? InductionNo);
}
