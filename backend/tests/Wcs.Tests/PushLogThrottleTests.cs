using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Wcs.Api;
using Wcs.Data;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// S-IF08-PUSH-LOG-THROTTLE — 반복-실패 push 로그 억제 검증(로깅만 조절·delivery 불변)
//
// RCS 호스트가 죽어 있으면 IF-08 chute-state push 재시도-소진 실패가 매 복구-하트비트 주기마다 세 sink
//   (operation_log WARN "FAIL" + 트레이스 이벤트 8/10 result:"FAIL" + Serilog LogError)에 폭주한다.
//   이 억제는 "반복되는 같은 실패"의 **로깅만** 막는다 — push 재시도·재발신·delivery·반환값은 전부 불변.
//
// 3계층 증거(층별 분리):
//   1. PushFailureLogThrottleStateTests — 억제 판정(Decide) 순수 시맨틱을 결정적 clock 으로(첫=Emit /
//      반복=Suppress / next_state 전이=Emit / 요약 주기=Summary / 리셋=재무장 / off=항상 Emit).
//   2. PushLogThrottleClientGateTests — 클라이언트가 게이트 판정대로 **세 sink 를 함께** emit/억제/요약하고,
//      delivery(재시도 HTTP 시도)는 판정과 무관하게 계속됨(가짜 RCS 수신 본문으로 실증).
//   3. PushLogThrottleEndToEndTests — 실 DestinationStatusPusher 관찰 루프 + 실 ChuteStatePushClient +
//      다운 상태 가짜 RCS 수신 서버 + 실 operation_log(EF, in-memory SQLite) + capturing 트레이스 sink 를
//      한 스택에 결선(VS-E1). N주기 재발신에도 FAIL 1건 / delivery≥N / 복구 1건+freeze / 리셋 후 재실패 1건.
// ════════════════════════════════════════════════════════════════════════════

// ── 테스트 더블 ──────────────────────────────────────────────────────────────

/// <summary>operation_log 기록을 detail 까지 스레드안전 수집(FAIL/SUMMARY/OK 카운트 분리). 클라이언트 emit 경계 계측.</summary>
public sealed class DetailCapturingOperationLogger : IOperationLogger
{
    private readonly ConcurrentQueue<(OperationLogCategory cat, string action, OperationLogLevel level, string? detail)> _e = new();
    public IReadOnlyList<(OperationLogCategory cat, string action, OperationLogLevel level, string? detail)> Entries => _e.ToArray();

    public void Log(OperationLog entry) => _e.Enqueue((entry.Category, entry.Action, entry.Level, entry.Detail));
    public void Log(OperationLogCategory category, string action, OperationLogLevel level = OperationLogLevel.INFO,
        int? sorterChuteNo = null, long? destinationId = null, string? barcode = null, int? pId = null, string? detail = null)
        => _e.Enqueue((category, action, level, detail));

    public int CountFail()    => Entries.Count(e => e.action == "CHUTESTATE_PUSH" && e.level == OperationLogLevel.WARN && Has(e.detail, "\"result\":\"FAIL\""));
    public int CountSummary() => Entries.Count(e => e.action == "CHUTESTATE_PUSH" && Has(e.detail, "\"result\":\"SUMMARY\""));
    private static bool Has(string? d, string s) => d is not null && d.Contains(s);
}

/// <summary>ILogger 캡처 — 레벨별 카운트(재시도-소진 Serilog LogError(sink c) 억제 실증).</summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    private readonly ConcurrentQueue<(LogLevel level, string message)> _e = new();
    public IReadOnlyList<(LogLevel level, string message)> Entries => _e.ToArray();

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _e.Enqueue((logLevel, formatter(state, exception)));

    public int CountError()       => Entries.Count(e => e.level == LogLevel.Error);
    public int CountSummaryWarn() => Entries.Count(e => e.level == LogLevel.Warning && e.message.Contains("아직 실패 중(요약)"));

    private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
}

/// <summary>프로그래밍된 판정을 순서대로 반환하는 억제 게이트(클라이언트가 게이트에 순응하는지 격리 검증).</summary>
public sealed class ProgrammedThrottle : IPushFailureLogThrottle
{
    private readonly Queue<PushFailureLogAction> _actions;
    public int Calls { get; private set; }
    public ProgrammedThrottle(params PushFailureLogAction[] actions) => _actions = new Queue<PushFailureLogAction>(actions);
    public PushFailureLogAction OnFailure(int nextState)
    {
        Calls++;
        return _actions.Count > 0 ? _actions.Dequeue() : PushFailureLogAction.Suppress;
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 1. 억제 판정(Decide) 순수 시맨틱 — 결정적 clock(벽시계 없이 요약 주기 검증)
// ════════════════════════════════════════════════════════════════════════════
public sealed class PushFailureLogThrottleStateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    // VS-B1/VS-B2: 첫 실패 = Emit, 반복 = Suppress.
    [Fact]
    public void FirstFailure_Emits_ThenRepeatSuppresses()
    {
        var s = new PushFailureLogThrottleState();
        Assert.Equal(PushFailureLogAction.Emit,     s.Decide(2, suppressEnabled: true, summaryIntervalMs: 300000, T0));
        Assert.Equal(PushFailureLogAction.Suppress, s.Decide(2, true, 300000, T0.AddMilliseconds(50)));
        Assert.Equal(PushFailureLogAction.Suppress, s.Decide(2, true, 300000, T0.AddMilliseconds(100)));
    }

    // VS-B5(핵심 시맨틱): next_state 전이(2↔3)는 같은 route라도 새 첫 실패 = Emit.
    [Fact]
    public void NextStateTransition_ReEmits_SameRoute()
    {
        var s = new PushFailureLogThrottleState();
        Assert.Equal(PushFailureLogAction.Emit,     s.Decide(2, true, 300000, T0));
        Assert.Equal(PushFailureLogAction.Suppress, s.Decide(2, true, 300000, T0.AddMilliseconds(10)));
        Assert.Equal(PushFailureLogAction.Emit,     s.Decide(3, true, 300000, T0.AddMilliseconds(20)));  // 2→3 = 새 전이.
        Assert.Equal(PushFailureLogAction.Suppress, s.Decide(3, true, 300000, T0.AddMilliseconds(30)));
        Assert.Equal(PushFailureLogAction.Emit,     s.Decide(2, true, 300000, T0.AddMilliseconds(40)));  // 3→2 = 또 새 전이.
    }

    // VS-B4(핵심 시맨틱): 복구(Reset) 후 재실패는 **같은 next_state여도** 새 첫 실패 = Emit(신호 재무장).
    [Fact]
    public void Reset_ReArms_SameNextStateEmitsAgain()
    {
        var s = new PushFailureLogThrottleState();
        Assert.Equal(PushFailureLogAction.Emit,     s.Decide(2, true, 300000, T0));
        Assert.Equal(PushFailureLogAction.Suppress, s.Decide(2, true, 300000, T0.AddMilliseconds(10)));
        s.Reset();   // push 성공(복구).
        Assert.Equal(PushFailureLogAction.Emit,     s.Decide(2, true, 300000, T0.AddMilliseconds(20)));  // 같은 2여도 재무장 = Emit.
    }

    // OQ-1/C8: 저빈도 요약은 설정 주기마다 1건(매 실패마다가 아님) · 전이 리셋.
    [Fact]
    public void Summary_FiresOncePerInterval_NotEveryFailure()
    {
        var s = new PushFailureLogThrottleState();
        const int period = 1000;
        Assert.Equal(PushFailureLogAction.Emit,     s.Decide(2, true, period, T0));                     // 첫 실패.
        Assert.Equal(PushFailureLogAction.Suppress, s.Decide(2, true, period, T0.AddMilliseconds(500))); // 주기 전 = 억제.
        Assert.Equal(PushFailureLogAction.Suppress, s.Decide(2, true, period, T0.AddMilliseconds(999)));
        Assert.Equal(PushFailureLogAction.Summary,  s.Decide(2, true, period, T0.AddMilliseconds(1000))); // 주기 도래 = 요약.
        Assert.Equal(PushFailureLogAction.Suppress, s.Decide(2, true, period, T0.AddMilliseconds(1500))); // 요약 직후 = 다시 억제.
        Assert.Equal(PushFailureLogAction.Summary,  s.Decide(2, true, period, T0.AddMilliseconds(2000))); // 다음 주기 = 요약.
    }

    // 절대규칙 #7: 억제 off → 매 실패 Emit(현행 동작 보존).
    [Fact]
    public void SuppressDisabled_AlwaysEmits()
    {
        var s = new PushFailureLogThrottleState();
        Assert.Equal(PushFailureLogAction.Emit, s.Decide(2, suppressEnabled: false, 300000, T0));
        Assert.Equal(PushFailureLogAction.Emit, s.Decide(2, false, 300000, T0.AddMilliseconds(1)));
        Assert.Equal(PushFailureLogAction.Emit, s.Decide(2, false, 300000, T0.AddMilliseconds(2)));
    }

    // 요약 비활성(≤0)이어도 첫 실패는 남고 반복은 억제(완전 무음은 아님 — 첫 1건 유지).
    [Fact]
    public void SummaryDisabled_ZeroInterval_FirstEmits_RepeatsSuppressed()
    {
        var s = new PushFailureLogThrottleState();
        Assert.Equal(PushFailureLogAction.Emit,     s.Decide(2, true, summaryIntervalMs: 0, T0));
        Assert.Equal(PushFailureLogAction.Suppress, s.Decide(2, true, 0, T0.AddDays(1)));  // 주기 무한대여도 억제 유지.
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 2. 클라이언트가 게이트 판정에 순응 — 세 sink 함께 emit/억제/요약 + delivery 불변
// ════════════════════════════════════════════════════════════════════════════
public sealed class PushLogThrottleClientGateTests
{
    private readonly ITestOutputHelper _out;
    public PushLogThrottleClientGateTests(ITestOutputHelper output) => _out = output;

    private static ChuteStatePushClient BuildClient(
        string? baseUrl, CapturingLogger<ChuteStatePushClient> log, DetailCapturingOperationLogger opLog,
        CapturingTraceLogger trace, int retryCount = 2, int retryBaseDelayMs = 10)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(ChuteStatePushClient.HttpClientName);
        var sp = services.BuildServiceProvider();
        var httpFactory = sp.GetRequiredService<IHttpClientFactory>();

        var opts = Options.Create(new WcsOptions
        {
            ChuteStatePush = new ChuteStatePushOptions
            {
                BaseUrl = baseUrl, Path = "/api/UpdateChuteState",
                RetryCount = retryCount, RetryBaseDelayMs = retryBaseDelayMs,
                RetryMaxDelayMs = retryBaseDelayMs * 4, HttpTimeoutMs = 2000,
            },
        });
        return new ChuteStatePushClient(httpFactory, opts, log, opLog, trace);
    }

    private static int TraceFail8(CapturingTraceLogger t) =>
        t.Records.Count(r => r.EventNo == 8 && r.Detail is not null && r.Detail.Contains("\"result\":\"FAIL\""));

    // 게이트 Emit → 세 sink 각 1건. Suppress → 추가 0. Summary → 요약 1건·FAIL 추가 0. delivery(HTTP 시도)는 매번 계속.
    [Fact]
    public async Task ClientObeysGate_EmitSuppressSummary_ThreeSinksTogether_DeliveryUnthrottled()
    {
        await using var srv = await FakeChuteStateServer.StartAsync();
        srv.SetMode(ChuteStateRespMode.Reject503);   // 항상 실패 → 재시도 소진.
        var log   = new CapturingLogger<ChuteStatePushClient>();
        var opLog = new DetailCapturingOperationLogger();
        var trace = new CapturingTraceLogger();
        var client = BuildClient(srv.BaseUrl, log, opLog, trace, retryCount: 2);
        var payload = new ChuteStatePushPayload(new[] { 30 }, new[] { 2 });

        var throttle = new ProgrammedThrottle(
            PushFailureLogAction.Emit, PushFailureLogAction.Suppress, PushFailureLogAction.Summary);

        // ① 첫 실패(Emit) → 세 sink 각 1건. delivery = 1+retry(2) = 3 시도.
        Assert.False(await client.PushAsync(payload, srv.BaseUrl, throttle));
        Assert.Equal(1, opLog.CountFail());
        Assert.Equal(1, TraceFail8(trace));
        Assert.Equal(1, log.CountError());
        Assert.Equal(3, srv.All.Count);

        // ② 반복 실패(Suppress) → 세 sink 추가 0. delivery 는 또 3 시도(재시도 불변).
        Assert.False(await client.PushAsync(payload, srv.BaseUrl, throttle));
        Assert.Equal(1, opLog.CountFail());
        Assert.Equal(1, TraceFail8(trace));
        Assert.Equal(1, log.CountError());
        Assert.Equal(6, srv.All.Count);   // ★ delivery 계속(로그만 억제).

        // ③ 요약(Summary) → 요약 1건, FAIL/트레이스/Error 추가 0. delivery 또 3.
        Assert.False(await client.PushAsync(payload, srv.BaseUrl, throttle));
        Assert.Equal(1, opLog.CountFail());       // FAIL 추가 0.
        Assert.Equal(1, opLog.CountSummary());    // 요약 1건(operation_log result:SUMMARY).
        Assert.Equal(1, log.CountSummaryWarn());  // Serilog 요약 1건(Fail-Loud — 완전 무음 아님).
        Assert.Equal(1, log.CountError());        // Error 추가 0.
        Assert.Equal(1, TraceFail8(trace));       // 트레이스 FAIL 추가 0.
        Assert.Equal(9, srv.All.Count);           // delivery 계속.

        Assert.Equal(3, throttle.Calls);
        _out.WriteLine("[gate] Emit/Suppress/Summary — FAIL 1·SUMMARY 1·Error 1·trace-FAIL 1, delivery 9(=3×3 불변)");
    }

    // 게이트 미주입(throttle=null) → 억제 없음(현행 동작): 매 실패마다 세 sink 로깅.
    [Fact]
    public async Task NoThrottle_LegacyBehavior_EveryFailureLogs()
    {
        await using var srv = await FakeChuteStateServer.StartAsync();
        srv.SetMode(ChuteStateRespMode.Reject503);
        var log   = new CapturingLogger<ChuteStatePushClient>();
        var opLog = new DetailCapturingOperationLogger();
        var trace = new CapturingTraceLogger();
        var client = BuildClient(srv.BaseUrl, log, opLog, trace, retryCount: 1);
        var payload = new ChuteStatePushPayload(new[] { 30 }, new[] { 2 });

        Assert.False(await client.PushAsync(payload, srv.BaseUrl, (IPushFailureLogThrottle?)null));
        Assert.False(await client.PushAsync(payload, srv.BaseUrl, (IPushFailureLogThrottle?)null));

        Assert.Equal(2, opLog.CountFail());   // 억제 없음 → 매 실패 1건씩.
        Assert.Equal(2, log.CountError());
        Assert.Equal(2, TraceFail8(trace));
        _out.WriteLine("[gate] throttle=null → 현행 동작(매 실패 로깅) 보존");
    }
}

// ════════════════════════════════════════════════════════════════════════════
// 3. End-to-end(VS-E1) — 실 Pusher + 실 Client + 다운 가짜 RCS + 실 operation_log(EF) + capturing 트레이스
// [Collection("RealSimSerial")] — 정확-카운트 단언을 무거운 실-Sim 테스트와 직렬화(병렬 부하 flake 제거).
// ════════════════════════════════════════════════════════════════════════════
[Collection("RealSimSerial")]
public sealed class PushLogThrottleEndToEndTests
{
    private readonly ITestOutputHelper _out;
    public PushLogThrottleEndToEndTests(ITestOutputHelper output) => _out = output;

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs, string msg, int pollMs = 20)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTimeOffset.Now > deadline) Assert.Fail($"WaitUntil 타임아웃({timeoutMs}ms): {msg}");
            await Task.Delay(pollMs);
        }
    }

    private static async Task WaitUntilExactAsync(
        Func<int> countFunc, int expected, int stableCount, int timeoutMs, string msg, int pollMs = 30)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        int consecutive = 0;
        while (consecutive < stableCount)
        {
            if (DateTimeOffset.Now > deadline)
                Assert.Fail($"WaitUntilExact 타임아웃({timeoutMs}ms): {msg} (현재={countFunc()}, 기대={expected})");
            if (countFunc() == expected) consecutive++;
            else                         consecutive = 0;
            await Task.Delay(pollMs);
        }
    }

    // configureExtra: (a) ITraceLogger 를 capturing 으로 교체(트레이스 8/10 계측), (b) 실 operation_log EF
    //   컨슈머 재가동. 공유 push 팩토리는 lambda 등록 IHostedService(OperationLogService 포함)를 제거하므로
    //   VS-E1 의 "실 operation_log(EF)" 실증을 위해 그 컨슈머만 되살린다(억제와 무관한 인프라 결선).
    private static Action<IServiceCollection> SwapSinks(CapturingTraceLogger trace) => services =>
    {
        services.RemoveAll<ITraceLogger>();
        services.AddSingleton<ITraceLogger>(trace);
        services.AddHostedService(sp => sp.GetRequiredService<OperationLogService>());
    };

    // operation_log(EF) 카운트 — CHUTESTATE_PUSH + chuteNo + result(+선택 next_state). AsEnumerable 후 detail 매칭.
    private static int CountOp(IServiceProvider sp, int chuteNo, string result, int? nextState = null)
    {
        using var scope = sp.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        return db.OperationLogs.Where(o => o.Action == "CHUTESTATE_PUSH").AsEnumerable()
            .Count(o => o.Detail is not null
                && o.Detail.Contains($"\"chute_numbers\":[{chuteNo}]")
                && o.Detail.Contains($"\"result\":\"{result}\"")
                && (nextState is null || o.Detail.Contains($"\"next_states\":[{nextState}]")));
    }

    private static int TraceFail(CapturingTraceLogger trace, int chuteNo) =>
        trace.Records.Count(r => (r.EventNo == 8 || r.EventNo == 10) && r.ChuteNo == chuteNo
            && r.Detail is not null && r.Detail.Contains("\"result\":\"FAIL\""));

    private static int FailedTries(FakeChuteStateServer rcs, int chuteNo) =>
        rcs.All.Count(p => !p.Accepted && p.ChuteNumbers.Contains(chuteNo));

    private static (long destId, int full) Chute4(RcsPushWebApplicationFactory f)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        var d4 = db.Destinations.First(d => d.ChuteNo == 4 && d.DestType == DestType.CHUTE);
        var full = db.ChuteDetails.First(cd => cd.DestinationId == d4.Id).WorkFullQty;
        return (d4.Id, full);
    }

    // ════════════════════════════════════════════════════════════════════════
    // VS-E1: RCS-down 하트비트 폭주 억제 + 재발신 살아있음 + 복구 실효(한 스택 병치).
    //   (a) operation_log FAIL == 1, (b) 트레이스 FAIL == 1, (c) 재발신 시도 ≥ N —
    //   억제 실효 + 폭주 부재 + 동작 불변을 분리 단언(GREEN 하나로 합치지 않음).
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task VSE1_RcsDownHeartbeat_SuppressesFailLog_DeliveryAndRecoveryAlive()
    {
        await using var rcs   = await FakeChuteStateServer.StartAsync();
        var             trace = new CapturingTraceLogger();
        await using var factory = new RcsPushWebApplicationFactory(
            rcs.BaseUrl, retryCount: 2, retryBaseDelayMs: 20, configureExtra: SwapSinks(trace));
        _ = factory.CreateClient();

        // 부트스트랩 정착(슈트4 = NORMAL → next_state 3, 성공 delivery 1건 + OK 로그).
        await WaitUntilAsync(() => rcs.CountFor(4) >= 1, 8000, "부트스트랩 슈트4 성공 수신");
        await WaitUntilExactAsync(() => rcs.CountFor(4), 1, stableCount: 5, timeoutMs: 4000, "부트스트랩 안정");
        var (dest4Id, full4) = Chute4(factory);

        // (a) RCS 다운(503) + 슈트4 만재 전이(3→2) = 첫 실패.
        rcs.StartRejecting();
        using (var scope = factory.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<IChuteCapacityService>().OnReserved(dest4Id, full4);

        // (b) 하트비트가 ≥3 주기 재발신 — 각 재발신 = 3 HTTP 시도 → 실패 시도 ≥9 = ≥3 재발신 실증.
        await WaitUntilAsync(() => FailedTries(rcs, 4) >= 9, 8000, "슈트4 하트비트 재발신 ≥3주기(≥9 시도)");

        // (c) 병치 단언: 재발신은 계속인데 FAIL 로그는 각 sink 정확히 1(폭주 0).
        await WaitUntilExactAsync(() => CountOp(factory.Services, 4, "FAIL"), 1, stableCount: 6, timeoutMs: 5000,
            "operation_log FAIL 정확히 1건(N 재발신에도 폭주 0)");
        Assert.Equal(1, TraceFail(trace, 4));                      // 트레이스 이벤트 8 result:FAIL 정확히 1건.
        int tries = FailedTries(rcs, 4);
        Assert.True(tries >= 9, $"delivery 시도 계속(≥9) — 로그만 억제(실제 {tries})");
        _out.WriteLine($"[VS-E1] RCS 다운 — oplog FAIL 1·trace FAIL 1·재발신 시도 {tries}(delivery 살아있음)");

        // (d) 복구 → 최신값(2) 성공 delivery + 성공 로그 정확히 1건(현행 성공 로깅 무변경) + 재발신 freeze.
        rcs.StopRejecting();
        await WaitUntilAsync(() => rcs.CountFor(4) >= 2, 6000, "복구 후 슈트4 최신(2) 성공 delivery");
        Assert.Equal(new[] { 2 }, rcs.LastFor(4)!.NextStates);
        await WaitUntilExactAsync(() => CountOp(factory.Services, 4, "OK", nextState: 2), 1, stableCount: 6, timeoutMs: 5000,
            "복구 성공 로그 정확히 1건(next_state 2)");
        int frozen = rcs.All.Count;
        await WaitUntilExactAsync(() => rcs.All.Count, frozen, stableCount: 8, timeoutMs: 5000,
            "복구·동기 완료 후 재발신 freeze(폭주 0)");
        _out.WriteLine($"[VS-E1] 복구 → 성공 로그 1건·재발신 freeze(총 {frozen})");
    }

    // ════════════════════════════════════════════════════════════════════════
    // VS-B4/C3: 복구 후 재실패 = 새 FAIL 1건(억제 리셋 실증).
    //   down→fail(2) 억제 정착 → 복구(성공·리셋) → 다시 down→새 전이(3) 실패 → FAIL 총 1→2 증가.
    //   (실 pusher 의 Computed≠Acked 디덥으로 복구는 next_state 를 뒤집으므로, 재실패는 새 전이로 발현된다.
    //    "리셋이 같은 next_state 를 재무장" 은 순수 Decide 테스트 Reset_ReArms_SameNextStateEmitsAgain 이 실증.)
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task VSB4_RecoveryReset_ThenRefail_NewFailLogged()
    {
        await using var rcs   = await FakeChuteStateServer.StartAsync();
        var             trace = new CapturingTraceLogger();
        await using var factory = new RcsPushWebApplicationFactory(
            rcs.BaseUrl, retryCount: 2, retryBaseDelayMs: 20, configureExtra: SwapSinks(trace));
        _ = factory.CreateClient();

        await WaitUntilAsync(() => rcs.CountFor(4) >= 1, 8000, "부트스트랩 슈트4");
        await WaitUntilExactAsync(() => rcs.CountFor(4), 1, stableCount: 5, timeoutMs: 4000, "안정");
        var (dest4Id, full4) = Chute4(factory);

        // ① down + 만재(3→2) → FAIL(2) 1건 정착 + 반복 억제.
        rcs.StartRejecting();
        using (var scope = factory.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<IChuteCapacityService>().OnReserved(dest4Id, full4);
        await WaitUntilAsync(() => FailedTries(rcs, 4) >= 6, 8000, "재발신(≥2주기)");
        await WaitUntilExactAsync(() => CountOp(factory.Services, 4, "FAIL"), 1, stableCount: 6, timeoutMs: 5000,
            "첫 실패 1건(반복 억제)");

        // ② 복구 → 만재(2) 성공 delivery → Acked=2·억제 리셋.
        rcs.StopRejecting();
        await WaitUntilAsync(() => rcs.CountFor(4) >= 2, 6000, "복구 후 성공 delivery(2)");
        Assert.Equal(new[] { 2 }, rcs.LastFor(4)!.NextStates);

        // ③ 다시 down + 비움(2→3) → 새 전이 실패 → 새 FAIL 1건(총 1→2). 리셋 재무장 실증.
        rcs.StartRejecting();
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IChuteCapacityService>().OnCleared(dest4Id, "test-op");
        await WaitUntilExactAsync(() => CountOp(factory.Services, 4, "FAIL"), 2, stableCount: 6, timeoutMs: 6000,
            "복구 후 재실패 = 새 FAIL 1건(총 2건)");
        Assert.Equal(1, CountOp(factory.Services, 4, "FAIL", nextState: 2));   // 이전 단위(2) 불변.
        Assert.Equal(1, CountOp(factory.Services, 4, "FAIL", nextState: 3));   // 새 단위(3) 1건.
        Assert.Equal(2, TraceFail(trace, 4));                                  // 트레이스 FAIL 2건.
        _out.WriteLine("[VS-B4] 복구(리셋) 후 재실패 = 새 FAIL 1건(총 2·이전 단위 불변) — 신호 재무장");
    }
}
