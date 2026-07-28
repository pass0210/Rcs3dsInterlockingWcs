using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api;
using Wcs.Data;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests.E2E;

// ════════════════════════════════════════════════════════════════════════════
// S-TRACE-LOG-VIEWER E2E — 전(全) 피스 흐름 계측(E1/E2).
//
//   실 Sim + WCS 기동 → IF-05(→ ① TgtFloor 인큐, 사이클 완료 시 ② 디큐) → IF-10(→ ③ 도착) →
//   핸드셰이크(→ ④ C 인큐 → ⑤ C 디큐/write → Sim C_Flag 1→0 → ⑥ C 클리어 관측) →
//   전용 파일에 6개 이벤트가 이벤트번호 태그로 기입되고 pId+(chuteNo,cSeq)로 한 흐름 재구성됨을 검증.
//   회귀 0(E2): 기존 operation_log·sorter_command 계측 종전대로(additive).
//
//   격리: per-test scratch 트레이스 디렉터리(실경로 D:\ 무접촉·절대규칙 #7). 백로그는 신규 REST
//   (GET /api/monitor/trace)로 읽어 파일·REST·상관 3축을 한 테스트에 병치(fresh evidence).
// ════════════════════════════════════════════════════════════════════════════

[Collection("RealSimSerial")]
public class E2EGroupN_TraceLogTests
{
    private readonly ITestOutputHelper _out;
    public E2EGroupN_TraceLogTests(ITestOutputHelper output) => _out = output;

    // 백엔드 TraceRecord 미러(카멜케이스 — GetFromJsonAsync 기본 web 옵션으로 바인딩).
    private sealed record TraceDto(
        int EventNo, string Event, DateTimeOffset At, int? PId, int? CSeq, int? ChuteNo,
        long? DestId, int? CellNo, int? Floor, int? InductionNo, string? Trigger, string? Detail);

    [Fact]
    public async Task N1_SinglePiece_SixTraceEvents_Numbered_And_Correlated()
    {
        var traceDir = Path.Combine(Path.GetTempPath(), "wcs-trace-e2e", Guid.NewGuid().ToString("N"));
        await using var rcs = await FakeChuteStateServer.StartAsync();
        // 인덕션 1→층2, Sim 초기 층2(즉시 정렬) — 사이클 완료 시 head(2)==CurFloor(2) pop(이벤트 2) 성립.
        // simLoopMs=150: Sim 이 C_Flag=1 을 150ms 유지(현장 PLC 처럼) → WCS 30ms 폴이 C_Flag 1→0 델타를
        //   결정적으로 관측(이벤트 6). 기본 10ms 는 즉시 클리어라 폴이 놓쳐 이벤트 6 이 비결정적이므로 상향.
        await using var factory = new E2EWebApplicationFactory(
            rcsBaseUrl: rcs.BaseUrl, initialCurFloor: 2, traceLogDir: traceDir, simLoopMs: 150);
        await factory.StartSimsAsync();
        var client = factory.CreateClient();
        await E2EWait.UntilAsync(() => factory.IsSorterOnline(factory.PrimarySorter.DestinationId), 5000, "소터 Online");

        long destId = factory.PrimarySorter.DestinationId;
        const int pId = 26301;

        var driver = MultiAgvDriver.ForFactory(factory);
        await driver.RunSingleAsync(new AgvJob(pId, 1, "TEST-BARCODE-3", E2EWebApplicationFactory.DefaultSorterChuteNo, InductionNo: 1));

        // 핸드셰이크 완료(ground-truth) — sorter_command COMPLETED.
        await E2EWait.UntilAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            return await db.SorterCommands.AnyAsync(c => c.Status == SorterCommandStatus.COMPLETED);
        }, 8000, "sorter_command COMPLETED");

        // 전용 파일에 6개 이벤트가 전부 기입될 때까지 폴링(비동기 파일 writer·pop 사이클 여유 — 고정 sleep 금지).
        async Task<IReadOnlyList<TraceDto>> ReadAllAsync()
            => (await client.GetFromJsonAsync<List<TraceDto>>("/api/monitor/trace?take=500"))!;

        await E2EWait.UntilAsync(async () =>
        {
            var recs = await ReadAllAsync();
            return recs.Select(r => r.EventNo).Distinct().OrderBy(n => n).SequenceEqual(new[] { 1, 2, 3, 4, 5, 6 });
        }, 12000, "전용 파일에 6개 이벤트(1~6) 전부 기입");

        var all = await ReadAllAsync();
        _out.WriteLine("[N1] trace 레코드:");
        foreach (var r in all)
            _out.WriteLine($"  [{r.EventNo}] {r.Event} pId={r.PId} cSeq={r.CSeq} chute={r.ChuteNo} cell={r.CellNo} floor={r.Floor}");

        // ── 이벤트 번호 정확 태깅(완료조건 6) — 6종 전부 존재 ──────────────────────
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, all.Select(r => r.EventNo).Distinct().OrderBy(n => n).ToArray());

        // ── 층-큐 흐름(1·2) ────────────────────────────────────────────────────────
        var e1 = all.First(r => r.EventNo == 1);
        Assert.Equal(pId, e1.PId);                 // 인큐는 트리거 pId best-effort.
        Assert.Equal(2, e1.Floor);                 // 인덕션 1 → 층 2.
        Assert.Equal("IF05", e1.Trigger);
        var e2 = all.First(r => r.EventNo == 2);
        Assert.Equal(2, e2.Floor);                 // 디큐 층(소터+층 상관 — pId 미포함, 수용된 경계).
        Assert.Null(e2.PId);

        // ── 피스 흐름(3~6) 상관 재구성 — pId + (chuteNo, cSeq) ──────────────────────
        var e3 = all.First(r => r.EventNo == 3);
        var e4 = all.First(r => r.EventNo == 4);
        var e5 = all.First(r => r.EventNo == 5);
        var e6 = all.First(r => r.EventNo == 6);

        // pId 전파(3~6 동일) — 한 피스 흐름.
        Assert.Equal(pId, e3.PId);
        Assert.Equal(pId, e4.PId);
        Assert.Equal(pId, e5.PId);
        Assert.Equal(pId, e6.PId);

        // cSeq 일치(4·5·6 = 같은 C 핸드셰이크 1건) — 기술 조인키.
        Assert.NotNull(e4.CSeq);
        Assert.Equal(e4.CSeq, e5.CSeq);
        Assert.Equal(e4.CSeq, e6.CSeq);

        // cellNo·chuteNo 일치(피스 흐름 전 레코드가 셀·목적지를 담음 — 계약 WHAT 요구).
        Assert.Equal(e4.CellNo, e5.CellNo);
        Assert.Equal(e4.CellNo, e6.CellNo);
        Assert.All(new[] { e4, e5, e6 }, r => Assert.Equal(destId, r.DestId));
        Assert.All(new[] { e3, e4, e5, e6 }, r => Assert.Equal(E2EWebApplicationFactory.DefaultSorterChuteNo, r.ChuteNo));

        // ── 회귀 0(E2): 전용 파일 계측이 기존 operation_log 를 대체하지 않음(additive) ──
        using (var db = factory.CreateDbScope())
        {
            Assert.True(await db.OperationLogs.AnyAsync(l => l.Category == OperationLogCategory.HANDSHAKE),
                "기존 operation_log HANDSHAKE 계측이 종전대로 유지돼야(회귀 0·additive)");
            Assert.True(await db.SorterCommands.AnyAsync(c => c.Status == SorterCommandStatus.COMPLETED));
        }

        _out.WriteLine($"[N1] 6개 이벤트 번호 정확·pId={pId} cSeq={e4.CSeq} cell={e4.CellNo} 상관 재구성 + additive 회귀 0");

        try { Directory.Delete(traceDir, recursive: true); } catch { }
    }

    // ════════════════════════════════════════════════════════════════════════
    // N2 (fix 회귀): HS_C_SENT 미도달(OFFLINE-before-C) 핸드셰이크 후 같은 소터에서 성공 핸드셰이크.
    //   조기 종결 시 TraceCorrelator._pending 이 discard 로 정리돼 (a) 누수 0, (b) 성공 피스의 이벤트 4·5·6 이
    //   자기 pId 로 상관(고아 head 로 인한 이전 피스 pId 오귀속 없음 — 완료조건 #6 방어).
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task N2_AbortedBeforeCSent_ThenSuccess_NoPendingLeak_NoMisattribution()
    {
        var traceDir = Path.Combine(Path.GetTempPath(), "wcs-trace-e2e", Guid.NewGuid().ToString("N"));
        await using var rcs = await FakeChuteStateServer.StartAsync();
        await using var factory = new E2EWebApplicationFactory(
            rcsBaseUrl: rcs.BaseUrl, initialCurFloor: 2, traceLogDir: traceDir, simLoopMs: 150);
        await factory.StartSimsAsync();
        var client = factory.CreateClient();
        long destId = factory.PrimarySorter.DestinationId;
        var correlator = factory.Services.GetRequiredService<TraceCorrelator>();
        var driver = MultiAgvDriver.ForFactory(factory);

        await E2EWait.UntilAsync(() => factory.IsSorterOnline(destId), 5000, "소터 Online");

        const int abortedPid = 26401;   // OFFLINE-before-C 로 종결될 피스.
        const int successPid = 26402;   // 이후 성공 피스.

        // ── (1) Sim 종료 → OFFLINE 전이 → 이 상태의 IF-10 은 핸드셰이크 시작 시 OFFLINE 으로 HS_C_SENT 前 종결 ──
        await factory.PrimarySorter.Sim.StopAsync();
        await E2EWait.UntilAsync(() => factory.SorterSnapshot(destId)?.Online == false, 5000, "소터 OFFLINE 전이");

        await driver.RunSingleAsync(new AgvJob(abortedPid, 1, "TEST-BARCODE-3", E2EWebApplicationFactory.DefaultSorterChuteNo, InductionNo: 1));

        // 조기 종결 continuation 이 DiscardPending 을 수행할 때까지 대기 — _pending 정리(누수 0).
        await E2EWait.UntilAsync(() => correlator.PendingCount(destId) == 0, 6000, "조기종결 후 _pending 정리(discard)");

        // ── (2) Sim 재기동 → Online 복구 → 성공 핸드셰이크 ──────────────────────────
        await factory.RestartSimAsync(0);
        await E2EWait.UntilAsync(() => factory.IsSorterOnline(destId), 6000, "소터 Online 복구");

        await driver.RunSingleAsync(new AgvJob(successPid, 1, "TEST-BARCODE-3", E2EWebApplicationFactory.DefaultSorterChuteNo, InductionNo: 1));
        await E2EWait.UntilAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            return await db.SorterCommands.AnyAsync(c => c.Status == SorterCommandStatus.COMPLETED);
        }, 8000, "성공 핸드셰이크 COMPLETED");

        // 성공 피스의 C 흐름 이벤트 4·5·6 이 전용 파일에 기입될 때까지 대기.
        async Task<IReadOnlyList<TraceDto>> ReadAllAsync()
            => (await client.GetFromJsonAsync<List<TraceDto>>("/api/monitor/trace?take=500"))!;

        await E2EWait.UntilAsync(async () =>
        {
            var recs = await ReadAllAsync();
            return recs.Any(r => r.EventNo == 4) && recs.Any(r => r.EventNo == 5) && recs.Any(r => r.EventNo == 6);
        }, 12000, "성공 피스 C 흐름(4·5·6) 기입");

        var all = await ReadAllAsync();
        _out.WriteLine("[N2] trace 레코드:");
        foreach (var r in all)
            _out.WriteLine($"  [{r.EventNo}] {r.Event} pId={r.PId} cSeq={r.CSeq} cell={r.CellNo}");

        // (a) 누수 0 — 조기종결 토큰 폐기 + 성공 토큰 소비 → _pending 비어 있음.
        Assert.Equal(0, correlator.PendingCount(destId));

        // (b) C 흐름(4·5·6)은 성공 피스 pId 로만 상관 — 조기종결 피스(HS_C_SENT 미도달)는 4·5·6 을 낳지 않았고,
        //     고아 head 폐기로 매핑이 밀리지 않아 이전 pId(abortedPid) 오귀속이 없다.
        Assert.All(all.Where(r => r.EventNo is 4 or 5 or 6), r =>
        {
            Assert.Equal(successPid, r.PId);
            Assert.NotEqual(abortedPid, r.PId);
        });

        // 조기종결 피스는 이벤트 3(도착)까지만 남고 이벤트 4·5·6 은 없음(HS_C_SENT 미도달).
        Assert.DoesNotContain(all, r => r.EventNo == 4 && r.PId == abortedPid);

        _out.WriteLine($"[N2] 조기종결(pId={abortedPid}) 후 _pending=0(누수 0) · 성공(pId={successPid}) C흐름 4·5·6 자기 pId 상관(오귀속 0)");

        try { Directory.Delete(traceDir, recursive: true); } catch { }
    }
}
