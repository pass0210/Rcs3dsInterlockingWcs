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

        // ★ S-TRACE-READY-PUSH-AND-DEFAULT: 이제 같은 흐름에 additive 이벤트 7/8/9/10(Ready 전이·IF-08 push)이
        //   함께 발화될 수 있다(분류 사이클의 Ready 토글 + 수용상태 push). 기존 6 이벤트(1~6)의 발화·상관은
        //   불변이므로 "정확히 {1..6}"이 아니라 "1~6 이 모두 포함(superset)"으로 검증한다(회귀 0·additive).
        await E2EWait.UntilAsync(async () =>
        {
            var recs = await ReadAllAsync();
            return recs.Select(r => r.EventNo).ToHashSet().IsSupersetOf(new[] { 1, 2, 3, 4, 5, 6 });
        }, 12000, "전용 파일에 6개 이벤트(1~6) 전부 기입");

        var all = await ReadAllAsync();
        _out.WriteLine("[N1] trace 레코드:");
        foreach (var r in all)
            _out.WriteLine($"  [{r.EventNo}] {r.Event} pId={r.PId} cSeq={r.CSeq} chute={r.ChuteNo} cell={r.CellNo} floor={r.Floor}");

        // ── 이벤트 번호 정확 태깅(완료조건 6) — 6종 전부 존재(additive 7~10 공존 허용·superset) ────
        var distinctEvents = all.Select(r => r.EventNo).ToHashSet();
        Assert.All(new[] { 1, 2, 3, 4, 5, 6 }, n => Assert.Contains(n, distinctEvents));

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

    // ════════════════════════════════════════════════════════════════════════
    // N3 (S-TRACE-READY-PUSH-AND-DEFAULT · E1/E2): Ready 전이 → IF-08 push 전송 관통.
    //   실 Sim 으로 소터 Ready 1→0·0→1 을 유도 → (PLC 폴)이벤트 7·9 + (관찰 루프→실 PushAsync PUT →
    //   fake RCS)이벤트 8·10 이 전용 파일에 raw 로 기입되고 같은 chuteNo 로 상관됨을 실증.
    //   결정성(DepositDecider·조사 C): 부트스트랩 accept=true(정렬)→10, Ready 1→0→accept false→8+이벤트7,
    //   Ready 0→1→accept true→10+이벤트9. 회귀 0: operation_log CHUTESTATE_PUSH 종전대로(additive).
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task N3_ReadyEdges_DriveIf08Push_Events7_8_9_10_FileAndCorrelation()
    {
        var traceDir = Path.Combine(Path.GetTempPath(), "wcs-trace-e2e", Guid.NewGuid().ToString("N"));
        await using var rcs = await FakeChuteStateServer.StartAsync();
        // 레거시 단일 호스트(rcsBaseUrl) → 소터 push 활성. initialCurFloor=2(정렬 — 부트스트랩 accept=true).
        await using var factory = new E2EWebApplicationFactory(
            rcsBaseUrl: rcs.BaseUrl, initialCurFloor: 2, traceLogDir: traceDir, sorterObserveIntervalMs: 30);
        await factory.StartSimsAsync();
        var client = factory.CreateClient();
        long destId = factory.PrimarySorter.DestinationId;
        int  chuteNo = E2EWebApplicationFactory.DefaultSorterChuteNo;

        await E2EWait.UntilAsync(() => factory.IsSorterOnline(destId), 5000, "소터 Online");

        async Task<IReadOnlyList<TraceDto>> ReadAllAsync()
            => (await client.GetFromJsonAsync<List<TraceDto>>("/api/monitor/trace?take=500"))!;

        // 소터 부트스트랩 push(accept=true → next_state 3 = 이벤트 10) 도달 대기 — Acked=3 확립·파이프 개통 확인.
        await E2EWait.UntilAsync(async () => (await ReadAllAsync()).Any(r => r.EventNo == 10 && r.ChuteNo == chuteNo), 8000,
            "소터 부트스트랩 IF-08 push(이벤트 10) 도달");

        // ── Ready 1→0 유도 → 이벤트 7(폴 관측) + 이벤트 8(push 3→2). Ready 를 되돌리기 전에 소터 이벤트 8 을
        //    반드시 확인해야 한다 — 안 그러면 관찰 루프가 3→2→3 을 합쳐 push 2 가 유실될 수 있다(결정성). ──
        factory.PrimarySorter.Sim.SetReady(false);
        await E2EWait.UntilAsync(async () => (await ReadAllAsync()).Any(r => r.EventNo == 7 && r.ChuteNo == chuteNo), 8000,
            "소터 Ready 1→0(이벤트 7) 관측");
        await E2EWait.UntilAsync(async () => (await ReadAllAsync()).Any(r => r.EventNo == 8 && r.ChuteNo == chuteNo), 8000,
            "소터 Ready 1→0 유발 IF-08 push(이벤트 8) 도달");

        // ── Ready 0→1 유도 → 이벤트 9(폴 관측) + 이벤트 10(push 2→3) ────────────────────
        factory.PrimarySorter.Sim.SetReady(true);
        await E2EWait.UntilAsync(async () => (await ReadAllAsync()).Any(r => r.EventNo == 9 && r.ChuteNo == chuteNo), 8000,
            "소터 Ready 0→1(이벤트 9) 관측");
        await E2EWait.UntilAsync(async () => (await ReadAllAsync()).Any(r => r.EventNo == 10 && r.ChuteNo == chuteNo
                && r.Detail!.Contains("\"next_state\":3")), 8000,
            "소터 Ready 0→1 유발 IF-08 push(이벤트 10) 도달");

        var all = await ReadAllAsync();
        _out.WriteLine("[N3] trace 레코드:");
        foreach (var r in all.Where(r => r.EventNo is 7 or 8 or 9 or 10))
            _out.WriteLine($"  [{r.EventNo}] {r.Event} chute={r.ChuteNo} floor={r.Floor} detail={r.Detail}");

        // ★ 시드가 소터(chuteNo=30) 외 CHUTE 도 생성해 부트스트랩 push(이벤트 8/10)가 타 chuteNo 로도 나간다.
        //   이 소터의 Ready↔push 관통을 검증하는 것이므로 chuteNo==30(소터)로 좁혀 선택한다(상관 키 = chuteNo).
        //   Ready 전이(7·9)는 소터만 발화하나 방어적으로 동일 chuteNo 로 좁힌다.

        // ── 이벤트 7·9(Ready 전이) — old/new·curFloor 정확·소터 scope(pId 없음) ──────
        var e7 = all.First(r => r.EventNo == 7 && r.ChuteNo == chuteNo);
        Assert.Equal(destId, e7.DestId);
        Assert.Null(e7.PId);
        Assert.Contains("\"old\":1", e7.Detail);
        Assert.Contains("\"new\":0", e7.Detail);
        Assert.Contains("\"curFloor\"", e7.Detail);

        var e9 = all.First(r => r.EventNo == 9 && r.ChuteNo == chuteNo);
        Assert.Contains("\"old\":0", e9.Detail);
        Assert.Contains("\"new\":1", e9.Detail);

        // ── 이벤트 8·10(IF-08 push) — next_state 정확·같은 chuteNo 로 Ready 이벤트와 상관 ──
        var e8 = all.First(r => r.EventNo == 8 && r.ChuteNo == chuteNo);   // 이벤트 7 과 같은 chuteNo → 상관·지연 산출(비인과).
        Assert.Null(e8.PId);
        Assert.Contains("\"next_state\":2", e8.Detail);
        Assert.Contains("\"result\":\"OK\"", e8.Detail);

        var e10 = all.First(r => r.EventNo == 10 && r.ChuteNo == chuteNo);
        Assert.Contains("\"next_state\":3", e10.Detail);

        // ── 회귀 0(additive): 전용 파일 계측이 기존 operation_log CHUTESTATE_PUSH 를 대체하지 않음 ──
        using (var db = factory.CreateDbScope())
        {
            Assert.True(await db.OperationLogs.AnyAsync(l => l.Category == OperationLogCategory.API && l.Action == "CHUTESTATE_PUSH"),
                "기존 operation_log CHUTESTATE_PUSH 계측이 종전대로 유지돼야(회귀 0·additive)");
        }

        // ── 파일 raw 태그([7]/[8]/[9]/[10]) 확인 — REST 뿐 아니라 파일 sink 도 신규 이벤트 수용 ──
        var files = Directory.GetFiles(traceDir, "trace-*.log");
        Assert.NotEmpty(files);
        var lines = files.SelectMany(ReadSharedLines).ToList();
        Assert.Contains(lines, l => l.StartsWith("[7] "));
        Assert.Contains(lines, l => l.StartsWith("[8] "));
        Assert.Contains(lines, l => l.StartsWith("[9] "));
        Assert.Contains(lines, l => l.StartsWith("[10] "));

        _out.WriteLine($"[N3] Ready 7·9 + push 8·10 파일·REST 관통 · chuteNo={chuteNo} 상관 · operation_log additive");

        try { Directory.Delete(traceDir, recursive: true); } catch { }
    }

    private static string[] ReadSharedLines(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        var list = new List<string>();
        string? line;
        while ((line = sr.ReadLine()) is not null)
            if (line.Length > 0) list.Add(line);
        return list.ToArray();
    }
}
