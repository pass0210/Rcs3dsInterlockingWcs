using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Data;
using Wcs.Tests.B2B;   // B2bWebApplicationFactory(in-memory SQLite 청정 호스트) 재사용.
using Xunit;

namespace Wcs.Tests.B2C;

// ════════════════════════════════════════════════════════════════════════════
// S-B2C-FACILITY 설비 관리 + 모니터 목적지 열거 + 혼합 토폴로지 IF-05 라우팅 통합 테스트.
//   · GET  /api/monitor/destinations — 전 목적지(CHUTE+SORTER_3D) 열거.
//   · POST /api/b2c/facility/destinations — 목적지 생성(소터/슈트)·중복 F·chute_detail.
//   · POST /api/b2c/facility/sorters/{id}/cells — 셀 벌크(행×열 순차·멱등·OQ-1).
//   · POST /api/b2c/facility/orders/assign|unassign — 오더→목적지(+셀) 할당·해제(OQ-3 가드).
//   · POST /api/b2c/facility/destinations/{id}/activate — 비활성화 가드(OQ-2 · force).
//   · 혼합 라우팅 E2E: 미할당 오더를 슈트/소터 양쪽 배정 → IF-05 각각 OK(슈트 chuteNo · 소터 chuteNo).
//   · reset E2E: 소터 오더 IF-05 예약 → reset(force) → 재 IF-05 재예약.
//
// B2bWebApplicationFactory 재사용(0 소터 게이트웨이 — IF-05 소터 가부는 SorterCanAcceptBarcode(DB)
// + status.Compute(bundle null → paused=false)라 라이브 게이트웨이 불요). 공유 DB → 테스트별 고유 chuteNo.
// ════════════════════════════════════════════════════════════════════════════
public class B2cFacilityApiTests : IClassFixture<B2bWebApplicationFactory>
{
    private readonly B2bWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public B2cFacilityApiTests(B2bWebApplicationFactory factory)
    {
        _factory = factory;
        _client  = factory.CreateClient();
    }

    private sealed record MgmtResp(string Status, string Message, Dictionary<string, int>? Counts);

    // ── 목적지 열거(A2) ──────────────────────────────────────────────────────────
    [Fact]
    public async Task MonitorDestinations_EnumeratesChuteAndSorter_WithCounts()
    {
        await CreateChute(101);
        await CreateSorter(130);
        await ConfigureCells(SorterId(130), rows: 2, cols: 3);   // 6 cells

        var res = await _client.GetAsync("/api/monitor/destinations");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());

        var chute = doc.RootElement.EnumerateArray().Single(e => e.GetProperty("chuteNo").GetInt32() == 101);
        Assert.Equal("CHUTE", chute.GetProperty("destType").GetString());
        Assert.True(chute.GetProperty("workFullQty").GetInt32() > 0);

        var sorter = doc.RootElement.EnumerateArray().Single(e => e.GetProperty("chuteNo").GetInt32() == 130);
        Assert.Equal("SORTER_3D", sorter.GetProperty("destType").GetString());
        Assert.Equal(6, sorter.GetProperty("cellTotal").GetInt32());
        Assert.Equal(6, sorter.GetProperty("cellEnabled").GetInt32());
    }

    // ── 목적지 생성 + 중복 F + chute_detail ───────────────────────────────────────
    [Fact]
    public async Task CreateDestination_Chute_CreatesChuteDetail_DuplicateRejected()
    {
        var ok = await PostMgmt("/api/b2c/facility/destinations",
            new { chuteNo = 111, destType = "CHUTE", workFullQty = 50, operatorName = "관리자" });
        Assert.Equal("S", ok.Status);
        Assert.Equal(1, ok.Counts!["chuteDetailCreated"]);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            var dest = db.Destinations.Single(d => d.ChuteNo == 111);
            Assert.Equal(DestType.CHUTE, dest.DestType);
            Assert.True(db.ChuteDetails.Any(cd => cd.DestinationId == dest.Id && cd.WorkFullQty == 50));
        }

        // 중복 chuteNo → F.
        var dup = await PostMgmt("/api/b2c/facility/destinations",
            new { chuteNo = 111, destType = "SORTER_3D", operatorName = "관리자" });
        Assert.Equal("F", dup.Status);
    }

    // ── 목적지 생성: 잘못된 destType → F ──────────────────────────────────────────
    [Fact]
    public async Task CreateDestination_InvalidType_F()
    {
        var res = await PostMgmt("/api/b2c/facility/destinations",
            new { chuteNo = 112, destType = "BOGUS", operatorName = "op" });
        Assert.Equal("F", res.Status);
    }

    // ── 셀 벌크(행×열 순차·멱등) ──────────────────────────────────────────────────
    [Fact]
    public async Task ConfigureCells_RowsCols_SequentialAndIdempotent()
    {
        await CreateSorter(141);
        long id = SorterId(141);

        var first = await PostMgmt($"/api/b2c/facility/sorters/{id}/cells",
            new { rows = 4, cols = 5, capacity = 10, enabled = true, operatorName = "op" });
        Assert.Equal("S", first.Status);
        Assert.Equal(20, first.Counts!["cellsCreated"]);
        Assert.Equal(20, first.Counts["cellTotal"]);

        // 순차 cellNo 1..20 검증.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            var cellNos = db.Cells.Where(c => c.DestinationId == id).Select(c => c.CellNo).OrderBy(x => x).ToList();
            Assert.Equal(Enumerable.Range(1, 20), cellNos);
        }

        // 멱등 재실행(같은 형상) → 신규 0.
        var again = await PostMgmt($"/api/b2c/facility/sorters/{id}/cells",
            new { rows = 4, cols = 5, capacity = 10, enabled = true, operatorName = "op" });
        Assert.Equal(0, again.Counts!["cellsCreated"]);
        Assert.Equal(0, again.Counts["cellsUpdated"]);
    }

    // ── 셀 벌크: CHUTE 대상 → F(소터 전용) ────────────────────────────────────────
    [Fact]
    public async Task ConfigureCells_OnChute_F()
    {
        await CreateChute(142);
        long id = ChuteId(142);
        var res = await PostMgmt($"/api/b2c/facility/sorters/{id}/cells", new { rows = 1, cols = 1 });
        Assert.Equal("F", res.Status);
    }

    // ── 오더 할당(슈트) + 미할당 목록 반영 + 해제 ──────────────────────────────────
    [Fact]
    public async Task AssignOrder_ToChute_ThenUnassign()
    {
        await CreateChute(151);
        long chuteId = ChuteId(151);
        long orderId = await GenerateOneOrder(prefix: "ASG", batch: "ASG-B");

        // 할당 전: 미할당 목록에 포함.
        var unassignedBefore = await GetOrders(assigned: false);
        Assert.Contains(unassignedBefore, o => o.GetProperty("orderId").GetInt64() == orderId);

        var assign = await PostMgmt("/api/b2c/facility/orders/assign",
            new { orderId, destinationId = chuteId, operatorName = "op" });
        Assert.Equal("S", assign.Status);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            var order = db.Orders.Single(o => o.Id == orderId);
            Assert.Equal(chuteId, order.DestinationId);
            Assert.Equal(DestAssignType.MANUAL, order.DestAssignType);
        }

        // 해제 → 미할당 복귀.
        var unassign = await PostMgmt("/api/b2c/facility/orders/unassign", new { orderId, operatorName = "op" });
        Assert.Equal("S", unassign.Status);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            Assert.Null(db.Orders.Single(o => o.Id == orderId).DestinationId);
        }
    }

    // ── 오더 할당(소터+셀) → cell_assignment 생성 ─────────────────────────────────
    [Fact]
    public async Task AssignOrder_ToSorterCell_CreatesCellAssignment()
    {
        await CreateSorter(161);
        long sorterId = SorterId(161);
        await ConfigureCells(sorterId, rows: 1, cols: 3);
        long orderId = await GenerateOneOrder(prefix: "SCEL", batch: "SCEL-B");

        var assign = await PostMgmt("/api/b2c/facility/orders/assign",
            new { orderId, destinationId = sorterId, cellNo = 2, operatorName = "op" });
        Assert.Equal("S", assign.Status);
        Assert.Equal(1, assign.Counts!["cellAssignmentCreated"]);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        var cell = db.Cells.Single(c => c.DestinationId == sorterId && c.CellNo == 2);
        Assert.True(db.CellAssignments.Any(a => a.OrderId == orderId && a.CellId == cell.Id && a.ReleasedAt == null));
    }

    // ── OQ-3 가드: 진행 중 오더(예약 있음)는 재배정 거부 ───────────────────────────
    [Fact]
    public async Task AssignOrder_StartedOrder_RefusedByOq3Guard()
    {
        await CreateChute(181);
        await CreateChute(182);
        long chute1 = ChuteId(181), chute2 = ChuteId(182);
        long orderId = await GenerateOneOrder(prefix: "OQ3", batch: "OQ3-B");

        // 배정 → IF-05 예약(reserved += 1) → 진행 중으로 만든다.
        await PostMgmt("/api/b2c/facility/orders/assign", new { orderId, destinationId = chute1, operatorName = "op" });
        var if05 = await PostIf05(pId: 21101, barcode: "OQ3-01", qty: 1);
        Assert.Equal("OK", if05);

        // 재배정 시도 → OQ-3 거부(F).
        var reassign = await PostMgmt("/api/b2c/facility/orders/assign",
            new { orderId, destinationId = chute2, operatorName = "op" });
        Assert.Equal("F", reassign.Status);
        Assert.True(reassign.Counts!["reserved"] >= 1);
    }

    // ── OQ-3 보완: DENIED-only 오더는 배정 허용 / RESERVED 이력 오더는 여전히 차단 ────
    //   RCS 선조회(IF-05) NO_DEST → DENIED 피스만 남은 오더(물리 라우팅 0)는 후할당/재할당 허용.
    //   (AUTO 배정 비결정성 회피 위해 DENIED/RESERVED 피스를 DB 로 직접 조성 — 결정적·격리.)
    [Fact]
    public async Task AssignOrder_DeniedOnlyOrder_Allowed_ReservedOrder_Blocked()
    {
        await CreateChute(183);
        long deniedOrder = await GenerateOneOrder(prefix: "DEN", batch: "DEN-B");
        SeedPieceForOrder("DEN-01", PieceStatus.DENIED, destId: null);   // RCS 선조회 NO_DEST 재현.

        // DENIED-only 미시작 오더 — 차단 피스 0(DENIED 제외) → canReassign true.
        var unassigned = await GetOrders(assigned: false);
        var row = unassigned.Single(o => o.GetProperty("orderId").GetInt64() == deniedOrder);
        Assert.False(row.GetProperty("hasActivePiece").GetBoolean(), "DENIED 피스는 차단 피스로 카운트 안 함(OQ-3 보완)");
        Assert.True(row.GetProperty("canReassign").GetBoolean(), "DENIED-only 오더는 배정 허용");

        var assign = await PostMgmt("/api/b2c/facility/orders/assign",
            new { orderId = deniedOrder, destinationId = ChuteId(183), operatorName = "op" });
        Assert.Equal("S", assign.Status);   // DENIED 예외가 가드를 통과시킴.

        // 대조: RESERVED(비-DENIED 활성) 이력이 있는 오더는 여전히 차단.
        await CreateChute(184);
        long reservedOrder = await GenerateOneOrder(prefix: "RSV", batch: "RSV-B");
        SeedPieceForOrder("RSV-01", PieceStatus.RESERVED, destId: ChuteId(184), reserveQty: 1);

        var blockedRow = (await GetOrders(assigned: false)).SingleOrDefault(o => o.GetProperty("orderId").GetInt64() == reservedOrder);
        // 예약 오더는 미할당 목록엔 있으나(destination 미설정) canReassign false.
        Assert.True(blockedRow.ValueKind != System.Text.Json.JsonValueKind.Undefined);
        Assert.False(blockedRow.GetProperty("canReassign").GetBoolean(), "RESERVED 이력 → 차단 유지");

        var reassign = await PostMgmt("/api/b2c/facility/orders/assign",
            new { orderId = reservedOrder, destinationId = ChuteId(184), operatorName = "op" });
        Assert.Equal("F", reassign.Status);
        Assert.True(reassign.Counts!["reserved"] >= 1);
    }

    // order_item(barcode) 에 지정 상태 활성 piece 를 직접 삽입(가드 결정적 조성).
    private void SeedPieceForOrder(string barcode, PieceStatus status, long? destId, int reserveQty = 0)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        var item = db.OrderItems.First(i => i.Barcode == barcode);
        if (reserveQty > 0) { item.ReservedQty += reserveQty; }
        db.Pieces.Add(new Piece
        {
            PId = 22000 + (int)(item.Id % 5000), IsActive = true, Barcode = barcode, Qty = 1,
            DestinationId = destId, OrderItemId = item.Id, Status = status,
            CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        db.SaveChanges();
    }

    // ── OQ-2 비활성화 가드: 활성 배정 있으면 거부 + force 로 강행 ───────────────────
    [Fact]
    public async Task Deactivate_WithActiveAssignment_RefusedThenForced()
    {
        await CreateSorter(191);
        long sorterId = SorterId(191);
        await ConfigureCells(sorterId, rows: 1, cols: 2);
        long orderId = await GenerateOneOrder(prefix: "DEA", batch: "DEA-B");
        await PostMgmt("/api/b2c/facility/orders/assign",
            new { orderId, destinationId = sorterId, cellNo = 1, operatorName = "op" });

        // force=false → 거부(활성 배정 1).
        var refused = await PostMgmt($"/api/b2c/facility/destinations/{sorterId}/activate",
            new { isActive = false, force = false, operatorName = "op" });
        Assert.Equal("F", refused.Status);
        Assert.True(refused.Counts!["activeAssignments"] >= 1);
        using (var scope = _factory.Services.CreateScope())
            Assert.True(scope.ServiceProvider.GetRequiredService<WcsDbContext>().Destinations.Single(d => d.Id == sorterId).IsActive);

        // force=true → 비활성화.
        var forced = await PostMgmt($"/api/b2c/facility/destinations/{sorterId}/activate",
            new { isActive = false, force = true, operatorName = "op" });
        Assert.Equal("S", forced.Status);
        using (var scope = _factory.Services.CreateScope())
            Assert.False(scope.ServiceProvider.GetRequiredService<WcsDbContext>().Destinations.Single(d => d.Id == sorterId).IsActive);

        // 감사: 거부·강제 전수 기록(operation_log 는 백그라운드 writer 비동기 flush → 출현 대기).
        await WaitUntilAsync(() =>
        {
            using var scope = _factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            return db.OperationLogs.Count(l => l.Action == "B2C_DEST_DEACTIVATE" && l.DestinationId == sorterId) >= 2;
        }, 4000, "B2C_DEST_DEACTIVATE 감사 2행(거부+강제)");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs, string msg, int pollMs = 40)
    {
        var deadline = DateTimeOffset.Now.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTimeOffset.Now > deadline) Assert.Fail($"WaitUntil 타임아웃({timeoutMs}ms): {msg}");
            await Task.Delay(pollMs);
        }
    }

    // ── ★ 혼합 토폴로지 IF-05 라우팅 E2E: 슈트행 OK + 소터행 OK ─────────────────────
    [Fact]
    public async Task Mixed_Topology_IF05_RoutesChuteAndSorter()
    {
        // ① 슈트 + 소터(셀) 생성.
        await CreateChute(201);
        await CreateSorter(231);
        long chuteId  = ChuteId(201);
        long sorterId = SorterId(231);
        await ConfigureCells(sorterId, rows: 1, cols: 2);

        // ② 미할당 오더 2건 생성.
        long chuteOrder  = await GenerateOneOrder(prefix: "MXC", batch: "MX-B", index: 1);
        long sorterOrder = await GenerateOneOrder(prefix: "MXS", batch: "MX-B", index: 1);

        // ③ 슈트/소터에 각각 배정(소터는 셀 지정).
        Assert.Equal("S", (await PostMgmt("/api/b2c/facility/orders/assign",
            new { orderId = chuteOrder, destinationId = chuteId, operatorName = "op" })).Status);
        Assert.Equal("S", (await PostMgmt("/api/b2c/facility/orders/assign",
            new { orderId = sorterOrder, destinationId = sorterId, cellNo = 1, operatorName = "op" })).Status);

        // ④ IF-05 왕복 — 슈트행 → OK + chuteNo=201, 소터행 → OK + chuteNo=231.
        var (chuteRes, chuteChute) = await PostIf05Full(pId: 21201, barcode: "MXC-01", qty: 1);
        Assert.Equal("OK", chuteRes);
        Assert.Equal(201, chuteChute);

        var (sorterRes, sorterChute) = await PostIf05Full(pId: 21202, barcode: "MXS-01", qty: 1);
        Assert.Equal("OK", sorterRes);
        Assert.Equal(231, sorterChute);
    }

    // ── ★ reset E2E(배치 스코프): 생성 → 소터 배정 → IF-05 예약 → reset(force) → 재 IF-05 ───
    //   S-B2C-UX: reset 은 배치 스코프. 소터에 배정된 오더도 그 오더의 배치를 초기화하면 아카이브·리셋된다.
    [Fact]
    public async Task Sorter_Generate_Assign_IF05_Reset_ReInject()
    {
        await CreateSorter(241);
        long sorterId = SorterId(241);
        await ConfigureCells(sorterId, rows: 1, cols: 1);
        long orderId = await GenerateOneOrder(prefix: "RSE", batch: "RSE-B");
        long batchId = BatchId("RSE-B");
        await PostMgmt("/api/b2c/facility/orders/assign",
            new { orderId, destinationId = sorterId, cellNo = 1, operatorName = "op" });

        // IF-05 예약.
        Assert.Equal("OK", await PostIf05(pId: 21301, barcode: "RSE-01", qty: 1));
        Assert.Equal(1, ReservedOf("RSE-01"));

        // reset(force — in-flight RESERVED) → 아카이브 + 수량 리셋. 배치 스코프(batchId)·operatorName 귀속.
        var refused = await PostMgmt("/api/b2c/test-data/reset", new { batchId, force = false, operatorName = "op" });
        Assert.Equal("F", refused.Status);
        var reset = await PostMgmt("/api/b2c/test-data/reset", new { batchId, force = true, operatorName = "op" });
        Assert.Equal("S", reset.Status);
        Assert.Equal(0, ReservedOf("RSE-01"));

        // 재 IF-05(같은 pId·바코드) → 아카이브 dedup 제외로 재예약 성공(정확히 qty).
        Assert.Equal("OK", await PostIf05(pId: 21301, barcode: "RSE-01", qty: 1));
        Assert.Equal(1, ReservedOf("RSE-01"));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        var pieces = db.Pieces.Where(p => p.PId == 21301).ToList();
        Assert.Equal(2, pieces.Count);   // 하드삭제 0 — 옛 아카이브 1 + 새 활성 1.
        Assert.Equal(1, pieces.Count(p => p.ArchivedAt != null));
        Assert.Equal(1, pieces.Count(p => p.ArchivedAt == null && p.IsActive));
    }

    // ── ★ FIX ITER 2(침묵 절단 제거): 배치가 과거 상한(200) 초과여도 전량 반환(take 미지정 기본 상한 상향) ──
    [Fact]
    public async Task GetOrders_LargeBatch_NotClampedAtLegacyCap()
    {
        const int n = 250;   // 과거 기본 상한 200 초과 — 침묵 절단이면 200 만 반환됐을 것.
        var gen = await _client.PostAsJsonAsync("/api/b2c/test-data/generate",
            new { workDate = "2026-07-14", batchNo = "BIG-B", waveNo = 1, plannedQty = n, barcodePrefix = "BIG" });
        Assert.Equal("S", (await gen.Content.ReadFromJsonAsync<MgmtResp>())!.Status);
        long batchId = BatchId("BIG-B");

        // take 미지정(프론트 기본 경로 시뮬 — 서버 기본 상한 = GenerateCountMax) → 250 전량.
        var res = await _client.GetAsync($"/api/b2c/facility/orders?batchId={batchId}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(n, doc.RootElement.GetArrayLength());
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private async Task<MgmtResp> PostMgmt(string path, object body)
    {
        var res = await _client.PostAsJsonAsync(path, body);
        return (await res.Content.ReadFromJsonAsync<MgmtResp>())!;
    }

    private Task CreateChute(int chuteNo) =>
        PostMgmt("/api/b2c/facility/destinations", new { chuteNo, destType = "CHUTE", operatorName = "op" });

    private Task CreateSorter(int chuteNo) =>
        PostMgmt("/api/b2c/facility/destinations", new { chuteNo, destType = "SORTER_3D", operatorName = "op" });

    private Task ConfigureCells(long sorterId, int rows, int cols) =>
        PostMgmt($"/api/b2c/facility/sorters/{sorterId}/cells", new { rows, cols, capacity = 5, enabled = true });

    private long ChuteId(int chuteNo)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        return db.Destinations.Single(d => d.ChuteNo == chuteNo && d.DestType == DestType.CHUTE).Id;
    }

    private long SorterId(int chuteNo)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        return db.Destinations.Single(d => d.ChuteNo == chuteNo && d.DestType == DestType.SORTER_3D).Id;
    }

    // 배치 대리키 조회(reset 배치 스코프 — batchNo 로 최근 1건).
    private long BatchId(string batchNo)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        return db.WorkBatches.OrderByDescending(b => b.Id).First(b => b.BatchNo == batchNo).Id;
    }

    // 미할당 오더 1건 생성 후 orderId 반환("{prefix}-{index:D2}").
    private async Task<long> GenerateOneOrder(string prefix, string batch, int index = 1)
    {
        var gen = await _client.PostAsJsonAsync("/api/b2c/test-data/generate",
            new { workDate = "2026-07-13", batchNo = batch, waveNo = 1, plannedQty = index, barcodePrefix = prefix });
        Assert.Equal("S", (await gen.Content.ReadFromJsonAsync<MgmtResp>())!.Status);
        var orderNo = $"{prefix}-{index:D2}";
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        return db.Orders.Single(o => o.OrderNo == orderNo).Id;
    }

    private async Task<List<JsonElement>> GetOrders(bool assigned)
    {
        var res = await _client.GetAsync($"/api/b2c/facility/orders?assigned={(assigned ? "true" : "false")}");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    private async Task<string> PostIf05(int pId, string barcode, int qty)
    {
        var (result, _) = await PostIf05Full(pId, barcode, qty);
        return result;
    }

    private async Task<(string Result, int? ChuteNo)> PostIf05Full(int pId, string barcode, int qty)
    {
        var res = await _client.PostAsJsonAsync("/api/v1/destination-query", new
        {
            pId, agvNo = 1, barcode, inductionNo = 1, qty, timeStamp = "2026-07-13 10:00:00",
        });
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var result = doc.RootElement.GetProperty("result").GetString()!;
        int? chuteNo = doc.RootElement.TryGetProperty("chuteNo", out var c) && c.ValueKind == JsonValueKind.Number
            ? c.GetInt32() : null;
        return (result, chuteNo);
    }

    private int ReservedOf(string barcode)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        return db.OrderItems.Where(i => i.Barcode == barcode).Sum(i => i.ReservedQty);
    }
}
