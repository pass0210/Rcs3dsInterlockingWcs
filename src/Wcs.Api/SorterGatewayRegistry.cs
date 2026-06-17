using Wcs.PlcGateway;

namespace Wcs.Api;

// ════════════════════════════════════════════════════════════════════════════
// ISorterGatewayRegistry — destination.id 키로 게이트웨이 스냅샷 조회 진입점.
//
// P2a: 단일 소터 전제 → 항상 동일 IPlcGateway 반환.
// P2b 확장점: 다중 소터 시 destination.id → 소터별 IPlcGateway 라우팅.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>
/// destination.id 키로 소터 게이트웨이 스냅샷을 조회하는 단일 진입점.
/// P2a는 단일 소터 전제 — 항상 동일 IPlcGateway 반환.
/// P2b에서 destination.id → 소터별 IPlcGateway 라우팅으로 확장.
/// </summary>
public interface ISorterGatewayRegistry
{
    /// <summary>
    /// destination.id에 해당하는 소터 게이트웨이 최신 스냅샷 반환.
    /// P2a: 단일 소터 전제 → destinationId 무관하게 같은 게이트웨이 반환.
    /// destinationId가 존재하지 않으면 null(P2b 확장 시 라우팅 실패).
    /// </summary>
    Wcs.Core.PlcSnapshot? GetLatest(long destinationId);
}

/// <summary>
/// P2a 단일 소터 구현 — 항상 주입된 IPlcGateway.Latest 반환.
/// destination.id 진입점 인터페이스를 충족하면서 P2b 확장점을 예약.
/// </summary>
public sealed class SingleSorterGatewayRegistry : ISorterGatewayRegistry
{
    private readonly IPlcGateway _gateway;

    public SingleSorterGatewayRegistry(IPlcGateway gateway)
    {
        _gateway = gateway;
    }

    /// <inheritdoc/>
    public Wcs.Core.PlcSnapshot? GetLatest(long destinationId)
    {
        // P2a: 단일 소터 — destinationId 무관하게 같은 게이트웨이 스냅샷 반환.
        // P2b: destinationId → 소터별 게이트웨이 라우팅으로 교체.
        return _gateway.Latest;
    }
}
