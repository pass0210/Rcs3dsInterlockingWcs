namespace Wcs.Api.B2B;

// ════════════════════════════════════════════════════════════════════════════
// B2BApiResponse — B2B 공통 응답 래퍼 { status:"S"|"F", message }.
// B2B 전용(기존 Wcs.Api 응답 형식과 격리 — 기존 400/500 무접촉).
// System.Text.Json Web 기본(camelCase) → {"status":"...","message":"..."}.
// ════════════════════════════════════════════════════════════════════════════

/// <summary>B2B API 공통 응답 래퍼.</summary>
public sealed record B2BApiResponse(string Status, string Message)
{
    /// <summary>성공(status "S"). 기본 message = "Success"(#9).</summary>
    public static B2BApiResponse Ok(string message = FailMessages.Success) => new("S", message);

    /// <summary>비즈니스 실패(status "F").</summary>
    public static B2BApiResponse Fail(string message) => new("F", message);
}
