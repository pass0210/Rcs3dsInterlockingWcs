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
    public static void Init()
    {
        WcsTeardownGuard.Install();

        // ── S-TRACE-LOG-VIEWER: 전용 추적 로그 기본 경로를 테스트 프로세스 전역에서 scratch 로 강제 ──
        // 절대규칙 #7 테스트 지침: 실경로(D:\Rcs3dsInterlockingWcsLogs)에 쓰지 않는다. 웹 호스트를 띄우는
        // 모든 테스트(E2E·API·Hub…)가 이 env(TraceLog__Directory → 설정 TraceLog:Directory)를 기본값으로
        // 집어 실 로그·머신 의존을 방지한다. 개별 테스트는 config 오버라이드로 per-test 디렉터리를 지정 가능
        // (in-memory config 가 env 보다 나중에 병합돼 우선). 이 env 는 테스트 프로세스에만 설정된다.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TraceLog__Directory")))
        {
            var dir = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "wcs-trace-tests", Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("TraceLog__Directory", dir);
        }
    }
}
