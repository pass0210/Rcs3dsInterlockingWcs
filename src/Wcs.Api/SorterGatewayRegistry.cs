using Wcs.PlcGateway;

namespace Wcs.Api;

// ════════════════════════════════════════════════════════════════════════════
// ISorterGatewayRegistry — destination.id 키로 소터 번들 핸들 조회 진입점.
//
// P2b: 멀티소터 — destination.id → 소터별 번들(스냅샷·큐·핸드셰이크).
// ════════════════════════════════════════════════════════════════════════════

// ── 소터 번들 핸들 ──────────────────────────────────────────────────────────

/// <summary>
/// 소터 1대에 대응하는 번들 핸들.
/// 스냅샷 조회·TgtFloor 큐 투입·핸드셰이크 트리거가 전부 이 핸들을 경유.
/// 인스턴스별 독립 IModbusMaster·PlcWriteQueue·HandshakeOrchestrator를 보유.
/// </summary>
public sealed class SorterBundleHandle
{
    // ── 구성 ─────────────────────────────────────────────────────────────────
    /// <summary>destination.id (DB 키). 불변.</summary>
    public long DestinationId { get; init; }

    /// <summary>destination.chute_no (소터 슬롯 번호). 불변.</summary>
    public int ChuteNo { get; init; }

    // ── 번들 컴포넌트 (각 소터별 독립 인스턴스) ──────────────────────────────
    private readonly PlcPollingService   _polling;
    private readonly HandshakeOrchestrator _handshake;

    // PlcWriteQueue는 PlcPollingService 안에 캡슐화돼 있음.
    // 외부에서 EnqueueAsync를 호출하려면 IPlcGateway 인터페이스를 경유.

    public SorterBundleHandle(
        long                   destinationId,
        int                    chuteNo,
        PlcPollingService      polling,
        HandshakeOrchestrator  handshake)
    {
        DestinationId = destinationId;
        ChuteNo       = chuteNo;
        _polling      = polling;
        _handshake    = handshake;
    }

    // ── API 계층이 사용하는 세 가지 조작 ─────────────────────────────────────

    /// <summary>이 소터의 최신 PLC 스냅샷(논블로킹).</summary>
    public Wcs.Core.PlcSnapshot Latest => _polling.Latest;

    /// <summary>TgtFloor 설정을 소터별 쓰기 큐에 투입(번들 전용 큐 — 절대규칙 #1 소터별 보존).</summary>
    public ValueTask EnqueueSetTgtFloorAsync(int floor, CancellationToken ct = default) =>
        _polling.EnqueueAsync(new PlcWrite.SetTgtFloor(floor), ct);

    /// <summary>셀 지정 핸드셰이크 1건 수행(백그라운드 태스크로 호출).</summary>
    public Task<HandshakeResult> ExecuteHandshakeAsync(int cellNo, CancellationToken ct = default) =>
        _handshake.ExecuteAsync(cellNo, ct);

    // ── IHostedService 위임 ──────────────────────────────────────────────────

    /// <summary>소터 폴링 서비스 시작 (PlcPollingHostedAdapter에서 호출).</summary>
    public Task StartPollingAsync(CancellationToken ct) => _polling.StartAsync(ct);

    /// <summary>소터 폴링 서비스 종료 (PlcPollingHostedAdapter에서 호출).</summary>
    public Task StopPollingAsync() => _polling.StopAsync();
}

// ── ISorterGatewayRegistry ───────────────────────────────────────────────────

/// <summary>
/// destination.id 키로 소터 번들 핸들을 조회하는 단일 진입점.
/// P2b: N대 멀티소터 라우팅 — destination.id → SorterBundleHandle.
/// </summary>
public interface ISorterGatewayRegistry
{
    /// <summary>
    /// destination.id에 해당하는 소터의 최신 PLC 스냅샷 반환.
    /// destination.id가 등록되지 않은 소터면 null(OFFLINE 경로 유지).
    /// </summary>
    Wcs.Core.PlcSnapshot? GetLatest(long destinationId);

    /// <summary>
    /// destination.id에 해당하는 소터 번들 핸들 반환.
    /// 미등록 destination.id는 null.
    /// </summary>
    SorterBundleHandle? GetBundle(long destinationId);

    /// <summary>등록된 전체 소터 번들 열거 (IHostedService Start/Stop 위임용).</summary>
    IReadOnlyCollection<SorterBundleHandle> AllBundles { get; }
}

// ── MultiSorterGatewayRegistry ───────────────────────────────────────────────

/// <summary>
/// P2b 멀티소터 구현.
/// 기동 시 DB에서 dest_type=SORTER_3D 조회 → ChuteNo로 appsettings 소터 배열 매칭 →
/// 소터별 번들(IModbusMaster + PlcWriteQueue + PlcPollingService + HandshakeOrchestrator) N대 구성.
/// SORTER_3D인데 appsettings에 ChuteNo 누락 시 fail-loud(기동 에러).
/// </summary>
public sealed class MultiSorterGatewayRegistry : ISorterGatewayRegistry
{
    // destination.id → 번들 핸들 (불변 딕셔너리)
    private readonly IReadOnlyDictionary<long, SorterBundleHandle> _bundles;

    public MultiSorterGatewayRegistry(IReadOnlyDictionary<long, SorterBundleHandle> bundles)
    {
        _bundles = bundles;
    }

    /// <inheritdoc/>
    public Wcs.Core.PlcSnapshot? GetLatest(long destinationId) =>
        _bundles.TryGetValue(destinationId, out var h) ? h.Latest : null;

    /// <inheritdoc/>
    public SorterBundleHandle? GetBundle(long destinationId) =>
        _bundles.TryGetValue(destinationId, out var h) ? h : null;

    /// <inheritdoc/>
    public IReadOnlyCollection<SorterBundleHandle> AllBundles =>
        (IReadOnlyCollection<SorterBundleHandle>)_bundles.Values;
}
