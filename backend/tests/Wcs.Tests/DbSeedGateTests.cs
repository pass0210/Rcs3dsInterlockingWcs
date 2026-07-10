using Wcs.Api.Startup;
using Xunit;

namespace Wcs.Tests;

/// <summary>
/// 시드 게이트(DbInitializer.ShouldSeed) 회귀 테스트 — 이 테스트가 스펙이다.
///
/// 2026-07-03 현장 SqlServer DB 오염 사고(Development 기동 → dev 시드가 현장 DB 주입 →
/// SorterRegistry fail-loud로 전체 기동 거부)의 재발 방지 고정.
///
/// 사고의 본질: 과거 게이트는 `seedOnStartup ?? IsDevelopment()` 였다 —
///   즉 SeedOnStartup 미지정(null) 시 ASPNETCORE_ENVIRONMENT=Development면 자동 on.
///   ShouldSeed는 이 환경 암묵 발동을 제거하고 "명시 true일 때만" 시드하도록 고정한다.
///
/// 순수 bool 게이트 — SQLite·호스트·DB 불요(함정1·2: 인메모리 팩토리는 ProvisionAsync를
/// 조기 no-op하므로 게이트는 호스트 경유로 관측 불가 → 순수 함수 직접 단위 테스트가 유일한 회귀 고정 경로).
/// </summary>
public class DbSeedGateTests
{
    // 사고의 핵심 회귀 방지: 미명시(null) → 시드 안 함.
    // (과거 `?? IsDevelopment()`였다면 Development 환경에서 true가 되어 현장 DB를 오염시켰다.)
    [Fact]
    public void ShouldSeed_Null_ReturnsFalse()
    {
        Assert.False(DbInitializer.ShouldSeed(null));
    }

    // 명시 false → 시드 안 함.
    [Fact]
    public void ShouldSeed_False_ReturnsFalse()
    {
        Assert.False(DbInitializer.ShouldSeed(false));
    }

    // 명시 true → 시드 실행(명시 경로만 살아있음).
    [Fact]
    public void ShouldSeed_True_ReturnsTrue()
    {
        Assert.True(DbInitializer.ShouldSeed(true));
    }
}
