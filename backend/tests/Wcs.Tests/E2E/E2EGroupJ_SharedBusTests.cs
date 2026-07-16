using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api;
using Wcs.Data;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests.E2E;

// ════════════════════════════════════════════════════════════════════════════
// S-MULTISORTER-SHARED-BUS (Phase 2) — 그룹 J: 공유 버스 풀스택 E2E
//
// Phase 1 버스 메커니즘(ModbusBus + SharedModbusConnection + BusSlaveMaster)이 DI/레지스트리/설정/
// 풀스택에 결선돼, 같은 버스 키(TCP=Host:Port)로 구성한 두 SORTER_3D가 하나의 ModbusBus
// (하나의 SharedModbusConnection = 포트/마스터 1개) 위에서 unitId로 구분되어 엔드투엔드
// (HTTP IF-05/09/10 · 핸드셰이크 · SignalR relay)로 동작함을 실 Sim + 실 EF DB로 입증한다.
//
// 시나리오(계약 Verification E2E):
//   (a) 한 공유 버스 두 소터 엔드투엔드 — 공유 연결 1개·둘 다 Online·각자 핸드셰이크 Success·relay 방출.
//   (b) 슬레이브 격리 — 한 소터 무응답 OFFLINE이 형제 Online·핸드셰이크에 영향 0(공유 연결 무-churn).
//   (c) fail-loud 기동 거부 — 같은 버스 시리얼 파라미터 불일치 / 중복 UnitId → 기동 명확 예외.
//   (d) 멀티 포트 병렬 회귀 — 서로 다른 버스 키(포트 2개)는 각자 독립 ModbusBus로 동시 Online·Success.
//
// 결정성(교훈): 고정 sleep 0, WaitUntil 조건 폴링, Online baseline 확립 후 관찰, teardown 결정적.
// 전송=Tcp(테스트 vehicle — 실 COM1/RTU 무접촉). 무거운 실 Sim + LongPolling 부하 격리를 위해
// 비병렬 컬렉션(parallel-load flake 교훈).
// ════════════════════════════════════════════════════════════════════════════

/// <summary>공유 버스 E2E 비병렬 컬렉션 — 무거운 실 Sim + 허브 LongPolling 부하 격리(parallel-load flake 교훈).</summary>
[CollectionDefinition("SharedBusE2ESerial", DisableParallelization = true)]
public sealed class SharedBusE2ESerialCollection { }

[Collection("SharedBusE2ESerial")]
public sealed class E2EGroupJ_SharedBusTests
{
    private readonly ITestOutputHelper _out;
    public E2EGroupJ_SharedBusTests(ITestOutputHelper output) => _out = output;

    private const int ChuteA = 30;   // 시드 기본 소터(barcode TEST-BARCODE-3)
    private const int ChuteB = 31;   // 추가 소터(barcode SORTER-31-BC)
    private const byte UnitA = 1;
    private const byte UnitB = 2;

    private static HubConnection BuildHub(E2EWebApplicationFactory f) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(f.Server.BaseAddress, "hubs/monitor"), o =>
            {
                o.HttpMessageHandlerFactory = _ => f.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;   // TestServer websocket 한계 회피.
            })
            .Build();

    // ════════════════════════════════════════════════════════════════════════
    // (a) 한 공유 버스 두 소터 엔드투엔드 (핵심 — C1)
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task A_SharedBus_TwoSorters_EndToEnd_OneConnection()
    {
        var rcs = await FakeChuteStateServer.StartAsync();
        await using var _r = rcs;
        await using var factory = new E2EWebApplicationFactory(
            rcsBaseUrl: rcs.BaseUrl,
            initialCurFloor: 2,
            sharedBusUnits: [(ChuteA, UnitA), (ChuteB, UnitB)]);
        await factory.StartSimsAsync();
        _ = factory.CreateClient();   // 호스트 기동(SorterRegistryFactory.StartAsync — 버스 키 그룹핑).

        long destA = factory.Sorter(ChuteA).DestinationId;
        long destB = factory.Sorter(ChuteB).DestinationId;

        // 두 소터 모두 Online (같은 포트를 공유하는 멀티유닛 Sim 1대 위에서).
        await E2EWait.UntilAsync(() => factory.IsSorterOnline(destA) && factory.IsSorterOnline(destB),
            8000, "공유 버스 두 소터 Online");

        // ── 공유 연결(포트/마스터) 1개임을 구조로 입증 ──────────────────────────
        Assert.Single(factory.Buses);                        // 물리 버스 1개(= SharedModbusConnection 1개).
        var bus = factory.Buses[0];
        Assert.Equal(2, bus.MemberCount);                    // 그 버스에 슬레이브 2개(unit1·unit2).
        Assert.Contains(UnitA, bus.UnitIds);
        Assert.Contains(UnitB, bus.UnitIds);
        var registry = factory.Services.GetRequiredService<ISorterGatewayRegistry>();
        Assert.Equal(2, registry.AllBundles.Count);          // destId→bundle 2개(소터 2대).
        _out.WriteLine($"[a] 공유 버스 busKey={bus.BusKey} 멤버={bus.MemberCount} unitIds=[{string.Join(",", bus.UnitIds)}]");

        // ── SignalR relay 결선 — 두 소터 워드 전이 방출 관측 ────────────────────
        await using var conn = BuildHub(factory);
        var seenChutes = new ConcurrentDictionary<int, byte>();
        var bootTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        conn.On<JsonElement>("Bootstrap", _ => bootTcs.TrySetResult());
        conn.On<JsonElement>("RegisterDelta", el =>
        {
            if (el.TryGetProperty("chuteNo", out var cn) && cn.ValueKind == JsonValueKind.Number)
                seenChutes[cn.GetInt32()] = 1;
        });
        await conn.StartAsync();
        await bootTcs.Task.WaitAsync(TimeSpan.FromSeconds(15));

        // ── HTTP(IF-05/09/10) + 핸드셰이크 — 각 소터로 바코드 라우팅(교차 0) ──────
        var driver = MultiAgvDriver.ForFactory(factory);
        var rA = await driver.RunSingleAsync(new AgvJob(28001, 1, "TEST-BARCODE-3", ChuteA, Qty: 2));
        var rB = await driver.RunSingleAsync(new AgvJob(28002, 2, E2EWebApplicationFactory.BarcodeForSorter(ChuteB), ChuteB, Qty: 3));
        Assert.Equal("OK", rA.If05Result);
        Assert.Equal("OK", rB.If05Result);

        // 두 소터 각각 핸드셰이크 COMPLETED(= R_Seq==자기 C_Seq Success) — 실 EF DB ground-truth.
        await E2EWait.UntilAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            return await db.SorterCommands.CountAsync(c => c.Status == SorterCommandStatus.COMPLETED) >= 2;
        }, 10000, "공유 버스 두 소터 핸드셰이크 COMPLETED");

        using (var db = factory.CreateDbScope())
        {
            var p1 = await db.Pieces.FirstAsync(p => p.PId == 28001);
            var p2 = await db.Pieces.FirstAsync(p => p.PId == 28002);
            var cmd1 = await db.SorterCommands.Include(c => c.Cell)
                .FirstAsync(c => c.PieceId == p1.Id && c.Status == SorterCommandStatus.COMPLETED);
            var cmd2 = await db.SorterCommands.Include(c => c.Cell)
                .FirstAsync(c => c.PieceId == p2.Id && c.Status == SorterCommandStatus.COMPLETED);

            // 각 소터 셀에 각자 적재(공유 버스에서 unitId로 구분 — 교차 0).
            Assert.Equal(destA, cmd1.Cell.DestinationId);
            Assert.Equal(destB, cmd2.Cell.DestinationId);
            Assert.NotEqual(cmd1.Cell.DestinationId, cmd2.Cell.DestinationId);
            // 핸드셰이크 매칭(R_Seq==C_Seq): COMPLETED 커맨드의 R_CellNo == 지정 CellNo(현 Sim 반향).
            Assert.Equal(cmd1.CellNo, cmd1.RCellNo);
            Assert.Equal(cmd2.CellNo, cmd2.RCellNo);
        }

        // relay가 두 소터(chuteNo 30·31) 워드 전이를 각각 방출.
        await E2EWait.UntilAsync(() => seenChutes.ContainsKey(ChuteA) && seenChutes.ContainsKey(ChuteB),
            10000, "SignalR relay 두 소터 델타 방출");
        _out.WriteLine($"[a] relay 방출 chuteNos=[{string.Join(",", seenChutes.Keys)}] — 공유 버스 두 소터 엔드투엔드 OK");
    }

    // ════════════════════════════════════════════════════════════════════════
    // (b) 슬레이브 격리 — 한 소터 OFFLINE이 형제 무영향 (C5·절대규칙 #5)
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task B_SharedBus_SlaveOffline_SiblingUnaffected()
    {
        var rcs = await FakeChuteStateServer.StartAsync();
        await using var _r = rcs;
        await using var factory = new E2EWebApplicationFactory(
            rcsBaseUrl: rcs.BaseUrl,
            initialCurFloor: 2,
            sharedBusUnits: [(ChuteA, UnitA), (ChuteB, UnitB)]);
        await factory.StartSimsAsync();
        _ = factory.CreateClient();

        long destA = factory.Sorter(ChuteA).DestinationId;
        long destB = factory.Sorter(ChuteB).DestinationId;
        await E2EWait.UntilAsync(() => factory.IsSorterOnline(destA) && factory.IsSorterOnline(destB),
            8000, "baseline 두 소터 Online");
        Assert.Single(factory.Buses);   // 공유 버스 1개.

        // 소터 B(unit2) 무응답 고장주입 → B unitId 요청만 Modbus 예외(soft) → B만 OFFLINE 누적.
        factory.Sorter(ChuteB).Sim.Unit(UnitB).InjectUnresponsive = true;
        await E2EWait.UntilAsync(() => factory.SorterSnapshot(destB)?.Online == false, 8000, "소터 B OFFLINE 전이");
        Assert.False(factory.IsSorterOnline(destB), "소터 B OFFLINE");

        // 소터 A는 공유 물리 버스에서 계속 정상 폴 — Online 유지 + 핸드셰이크 Success(형제 무영향·무-churn).
        Assert.True(factory.IsSorterOnline(destA), "소터 A Online 유지(B OFFLINE에도)");
        var driver = MultiAgvDriver.ForFactory(factory);
        await driver.RunSingleAsync(new AgvJob(28101, 1, "TEST-BARCODE-3", ChuteA, Qty: 1));
        await E2EWait.UntilAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            return await db.SorterCommands.AnyAsync(c => c.Status == SorterCommandStatus.COMPLETED);
        }, 10000, "소터 A 핸드셰이크 COMPLETED(B OFFLINE 중)");
        Assert.True(factory.IsSorterOnline(destA), "핸드셰이크 후에도 A Online");

        // 복구: 고장 해제 → 소터 B 다시 Online(공유 재연결 없이).
        factory.Sorter(ChuteB).Sim.Unit(UnitB).InjectUnresponsive = false;
        await E2EWait.UntilAsync(() => factory.IsSorterOnline(destB), 8000, "소터 B Online 복구");
        Assert.True(factory.IsSorterOnline(destB), "소터 B 복구");
        _out.WriteLine("[b] 소터 B OFFLINE 동안 소터 A Online·Success — 슬레이브별 OFFLINE 독립(절대규칙 #5)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // (c1) fail-loud — 같은 버스 시리얼 파라미터 불일치 → 기동 거부 (OQ4·C3)
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task C1_SharedBus_SerialParamMismatch_FailsLoud()
    {
        await using var factory = new E2EWebApplicationFactory(
            sharedBusUnits: [(ChuteA, UnitA), (ChuteB, UnitB)],
            induceSerialMismatch: true);
        await factory.StartSimsAsync();

        // 호스트 기동(SorterRegistryFactory.StartAsync)이 명확한 예외로 실패해야 함.
        var ex = Record.Exception(() => factory.CreateClient());
        Assert.NotNull(ex);
        var text = ex!.ToString();
        Assert.Contains("시리얼 파라미터", text);
        Assert.Contains("fail-loud", text);
        _out.WriteLine($"[c1] 시리얼 파라미터 불일치 → 기동 거부(fail-loud): {ex.GetBaseException().Message}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // (c2) fail-loud — 같은 버스 중복 UnitId → 기동 거부 (OQ11·C3)
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task C2_SharedBus_DuplicateUnitId_FailsLoud()
    {
        await using var factory = new E2EWebApplicationFactory(
            sharedBusUnits: [(ChuteA, UnitA), (ChuteB, UnitB)],
            induceDuplicateUnitId: true);   // 두 멤버 모두 UnitId=1 → 같은 버스 중복.
        await factory.StartSimsAsync();

        var ex = Record.Exception(() => factory.CreateClient());
        Assert.NotNull(ex);
        var text = ex!.ToString();
        Assert.Contains("UnitId", text);
        Assert.Contains("중복", text);
        _out.WriteLine($"[c2] 중복 UnitId → 기동 거부(fail-loud): {ex.GetBaseException().Message}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // (c3) fail-loud — 같은 버스 PollIntervalMs 불일치 → 기동 거부 (OQ9-i)
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task C3_SharedBus_PollIntervalMismatch_FailsLoud()
    {
        await using var factory = new E2EWebApplicationFactory(
            sharedBusUnits: [(ChuteA, UnitA), (ChuteB, UnitB)],
            inducePollIntervalMismatch: true);   // 둘째 멤버 PollIntervalMs만 상이 → 같은 버스 폴 주기 충돌.
        await factory.StartSimsAsync();

        var ex = Record.Exception(() => factory.CreateClient());
        Assert.NotNull(ex);
        var text = ex!.ToString();
        Assert.Contains("PollIntervalMs", text);
        Assert.Contains("fail-loud", text);
        _out.WriteLine($"[c3] PollIntervalMs 불일치 → 기동 거부(fail-loud): {ex.GetBaseException().Message}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // (c4) fail-loud — 같은 버스 연결 타임아웃(Read/WriteTimeoutMs) 불일치 → 기동 거부 (CR-I1)
    //   공유 클라이언트의 Read/Write 타임아웃은 버스 단위 1개 — 멤버별 상이 시 대표가 조용히 이기는 대신 fail-loud.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task C4_SharedBus_ConnTimeoutMismatch_FailsLoud()
    {
        await using var factory = new E2EWebApplicationFactory(
            sharedBusUnits: [(ChuteA, UnitA), (ChuteB, UnitB)],
            induceTimeoutMismatch: true);   // 둘째 멤버 ReadTimeoutMs만 상이 → 같은 버스 연결 타임아웃 충돌.
        await factory.StartSimsAsync();

        var ex = Record.Exception(() => factory.CreateClient());
        Assert.NotNull(ex);
        var text = ex!.ToString();
        Assert.Contains("ReadTimeoutMs", text);
        Assert.Contains("fail-loud", text);
        _out.WriteLine($"[c4] 연결 타임아웃 불일치 → 기동 거부(fail-loud): {ex.GetBaseException().Message}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // (e) C4 — SOLO 버스(단일 멤버) read TimeoutException → HARD 재연결(reopen)·복구
    //     = pre-Phase-2 단독 의미 보존. 실 레지스트리 경로(SorterRegistryFactory가 1-멤버 버스 생성)에
    //     read-timeout 주입 데코레이터를 끼워 검증. MultiSorter C3(N=2·soft·무-churn)의 대칭(반대 단언).
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task E_SoloBus_ReadTimeout_HardReconnect_Recovers()
    {
        var rcs = await FakeChuteStateServer.StartAsync();
        await using var _r = rcs;
        // 단일 소터(시드 chuteNo=30)만 — sharedBusUnits/extras 없음 → 레지스트리가 1-멤버(solo) 버스 생성.
        await using var factory = new E2EWebApplicationFactory(
            rcsBaseUrl: rcs.BaseUrl,
            initialCurFloor: 2,
            injectTimeoutConnection: true);   // 실 레지스트리가 만드는 공유 연결을 timeout 데코레이터로 감쌈.
        await factory.StartSimsAsync();
        _ = factory.CreateClient();

        long destA = factory.Sorter(ChuteA).DestinationId;
        await E2EWait.UntilAsync(() => factory.IsSorterOnline(destA), 8000, "solo 소터 Online");

        // 레지스트리가 만든 버스는 정확히 1개·멤버 1(solo).
        Assert.Single(factory.Buses);
        Assert.Equal(1, factory.Buses[0].MemberCount);

        var deco = Assert.Single(factory.InjectedConnections);
        int baseDisc = deco.DisconnectCalls;

        // 그 소터(unit 1)의 read를 타임아웃시킨다 → solo=HARD → 재연결(reopen) 발생(Disconnect 증가).
        // (다중 멤버였다면 soft·무-churn으로 Disconnect 불변 — MultiSorter C3.)
        deco.SetTimeoutUnit(UnitA);
        await E2EWait.UntilAsync(() => deco.DisconnectCalls > baseDisc, 8000,
            "solo read timeout → HARD 재연결(reopen) 발생");
        Assert.True(deco.DisconnectCalls > baseDisc,
            $"solo 타임아웃은 HARD(재연결) 여야 함 — DisconnectCalls {baseDisc}→{deco.DisconnectCalls}");

        // 타임아웃 해제 → 재연결(reopen)로 Online 복구(pre-Phase-2 복구 경로 동치).
        deco.SetTimeoutUnit(-1);
        await E2EWait.UntilAsync(() => factory.IsSorterOnline(destA), 8000, "solo 타임아웃 해제 후 Online 복구");
        Assert.True(factory.IsSorterOnline(destA), "solo 소터 복구");
        _out.WriteLine($"[e] solo read-timeout → HARD 재연결(Disconnect {baseDisc}→{deco.DisconnectCalls})·복구 — pre-Phase-2 의미 보존(C4)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // (d) 멀티 포트 병렬 회귀 — 서로 다른 버스 키는 각자 독립 ModbusBus (A2·C2)
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task D_MultiPort_DifferentBusKeys_ParallelIndependent()
    {
        var rcs = await FakeChuteStateServer.StartAsync();
        await using var _r = rcs;
        await using var factory = new E2EWebApplicationFactory(
            rcsBaseUrl: rcs.BaseUrl,
            extraSorterChuteNos: [ChuteB],   // 멀티 포트 모드(소터당 별도 포트 = 다른 버스 키).
            initialCurFloor: 2);
        await factory.StartSimsAsync();
        _ = factory.CreateClient();

        long destA = factory.Sorter(ChuteA).DestinationId;
        long destB = factory.Sorter(ChuteB).DestinationId;
        await E2EWait.UntilAsync(() => factory.IsSorterOnline(destA) && factory.IsSorterOnline(destB),
            8000, "멀티 포트 두 소터 Online");

        // 서로 다른 버스 키 = 독립 ModbusBus 2개(각 멤버 1) — 멀티 포트 보존.
        Assert.Equal(2, factory.Buses.Count);
        Assert.All(factory.Buses, b => Assert.Equal(1, b.MemberCount));
        Assert.NotEqual(factory.Sorter(ChuteA).Port, factory.Sorter(ChuteB).Port);

        var driver = MultiAgvDriver.ForFactory(factory);
        await driver.RunSingleAsync(new AgvJob(28201, 1, "TEST-BARCODE-3", ChuteA, Qty: 1));
        await driver.RunSingleAsync(new AgvJob(28202, 2, E2EWebApplicationFactory.BarcodeForSorter(ChuteB), ChuteB, Qty: 1));
        await E2EWait.UntilAsync(async () =>
        {
            using var db = factory.CreateDbScope();
            return await db.SorterCommands.CountAsync(c => c.Status == SorterCommandStatus.COMPLETED) >= 2;
        }, 10000, "멀티 포트 두 소터 핸드셰이크 COMPLETED");
        _out.WriteLine($"[d] 독립 버스 {factory.Buses.Count}개(각 멤버 1) 동시 Online·Success — 멀티 포트 회귀 0");
    }
}
