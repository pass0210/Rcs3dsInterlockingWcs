using Wcs.Core;
using Xunit;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// S-B2C-BARCODE-MULTI-FIX Fix 2 — 순수 선택 규칙(BarcodeDestinationSelector) 단위테스트.
//   절대규칙 #8: 판정은 Wcs.Core 순수 함수 — I/O·DI·EF 무의존. 테스트가 스펙이다.
//
// 규칙(SPEC §7-B 확정 — 배정-우선 결정적 선택):
//   (1) 배정 후보(IsAssigned)가 있으면 그 그룹 우선.
//   (2) tiebreak = 배정확정 최신(DestAssignedAt desc, null=MinValue) → 최소 OrderId → 최소 OrderItemId.
//   (3) 배정 후보 없으면 미배정에서 같은 정렬로 결정적 1건.
//   단건 1:1(후보 1개)은 그 후보 반환(회귀 0).
// ════════════════════════════════════════════════════════════════════════════
public class BarcodeDestinationSelectorTests
{
    private static readonly DateTime T0 = new(2026, 7, 27, 9, 0, 0, DateTimeKind.Utc);

    private static BarcodeDestinationCandidate Cand(
        long orderItemId, long orderId, bool assigned, DateTime? assignedAt = null)
        => new(orderItemId, orderId, assigned, assignedAt);

    // ── 경계: 후보 없음 → null(호출자 NO_DEST) ──────────────────────────────────
    [Fact]
    public void Empty_ReturnsNull()
    {
        Assert.Null(BarcodeDestinationSelector.Select(new List<BarcodeDestinationCandidate>()));
    }

    // ── 단건 1:1 — 배정/미배정 무관하게 그 후보 반환(회귀 0) ─────────────────────
    [Fact]
    public void SingleCandidate_Unassigned_ReturnsIt()
    {
        var only = Cand(orderItemId: 10, orderId: 5, assigned: false);
        Assert.Same(only, BarcodeDestinationSelector.Select(new[] { only }));
    }

    [Fact]
    public void SingleCandidate_Assigned_ReturnsIt()
    {
        var only = Cand(orderItemId: 11, orderId: 6, assigned: true, assignedAt: T0);
        Assert.Same(only, BarcodeDestinationSelector.Select(new[] { only }));
    }

    // ── (1) 배정 우선: 배정+미배정 공존 → 배정 후보 선택(비결정적 미배정 픽 결함 수정) ──
    [Fact]
    public void AssignedPreferredOverUnassigned()
    {
        var unassigned = Cand(orderItemId: 20, orderId: 1, assigned: false);         // 더 작은 OrderId(구 버그면 이게 뽑혔음)
        var assigned   = Cand(orderItemId: 21, orderId: 9, assigned: true, T0);
        var chosen = BarcodeDestinationSelector.Select(new[] { unassigned, assigned });
        Assert.Same(assigned, chosen);
    }

    // ── (2) 다중 배정 tiebreak: 배정확정 최신(DestAssignedAt desc) → 최소 OrderId ──
    [Fact]
    public void MultiAssigned_MostRecentDestAssignedAt_Wins()
    {
        var older = Cand(orderItemId: 30, orderId: 3, assigned: true, T0);
        var newer = Cand(orderItemId: 31, orderId: 7, assigned: true, T0.AddMinutes(5));
        Assert.Same(newer, BarcodeDestinationSelector.Select(new[] { older, newer }));
        // 순서 무관(결정적) — 입력 순서를 뒤집어도 동일.
        Assert.Same(newer, BarcodeDestinationSelector.Select(new[] { newer, older }));
    }

    // ── (2) 동일 DestAssignedAt → 최소 OrderId tiebreak ─────────────────────────
    [Fact]
    public void MultiAssigned_SameTimestamp_MinOrderId_Wins()
    {
        var a = Cand(orderItemId: 40, orderId: 8, assigned: true, T0);
        var b = Cand(orderItemId: 41, orderId: 2, assigned: true, T0);   // 더 작은 OrderId
        Assert.Same(b, BarcodeDestinationSelector.Select(new[] { a, b }));
    }

    // ── (2) 동일 OrderId(방어적 — 실제론 barcode UQ)까지 겹치면 최소 OrderItemId ──
    [Fact]
    public void MultiAssigned_SameOrder_MinOrderItemId_Wins()
    {
        var a = Cand(orderItemId: 51, orderId: 4, assigned: true, T0);
        var b = Cand(orderItemId: 50, orderId: 4, assigned: true, T0);   // 더 작은 OrderItemId
        Assert.Same(b, BarcodeDestinationSelector.Select(new[] { a, b }));
    }

    // ── (3) 미배정만: 결정적 최소 OrderId(→ 이후 AUTO/NO_DEST 기존 흐름) ──────────
    [Fact]
    public void UnassignedOnly_MinOrderId_Wins()
    {
        var a = Cand(orderItemId: 60, orderId: 12, assigned: false);
        var b = Cand(orderItemId: 61, orderId: 3, assigned: false);      // 최소 OrderId
        var c = Cand(orderItemId: 62, orderId: 7, assigned: false);
        Assert.Same(b, BarcodeDestinationSelector.Select(new[] { a, b, c }));
        Assert.Same(b, BarcodeDestinationSelector.Select(new[] { c, b, a }));   // 순서 무관.
    }

    // ── 배정 후보 중 DestAssignedAt==null 방어: MinValue 취급(가장 오래됨) → 최신 배정이 이김 ──
    [Fact]
    public void AssignedWithNullTimestamp_TreatedAsOldest()
    {
        var nullTs = Cand(orderItemId: 70, orderId: 1, assigned: true, assignedAt: null);
        var dated  = Cand(orderItemId: 71, orderId: 9, assigned: true, assignedAt: T0);
        Assert.Same(dated, BarcodeDestinationSelector.Select(new[] { nullTs, dated }));
    }

    // ── 결정성 잠금: 다양한 입력 순열 전부 동일 결과(전순서 OrderItemId 잠금) ────────
    [Fact]
    public void Deterministic_AcrossPermutations()
    {
        var a = Cand(orderItemId: 80, orderId: 5, assigned: true, T0.AddMinutes(1));
        var b = Cand(orderItemId: 81, orderId: 2, assigned: true, T0.AddMinutes(1));   // 동일 최신 ts, 더 작은 OrderId → 승
        var c = Cand(orderItemId: 82, orderId: 1, assigned: false);
        var perms = new[]
        {
            new[] { a, b, c }, new[] { a, c, b }, new[] { b, a, c },
            new[] { b, c, a }, new[] { c, a, b }, new[] { c, b, a },
        };
        foreach (var p in perms)
            Assert.Same(b, BarcodeDestinationSelector.Select(p));
    }
}
