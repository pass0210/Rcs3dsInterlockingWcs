using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests.E2E;

// ════════════════════════════════════════════════════════════════════════════
// LiveMultiAgvRunner — 라이브 구동 진입점 (계약 §3.3 / §7)
//
//   자동 스위트와 **동일한 MultiAgvDriver 로직을 공유**(중복 구현 금지)하여, 실제로 기동한
//   WCS API(dotnet run --project src/Wcs.Api) + 실 Sim(dotnet run --project src/Wcs.Sim3ds)에
//   다중 AGV 동시 부하를 인가한다. 로그·DB·push 수신은 orchestrator가 육안 관찰(§7).
//
//   실행 방식(orchestrator step — APPROVED 후):
//     WCS_LIVE_BASEURL 환경변수에 라이브 WCS base URL(예: http://127.0.0.1:5080)을 설정하고
//     이 [Fact]를 명시 필터로 실행한다. 환경변수 미설정이면 Skip(자동 회귀 0 영향).
//       예) WCS_LIVE_BASEURL=http://127.0.0.1:5080 \
//           dotnet test --filter "FullyQualifiedName~LiveMultiAgvRunner"
//
//   파라미터(환경변수, 전부 선택 — 기본값 내장):
//     WCS_LIVE_BASEURL   : 라이브 WCS base URL(필수 — 미설정 시 Skip).
//     WCS_LIVE_AGVS      : 동시 AGV 수(기본 3).
//     WCS_LIVE_BARCODE   : 사용 바코드(기본 TEST-BARCODE-3 — 소터). 슈트는 TEST-BARCODE-1.
//     WCS_LIVE_CHUTE     : chuteNo(기본 30 — 소터). 슈트는 1.
//     WCS_LIVE_PIDBASE   : pId 시작값(기본 1000 — 1~30000 범위 유지).
//
//   ⚠ 한 소터 동시 IF-10은 핸드셰이크 직렬화 부재로 일부 MISMATCH가 정상(F1b finding) —
//      라이브 관찰 시 같은 소터엔 직렬 dispatch가 권장. 이 러너는 동시 부하를 거는 도구이므로
//      orchestrator가 의도(동시 부하 vs 직렬 흐름)에 맞게 AGV 수·바코드·소터 분산을 조정한다.
// ════════════════════════════════════════════════════════════════════════════
public class LiveMultiAgvRunner
{
    private readonly ITestOutputHelper _out;
    public LiveMultiAgvRunner(ITestOutputHelper output) => _out = output;

    [Fact]
    public async Task Live_DriveConcurrentAgvLoad()
    {
        var baseUrl = Environment.GetEnvironmentVariable("WCS_LIVE_BASEURL");
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            // WCS_LIVE_BASEURL 미설정 — 라이브 구동은 orchestrator step(APPROVED 후).
            // 자동 회귀에선 no-op으로 통과(xUnit 2.9.3은 동적 Skip 미지원 → early return).
            _out.WriteLine("[LIVE] WCS_LIVE_BASEURL 미설정 — 라이브 구동 건너뜀(orchestrator step에서 실행).");
            return;
        }

        int    agvs    = ParseInt("WCS_LIVE_AGVS", 3);
        string barcode = Environment.GetEnvironmentVariable("WCS_LIVE_BARCODE") ?? "TEST-BARCODE-3";
        int    chute   = ParseInt("WCS_LIVE_CHUTE", 30);
        int    pidBase = ParseInt("WCS_LIVE_PIDBASE", 1000);

        // 자동 스위트와 동일한 드라이버 — 라이브 base URL로만 바인딩(코드 공유).
        var driver = MultiAgvDriver.ForBaseUrl(baseUrl!);

        var jobs = Enumerable.Range(0, agvs)
            .Select(i => new AgvJob(
                PId: pidBase + i, AgvNo: (i % 4) + 1, Barcode: barcode, ChuteNo: chute, Qty: 1))
            .ToList();

        _out.WriteLine($"[LIVE] {agvs} AGV 동시 부하 → {baseUrl} (barcode={barcode} chute={chute} pidBase={pidBase})");

        var results = await driver.RunConcurrentAsync(jobs);

        foreach (var r in results)
            _out.WriteLine($"[LIVE] pId={r.PId} IF05={r.If05Status}/{r.If05Result} chuteNo={r.ChuteNo} IF09={r.If09Status} IF10={r.If10Status}/{r.If10Result}");

        // 라이브는 관찰이 목적 — 단언은 "모든 IF-05가 200 응답(검증 통과)"만(NG/OK는 라이브 상태 의존).
        Assert.All(results, r => Assert.True(
            r.If05Status == System.Net.HttpStatusCode.OK,
            $"pId={r.PId} IF-05 응답 {r.If05Status}(라이브 검증 통과 기대)"));
        _out.WriteLine($"[LIVE] 완료 — {results.Count} AGV 사이클. orchestrator가 로그·DB·push 육안 관찰(§7).");
    }

    private static int ParseInt(string env, int dflt) =>
        int.TryParse(Environment.GetEnvironmentVariable(env), out var v) ? v : dflt;
}
