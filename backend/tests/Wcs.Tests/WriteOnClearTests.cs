using System.Linq;
using System.Threading;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Wcs.Api;
using Wcs.Core;
using Wcs.Data;
using Wcs.PlcGateway;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// S-TWO-FLOOR-WRITE-ON-CLEAR (C4) — 관측 루프 write-on-clear·분류시작-pop 결정적 검증.
//
// SorterFloorReturnService(관측 루프)를 직접 구성(FakeModbusMasterForApi = Modbus 슬레이브 스탠드인,
// 단일 쓰기 큐 → 레지스터 경로 실증)하고, 레지스터를 직접 조작해 다음을 결정적으로 실증한다:
//   C4-1 write-on-clear(write-during-busy): TgtFloor 비영→0 에지가 Ready==0 중 발생해도 WCS가 새 머리
//        층을 D6 에 기입한다(분류 중 다음 층 커밋).
//   C4-2 same-floor hold: 새 머리 == CurFloor 여도 WCS가 그 층을 기입한다(정렬 완료 스킵 제거 — 드리프트 방지).
//   C4-3 one-pop-per-clear: 단일 비영→0 에지가 큐 머리 1건만 pop + 트레이스 event2 정확히 1건. 정상 0-틱
//        (새 에지 없음)엔 재pop 없음(steady TgtFloor==0 무-재pop).
//   C4-4 empty-queue park + next-piece recovery: 큐를 비우는 클리어에선 미기입·TgtFloor 0 유지(디폴트층 park),
//        이후 다른 층 피스가 enqueue 되면 다음 TgtFloor==0 관측에서 기입(다음 피스 복구).
//   C4-5 no missed / no spurious edge: 무장(첫 TgtFloor==0 관측)·OFFLINE 재무장이 스퓨리어스 pop 을 만들지
//        않는다(콜드스타트 잔류 2→0·오프라인 스팬 클리어를 pop 으로 오인하지 않음).
//
// [Collection("RealSimSerial")] — 타이밍-민감 정확-카운트 단언을 무거운 실-Sim 테스트와 직렬화
//   (병렬 CPU 경합 지터 flake 제거 — SorterStallDetectorTests·TwoFloorWriteGateI2Tests 동형).
// ════════════════════════════════════════════════════════════════════════════
[Collection("RealSimSerial")]
public class WriteOnClearTests
{
    private readonly ITestOutputHelper _out;
    public WriteOnClearTests(ITestOutputHelper output) => _out = output;

    private const int ObserveMs = 20;

    // ── 테스트 더블 ────────────────────────────────────────────────────────────

    /// <summary>정지 토글 status 더블. Compute 는 이 경로에서 호출되면 안 됨(I-2) — 호출 시 예외로 실패.</summary>
    private sealed class SimplePausedStatus : IDestinationStatusService
    {
        private volatile bool _paused;
        public void SetPaused(bool p) => _paused = p;
        public DestinationReadiness Compute(long destinationId, DestType destType) =>
            throw new Xunit.Sdk.XunitException("Compute(heavy) must NOT be called by the observe loop (I-2).");
        public bool SorterHasAssignedCellWithRoomForBarcode(long d, string b) => false;
        public bool SorterCanAcceptBarcode(long d, string b) => false;
        public bool IsPaused(long destinationId) => _paused;
    }

    private sealed class NoopQueueRestorer : IPendingFloorQueueRestorer
    {
        public Task<int> RestoreAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class FakeLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted  => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped  => CancellationToken.None;
        public void StopApplication() { }
    }

    // ── 헬퍼 ──────────────────────────────────────────────────────────────────

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs, string msg, int pollMs = 10)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTimeOffset.Now > deadline) Assert.Fail($"WaitUntil 타임아웃({timeoutMs}ms): {msg}");
            await Task.Delay(pollMs);
        }
    }

    /// <summary>관측 루프가 특정 TgtFloor 스냅샷을 "관측·정착"했음을 보장(에지 감지 prevTgtFloor 확립).
    ///   폴이 값을 반영할 때까지 대기한 뒤 여러 관측 주기 정착 — 이후 레지스터를 바꿔 에지를 결정적으로 만든다.</summary>
    private static async Task SettleObservedTgtFloorAsync(PlcPollingService polling, int expected)
    {
        await WaitUntilAsync(() => polling.Latest.Online && polling.Latest.TgtFloor == expected, 3000,
            $"폴 스냅샷 TgtFloor={expected} 반영");
        await Task.Delay(ObserveMs * 6);   // 관측 루프가 이 값으로 prevTgtFloor 확립(정착).
    }

    private sealed record Harness(
        SorterFloorReturnService Svc, FakeModbusMasterForApi Master, PlcWriteQueue Wq,
        PlcPollingService Polling, SorterPendingFloorQueues Queues, CapturingTraceLogger Trace,
        SimplePausedStatus Status);

    private static Harness Build(long destId, int chuteNo, int stallTicks = 0)
    {
        var master  = new FakeModbusMasterForApi();   // 기본 online·Ready=1·CurFloor=1·TgtFloor=0.
        var gwOpt   = new PlcGatewayOptions
        {
            Host = "127.0.0.1", Port = 1502,
            PollIntervalMs = ObserveMs, OfflineAfterFailures = 3, WriteTimeoutMs = 1000,
            RFlagPollMs = 100, RFlagTimeoutMs = 30000, CFlagTimeoutMs = 5000,
        };
        var wq        = new PlcWriteQueue();
        var polling   = new PlcPollingService(gwOpt, wq, master);
        var handshake = new HandshakeOrchestrator(polling, gwOpt);
        var bundle    = new SorterBundleHandle(destId, chuteNo, polling, handshake);
        var registry  = new FakeSorterGatewayRegistry(bundle);
        var queues    = new SorterPendingFloorQueues();
        var trace     = new CapturingTraceLogger();
        var status    = new SimplePausedStatus();

        var opts = Options.Create(new WcsOptions
        {
            SorterFloorReturn = new SorterFloorReturnOptions { ObserveIntervalMs = ObserveMs, StallSuspectTicks = stallTicks },
        });
        var svc = new SorterFloorReturnService(
            registry, queues, status, new FakeLifetime(), new NoopQueueRestorer(),
            new CapturingOperationLogger(), trace, opts, NullLogger<SorterFloorReturnService>.Instance);

        return new Harness(svc, master, wq, polling, queues, trace, status);
    }

    private static int Event2Count(CapturingTraceLogger trace) => trace.Records.Count(r => r.EventNo == 2);

    // ════════════════════════════════════════════════════════════════════════
    // C4-1: write-during-busy — TgtFloor 비영→0 에지가 Ready==0 중 발생해도 새 머리 층을 D6 에 기입.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task C4_1_WriteOnClear_DuringBusy_WritesNewHead_WhileNotReady()
    {
        const long destId = 900; const int chuteNo = 30;
        var h = Build(destId, chuteNo);
        await h.Polling.StartAsync(CancellationToken.None);
        try
        {
            h.Master.SetReady(true); h.Master.SetCurFloor(1); h.Master.SetTgtFloor(0);
            await WaitUntilAsync(() => h.Polling.Latest.Online && h.Polling.Latest.Ready
                                    && h.Polling.Latest.CurFloor == 1 && h.Polling.Latest.TgtFloor == 0, 3000, "스냅샷 정착");

            // 큐 [1,2]: 머리 1(==CurFloor) → WCS 가 1 정렬 기입(same-floor hold). 관측 정착으로 prevTgtFloor=1 확립.
            h.Queues.Enqueue(destId, 1);
            h.Queues.Enqueue(destId, 2);
            await h.Svc.StartAsync(CancellationToken.None);
            await SettleObservedTgtFloorAsync(h.Polling, 1);
            Assert.Equal(2, h.Queues.Count(destId));   // 아직 pop 없음(에지 미발생).

            // ── 분류 시작 시뮬: Ready=0(busy) + TgtFloor=0(PLC 클리어). Ready 는 이후 절대 1로 되돌리지 않는다 ──
            h.Master.SetReady(false);
            h.Master.SetTgtFloor(0);
            await WaitUntilAsync(() => !h.Polling.Latest.Ready && h.Polling.Latest.TgtFloor is 0 or 2, 3000, "busy+클리어 관측");

            // WCS: 비영→0 에지 → 머리 1 pop → 새 머리 2 → write-during-busy 로 D6=2 기입(Ready==0 유지).
            await WaitUntilAsync(() => h.Master.GetTgtFloor() == 2, 4000, "새 머리(2) write-during-busy 기입");

            Assert.False(h.Polling.Latest.Ready, "D6=2 기입이 Ready==0(busy) 중 발생(write-during-busy)");
            Assert.Equal(1, h.Queues.Count(destId));               // 정확히 1건 pop([2] 잔여).
            Assert.Equal(new[] { 2 }, h.Queues.Snapshot(destId));
            Assert.Equal(1, Event2Count(h.Trace));                 // 에지당 event2 정확히 1건.
            _out.WriteLine($"[C4-1] 클리어 에지@busy → 머리1 pop → 새 머리2 D6 기입(Ready={h.Polling.Latest.Ready})");
        }
        finally { await h.Svc.StopAsync(CancellationToken.None); h.Wq.Writer.TryComplete(); await h.Polling.DisposeAsync(); }
    }

    // ════════════════════════════════════════════════════════════════════════
    // C4-2: same-floor hold — 머리 == CurFloor 여도 WCS 가 그 층을 기입(정렬 완료 스킵 제거·드리프트 방지).
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task C4_2_SameFloorHold_WritesEvenWhenAlreadyAligned()
    {
        const long destId = 910; const int chuteNo = 30;
        var h = Build(destId, chuteNo);
        await h.Polling.StartAsync(CancellationToken.None);
        try
        {
            // CurFloor=2 · 머리=2(이미 정렬) · TgtFloor=0. 구 모델은 CurFloor==f 스킵으로 미기입 → 드리프트.
            h.Master.SetReady(true); h.Master.SetCurFloor(2); h.Master.SetTgtFloor(0);
            await WaitUntilAsync(() => h.Polling.Latest.Online && h.Polling.Latest.Ready
                                    && h.Polling.Latest.CurFloor == 2 && h.Polling.Latest.TgtFloor == 0, 3000, "스냅샷 정착");

            h.Queues.Enqueue(destId, 2);
            await h.Svc.StartAsync(CancellationToken.None);

            // ★ 정렬 완료(CurFloor==머리) 상태에서도 D6=2 를 기입(같은 층 hold — TgtFloor==0 방치 시 디폴트층 드리프트).
            await WaitUntilAsync(() => h.Master.GetTgtFloor() == 2, 4000, "same-floor hold D6=2 기입");
            Assert.Equal(2, h.Master.GetTgtFloor());
            Assert.Equal(1, h.Queues.Count(destId));   // 기입은 pop 아님 — 큐 불변(분류 시작 클리어 시에만 pop).
            Assert.Equal(0, Event2Count(h.Trace));      // pop 0.
            _out.WriteLine("[C4-2] 정렬 완료(CurFloor==머리 2)에도 D6=2 기입(드리프트 방지·pop 0)");
        }
        finally { await h.Svc.StopAsync(CancellationToken.None); h.Wq.Writer.TryComplete(); await h.Polling.DisposeAsync(); }
    }

    // ════════════════════════════════════════════════════════════════════════
    // C4-3 + C4-4: one-pop-per-clear + empty-queue park + next-piece recovery.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task C4_3_4_OnePopPerClear_EmptyQueuePark_ThenNextPieceRecovery()
    {
        const long destId = 920; const int chuteNo = 30;
        var h = Build(destId, chuteNo);
        await h.Polling.StartAsync(CancellationToken.None);
        try
        {
            h.Master.SetReady(true); h.Master.SetCurFloor(1); h.Master.SetTgtFloor(0);
            await WaitUntilAsync(() => h.Polling.Latest.Online && h.Polling.Latest.Ready
                                    && h.Polling.Latest.CurFloor == 1 && h.Polling.Latest.TgtFloor == 0, 3000, "스냅샷 정착");

            // 큐 [1](단일 피스). WCS 가 1 정렬 기입 → 정착(prevTgtFloor=1).
            h.Queues.Enqueue(destId, 1);
            await h.Svc.StartAsync(CancellationToken.None);
            await SettleObservedTgtFloorAsync(h.Polling, 1);

            // ── 단일 클리어(TgtFloor 1→0) → 머리 1 pop → 큐 빔 → 미기입(TgtFloor 0 유지 = park) ──
            h.Master.SetTgtFloor(0);
            await WaitUntilAsync(() => h.Queues.Count(destId) == 0, 4000, "머리 1 pop(큐 빔)");
            Assert.Equal(1, Event2Count(h.Trace));   // one-pop-per-clear: event2 정확히 1건.

            // empty-queue park: 큐 비었으므로 미기입 → TgtFloor 0 유지. 여러 관측 주기 steady 확인(재pop·기입 0).
            await Task.Delay(ObserveMs * 8);
            Assert.Equal(0, h.Master.GetTgtFloor());  // OQ2: 디폴트층 park(WCS 미기입·0 유지).
            Assert.Equal(0, h.Queues.Count(destId));
            Assert.Equal(1, Event2Count(h.Trace));    // steady TgtFloor==0(새 에지 없음) → 재pop 0.

            // ── next-piece recovery: 다른 층(2) 피스 enqueue → 다음 TgtFloor==0 관측에서 D6=2 기입 ──
            h.Queues.Enqueue(destId, 2);
            await WaitUntilAsync(() => h.Master.GetTgtFloor() == 2, 4000, "다음 피스(2) 복구 기입");
            Assert.Equal(2, h.Master.GetTgtFloor());
            Assert.Equal(1, h.Queues.Count(destId));  // 기입은 pop 아님.
            Assert.Equal(1, Event2Count(h.Trace));     // 복구 기입은 pop 아님 — event2 불변.
            _out.WriteLine("[C4-3/4] 단일 클리어 1-pop·1-event2 → 빈큐 park(D6 0 유지) → 다음 피스(2) 복구 기입");
        }
        finally { await h.Svc.StopAsync(CancellationToken.None); h.Wq.Writer.TryComplete(); await h.Polling.DisposeAsync(); }
    }

    // ════════════════════════════════════════════════════════════════════════
    // C4-5(a): 무장(첫 TgtFloor==0 관측) baseline — 콜드스타트 잔류 2→0 을 pop 으로 오인하지 않음.
    //   FakeMaster 에 잔류 TgtFloor=2 를 심고 기동 → PlcPollingService 콜드스타트 StartupClear 가 2→0 으로
    //   지운다. 관측 루프는 0 을 최초 관측한 뒤에만 무장하므로 이 2→0 전이를 분류-시작 pop 으로 오인하지 않는다.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task C4_5a_StartupClearResidual_NoSpuriousPop()
    {
        const long destId = 930; const int chuteNo = 30;
        var h = Build(destId, chuteNo);

        // 기동 전 잔류 주입: TgtFloor=2(콜드스타트 잔류) + CurFloor=1·Ready=1. StartupClear 가 D6 2→0 으로 지운다.
        h.Master.SetReady(true); h.Master.SetCurFloor(1); h.Master.SetTgtFloor(2);

        await h.Polling.StartAsync(CancellationToken.None);
        try
        {
            // 복원 큐(미완료 피스) 시뮬 — 머리 1(==CurFloor). StartupClear(2→0)를 pop 으로 오인하면 이 머리가 조기 드레인.
            h.Queues.Enqueue(destId, 1);
            await h.Svc.StartAsync(CancellationToken.None);

            // 콜드스타트 StartupClear 가 D6 2→0 으로 지우고, WCS 는 0 무장 후 머리 1 을 재기입(정렬) → D6=1.
            await WaitUntilAsync(() => h.Master.GetTgtFloor() == 1, 5000, "StartupClear(2→0) 후 머리 1 정렬 기입");
            await Task.Delay(ObserveMs * 6);   // 정착 — 스퓨리어스 pop 이 있었다면 이 창에서 드러남.

            Assert.Equal(0, Event2Count(h.Trace));      // ★ StartupClear 2→0 은 pop 아님(무장 전 전이 무시).
            Assert.Equal(1, h.Queues.Count(destId));    // 복원 머리 1 조기 드레인 0.
            Assert.Equal(1, h.Master.GetTgtFloor());     // 무장 후 머리 1 정렬 기입.
            _out.WriteLine("[C4-5a] 콜드스타트 잔류 2→0 → 스퓨리어스 pop 0(event2 0·머리 보존)");
        }
        finally { await h.Svc.StopAsync(CancellationToken.None); h.Wq.Writer.TryComplete(); await h.Polling.DisposeAsync(); }
    }

    // ════════════════════════════════════════════════════════════════════════
    // C4-5(b): OFFLINE 재무장 — 오프라인 스팬 동안의 클리어를 복구 시 fabricated pop 으로 만들지 않는다.
    //   정렬(D6=1) 정착 → OFFLINE 주입(재무장 해제) → 오프라인 중 레지스터 1→0 → 복구 → 첫 관측 0 무장(pop 0).
    //   이후 정상 재무장돼 실제 클리어에선 다시 pop 됨(재무장 실효 확인).
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task C4_5b_OfflineReSync_NoSpuriousPop_ThenReArms()
    {
        const long destId = 940; const int chuteNo = 30;
        var h = Build(destId, chuteNo);
        await h.Polling.StartAsync(CancellationToken.None);
        try
        {
            h.Master.SetReady(true); h.Master.SetCurFloor(1); h.Master.SetTgtFloor(0);
            await WaitUntilAsync(() => h.Polling.Latest.Online && h.Polling.Latest.Ready
                                    && h.Polling.Latest.CurFloor == 1 && h.Polling.Latest.TgtFloor == 0, 3000, "스냅샷 정착");

            // 큐 [1,1]: WCS 가 머리 1 정렬 기입 → 정착(무장·prevTgtFloor=1).
            h.Queues.Enqueue(destId, 1);
            h.Queues.Enqueue(destId, 1);
            await h.Svc.StartAsync(CancellationToken.None);
            await SettleObservedTgtFloorAsync(h.Polling, 1);
            Assert.Equal(2, h.Queues.Count(destId));

            // ── OFFLINE 주입(읽기 실패) → 관측 루프 재무장 해제 ──
            h.Master.SetFailReads(true);
            await WaitUntilAsync(() => !h.Polling.Latest.Online, 6000, "오프라인 전이");
            // 오프라인이 최소 여러 관측 주기 지속되게 대기 — 관측 루프가 오프라인 스냅샷을 처리해 Armed=false 로
            //   재무장 해제함을 보장(현장 오프라인 창 ≫ 관측 주기 반영; 테스트가 너무 빨리 복구하면 관측 루프가
            //   오프라인 틱을 놓쳐 재무장이 안 될 수 있음).
            await Task.Delay(ObserveMs * 8);

            // 오프라인 스팬 동안 레지스터가 1→0 으로 바뀜(관측 못 함). 복구 후 첫 관측이 0 이라도 fabricated pop 금지.
            //   (복구 첫 관측 TgtFloor=0 은 WCS 가 곧 머리 1 을 재기입해 1 로 덮으므로 poll 에선 0 창이 짧다 —
            //    관측 루프는 그 tick 에 0 을 무장 기준으로 잡으므로 테스트는 Online 만 대기하고 정착으로 확인.)
            h.Master.SetTgtFloor(0);
            h.Master.SetFailReads(false);
            await WaitUntilAsync(() => h.Polling.Latest.Online, 6000, "온라인 복구");
            await Task.Delay(ObserveMs * 8);   // 재무장(첫 0 관측)·정착.

            Assert.Equal(0, Event2Count(h.Trace));    // ★ 오프라인 스팬 1→0 은 fabricated pop 아님(재무장으로 무시).
            Assert.Equal(2, h.Queues.Count(destId));  // 머리 드레인 0.

            // 재무장 실효 확인: 복구 후 WCS 가 머리 1 재기입(정렬) → D6=1 정착 → 실제 클리어(1→0) → 이제 pop 1건.
            await SettleObservedTgtFloorAsync(h.Polling, 1);
            h.Master.SetTgtFloor(0);
            await WaitUntilAsync(() => Event2Count(h.Trace) == 1, 4000, "재무장 후 실제 클리어 pop 1건");
            Assert.Equal(1, h.Queues.Count(destId));   // 정확히 1건 pop([1] 잔여).
            _out.WriteLine("[C4-5b] 오프라인 스팬 클리어 = fabricated pop 0 → 재무장 후 실제 클리어 pop 1건");
        }
        finally { await h.Svc.StopAsync(CancellationToken.None); h.Wq.Writer.TryComplete(); await h.Polling.DisposeAsync(); }
    }
}
