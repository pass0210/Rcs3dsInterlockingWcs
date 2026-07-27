using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wcs.Data;
using Wcs.Tests.B2B;   // B2bWebApplicationFactory(in-memory SQLite 청정 호스트) 재사용.
using Xunit;

namespace Wcs.Tests.B2C;

// ════════════════════════════════════════════════════════════════════════════
// S-B2C-BARCODE-MULTI-FIX — 통합 검증(현장 온프레미스 실발생 두 결함).
//
//   Fix 2 (IF-05 배정-우선 결정적 선택): 같은 바코드가 (배정 오더, 미배정 오더) 둘에 매칭될 때
//     QueryDestination 이 **배정 오더 목적지로 OK+chuteNo**(정렬 없는 .FirstOrDefault() 가 미배정 오더를
//     골라 NG/NO_DEST 를 뱉던 결함 수정). 다중 배정 tiebreak 결정성도 실측.
//   Fix 1 (배치 상세 per-item): GET /api/b2c/facility/batch-items?batchId= 가 order_item(바코드)당 1행 —
//     1 오더:N 바코드 → N행. 항목별 수량 + 오더 레벨 필드(상태·목적지·할당셀).
//
// B2bWebApplicationFactory: 0 소터 게이트웨이(IF-05 슈트 라우팅은 게이트웨이 불요). 인스턴스별 격리 DB —
//   이 클래스 전용 fixture 라 타 클래스와 무충돌. 시드는 DB scope 로 직접(결정적·AUTO 비결정성 회피).
// ════════════════════════════════════════════════════════════════════════════
public class B2cBarcodeMultiFixTests : IClassFixture<B2bWebApplicationFactory>
{
    private readonly B2bWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public B2cBarcodeMultiFixTests(B2bWebApplicationFactory factory)
    {
        _factory = factory;
        _client  = factory.CreateClient();
    }

    // ════════════════════════════════════════════════════════════════════════
    // Fix 2 — 같은 바코드: 배정 오더 + 미배정 오더 → IF-05 가 배정 오더 목적지로 OK+chuteNo.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task If05_DuplicateBarcode_PrefersAssignedOrder()
    {
        const int chuteNo = 301;
        const string barcode = "MULTIDUP-01";
        long assignedItemId, unassignedItemId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db    = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            var chute = SeedChute(db, chuteNo);
            var batch = SeedBatch(db, "MULTIDUP-B", 1);

            // 미배정 오더(더 작은 OrderId — 정렬 없는 구 .FirstOrDefault() 라면 이게 뽑혀 NG 로 떨어졌을 것).
            var unassigned = SeedOrder(db, batch, "MULTIDUP-U", destId: null, assignedAt: null,
                status: OrderStatus.WAITING);
            unassignedItemId = SeedItem(db, unassigned, barcode, planned: 10).Id;

            // 배정 오더(슈트) — 더 큰 OrderId 이지만 배정 우선으로 이게 뽑혀야 함.
            var assigned = SeedOrder(db, batch, "MULTIDUP-A", destId: chute.Id, assignedAt: DateTime.UtcNow,
                status: OrderStatus.RUNNING);
            assignedItemId = SeedItem(db, assigned, barcode, planned: 10).Id;
        }

        var (result, resChute) = await PostIf05(pId: 23001, barcode: barcode, inductionNo: 1, qty: 1);
        Assert.Equal("OK", result);
        Assert.Equal(chuteNo, resChute);

        // 예약은 배정 오더 항목에만 반영 — 선택이 배정 오더였음을 증거로 확정.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            Assert.Equal(1, db.OrderItems.Single(i => i.Id == assignedItemId).ReservedQty);
            Assert.Equal(0, db.OrderItems.Single(i => i.Id == unassignedItemId).ReservedQty);

            // 활성 piece 는 배정 오더 항목에 연결(RESERVED).
            var piece = db.Pieces.Single(p => p.PId == 23001 && p.IsActive);
            Assert.Equal(assignedItemId, piece.OrderItemId);
            Assert.Equal(PieceStatus.RESERVED, piece.Status);
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    // Fix 2 — 다중 배정(같은 바코드, 두 슈트) → 배정확정 최신(DestAssignedAt) 목적지로 결정적 OK.
    //   반복 호출해도 같은 목적지(결정성).
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task If05_DuplicateBarcode_MultiAssigned_DeterministicTiebreak()
    {
        const int olderChute = 311, newerChute = 312;
        const string barcode = "TIEDUP-01";

        using (var scope = _factory.Services.CreateScope())
        {
            var db     = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            var cOld   = SeedChute(db, olderChute);
            var cNew   = SeedChute(db, newerChute);
            var batch  = SeedBatch(db, "TIEDUP-B", 1);
            var t0     = new DateTime(2026, 7, 27, 9, 0, 0, DateTimeKind.Utc);

            // 두 배정 오더 — newer 가 최신 DestAssignedAt(승자). OrderId 대소는 승자와 무관하게 tiebreak 검증.
            var orderOld = SeedOrder(db, batch, "TIEDUP-OLD", destId: cOld.Id, assignedAt: t0,
                status: OrderStatus.RUNNING);
            SeedItem(db, orderOld, barcode, planned: 10);

            var orderNew = SeedOrder(db, batch, "TIEDUP-NEW", destId: cNew.Id, assignedAt: t0.AddMinutes(5),
                status: OrderStatus.RUNNING);
            SeedItem(db, orderNew, barcode, planned: 10);
        }

        // 첫 호출 → newer 슈트. (pId 재사용 시 이전 활성 piece 비활성화 후 재예약 — 결정성 확인용 2회.)
        var (r1, c1) = await PostIf05(pId: 23011, barcode: barcode, inductionNo: 1, qty: 1);
        Assert.Equal("OK", r1);
        Assert.Equal(newerChute, c1);

        var (r2, c2) = await PostIf05(pId: 23012, barcode: barcode, inductionNo: 1, qty: 1);
        Assert.Equal("OK", r2);
        Assert.Equal(newerChute, c2);   // 결정적 — 같은 목적지.
    }

    // ════════════════════════════════════════════════════════════════════════
    // Fix 1 — 배치 상세 per-item: 1 오더:3 바코드 → 3행. 항목별 수량 + 오더 레벨 필드.
    // ════════════════════════════════════════════════════════════════════════
    [Fact]
    public async Task BatchItems_OneOrderThreeBarcodes_ReturnsThreeRows_WithOrderLevelFields()
    {
        const int sorterNo = 331;
        long batchId, orderId;

        using (var scope = _factory.Services.CreateScope())
        {
            var db     = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            var sorter = SeedSorter(db, sorterNo);
            var cell   = SeedCell(db, sorter, cellNo: 2);
            var batch  = SeedBatch(db, "PERITEM-B", 1);
            batchId = batch.Id;

            var order = SeedOrder(db, batch, "PERITEM-ORD", destId: sorter.Id, assignedAt: DateTime.UtcNow,
                status: OrderStatus.RUNNING);
            orderId = order.Id;
            SeedItem(db, order, "PI-BC-1", planned: 5, reserved: 2, sorted: 1);
            SeedItem(db, order, "PI-BC-2", planned: 7, reserved: 0, sorted: 0);
            SeedItem(db, order, "PI-BC-3", planned: 3, reserved: 3, sorted: 3);

            // 오더 레벨 활성 배정 셀 — 3행 모두에 반복돼야 함.
            db.CellAssignments.Add(new CellAssignment
            {
                CellId = cell.Id, OrderId = order.Id, AssignedAt = DateTime.UtcNow, ReleasedAt = null,
                CreatedAt = DateTime.UtcNow,
            });
            db.SaveChanges();
        }

        var items = await GetBatchItems(batchId);

        // 1 오더:3 바코드 → 정확히 3행.
        Assert.Equal(3, items.Count);
        Assert.All(items, r => Assert.Equal(orderId, r.GetProperty("orderId").GetInt64()));
        Assert.All(items, r => Assert.Equal("PERITEM-ORD", r.GetProperty("orderNo").GetString()));

        // 오더 레벨 필드 3행 반복(상태·목적지·타입·할당셀).
        Assert.All(items, r => Assert.Equal("RUNNING", r.GetProperty("status").GetString()));
        Assert.All(items, r => Assert.Equal(sorterNo, r.GetProperty("destinationChuteNo").GetInt32()));
        Assert.All(items, r => Assert.Equal("SORTER_3D", r.GetProperty("destType").GetString()));
        Assert.All(items, r => Assert.Equal(2, r.GetProperty("assignedCellNo").GetInt32()));

        // 항목별 수량(집계 아님) — 바코드별 실값.
        var bc1 = items.Single(r => r.GetProperty("barcode").GetString() == "PI-BC-1");
        Assert.Equal(5, bc1.GetProperty("plannedQty").GetInt32());
        Assert.Equal(2, bc1.GetProperty("reservedQty").GetInt32());
        Assert.Equal(1, bc1.GetProperty("sortedQty").GetInt32());

        var bc2 = items.Single(r => r.GetProperty("barcode").GetString() == "PI-BC-2");
        Assert.Equal(7, bc2.GetProperty("plannedQty").GetInt32());
        Assert.Equal(0, bc2.GetProperty("reservedQty").GetInt32());

        var bc3 = items.Single(r => r.GetProperty("barcode").GetString() == "PI-BC-3");
        Assert.Equal(3, bc3.GetProperty("plannedQty").GetInt32());
        Assert.Equal(3, bc3.GetProperty("sortedQty").GetInt32());

        // row key(orderItemId)는 3행 모두 유일.
        var itemIds = items.Select(r => r.GetProperty("orderItemId").GetInt64()).ToList();
        Assert.Equal(3, itemIds.Distinct().Count());
    }

    // ── 배치 스코프: 타 배치 항목은 미포함 + 미존재 batchId → 빈 배열 200 ──────────────
    [Fact]
    public async Task BatchItems_ScopedByBatch_AndUnknownBatchEmpty()
    {
        long batchId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db     = scope.ServiceProvider.GetRequiredService<WcsDbContext>();
            var batchA = SeedBatch(db, "SCOPE-A", 1);
            var batchB = SeedBatch(db, "SCOPE-B", 1);
            batchId = batchA.Id;

            var oa = SeedOrder(db, batchA, "SCOPE-A-ORD", destId: null, assignedAt: null, status: OrderStatus.WAITING);
            SeedItem(db, oa, "SCOPE-A-BC", planned: 1);
            var ob = SeedOrder(db, batchB, "SCOPE-B-ORD", destId: null, assignedAt: null, status: OrderStatus.WAITING);
            SeedItem(db, ob, "SCOPE-B-BC", planned: 1);
        }

        var items = await GetBatchItems(batchId);
        Assert.Single(items);
        Assert.Equal("SCOPE-A-BC", items[0].GetProperty("barcode").GetString());

        // 미존재 batchId → 200 + 빈 배열(500/400 아님).
        var res = await _client.GetAsync("/api/b2c/facility/batch-items?batchId=999999");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetArrayLength());
    }

    // ── 시드 헬퍼(DB 직접 — 결정적·격리) ────────────────────────────────────────────
    private static Destination SeedChute(WcsDbContext db, int chuteNo)
    {
        var now  = DateTime.UtcNow;
        var dest = new Destination
        {
            ChuteNo = chuteNo, DestType = DestType.CHUTE, Floor = 1,
            Status = DestStatus.NORMAL, IsActive = true, CreatedAt = now, UpdatedAt = now,
        };
        db.Destinations.Add(dest);
        db.SaveChanges();
        return dest;
    }

    private static Destination SeedSorter(WcsDbContext db, int chuteNo)
    {
        var now  = DateTime.UtcNow;
        var dest = new Destination
        {
            ChuteNo = chuteNo, DestType = DestType.SORTER_3D, Floor = null,
            Status = DestStatus.NORMAL, IsActive = true, CreatedAt = now, UpdatedAt = now,
        };
        db.Destinations.Add(dest);
        db.SaveChanges();
        return dest;
    }

    private static Cell SeedCell(WcsDbContext db, Destination sorter, int cellNo)
    {
        var cell = new Cell
        {
            DestinationId = sorter.Id, CellNo = cellNo, Capacity = 100, Enabled = true,
            CreatedAt = DateTime.UtcNow,
        };
        db.Cells.Add(cell);
        db.SaveChanges();
        return cell;
    }

    private static WorkBatch SeedBatch(WcsDbContext db, string batchNo, int waveNo)
    {
        var now   = DateTime.UtcNow;
        var batch = new WorkBatch
        {
            WorkDate = new DateOnly(2026, 7, 27), BatchNo = batchNo, WaveNo = waveNo,
            Status = WorkBatchStatus.RUNNING, CreatedAt = now, UpdatedAt = now,
        };
        db.WorkBatches.Add(batch);
        db.SaveChanges();
        return batch;
    }

    private static WcsOrder SeedOrder(WcsDbContext db, WorkBatch batch, string orderNo,
        long? destId, DateTime? assignedAt, OrderStatus status)
    {
        var now   = DateTime.UtcNow;
        var order = new WcsOrder
        {
            WorkBatchId = batch.Id, OrderNo = orderNo, OrderType = OrderType.GENERAL,
            DestinationId = destId,
            DestAssignType = destId.HasValue ? DestAssignType.MANUAL : null,
            DestAssignedAt = assignedAt,
            Status = status, StartedAt = status == OrderStatus.RUNNING ? now : null,
            CreatedAt = now, UpdatedAt = now,
        };
        db.Orders.Add(order);
        db.SaveChanges();
        return order;
    }

    private static OrderItem SeedItem(WcsDbContext db, WcsOrder order, string barcode,
        int planned, int reserved = 0, int sorted = 0)
    {
        var now  = DateTime.UtcNow;
        var item = new OrderItem
        {
            OrderId = order.Id, Barcode = barcode, PlannedQty = planned,
            ReservedQty = reserved, SortedQty = sorted, CreatedAt = now, UpdatedAt = now,
        };
        db.OrderItems.Add(item);
        db.SaveChanges();
        return item;
    }

    // ── API 헬퍼 ──────────────────────────────────────────────────────────────────
    private async Task<(string Result, int? ChuteNo)> PostIf05(int pId, string barcode, int inductionNo, int qty)
    {
        var res = await _client.PostAsJsonAsync("/api/v1/destination-query", new
        {
            pId, agvNo = 1, barcode, inductionNo, qty, timeStamp = "2026-07-27 09:00:00",
        });
        res.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var result = doc.RootElement.GetProperty("result").GetString()!;
        int? chuteNo = doc.RootElement.TryGetProperty("chuteNo", out var c) && c.ValueKind == JsonValueKind.Number
            ? c.GetInt32() : null;
        return (result, chuteNo);
    }

    private async Task<List<JsonElement>> GetBatchItems(long batchId)
    {
        var res = await _client.GetAsync($"/api/b2c/facility/batch-items?batchId={batchId}");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }
}
