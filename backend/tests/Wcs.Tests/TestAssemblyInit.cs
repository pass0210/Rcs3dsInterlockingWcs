using System.Runtime.CompilerServices;
using Wcs.Api;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// TestAssemblyInit — 테스트 어셈블리 로드 시 1회 실행.
//
// 종료 단계 관찰되지 않은 Task 예외 가드(WcsTeardownGuard)를 등록한다.
// 웹 호스트를 띄우는 테스트(ApiIntegrationTests·ScenarioTests)는 Program.cs가
// 동일 가드를 등록하지만, FluentModbus 서버/클라이언트를 직접 생성하는 테스트
// (RtuTransportTests·PlcGatewayIntegrationTests)는 Program.cs를 거치지 않으므로
// 모든 테스트를 포괄하려면 어셈블리 진입 시점에 한 번 등록해야 한다.
// (WcsTeardownGuard.Install은 프로세스당 멱등 — 양쪽 호출이 충돌하지 않는다.)
// ════════════════════════════════════════════════════════════════════════════
internal static class TestAssemblyInit
{
    [ModuleInitializer]
    public static void Init() => WcsTeardownGuard.Install();
}
