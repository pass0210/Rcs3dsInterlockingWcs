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
| GET  | `/api/b2c/facility/orders?assigned=&batchId=&take=` | 오더 목록(할당 UI 소스) — `B2cOrderDto[]` |
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

## 5. 프론트 페이지 (`/b2c/facility` · NAV_SETS.b2c "설비 관리")

- **목적지 구성·제어**: 전 목적지 테이블(chuteNo·타입·상태 배지·셀/만재) + 행별 제어(pause/resume·슈트 clear·소터 셀 설정·소터 초기화·활성/비활성) + "새 목적지" 다이얼로그.
- **오더 할당**: 미할당/할당 탭 → 오더 테이블 → 배정 다이얼로그(목적지 선택·소터면 셀 번호) / 재배정·해제(OQ-3 — `canReassign` false 면 버튼 비활성).
- **작업자 이름**: 페이지 상단 필수 입력(감사 귀속) — 공백이면 파괴/변경 액션 차단. 파괴 액션은 `ConfirmDialog`(범위·비가역·작업자 표기).
- 재사용: `Card`/`Button`/`Select`/`Badge`/`ConfirmDialog`/`Dialog`/`useToast`/`StateMessage`/TanStack Query. 신규 UI 프리미티브 0. 단일 라이트 테마.
- API 클라이언트: `b2cFacility.ts`(facility 표면 미러) · `ops.ts`(`clearChute` 추가·pause/resume 재사용) · `queries.ts`(`useDestinations`).

---

## 6. 무접촉 경계

- `Wcs.PlcGateway`·`Wcs.Core` **무접촉**. 슈트 제어는 기존 `OpsController` API 호출만(Modbus/판정 직접 호출 0 — 절대규칙 #1·#8).
- 마이그레이션 0(스키마 무변경 — order 목적지 이미 nullable·행/열 DB 미저장).
- 실 3DS PLC / COM1 / Azure / 사용자 로컬 DB 무접촉 — 검증은 Sim3ds TCP + in-memory/scratch SQLite.
- 상수 외부화(`B2cConstants` — 하드코딩 금지·절대규칙 #7).
