# B2C-DATAGEN.md — B2C(3D 소터) 데이터 생성·초기화 계약 캡처 (Generator/Evaluator 단일 근거)

> 작성: Generator (S-B2C-DATAGEN) · 2026-07-13. **개정: S-B2C-FACILITY · 2026-07-14 — 생성 폼 슬림화(2a).**
> 근거: `tasks/sprint-contract.md`(게이트 확정) · `docs/ERD.md`(17테이블·이력 불변) · `docs/B2C-FACILITY.md`(설비 관리 2b).

---

## 0. 범위 요약

- **목적**: 운영자가 브라우저에서 **B2C 3D 소터 테스트용 오더/바코드를 생성**하고, 별도의 **설비 관리** 페이지에서 목적지·셀을
  구성하고 오더를 배정한 뒤, 재테스트를 위해 **초기화**할 수 있는 백엔드 관리 API + React 페이지를 제공한다.
- **★ S-B2C-FACILITY 책임 분리(2a/2b)**:
  - **데이터 생성(2a · 이 문서)**: 생성 폼은 **오더/바코드만** 만든다(목적지 **미할당**). 파라미터 5개 — 작업일자·배치명·차수·계획수량·바코드 접두.
    소터/셀/cell_assignment 자동 생성·N↔N 배정은 **제거**(설비 관리 2b 로 이관).
  - **설비 관리(2b · `docs/B2C-FACILITY.md`)**: 목적지(소터/슈트) 생성·활성화, 소터 셀 설정, 오더→목적지(+셀) 할당, 슈트 제어(clear/pause/resume).
- **포함(이 문서·2a)**: 생성(`generate` 슬림) · 최근 배치(`batches`) · 요약(`summary`) · 셀 상세(`detail`) · 초기화(`reset`) API + 데이터 생성 페이지.
  ★ **S-B2C-UX**: 초기화(`reset`)는 **데이터 생성 페이지 소속**으로 이관되고 스코프가 **소터 → 배치**로 바뀐다. 데이터 생성 페이지는
  (a) 5-필드 생성 폼 + (b) 생성 결과 **마스터-디테일 그리드**(행 체크박스 다건 초기화 + 상단 초기화 버튼 + 하단 배치 오더 상세)를 제공.
  `summary`/`detail` 은 백엔드 무변경. 하단 디테일 소스 = `GET /api/b2c/facility/orders?batchId=`(F2 재사용).
- **핵심 결정(OQ1=B)**: 초기화는 `piece`/`piece_event`/`sorter_command` 를 **하드삭제하지 않고 `archived_at` 소프트삭제**(B2B 정책 일관).
  → 모든 활성 조회 경로가 archived 행을 제외해야 함(§3 — HIGHEST-STAKES). **reset 시맨틱은 스코프(소터→배치)만 바뀌고 나머지는 불변.**

---

## 1. 관리 API 계약 (프론트 전용 — RCS 계약 아님)

라우트 접두 `/api/b2c/test-data`(OQ7). 기존 `/api/test-data`(B2B)·`/api/v1`(RCS)·`/api/monitor`·`/api/ops` 무충돌.
관리 액션 응답 = `{ status:"S"|"F", message, counts }`(`B2cManagementResponse`). 조회 = 원시 JSON(camelCase).
**성공 판정 = `res.ok && body.status==="S"`**(200 F 오인 금지 — B2B-DATAGEN §7.1 함정).

| 메서드 | 경로 | 용도 | 요청 | 응답 |
|---|---|---|---|---|
| POST | `/api/b2c/test-data/generate` | 멱등 생성(슬림 — 미할당 오더 N) | `B2cGenerateRequest`(body) | `B2cManagementResponse` |
| POST | `/api/b2c/test-data/upload` | 엑셀 업로드(6열 · 1 오더:N 바코드 · S-B2C-DATAGEN-UPLOAD) | multipart `IFormFile file`(.xlsx) | `B2cUploadResponse` |
| GET  | `/api/b2c/test-data/batches?take=` | 최근 배치 요약(생성 결과 view) | query `take?` | `B2cBatchSummary[]` |
| GET  | `/api/b2c/test-data/summary?sorterChuteNo=` | 소터별 요약 집계(선택 필터) | query `sorterChuteNo?` | `B2cSorterSummary[]` |
| GET  | `/api/b2c/test-data/detail?sorterChuteNo=` | 셀 상세(그리드) | query `sorterChuteNo`(필수) | `B2cCellDetail[]` |
| POST | `/api/b2c/test-data/reset` | 재테스트 초기화(**배치 스코프** · S-B2C-UX) | `B2cResetRequest`(body) | `B2cManagementResponse` |

### 1.1 `B2cGenerateRequest` (슬림 5-파라미터 · DataAnnotations 검증 — 실패 시 400 + `{status:"F"}`)

| 필드 | 타입 | 검증 | 기본 | 의미 |
|---|---|---|---|---|
| workDate | string | `[Required]`, `^\d{8}$\|^\d{4}-\d{2}-\d{2}$` | — | 작업일자 |
| batchNo | string | `[Required]`, `StringLength(100,Min=1)` | — | 배치명 |
| waveNo | int | `[Range(1,9999)]` | 1 | 차수 |
| plannedQty | int | `[Range(1,1000)]` | — | **생성할 오더/바코드 개수 N**(OQ-4 · 각 order_item.planned_qty=1 고정) |
| barcodePrefix | string | `[Required]`, `^[A-Za-z0-9_\-]{1,50}$` | — | 바코드/오더번호 접두 |

- **제거된 필드(2a 슬림)**: `sorterChuteNo`·`cellCount`·`cellCapacity`·`orderPrefix`(→ `barcodePrefix` 로 개명). 목적지·셀은 설비 관리 소관.
- 검증 400 형식화: `InvalidModelStateResponseFactory` allowlist 에 `/api/b2c/test-data`·`/api/b2c/facility` 추가(additive — 기존 계약 불변).
- 비존재 날짜(형식통과·달력 무효, 예 `2026-02-30`)는 `AppUtils.NormalizeBizDay` 가 `ArgumentException` → 컨트롤러 국소 catch → 400 `{status:"F"}`.
- 상한 상수는 `B2cConstants`(하드코딩 금지 — 절대규칙 #7): `GenerateCountMax=1000`.

### 1.2 `B2cResetRequest` (★ S-B2C-UX — 배치 스코프로 재정의, 소터 스코프 폐지)

| 필드 | 타입 | 의미 |
|---|---|---|
| batchId | long `[Range(1,long.Max)]` | 초기화 대상 배치 대리키(work_batch.id — 배치에 속한 오더 전체가 대상) |
| force | bool | in-flight 존재 시 강제(OQ3 — 기본 false 거부·true 만 진행) |
| operatorName | string? | 작업자 이름(감사 귀속 · OQ-3 — operation_log detail 기록. 공백은 "(unspecified)") |

> ★ 소터 스코프(`sorterChuteNo`)는 **폐지**. "초기화 = 생성한 배치를 되돌린다"는 도메인 판단으로 스코프를
> 배치로 옮겼다. 다건 초기화는 프론트가 체크된 batchId 별 **순차 호출 + 집계 토스트**(force 체이닝)로 표현한다.

---

## 2. 생성 알고리즘 (슬림 · 멱등 upsert — 미할당 오더만)

**순수 함수** `B2cTestDataService.BuildOrderNumbers(count, prefix)` 가 결정적 오더번호 목록을 산출
(I/O 무의존·테스트 가능 — 절대규칙 #8 정신). 이후 upsert 로 적용.

- **결정적 오더번호**: n = 1..N(=plannedQty). `orderNo == barcode == "{prefix}-{NN}"`(zero-pad 폭 = max(2, N 자릿수) — 예 `0714-A-01`).
- **멱등 upsert**(같은 파라미터 재실행 → 신규 카운트 0):
  1. work_batch(workDate, batchNo, waveNo) — 없으면 RUNNING 생성.
  2. wcs_order(batchId, orderNo) — 없으면 **RUNNING·GENERAL·destination 미할당(DestinationId=null·DestAssignType=null)** 생성.
  3. order_item(orderId, barcode) — 없으면 **planned_qty=1**(OQ-4)·reserved/sorted=0 **INSERT 만**(기존 reserved/sorted **보존** — 재생성이 실적 클로버 금지).
- **소터/셀/cell_assignment 생성 없음**(S-B2C-FACILITY — 설비 관리 2b 로 이관). destination 점유 검사도 없음(오더만 만듦).
- 응답 counts: `ordersCreated·orderItemsCreated·requestedCount`.
- **미할당 오더의 IF-05 거동**: `DestinationId=null` → `QueryDestination` 이 빈 NORMAL 활성 CHUTE 자동 배정(AUTO·OK), 빈 슈트 없으면 NG NO_DEST.
  명시 배정(설비 관리)이 주 경로 — 소터 배정은 설비 관리에서 cell_assignment 를 만들어야 `SorterCanAcceptBarcode` OK 가 된다.

---

## 3. ★ 초기화(reset) 의미 + 아카이브 정합 (OQ1=B · HIGHEST-STAKES)

### 3.1 reset 동작 (★ S-B2C-UX = **배치 스코프** · 시맨틱 불변)
대상 **배치(batchId)** 에 속한 오더(슈트/소터/미할당 무관)에 대해 한 트랜잭션으로. piece 는 `order_item →
wcs_order.WorkBatchId` 를 통해 배치에 귀속(스코프 술어 = `p.OrderItem.Order.WorkBatchId == batchId`):
1. **in-flight 가드(OQ3)** — 배치 오더의 활성 piece 중 status ∈ {QUERIED,RESERVED,PERMITTED,CELL_ASSIGNED,LOADED} 이 있고 `force==false` 면
   **거부(F + `counts.inFlight`)·데이터 무접촉**. `force==true` 면 진행 중 포함 진행. (재판정은 트랜잭션 안 — TOCTOU 협착.)
2. **아카이브(소프트삭제·OQ1=B)** — 배치 오더의 `piece`(+ 연관 `piece_event`·`sorter_command`) 중 `archived_at==null` 을 `archived_at=now` 로 세팅. **하드삭제(DELETE) 0**.
3. **수량 리셋** — 배치 소속 오더의 `order_item.reserved_qty=0, sorted_qty=0`.
4. **오더 재개** — 배치 내 `COMPLETED` 오더만 → `RUNNING`(ClosedAt=null). *재테스트 가능성 보장*: `QueryDestination` 이 COMPLETED/CANCELLED 오더를 제외하므로 재개하지 않으면 같은 바코드 재투입이 NG.
   - ⚠ **CANCELLED 오더는 재개하지 않는다**(의도 — 취소는 운영자 결정이므로 reset 이 되살리지 않음). CANCELLED 바코드 재투입이 필요하면 운영자가 명시적으로 오더 상태를 되돌려야 한다.
5. **보존(OQ2)** — `wcs_order`·`cell_assignment` 행 보존(재테스트 시 같은 배정 재사용). CANCELLED 오더는 유지(재개 안 함).
- 응답 counts: `archivedPieces·archivedPieceEvents·archivedSorterCommands·resetOrderItems·reopenedOrders·forcedInFlight`.
- 감사(operation_log STATE `B2C_RESET`) — 성공 INFO·거부/force WARN 전수, `op`(작업자)·`batchId` 실어 기록.
- ★ **소터 스코프 폐지(OQ-1)**: 기존 `sorterChuteNo` reset 은 은퇴(고아 엔드포인트 0 — 라우트 재사용·본문만 변경).
  아카이브 소프트삭제·수량 리셋·오더/배정 보존·COMPLETED→RUNNING·archived-exclusion 불변량은 스코프만 바뀌고 **전부 보존**.

### 3.2 활성 조회 경로 archived 제외 (전수 감사 — 이중 카운트 차단)
`piece`/`piece_event`/`sorter_command` 에 `DateTime? ArchivedAt`(nullable, 물리 컬럼 `ArchivedAt`) 추가. 모든 활성 조회는 `ArchivedAt == null` 만 읽는다:

| 경로 | 파일 | 위험 |
|---|---|---|
| 셀 currentQty(=COMPLETED sorter_command JOIN piece) | `SorterCellQty.LoadedQtyByCell` | ★ 이중 카운트 → SorterFull·IF-05 `SorterCanAcceptBarcode`·IF-10 `SelectCell`·모니터 셀 그리드가 전부 이 함수 경유(단일 수정으로 전파) |
| IF-05 p_id dedup / NG dedup | `DbRepositories.QueryDestination`·`RecordDenied` | 옛 piece 오소비 |
| IF-09 도착 기록 | `EfArrivalRecorder.RecordArrival` | 옛 piece에 이벤트 부착 |
| IF-10 투입/멱등 | `EfDepositRecorder.RecordDeposit`·`HasDepositRecord` | 옛 LOADED piece를 "중복"으로 오판 → 재테스트 IF-10 유실 |
| IF-10 qty·pieceId 조회 | `RcsController` | 옛 piece qty/handshake |
| 슈트 FULL 집계(CHUTE) | `ChuteCapacityService.InitializeFromDbAsync` | (B2C reset은 SORTER만 아카이브하나 일관성 위해 제외) |
| 모니터 in-flight piece / sorter_command | `MonitoringQueries` | 아카이브분 노출 |

- ⚠ 값변환 enum(`Status` HasConversion&lt;string&gt;)은 정적 배열 `Contains` 번역이 깨짐 → in-flight 술어는 **명시 OR**(`IsInFlight`) 로 표현(MonitoringQueries 패턴 준수).
- 회귀 0 근거: `ArchivedAt` 은 신규 nullable 컬럼 — 기존 모든 행은 `ArchivedAt==null` → `WHERE ArchivedAt IS NULL` 이 전량 통과 → 기존 동작·330 테스트 불변.

### 3.3 스키마·마이그레이션 (OQ1=B 발동 — 양 provider)
- 엔티티 3종에 `ArchivedAt` add-only nullable. 마이그레이션 `AddPieceArchivedAt`(SqlServer `20260713053134`·Sqlite `20260713053144`) — `AddHotPathIndexes` **뒤** 체이닝.
- Up = `AddColumn ArchivedAt` × 3(piece·piece_event·sorter_command), Down = 대칭 `DropColumn` × 3. ModelSnapshot diff = +9 순수 additive(재정렬 0).
- 마이그레이션 생성 명령(팩토리 헤더의 `--startup-project src/Wcs.Data` 는 선재 오류 — 마이그레이션 프로젝트 자신을 startup 으로):
  `dotnet ef migrations add AddPieceArchivedAt --project src/Wcs.Migrations.<Provider> --startup-project src/Wcs.Migrations.<Provider>`.

---

## 4. 프론트 페이지 (S-B2C-UX 개정 — 마스터-디테일 + 초기화 이관)

- **데이터 생성(2a)**: 경로 `/b2c/test-data`, NAV_SETS **b2c** "데이터 생성"(★ NAV 최상단). 헤더 "데이터 생성".
  - 상단 2분할: (좌) 5-필드 생성 폼(작업일자·배치명·차수·계획수량[생성 개수]·바코드 접두) / (우) 생성 결과 **마스터 그리드** =
    최근 배치(작업일자·배치·차수·상태·오더 총/미할당·항목) + **행별 체크박스**(초기화 다중 선택·OQ-5) + **행 선택**(디테일 로드) +
    상단 **작업자 이름 입력 + 초기화 버튼**(체크된 배치 다건 초기화).
  - 하단 **배치 상세 그리드**: **바코드(order_item)당 1행**(S-B2C-BARCODE-MULTI-FIX Fix 1). 한 오더에 바코드가
    N개면 **N행**이 뜬다(1 오더:N 바코드). 각 행 = 오더번호·바코드·계획·예약·분류 수량(**항목별** order_item)/
    상태·목적지·할당셀(**오더 레벨** — 오더에서 반복). row key = `order_item.id`. 소스 =
    `GET /api/b2c/facility/batch-items?batchId=`(take=1000 명시 · 배치 상세 전용 신설 엔드포인트).
    (구: `orders?batchId=` 오더 단위 집계 — 대표 바코드 FirstOrDefault·수량 Sum 로 첫 바코드만 보이던 근본을 해소.)
    반환수가 상한(1000)이면 절단 힌트 표면화(Fail-Loud · FIX ITER 2) — 표시 절단이며 초기화는 배치키 서버 스코프라 전량 적용.
    ⚠ 설비 관리(2b)의 오더 목록(할당 UI)은 여전히 **오더 단위**(`orders?...` · `B2cOrderDto` — 배정은 오더 단위) 무변경.
  - **초기화** = 배치 스코프 · **danger `ConfirmDialog`**(대상 배치 목록·삭제 범위·"되돌릴 수 없음"·작업자 이름) 경유.
    작업자 이름 공백이면 초기화 차단. 다건 = 체크된 batchId 별 순차 호출 + 집계 토스트. in-flight 거부 시 **강제 초기화(force) 재확인 체이닝**.
- **설비 관리(2b)**: 경로 `/b2c/facility`, NAV_SETS **b2c** "설비 관리". 상세는 `docs/B2C-FACILITY.md`. **초기화 없음(데이터 생성으로 이관).**
- 재사용: `Card`/`Button`/`Select`/`Badge`/`ConfirmDialog`/`Dialog`/`useToast`/TanStack Query/`StateMessage`. 단일 라이트 테마(다크모드 N/A).

### ★ 생성 결과 배치 그리드 상호작용 (S-B2C-GRID-UX · 2026-07-15)
- 배치 그리드(G1)에 공용 행 선택 상호작용을 결선(`useRowSelection` + `ContextMenu` — `docs/FRONTEND.md §5` 프리미티브, 그리드별 중복 0):
  - **드래그 = 범위 하이라이트**(연속 행·체크 상태와 시각 구분: teal 좌측 바 + 옅은 배경). **우클릭 = 4항목 메뉴**(전체 선택/해제 · 선택행 체크/해제).
  - ③④는 하이라이트 행에, ①②는 전체 행에 작용. 배치 그리드는 비활성 행이 없어 전 행 체크 가능.
  - **공존**: 행 클릭(디테일 로드)·헤더 전체선택·개별 체크박스 토글 무손상(클릭↔드래그 이동 임계 판별·click 억제).
- **랜딩(R1)**: B2C 첫 착지 = 이 데이터 생성 페이지(`homePathFor('b2c')`=`/b2c/test-data`). B2B 는 `/data-generator` 불변.
- 신규 UI 프리미티브: `context-menu.tsx`(전 그리드 공용) 1종 추가.

---

## 5. 감사 (OQ8) · 무접촉 경계

- **operation_log**: generate/reset 을 카테고리 `STATE`, action `B2C_GENERATE`/`B2C_RESET` 로 1행 기록(성공 INFO·거부/실패 WARN — 전수). 마이그레이션 0(기존 STATE 재사용).
- **무접촉**: `Wcs.PlcGateway`·`Wcs.Core`·`HandshakeOrchestrator` diff 0. 컨트롤러가 Modbus/판정 직접 호출 0(WcsDbContext+IOperationLogger 만). 실 3DS PLC/COM1/Azure/사용자 로컬 DB 무접촉 — 검증은 Sim3ds TCP + in-memory SQLite.
- **DI**: `AddScoped<IB2cTestDataService, B2cTestDataService>()` append(기존 배선 무접촉).

---

## 6. 검증 (실증)

- `dotnet test backend/Wcs.sln`: 전체 GREEN(회귀 0). ★ **S-B2C-UX: 마이그레이션 0**(ArchivedAt 기존재 — 스키마 무변경).
- reset 테스트(배치 스코프로 갱신): `B2cTestDataServiceTests`(BuildPlan 결정성·생성 멱등·수량 보존·**배치 reset** 소프트삭제/재개/보존·
  **아카이브 후 셀 currentQty=0 이중카운트 차단**·in-flight 가드/force·**미존재 배치 F**·TOCTOU COUNT-in-tx) + `B2cApiTests`(generate 왕복·검증 400·**미존재 배치 200 F**) +
  `B2cFacilityApiTests`(**E2E generate→소터 셀 배정→IF-05 예약→배치 reset(force)→재 IF-05 재예약** + 하드삭제 0 단언).
- 마이그레이션: SQLite 스크래치 `ef database update` 5체인 적용 + `ArchivedAt` 3테이블 실재 확인. (SqlServer 는 localhost 일회용 DB 로 검증.)

---

## 7. 엑셀 업로드 + 정적 양식 (S-B2C-DATAGEN-UPLOAD · 2026-07-26)

파라미터 생성 폼의 **대안 입력 경로** — 엑셀 6열(작업일자·배치명·차수·**오더번호**·바코드·수량).
**1 오더 : N 바코드** — 같은 (작업일자·배치명·차수·오더번호) 행들을 하나의 `WcsOrder` 로 묶고, 각 행의
바코드를 그 오더의 `order_item`(planned_qty = 행 수량)으로 만든다. 생성과 동일하게 **목적지 미할당 오더**
(`DestinationId=null`)를 만든다(셀/목적지 배정은 설비 관리 2b 소관). 확정 결정(사용자 게이트 2026-07-26).
DB 스키마 무변경(마이그레이션 0) — 데이터 모델(`wcs_order` 1:N `order_item`)이 이미 1 오더:N 을 지원.

> **오더번호 ≠ 바코드**(과거 S-B2C-EXCEL-UPLOAD 의 `orderNo==barcode` 강제 폐지). 오더번호 컬럼 신설로
> 한 오더가 여러 바코드를 가질 수 있다.

### 7.1 양식 컬럼 (헤더 고정 · 위치 기반 파싱 · 파서/템플릿 단일 소스 `B2cConstants.Hdr*`)

| 순서 | 헤더 | 필수 | 기입 대상 | 검증 |
|---|---|---|---|---|
| 1 | 작업일자 | 필수 | `work_batch.work_date` | `YYYYMMDD`\|`YYYY-MM-DD` + 달력 유효(`NormalizeBizDay`) |
| 2 | 배치명 | 필수 | `work_batch.batch_no` | 1~`BatchNoMaxLength`(100)자 |
| 3 | 차수 | 선택(기본 1) | `work_batch.wave_no` | 정수 1~9999 |
| 4 | 오더번호 | 필수 | `wcs_order.order_no` | `^[A-Za-z0-9_\-]{1,100}$`(`UploadOrderNoRegex`) |
| 5 | 바코드 | 필수 | `order_item.barcode` | `^[A-Za-z0-9_\-]{1,100}$`(`UploadBarcodeRegex`) |
| 6 | 수량 | 선택(기본 1) | `order_item.planned_qty` | 정수 1~9999 |

- **배치 그룹핑** = (작업일자·배치명·차수). **오더 그룹핑** = 배치 + 오더번호(같은 오더번호 여러 행 = 오더 1건).
- **배치 내 바코드 유일**: 파일 내 중복 판정 키 = (작업일자·배치명·차수·**바코드**) — 오더번호는 키에 넣지
  않는다(같은 오더가 여러 행에 정당하게 반복되므로). 이 키가 "다른 오더가 같은 바코드" + "같은 오더에 같은
  바코드 반복" 을 모두 잡는다.
- 목적지/셀 컬럼 **없음**(2b 소관 — 미할당 유지).

### 7.2 엔드포인트 `POST /api/b2c/test-data/upload` (multipart `IFormFile file`)

- **파일 레벨 검증(400 선행 · 컨트롤러)**: 파일 없음/0바이트 · 크기 > 10MB(`UploadMaxBytes`) · 확장자 ≠ `.xlsx`(**`.xls` 거부**) · MIME 화이트리스트 불일치. (컨트롤러는 컬럼 파싱을 하지 않아 무변경.)
- **구조/행 검증(200 + `status:"F"`)**: 헤더 6열 불일치 · 사용범위 팽창(행 `UploadMaxRows`/열 `UploadMaxColumns` — zip-bomb 방어) · 데이터 행 0 · 데이터 행 > 1000(`UploadDataRowsMax=GenerateCountMax`) · **행별 검증 오류**(→ `rowErrors[{row,message}]`).
- **원자성(Q4 확정)**: 행 검증 오류가 하나라도 있으면 **커밋 0**(트랜잭션 진입 전 조기 반환) + 전체 `rowErrors` 반환. 배치 내 바코드 중복(다른 오더/같은 오더 반복 무관)도 행 오류.
- **그룹핑 2단(영속화)**: (작업일자·배치명·차수) → `work_batch` upsert(UQ 멱등) → 배치 내 distinct 오더번호 → `WcsOrder` upsert(UQ `(WorkBatchId,OrderNo)`·미할당·RUNNING·GENERAL) → 각 행 → `order_item`(barcode=행 바코드·planned_qty=행 수량) INSERT.
- **멱등 append**: 기존 (배치·오더번호) 오더는 upsert 스킵 · 기존 (오더·바코드) `order_item` 은 INSERT 스킵 → 재업로드 시 신규 카운트 0, 기존 `reserved/sorted` 보존(생성과 동형).
- **응답 `B2cUploadResponse`** = `{ status, message, counts?, rowErrors? }`. `counts` = `ordersCreated`(신규 distinct 오더)·`orderItemsCreated`(신규 바코드)·`batches`·`dataRows`. 성공 판정 = `res.ok && status==="S"`.
- **파싱 예외**는 삼키지 않고 명시 `F`(`엑셀 파싱 오류: …`) + 감사 WARN. 순수 파싱/검증(`B2cTestDataService.ValidateUploadRows`)은 I/O 무의존(절대규칙 #8 · 테스트 가능).
- **감사**: `operation_log` `STATE`/`B2C_UPLOAD`(성공 INFO · 거부/실패 WARN — 전수). 마이그레이션 0(기존 오더 테이블 재사용).
- **교차-업로드 충돌 범위(설계 결정)**: 배치 내 바코드 유일은 **파일 내 검증이 정본**이다. 별도 업로드에서
  **다른** 오더가 기존 배치의 바코드를 재사용하는 교차-업로드 충돌은 이 스프린트에서 추가로 막지 않는다
  (재테스트 데이터 생성 도구의 멱등 append 철학·`ValidateUploadRows` 순수성 보존과 정합 · DB `UQ(OrderId,Barcode)`
  는 방어선). IF-05 동일-바코드 다중목적지 비결정성은 선재 미확정 항목과 동류(운영 다중 배치·동일 바코드 도입
  시 재검토).

### 7.3 정적 양식 파일 (동적 엔드포인트 없음 — 확정 결정)

- `frontend/public/b2c-order-upload-template.xlsx`(6열 헤더행 + 예시행 3건 + "설명" 시트). vite build 시 `wwwroot/` 로 복사 → 동일 출처 서빙(`UseStaticFiles`). dev 는 vite 가 `public/` 서빙.
- **예시행**: `ORD-0001`(바코드 `BC-0001`·`BC-0002` 2행 = **한 오더에 바코드 2건** · 1 오더:N 실증) + `ORD-0002`(바코드 `BC-0003` 단일·수량 2). 헤더는 `B2cConstants.Hdr*` 와 정확히 일치.
- 재생성: 커밋된 양식 생성 스크립트는 리포에 없다 — `ClosedXML`(백엔드 의존)로 일회성 재생성(테스트 헬퍼 `BuildXlsx` 패턴). 헤더 정합은 라운드트립 테스트가 잠근다.
- 프론트 "양식 다운로드" 버튼 = 이 정적 파일 링크(`/b2c-order-upload-template.xlsx`). **동적 `GET /template` 미구현**.
- 드리프트 방지: 커밋된 양식을 파서에 재투입하는 **라운드트립 테스트**(`StaticTemplate_RoundTrips_ThroughParser`)가 헤더·1:N 예시행 정합을 잠근다.

### 7.4 프론트 (`B2cDataGenPage` 생성 카드 좌측 — 생성 폼과 공존)

- **엑셀 업로드 블록 = 접기/펼치기 disclosure**(기본 접힘 · S-B2C-DATAGEN-UPLOAD A). 헤더 행(항상 표시) = 토글 버튼("엑셀 업로드" + chevron) + 양식 다운로드 링크(접힘에서도 접근 가능). 본문(파일 선택·업로드 버튼·안내·행오류)은 펼침일 때만 렌더. 접힘 기본으로 좌측 폼 자연높이를 줄여 하단 "배치 상세" 그리드가 오더 행을 실제로 표시(폼 오버랩 회귀 0 유지).
- 접근성: 토글은 native `<button type="button">` · `aria-expanded` · `aria-controls`(본문 id) · 키보드 Enter/Space(native button 기본). 신규 공용 UI 컴포넌트 없이 이 파일 내 로컬 구현.
- 파일 선택(`accept=".xlsx"`) + 업로드 버튼(파일 미선택 시 disabled · 업로드 중 로딩). 클라 `b2cTestData.upload(file)`(FormData · Content-Type 수동지정 금지 · 파일만 POST — 클라 무변경).
- 성공 → 성공 토스트 + 배치 그리드 invalidate + 파일 입력 리셋. 실패 → 에러 토스트 + **행별 오류 목록**(행번호+사유) 렌더(Fail-Loud). 생성 폼(`B2cGenerateForm` 5-파라미터 `orderNo==barcode`)은 **무접촉**(업로드 경로 한정).

### 7.5 검증 (실증)

- 백엔드: `dotnet test backend/Wcs.sln` 전량 GREEN(회귀 0). `B2cUploadServiceTests`(순수 검증 = OrderNo 필수·안전문자·배치내 바코드중복[다른 오더/같은 오더 반복]·1 오더:N 파싱 / 서비스 = 1 오더 2 바코드→`ordersCreated=1`·`orderItemsCreated=2`·planned_qty=행 수량·미할당·orderNo≠barcode·전체롤백·멱등·상한·빈파일·헤더불일치·정적양식 라운드트립) + `B2cUploadApiTests`(happy 200S[1 오더 2 바코드]·200F+rowErrors·배치내 바코드중복 200F·`.xls` 400·빈파일 400·MIME 400).
- 브라우저(Playwright): 엑셀 업로드 블록 접힘(기본)→하단 상세 오더 행 실제 표시 · 토글/키보드로 펼침(aria-expanded) · 양식 다운로드 링크 표시 · 6열(1 오더:2 바코드) 파일 선택→업로드→성공 토스트+배치 출현+오더 1건·항목 2건 · 배치내 바코드중복 파일→행별 오류 렌더+미생성(원자성) · 3뷰포트 폼 오버랩 0 · 콘솔 에러 0.
