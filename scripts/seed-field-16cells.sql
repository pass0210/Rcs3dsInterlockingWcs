-- ════════════════════════════════════════════════════════════════════════════
-- seed-field-16cells.sql — 실 3DS 하드웨어 테스트용 16셀 작업 데이터 적재 (S-FIELD-SEED-16CELLS)
--
-- 대상 DB : Rcs3dsInterlockingWcs (운영 provider = SQL Server / localhost 2025)
-- 실행 예 : sqlcmd -S localhost -d Rcs3dsInterlockingWcs -E -C -f 65001 -i scripts/seed-field-16cells.sql
--          (-f 65001: 이 파일이 UTF-8(BOM 없음)이라 한글 주석을 올바로 해석하기 위함.
--           SSMS에서 열어 실행해도 동일하게 동작한다.)
--
-- 설계 결정(계약 §0 결정적 사실 준수):
--   · 테이블명 = snake_case(destination, cell, cell_assignment, work_batch, wcs_order, order_item)
--   · 물리 컬럼명 = PascalCase(엔티티 프로퍼티명 그대로 — EF는 컬럼명을 별도 매핑하지 않음)
--   · enum 컬럼 = 문자열 저장(HasConversion<string>) → 'SORTER_3D'/'NORMAL'/'RUNNING'/'GENERAL'/'UPSTREAM' 리터럴
--   · PK = bigint identity → Id 미지정. RowVersion(rowversion 자동)·XminRowVersion(미존재) → INSERT 생략.
--
-- 멱등성(계약 C5 — 2회 이상 실행해도 중복/오류 0):
--   전 구간 IF NOT EXISTS / NOT EXISTS 가드 또는 UPDATE 보정으로 작성. 재실행해도
--   셀 16·오더 16·order_item 16·cell_assignment 16 불변.
--
-- DbSeeder 충돌 회피(계약 §0-6 / S-OBSERVABILITY 개정):
--   · 소터 destination chuteNo=1 : 실 현장 3D Sorter 슈트번호. 이미 있으면 재사용, 없으면 생성.
--     (실 현장 DB Rcs3dsInterlockingWcs는 SeedOnStartup=false라 DbSeeder가 안 돌아 CHUTE 1~5 시드 부재 →
--      chuteNo=1 전역 유니크(UQ_destination_chute_no) 충돌 없음. dev DbSeeder는 소터 chuteNo=30 유지 — 형상 차이 의도적.)
--   · work_batch : DbSeeder는 (WorkDate=today, BatchNo='SEED', WaveNo=1). 본 스크립트는
--                  (WorkDate='2026-07-01', BatchNo='FIELD-16', WaveNo=1) — UQ(WorkDate,BatchNo,WaveNo) 비충돌.
--   · cell 1~16  : UQ(DestinationId, CellNo)로 멱등. DbSeeder가 만든 cell 1~3(Capacity=NULL)이 있어도
--                  본 스크립트가 Capacity=3·Enabled=1로 보정(UPDATE)해 16셀이 명세대로 보장.
--
-- 생성하지 않음 : piece(IF-05 투입 시 런타임 생성).
-- ════════════════════════════════════════════════════════════════════════════

-- filtered index(부분 유니크)를 가진 테이블(cell_assignment·piece)에 INSERT하려면
-- QUOTED_IDENTIFIER·ANSI_NULLS가 ON이어야 한다(SQL Server 요구). sqlcmd는 기본 OFF일 수 있어 명시.
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @now        datetime2 = SYSUTCDATETIME();
DECLARE @workDate   date      = '2026-07-01';   -- 계약 §4: 내일
DECLARE @batchNo    nvarchar(100) = 'FIELD-16'; -- DbSeeder 'SEED'와 비충돌
DECLARE @waveNo     int       = 1;
DECLARE @sorterChute int      = 1;              -- 3D Sorter 전용 슈트번호(실 현장값 — S-OBSERVABILITY)
DECLARE @cellCap    int       = 3;
DECLARE @plannedQty int       = 3;

BEGIN TRANSACTION;

-- ────────────────────────────────────────────────────────────────────────────
-- 1) 소터 destination (SORTER_3D, ChuteNo=1) — 있으면 재사용, 없으면 생성
-- ────────────────────────────────────────────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM destination WHERE ChuteNo = @sorterChute)
BEGIN
    INSERT INTO destination (ChuteNo, DestType, Floor, Status, IsActive, CreatedAt, UpdatedAt)
    VALUES (@sorterChute, 'SORTER_3D', NULL, 'NORMAL', 1, @now, @now);
END

DECLARE @sorterId bigint =
    (SELECT TOP 1 Id FROM destination WHERE ChuteNo = @sorterChute AND DestType = 'SORTER_3D');

IF @sorterId IS NULL
BEGIN
    -- chuteNo=1이 SORTER_3D가 아닌 다른 타입(예: CHUTE)으로 점유된 비정상 상태 — fail-loud.
    THROW 50001, 'destination ChuteNo=1 이 SORTER_3D 타입이 아닙니다. 데이터 상태를 확인하세요.', 1;
END

-- ────────────────────────────────────────────────────────────────────────────
-- 2) 셀 1~16 (소터 소속, Capacity=3, Enabled=1)
--    UQ(DestinationId, CellNo) 멱등. 기존 셀(DbSeeder cell 1~3 등)은 Capacity·Enabled 보정.
-- ────────────────────────────────────────────────────────────────────────────
;WITH nums AS (
    SELECT n FROM (VALUES (1),(2),(3),(4),(5),(6),(7),(8),
                          (9),(10),(11),(12),(13),(14),(15),(16)) AS v(n)
)
MERGE cell AS tgt
USING (SELECT @sorterId AS DestinationId, n AS CellNo FROM nums) AS src
    ON tgt.DestinationId = src.DestinationId AND tgt.CellNo = src.CellNo
WHEN MATCHED AND (tgt.Capacity IS NULL OR tgt.Capacity <> @cellCap OR tgt.Enabled <> 1) THEN
    UPDATE SET tgt.Capacity = @cellCap, tgt.Enabled = 1
WHEN NOT MATCHED BY TARGET THEN
    INSERT (DestinationId, CellNo, Capacity, Enabled, CreatedAt)
    VALUES (src.DestinationId, src.CellNo, @cellCap, 1, @now);

-- ────────────────────────────────────────────────────────────────────────────
-- 3) work_batch (WorkDate='2026-07-01', BatchNo='FIELD-16', WaveNo=1, Status='RUNNING')
-- ────────────────────────────────────────────────────────────────────────────
IF NOT EXISTS (SELECT 1 FROM work_batch
               WHERE WorkDate = @workDate AND BatchNo = @batchNo AND WaveNo = @waveNo)
BEGIN
    INSERT INTO work_batch (WorkDate, BatchNo, WaveNo, Status, OpenedAt, ClosedAt, CreatedAt, UpdatedAt)
    VALUES (@workDate, @batchNo, @waveNo, 'RUNNING', @now, NULL, @now, @now);
END

DECLARE @batchId bigint =
    (SELECT TOP 1 Id FROM work_batch
     WHERE WorkDate = @workDate AND BatchNo = @batchNo AND WaveNo = @waveNo);

-- ────────────────────────────────────────────────────────────────────────────
-- 4) 오더 16개 (OrderNo = 0701-CELL-01 ~ 0701-CELL-16)
--    UQ(WorkBatchId, OrderNo) 멱등. OrderType='GENERAL', DestAssignType='UPSTREAM', Status='RUNNING'.
-- ────────────────────────────────────────────────────────────────────────────
;WITH nums AS (
    SELECT n FROM (VALUES (1),(2),(3),(4),(5),(6),(7),(8),
                          (9),(10),(11),(12),(13),(14),(15),(16)) AS v(n)
)
INSERT INTO wcs_order
    (WorkBatchId, OrderNo, OrderType, RefNo, RefName, DestinationId,
     DestAssignType, DestAssignedAt, Status, StartedAt, ClosedAt, CreatedAt, UpdatedAt)
SELECT
    @batchId,
    '0701-CELL-' + RIGHT('0' + CAST(n AS varchar(2)), 2),   -- 0701-CELL-01 ~ -16
    'GENERAL', NULL, NULL, @sorterId,
    'UPSTREAM', @now, 'RUNNING', @now, NULL, @now, @now
FROM nums
WHERE NOT EXISTS (
    SELECT 1 FROM wcs_order o
    WHERE o.WorkBatchId = @batchId
      AND o.OrderNo = '0701-CELL-' + RIGHT('0' + CAST(n AS varchar(2)), 2));

-- ────────────────────────────────────────────────────────────────────────────
-- 5) order_item 16개 (오더 N ↔ Barcode 0701-CELL-N, PlannedQty=3, ReservedQty=0, SortedQty=0)
--    UQ(OrderId, Barcode) 멱등.
-- ────────────────────────────────────────────────────────────────────────────
INSERT INTO order_item
    (OrderId, Barcode, PlannedQty, ReservedQty, SortedQty, CreatedAt, UpdatedAt)
SELECT
    o.Id, o.OrderNo, @plannedQty, 0, 0, @now, @now       -- Barcode = OrderNo(= 0701-CELL-NN)
FROM wcs_order o
WHERE o.WorkBatchId = @batchId
  AND o.OrderNo LIKE '0701-CELL-[0-9][0-9]'
  AND NOT EXISTS (
        SELECT 1 FROM order_item oi
        WHERE oi.OrderId = o.Id AND oi.Barcode = o.OrderNo);

-- ────────────────────────────────────────────────────────────────────────────
-- 6) cell_assignment 16건 — 결정적 N↔N (CellNo=N ↔ OrderNo=0701-CELL-N)
--    AssignedAt=현재, ReleasedAt=NULL(점유 중). 부분유니크 (CellId) WHERE ReleasedAt IS NULL 준수.
--    멱등: 같은 (CellId, OrderId)에 활성(ReleasedAt IS NULL) 배정이 이미 있으면 INSERT 안 함.
-- ────────────────────────────────────────────────────────────────────────────
INSERT INTO cell_assignment (CellId, OrderId, AssignedAt, ReleasedAt, CreatedAt)
SELECT c.Id, o.Id, @now, NULL, @now
FROM cell c
JOIN wcs_order o
    ON o.WorkBatchId = @batchId
   AND o.OrderNo = '0701-CELL-' + RIGHT('0' + CAST(c.CellNo AS varchar(2)), 2)
WHERE c.DestinationId = @sorterId
  AND c.CellNo BETWEEN 1 AND 16
  AND NOT EXISTS (
        SELECT 1 FROM cell_assignment ca
        WHERE ca.CellId = c.Id AND ca.ReleasedAt IS NULL);

COMMIT TRANSACTION;

-- ────────────────────────────────────────────────────────────────────────────
-- 적재 요약 출력(검증 편의)
-- ────────────────────────────────────────────────────────────────────────────
SELECT
    (SELECT COUNT(*) FROM cell WHERE DestinationId = @sorterId AND CellNo BETWEEN 1 AND 16
        AND Capacity = @cellCap AND Enabled = 1)                                   AS cells_16,
    (SELECT COUNT(*) FROM order_item oi JOIN wcs_order o ON oi.OrderId = o.Id
        WHERE o.WorkBatchId = @batchId AND oi.Barcode LIKE '0701-CELL-[0-9][0-9]'
          AND oi.PlannedQty = @plannedQty)                                         AS items_16,
    (SELECT COUNT(*) FROM cell_assignment ca
        JOIN cell c     ON ca.CellId = c.Id  AND c.DestinationId = @sorterId
        JOIN wcs_order o ON ca.OrderId = o.Id AND o.WorkBatchId = @batchId
        WHERE ca.ReleasedAt IS NULL
          AND o.OrderNo = '0701-CELL-' + RIGHT('0' + CAST(c.CellNo AS varchar(2)), 2)) AS assignments_NtoN_16;
