using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wcs.Api;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// S-TRACE-LOG-VIEWER 단위 테스트 — 전용 추적 sink + C 흐름 상관.
//   · TraceCorrelator: 등록→C인큐(4)→C디큐(5)→C클리어(6) pId/cSeq 상관 재구성 + FIFO + 미등록 fail-safe.
//   · TraceLogService: 논블로킹 채널 + 전용 Serilog 파일 sink 기입 + 백로그 tail·필터·take clamp +
//     디렉터리 부재 시 생성 후 빈 목록 + 앞머리 [N] 이벤트번호 태그.
//   전부 scratch/temp 디렉터리 사용(실경로 D:\Rcs3dsInterlockingWcsLogs 무접촉·머신 의존 0 — 절대규칙 #7).
// ════════════════════════════════════════════════════════════════════════════
public sealed class TraceLogTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly string _dir;

    public TraceLogTests(ITestOutputHelper output)
    {
        _out = output;
        _dir = Path.Combine(Path.GetTempPath(), "wcs-trace-unit", Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static TraceRecord Rec(int eventNo, int? pId = null, int? cSeq = null, int? chuteNo = null,
        long? destId = null, int? cellNo = null, int? floor = null, string ev = "X") =>
        new(eventNo, ev, DateTimeOffset.Now, pId, cSeq, chuteNo, destId, cellNo, floor, null, null, null);

    private TraceLogService NewService(bool enabled = true) => new(
        Options.Create(new TraceLogOptions
        {
            Enabled                = enabled,
            Directory              = _dir,
            FileNamePattern        = "trace-.log",
            RollingInterval        = "Day",
            FileSizeLimitBytes     = 104_857_600,
            RetainedFileCountLimit = 30,
            BacklogTakeDefault     = 100,
            BacklogTakeMax         = 500,
        }),
        NullLogger<TraceLogService>.Instance);

    // ════════════════════════════════════════════════════════════════════════
    // TraceCorrelator — 피스 흐름(3~6) pId/cSeq 상관 재구성.
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Correlator_PieceFlow_ReconstructsPidCSeqAcross_C_Enqueue_Dequeue_Clear()
    {
        var c = new TraceCorrelator();
        const long destId = 7;

        // RcsController 가 핸드셰이크 직전 등록(pId=100, cellNo=5, chuteNo=30).
        c.RegisterHandshake(destId, pId: 100, cellNo: 5, chuteNo: 30);

        // 이벤트 4(HS_C_SENT, cSeq=1) — FIFO pop 으로 pId 해소.
        var e4 = c.ResolveCSent(destId, cSeq: 1, cellNo: 5, chuteNo: 30);
        Assert.Equal(100, e4.PId);
        Assert.Equal(1, e4.CSeq);
        Assert.Equal(5, e4.CellNo);

        // 이벤트 5(CELL_ASSIGN write, cSeq=1) — cSeq→컨텍스트로 pId 해소.
        var e5 = c.ResolveWrite(destId, cSeq: 1);
        Assert.NotNull(e5);
        Assert.Equal(100, e5!.PId);
        Assert.Equal(5, e5.CellNo);

        // 이벤트 6(C_Flag 1→0) — 미결 C 에서 cSeq/pId/cellNo 해소(델타엔 cSeq 없음).
        var e6 = c.ResolveClear(destId);
        Assert.NotNull(e6);
        Assert.Equal(100, e6!.PId);
        Assert.Equal(1, e6.CSeq);
        Assert.Equal(5, e6.CellNo);

        // 해소 후 정리 — 다음 클리어는 미결 없음 → null.
        Assert.Null(c.ResolveClear(destId));
    }

    [Fact]
    public void Correlator_SerialHandshakes_FifoOrder_MapsEachCSeqToItsPid()
    {
        var c = new TraceCorrelator();
        const long destId = 7;

        // 소터 직렬 — 핸드셰이크 1 완주(등록→C인큐→C디큐→C클리어) 후 핸드셰이크 2 진행(실제 순서).
        //   등록 순서(FIFO)가 cSeq→pId 매핑을 결정. 각 write(이벤트 5)는 다음 C인큐(이벤트 4) 이전에 해소된다.
        c.RegisterHandshake(destId, pId: 100, cellNo: 5, chuteNo: 30);
        var e4a = c.ResolveCSent(destId, cSeq: 1, cellNo: 5, chuteNo: 30);
        Assert.Equal(100, e4a.PId);
        Assert.Equal(100, c.ResolveWrite(destId, 1)!.PId);   // 이벤트 5 — 핸드셰이크 2 시작 전 해소.
        Assert.Equal(100, c.ResolveClear(destId)!.PId);      // 이벤트 6.

        c.RegisterHandshake(destId, pId: 200, cellNo: 6, chuteNo: 30);
        var e4b = c.ResolveCSent(destId, cSeq: 2, cellNo: 6, chuteNo: 30);
        Assert.Equal(200, e4b.PId);
        Assert.Equal(200, c.ResolveWrite(destId, 2)!.PId);
        Assert.Equal(200, c.ResolveClear(destId)!.PId);
    }

    [Fact]
    public void Correlator_AbortedBeforeCSent_DiscardCleansPending_NoMisattribution()
    {
        var c = new TraceCorrelator();
        const long destId = 7;

        // 핸드셰이크 1: 등록됐으나 HS_C_SENT 미도달(시작 OFFLINE·잔류실패·안착OFFLINE·C_Flag대기OFFLINE·
        //   CFlagTimeout 어느 경로든 — DiscardPending 은 cSeq 무관·토큰 소비 여부만 본다). continuation 이 폐기.
        var token1 = c.RegisterHandshake(destId, pId: 100, cellNo: 5, chuteNo: 30);
        Assert.Equal(1, c.PendingCount(destId));
        c.DiscardPending(destId, token1);          // 조기 종결(미소비) → 정확히 제거.
        Assert.Equal(0, c.PendingCount(destId));   // 누수 0(무한 증가 방지).

        // 핸드셰이크 2: 성공 — 자기 pId(200)로 상관. 고아 head 로 인한 200↔100 오귀속 없음(완료조건 #6).
        var token2 = c.RegisterHandshake(destId, pId: 200, cellNo: 6, chuteNo: 30);
        var e4 = c.ResolveCSent(destId, cSeq: 1, cellNo: 6, chuteNo: 30);
        Assert.Equal(200, e4.PId);                 // ★ 이전 피스(100) 아님 — 폐기가 매핑을 밀지 않음.
        Assert.Equal(200, c.ResolveWrite(destId, 1)!.PId);
        Assert.Equal(200, c.ResolveClear(destId)!.PId);
        Assert.Equal(0, c.PendingCount(destId));

        // DiscardPending idempotent — HS_C_SENT 도달(소비)한 token2 를 폐기해도 no-op(다음 피스 무영향).
        c.DiscardPending(destId, token2);
        Assert.Equal(0, c.PendingCount(destId));

        // 다음 피스가 멀쩡히 head — 오귀속 없음.
        c.RegisterHandshake(destId, pId: 300, cellNo: 7, chuteNo: 30);
        Assert.Equal(300, c.ResolveCSent(destId, cSeq: 2, cellNo: 7, chuteNo: 30).PId);
    }

    [Fact]
    public void Correlator_PendingCap_BoundsUnboundedGrowth()
    {
        var c = new TraceCorrelator();
        const long destId = 7;

        // discard 누락(방어 심화 시나리오) — 미소비 등록이 계속 쌓여도 상한(MaxPending=32)으로 bounded.
        for (int i = 0; i < 200; i++) c.RegisterHandshake(destId, pId: 1000 + i, cellNo: 1, chuteNo: 30);
        Assert.True(c.PendingCount(destId) <= 32, $"미소비 등록은 상한으로 bounded 되어야 함(현재 {c.PendingCount(destId)})");
    }

    [Fact]
    public void Correlator_UnregisteredPath_FailSafe_NullOrZeroPid()
    {
        var c = new TraceCorrelator();
        const long destId = 7;

        // 등록 없이 C인큐(OpsController 수동 CellAssign 등) — pId 미상(0), 크래시 0.
        var e4 = c.ResolveCSent(destId, cSeq: 9, cellNo: 3, chuteNo: 30);
        Assert.Equal(0, e4.PId);
        Assert.Equal(9, e4.CSeq);
        Assert.Equal(3, e4.CellNo);

        // 미상 cSeq write → null. 미결 없는 클리어 → null.
        Assert.Null(c.ResolveWrite(destId, cSeq: 999));
        Assert.NotNull(c.ResolveClear(destId));   // e4 가 미결 C 를 세팅했으므로 그건 해소됨.
        Assert.Null(c.ResolveClear(destId));
    }

    // ════════════════════════════════════════════════════════════════════════
    // TraceLogService — 파일 기입 + 백로그 tail·필터·clamp + 디렉터리 부재.
    // ════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Sink_WritesEventNumberedLines_And_BacklogReadsThemBack()
    {
        await using var svc = NewService();
        await svc.StartAsync(CancellationToken.None);

        // 6개 이벤트 각 1건(피스 흐름 3~6 + 층-큐 1·2).
        svc.Log(Rec(1, pId: 100, chuteNo: 30, destId: 7, floor: 2, ev: "TGTFLOOR_ENQUEUE"));
        svc.Log(Rec(2, chuteNo: 30, destId: 7, floor: 2, ev: "TGTFLOOR_DEQUEUE"));
        svc.Log(Rec(3, pId: 100, chuteNo: 30, ev: "IF10_ARRIVAL"));
        svc.Log(Rec(4, pId: 100, cSeq: 1, chuteNo: 30, destId: 7, cellNo: 5, ev: "C_ENQUEUE"));
        svc.Log(Rec(5, pId: 100, cSeq: 1, chuteNo: 30, destId: 7, cellNo: 5, ev: "C_DEQUEUE"));
        svc.Log(Rec(6, pId: 100, cSeq: 1, chuteNo: 30, destId: 7, cellNo: 5, ev: "C_CLEAR"));

        // 비동기 파일 기입 완료 대기(폴링 — 고정 sleep 금지).
        await WaitUntilAsync(() => svc.Read(500, null, null, null).Count >= 6, 5000, "6개 트레이스 기입");

        var all = svc.Read(500, null, null, null);
        Assert.Equal(6, all.Count);
        // 시계열 오름차순(최신 하단) — 이벤트 번호 1~6 순서.
        Assert.Equal(new[] { 1, 2, 3, 4, 5, 6 }, all.Select(r => r.EventNo).ToArray());

        // 앞머리 [N] 이벤트번호 태그 — 실제 파일 줄 확인(fresh evidence). Serilog 가 shared 로 파일을 잡고
        // 있으므로 FileShare.ReadWrite 로 열어 읽는다(백로그 reader 와 동일 경로).
        var file = Directory.GetFiles(_dir, "trace-*.log").Single();
        var lines = ReadSharedLines(file);
        Assert.Equal(6, lines.Length);
        for (int i = 0; i < 6; i++)
            Assert.StartsWith($"[{i + 1}] ", lines[i]);
        _out.WriteLine($"[sink] 파일={Path.GetFileName(file)} 6줄 · 앞머리 태그 [1]~[6] 확인");

        // 피스 흐름 상관 재구성 — pId=100 + cSeq=1 로 이벤트 4·5·6 이어짐.
        var piece = svc.Read(500, null, pId: 100, cSeq: 1);
        Assert.Equal(new[] { 4, 5, 6 }, piece.Select(r => r.EventNo).ToArray());
        Assert.All(piece, r => Assert.Equal(5, r.CellNo));

        // 이벤트번호 필터.
        var only3 = svc.Read(500, eventNo: 3, null, null);
        Assert.Single(only3);
        Assert.Equal(3, only3[0].EventNo);
    }

    [Fact]
    public async Task Backlog_TakeClamp_LimitsReturnedRows()
    {
        await using var svc = NewService();
        await svc.StartAsync(CancellationToken.None);

        for (int i = 0; i < 20; i++)
            svc.Log(Rec(3, pId: 1000 + i, chuteNo: 30, ev: "IF10_ARRIVAL"));
        await WaitUntilAsync(() => svc.Read(500, null, null, null).Count >= 20, 5000, "20건 기입");

        // take=5 → 최근 5건만(시계열 오름차순 — 마지막이 최신).
        var five = svc.Read(5, null, null, null);
        Assert.Equal(5, five.Count);
        Assert.Equal(1015, five[0].PId);   // 최근 5건(1015~1019).
        Assert.Equal(1019, five[^1].PId);

        // take=null → BacklogTakeDefault(100) 이하.
        Assert.True(svc.Read(null, null, null, null).Count <= 100);
        // take>Max → Max(500) 로 clamp(음성 대조: 20건뿐이라 20 반환).
        Assert.Equal(20, svc.Read(100000, null, null, null).Count);
    }

    [Fact]
    public async Task Backlog_MissingDirectory_CreatesAndReturnsEmpty_No500()
    {
        // StartAsync 없이(파일 로거 미초기화) 존재하지 않는 디렉터리에서 Read → 예외 0·빈 목록·디렉터리 생성.
        Assert.False(Directory.Exists(_dir));
        await using var svc = NewService();
        var result = svc.Read(100, null, null, null);
        Assert.Empty(result);
        Assert.True(Directory.Exists(_dir), "백로그 조회가 디렉터리를 생성해야 함(500 금지)");
    }

    [Fact]
    public async Task Disabled_NoOp_LogAndBacklogInert()
    {
        await using var svc = NewService(enabled: false);
        await svc.StartAsync(CancellationToken.None);
        svc.Log(Rec(3, pId: 1));
        Assert.Empty(svc.Read(100, null, null, null));   // 비활성 — 항상 빈 목록.
    }

    // Serilog 가 shared 로 잡고 있는 파일을 FileShare.ReadWrite 로 읽는다(비어있지 않은 줄만).
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

    private static async Task WaitUntilAsync(Func<bool> cond, int timeoutMs, string msg, int pollMs = 25)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        while (!cond())
        {
            if (DateTimeOffset.Now > deadline) Assert.Fail($"WaitUntil 타임아웃({timeoutMs}ms): {msg}");
            await Task.Delay(pollMs);
        }
    }
}
