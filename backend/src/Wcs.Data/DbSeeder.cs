using Microsoft.EntityFrameworkCore;

namespace Wcs.Data;

// ════════════════════════════════════════════════════════════════════════════
// DbSeeder — 기준정보 + M3 테스트가 의존하는 최소 오더 시드.
// M3 인메모리 시드(Program.cs)와 데이터 동등 — 회귀 0의 토대.
//
// appsettings Floors:AgvNoToFloor는 시드 전용으로 강등 — 런타임 DB 조회가 진실.
// 시드는 멱등(재시작해도 중복 삽입 없음).
// ════════════════════════════════════════════════════════════════════════════

public static class DbSeeder
{
    /// <summary>
    /// 기준정보 + 최소 오더 시드.
    /// db.Database.Migrate() 후 호출.
    /// appsettings의 Floors:AgvNoToFloor를 받아 agv 시드에 사용 (시드 전용, 런타임 조회 X).
    /// </summary>
    public static void Seed(WcsDbContext db, IReadOnlyDictionary<string, int>? agvFloorMap = null)
    {
        SeedDestinations(db);
        SeedChuteDetails(db);
        SeedCells(db);
        SeedAgvs(db, agvFloorMap);
        SeedInductions(db);
        SeedPrinters(db);
        SeedWorkBatchAndOrders(db);
        db.SaveChanges();
    }

    // ── 목적지 (슈트 1~5 + 3D Sorter chuteNo=30) ───────────────────────────
    private static void SeedDestinations(WcsDbContext db)
    {
        var now = DateTime.UtcNow;
        var existing = db.Destinations.Select(d => d.ChuteNo).ToHashSet();

        // 슈트 1~5 (CHUTE)
        for (int chuteNo = 1; chuteNo <= 5; chuteNo++)
        {
            if (existing.Contains(chuteNo)) continue;
            db.Destinations.Add(new Destination
            {
                ChuteNo   = chuteNo,
                DestType  = DestType.CHUTE,
                Floor     = null,
                Status    = DestStatus.NORMAL,
                IsActive  = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        // 3D Sorter — SORTER_3D 목적지는 전용 chute_no=30으로 등록(CHUTE 1~5와 겹치지 않는 별도 번호).
        // M3 시드 오더 TEST-BARCODE-3 → ORD-003 → ChuteNo=30 SORTER_3D (라인 185와 일치).
        // dev 콜드스타트 SorterRegistry 매칭을 위해 appsettings.Development.json Sorters[]는 ChuteNo=30(E-⑤).
        if (!existing.Contains(30))
        {
            db.Destinations.Add(new Destination
            {
                ChuteNo   = 30,           // 3D Sorter 전용 슈트번호
                DestType  = DestType.SORTER_3D,
                Floor     = null,
                Status    = DestStatus.NORMAL,
                IsActive  = true,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        db.SaveChanges(); // destination Id 확정 후 사용
    }

    // ── 슈트 상세 (CHUTE 전용) ───────────────────────────────────────────────
    private static void SeedChuteDetails(WcsDbContext db)
    {
        var now      = DateTime.UtcNow;
        var chuteNos = Enumerable.Range(1, 5).ToArray();

        foreach (var chuteNo in chuteNos)
        {
            var dest = db.Destinations.FirstOrDefault(d => d.ChuteNo == chuteNo && d.DestType == DestType.CHUTE);
            if (dest is null) continue;
            if (db.ChuteDetails.Any(c => c.DestinationId == dest.Id)) continue;

            db.ChuteDetails.Add(new ChuteDetail
            {
                DestinationId  = dest.Id,
                DefaultFullQty = 100,
                WorkFullQty    = 100,
                PrinterId      = null,
                LastClearedAt  = null,
                Zone           = null,
                CreatedAt      = now,
                UpdatedAt      = now,
            });
        }

        db.SaveChanges();
    }

    // ── 3D Sorter 셀 (소터 목적지 소속) ─────────────────────────────────────
    private static void SeedCells(WcsDbContext db)
    {
        var now       = DateTime.UtcNow;
        var sorterDest = db.Destinations.FirstOrDefault(d => d.DestType == DestType.SORTER_3D && d.IsActive);
        if (sorterDest is null) return;

        // M3 시드: CellNo 1~3, SorterChuteNo=3
        for (int cellNo = 1; cellNo <= 3; cellNo++)
        {
            if (db.Cells.Any(c => c.DestinationId == sorterDest.Id && c.CellNo == cellNo)) continue;
            db.Cells.Add(new Cell
            {
                DestinationId = sorterDest.Id,
                CellNo        = cellNo,
                Capacity      = null,
                Enabled       = true,
                CreatedAt     = now,
            });
        }

        db.SaveChanges();
    }

    // ── AGV — appsettings Floors:AgvNoToFloor 기준 (시드 전용) ─────────────
    private static void SeedAgvs(WcsDbContext db, IReadOnlyDictionary<string, int>? agvFloorMap)
    {
        var now = DateTime.UtcNow;
        // 기본 매핑: agvNo=1→floor=1, agvNo=2→floor=2 (M3 appsettings 동등)
        var map = agvFloorMap ?? new Dictionary<string, int> { ["1"] = 1, ["2"] = 2 };

        foreach (var (key, floor) in map)
        {
            if (!int.TryParse(key, out int agvNo)) continue;
            if (db.Agvs.Any(a => a.AgvNo == agvNo)) continue;

            db.Agvs.Add(new Agv
            {
                AgvNo     = agvNo,
                Floor     = floor,
                Enabled   = true,
                CreatedAt = now,
            });
        }

        db.SaveChanges();
    }

    // ── 인덕션 ─────────────────────────────────────────────────────────────
    private static void SeedInductions(WcsDbContext db)
    {
        var now = DateTime.UtcNow;
        if (db.Inductions.Any()) return;

        // 층별 인덕션 각 1개 (기본)
        db.Inductions.AddRange(
            new Induction { InductionNo = 1, Floor = 1, Enabled = true, CreatedAt = now },
            new Induction { InductionNo = 2, Floor = 2, Enabled = true, CreatedAt = now }
        );
        db.SaveChanges();
    }

    // ── 프린터 ─────────────────────────────────────────────────────────────
    private static void SeedPrinters(WcsDbContext db)
    {
        var now = DateTime.UtcNow;
        if (db.Printers.Any()) return;

        db.Printers.Add(new Printer
        {
            PrinterNo = 1,
            Name      = "DEFAULT",
            ConnInfo  = null,
            Enabled   = true,
            CreatedAt = now,
        });
        db.SaveChanges();
    }

    // ── WorkBatch + WcsOrder + OrderItem (M3 테스트 의존 최소 오더) ─────────
    //
    // M3 인메모리 시드 오더와 데이터 동등:
    //   TEST-BARCODE-1 → ORD-001 → ChuteNo=1  (CHUTE)
    //   TEST-BARCODE-2 → ORD-002 → ChuteNo=2  (CHUTE)
    //   TEST-BARCODE-3 → ORD-003 → ChuteNo=30 (SORTER_3D, chute_no=30)
    //   TEST-BARCODE-AUTO → ORD-004 → destination NULL (AUTO 배정)
    //   TEST-BARCODE-PAUSED → ORD-005 → ChuteNo=2, destination PAUSED
    private static void SeedWorkBatchAndOrders(WcsDbContext db)
    {
        var now   = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // 배치 조회 또는 생성
        var batch = db.WorkBatches.FirstOrDefault(b =>
            b.WorkDate == today && b.BatchNo == "SEED" && b.WaveNo == 1);

        if (batch is null)
        {
            batch = new WorkBatch
            {
                WorkDate  = today,
                BatchNo   = "SEED",
                WaveNo    = 1,
                Status    = WorkBatchStatus.RUNNING,
                OpenedAt  = now,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.WorkBatches.Add(batch);
            db.SaveChanges(); // Id 확정
        }

        // 목적지 조회
        var dest1    = db.Destinations.First(d => d.ChuteNo == 1 && d.DestType == DestType.CHUTE);
        var dest2    = db.Destinations.First(d => d.ChuteNo == 2 && d.DestType == DestType.CHUTE);
        var dest3    = db.Destinations.First(d => d.DestType == DestType.SORTER_3D);

        // PAUSED 목적지: dest2 (슈트 2) → Status=PAUSED (PAUSED 오더용)
        // 주의: dest2를 PAUSED로 설정하면 ORD-002(TEST-BARCODE-2)도 영향. PAUSED 전용 목적지 필요.
        // M3 시드에서 TEST-BARCODE-PAUSED → IsPaused=true (오더 레벨). 여기서는 목적지 PAUSED로 표현.
        // ORD-005는 별도 슈트(ChuteNo=2)에 배정하되 destination.status=PAUSED로 설정.
        // 그러나 dest2를 PAUSED로 하면 ORD-002도 PAUSED가 됨 → 별도 PAUSED 목적지(ChuteNo=6) 생성.
        var destPaused = db.Destinations.FirstOrDefault(d => d.ChuteNo == 6);
        if (destPaused is null)
        {
            destPaused = new Destination
            {
                ChuteNo   = 6,
                DestType  = DestType.CHUTE,
                Floor     = null,
                Status    = DestStatus.PAUSED,  // PAUSED
                IsActive  = true,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Destinations.Add(destPaused);
            db.SaveChanges();

            // ChuteDetail도 생성
            db.ChuteDetails.Add(new ChuteDetail
            {
                DestinationId  = destPaused.Id,
                DefaultFullQty = 100,
                WorkFullQty    = 100,
                CreatedAt      = now,
                UpdatedAt      = now,
            });
            db.SaveChanges();
        }

        // 오더 생성 (이미 있으면 스킵 — 멱등)
        void EnsureOrder(string orderNo, long? destId, string barcode, int plannedQty)
        {
            if (db.Orders.Any(o => o.WorkBatchId == batch!.Id && o.OrderNo == orderNo)) return;

            var order = new WcsOrder
            {
                WorkBatchId    = batch!.Id,
                OrderNo        = orderNo,
                OrderType      = OrderType.GENERAL,
                DestinationId  = destId,
                DestAssignType = destId.HasValue ? DestAssignType.UPSTREAM : null,
                DestAssignedAt = destId.HasValue ? now : null,
                Status         = OrderStatus.RUNNING,
                StartedAt      = now,
                CreatedAt      = now,
                UpdatedAt      = now,
            };
            db.Orders.Add(order);
            db.SaveChanges();

            db.OrderItems.Add(new OrderItem
            {
                OrderId     = order.Id,
                Barcode     = barcode,
                PlannedQty  = plannedQty,
                ReservedQty = 0,
                SortedQty   = 0,
                CreatedAt   = now,
                UpdatedAt   = now,
            });
            db.SaveChanges();
        }

        EnsureOrder("ORD-001", dest1.Id,       "TEST-BARCODE-1",      50);
        EnsureOrder("ORD-002", dest2.Id,       "TEST-BARCODE-2",      30);
        EnsureOrder("ORD-003", dest3.Id,       "TEST-BARCODE-3",      20);
        EnsureOrder("ORD-004", null,           "TEST-BARCODE-AUTO",   10); // AUTO 배정
        EnsureOrder("ORD-005", destPaused.Id,  "TEST-BARCODE-PAUSED", 10); // PAUSED 목적지
    }
}
