using Wcs.Api;
using Xunit;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// S-AUDIT-B-DEPLOY-HARDENING 모듈 1 — 로그 위치 결정성 게이트(순수 판정) 테스트
//
//   ServiceHostingEnvironment.ResolveWorkingDirectoryOverride 는 "서비스 컨텍스트면 작업
//   디렉터리를 exe 인접 배포 폴더로 고정할지"를 부작용 없이 산출한다. Program 부트스트랩부는
//   이 결과만 소비해 Directory.SetCurrentDirectory를 호출하므로, 이 순수 함수만 검증하면
//   실 서비스 컨텍스트를 재현하지 않고도 게이트 실효(서비스=고정 / 비서비스=미변경)를 입증할 수 있다.
// ════════════════════════════════════════════════════════════════════════════

public class ServiceHostingEnvironmentTests
{
    [Fact]
    public void ServiceContext_ReturnsBaseDirectory()
    {
        // 서비스 컨텍스트 → CWD를 exe 인접 배포 폴더(baseDirectory)로 고정하라는 신호(그 경로 반환).
        const string baseDir = @"C:\BOWOO\Wcs.Api";
        var result = ServiceHostingEnvironment.ResolveWorkingDirectoryOverride(
            isWindowsService: true, baseDirectory: baseDir);
        Assert.Equal(baseDir, result);
    }

    [Fact]
    public void NonServiceContext_ReturnsNull_NoOverride()
    {
        // 비서비스(dev dotnet run·콘솔·테스트 호스트) → null(작업 디렉터리 미변경 — 회귀 0).
        var result = ServiceHostingEnvironment.ResolveWorkingDirectoryOverride(
            isWindowsService: false, baseDirectory: @"C:\anything\ignored");
        Assert.Null(result);
    }

    [Theory]
    // 반환값은 오직 호출부가 넘긴 baseDirectory 파생 — 리터럴 경로 하드코딩 없음(#7).
    [InlineData(@"C:\BOWOO\Wcs.Api")]
    [InlineData(@"D:\deploy\wcs")]
    [InlineData(@"/opt/wcs")]
    public void ServiceContext_EchoesGivenBaseDirectory(string baseDir)
    {
        Assert.Equal(baseDir,
            ServiceHostingEnvironment.ResolveWorkingDirectoryOverride(true, baseDir));
    }
}
