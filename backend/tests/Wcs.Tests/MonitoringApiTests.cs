using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wcs.Api;
using Wcs.Api.Monitoring;
using Wcs.Data;
using Wcs.PlcGateway;
using Xunit;
using Xunit.Abstractions;

namespace Wcs.Tests;

// ════════════════════════════════════════════════════════════════════════════
// F1 MonitoringController 통합 테스트 (E1~E7 + fallback 음성 대조 + 페이징/에러케이스)
//
// 테스트 더블 패턴은 ApiIntegrationTests.FakeModbusWebApplicationFactory와 동일하되
// (UseSetting("Database:Provider","Sqlite") + named in-memory SQLite anchor + EnsureCreated +
//  DbSeeder.Seed + SorterRegistry 교체) **DB 이름을 인스턴스별 고유값**으로 둔다.
//
// ※ 재사용하지 않은 이유: FakeModbusWebApplicationFactory._dbName은 `static readonly`라
//   모든 인스턴스가 하나의 in-memory DB를 공유한다. 그 팩토리는 IClassFixture 단일 인스턴스
//   전제로 설계됐고, 본 테스트처럼 테스트마다 새 팩토리를 만들면 공유 DB에서 EnsureCreated/시드
//   충돌·교차오염이 발생한다(기존 파일 무변경 원칙상 그 static 필드는 수정 불가). 따라서 공개
//   헬퍼 클래스(FakeModbusMasterForApi·FakeSorterGatewayRegistry·NopSorterRegistryFactory)만
//   재사용하고, 인스턴스 고유 DB를 쓰는 전용 팩토리를 여기 둔다 → piece·sorter_command 삽입
//   테스트가 서로/타 테스트 클래스와 격리(결정적 단언). Fake Modbus라 실 소켓·포트 경합 0.
// ════════════════════════════════════════════════════════════════════════════
public sealed class MonitoringApiTests
{
    private readonly ITestOutputHelper _out;

    public MonitoringApiTests(ITestOutputHelper output) => _out = output;

    // ── 헬퍼: 시드 조회 ─────────────────────────────────────────────────────────
    private static long SeedBatchId(MonitoringWebApplicationFactory f)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        return db.WorkBatches.First(b => b.BatchNo == "SEED").Id;
    }

    private static long SorterDestId(MonitoringWebApplicationFactory f)
    {
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
        return db.Destinations.First(d => d.DestType == DestType.SORTER_3D).Id;
    }

    // ════════════════════════════════════════════════════════════════════════
    // E1 batches
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task E1_Batches_ReturnsSeededBatch()
    {
        await using var f = new MonitoringWebApplicationFactory();
        var client = f.CreateClient();

        var resp = await client.GetAsync("/api/monitor/batches");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var batches = await resp.Content.ReadFromJsonAsync<List<BatchDto>>();
        Assert.NotNull(batches);
        var seed = batches!.FirstOrDefault(b => b.BatchNo == "SEED");
        Assert.NotNull(seed);
        Assert.Equal("RUNNING", seed!.Status);
        Assert.Equal(1, seed.WaveNo);
        _out.WriteLine($"[E1] batches={batches.Count} seed.status={seed.Status}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // E2 orders — 집계·필터
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task E2_Orders_ByBatch_AggregatesItemSums()
    {
        await using var f = new MonitoringWebApplicationFactory();
        var client = f.CreateClient();
        var batchId = SeedBatchId(f);

        var orders = await client.GetFromJsonAsync<List<OrderProgressDto>>(
            $"/api/monitor/orders?batchId={batchId}");
        Assert.NotNull(orders);
        Assert.True(orders!.Count >= 5, $"시드 오더 5건 이상 기대, 실제 {orders.Count}");

        var ord001 = orders.First(o => o.OrderNo == "ORD-001");
        Assert.Equal("GENERAL", ord001.OrderType);
        Assert.Equal("RUNNING", ord001.Status);
        Assert.Equal(1, ord001.DestinationChuteNo);   // TEST-BARCODE-1 → chuteNo=1
        Assert.Equal(50, ord001.PlannedQty);          // 시드 PlannedQty=50
        Assert.Equal(0, ord001.ReservedQty);
        Assert.Equal(0, ord001.SortedQty);

        // AUTO 오더(ORD-004)는 미배정 → destinationChuteNo null.
        var ord004 = orders.First(o => o.OrderNo == "ORD-004");
        Assert.Null(ord004.DestinationChuteNo);
        _out.WriteLine($"[E2] orders={orders.Count} ORD-001 planned={ord001.PlannedQty} chuteNo={ord001.DestinationChuteNo}");
    }

    [Fact]
    public async Task E2_Orders_StatusFilter()
    {
        await using var f = new MonitoringWebApplicationFactory();
        var client = f.CreateClient();
        var batchId = SeedBatchId(f);

        // RUNNING → 시드 오더 전부(대소문자 무시)
        var running = await client.GetFromJsonAsync<List<OrderProgressDto>>(
            $"/api/monitor/orders?batchId={batchId}&status=running");
        Assert.NotNull(running);
        Assert.All(running!, o => Assert.Equal("RUNNING", o.Status));
        Assert.True(running.Count >= 5);

        // COMPLETED → 시드 없음 → 빈 배열
        var completed = await client.GetFromJsonAsync<List<OrderProgressDto>>(
            $"/api/monitor/orders?batchId={batchId}&status=COMPLETED");
        Assert.NotNull(completed);
        Assert.Empty(completed!);

        // 잘못된 status → 빈 배열(500 아님·일관)
        var bogusResp = await client.GetAsync($"/api/monitor/orders?batchId={batchId}&status=BOGUS");
        Assert.Equal(HttpStatusCode.OK, bogusResp.StatusCode);
        var bogus = await bogusResp.Content.ReadFromJsonAsync<List<OrderProgressDto>>();
        Assert.Empty(bogus!);
        _out.WriteLine($"[E2필터] running={running.Count} completed=0 bogus=0");
    }

    // ════════════════════════════════════════════════════════════════════════
    // E3 order items
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task E3_OrderItems_ReturnsItems()
    {
        await using var f = new MonitoringWebApplicationFactory();
        var client = f.CreateClient();
        var batchId = SeedBatchId(f);

        var orders = await client.GetFromJsonAsync<List<OrderProgressDto>>(
            $"/api/monitor/orders?batchId={batchId}");
        var ord001Id = orders!.First(o => o.OrderNo == "ORD-001").Id;

        var items = await client.GetFromJsonAsync<List<OrderItemDto>>(
            $"/api/monitor/orders/{ord001Id}/items");
        Assert.NotNull(items);
        var item = Assert.Single(items!);
        Assert.Equal("TEST-BARCODE-1", item.Barcode);
        Assert.Equal(50, item.PlannedQty);
        _out.WriteLine($"[E3] ORD-001 items={items.Count} barcode={item.Barcode}");
    }

    [Fact]
    public async Task E3_OrderItems_UnknownOrder_EmptyList()
    {
        await using var f = new MonitoringWebApplicationFactory();
        var client = f.CreateClient();

        var resp = await client.GetAsync("/api/monitor/orders/999999/items");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var items = await resp.Content.ReadFromJsonAsync<List<OrderItemDto>>();
        Assert.Empty(items!);
        _out.WriteLine("[E3] 미존재 오더 → 200 빈 배열");
    }

    // ════════════════════════════════════════════════════════════════════════
    // E4 in-flight pieces — 상태 필터 + 키셋 커서 페이징
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task E4_InFlight_FiltersStatusAndPaginatesByCursor()
    {
        await using var f = new MonitoringWebApplicationFactory();
        var client = f.CreateClient();

        // 데이터 삽입: 이동중 3건(QUERIED/RESERVED/PERMITTED) + 종료 1건(DEPOSITED — 제외돼야 함)
        using (var scope = f.Services.CreateScope())
        {
            var db  = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            var now = DateTime.UtcNow;
            var dest1 = db.Destinations.First(d => d.ChuteNo == 1 && d.DestType == DestType.CHUTE);
            var agv1  = db.Agvs.First(a => a.AgvNo == 1);
            var ind1  = db.Inductions.First(i => i.InductionNo == 1);

            db.Pieces.AddRange(
                NewPiece(40001, "IF-Q",  PieceStatus.QUERIED,   now, dest1.Id, agv1.Id, ind1.Id),
                NewPiece(40002, "IF-R",  PieceStatus.RESERVED,  now, dest1.Id, agv1.Id, ind1.Id),
                NewPiece(40003, "IF-P",  PieceStatus.PERMITTED, now, dest1.Id, agv1.Id, ind1.Id),
                NewPiece(40004, "DONE",  PieceStatus.DEPOSITED, now, dest1.Id, agv1.Id, ind1.Id));
            db.SaveChanges();
        }

        // 1페이지 take=2 → 최신순(Id desc) 상위 2건 + nextCursor
        var page1 = await client.GetFromJsonAsync<PagedResult<InFlightPieceDto>>(
            "/api/monitor/pieces/in-flight?take=2");
        Assert.NotNull(page1);
        Assert.Equal(2, page1!.Items.Count);
        Assert.NotNull(page1.NextCursor);
        // 최신순: 40003(PERMITTED), 40002(RESERVED)
        Assert.Equal(40003, page1.Items[0].PId);
        Assert.Equal(40002, page1.Items[1].PId);
        // 조인 검증: chuteNo=1, agvNo=1, inductionNo=1
        Assert.Equal(1, page1.Items[0].DestinationChuteNo);
        Assert.Equal(1, page1.Items[0].AgvNo);
        Assert.Equal(1, page1.Items[0].InductionNo);

        // 2페이지 cursor 이어받기 → 40001만 남음, DEPOSITED(40004) 제외
        var page2 = await client.GetFromJsonAsync<PagedResult<InFlightPieceDto>>(
            $"/api/monitor/pieces/in-flight?take=2&cursor={page1.NextCursor}");
        Assert.NotNull(page2);
        var last = Assert.Single(page2!.Items);
        Assert.Equal(40001, last.PId);
        Assert.Null(page2.NextCursor);   // 마지막 페이지
        Assert.DoesNotContain(page2.Items, p => p.Status == "DEPOSITED");
        _out.WriteLine($"[E4] page1={page1.Items.Count} next={page1.NextCursor} page2={page2.Items.Count} (DEPOSITED 제외)");
    }

    [Fact]
    public async Task E4_InFlight_InvalidCursor_Returns400()
    {
        await using var f = new MonitoringWebApplicationFactory();
        var client = f.CreateClient();

        // 비-정수 커서 → [ApiController] 모델 바인딩 자동 400(long? 파싱 실패·500 아님)
        var resp = await client.GetAsync("/api/monitor/pieces/in-flight?cursor=not-a-number");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        _out.WriteLine("[E4] 잘못된 커서 → 400");
    }

    [Fact]
    public async Task E4_InFlight_LargeTake_ClampedAndSucceeds()
    {
        await using var f = new MonitoringWebApplicationFactory();
        var client = f.CreateClient();

        // take 상한 초과 → clamp(무한 로드 아님). 200 + 항목 ≤ TakeMax.
        var page = await client.GetFromJsonAsync<PagedResult<InFlightPieceDto>>(
            "/api/monitor/pieces/in-flight?take=100000");
        Assert.NotNull(page);
        Assert.True(page!.Items.Count <= MonitoringQueries.TakeMax);
        _out.WriteLine($"[E4] take=100000 → clamp(≤{MonitoringQueries.TakeMax}) items={page.Items.Count}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // E5 sorters + readiness
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task E5_Sorters_ReturnsSorterWithReadinessFields()
    {
        await using var f = new MonitoringWebApplicationFactory();
        var client = f.CreateClient();

        var sorters = await client.GetFromJsonAsync<List<SorterStatusDto>>("/api/monitor/sorters");
        Assert.NotNull(sorters);
        var s = sorters!.FirstOrDefault(x => x.ChuteNo == 30);   // 시드 소터 chuteNo=30
        Assert.NotNull(s);
        Assert.True(s!.DestId > 0);
        // readiness 필드는 DestinationStatusService.Compute 산출 — 존재/타입만 단언(운영상태는 폴 타이밍 의존).
        _out.WriteLine($"[E5] chuteNo={s.ChuteNo} destId={s.DestId} online={s.Online} ready={s.Ready} full={s.Full} paused={s.Paused}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // E6 cells
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task E6_Cells_ReturnsSeedCells()
    {
        await using var f = new MonitoringWebApplicationFactory();
        var client = f.CreateClient();
        var destId = SorterDestId(f);

        var cells = await client.GetFromJsonAsync<List<CellStatusDto>>(
            $"/api/monitor/sorters/{destId}/cells");
        Assert.NotNull(cells);
        Assert.Equal(3, cells!.Count);   // 시드 셀 1~3
        Assert.Equal(new[] { 1, 2, 3 }, cells.Select(c => c.CellNo).OrderBy(x => x).ToArray());
        Assert.All(cells, c =>
        {
            Assert.True(c.Enabled);
            Assert.Equal(0, c.CurrentQty);   // 적재 이력 없음
            Assert.False(c.Occupied);        // 활성 배정 없음
        });
        _out.WriteLine($"[E6] cells={cells.Count} (seed 1~3, currentQty=0)");
    }

    [Fact]
    public async Task E6_Cells_ReflectsLoadedQtyAndAssignment()
    {
        await using var f = new MonitoringWebApplicationFactory();
        var client = f.CreateClient();
        var destId = SorterDestId(f);

        string orderNo;
        using (var scope = f.Services.CreateScope())
        {
            var db  = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            var now = DateTime.UtcNow;
            var cell1 = db.Cells.First(c => c.DestinationId == destId && c.CellNo == 1);
            var order = db.Orders.First(o => o.OrderNo == "ORD-003"); // 소터(chuteNo=30) 오더
            orderNo = order.OrderNo;

            // LOADED piece + COMPLETED sorter_command(qty=2) → cell1 currentQty=2
            var piece = NewPiece(41001, "SC-LOAD", PieceStatus.LOADED, now, destId, null, null);
            piece.Qty = 2;
            db.Pieces.Add(piece);
            db.SaveChanges();

            db.SorterCommands.Add(new SorterCommand
            {
                PieceId = piece.Id, CellId = cell1.Id, CSeq = 1, CellNo = 1,
                CWrittenAt = now, RSeq = 1, RCellNo = 1, TiltedAt = now,
                Status = SorterCommandStatus.COMPLETED, CreatedAt = now,
            });
            // 활성 cell_assignment: cell1 → ORD-003
            db.CellAssignments.Add(new CellAssignment
            {
                CellId = cell1.Id, OrderId = order.Id, AssignedAt = now, ReleasedAt = null, CreatedAt = now,
            });
            db.SaveChanges();
        }

        var cells = await client.GetFromJsonAsync<List<CellStatusDto>>(
            $"/api/monitor/sorters/{destId}/cells");
        var c1 = cells!.First(c => c.CellNo == 1);
        Assert.Equal(2, c1.CurrentQty);          // SorterCellQty 재사용 산출
        Assert.True(c1.Occupied);
        Assert.Equal(orderNo, c1.AssignedOrderNo);
        _out.WriteLine($"[E6] cell1 currentQty={c1.CurrentQty} occupied={c1.Occupied} order={c1.AssignedOrderNo}");
    }

    [Fact]
    public async Task E6_Cells_UnknownDest_EmptyList()
    {
        await using var f = new MonitoringWebApplicationFactory();
        var client = f.CreateClient();

        var resp = await client.GetAsync("/api/monitor/sorters/999999/cells");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var cells = await resp.Content.ReadFromJsonAsync<List<CellStatusDto>>();
        Assert.Empty(cells!);
        _out.WriteLine("[E6] 미존재 destId → 200 빈 배열");
    }

    // ════════════════════════════════════════════════════════════════════════
    // E7 sorter-commands — 형상 + 키셋 커서 페이징
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task E7_SorterCommands_ShapeAndCursorPaging()
    {
        await using var f = new MonitoringWebApplicationFactory();
        var client = f.CreateClient();
        var destId = SorterDestId(f);

        using (var scope = f.Services.CreateScope())
        {
            var db  = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            var now = DateTime.UtcNow;
            var cell1 = db.Cells.First(c => c.DestinationId == destId && c.CellNo == 1);
            var piece = NewPiece(42001, "SC-BC", PieceStatus.LOADED, now, destId, null, null);
            db.Pieces.Add(piece);
            db.SaveChanges();

            for (int i = 1; i <= 3; i++)
                db.SorterCommands.Add(new SorterCommand
                {
                    PieceId = piece.Id, CellId = cell1.Id, CSeq = i, CellNo = 1,
                    CWrittenAt = now, RSeq = i, RCellNo = 1, TiltedAt = now,
                    Status = SorterCommandStatus.COMPLETED, CreatedAt = now,
                });
            db.SaveChanges();
        }

        var page1 = await client.GetFromJsonAsync<PagedResult<SorterCommandDto>>(
            $"/api/monitor/sorter-commands?destId={destId}&take=2");
        Assert.NotNull(page1);
        Assert.Equal(2, page1!.Items.Count);
        Assert.NotNull(page1.NextCursor);
        var top = page1.Items[0];
        Assert.Equal(42001, top.PId);
        Assert.Equal("SC-BC", top.Barcode);
        Assert.Equal(1, top.CellNo);
        Assert.Equal("COMPLETED", top.Status);

        var page2 = await client.GetFromJsonAsync<PagedResult<SorterCommandDto>>(
            $"/api/monitor/sorter-commands?destId={destId}&take=2&cursor={page1.NextCursor}");
        Assert.Single(page2!.Items);
        Assert.Null(page2.NextCursor);
        _out.WriteLine($"[E7] page1={page1.Items.Count} next={page1.NextCursor} page2={page2.Items.Count}");
    }

    // ════════════════════════════════════════════════════════════════════════
    // fallback 음성 대조 — /api/** 미존재 경로는 index.html(HTML 200)로 안 떨어짐(함정 #1·C2)
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task Fallback_UnknownApiRoute_Returns404NotHtml()
    {
        await using var f = new MonitoringWebApplicationFactory();
        var client = f.CreateClient();

        var resp = await client.GetAsync("/api/monitor/this-route-does-not-exist");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<html", body, StringComparison.OrdinalIgnoreCase);
        _out.WriteLine($"[fallback] /api/monitor/오타 → {(int)resp.StatusCode} (index.html 미삼킴)");
    }

    // ════════════════════════════════════════════════════════════════════════
    // ClampTake 단위 — take 상한/기본값 정책(순수)
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public void ClampTake_EnforcesBoundsAndDefault()
    {
        Assert.Equal(MonitoringQueries.TakeDefault, MonitoringQueries.ClampTake(null));
        Assert.Equal(MonitoringQueries.TakeDefault, MonitoringQueries.ClampTake(0));
        Assert.Equal(MonitoringQueries.TakeDefault, MonitoringQueries.ClampTake(-5));
        Assert.Equal(10, MonitoringQueries.ClampTake(10));
        Assert.Equal(MonitoringQueries.TakeMax, MonitoringQueries.ClampTake(100000));
    }

    // ── 헬퍼: 최소 piece 생성 ───────────────────────────────────────────────────
    private static Piece NewPiece(
        int pid, string barcode, PieceStatus status, DateTime now,
        long? destId, long? agvId, long? inductionId) => new()
    {
        PId           = pid,
        IsActive      = true,
        Barcode       = barcode,
        Qty           = 1,
        Status        = status,
        DestinationId = destId,
        AgvId         = agvId,
        InductionId   = inductionId,
        DepositedAt   = status == PieceStatus.DEPOSITED ? now : null,
        CreatedAt     = now,
        UpdatedAt     = now,
    };
}

// ════════════════════════════════════════════════════════════════════════════
// MonitoringWebApplicationFactory — 인스턴스별 고유 in-memory SQLite DB를 쓰는 테스트 팩토리.
// ApiIntegrationTests.FakeModbusWebApplicationFactory와 동일 배선이나 DB 이름이 인스턴스 필드라
// 테스트마다 완전 격리된다(위 클래스 주석 참조). 공개 헬퍼 클래스(FakeModbusMasterForApi·
// FakeSorterGatewayRegistry·NopSorterRegistryFactory)를 그대로 재사용한다(기존 파일 무변경).
// ════════════════════════════════════════════════════════════════════════════
public sealed class MonitoringWebApplicationFactory : WebApplicationFactory<Program>
{
    // 인스턴스별 고유 DB 이름 — 병렬/반복 생성 시 상호 격리(static 아님).
    private readonly string _dbName = $"MonTest_{Guid.NewGuid():N}";
    private readonly Microsoft.Data.Sqlite.SqliteConnection _anchor;

    private readonly FakeModbusMasterForApi _master = new();
    private readonly PlcWriteQueue          _writeQueue = new();
    private readonly PlcGatewayOptions      _gwOpt = new()
    {
        Host = "127.0.0.1", Port = 1502,
        PollIntervalMs = 150, OfflineAfterFailures = 3, WriteTimeoutMs = 1000,
        RFlagPollMs = 100, RFlagTimeoutMs = 30000, CFlagTimeoutMs = 5000,
    };
    private readonly PlcPollingService     _polling;
    private readonly HandshakeOrchestrator _handshake;

    public MonitoringWebApplicationFactory()
    {
        _anchor = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={_dbName};Mode=Memory;Cache=Shared");
        _anchor.Open();
        _polling   = new PlcPollingService(_gwOpt, _writeQueue, _master);
        _handshake = new HandshakeOrchestrator(_polling, _gwOpt);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // base appsettings=SqlServer → host setting으로 Provider=Sqlite 주입(즉시 평가 키는 UseSetting만
        // 유효 — lessons 2026-06-30). connection은 아래에서 named in-memory(anchor)로 재등록.
        builder.UseSetting("Database:Provider", "Sqlite");

        builder.ConfigureServices(services =>
        {
            // WcsDbContext를 named in-memory SQLite로 교체
            var dbDescriptors = services
                .Where(d => d.ServiceType == typeof(DbContextOptions<WcsDbContext>)
                         || d.ServiceType == typeof(WcsDbContext))
                .ToList();
            foreach (var d in dbDescriptors) services.Remove(d);

            var connStr = $"Data Source={_dbName};Mode=Memory;Cache=Shared";
            services.AddDbContext<WcsDbContext>(opts =>
                opts.UseSqlite(connStr, sqlite => sqlite.CommandTimeout(30))
                    .ConfigureWarnings(w => w.Ignore(
                        Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)),
                ServiceLifetime.Scoped);

            // 스키마 생성 + 시드(anchor 연결 — DB 수명 고정)
            var dbOpts = new DbContextOptionsBuilder<WcsDbContext>()
                .UseSqlite(_anchor)
                .ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning))
                .Options;
            using var db = new WcsDbContext(dbOpts);
            db.Database.EnsureCreated();
            DbSeeder.Seed(db, new Dictionary<string, int> { ["1"] = 1, ["2"] = 2 });

            var sorterDest = db.Destinations.First(d => d.DestType == DestType.SORTER_3D && d.IsActive);
            var bundle = new SorterBundleHandle(
                destinationId: sorterDest.Id, chuteNo: sorterDest.ChuteNo,
                polling: _polling, handshake: _handshake);
            var registry = new FakeSorterGatewayRegistry(bundle);

            // SorterRegistryFactory(IHostedService+ISorterGatewayRegistry) 교체 — DB 기동 판별 우회.
            var srfToRemove = services
                .Where(d => d.ServiceType == typeof(SorterRegistryFactory)
                         || d.ServiceType == typeof(ISorterGatewayRegistry))
                .ToList();
            foreach (var d in srfToRemove) services.Remove(d);

            var nullHosted = services
                .Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == null)
                .ToList();
            foreach (var d in nullHosted) services.Remove(d);

            services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<ChuteCapacityService>());

            var nop = new NopSorterRegistryFactory(_polling, registry);
            services.AddSingleton<ISorterGatewayRegistry>(nop);
            services.AddSingleton<IHostedService>(nop);
        });
    }

    public override async ValueTask DisposeAsync()
    {
        // 쓰기 큐 채널을 먼저 완료 → 쓰기 컨슈머 결정적 종료(teardown 경쟁 회피 — 기존 팩토리와 동일).
        _writeQueue.Writer.TryComplete();
        await base.DisposeAsync().ConfigureAwait(false);
        _anchor.Dispose();
        GC.SuppressFinalize(this);
    }
}
