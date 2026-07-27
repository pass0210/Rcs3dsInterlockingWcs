# B2C-FACILITY.md — B2C 설비 관리(목적지·셀·오더 할당·슈트 제어) 계약 캡처 (Generator/Evaluator 단일 근거)

> 작성: Generator (S-B2C-FACILITY) · 2026-07-14. 게이트 확정(OQ-1~OQ-4, 사용자 2026-07-14)을 코드로 구현한 결과를 캡처한다.
> 근거: `tasks/sprint-contract.md`(게이트 확정) · `docs/B2C-DATAGEN.md`(생성 2a) · `docs/FRONTEND.md`(§3.1·§3.3) · `docs/ERD.md`.

---

## 0. 범위·게이트 확정

운영자가 브라우저에서 **목적지(소터/슈트)를 구성하고, 소터 셀을 설정하고, 미할당 오더를 목적지/셀에 할당하고, 슈트를 제어**한다.
데이터 생성(2a)이 만든 **미할당 오더**를 이 페이지에서 배정한다. 혼합 토폴로지(소터 + 슈트)를 UI 만으로 구성·배정·제어할 수 있다.

- **OQ-1 = UI 셀 생성기**: 행×열 입력 → 순차 cellNo 1..(rows×cols) 벌크 생성. **스키마 무변경**(행/열 DB 미저장·마이그레이션 0).
- **OQ-2 = 거부+force**: 진행 중(in-flight piece / 활성 cell_assignment) 목적지 비활성화는 기본 **거부**·`force=true` 로만 강행.
  피스 이력 있는 목적지는 chuteNo/타입 수정 **불가**(수정 API 는 status/floor/workFullQty 만 — chuteNo·destType 미노출).
- **OQ-3 = 미시작 오더만 재할당**: `reserved=0 ∧ sorted=0 ∧ 활성(비아카이브) 피스 0` 인 오더만 목적지 변경(할당/재배정/해제) 허용. 진행 중이면 거부(force 없음).
- **OQ-4 = 계획수량 = 생성 개수**(생성 2a 소관 — `docs/B2C-DATAGEN.md`).
- **오케스트레이션**: 단일 Generator 순차(백엔드→프론트). 마이그레이션 0(스키마 무변경 — `WcsOrder.DestinationId`·`DestAssignType` 이미 nullable).

---

## 1. API 계약 (프론트 전용 — RCS 계약 아님)

라우트 접두 `/api/b2c/facility`(기존 `/api/b2c/test-data`·`/api/v1`·`/api/monitor`·`/api/ops` 무충돌).
관리 액션 응답 = `B2cManagementResponse`(`{status:"S"|"F", message, counts}`). 조회 = 원시 JSON(camelCase).
**성공 판정 = `res.ok && status==="S"`**(200 F 오인 금지). 검증 실패(Range/Required)만 400(경로분기 팩토리 allowlist 에 `/api/b2c/facility` 추가).

| 메서드 | 경로 | 용도 |
|---|---|---|
| GET  | `/api/monitor/destinations` | **전 목적지 열거**(CHUTE+SORTER_3D) — 목록·슈트 제어 destId 소스(A2·신설) |
| POST | `/api/b2c/facility/destinations` | 목적지 생성(`chuteNo·destType·floor?·workFullQty?`) — 소터/슈트 |
| POST | `/api/b2c/facility/destinations/{id}/activate` | 활성/비활성 토글(`isActive·force`) — OQ-2 가드 |
| POST | `/api/b2c/facility/destinations/{id}` | 수정(`status·floor·workFullQty`) — chuteNo/type 변경 없음(OQ-2) |
| POST | `/api/b2c/facility/sorters/{id}/cells` | 소터 셀 벌크(`rows·cols·capacity?·enabled?`) — 순차 cellNo(OQ-1) |
| GET  | `/api/b2c/facility/orders?assigned=&batchId=&take=` | 오더 목록(할당 UI 소스 — **오더 단위**) — `B2cOrderDto[]`. ★ take 상한 = `GenerateCountMax`(1000, 기본값도 동일). 프론트는 항상 상한을 명시 전달하고 반환수==상한이면 절단 힌트를 표면화(Fail-Loud — S-B2C-UX FIX ITER 2). 과거 200/500 침묵 절단 제거 |
| GET  | `/api/b2c/facility/batch-items?batchId=&take=` | 배치 상세 **per-item(order_item 단위)** — `B2cBatchItemDto[]`(S-B2C-BARCODE-MULTI-FIX Fix 1). 데이터 생성 페이지 하단 그리드 전용 — 1 오더:N 바코드 → N행(항목별 수량 + 오더 레벨 status·목적지·할당셀). take 상한 = `GenerateCountMax`(orders 와 동형). 설비 관리 배정 UI(오더 단위 `orders`)와 별개 경로 |
| POST | `/api/b2c/facility/orders/assign` | 오더→목적지 할당(`orderId·destinationId·cellNo?`) — 소터면 cell_assignment |
| POST | `/api/b2c/facility/orders/unassign` | 할당 해제/재배정(`orderId`) — OQ-3 |
| (재사용·무변경) POST | `/api/ops/chutes/{id}/clear`·`/api/ops/destinations/{id}/pause`·`/resume` | 슈트 제어(O1·O2·O3) |

### 1.1 `GET /api/monitor/destinations` → `DestinationDto[]`
`id·chuteNo·destType·floor·status·isActive` + readiness(`online·ready·full·paused` — `DestinationStatusService.Compute` 재사용) +
CHUTE `workFullQty·lastClearedAt`(chute_detail) / SORTER_3D `cellTotal·cellEnabled`(cell 집계). 읽기 전용·부수효과 0.

---

## 2. 파괴/변경 안전 (OQ-2/OQ-3 가드 · 감사)

- **목적지 생성**: chuteNo 중복 → F. destType 오류 → F. CHUTE 는 `chute_detail`(workFullQty) 동시 생성. SORTER_3D 는 floor NULL·폴링은 재기동 후.
- **비활성화 가드(OQ-2)**: 대상의 in-flight piece(QUERIED/RESERVED/PERMITTED/CELL_ASSIGNED/LOADED·archived 제외) 또는 활성 cell_assignment 가 있으면 `force=false` 에서 **거부(F + counts{inFlight,activeAssignments})**·`force=true` 로만 강행. 가드는 트랜잭션 안(TOCTOU 협착 — B2C-DATAGEN 선례).
- **오더 할당/재배정/해제(OQ-3)**: 트랜잭션 안 재판정으로 `reserved==0 ∧ sorted==0 ∧ 활성 피스 0` 만 허용, 아니면 거부(F). 재배정 시 기존 활성 cell_assignment release 후 신규 생성(부분 유니크 `(cell_id) WHERE released_at IS NULL` 준수). CHUTE 는 셀 지정 불가(F).
- **감사(전수)**: `operation_log` 카테고리 `STATE`, action `B2C_DEST_CREATE`/`B2C_DEST_ACTIVATE`/`B2C_DEST_DEACTIVATE`/`B2C_DEST_UPDATE`/`B2C_SORTER_CELLS`/`B2C_ORDER_ASSIGN`/`B2C_ORDER_UNASSIGN`(성공 INFO·거부/강제 WARN — 전수). 정본 감사는 `destination_event`(operator_id 컬럼 — 목적지 생성/활성 전이). 마이그레이션 0(기존 STATE/DestinationEventType 재사용).

---

## 3. 런타임 신설 목적지 반영 (기동-후 즉시 동작 — 핵심)

기동 시 `SorterRegistryFactory`·`ChuteCapacityService`·`DestinationStatusPusher` 는 그 시점 DB 목적지만 등록한다.
설비 관리로 만든 **CHUTE** 는 다음으로 런타임 편입해 기동 후에도 즉시 정상 동작한다(모두 `Wcs.Api` 내 — `Wcs.PlcGateway`/`Wcs.Core` 무접촉):

- `IChuteCapacityService.EnsureChuteRegistered(destId, workFullQty, isActive, isPaused)` — 인메모리 집계에 등록(GetHold·pause/resume 인메모리 반영·IF-08 push readiness 정확). 멱등.
- `DestinationStatusPusher.RegisterDestination(destId, chuteNo, destType)` — 전이 추적에 편입(`ConcurrentDictionary` — 관찰 루프 순회 중 동시 add 안전) + 부트스트랩 push. DORMANT(BaseUrl 미설정)면 no-op.
- 순서: capacity 먼저(ComputeAccept 재료) → pusher(올바른 accept 산출). 생성 직후 push 부트스트랩(신규 슈트 수용상태를 RCS 에 알림).

**SORTER_3D 신설**: DB 레코드는 즉시(IF-05 라우팅 유효 — `QueryDestination` DB 직독). 단 폴링/핸드셰이크는 `appsettings Sorters[]` 항목 추가 + **재기동** 후 시작(폴링 스코프 아웃 — UI 명시). 런타임 pusher 등록 안 함(bundle 부재 — 재기동 시 편입).

---

## 4. IF-05 라우팅 (혼합 토폴로지)

- **슈트 배정 오더** → `QueryDestination` 이 order.DestinationId(CHUTE) 사용 → 활성·미차단 → **OK + chuteNo**. CHUTE PAUSED 도 IF-05 OK(readiness 는 IF-08 push 별도 채널).
- **소터 배정 오더** → SORTER_3D Paused 면 NG. 아니면 `SorterCanAcceptBarcode`(빈 enabled 셀 ≥1 OR 그 오더 배정 셀 여유) → **OK + 소터 chuteNo**(셀 선택은 IF-10 SelectCell). 소터 배정 시 cell_assignment 를 만들어야 OK.
- **미할당 오더** → 빈 NORMAL 활성 CHUTE 자동 배정(AUTO·OK), 없으면 NG NO_DEST. 명시 배정이 주 경로.
- **슈트 pause → IF-08 UpdateChuteState next_state=2 발신 · IF-05 는 여전히 OK**(dispatch/readiness 분리). resume → next_state=3.

---

## 5. 프론트 페이지 (`/b2c/facility` · NAV_SETS.b2c "설비 관리") — ★ S-B2C-UX 개정

- **목적지 구성·제어**: 전 목적지 테이블(chuteNo·타입·상태 배지·셀/만재) + 행별 제어(pause/resume·슈트 clear·소터 셀 설정·활성/비활성) + "새 목적지" 다이얼로그.
  ★ **초기화(reset) 제거** — 데이터 생성 페이지로 이관(배치 스코프). 소터 행의 초기화 버튼·다이얼로그 없음(고아 엔드포인트 0 — reset 라우트는 데이터 생성이 배치 스코프로 소비).
  ★ **비활성 목적지 배지 정리(Minor #1/#7)**: `!isActive` 면 `비활성` 배지만 표기(정지·만재·정상 억제·정지 배지 혼동 해소) + 소터는 online/offline 하드웨어 상태만 병기.
- **오더 할당 = 2패널(OQ-4)**: 미할당/할당 탭 대체.
  - **좌 = 배정 대상 그리드**: 슈트(리프·체크박스) + 소터(펼침 가능 — 셀별 체크박스·점유 오더 표시). 각 대상 현재 배정 정보 병기(슈트=배정 오더 수·오더번호 발췌 / 셀=점유 오더). 상단 **`해제` 버튼**(체크 대상의 배정 미시작 오더 다건 순차 unassign·진행 중 스킵·집계 리포트).
  - **우 = 미할당 오더 그리드**: `orders?assigned=false` + 행별 체크박스(진행 중 오더는 비활성·OQ-3).
  - **`배정` 버튼**: 좌 선택 대상 ↔ 우 선택 오더 **min(N,M) 인덱스 1:1 페어링**(대상=chuteNo/cellNo 정렬·오더=orderNo 정렬). 슈트 → `assign(order, destId)`, 소터 셀 → `assign(order, destId, cellNo)`. 기존 단건 assign 엔드포인트 순차 호출(OQ-3 가드·DENIED 예외·감사 보존). 양쪽 ≥1 체크 시 활성.
- **작업자 이름**: 페이지 상단 필수 입력(감사 귀속) — 공백이면 파괴/변경/배정/해제 액션 차단. 파괴 액션(해제·비활성·clear·pause)은 `ConfirmDialog`(범위·비가역·작업자 표기).
- 재사용: `Card`/`Button`/`Select`/`Badge`/`ConfirmDialog`/`Dialog`/`useToast`/`StateMessage`/TanStack Query. 단일 라이트 테마.

### ★ 오더 할당 2패널 그리드 상호작용 (S-B2C-GRID-UX · 2026-07-15)
- 좌 배정 대상(G2 — 슈트 리프 + 소터 셀)·우 미할당 오더(G3) 두 그리드에 공용 행 선택 상호작용을 각각 결선
  (`useRowSelection` + `ContextMenu` — `docs/FRONTEND.md §5` 프리미티브, 그리드별 중복 0):
  - **드래그 = 범위 하이라이트**(체크와 시각 구분) · **우클릭 = 4항목 메뉴**(전체 선택/해제 · 선택행 체크/해제).
  - **자격(eligibility) 존중**(핵심): ①전체 선택·③선택행 체크는 개별 체크박스 비활성 조건과 동일 —
    G2 비활성 슈트(`!isActive`)·비활성 셀(`!enabled`), G3 진행 중 오더(`!canReassign`)는 체크되지 않음.
  - G2 소터 셀은 펼침 시 렌더되는 지연 로딩 행이지만 DOM 기반 훅이라 자동 포함(전체선택/하이라이트 대상 = 렌더된 행, OQ-1).
  - **공존**: G2 소터 행 펼침(행 클릭)·개별 체크박스 토글·기존 `배정`/`해제` 버튼 카운트 반영 무손상.
- 신규 UI 프리미티브: `context-menu.tsx`(전 그리드 공용).
- API 클라이언트: `b2cFacility.ts`(facility 표면 미러 + `useFacilityOrders` 오더단위 · `useFacilityBatchItems` 배치상세 per-item) · `ops.ts`(`clearChute`·pause/resume) · `queries.ts`(`useDestinations`·`useCells` — 소터 셀 드롭다운 소스).

---

## 6. 무접촉 경계

- `Wcs.PlcGateway`·`Wcs.Core` **무접촉**. 슈트 제어는 기존 `OpsController` API 호출만(Modbus/판정 직접 호출 0 — 절대규칙 #1·#8).
- 마이그레이션 0(스키마 무변경 — order 목적지 이미 nullable·행/열 DB 미저장).
- 실 3DS PLC / COM1 / Azure / 사용자 로컬 DB 무접촉 — 검증은 Sim3ds TCP + in-memory/scratch SQLite.
- 상수 외부화(`B2cConstants` — 하드코딩 금지·절대규칙 #7).
