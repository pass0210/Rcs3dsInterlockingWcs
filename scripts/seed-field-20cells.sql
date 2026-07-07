-- ════════════════════════════════════════════════════════════════════════════
-- seed-field-20cells.sql — 실 3DS 하드웨어 테스트용 20셀·15가용 작업 데이터 (S-FIELD-20CELLS)
--
-- 대상 DB : Rcs3dsInterlockingWcs (운영 provider = SQL Server / localhost 2025)
-- 실행 예 : sqlcmd -S localhost -d Rcs3dsInterlockingWcs -E -C -f 65001 -i scripts/seed-field-20cells.sql
--          (-f 65001: 이 파일이 UTF-8(BOM 없음)이라 한글 주석을 올바로 해석하기 위함.
--           SSMS에서 열어 실행해도 동일하게 동작한다.)
--
-- ── 이 스크립트가 만드는 상태(S-FIELD-16CELLS → S-FIELD-20CELLS 전이) ──────────
--   현장 3D Sorter는 물리 20셀(4행×5열)이지만 PLC 매핑이 15번 셀까지만 완료되어 있다.
--   셀 16~20에 작업이 배정되면 미매핑 셀로 틸트 지령이 나가는 물리 리스크가 있으므로:
--     · cell 은 물리 20행 전부 존재(모니터링 4×5=20 타일 요구).
--     · 그러나 **가용(작업 배정·선택 가능) 셀은 1~15만** — 16~20은 Enabled=0.
--   기존 FIELD-16 시드(셀 1~16·오더/배정 0701-CELL-01~16)가 이미 적용된 DB에서
--   20셀·15가용으로 **전이**한다(멱등). 코드/스키마 무변경(A안 — 기존 Enabled 게이트 재사용):
--     · EfCellSelector.SelectCell ②빈 셀 폴백 : c.Enabled 필터로 비활성 셀 제외.
--     · IF-05 HasFreeEnabledCell / ComputeSorterFull : Enabled 셀만 집계.
--     · 모니터링 GetCells : Enabled 필터 없음(20타일 반환) + DTO에 Enabled 포함(프론트 "비활성" 렌더).
--   ★ gap 보완: Enabled=0 은 ②빈 셀 폴백만 차단하고 ①활성 배정 재사용 경로는 Enabled를 보지 않는다.
--     따라서 셀 16이 **활성 cell_assignment 를 갖지 않도록** 배정 해제 + 오더 CANCELLED 로 완결 차단한다.
--
-- ── ★ 매핑 확장 경로(코드/스키마 변경 불요 — 데이터-온리) ───────────────────────
--   현장 PLC 매핑이 16~20까지 완료되면 아래 한 줄만으로 가용 상한을 15→20 으로 확장한다:
--       UPDATE cell SET Enabled = 1
--        WHERE DestinationId = (SELECT Id FROM destination WHERE ChuteNo = 1 AND DestType = 'SORTER_3D')
--          AND CellNo BETWEEN 16 AND 20;
--   (필요 시 오더/아이템/배정 16~20 추가는 아래 §4~6 의 nums 범위를 1~20 으로 넓히고,
--    §7 셀16 CANCELLED 블록을 제거하면 된다. 애플리케이션 코드·마이그레이션 변경은 없다.)
--
-- 설계 결정(계약 §0 결정적 사실 준수):
--   · 테이블명 = snake_case(destination, cell, cell_assignment, work_batch, wcs_order, order_item)
--   · 물리 컬럼명 = PascalCase(엔티티 프로퍼티명 그대로 — EF는 컬럼명을 별도 매핑하지 않음)
--   · enum 컬럼 = 문자열 저장(HasConversion<string>) → 'SORTER_3D'/'NORMAL'/'RUNNING'/'CANCELLED'/'GENERAL'/'UPSTREAM' 리터럴
--   · PK = bigint identity → Id 미지정. RowVersion(rowversion 자동)·XminRowVersion(미존재) → INSERT 생략.
--
-- 멱등성(계약 §1-f — 2회 이상 실행해도 카운트 불변):
--   전 구간 IF NOT EXISTS / NOT EXISTS / MERGE / UPDATE 보정으로 작성. 재실행해도
--   셀 20·가용(Enabled=1) 15·활성 배정 15·오더 0701-CELL-16 CANCELLED 불변.
--
-- 현장 실 데이터 무접촉(계약 ⚠ 환경 주의):
--   현장 DB에는 어제 현장 테스트 흔적(piece pId 701/1/2/908/909, 셀 3~9 SortedQty 등)이 있다.
--   이 스크립트는 **셀 Capacity/Enabled · 활성 배정 · 오더 Status** 만 보정한다.
--     · piece / sorter_command / piece_event 는 일절 건드리지 않는다(생성·수정·삭제 0).
--     · order_item 은 INSERT(NOT EXISTS)만 — ReservedQty/SortedQty 등 기존 실적은 절대 조작하지 않는다.
--
-- DbSeeder 충돌 회피(계약 §0-6 / S-OBSERVABILITY 개정):
--   · 소터 destination chuteNo=1 : 실 현장 3D Sorter 슈트번호. 이미 있으면 재사용, 없으면 생성.
--   · work_batch : (WorkDate='2026-07-01', BatchNo='FIELD-16', WaveNo=1) — DbSeeder 'SEED'와 비충돌.
--                  (배치명은 기존 현장 데이터 배치를 그대로 유지 — 리네임 시 기존 오더/실적이 고아가 됨.)
--   · cell 1~15  : UQ(DestinationId, CellNo)로 멱등. Capacity=3·Enabled=1 보정.
--     cell 16     : UPDATE Enabled 1→0(가용 상한 15로 축소).
--     cell 17~20  : INSERT Enabled=0(20타일 물리 미러링·미매핑 셀).
--
-- 생성하지 않음 : piece(IF-05 투입 시 런타임 생성).
-- ════════════════════════════════════════════════════════════════════════════

-- filtered index(부분 유니크)를 가진 테이블(cell_assignment·piece)에 INSERT/UPDATE 하려면
-- QUOTED_IDENTIFIER·ANSI_NULLS가 ON이어야 한다(SQL Server 요구). sqlcmd는 기본 OFF일 수 있어 명시.
SET QUOTED_IDENTIFIER ON;
SET ANSI_NULLS ON;
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @now         datetime2     = SYSUTCDATETIME();
DECLARE @workDate    date          = '2026-07-01';   -- 기존 현장 배치 WorkDate 유지
DECLARE @batchNo     nvarchar(100) = 'FIELD-16';     -- 기존 현장 배치명 유지(DbSeeder 'SEED'와 비충돌)
DECLARE @waveNo      int           = 1;
DECLARE @sorterChute int           = 1;              -- 3D Sorter 전용 슈트번호(실 현장값 — S-OBSERVABILITY)
DECLARE @cellCap     int           = 3;
DECLARE @plannedQty  int           = 3;
DECLARE @availMax    int           = 15;             -- 가용(Enabled=1) 셀 상한(PLC 매핑 완료분). 확장 시 20으로.
DECLARE @physMax     int           = 20;             -- 물리 셀 수(모니터링 4×5 타일).

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
-- 2) 셀 1~20 (소터 소속, Capacity=3) — 가용 게이트는 Enabled 로만 표현
--    · 1~15 : Enabled=1 (PLC 매핑 완료 — 작업 배정·선택 가능)
--    · 16~20: Enabled=0 (미매핑 — 모니터링 20타일엔 보이나 배정·선택 불가)
--    UQ(DestinationId, CellNo) 멱등. 기존 셀(1~16)은 Capacity·Enabled 보정.
--    셀 16 은 여기서 Enabled 1→0 으로 전이(MATCHED UPDATE). 17~20 은 신규 INSERT.
--    Capacity/Enabled 외 컬럼은 건드리지 않는다(현장 실 데이터 무접촉).
-- ────────────────────────────────────────────────────────────────────────────
;WITH nums AS (
    SELECT n, CASE WHEN n <= @availMax THEN 1 ELSE 0 END AS Enab
    FROM (VALUES (1),(2),(3),(4),(5),(6),(7),(8),(9),(10),
                 (11),(12),(13),(14),(15),(16),(17),(18),(19),(20)) AS v(n)
    WHERE n <= @physMax
)
MERGE cell AS tgt
USING (SELECT @sorterId AS DestinationId, n AS CellNo, Enab FROM nums) AS src
    ON tgt.DestinationId = src.DestinationId AND tgt.CellNo = src.CellNo
WHEN MATCHED AND (tgt.Capacity IS NULL OR tgt.Capacity <> @cellCap OR tgt.Enabled <> src.Enab) THEN
    UPDATE SET tgt.Capacity = @cellCap, tgt.Enabled = src.Enab
WHEN NOT MATCHED BY TARGET THEN
    INSERT (DestinationId, CellNo, Capacity, Enabled, CreatedAt)
    VALUES (src.DestinationId, src.CellNo, @cellCap, src.Enab, @now);

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
-- 4) 오더 15개 (OrderNo = 0701-CELL-01 ~ 0701-CELL-15) — 가용 셀 1~15 에 대응
--    UQ(WorkBatchId, OrderNo) 멱등. OrderType='GENERAL', DestAssignType='UPSTREAM', Status='RUNNING'.
--    오더 0701-CELL-16 은 여기서 생성하지 않는다(§7 에서 기존 것을 CANCELLED 로 전이).
-- ────────────────────────────────────────────────────────────────────────────
;WITH nums AS (
    SELECT n FROM (VALUES (1),(2),(3),(4),(5),(6),(7),(8),
                          (9),(10),(11),(12),(13),(14),(15)) AS v(n)
)
INSERT INTO wcs_order
    (WorkBatchId, OrderNo, OrderType, RefNo, RefName, DestinationId,
     DestAssignType, DestAssignedAt, Status, StartedAt, ClosedAt, CreatedAt, UpdatedAt)
SELECT
    @batchId,
    '0701-CELL-' + RIGHT('0' + CAST(n AS varchar(2)), 2),   -- 0701-CELL-01 ~ -15
    'GENERAL', NULL, NULL, @sorterId,
    'UPSTREAM', @now, 'RUNNING', @now, NULL, @now, @now
FROM nums
WHERE NOT EXISTS (
    SELECT 1 FROM wcs_order o
    WHERE o.WorkBatchId = @batchId
      AND o.OrderNo = '0701-CELL-' + RIGHT('0' + CAST(n AS varchar(2)), 2));

-- ────────────────────────────────────────────────────────────────────────────
-- 5) order_item 15개 (오더 N ↔ Barcode 0701-CELL-N, PlannedQty=3, ReservedQty=0, SortedQty=0)
--    UQ(OrderId, Barcode) 멱등. **INSERT(NOT EXISTS)만** — 기존 실적(ReservedQty/SortedQty) 무조작.
--    (오더 0701-CELL-16 의 order_item 은 이력 보존을 위해 건드리지 않는다 — §7 참조.)
-- ────────────────────────────────────────────────────────────────────────────
INSERT INTO order_item
    (OrderId, Barcode, PlannedQty, ReservedQty, SortedQty, CreatedAt, UpdatedAt)
SELECT
    o.Id, o.OrderNo, @plannedQty, 0, 0, @now, @now       -- Barcode = OrderNo(= 0701-CELL-NN)
FROM wcs_order o
WHERE o.WorkBatchId = @batchId
  AND o.OrderNo LIKE '0701-CELL-[0-1][0-9]'
  AND CAST(RIGHT(o.OrderNo, 2) AS int) BETWEEN 1 AND @availMax
  AND NOT EXISTS (
        SELECT 1 FROM order_item oi
        WHERE oi.OrderId = o.Id AND oi.Barcode = o.OrderNo);

-- ────────────────────────────────────────────────────────────────────────────
-- 6) cell_assignment 15건 — 결정적 N↔N (CellNo=N ↔ OrderNo=0701-CELL-N, N=1~15)
--    AssignedAt=현재, ReleasedAt=NULL(점유 중). 부분유니크 (CellId) WHERE ReleasedAt IS NULL 준수.
--    멱등: 같은 셀에 활성(ReleasedAt IS NULL) 배정이 이미 있으면 INSERT 안 함.
--    셀 16 은 배정 대상에서 제외(BETWEEN 1 AND @availMax) — §7 에서 기존 활성 배정을 해제한다.
-- ────────────────────────────────────────────────────────────────────────────
INSERT INTO cell_assignment (CellId, OrderId, AssignedAt, ReleasedAt, CreatedAt)
SELECT c.Id, o.Id, @now, NULL, @now
FROM cell c
JOIN wcs_order o
    ON o.WorkBatchId = @batchId
   AND o.OrderNo = '0701-CELL-' + RIGHT('0' + CAST(c.CellNo AS varchar(2)), 2)
WHERE c.DestinationId = @sorterId
  AND c.CellNo BETWEEN 1 AND @availMax
  AND NOT EXISTS (
        SELECT 1 FROM cell_assignment ca
        WHERE ca.CellId = c.Id AND ca.ReleasedAt IS NULL);

-- ────────────────────────────────────────────────────────────────────────────
-- 7) 셀 16 전이 처리 (0701-CELL-16) — 미매핑 셀로의 배정을 완결 차단(gap 보완)
--    a. 셀 16 의 활성 cell_assignment 를 해제(ReleasedAt=now). 멱등(이미 해제면 0행).
--       → ①활성 배정 재사용 경로(Enabled 무시)가 셀 16 을 고르지 못하게 한다.
--    b. 오더 0701-CELL-16 을 Status='CANCELLED' 로 전이. 멱등(이미 CANCELLED면 0행).
--       → barcode 0701-CELL-16 이 유효 오더를 갖지 않아 IF-05 가 1~15 점유와 무관하게 결정적 NG.
--         (QueryDestination 이 Status IN (COMPLETED, CANCELLED) 오더를 제외 → NO_DEST NG.)
--       → order_item 행은 보존(append-only 이력). 예약/실적(ReservedQty/SortedQty) 무조작.
-- ────────────────────────────────────────────────────────────────────────────
DECLARE @cell16Id bigint =
    (SELECT Id FROM cell WHERE DestinationId = @sorterId AND CellNo = 16);

-- a. 셀 16 활성 배정 해제
UPDATE cell_assignment
   SET ReleasedAt = @now
 WHERE ReleasedAt IS NULL
   AND CellId = @cell16Id;

-- b. 오더 0701-CELL-16 CANCELLED 전이
UPDATE wcs_order
   SET Status    = 'CANCELLED',
       ClosedAt  = COALESCE(ClosedAt, @now),
       UpdatedAt = @now
 WHERE WorkBatchId = @batchId
   AND OrderNo     = '0701-CELL-16'
   AND Status      <> 'CANCELLED';

COMMIT TRANSACTION;

-- ────────────────────────────────────────────────────────────────────────────
-- 적재 요약 출력(검증 편의) — 20셀·가용15·활성배정15·오더16 CANCELLED 단정용
-- ────────────────────────────────────────────────────────────────────────────
SELECT
    (SELECT COUNT(*) FROM cell WHERE DestinationId = @sorterId
        AND CellNo BETWEEN 1 AND @physMax)                                       AS cells_total_20,
    (SELECT COUNT(*) FROM cell WHERE DestinationId = @sorterId
        AND Enabled = 1 AND Capacity = @cellCap)                                 AS cells_enabled_15,
    (SELECT COUNT(*) FROM cell WHERE DestinationId = @sorterId
        AND Enabled = 0)                                                         AS cells_disabled_5,
    (SELECT COUNT(*) FROM cell_assignment ca
        JOIN cell c      ON ca.CellId  = c.Id AND c.DestinationId = @sorterId
        JOIN wcs_order o ON ca.OrderId = o.Id AND o.WorkBatchId   = @batchId
        WHERE ca.ReleasedAt IS NULL
          AND o.OrderNo = '0701-CELL-' + RIGHT('0' + CAST(c.CellNo AS varchar(2)), 2)) AS active_assignments_15,
    (SELECT COUNT(*) FROM wcs_order
        WHERE WorkBatchId = @batchId AND OrderNo = '0701-CELL-16' AND Status = 'CANCELLED') AS order16_cancelled_1;
