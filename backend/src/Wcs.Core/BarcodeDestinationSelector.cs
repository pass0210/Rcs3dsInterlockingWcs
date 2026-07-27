namespace Wcs.Core;

/// <summary>
/// IF-05 목적지 조회에서 **한 바코드가 여러 order_item 에 매칭**될 때(교차-배치 중복 업로드로 발생)
/// 어느 후보를 고를지 결정하는 **순수 선택 규칙**(절대규칙 #8 — I/O·DI·EF 무의존, 테스트가 스펙).
///
/// 입력은 EF 엔티티가 아니라 최소 projection(<see cref="BarcodeDestinationCandidate"/>) 리스트다.
/// 호출자(DbRepositories.QueryDestination)가 후보를 materialize 한 뒤 projection 으로 변환해 넘긴다.
///
/// 규칙(SPEC §7-B 확정 — 배정-우선 결정적 선택):
///   (1) 후보 중 <see cref="BarcodeDestinationCandidate.IsAssigned"/>(order.DestinationId != null)가
///       하나라도 있으면 **배정된 후보 그룹**을 우선한다(미배정 후보를 골라 NG/NO_DEST 로 떨어뜨리던
///       비결정적 .FirstOrDefault() 결함 수정).
///   (2) 우선 그룹 내 tiebreak = **결정적**: 배정확정 최신(<see cref="BarcodeDestinationCandidate.DestAssignedAt"/>
///       내림차순 — 운영자의 가장 최근 배정 의도가 이긴다) → 최소 OrderId → 최소 OrderItemId.
///       (DestAssignedAt 이 null 인 후보는 <see cref="System.DateTime.MinValue"/>(가장 오래됨)로 취급.)
///   (3) 배정 후보가 없으면 미배정 후보에서 같은 정렬로 결정적 1건(전부 DestAssignedAt==null →
///       최소 OrderId → 최소 OrderItemId) → 이후 기존 AUTO 슈트배정/NO_DEST 흐름 그대로.
///
/// OrderItemId 는 PK(유일)라 최종 tiebreak 로 **완전한 전순서(total order)** 를 보장한다(결정성 잠금).
/// 단건 1:1 매치(후보 1개)는 그 후보를 그대로 반환 → 기존 동작과 동일(회귀 0).
/// </summary>
public static class BarcodeDestinationSelector
{
    /// <summary>
    /// 후보 리스트에서 결정적으로 1건 선택. 후보가 없으면 null(호출자가 NO_DEST 처리).
    /// </summary>
    public static BarcodeDestinationCandidate? Select(IReadOnlyList<BarcodeDestinationCandidate> candidates)
    {
        if (candidates is null || candidates.Count == 0)
            return null;

        // (1) 배정된 후보가 하나라도 있으면 그 그룹만, 없으면 전체(미배정)를 대상으로.
        bool anyAssigned = false;
        for (int i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].IsAssigned) { anyAssigned = true; break; }
        }

        IEnumerable<BarcodeDestinationCandidate> group =
            anyAssigned ? candidates.Where(c => c.IsAssigned) : candidates;

        // (2)/(3) 결정적 tiebreak: 배정확정 최신 → 최소 OrderId → 최소 OrderItemId(전순서 잠금).
        return group
            .OrderByDescending(c => c.DestAssignedAt ?? DateTime.MinValue)
            .ThenBy(c => c.OrderId)
            .ThenBy(c => c.OrderItemId)
            .First();
    }
}

/// <summary>
/// 바코드-목적지 선택용 최소 projection(EF 무의존 — 순수 규칙 입력).
/// </summary>
/// <param name="OrderItemId">order_item 대리키(PK) — 최종 결정적 tiebreak·선택 결과 식별.</param>
/// <param name="OrderId">소속 오더 대리키.</param>
/// <param name="IsAssigned">order.DestinationId != null (배정 여부).</param>
/// <param name="DestAssignedAt">배정 확정 시각(미배정·미기록이면 null).</param>
public sealed record BarcodeDestinationCandidate(
    long OrderItemId,
    long OrderId,
    bool IsAssigned,
    DateTime? DestAssignedAt);
