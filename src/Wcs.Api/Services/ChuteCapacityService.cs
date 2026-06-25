using Microsoft.EntityFrameworkCore;
using Wcs.Core;
using Wcs.Data;

namespace Wcs.Api;

// ════════════════════════════════════════════════════════════════════════════
// ChuteCapacityService — FULL/PAUSED 인메모리 집계 서비스.
//
// ERD §7: FULL 계산 = SUM(piece.qty WHERE deposited_at > chute_detail.last_cleared_at)
//         + 이동중 예약 qty(RESERVED/PERMITTED 활성 piece qty)
//         >= work_full_qty → Full(qty>1 가능, COUNT 아님)
// PAUSED = destination.status == PAUSED
//
// 싱글톤: 기동 시 DB로 재구성 → IF-05 예약(+)·IF-10 투입·비움(리셋) 이벤트로 증감.
// cur_qty 컬럼 금지 — piece 테이블이 단일 진실.
// Hold 우선순위(SPEC §2): Full+Paused 동시 → Full 우선.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// 슈트 FULL/PAUSED 상태 조회 인터페이스.
/// IF-08 핸들러가 chuteNo로 WcsHold 산출에 사용.
/// </summary>
public interface IChuteCapacityService
{
    /// <summary>
    /// destination.id에 해당하는 슈트의 WcsHold(None/Full/Paused) 반환.
    /// 비소터(CHUTE) 전용 — SORTER_3D에는 사용 불가(SPEC §2 분기).
    /// 비활성(IsActive=false) 또는 비존재 목적지 → Paused 매핑.
    /// </summary>
    WcsHold GetHold(long destinationId);

    /// <summary>
    /// IF-05 OK 예약 발생 시 인메모리 카운터 증가.
    /// qty: 예약된 피스 qty.
    /// </summary>
    void OnReserved(long destinationId, int qty);

    /// <summary>
    /// IF-10 투입 확정 시 예약→적재 전환(예약 qty 차감, 적재 qty 증가).
    /// reserved에서 차감하고 deposited로 전환 — 총 집계는 동일.
    /// </summary>
    void OnDeposited(long destinationId, int qty);

    /// <summary>
    /// IF-05 NG·취소 시 예약 해제(예약 qty 차감).
    /// </summary>
    void OnReservationCancelled(long destinationId, int qty);

    /// <summary>
    /// 슈트 비움(CLEARED) 이벤트 — DB 영속화 후 집계 리셋.
    /// chute_detail.last_cleared_at = UtcNow + destination_event(CLEARED) append 후 인메모리 카운터 0으로 리셋.
    /// 재시작 시 InitializeFromDbAsync가 last_cleared_at 기준으로 재구성하므로 DB 영속화 필수.
    /// </summary>
    Task OnCleared(long destinationId);
}

/// <summary>
/// IChuteCapacityService 구현 — 인메모리 집계 싱글톤.
/// 기동 시 DB 재구성 필요: InitializeAsync() 호출 필수.
/// thread-safe: ReaderWriterLockSlim으로 읽기 병렬 / 쓰기 단독.
/// </summary>
public sealed class ChuteCapacityService : IChuteCapacityService, IHostedService
{
    // ── 슈트별 인메모리 상태 ────────────────────────────────────────────────
    private sealed class ChuteState
    {
        /// <summary>RESERVED/PERMITTED 활성 piece의 이동중 예약 qty 합산.</summary>
        public int InFlightQty { get; set; }

        /// <summary>
        /// deposited_at > last_cleared_at인 DEPOSITED 이상 piece qty 합산.
        /// (비움 시 0으로 리셋)
        /// </summary>
        public int DepositedQty { get; set; }

        /// <summary>work_full_qty (DB chute_detail.work_full_qty).</summary>
        public int WorkFullQty { get; set; }

        /// <summary>PAUSED = destination.status == PAUSED.</summary>
        public bool IsPaused { get; set; }

        /// <summary>IsActive = destination.is_active.</summary>
        public bool IsActive { get; set; }

        /// <summary>총 집계 = DepositedQty + InFlightQty.</summary>
        public int TotalQty => DepositedQty + InFlightQty;
    }

    // destination.id → ChuteState (CHUTE 전용)
    private readonly Dictionary<long, ChuteState> _states = new();
    private readonly ReaderWriterLockSlim _rwLock = new();

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ChuteCapacityService> _log;

    // ── IF-08 푸시 변화원 ① 통지 (Phase 2) ──────────────────────────────────
    // 슈트 상태(InFlight/Deposited/Paused/Active)가 바뀔 수 있는 이벤트마다 발화.
    // DestinationStatusPusher가 구독해 ready 전이를 재평가·푸시(무변화면 0건).
    // 게이트웨이 OnOfflineTransition과 동형 — 단방향 이벤트(여기는 DB·푸시 무지).
    // 발화는 _rwLock 임계구역 밖(구독자 콜백이 락을 잡지 않게 — 데드락·확장 방지).
    public event Action<long>? OnChuteStateChanged;

    public ChuteCapacityService(
        IServiceScopeFactory scopeFactory,
        ILogger<ChuteCapacityService> log)
    {
        _scopeFactory = scopeFactory;
        _log          = log;
    }

    // ── IHostedService: 기동 시 DB로 인메모리 재구성 ─────────────────────────

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await InitializeFromDbAsync(cancellationToken).ConfigureAwait(false);
        _log.LogInformation("[ChuteCapacity] 인메모리 집계 초기화 완료. 슈트 수={Count}", _states.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // ── DB 재구성 ───────────────────────────────────────────────────────────

    private async Task InitializeFromDbAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();

        // CHUTE 목적지 + chute_detail JOIN
        var chuteDestinations = await db.ChuteDetails
            .Include(cd => cd.Destination)
            .Where(cd => cd.Destination.DestType == DestType.CHUTE)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // deposited qty: chute_detail.last_cleared_at 이후 piece만 합산 (MAJOR-2 수정).
        // last_cleared_at=NULL 또는 piece.deposited_at=NULL이면 필터 통과(전체 포함).
        // piece.destination_id JOIN chute_detail: CHUTE 전용 집계이므로 JOIN 가능.
        // 이동중 예약 qty: RESERVED/PERMITTED 활성 piece qty 합
        var depositedQtys = await db.Pieces
            .Join(db.ChuteDetails,
                  p  => p.DestinationId,
                  cd => (long?)cd.DestinationId,
                  (p, cd) => new { Piece = p, ChuteDetail = cd })
            .Where(x => x.Piece.IsActive
                     && (x.Piece.Status == PieceStatus.DEPOSITED
                      || x.Piece.Status == PieceStatus.CELL_ASSIGNED
                      || x.Piece.Status == PieceStatus.LOADED)
                     // last_cleared_at 이후 투입분만 합산(비움 이전 piece 제외)
                     && (x.Piece.DepositedAt  == null
                      || x.ChuteDetail.LastClearedAt == null
                      || x.Piece.DepositedAt > x.ChuteDetail.LastClearedAt))
            .GroupBy(x => x.Piece.DestinationId)
            .Select(g => new { DestinationId = g.Key, Qty = g.Sum(x => x.Piece.Qty) })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var inFlightQtys = await db.Pieces
            .Where(p => p.IsActive
                     && (p.Status == PieceStatus.RESERVED
                      || p.Status == PieceStatus.PERMITTED))
            .GroupBy(p => p.DestinationId)
            .Select(g => new { DestinationId = g.Key, Qty = g.Sum(p => p.Qty) })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        // DestinationId가 long?(MINOR-5 nullable FK) — null 키는 CHUTE 목적지 없는 DENIED piece이므로 제외.
        var depositedMap  = depositedQtys
            .Where(x => x.DestinationId != null)
            .ToDictionary(x => x.DestinationId!.Value, x => x.Qty);
        var inFlightMap   = inFlightQtys
            .Where(x => x.DestinationId != null)
            .ToDictionary(x => x.DestinationId!.Value, x => x.Qty);

        _rwLock.EnterWriteLock();
        try
        {
            _states.Clear();
            foreach (var cd in chuteDestinations)
            {
                var dest = cd.Destination;
                _states[dest.Id] = new ChuteState
                {
                    WorkFullQty  = cd.WorkFullQty,
                    IsPaused     = dest.Status == DestStatus.PAUSED,
                    IsActive     = dest.IsActive,
                    DepositedQty = depositedMap.GetValueOrDefault(dest.Id, 0),
                    InFlightQty  = inFlightMap.GetValueOrDefault(dest.Id, 0),
                };
            }
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
    }

    // ── IChuteCapacityService 구현 ──────────────────────────────────────────

    /// <inheritdoc/>
    public WcsHold GetHold(long destinationId)
    {
        _rwLock.EnterReadLock();
        try
        {
            if (!_states.TryGetValue(destinationId, out var state))
            {
                // 알 수 없는 목적지 → PAUSED(비활성 슈트로 취급, SPEC §2 비활성→PAUSED 매핑)
                return WcsHold.Paused;
            }

            // 비활성 슈트 → PAUSED 매핑
            if (!state.IsActive) return WcsHold.Paused;

            // Hold 우선순위: Full > Paused > None (SPEC §2)
            if (state.TotalQty >= state.WorkFullQty && state.WorkFullQty > 0)
                return WcsHold.Full;

            if (state.IsPaused) return WcsHold.Paused;

            return WcsHold.None;
        }
        finally
        {
            _rwLock.ExitReadLock();
        }
    }

    /// <inheritdoc/>
    public void OnReserved(long destinationId, int qty)
    {
        _rwLock.EnterWriteLock();
        try
        {
            if (_states.TryGetValue(destinationId, out var state))
                state.InFlightQty += qty;
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
        RaiseChuteStateChanged(destinationId);
    }

    /// <inheritdoc/>
    public void OnDeposited(long destinationId, int qty)
    {
        _rwLock.EnterWriteLock();
        try
        {
            if (_states.TryGetValue(destinationId, out var state))
            {
                // 예약 해제(IF-05 예약 차감분) + 투입 확정
                state.InFlightQty  = Math.Max(0, state.InFlightQty - qty);
                state.DepositedQty += qty;
            }
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
        RaiseChuteStateChanged(destinationId);
    }

    /// <inheritdoc/>
    public void OnReservationCancelled(long destinationId, int qty)
    {
        _rwLock.EnterWriteLock();
        try
        {
            if (_states.TryGetValue(destinationId, out var state))
                state.InFlightQty = Math.Max(0, state.InFlightQty - qty);
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }
        RaiseChuteStateChanged(destinationId);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// DB 트랜잭션 처리(chute_detail.last_cleared_at + destination_event(CLEARED))가 완료된 후
    /// 인메모리 카운터를 리셋한다. 락 보유 중 I/O를 하지 않도록 DB 쓰기 후 락 진입.
    /// </remarks>
    public async Task OnCleared(long destinationId)
    {
        // ① 락 밖에서 DB 트랜잭션 수행 (싱글톤에서 스코프 서비스 사용)
        using (var scope = _scopeFactory.CreateScope())
        {
            var db  = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            var now = DateTime.UtcNow;

            // chute_detail.last_cleared_at 갱신 — DestinationId가 ChuteDetail PK
            var detail = await db.ChuteDetails.FindAsync(destinationId).ConfigureAwait(false);
            if (detail is not null)
            {
                detail.LastClearedAt = now;
                detail.UpdatedAt     = now;
            }

            // destination_event(CLEARED) append
            db.DestinationEvents.Add(new DestinationEvent
            {
                DestinationId = destinationId,
                EventType     = DestinationEventType.CLEARED,
                At            = now,
            });

            await db.SaveChangesAsync().ConfigureAwait(false);
        }

        // ② DB 커밋 후 인메모리 카운터 리셋
        _rwLock.EnterWriteLock();
        try
        {
            if (_states.TryGetValue(destinationId, out var state))
            {
                state.DepositedQty = 0;
                state.InFlightQty  = 0;
            }
        }
        finally
        {
            _rwLock.ExitWriteLock();
        }

        _log.LogInformation("[ChuteCapacity] destinationId={Id} CLEARED — last_cleared_at 갱신·인메모리 리셋", destinationId);
        RaiseChuteStateChanged(destinationId);
    }

    // ── IF-08 푸시 변화원 통지 — 락 밖에서 발화 ───────────────────────────────
    // 구독자(DestinationStatusPusher) 콜백 예외가 capacity 갱신을 죽이지 않도록 흡수
    // (Fail-Loud: 예외는 로깅). 콜백은 빠르게 반환(Pusher가 비동기 푸시 루프만 기동).
    private void RaiseChuteStateChanged(long destinationId)
    {
        var handler = OnChuteStateChanged;
        if (handler is null) return;
        try
        {
            handler(destinationId);
        }
        catch (Exception ex)
        {
            try { _log.LogError(ex, "[ChuteCapacity] OnChuteStateChanged 구독자 예외 destId={Id}", destinationId); }
            catch { /* teardown 중 로거 disposed */ }
        }
    }
}
