# [Sprint Contract] S-B2C-FACILITY — B2C 설비/목적지 관리 페이지(백로그 2b) + 데이터 생성 폼 슬림화(2a)

> 작성: Planner Subagent · 2026-07-14. 사용자 착수 승인 + 사전 확정 사항 다수(아래 §0) 반영.
> 근거(직접 확인): 프로젝트 CLAUDE.md · tasks/lessons.md · tasks/workflow-agents.md(계약 템플릿) ·
> tasks/sprint-feedback.md(S-B2C-DATAGEN Minor) · docs/B2C-DATAGEN.md · docs/FRONTEND.md(§3.1·§3.3) ·
> 실코드(B2cTestDataService·OpsController·MonitoringController/Queries·DestinationControlService·
> SorterGatewayRegistry·WcsDbContext·Entities·DbRepositories.QueryDestination·B2cDataGenPage·Layout·OpsControls·ops.ts·b2cTestData.ts).

---

## 0. 사전 확정 사항 (재질문 금지 — 계약 반영본)

1. **역할 분배**: 데이터 생성 페이지(2a, PR #62 병합)는 **"오더/바코드 데이터"만** 생성. **목적지 구성·셀 설정·오더→목적지 할당은 이 스프린트(2b) 설비 관리 페이지**가 담당.
2. **2a 생성 폼 재설계**: 파라미터 **5개만** — 작업일자·배치명·차수·계획수량·바코드 접두. 생성 결과 = **목적지 미할당 오더**(할당 전 IF-05 라우팅은 §4). 기존 generate 의 소터/셀 자동생성 + N↔N 배정 **제거**(설비 관리로 이관).
   - ✅ **Planner 코드 확인 결과: 마이그레이션 불필요.** `WcsOrder.DestinationId`·`DestAssignType` 는 이미 nullable(`WcsDbContext` 의 `IsRequired(false)`, Entities 의 `long?`/`DestAssignType?`). 미할당 오더는 **FK 를 세팅하지 않는 코드 변경만**으로 성립 — 양 provider 스키마 무변경(사전 확정의 조건부 마이그레이션 발동 안 함).
3. **설비 관리 페이지(2b) 3+1 기능**:
   a. **목적지 구성**: 소터(SORTER_3D)·슈트(CHUTE) 생성/수정/활성화. DB 목적지 레코드 수준(소터 Transport 연결은 appsettings 소관 — DB 레벨만).
   b. **소터별 셀 설정**: 셀 레이아웃/Capacity/Enabled (현재 seed SQL 로만 하던 것을 UI 로).
   c. **오더→목적지/셀 할당**: 미할당 오더를 소터(+셀) 또는 슈트에 할당(슈트행 오더 할당이 현재 UI 불가 갭).
   d. **슈트 제어 UI**(F3b 이연분): 슈트 clear(O1) + pause/resume — **백엔드는 OpsController 에 이미 존재**(코드 확인 완료), 프론트 결선만. 선행: **전 목적지 열거 API `GET /api/monitor/destinations` 신설**(FRONTEND.md §3.1 계획됐으나 미구현 — 코드 확인 완료).
4. **혼합 토폴로지 E2E(필수 슬롯)**: **소터 1(chuteNo=1) + 일반 슈트 chuteNo 2~9** 를 UI 로 구성, 오더를 슈트/소터 양쪽에 할당해 **IF-05 가 각각 올바르게 라우팅**(슈트행 OK · 소터행 OK+셀), 슈트 제어(pause 시 슈트도 IF-08 푸시 발신 · IF-05 정책 반영)를 E2E 로 입증.

---

## 1. Goal

운영자가 브라우저에서 **목적지(소터/슈트)를 구성하고, 소터 셀을 설정하고, 미할당 오더를 목적지/셀에 할당하고, 슈트를 제어**할 수 있는 **B2C 설비 관리 페이지**를 제공한다. 동시에 **데이터 생성 페이지를 "오더/바코드만 만드는" 5-파라미터 폼으로 슬림화**해 생성과 목적지 배정의 책임을 분리한다. 결과적으로 **혼합 토폴로지(소터 1 + 슈트 2~9)** 를 UI 만으로 구성·배정·제어할 수 있고, IF-05 가 각 목적지 타입으로 정확히 라우팅됨을 E2E 로 입증한다.

---

## 2. Implementation Scope (Generator 구현 대상)

> ⚠ 아래 **API 표면(경로·필드명)** 은 백엔드/프론트 두 병렬 모듈이 공유하는 **좌표 계약(coordination contract)** 이다 — 두 모듈이 이 표면에 맞춰 독립 빌드 후 fan-in 통합한다. 내부 구현(서비스 구조·컴포넌트 분해)은 Generator 재량. 필드명/경로는 프론트 클라이언트가 미러하므로 **변경 시 양 모듈 동시 반영**.

### A. 백엔드 (모듈 A — `backend/src/Wcs.Api/**`, `docs/**`)

**A1. 생성 슬림화 (`B2cGenerateRequest` + `GenerateAsync`)**
- `B2cGenerateRequest` 를 5-파라미터로 축소: `workDate`·`batchNo`·`waveNo`·`plannedQty`(→ OQ-4 확정 의미)·`barcodePrefix`. **제거**: `sorterChuteNo`·`cellCount`·`cellCapacity`.
- `GenerateAsync`: work_batch(멱등) + **미할당 오더 N건**(`DestinationId=null`·`DestAssignType=null`) + order_item(barcode=`{prefix}-{NN}`) 생성. **소터/셀/cell_assignment 생성 제거.** 기존 실적(reserved/sorted) 보존·멱등 유지.
- `B2cConstants`: `cellCount`/`cellCapacity`/`sorterChuteNo` 관련 상한 정리. `OrderPrefixRegex`·`WorkDateRegex` 유지.
- **마이그레이션 없음**(§0-2 확인). 스키마 무변경.

**A2. 목적지 열거 API (신설)**
- `GET /api/monitor/destinations` — `MonitoringController` + `IMonitoringQueries`(AsNoTracking) 에 추가. 전 목적지(CHUTE + SORTER_3D) 열거: `id·chuteNo·destType·floor·status·isActive` + CHUTE 는 `workFullQty·lastClearedAt`(chute_detail), SORTER_3D 는 `cellTotal/cellEnabled`. readiness(online/ready/full/paused)는 기존 `DestinationStatusService` 재사용(가능 시). 읽기 전용·부수효과 0.

**A3. 설비 관리 API (신설 — 목적지 CRUD·셀 설정·오더 할당)**
- 라우트 접두 `/api/b2c/facility`(기존 `/api/v1`·`/api/test-data`·`/api/monitor`·`/api/ops` 무충돌). 관리 액션 응답 = 기존 `B2cManagementResponse`(`{status:"S"|"F", message, counts}`) 재사용, 조회는 원시 JSON(camelCase). 성공 판정 = `res.ok && status==="S"`.
- 좌표 계약(제안 표면 — 필드명/경로 프리즈):

  | 메서드 | 경로 | 용도 |
  |---|---|---|
  | POST | `/api/b2c/facility/destinations` | 목적지 생성(`chuteNo·destType·floor?·workFullQty?`) — 소터/슈트 |
  | POST | `/api/b2c/facility/destinations/{id}/activate` | 활성/비활성 토글(`isActive`) — 파괴/변경 가드(OQ-2) |
  | POST | `/api/b2c/facility/destinations/{id}` (또는 PATCH) | 수정(`status`·`floor`·`workFullQty` 등, chuteNo/type 변경 제약은 OQ-2) |
  | POST | `/api/b2c/facility/sorters/{id}/cells` | 소터 셀 설정(레이아웃/Capacity/Enabled — OQ-1 확정 형태) |
  | GET | `/api/b2c/facility/orders?assigned=false&batchId=` | 미할당(또는 전체) 오더 목록(할당 UI 소스) |
  | POST | `/api/b2c/facility/orders/assign` | 오더→목적지 할당(`orderId·destinationId·cellNo?`) — 소터면 cell_assignment 생성 |
  | POST | `/api/b2c/facility/orders/unassign` | 할당 해제/재배정(OQ-3 확정) |

- 할당 규칙: 오더에 `DestinationId` + `DestAssignType=MANUAL` + `DestAssignedAt` 세팅. 소터 대상이면 `(cell_id) WHERE released_at IS NULL` 부분 유니크 준수하며 `cell_assignment` 생성. 슈트 대상이면 셀 없음.
- **파괴/변경 안전**(사전 확정): 비활성화·재배정 등은 트랜잭션 + operation_log 감사(카테고리 `STATE`, action 예 `B2C_DEST_CREATE`/`B2C_DEST_DEACTIVATE`/`B2C_ORDER_ASSIGN` — 기존 STATE 재사용, 마이그레이션 0). 실패/거부도 전수 기록.

**A4. 슈트 제어 (백엔드 무변경 — 기존 API 소비)**
- `OpsController` 의 O1 `POST /api/ops/chutes/{destId}/clear`, O2 `.../destinations/{destId}/pause`, O3 `.../resume` **이미 존재**(코드 확인). 백엔드 신규 작업 0 — 프론트 결선만(모듈 B). `DestinationControlService` 가 CHUTE pause/resume 을 인메모리(`ApplyPauseStateInMemory`)까지 반영 + `OnTransition` 발화 → `DestinationStatusPusher` 가 IF-08 UpdateChuteState 발신. **런타임 생성 슈트의 인메모리 반영 가능 여부는 Generator 가 구현 중 확인**(§4 제약).

**A5. 문서 갱신**
- `docs/B2C-DATAGEN.md`: 생성=오더/바코드만(목적지 미할당)·설비 관리로 목적지/셀/할당 이관 반영. reset 의 CANCELLED 비재개 명시(S-B2C-DATAGEN Minor #3 흡수).
- `docs/FRONTEND.md` §3.1: `GET /api/monitor/destinations` 를 "계획"→"구현" 표기. §3.3 슈트 제어 프론트 결선 완료 반영.

### B. 프론트엔드 (모듈 B — `frontend/src/**`)

**B1. 데이터 생성 페이지 슬림화 (`B2cDataGenPage.tsx` + `b2cTestData.ts`)**
- 생성 폼을 5-필드로 축소(작업일자·배치명·차수·계획수량·바코드 접두). `B2cGenerateRequest` 인터페이스 미러 갱신. 클라 검증(정규식·정수·상한) 유지·불필요분 제거.
- 생성 결과 view: 생성된 **미할당 오더/배치** 목록(운영자가 무엇이 만들어졌는지 확인). 목적지 요약/셀 상세/초기화 패널은 **설비 관리 페이지로 이관**(B2).

**B2. 설비 관리 페이지 (신설 — `설비 관리` NAV)**
- 목적지 구성: 목적지 목록(소터/슈트) + 생성 다이얼로그 + 활성/비활성 토글(ConfirmDialog + 작업자 감사). 소터별 셀 설정 패널(레이아웃/Capacity/Enabled — OQ-1 형태).
- 오더 할당: 미할당 오더 목록 → 목적지(+셀) 선택 → 할당. 재배정/해제(OQ-3).
- 슈트 제어: 슈트 목록 대상 clear(O1) + pause/resume(O2/O3) — 확인 다이얼로그 + 작업자 이름 필수(기존 `OpsControls` 패턴 재사용). `ops.ts` 에 `clearChute`·`pauseChute`/`resumeChute`(destId 로 pause/resume 재사용) 추가.
- 목적지 요약 + **재테스트 초기화(reset)** 이관: 목적지-스코프 액션이므로 설비 관리 페이지에 배치(기존 `/api/b2c/test-data/reset` 계약·force 경로 재사용, 백엔드 무변경).
- 재사용 프리미티브: `Card`·`Button`·`Select`·`Dialog`/`ConfirmDialog`·`useToast`·`Badge`·TanStack Query·`StateMessage`. 신규 UI 프리미티브 0. 단일 라이트 테마.

**B3. API 클라이언트**
- `b2cFacility.ts`(신설) — A2·A3 표면 미러(성공 판정 함정·200 F 처리는 기존 `b2cTestData.ts` 패턴 준용).
- `ops.ts` — `clearChute(destId, op)` 등 슈트 제어 추가(기존 pause/resume 은 destId 로 CHUTE 에도 동작).
- `api.ts`/`queries.ts` — `GET /api/monitor/destinations` 훅.

**B4. 내비게이션**
- `Layout.tsx` `NAV_SETS.b2c` 에 `설비 관리`(예 `/b2c/facility`) 항목 추가(모니터링·3DS 워드·운영 제어·데이터 관리 옆). 발견가능성 = Evaluator 검증 대상.

### C. 이월 Minor 처리 (관련 파일 접촉 시 함께 — 비차단)
- S-B2C-DATAGEN Minor: ① 프론트 셀 개수 상한 200 하드코딩 미러 → 상수화(설비 셀 설정 접촉 시). ④ `GetSummaryAsync` 소터당 N+1(요약 이관 시 개선 후보). ② 인프라 예외 operation_log 미기록. ⑥ B2B 모드에서 `/b2c/*` 직접 진입 배너 제목 cosmetic.
- S-HARDENING-1 이월 Minor: 관련 파일 접촉 시 함께 처리 후보(coordinator 판단 — 강제 아님).

---

## 3. Evaluation Criteria (Evaluator 판정 기준 — Full-stack)

- **통합 품질(★★★)**: API 계약 정합(백엔드 DTO ↔ 프론트 클라이언트 필드명/형상 일치), IF-05 라우팅 정확성(슈트/소터 분기), 슈트 제어 결선(clear/pause/resume → 백엔드 → IF-08 push). 레이어 경계 갭 0.
- **파괴 작업 안전성(★★★)**: 목적지 비활성화·오더 재배정·reset 의 가드(OQ-2/OQ-3 확정 규칙 준수)·감사 전수 기록(operation_log STATE + 실패/거부 포함)·아카이브 행 제외 회귀 0(reset 계약 불변)·하드삭제 0. 확인 다이얼로그(범위·비가역·작업자 귀속) 실동작.
- **레이어별 품질(★★)**: (프론트) 밀집 운영툴 톤 일관·발견가능성·상태(로딩/빈/에러) 처리·콘솔 0. (백엔드) RESTful 네이밍 일관·검증(400)·에러 응답 구조·트랜잭션·멱등.
- **회귀 0 + 크래프트(★★)**: 기존 스위트 GREEN(비-B2C 330 불변, B2C 테스트는 슬림 계약에 맞춰 갱신 — 후술), tsc/eslint 0, 무접촉 경계 준수, 상수 외부화(절대규칙 #7).

---

## 4. 현실적 제약 (Generator 유의 — 코드 확인 기반)

- **소터 런타임 생성**: `MultiSorterGatewayRegistry` 는 **기동 시** DB SORTER_3D ∩ appsettings `Sorters[]`(ChuteNo 매칭)로 번들을 구성한다(불변 딕셔너리). UI 로 신규 SORTER_3D destination 을 만들어도 **재기동 + appsettings 항목 없이는 폴링/핸드셰이크가 시작되지 않는다**(appsettings 누락 시 기동 fail-loud — lessons 2026-07-03). ⇒ **혼합 토폴로지 E2E 는 이미 appsettings/Sim3ds 에 배선된 소터 chuteNo=1 을 사용**하고, **신규 소터 destination 생성은 DB 레코드 수준까지만 검증**(재기동 후 실 폴링은 스코프 아웃/후속). UI 는 "소터 신설은 재기동 후 폴링 시작" 을 명시(운영자 오해 방지).
- **슈트 런타임 생성**: 신규 CHUTE 는 DB 레코드 즉시 **IF-05 라우팅 유효**(`QueryDestination` 이 DB 직독). 단 `ChuteCapacityService` 의 인메모리 FULL 추적은 기동 시 1회(`InitializeFromDbAsync`) 구성 → 신규 슈트 FULL 추적은 재기동 후 반영. **pause/resume 런타임 전이의 인메모리 반영이 startup-미등록 슈트에도 성립하는지 Generator 가 확인**(성립 안 하면 E2E 의 슈트 pause 는 startup 존재/seed 슈트로 조성). IF-08 push 자체는 `OnTransition` 이 chuteNo 를 실어 발신하므로 인메모리 맵과 독립.
- **IF-05 미할당 오더 거동**: 오더 `DestinationId=null` → `QueryDestination` 이 **빈 NORMAL 활성 CHUTE 자동 배정(OK)**, 빈 슈트 없으면 **NG NO_DEST**. 즉 "미할당=항상 NG" 아님 — 슈트 존재 시 AUTO 배정된다. 명시 할당(설비 페이지)이 주 경로이며 AUTO 는 슈트 fallback. **CHUTE PAUSED 는 IF-05 에서 OK**(readiness 는 IF-08 push 로 별도 전달 — dispatch/readiness 분리). **SORTER_3D PAUSED 는 IF-05 NG**.

## 5. 무접촉 경계 (사전 확정)

- `Wcs.PlcGateway`·`Wcs.Core` **무접촉**(슈트 제어는 기존 `OpsController` API 호출만·판정/Modbus 직접 호출 0 — 절대규칙 #1·#8).
- 실 3DS PLC / COM1 / Azure / 사용자 로컬 DB **무접촉** — 검증은 **Sim3ds TCP + SQLite**.
- 포트(하드코딩 금지 — `.claude/ports.local.json`): 평가자 :5215/:1512/:5190대, 생성자 :5216/:1513/:5191대.
- 파괴/변경 작업은 ConfirmDialog + operation_log 감사(기존 패턴).

---

## 6. Open Questions (진짜 새로운 도메인 결정 — 사전 확정 재질문 아님)

> 각 항목에 Planner 권고안 포함. 사용자는 확인/수정만 하면 됨.

- **OQ-1 — 소터 셀 "행·열" 모델**: 현 `Cell` 엔티티는 `CellNo·Capacity·Enabled` 만 보유하며 **행/열 컬럼이 없다**(코드 확인). "행·열 설정"이 (a) **UI 전용 대량 생성기**(예 "5행×4열=20셀" → cellNo 1..20 순차 확장, 스키마 무변경) 인지, (b) **영속 물리 레이아웃 속성**(row/col 컬럼 추가 = 양 provider 마이그레이션 + ERD 개정) 인지 결정 필요.
  - **권고: (a) UI 전용 생성기** — 행×열 입력을 순차 cellNo 로 확장(스키마 무변경·마이그레이션 0). 물리 좌표 표시 요구가 확인되면 (b)를 후속 스프린트로.
- **OQ-2 — 목적지 비활성화/수정 가드**: 활성 오더·in-flight piece·활성 cell_assignment 가 걸린 목적지를 비활성화/타입·chuteNo 변경할 때 (a) **거부(reset 의 in-flight 가드 선례) + force 로만 강행** 인지 (b) 무조건 허용 인지.
  - **권고: 비활성화 = in-flight/활성배정 있으면 거부 + force 재확인(reset 선례 일관). chuteNo·destType 변경 = piece 존재 시 불가(정합 위험)·floor/workFullQty/status 만 수정 허용.**
- **OQ-3 — 오더 재배정**: 이미 할당된 오더를 다른 목적지/셀로 재배정 허용 여부. (a) **미시작 오더만 재배정**(기존 cell_assignment released + 신규 생성) (b) 할당-1회 고정.
  - **권고: (a) — 예약/적재(reserved/loaded/piece 존재) 전 오더만 재배정 허용, 진행 중이면 거부(force 옵션).**
- **OQ-4 — "계획수량" 의미**: 슬림 폼의 `계획수량` 이 (a) **생성할 오더/바코드 건수 N**(각 order_item.planned_qty = 1 또는 상수) 인지 (b) **단일 오더의 per-item planned_qty**(그러면 오더 건수 파라미터가 5개에 없음 → 오더 1건) 인지. 현 코드는 `cellCount`(제거됨)가 오더 건수, `plannedQty`(=3)가 per-item 이었다.
  - **권고: (a) 계획수량 = 생성할 오더/바코드 건수 N**(barcode/orderNo = `{prefix}-{NN}` zero-pad). 각 order_item.planned_qty = 1(단건 테스트 모델). 5-파라미터로 가변 개수의 미할당 오더를 얻는 유일한 정합 해석.

---

## 7. Parallel Modules (Generator fan-out — §Multi-Instance Scaling)

- **모듈 A (Backend)**: `backend/src/Wcs.Api/**` + `docs/**` — A1~A5(생성 슬림·목적지 열거·설비 API·문서).
- **모듈 B (Frontend)**: `frontend/src/**` — B1~B4(폼 슬림·설비 페이지·API 클라이언트·NAV).
- **경계 청정성**: 두 모듈은 **파일을 공유하지 않는다**(`backend/**` vs `frontend/**` disjoint 서브트리). 공유 좌표 = §2 API 표면(경로·필드명) — 본 계약에서 프리즈. worktree 격리 불요(disjoint 경로 = strict partition 로 충분); 만약 worktree 사용 시 lessons(agent-worktree-stale-base) 준수 — 현 HEAD 에서 수동 `git worktree add`.
- **Fan-in**: 두 모듈 병합 후 **통합 빌드 + §10.3 혼합 토폴로지 E2E** 를 돌린 뒤에야 Evaluator 루프 진입. 미해결 충돌·통합 실패 상태로 fan-in 종료 금지.
- 각 모듈 로그 → `tasks/sprint-log/{module}.md`, fan-in 이 단일 `## IMPLEMENTATION COMPLETE` 를 `tasks/sprint-log.md` 로 통합.

## 8. Evaluation Dimensions

- **functional only** (단일 Evaluator). 파괴 작업 안전성은 별도 expert pool 없이 §3 기준 내 ★★★ 가중으로 심사(S-B2C-DATAGEN 선례 — 단일 Evaluator 가 TOCTOU 안전성까지 완결). 과도한 machinery 회피.

---

## 9. Detected Project Type: **Full-stack**

(레포 신호: `frontend/`(React+TS+Vite SPA, 브라우저 진입점) + `backend/src/Wcs.Api`(ASP.NET Core Controllers·서버 라우트)가 동일 레포 공존 — 사용자 표현이 아닌 파일 구조로 판정.)

## 10. Verification Scenarios (Full-stack — 필수)

### 10.1 Web/UI 시나리오 (프론트 표면)
- **U1. 데이터 생성 페이지 기본 상태**: 슬림 5-필드 폼 렌더 + 생성 결과(미할당 오더/배치) view.
- **U2. 설비 관리 페이지 기본 상태**: 목적지 목록(소터 #1 + 슈트) · 셀 설정 패널 · 미할당 오더 할당 패널 · 슈트 제어 패널 · (이관) 목적지 요약/reset.
- **U3. Alternate 상태**: 목적지 생성 다이얼로그 · 셀 설정 입력 · 오더 할당 선택 · 파괴 ConfirmDialog(비활성화·재배정·reset·슈트 clear — 범위/비가역/작업자 이름 필수).
- **U4. 빈/에러 상태**: 목적지 0 · 미할당 오더 0 · API 에러 행(StateMessage).
- **U5. 다크모드**: **N/A** — B2C 는 단일 라이트 테마(docs/B2C-DATAGEN.md §4).
- **U6. 발견가능성**: `설비 관리` 가 b2c NAV_SETS 에 노출 + 정상 nav 경로로 도달(직접 URL 아님).
- **U7. 핵심 상호작용 흐름**: 슈트 생성 → 확인 → 목록 반영 → 오더 할당 → 할당 반영 → 슈트 pause(확인+작업자) → 상태 배지 갱신. 각 단계 번호 스크린샷 + 콘솔 로그(`page.on('console'/'pageerror')` → `screenshots/{sprint}/console.log`, BLOCKING 규칙).

### 10.2 Backend/API 시나리오 (백엔드 표면)
- **엔드포인트(method+path)**: POST `/api/b2c/test-data/generate`(슬림) · GET `/api/monitor/destinations` · POST `/api/b2c/facility/destinations` · POST `/api/b2c/facility/destinations/{id}/activate` · POST `/api/b2c/facility/sorters/{id}/cells` · GET `/api/b2c/facility/orders?assigned=` · POST `/api/b2c/facility/orders/assign` · POST `/api/b2c/facility/orders/unassign` · (재사용·무변경) POST `/api/ops/chutes/{id}/clear`·`/api/ops/destinations/{id}/pause`·`/resume`.
- **Happy path(입력→출력 형상)**: generate → 미할당 오더 N(`DestinationId=null`) · counts. destinations → CHUTE+SORTER 열거 형상. facility create → S + 생성 목적지. assign → 오더 `DestinationId`/`DestAssignType=MANUAL` 세팅(+소터면 cell_assignment). chute clear → `last_cleared_at` 갱신 + destination_event(CLEARED,operator).
- **에러 케이스(적용분만 — pad 금지)**: generate 검증 400(workDate/prefix 인젝션·범위). 중복 chuteNo 생성 → F(또는 409). 미존재 오더/목적지 assign → 404/F. **비활성화 가드(OQ-2)**: in-flight/활성배정 있는 목적지 비활성화 force=false → 거부(F+counts). 비-CHUTE clear → 404. operatorName 공백 → 400.

### 10.3 End-to-end 교차 레이어 (2개 이상 레이어)
- **E2E-1 (MANDATORY — 혼합 토폴로지)**: (BE+FE+Sim3ds+DB) ① UI 로 슈트 chuteNo 2~9 생성 + 소터 #1 셀 설정(소터 #1 = 기존 appsettings/Sim3ds 배선). ② 슬림 폼으로 미할당 오더 생성. ③ 일부 오더를 **소터 #1(+셀)**, 일부를 **슈트 2~9** 에 할당. ④ 각 바코드로 IF-05(RCS→WCS) 왕복: **소터행 → OK + chuteNo=1 + 셀 선택**, **슈트행 → OK + 해당 chuteNo**. ⑤ 슈트 하나 UI pause → **IF-08 UpdateChuteState(paused/not-ready) 발신 관측** + 그 슈트 오더의 **IF-05 는 여전히 OK**(dispatch/readiness 분리 입증) → resume → push normal. 모두 평가자 포트(Sim3ds :1512 · API :5215 · Vite :5190) + SQLite.
- **E2E-2 (회귀·통합)**: 독립 `dotnet test backend/Wcs.sln` — 비-B2C **330 GREEN 불변**, B2C 테스트는 **슬림 생성 계약에 맞춰 갱신**(기존 소터/셀 자동생성·N↔N 단언 제거·미할당 오더 단언 추가)한 뒤 GREEN. 전체 스위트 GREEN·exit 0. `npx tsc --noEmit` 0 · `npm run lint` 0 · 브라우저 콘솔 error/warning/pageerror 0.

---

## 11. Completion Conditions (Evaluator PASS 최소 조건)

1. 데이터 생성 폼이 5-파라미터로 슬림화되고 **미할당 오더**(DestinationId=null)를 생성(마이그레이션 0 — order 목적지 이미 nullable).
2. 설비 관리 페이지에서 **목적지 생성/수정/활성화 · 소터 셀 설정 · 오더→목적지(+셀) 할당 · 슈트 clear/pause/resume** 이 정상 nav 경로로 도달·동작(발견가능성).
3. `GET /api/monitor/destinations` 신설·프론트 결선(슈트 제어의 destId 소스).
4. **혼합 토폴로지 E2E(E2E-1) 통과** — 소터행 OK+셀 · 슈트행 OK · 슈트 pause 시 IF-08 push + IF-05 OK(분리) 실증.
5. 파괴/변경 작업(비활성화·재배정·reset)의 가드(OQ-2/OQ-3) + ConfirmDialog + operation_log 감사(실패/거부 포함) 실동작. reset 아카이브 계약 불변(하드삭제 0·아카이브 행 제외 회귀 0).
6. 회귀 0: 비-B2C 330 GREEN 불변 + B2C 갱신 테스트 GREEN + 전체 스위트 GREEN, tsc/eslint 0, 브라우저 콘솔 0.
7. 무접촉 경계 준수(`Wcs.PlcGateway`·`Wcs.Core`·실 PLC/COM1/Azure/사용자 DB diff 0), 상수 외부화(절대규칙 #7).
8. 문서 갱신: B2C-DATAGEN.md(생성=오더만·이관 반영) · FRONTEND.md §3.1(destinations 구현 표기).

---

> **Planner self-check — Detected project type: Full-stack. Required scenario slots: 3 (Web/UI 시나리오, Backend/API 시나리오, End-to-end 교차 레이어). All slots filled: yes.**
>
> 보강: 절대규칙 점검 — #1 슈트 제어는 기존 OpsController API 호출만(Modbus 직접 0)·#8 판정 로직 무접촉. 마이그레이션 불필요 확인(order 목적지 이미 nullable). 사전 확정 4항 전부 반영·재질문 0. Open Question 은 코드에서 답이 나오지 않는 진짜 새 결정 4건만(셀 행열 모델·비활성화/수정 가드·재배정·계획수량 의미) — 각 Planner 권고안 포함. 혼합 토폴로지 E2E 를 필수 슬롯(E2E-1)으로 명시. Parallel Modules 는 disjoint 파일 경계(backend/frontend)로 선언, 공유 좌표(API 표면)는 계약에서 프리즈. Evaluation Dimensions=functional only(과도 machinery 회피).

---

## 게이트 확정 (사용자, 2026-07-14 — OQ 최종 답)

- **OQ-1 = UI 생성기만**: 행×열 입력 → 순차 cellNo 벌크 생성(스키마 무변경·마이그레이션 0). 행/열은 DB 미저장.
- **OQ-2 = 거부+force**: 진행 중 피스 있는 목적지 비활성화는 기본 거부·force로만 강행. 피스 이력 있는
  목적지는 chuteNo/타입 수정 불가(비활성화+신설로 대체).
- **OQ-3 = 미시작 오더만 재할당**: reserved/sorted=0 ∧ 피스 이력 0인 오더만 목적지 변경 허용.
- **OQ-4 = 계획수량 = 생성 개수**: 생성 폼 5개 파라미터 유지, 계획수량 = 생성할 바코드/오더 수(각 오더
  plannedQty=1 고정).
- **오케스트레이션 노트**: Parallel Modules 선언은 유지하되 실행은 단일 Generator 순차(백엔드→프론트) —
  프론트 브라우저 검증이 백엔드 신규 API 런타임에 의존 + worktree 스테일 베이스 교훈(agent-worktree-stale-base).

## 게이트 보완 (사용자, 2026-07-14 — iteration 1 평가 중 발견분)
- **OQ-3 보완 = DENIED 예외**: 거부(DENIED) 피스는 물리 라우팅 0이므로 재할당 가드의 "피스 이력"에
  카운트하지 않는다 — DENIED 기록만 있는 오더는 할당/재할당 허용(RCS 선조회 NO_DEST → 후할당 흐름 성립).
  예약/적재(비-DENIED) 이력이 있으면 기존대로 차단.
