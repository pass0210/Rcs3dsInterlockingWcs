# B2C-DATAGEN.md — B2C(3D 소터) 테스트 데이터 생성·초기화 계약 캡처 (Generator/Evaluator 단일 근거)

> 작성: Generator (S-B2C-DATAGEN) · 2026-07-13. 게이트 확정(OQ1~OQ8, 사용자 2026-07-13)을 코드로 구현한 결과를 캡처한다.
> 근거: `tasks/sprint-contract.md`(게이트 확정) · `docs/ERD.md`(17테이블·이력 불변) · `scripts/seed-field-20cells.sql`(현 수동 시드) ·
> `docs/B2B-DATAGEN.md`(B2B 선례 — archived_at 소프트삭제 정책).

---

## 0. 범위 요약

- **목적**: 개발자가 `sqlcmd` 로 실행하던 `scripts/seed-field-20cells.sql`(B2C 3D 소터 테스트 데이터)과 수동 삭제 SQL(재테스트 초기화)을
  운영자가 브라우저에서 대체할 수 있는 **백엔드 관리 API + React 페이지**를 제공한다.
- **포함**: 생성(`generate`) · 요약(`summary`) · 셀 상세(`detail`) · 초기화(`reset`) API + B2C 메뉴 세트 페이지(생성 폼 + 소터 요약 + 셀 상세 + danger 초기화).
- **핵심 결정(OQ1=B)**: 초기화는 `piece`/`piece_event`/`sorter_command` 를 **하드삭제하지 않고 `archived_at` 소프트삭제**(B2B 정책 일관).
  → 모든 활성 조회 경로가 archived 행을 제외해야 함(§3 — HIGHEST-STAKES).

---

## 1. 관리 API 계약 (프론트 전용 — RCS 계약 아님)

라우트 접두 `/api/b2c/test-data`(OQ7). 기존 `/api/test-data`(B2B)·`/api/v1`(RCS)·`/api/monitor`·`/api/ops` 무충돌.
관리 액션 응답 = `{ status:"S"|"F", message, counts }`(`B2cManagementResponse`). 조회 = 원시 JSON(camelCase).
**성공 판정 = `res.ok && body.status==="S"`**(200 F 오인 금지 — B2B-DATAGEN §7.1 함정).

| 메서드 | 경로 | 용도 | 요청 | 응답 |
|---|---|---|---|---|
| POST | `/api/b2c/test-data/generate` | 멱등 생성(OQ4) | `B2cGenerateRequest`(body) | `B2cManagementResponse` |
| GET  | `/api/b2c/test-data/summary?sorterChuteNo=` | 소터별 요약 집계(선택 필터) | query `sorterChuteNo?` | `B2cSorterSummary[]` |
| GET  | `/api/b2c/test-data/detail?sorterChuteNo=` | 셀 상세(그리드) | query `sorterChuteNo`(필수) | `B2cCellDetail[]` |
| POST | `/api/b2c/test-data/reset` | 재테스트 초기화(OQ1·2·3) | `B2cResetRequest`(body) | `B2cManagementResponse` |

### 1.1 `B2cGenerateRequest` (DataAnnotations 검증 — 실패 시 400 + `{status:"F"}`)

| 필드 | 타입 | 검증 | 기본 |
|---|---|---|---|
| sorterChuteNo | int | `[Range(1,9999)]` | — |
| workDate | string | `[Required]`, `^\d{8}$\|^\d{4}-\d{2}-\d{2}$` | — |
| batchNo | string | `[Required]`, `StringLength(100,Min=1)` | — |
| waveNo | int | `[Range(1,9999)]` | 1 |
| cellCount | int | `[Range(1,200)]` | — |
| cellCapacity | int | `[Range(1,100000)]` | 3 |
| plannedQty | int | `[Range(1,100000)]` | 3 |
| orderPrefix | string | `[Required]`, `^[A-Za-z0-9_\-]{1,50}$` | — |

- 검증 400 형식화: `InvalidModelStateResponseFactory` allowlist 에 `/api/b2c/test-data` 추가(additive — 기존 `/api/v1/works`·`/api/test-data` 계약·B2C ProblemDetails 불변).
- 비존재 날짜(형식통과·달력 무효, 예 `2026-02-30`)는 `AppUtils.NormalizeBizDay` 가 `ArgumentException` → 컨트롤러 국소 catch → 400 `{status:"F"}`.
- 상한 상수는 `B2cConstants`(하드코딩 금지 — 절대규칙 #7).

### 1.2 `B2cResetRequest`

| 필드 | 타입 | 의미 |
|---|---|---|
| sorterChuteNo | int `[Range(1,9999)]` | 초기화 대상 소터(OQ2 — 대상 소터 지정) |
| force | bool | in-flight 존재 시 강제(OQ3 — 기본 false 거부·true 만 진행) |

---

## 2. 생성 알고리즘 (OQ4 멱등 upsert · OQ5 규약 · OQ6 소터/셀 생성)

`scripts/seed-field-20cells.sql` 을 코드로 흡수. **순수 함수** `B2cTestDataService.BuildPlan(cellCount, orderPrefix)` 가
결정적 계획 `(cellNo, orderNo)` 를 산출(I/O 무의존·테스트 가능 — 절대규칙 #8 정신). 이후 upsert 로 적용.

- **N↔N 결정적 배정**: n = 1..cellCount. `orderNo == barcode == "{orderPrefix}-{NN}"`(zero-pad 폭 = max(2, cellCount 자릿수) — 예 `0701-CELL-01`). 셀 n ↔ 오더 n.
- **멱등 upsert**(같은 파라미터 재실행 → 신규 카운트 0):
  1. destination(SORTER_3D, chuteNo) — 없으면 생성(OQ6). **다른 타입(CHUTE 등)으로 점유돼 있으면 F**.
  2. work_batch(workDate, batchNo, waveNo) — 없으면 RUNNING 생성.
  3. cell(destinationId, cellNo) — 없으면 생성(capacity/enabled=true), 있으면 capacity/enabled 만 보정(멱등).
  4. wcs_order(batchId, orderNo) — 없으면 RUNNING·UPSTREAM·destination=sorter 생성.
  5. order_item(orderId, barcode) — 없으면 planned/reserved=0/sorted=0 **INSERT 만**(기존 reserved/sorted **보존** — 재생성이 실적 클로버 금지).
  6. cell_assignment(cellId↔orderId) — 그 셀에 **활성 배정 없을 때만** 생성(부분 유니크 `(cell_id) WHERE released_at IS NULL` 준수).
- 응답 counts: `destinationCreated·cellsCreated·ordersCreated·orderItemsCreated·cellAssignmentsCreated·cellCount`.
- 생성 데이터는 실제 **IF-05(RCS→WCS) 투입 판정에서 유효 오더로 소비 가능**(RUNNING 오더 + 활성 배정 셀 → `SorterCanAcceptBarcode` OK → 예약).

---

## 3. ★ 초기화(reset) 의미 + 아카이브 정합 (OQ1=B · HIGHEST-STAKES)

### 3.1 reset 동작 (OQ1·OQ2·OQ3)
대상 소터(sorterChuteNo)에 대해 한 트랜잭션으로:
1. **in-flight 가드(OQ3)** — 활성 piece 중 status ∈ {QUERIED,RESERVED,PERMITTED,CELL_ASSIGNED,LOADED} 이 있고 `force==false` 면
   **거부(F + `counts.inFlight`)·데이터 무접촉**. `force==true` 면 진행 중 포함 진행.
2. **아카이브(소프트삭제·OQ1=B)** — 그 소터의 `piece`(+ 연관 `piece_event`·`sorter_command`) 중 `archived_at==null` 을 `archived_at=now` 로 세팅. **하드삭제(DELETE) 0**.
3. **수량 리셋(OQ2)** — 소터 소속 오더의 `order_item.reserved_qty=0, sorted_qty=0`.
4. **오더 재개** — `COMPLETED` 오더 → `RUNNING`(ClosedAt=null). *재테스트 가능성 보장*: `QueryDestination` 이 COMPLETED/CANCELLED 오더를 제외하므로 재개하지 않으면 같은 바코드 재투입이 NG.
5. **보존(OQ2)** — `wcs_order`·`cell_assignment` 행 보존(재테스트 시 같은 배정 재사용). CANCELLED 오더는 유지.
- 응답 counts: `archivedPieces·archivedPieceEvents·archivedSorterCommands·resetOrderItems·reopenedOrders·forcedInFlight`.

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

## 4. 프론트 페이지 (B2C 메뉴 세트 — OQ6)

- 경로 `/b2c/test-data`, NAV_SETS **b2c** 세트에 "데이터 관리" 추가(모니터링·3DS 워드·운영 제어 옆). 헤더 타이틀 "테스트 데이터 관리".
- 3분할: (좌) 생성 폼(대상 소터·작업일자·배치·차수·셀 개수·셀 용량·계획 수량·오더 접두) / (중) 소터 요약(셀 enabled/total/배정·오더 R/C/total·계획/예약/분류·진행중 피스) + 소터별 **초기화** 버튼 / (우) 선택 소터 셀 상세(셀·용량·현재수량·배정 오더·예약/분류·enabled).
- 초기화 = **danger `ConfirmDialog`** + 삭제 범위·"되돌릴 수 없음" 명시 + 진행 중(in-flight) 경고. force 경로: 기본 초기화가 in-flight 로 거부되면(`counts.inFlight>0`) **강제 초기화 다이얼로그**로 재요청.
- 재사용: `Card`/`Button`/`Select`/`ConfirmDialog`/`useToast`/TanStack Query/`StateMessage`(로딩·에러·빈). 신규 UI 프리미티브 0. 단일 라이트 테마(다크모드 N/A).
- workDate 는 페이지 폼 로컬 상태(기본 오늘) — B2C 헤더는 StatusRail 이라 전역 bizDay 컨트롤 비노출(B2B 세트 전용).

---

## 5. 감사 (OQ8) · 무접촉 경계

- **operation_log**: generate/reset 을 카테고리 `STATE`, action `B2C_GENERATE`/`B2C_RESET` 로 1행 기록(성공 INFO·거부/실패 WARN — 전수). 마이그레이션 0(기존 STATE 재사용).
- **무접촉**: `Wcs.PlcGateway`·`Wcs.Core`·`HandshakeOrchestrator` diff 0. 컨트롤러가 Modbus/판정 직접 호출 0(WcsDbContext+IOperationLogger 만). 실 3DS PLC/COM1/Azure/사용자 로컬 DB 무접촉 — 검증은 Sim3ds TCP + in-memory SQLite.
- **DI**: `AddScoped<IB2cTestDataService, B2cTestDataService>()` append(기존 배선 무접촉).

---

## 6. 검증 (실증)

- `dotnet test backend/Wcs.sln`: 345 GREEN(기존 330 + 신규 15). 회귀 0.
- 신규 테스트: `B2cTestDataServiceTests`(BuildPlan 결정성·생성 멱등·수량 보존·CHUTE 점유 F·reset 소프트삭제/재개/보존·**아카이브 후 셀 currentQty=0 이중카운트 차단**·in-flight 가드/force·미존재 F) + `B2cApiTests`(generate 왕복·검증 400·비즈니스 200 F·detail 400·**E2E generate→IF-05 예약→reset(force)→재 IF-05 재예약** + 하드삭제 0 단언).
- 마이그레이션: SQLite 스크래치 `ef database update` 5체인 적용 + `ArchivedAt` 3테이블 실재 확인. (SqlServer 는 localhost 일회용 DB 로 검증.)
