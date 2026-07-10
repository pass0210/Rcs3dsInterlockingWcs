using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Api;
using Wcs.Core;
using Wcs.Data;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// OpsControllerTests (S-F3a) — B2C 운영자 제어 백엔드 통합 테스트.
//
// ★ 안전 경계(계약 SAFETY BOUNDARY) 검증 원칙:
//   · 검증은 Sim3ds(FluentModbus TCP, 동적 포트) 전용 — 실 COM1/RTU 미접근. 스크래치 in-memory SQLite.
//   · 워드 쓰기(O4~O6)가 **단일 쓰기 큐 컨슈머**를 경유함을 (a) Sim 레지스터 반영 + (b) PLC_WRITE
//     operation_log(컨슈머 EmitWrite에서만 발화)로 이중 입증 — 컨트롤러의 직접 Modbus 호출 부재 증거.
//   · TgtFloor 게이트(#2 핑퐁 차단)·비클리어(#3 floor>=1)·런타임 PAUSED/RESUMED 전이·A-8(clear) 실증.
//
// 각 [Fact]는 fresh SimWebApplicationFactory(자체 포트·Sim·DB·인메모리 상태) — 테스트 간 격리.
// (SimWebApplicationFactory는 ScenarioTests.cs에 정의된 것을 재사용.)
// ════════════════════════════════════════════════════════════════════════════
public class OpsControllerTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _out;
    private readonly int _port;
    private SimWebApplicationFactory? _factory;
    private HttpClient? _client;

    public OpsControllerTests(ITestOutputHelper output)
    {
        _out  = output;
        _port = GetFreePort();
    }

    public async Task InitializeAsync()
    {
        _factory = new SimWebApplicationFactory(_port, initialCurFloor: 2);
        await _factory.StartSimAsync();
        _client = _factory.CreateClient();
        // 소터 GW Online 대기 — 첫 유효 폴 완료(기동 잔류 reconcile 게이트 통과) 보장.
        await WaitUntilAsync(() => _factory!.IsSorterOnline(), 5000, "소터 GW Online");
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_factory is not null)
            await _factory.DisposeAsync();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // O4 SetTgtFloor — 단일 큐 경유 Sim D6 반영 + PLC_WRITE 자동 감사
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task O4_SetTgtFloor_ReflectsInSimD6_ThroughSingleQueue()
    {
        long sorterId = SorterId();

        // 현재 TgtFloor==0 전제(Sim 기동 무잔류) → floor=2(운영층·이동 없음) 쓰기.
        var resp = await _client!.PostAsJsonAsync(
            $"/api/ops/sorters/{sorterId}/tgtfloor",
            new { floor = 2, operatorName = "홍길동" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("enqueued", body.GetProperty("status").GetString());
        Assert.False(body.GetProperty("pingPongGuard").GetBoolean(), "TgtFloor==0 → 핑퐁 가드 false");

        // (a) Sim 레지스터에 D6=2 반영(단일 큐 컨슈머가 실기입).
        await WaitUntilAsync(() => _factory!.Sim.ReadSnapshot().TgtFloor == 2, 4000, "Sim D6=2 반영");

        // (b) PLC_WRITE/SET_TGTFLOOR operation_log — 컨슈머 EmitWrite에서만 발화(단일 큐 경유 증거).
        await WaitUntilAsync(() => HasOpLog(OperationLogCategory.PLC_WRITE, "SET_TGTFLOOR"),
            4000, "PLC_WRITE/SET_TGTFLOOR 자동 감사");

        // 운영자 발원 STATE 감사 1행(operatorName detail 귀속).
        Assert.True(HasOpLog(OperationLogCategory.STATE, "OPS_SET_TGTFLOOR"), "운영자 발원 STATE 감사");
        _out.WriteLine("[O4] SetTgtFloor → Sim D6=2 + PLC_WRITE 감사 확인(단일 큐 경유)");
    }

    [Fact]
    public async Task O4_SetTgtFloor_PingPongBlocked_WhenTgtFloorNonZero()
    {
        long sorterId = SorterId();

        // 1) TgtFloor 0→2 기입 후 WCS 폴이 관측(=컨슈머 가드가 볼 _latest.TgtFloor=2) 대기.
        await _client!.PostAsJsonAsync($"/api/ops/sorters/{sorterId}/tgtfloor",
            new { floor = 2, operatorName = "op1" });
        await WaitUntilAsync(() => _factory!.SorterSnapshot()?.TgtFloor == 2, 4000, "WCS 폴 TgtFloor=2 관측");

        // 2) TgtFloor≠0 상태에서 floor=5 재요청 — API는 enqueue 수락하나 응답에 핑퐁 가드=true를 정직히 보고.
        var resp = await _client!.PostAsJsonAsync($"/api/ops/sorters/{sorterId}/tgtfloor",
            new { floor = 5, operatorName = "op2" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("pingPongGuard").GetBoolean(),
            "TgtFloor≠0 → API가 핑퐁 차단 가능성을 pingPongGuard=true로 정직 보고(#2)");

        // 3) 컨슈머가 TgtFloor==0 재확인 가드로 스킵 — D6는 2로 유지(5로 덮이지 않음).
        await PollForDurationAsync(400);
        Assert.Equal(2, _factory!.Sim.ReadSnapshot().TgtFloor);
        Assert.DoesNotContain(_factory!.Timeline, l => l.Contains("WCS 쓰기 수신: D6 2→5"));
        _out.WriteLine("[O4-핑퐁] TgtFloor≠0 재요청 스킵 확인 — 컨슈머 가드 보존(#2)");
    }

    [Fact]
    public async Task O4_FloorLessThan1_Returns400_NonClearGuard()
    {
        long sorterId = SorterId();
        // #3: WCS 비클리어 — floor=0 수동 리셋은 F3a 미노출(floor>=1만 수락).
        var resp = await _client!.PostAsJsonAsync($"/api/ops/sorters/{sorterId}/tgtfloor",
            new { floor = 0, operatorName = "op" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    // I-1: 상한 초과 floor는 400·enqueue 0 — 컨슈머 (short) 캐스트 조용한 wrap 방지(Fail Loud).
    [Fact]
    public async Task O4_FloorAboveBound_Returns400_NoEnqueue()
    {
        long sorterId = SorterId();

        // 도메인 sane 상한(설정 MaxTgtFloor=20) 바로 위 → 400.
        var justOver = await _client!.PostAsJsonAsync($"/api/ops/sorters/{sorterId}/tgtfloor",
            new { floor = 21, operatorName = "op" });
        Assert.Equal(HttpStatusCode.BadRequest, justOver.StatusCode);

        // 하드 타입 상한(short.MaxValue=32767) 초과 → 400(캐스트 시 음수 wrap 방지).
        var typeWrap = await _client!.PostAsJsonAsync($"/api/ops/sorters/{sorterId}/tgtfloor",
            new { floor = 70000, operatorName = "op" });
        Assert.Equal(HttpStatusCode.BadRequest, typeWrap.StatusCode);

        // enqueue 0: 검증 실패로 큐에 안 들어감 → PLC_WRITE/OPS 감사 0, Sim D6=0 유지.
        await PollForDurationAsync(250);
        Assert.False(HasOpLog(OperationLogCategory.PLC_WRITE, "SET_TGTFLOOR"), "상한 초과 → PLC_WRITE enqueue 0");
        Assert.False(HasOpLog(OperationLogCategory.STATE, "OPS_SET_TGTFLOOR"), "상한 초과 → 운영자 감사 0");
        Assert.Equal(0, _factory!.Sim.ReadSnapshot().TgtFloor);
        _out.WriteLine("[O4/I-1] floor 상한 초과(21·70000) → 400, enqueue 0, D6 미변경");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // O5 ClearR — 단일 큐 경유 Sim R 영역 클리어(사전 잔류 세팅) + PLC_WRITE
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task O5_ClearR_ClearsSimRRegion_ThroughSingleQueue()
    {
        long sorterId = SorterId();

        // 기동 후(첫 폴 reconcile 게이트 통과) R 잔류를 세팅 — 이후엔 자동 reconcile 없음(런타임 잔류).
        _factory!.Sim.SetRResidue(rCellNo: 20, rSeq: 123);

        var resp = await _client!.PostAsJsonAsync($"/api/ops/sorters/{sorterId}/clear-r",
            new { operatorName = "정비사" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // 컨슈머가 D2·D3=0 + R_Flag clear(RMW) 실기입 → Sim R 영역 클리어 관찰.
        await WaitUntilAsync(() =>
        {
            var s = _factory!.Sim.ReadSnapshot();
            return !s.RFlag && s.RCellNo == 0 && s.RSeq == 0;
        }, 4000, "Sim R 영역 클리어(R_Flag=0·R_CellNo=0·R_Seq=0)");

        await WaitUntilAsync(() => HasOpLog(OperationLogCategory.PLC_WRITE, "CLEAR_R"),
            4000, "PLC_WRITE/CLEAR_R 자동 감사(단일 큐 경유)");
        _out.WriteLine("[O5] ClearR → Sim R 영역 클리어 + PLC_WRITE 감사(단일 큐 경유)");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // O6 CellAssign — 단일 큐 경유 Sim C 영역 수신 + PLC_WRITE
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task O6_CellAssign_ReceivedBySim_ThroughSingleQueue()
    {
        long sorterId = SorterId();

        var resp = await _client!.PostAsJsonAsync($"/api/ops/sorters/{sorterId}/cell-assign",
            new { cellNo = 2, seq = 7, operatorName = "관리자" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // 컨슈머가 C_CellNo=2·C_Seq=7·C_Flag=1 기입 → Sim이 C_Flag=1 감지·C 영역 소비(타임라인).
        await WaitUntilAsync(
            () => _factory!.Timeline.Any(l => l.Contains("C 수신: CellNo=2 C_Seq=7")),
            4000, "Sim C 영역 수신(CellNo=2 C_Seq=7)");

        await WaitUntilAsync(() => HasOpLog(OperationLogCategory.PLC_WRITE, "CELL_ASSIGN"),
            4000, "PLC_WRITE/CELL_ASSIGN 자동 감사(단일 큐 경유)");
        _out.WriteLine("[O6] CellAssign → Sim C 수신 + PLC_WRITE 감사(단일 큐 경유)");
    }

    // I-1: 상한 초과 cellNo/seq는 400·enqueue 0 — 컨슈머 (short) 캐스트 조용한 wrap 방지(Fail Loud).
    [Fact]
    public async Task O6_CellNoOrSeqAboveBound_Returns400_NoEnqueue()
    {
        long sorterId = SorterId();

        // cellNo 도메인 상한(설정 MaxCellNo=1000) 바로 위 → 400.
        var cellOver = await _client!.PostAsJsonAsync($"/api/ops/sorters/{sorterId}/cell-assign",
            new { cellNo = 1001, seq = 1, operatorName = "op" });
        Assert.Equal(HttpStatusCode.BadRequest, cellOver.StatusCode);

        // seq 도메인 상한(설정 MaxCellSeq=30000) 바로 위 → 400.
        var seqOver = await _client!.PostAsJsonAsync($"/api/ops/sorters/{sorterId}/cell-assign",
            new { cellNo = 1, seq = 30001, operatorName = "op" });
        Assert.Equal(HttpStatusCode.BadRequest, seqOver.StatusCode);

        // 하드 타입 상한(short.MaxValue) 초과 → 400(캐스트 wrap 방지).
        var typeWrap = await _client!.PostAsJsonAsync($"/api/ops/sorters/{sorterId}/cell-assign",
            new { cellNo = 70000, seq = 1, operatorName = "op" });
        Assert.Equal(HttpStatusCode.BadRequest, typeWrap.StatusCode);

        // enqueue 0: 검증 실패 → PLC_WRITE/CELL_ASSIGN·OPS 감사 0.
        await PollForDurationAsync(250);
        Assert.False(HasOpLog(OperationLogCategory.PLC_WRITE, "CELL_ASSIGN"), "상한 초과 → PLC_WRITE enqueue 0");
        Assert.False(HasOpLog(OperationLogCategory.STATE, "OPS_CELL_ASSIGN"), "상한 초과 → 운영자 감사 0");
        _out.WriteLine("[O6/I-1] cellNo/seq 상한 초과 → 400, enqueue 0");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // S-F3B-FOLLOWUP 3-B: 수동 O4/O6 Ready 사전점검(409) — Ready==0이면 거부·enqueue 0
    //   O5 ClearR은 Ready 무관 허용(복구 도구, Q1). 자동 IF-09 정렬은 Ready==0에도 무회귀(공유 컨슈머 case).
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task O4_NotReady_Returns409_NoEnqueue()
    {
        long sorterId = SorterId();

        // 소터를 BUSY(Ready==0, 분류/이동 중)로 결정적으로 몬다(Sim test seam).
        _factory!.Sim.SetReady(false);
        await WaitUntilAsync(() => _factory!.SorterSnapshot() is { Ready: false, Online: true }, 4000, "소터 Ready=0");

        var resp = await _client!.PostAsJsonAsync($"/api/ops/sorters/{sorterId}/tgtfloor",
            new { floor = 2, operatorName = "홍길동" });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);  // 409 — 사전점검 거부

        // enqueue 0: PLC_WRITE/SET_TGTFLOOR 없음, Sim D6 미변경(0 유지).
        await PollForDurationAsync(300);
        Assert.False(HasOpLog(OperationLogCategory.PLC_WRITE, "SET_TGTFLOOR"), "409 → enqueue 0");
        Assert.Equal(0, _factory!.Sim.ReadSnapshot().TgtFloor);
        _out.WriteLine("[O4/3-B] Ready==0 → 409, enqueue 0, D6 미변경");
    }

    [Fact]
    public async Task O6_NotReady_Returns409_NoEnqueue()
    {
        long sorterId = SorterId();

        _factory!.Sim.SetReady(false);
        await WaitUntilAsync(() => _factory!.SorterSnapshot() is { Ready: false, Online: true }, 4000, "소터 Ready=0");

        var resp = await _client!.PostAsJsonAsync($"/api/ops/sorters/{sorterId}/cell-assign",
            new { cellNo = 2, seq = 7, operatorName = "관리자" });
        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);

        await PollForDurationAsync(300);
        Assert.False(HasOpLog(OperationLogCategory.PLC_WRITE, "CELL_ASSIGN"), "409 → enqueue 0");
        _out.WriteLine("[O6/3-B] Ready==0 → 409, enqueue 0");
    }

    [Fact]
    public async Task O5_ClearR_NotReady_StillAllowed()
    {
        long sorterId = SorterId();

        // R 잔류 세팅 후 Ready=0으로 몰아도 ClearR은 복구 도구라 허용(Q1 — Ready 게이트 대상 아님).
        _factory!.Sim.SetRResidue(rCellNo: 20, rSeq: 123);
        _factory!.Sim.SetReady(false);
        await WaitUntilAsync(() => _factory!.SorterSnapshot() is { Ready: false, Online: true }, 4000, "소터 Ready=0");

        var resp = await _client!.PostAsJsonAsync($"/api/ops/sorters/{sorterId}/clear-r",
            new { operatorName = "정비사" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);  // Ready 무관 허용

        await WaitUntilAsync(() =>
        {
            var s = _factory!.Sim.ReadSnapshot();
            return !s.RFlag && s.RCellNo == 0 && s.RSeq == 0;
        }, 4000, "Ready==0에도 R 영역 클리어(단일 큐 경유)");
        _out.WriteLine("[O5/Q1] Ready==0에도 ClearR 허용 — R 영역 클리어");
    }

    [Fact]
    public async Task O6_CellAssign_CFlagGuard_ReportsAdvisory()
    {
        long sorterId = SorterId();

        // Sim 상태 루프 동결 — 첫 CellAssign의 C_Flag=1이 소비되지 않아 두 번째 응답에 advisory 노출.
        // (Ready=1 유지 — 사전점검 통과. Modbus 서버는 계속 응답 → 폴이 C_Flag=1 관측.)
        _factory!.Sim.InjectNoResponse = true;

        // 1) 첫 CellAssign — cFlagGuard=false(아직 C_Flag=0). 큐가 C_Flag=1 세팅.
        var first = await _client!.PostAsJsonAsync($"/api/ops/sorters/{sorterId}/cell-assign",
            new { cellNo = 3, seq = 11, operatorName = "op1" });
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(firstBody.GetProperty("cFlagGuard").GetBoolean(), "첫 요청은 C_Flag=0 → advisory false");

        // 폴이 C_Flag=1을 관측할 때까지 대기(사전점검·advisory 근거 = bundle.Latest).
        await WaitUntilAsync(() => _factory!.SorterSnapshot()?.CFlag == true, 4000, "폴 C_Flag=1 관측");

        // 2) 두 번째 CellAssign — 응답 cFlagGuard=true(진행 중 스킵 가능성 정직 표면화 · O4 pingPongGuard 미러).
        var second = await _client!.PostAsJsonAsync($"/api/ops/sorters/{sorterId}/cell-assign",
            new { cellNo = 4, seq = 12, operatorName = "op2" });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(secondBody.GetProperty("cFlagGuard").GetBoolean(),
            "C_Flag=1 상태 → cFlagGuard=true 정직 보고(3-C)");
        _out.WriteLine("[O6/3-C] C_Flag=1 → 응답 cFlagGuard=true(정직 표면화)");
    }

    [Fact]
    public async Task IF09_AutoAlign_WritesTgtFloor_EvenWhenReadyZero_NoRegression()
    {
        long sorterId = SorterId();
        int sorterChuteNo;
        using (var db = _factory!.CreateDbScope())
            sorterChuteNo = db.Destinations.First(d => d.Id == sorterId).ChuteNo;

        // 소터를 BUSY(Ready==0)로 몬다. 자동 IF-09 정렬은 Ready==0·TgtFloor==0에서 운영층 복귀를
        // 선기입해야 한다(DepositDecider — Ready==0 의도적 기입). 컨슈머 fresh-read 가드는 TgtFloor만
        // 보고 Ready를 보지 않으므로 이 자동/공유 경로는 무회귀여야 한다.
        _factory!.Sim.SetReady(false);
        await WaitUntilAsync(
            () => _factory!.SorterSnapshot() is { Ready: false, Online: true, TgtFloor: 0 },
            4000, "소터 Ready=0·TgtFloor=0");

        // IF-09 도착 보고 → 3D 소터면 AlignSorterToOperationalFloor(자동, 공유 컨슈머 case) 발동.
        var resp = await _client!.PostAsJsonAsync("/api/v1/arrival-report",
            new { pId = 1, chuteNo = sorterChuteNo, agvNo = 1, timeStamp = (string?)null });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // 운영층(2)로 정렬 쓰기가 통과 → Sim D6=2 기입(Ready==0에도 자동 정렬 무회귀).
        await WaitUntilAsync(() => _factory!.Sim.ReadSnapshot().TgtFloor == 2, 4000, "자동 IF-09 정렬 D6=2(Ready==0)");
        await WaitUntilAsync(() => HasOpLog(OperationLogCategory.PLC_WRITE, "SET_TGTFLOOR"),
            4000, "PLC_WRITE/SET_TGTFLOOR(자동 경로)");
        _out.WriteLine("[IF-09/auto] Ready==0에서도 자동 정렬 TgtFloor=2 기입 — 공유 컨슈머 case 무회귀");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // O2/O3 소터 PAUSED/RESUMED — IF-05 dispatch 게이트 차단·복원 + destination_event
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task O2_O3_SorterPause_BlocksIf05_Resume_Restores()
    {
        long sorterId = SorterId();

        // 기준: 미정지 소터는 IF-05(barcode -3 → 소터) OK.
        Assert.Equal("OK", (await If05Sorter(20001)).Result);

        // O2 pause → IF-05 NG(소터 Paused는 예외 없이 차단).
        var pauseResp = await _client!.PostAsJsonAsync($"/api/ops/destinations/{sorterId}/pause",
            new { operatorName = "야간조" });
        Assert.Equal(HttpStatusCode.OK, pauseResp.StatusCode);
        Assert.NotEqual("OK", (await If05Sorter(20002)).Result);

        // DB Status=PAUSED + destination_event(PAUSED, operator) 확인.
        using (var db = _factory!.CreateDbScope())
        {
            Assert.Equal(DestStatus.PAUSED, db.Destinations.First(d => d.Id == sorterId).Status);
            Assert.True(db.DestinationEvents.Any(e =>
                e.DestinationId == sorterId && e.EventType == DestinationEventType.PAUSED
                && e.OperatorId == "야간조"), "destination_event(PAUSED, op=야간조)");
        }

        // O3 resume → IF-05 OK 복원.
        var resumeResp = await _client!.PostAsJsonAsync($"/api/ops/destinations/{sorterId}/resume",
            new { operatorName = "야간조" });
        Assert.Equal(HttpStatusCode.OK, resumeResp.StatusCode);
        Assert.Equal("OK", (await If05Sorter(20003)).Result);

        using (var db = _factory!.CreateDbScope())
        {
            Assert.Equal(DestStatus.NORMAL, db.Destinations.First(d => d.Id == sorterId).Status);
            Assert.True(db.DestinationEvents.Any(e =>
                e.DestinationId == sorterId && e.EventType == DestinationEventType.RESUMED
                && e.OperatorId == "야간조"), "destination_event(RESUMED, op=야간조)");
        }
        _out.WriteLine("[O2/O3] 소터 pause→IF-05 NG, resume→OK 복원 + destination_event 귀속");
    }

    [Fact]
    public async Task O2_O3_ChutePause_GetHoldPaused_Resume_None()
    {
        long chuteId = ChuteId(1);  // 시드 chuteNo=1 = NORMAL CHUTE
        var capacity = _factory!.Services.GetRequiredService<IChuteCapacityService>();

        Assert.Equal(WcsHold.None, capacity.GetHold(chuteId));  // 기준

        // O2 pause → 인메모리 IsPaused 반영 → GetHold=Paused.
        var pauseResp = await _client!.PostAsJsonAsync($"/api/ops/destinations/{chuteId}/pause",
            new { operatorName = "주간조" });
        Assert.Equal(HttpStatusCode.OK, pauseResp.StatusCode);
        Assert.Equal(WcsHold.Paused, capacity.GetHold(chuteId));

        using (var db = _factory!.CreateDbScope())
            Assert.Equal(DestStatus.PAUSED, db.Destinations.First(d => d.Id == chuteId).Status);

        // O3 resume → GetHold=None 복원.
        var resumeResp = await _client!.PostAsJsonAsync($"/api/ops/destinations/{chuteId}/resume",
            new { operatorName = "주간조" });
        Assert.Equal(HttpStatusCode.OK, resumeResp.StatusCode);
        Assert.Equal(WcsHold.None, capacity.GetHold(chuteId));
        _out.WriteLine("[O2/O3] 슈트 pause→GetHold Paused, resume→None(인메모리 반영)");
    }

    [Fact]
    public async Task O2_Idempotent_AlreadyPaused_NoDuplicateEvent()
    {
        long chuteId = ChuteId(1);

        await _client!.PostAsJsonAsync($"/api/ops/destinations/{chuteId}/pause", new { operatorName = "op" });
        var second = await _client!.PostAsJsonAsync($"/api/ops/destinations/{chuteId}/pause", new { operatorName = "op" });
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AlreadyInState", body.GetProperty("outcome").GetString());

        // 멱등: PAUSED destination_event는 정확히 1건(중복 append 없음).
        using var db = _factory!.CreateDbScope();
        int paused = db.DestinationEvents.Count(e =>
            e.DestinationId == chuteId && e.EventType == DestinationEventType.PAUSED);
        Assert.Equal(1, paused);
        _out.WriteLine("[O2-멱등] 재요청 AlreadyInState + destination_event 중복 0");
    }

    // I-2: DB Status=PAUSED인데 인메모리 IsPaused=false로 어긋난 상태를 멱등 pause 1회가 교정.
    [Fact]
    public async Task O2_Idempotent_ReconcilesDivergentInMemoryFlag()
    {
        long chuteId = ChuteId(1);
        var capacity = _factory!.Services.GetRequiredService<IChuteCapacityService>();

        // divergence 인위 조성: DB만 PAUSED로 직접 전환(서비스 우회) → 인메모리 IsPaused는 false 유지.
        using (var db = _factory!.CreateDbScope())
        {
            var d = db.Destinations.First(x => x.Id == chuteId);
            d.Status = DestStatus.PAUSED;
            db.SaveChanges();
        }
        // 게이트는 인메모리를 보므로 아직 None(게이트 열림 ↔ DB는 PAUSED — divergence).
        Assert.Equal(WcsHold.None, capacity.GetHold(chuteId));

        // 멱등 pause(DB 이미 PAUSED → AlreadyInState) — 그래도 인메모리 IsPaused를 강제 동기(I-2).
        var resp = await _client!.PostAsJsonAsync($"/api/ops/destinations/{chuteId}/pause",
            new { operatorName = "op" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("AlreadyInState", body.GetProperty("outcome").GetString());

        // 교정 확인: 게이트가 이제 Paused(divergence self-heal).
        Assert.Equal(WcsHold.Paused, capacity.GetHold(chuteId));
        _out.WriteLine("[O2/I-2] divergent 인메모리 플래그를 멱등 pause 1회가 교정 → GetHold Paused");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // O1 슈트 비움 (A-8: OnCleared production 호출자 신설 — FULL 복구 + operator 귀속)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task O1_ClearChute_RecoversFull_A8_RecordsOperator()
    {
        long chuteId = ChuteId(1);
        var capacity = _factory!.Services.GetRequiredService<IChuteCapacityService>();

        int workFullQty;
        using (var db = _factory!.CreateDbScope())
            workFullQty = db.ChuteDetails.First(cd => cd.DestinationId == chuteId).WorkFullQty;

        // 슈트를 FULL로 만든다(예약 qty가 work_full_qty 도달).
        capacity.OnReserved(chuteId, workFullQty);
        Assert.Equal(WcsHold.Full, capacity.GetHold(chuteId));

        // O1 clear — A-8: production 호출자(신설)가 OnCleared로 실제 복구.
        var resp = await _client!.PostAsJsonAsync($"/api/ops/chutes/{chuteId}/clear",
            new { operatorName = "홍길동" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // FULL → None 복구(비가역 갭 A-8 해소 실증).
        Assert.Equal(WcsHold.None, capacity.GetHold(chuteId));

        // destination_event(CLEARED, operator_id=홍길동) 기입 확인.
        using var db2 = _factory!.CreateDbScope();
        Assert.True(db2.DestinationEvents.Any(e =>
            e.DestinationId == chuteId && e.EventType == DestinationEventType.CLEARED
            && e.OperatorId == "홍길동"), "destination_event(CLEARED, op=홍길동)");
        _out.WriteLine("[O1/A-8] FULL 슈트 clear → 복구 + destination_event(CLEARED, 홍길동)");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // 검증·라우팅 edge (operatorName 필수·404 라우팅)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task Ops_MissingOperatorName_Returns400()
    {
        long chuteId = ChuteId(1);
        long sorterId = SorterId();

        var r1 = await _client!.PostAsJsonAsync($"/api/ops/destinations/{chuteId}/pause",
            new { operatorName = "   " });   // 공백 → 귀속 불가 → 400
        Assert.Equal(HttpStatusCode.BadRequest, r1.StatusCode);

        var r2 = await _client!.PostAsJsonAsync($"/api/ops/sorters/{sorterId}/clear-r",
            new { foo = "bar" });            // operatorName 누락 → 400
        Assert.Equal(HttpStatusCode.BadRequest, r2.StatusCode);
    }

    [Fact]
    public async Task WordWrite_NonSorterOrUnknownDestId_Returns404()
    {
        long chuteId = ChuteId(1);   // CHUTE — 소터 번들 없음
        // O4~O6에 비-SORTER_3D destId → GetBundle null → 404.
        var r4 = await _client!.PostAsJsonAsync($"/api/ops/sorters/{chuteId}/tgtfloor",
            new { floor = 2, operatorName = "op" });
        Assert.Equal(HttpStatusCode.NotFound, r4.StatusCode);

        var r5 = await _client!.PostAsJsonAsync($"/api/ops/sorters/{chuteId}/clear-r",
            new { operatorName = "op" });
        Assert.Equal(HttpStatusCode.NotFound, r5.StatusCode);

        var r6 = await _client!.PostAsJsonAsync($"/api/ops/sorters/999999/cell-assign",
            new { cellNo = 1, seq = 1, operatorName = "op" });
        Assert.Equal(HttpStatusCode.NotFound, r6.StatusCode);
    }

    [Fact]
    public async Task ClearChute_NonChuteOrUnknownDestId_Returns404()
    {
        long sorterId = SorterId();
        // O1 clear는 CHUTE 전용 — 소터 destId → 404.
        var r = await _client!.PostAsJsonAsync($"/api/ops/chutes/{sorterId}/clear",
            new { operatorName = "op" });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);

        var r2 = await _client!.PostAsJsonAsync($"/api/ops/chutes/999999/clear",
            new { operatorName = "op" });
        Assert.Equal(HttpStatusCode.NotFound, r2.StatusCode);
    }

    [Fact]
    public async Task Pause_UnknownDestId_Returns404()
    {
        var r = await _client!.PostAsJsonAsync("/api/ops/destinations/999999/pause",
            new { operatorName = "op" });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<DestinationQueryResponse> If05Sorter(int pId)
    {
        var resp = await _client!.PostAsJsonAsync("/api/v1/destination-query",
            new { pId, agvNo = 1, barcode = "TEST-BARCODE-3", inductionNo = 1, qty = 1, timeStamp = (string?)null });
        return (await resp.Content.ReadFromJsonAsync<DestinationQueryResponse>())!;
    }

    private long SorterId()
    {
        using var db = _factory!.CreateDbScope();
        return db.Destinations.First(d => d.DestType == DestType.SORTER_3D && d.IsActive).Id;
    }

    private long ChuteId(int chuteNo)
    {
        using var db = _factory!.CreateDbScope();
        return db.Destinations.First(d => d.ChuteNo == chuteNo && d.DestType == DestType.CHUTE).Id;
    }

    private bool HasOpLog(OperationLogCategory category, string action)
    {
        using var db = _factory!.CreateDbScope();
        return db.OperationLogs.Any(l => l.Category == category && l.Action == action);
    }

    private async Task WaitUntilAsync(Func<bool> condition, int timeoutMs, string msg, int pollMs = 40)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTimeOffset.Now > deadline)
                Assert.Fail($"WaitUntil 타임아웃({timeoutMs}ms): {msg}");
            await Task.Delay(pollMs);
        }
    }

    // 비-이벤트(변화가 일어나지 않음)를 확인하는 바운드 대기 — IT3b 패턴(핑퐁 스킵 검증용).
    private static async Task PollForDurationAsync(int ms) => await Task.Delay(ms);

    private static int GetFreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}
