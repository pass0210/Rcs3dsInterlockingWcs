# [Sprint Contract] S-B2C-UX — B2C 데이터 생성/설비 관리 UX 재구성 (초기화 이관 · 생성결과 마스터-디테일 · 2패널 배정 · NAV 순서)

> 작성: Planner Subagent · 2026-07-14. 방금 병합된 **S-B2C-FACILITY(PR #63)** 위에 올라가는 **프론트 중심** UX 개선.
> 근거(직접 확인): 프로젝트 CLAUDE.md · tasks/lessons.md · tasks/workflow-agents.md(계약 템플릿) ·
> tasks/sprint-feedback.md(S-B2C-FACILITY Minor 7건) · docs/B2C-DATAGEN.md · docs/B2C-FACILITY.md · docs/FRONTEND.md ·
> 실코드(B2cDataGenPage.tsx · B2cFacilityPage.tsx · Layout.tsx NAV_SETS · b2cTestData.ts · b2cFacility.ts ·
> B2cTestDataService.cs(reset/batches/detail) · B2cFacilityService.cs(assign/unassign/orders) · B2c*Controller.cs).

---

## 0. 배경 — 사용자 지시 (2026-07-14) 4건

운영자 관점 재구성. **초기화는 "생성한 테스트 데이터를 되돌리는" 행위라 데이터 생성 쪽이 맞다**는 도메인 판단이 근간.

1. **초기화 이관**: 현재 **설비 관리 페이지**의 목적지 행 제어에 있는 `초기화(reset)` 를 → **데이터 생성 페이지의 "생성 결과" 섹션**으로 이동.
2. **생성 결과 그리드 = 마스터-디테일**: 배치 목록 그리드에 **행별 체크박스** + **상단 초기화 버튼**(체크된 배치 다건 초기화) + **하단 디테일 그리드**(선택 배치의 오더/바코드/수량/할당 상태).
3. **설비 관리 = 2패널 배정 UI**: **좌 = 목적지 목록**(각 목적지의 **현재 배정 정보 병기**) + **우 = 미할당 오더 목록**. 양쪽 **체크박스 다중 선택** → `배정` 버튼(좌 목적지 ↔ 우 오더 1:1). **해제**는 좌 목적지 그리드 **상단 `해제` 버튼**(체크한 목적지의 배정 해제).
4. **NAV 순서**: 사이드바에서 **데이터 생성·설비 관리 메뉴를 최상단**으로.

---

## 1. Goal

운영자가 (a) **데이터 생성 페이지에서** 생성한 배치를 **마스터-디테일로 열람하고 다건 선택으로 초기화(되돌리기)** 하며, (b) **설비 관리 페이지에서** 목적지와 미할당 오더를 **양쪽 체크박스로 다중 선택해 배정/해제** 할 수 있도록 두 B2C 관리 페이지의 UX 를 재구성한다. 초기화의 소속을 "설비"에서 "데이터 생성"으로 옮기고, 사이드바 진입 동선을 관리 흐름(생성→설비) 순서로 최상단에 둔다. **기존 백엔드 계약(가드·감사·force·아카이브 시맨틱)은 보존**하며 프론트를 중심으로 개선하되, 초기화의 **스코프가 소터→배치로 바뀌는 부분만** 최소 백엔드를 추가한다.

---

## 2. ★ 핵심 발견 (Planner 코드 확인 — 계약의 전제)

Generator/Evaluator 는 이 절을 사실 근거로 삼는다(추측 금지).

- **F1 — 초기화는 현재 "소터 스코프", 사용자는 "배치 스코프"를 원한다.**
  `B2cTestDataService.ResetAsync(B2cResetRequest{ sorterChuteNo, force })` 는 **SORTER_3D 목적지 1개** 기준으로 동작한다: piece `WHERE DestinationId==sorter` 아카이브, 그 소터에 배정된 오더의 order_item reserved/sorted=0, COMPLETED→RUNNING 재개. **배치(work_batch) 단위가 아니다.** 사용자가 원하는 "체크한 배치 초기화"는 배치에 속한 오더(슈트/소터/미할당 무관)를 대상으로 하므로 **소터-스코프 reset 을 프론트에서 루프해도 표현 불가** → **배치-스코프 reset 이 유일한 정합 해**다. `ArchivedAt` 컬럼은 이미 3테이블에 존재(S-B2C-DATAGEN) → **마이그레이션 0**.
- **F2 — 배치 디테일 데이터 소스는 이미 존재한다(백엔드 신설 불요).**
  `GET /api/b2c/facility/orders?batchId=<id>` → `B2cOrderDto[]`(orderNo·batchLabel·barcode·plannedQty·reservedQty·sortedQty·status·destinationChuteNo·destType·assignType·assignedCellNo·hasActivePiece·canReassign). 하단 디테일 그리드가 요구하는 필드를 전부 제공한다. 클라이언트 `b2cFacility.orders(assigned, batchId, signal)` 도 이미 `batchId` 를 받는다 — **훅(`useFacilityOrders`)만 batchId 를 전달하도록 확장**하면 된다.
- **F3 — 배정 모델은 "목적지 1 : 오더 N"이다(1:1 아님).**
  `wcs_order.DestinationId` 는 오더 쪽에 있어 **한 목적지에 여러 오더**가 배정될 수 있다(슈트: N오더→1슈트, 소터: N오더→N셀). 좌 그리드의 "현재 배정 정보"는 목적지당 0..N 오더 → **배정 오더 수 + 오더번호 요약** 표기가 자연스럽다. 데이터 소스 = `GET /api/b2c/facility/orders?assigned=true` 를 클라에서 목적지별 그룹핑(백엔드 신설 불요).
- **F4 — 배정/해제 백엔드는 "단건·가드"만 있다(벌크 엔드포인트 없음).**
  `AssignOrderAsync(orderId, destinationId, cellNo?)` / `UnassignOrderAsync(orderId)` 는 단건이며 **OQ-3 가드(미시작 오더만 · DENIED 예외)** 를 트랜잭션 안에서 강제한다. 다중 배정/해제는 **프론트가 기존 단건 엔드포인트를 페어/대상별로 순차 호출**해 표현하면 가드·감사 시맨틱이 그대로 보존된다(백엔드 신설 불요·집계 결과 리포트). 벌크 엔드포인트 추가는 최적화 옵션(OQ-4).
- **F5 — 초기화의 유일한 UI 호출부는 설비 페이지 소터 행 버튼**(`requestReset`)이다. 이관 시 이 버튼을 제거하면 소터-스코프 `/reset` 은 **고아 엔드포인트**가 된다 → Evaluator 규칙상 FAIL 소지. ⇒ **reset 을 배치-스코프로 재정의(repurpose)** 하면 고아 문제와 스코프 변경이 동시에 해결된다(OQ-1).

---

## 3. Implementation Scope (Generator 구현 대상)

> ⚠ **좌표 계약(coordination contract)**: 아래 reset 요청 형상 변경(`sorterChuteNo`→`batchId`)은 백엔드 DTO/컨트롤러와 프론트 클라이언트가 **동시에** 미러해야 하는 유일한 표면 변경이다. 내부 구현·컴포넌트 분해는 Generator 재량.

### A. 프론트 — 데이터 생성 페이지 (`B2cDataGenPage.tsx` + `b2cTestData.ts`)

**A1. 생성 결과 = 마스터-디테일 그리드**
- 상단 "생성 결과 — 최근 배치" 그리드(현 컬럼: 작업일자·배치·차수·상태·오더(미할당)·항목)에 **행별 체크박스 컬럼**(다중 선택) + 단일 행 **선택(활성 행 하이라이트)** 상태를 도입. 체크박스(다건 초기화 대상)와 행 선택(디테일 조회 대상)은 목적이 다르므로 UX 상 구분(권고: 체크박스=초기화 선택, 행 클릭=디테일 로드).
- **상단 초기화 버튼**: 체크된 배치들을 초기화. 파괴 액션이므로 **ConfirmDialog**(대상 배치 목록·삭제 범위·"되돌릴 수 없음"·작업자 이름) 경유. in-flight 존재 배치는 거부 → **강제 초기화 재확인(force) 체이닝**(설비 reset 의 기존 refuse→force UX 패턴 이식).
- **하단 디테일 그리드**: 선택한 배치의 오더 리스트(오더번호·바코드·계획/예약/분류 수량·상태·목적지(chuteNo/타입/셀)·할당여부). 데이터 소스 = `GET /api/b2c/facility/orders?batchId`(F2). 미선택 시 안내(빈 상태), 로딩/에러 행(StateMessage).
- **작업자 이름 입력**: 초기화는 감사 귀속이 필요하다(설비 페이지와 동형) → 데이터 생성 페이지에도 초기화용 **작업자 이름 필수 게이트**(공백이면 초기화 차단). 단, reset 백엔드(`B2cResetRequest`)는 현재 operatorName 필드가 없다 → operatorName 을 reset 계약에 추가할지는 OQ-3(권고: 추가해 감사 귀속 일관).

**A2. reset 클라이언트 재정의**
- `b2cTestData.reset(...)` 를 **배치-스코프**로 갱신(`{ batchId, force, operatorName? }`). `B2cResetRequest` 인터페이스 미러 갱신. 다건은 **체크된 batchId 별 순차 호출 + 집계 토스트**(성공 n·거부/force m 리포트) — F4 와 동형 프론트 오케스트레이션. 성공 판정 함정(`res.ok && status==="S"`) 유지.

### B. 프론트 — 설비 관리 페이지 (`B2cFacilityPage.tsx` + `b2cFacility.ts`)

**B1. 초기화 제거**
- 소터 행의 `초기화` 버튼·`requestReset` 다이얼로그 제거(→ 데이터 생성으로 이관, A1). 목적지 **CRUD·셀 설정·활성/비활성·슈트 clear/pause/resume 은 유지**(OQ-2 확인). `b2cTestData` import 가 초기화 외 용도로 남지 않으면 정리.

**B2. 오더 할당 = 2패널 배정 UI (기존 미할당/할당 탭 패널 대체)**
- **좌 패널 = 목적지 목록**: 목적지 행(chuteNo·타입·상태 배지·셀/만재) + **행별 체크박스** + **현재 배정 정보 병기**(그 목적지에 배정된 오더 수 + 오더번호 요약 — `orders?assigned=true` 그룹핑, F3). 상단 **`해제` 버튼**: 체크한 목적지(들)에 배정된 **미시작 오더**를 해제(단건 `unassignOrder` 순차 호출·OQ-3 가드 준수, 진행 중은 스킵·집계 리포트, F4). 파괴 액션 → **ConfirmDialog**(대상 목적지·해제될 오더 수·작업자).
- **우 패널 = 미할당 오더 목록**: `orders?assigned=false` + **행별 체크박스**(다중 선택).
- **`배정` 버튼**: 좌 선택 목적지 ↔ 우 선택 오더를 **1:1 페어링**해 순차 `assignOrder` 호출(OQ-4 로 페어링 규칙·소터 셀 처리 확정). 파괴/변경 아님이나 다건이므로 진행 상태·집계 결과 표면화.
- **소터 셀 배정 처리**(OQ-4): 벌크 1:1 배정은 셀 번호를 페어별로 못 받으므로 — 권고: **벌크 배정 = 목적지 레벨(셀 미지정, `cellNo=null`)**, 세밀한 소터+셀 배정은 **별도 단건 경로**(기존 AssignOrderDialog 유지 또는 행 액션)로 남긴다.

**B3. 이월 Minor 흡수 (재구성 표면과 겹치는 것만)**
- **[흡수] Minor #1/#7 — 파괴 액션 confirm 게이트 + 비활성 배지 혼동**: 좌 그리드 재구성 시 (a) 비활성 목적지에 `정지` 배지+`재개` 버튼이 병기돼 혼동되는 문제를 정리(비활성이면 정지 배지 억제 또는 "비활성" 우선 표기), (b) 새 `해제`/`배정` 다건 액션은 ConfirmDialog 로 게이트(재배정 confirm 미게이트 갭 해소).
- **[조건부 흡수] Minor #3 — `GetBatchesAsync` N+1**: 배치 그리드가 이 스프린트의 중심이므로, Generator 가 해당 서비스를 접촉하면 GroupBy 1쿼리로 축약(비접촉이면 defer). 저위험·비차단.
- **[명시적 defer — 이번 스코프 아님, tasks/todo.md 잔존]**: Minor #2(facility API operatorName 서버측 공백 거부 — 회귀 위험·프론트 중심 유지), destination_event enum 확장, `GetOrdersAsync` N+1, useDestinations abort signal. 계약 위반 아님(별도 후속).

### C. 프론트 — NAV 순서 (`Layout.tsx`)
- `NAV_SETS.b2c` 순서를 **데이터 생성 → 설비 관리 → 모니터링 → 3DS 워드 → 운영 제어** 로 재배열(현재는 모니터링·3DS·운영·데이터생성·설비). 항목 정의·아이콘·경로·`enabled` 불변, **순서만** 변경. 헤더 타이틀 매칭 로직(첫 enabled 항목 기본)이 순서 변경에 안전한지 확인(모드 진입 기본 페이지 `homePathFor('b2c')` 정합 — 바뀌면 함께 조정).

### D. 백엔드 — 배치-스코프 초기화 (`B2cTestDataService`·컨트롤러·DTO)
- `B2cResetRequest` 를 **배치 스코프**로 재정의: `sorterChuteNo` → `batchId`(+ `force`, + OQ-3 결정 시 `operatorName?`). 컨트롤러 라우트는 기존 `POST /api/b2c/test-data/reset` 재사용(요청 본문만 변경) — F5.
- reset 동작을 배치 스코프로 재정의하되 **기존 시맨틱 전부 보존**(B2C-DATAGEN §3 계약 불변):
  1. **in-flight 가드(OQ3)** — 배치에 속한 오더의 활성 piece(QUERIED/RESERVED/PERMITTED/CELL_ASSIGNED/LOADED·archived 제외) 존재 + `force==false` → **거부 + counts.inFlight**·데이터 무접촉.
  2. **아카이브(소프트삭제)** — 배치 오더에 연결된 piece(+piece_event·sorter_command) `ArchivedAt=now`. **하드삭제 0.**
  3. **수량 리셋** — 배치 오더의 order_item reserved/sorted=0.
  4. **오더 재개** — 배치 내 COMPLETED→RUNNING(CANCELLED 비재개 — 기존 규칙 불변).
  5. **보존** — wcs_order·cell_assignment 보존.
  - ★ **아카이브 행 제외 불변량(B2C-DATAGEN §3.2 HIGHEST-STAKES)** 유지 — 셀 currentQty·IF-05/10 dedup·모니터가 `ArchivedAt==null` 만 읽는 경로는 무변경(배치 스코프 아카이브도 동일 컬럼 사용).
  - 감사(operation_log STATE `B2C_RESET`) — 성공 INFO·거부/force WARN 전수, batchId 실어 기록.
- **마이그레이션 0**(ArchivedAt 기존재). 다건은 프론트 순차 호출(D 는 단건 배치 reset; 벌크 엔드포인트는 옵션 — OQ-4 와 동일 판단).

### E. 문서 갱신
- `docs/B2C-DATAGEN.md`: reset 을 **배치-스코프 + 데이터 생성 페이지 소속**으로 개정(`B2cResetRequest.batchId`, 마스터-디테일 그리드). §3 시맨틱은 스코프만 소터→배치로 갱신(아카이브·보존·재개 규칙 불변 명시).
- `docs/B2C-FACILITY.md`: reset 제거(→ 데이터 생성) + 오더 할당을 **2패널(목적지+미할당 오더) 다중선택 배정/해제** 로 개정. 좌 그리드 배정정보 병기 명시.

---

## 4. Evaluation Criteria (Evaluator 판정 기준 — Full-stack)

- **통합 품질(★★★)**: reset 요청 형상 변경(`batchId`)이 백엔드 DTO ↔ 프론트 클라이언트에 정합. 배치 디테일(`orders?batchId`)·좌 배정정보(`orders?assigned=true` 그룹핑)·2패널 배정/해제가 실제 데이터로 왕복. 레이어 경계 갭 0(고아 엔드포인트 0 — 이관 후 소터-스코프 reset 잔존 금지).
- **파괴 작업 안전성(★★★)**: 배치 초기화·다중 해제의 가드(OQ-3 미시작·DENIED 예외 준수)·감사 전수(operation_log STATE, 거부/force 포함)·아카이브 시맨틱 불변(하드삭제 0·아카이브 행 제외 회귀 0)·재개 규칙 불변(CANCELLED 비재개). 모든 파괴 액션이 ConfirmDialog(범위·비가역·작업자 귀속) 경유.
- **레이어별 품질(★★)**: (프론트) 마스터-디테일·2패널의 밀집 운영툴 톤 일관·발견가능성·체크박스 다중선택 UX 명료·상태(로딩/빈/에러) 처리·**콘솔 error/warning/pageerror 0**(BLOCKING). (백엔드) reset 배치 스코프 정확·검증(400)·트랜잭션·멱등·in-flight 재판정 트랜잭션 내(TOCTOU).
- **회귀 0 + 크래프트(★★)**: 기존 스위트 GREEN(비-B2C 330 불변, B2C reset 테스트는 배치 스코프로 **갱신** 후 GREEN — 후술), tsc/eslint/build 0, 무접촉 경계 준수, 상수 외부화(절대규칙 #7).

---

## 5. 무접촉 경계 · 제약 (사전 확정)

- `Wcs.PlcGateway`·`Wcs.Core`·`HandshakeOrchestrator` **무접촉**(reset·배정은 WcsDbContext + IOperationLogger 만·Modbus/판정 직접 호출 0 — 절대규칙 #1·#8).
- 실 3DS PLC / COM1 / Azure / 사용자 로컬 DB **무접촉** — 검증은 **Sim3ds TCP + SQLite**.
- **신규 마이그레이션 지양**(ArchivedAt 기존재 → 0). 불가피 시 양 provider(SqlServer+Sqlite) — 단 본 스프린트는 스키마 무변경이 목표.
- 포트(하드코딩 금지 — `.claude/ports.local.json` 정본): **평가자 :5215/:1512/:5190대**, **생성자 :5216/:1513/:5191대**. 사용자 :5205/:1502 무접촉.
- 기존 **359 GREEN 회귀 0**(reset 계약 스코프 변경에 따른 reset 전용 테스트 갱신은 회귀 아님 — 의도된 계약 변경, 재검증 필수).

---

## 6. Open Questions (진짜 새 도메인/UX 결정 — Planner 권고 포함)

> 사용자는 확인/수정만. 각 항목 코드에서 답이 나오지 않는 결정.

- **OQ-1 — reset 스코프 전환 방식(replace vs additive)**: 소터-스코프 reset 을 (a) **배치-스코프로 repurpose(소터-스코프 은퇴)** 인지 (b) 배치-스코프를 **추가**하고 소터-스코프도 유지 인지.
  - **권고: (a) repurpose.** F5 대로 소터-스코프의 유일한 UI 호출부가 이관으로 사라져 고아가 되고, 사용자 도메인 판단("초기화=배치 되돌리기")과 배치 스코프가 일치. 기존 소터-스코프 reset 테스트(`B2cApiTests`/`B2cTestDataServiceTests` reset E2E)는 배치 스코프로 **갱신**(단언 대상 = 배치 오더 아카이브·수량 리셋·재개·보존, in-flight refuse→force). 소터-스코프 잔존은 후속 요구 확인 시에만.
- **OQ-2 — 초기화 이관 후 설비 관리 잔존 기능**: 목적지 CRUD·셀 설정·활성/비활성·슈트 clear/pause/resume 은 **전부 유지**가 맞는가(초기화만 제거)?
  - **권고: 예 — reset 만 제거, 나머지 목적지/셀/슈트 제어 전부 유지.** (사용자 지시 원문 취지와 정합.)
- **OQ-3 — 배치 초기화 감사 귀속(operatorName)**: reset 에 작업자 이름을 요구할 것인가.
  - **권고: 예 — `B2cResetRequest.operatorName` 추가 + 데이터 생성 페이지 작업자 게이트.** 설비 페이지 파괴 액션과 감사 일관(operation_log 귀속). 서버측 공백 정책은 기존 `Op()` 패턴("(unspecified)" 대체) 준용(설비와 동형 — Minor #2 서버측 강제는 이번 defer).
- **OQ-4 — 2패널 배정 1:1 페어링 규칙 + 소터 셀 처리**: 좌 N목적지 · 우 M오더 선택 시 (a) **N==M 강제**(불일치면 배정 버튼 비활성/경고) (b) min(N,M) 인덱스 페어링 (c) 기타. 소터 배정 시 셀 지정 방식.
  - **권고: (a) N==M 강제 + 안정 정렬 인덱스 페어링**("1:1 배정"의 문언에 가장 부합·예측 가능). 소터 셀은 **벌크 배정에서 미지정(cellNo=null)**, 셀 단위 배정은 기존 단건 다이얼로그로 분리(F3·F4). 벌크 assign/unassign 은 **프론트 순차 호출**로 기존 가드·감사 재사용(전용 bulk 엔드포인트는 성능 요구 확인 시 후속).
- **OQ-5 — 마스터-디테일 인터랙션**: 다건 초기화용 **체크박스**와 디테일 로드용 **행 선택**을 구분할 것인가(권고: 예 — 체크박스=초기화 대상 다중, 행 클릭=디테일 단일 로드), 아니면 체크 1건일 때 그 배치 디테일 표시(단일화)인가.
  - **권고: 분리(체크박스+행선택 별도).** 다건 초기화와 단건 디테일 열람은 목적이 달라 혼용 시 오작동 위험.

---

## 7. Parallel Modules

- **N/A (단일 모듈).** 프론트가 백엔드 신규 reset 런타임에 의존(브라우저 검증)하고, 데이터 생성 페이지의 디테일 그리드가 **설비 lib(`b2cFacility.orders`)를 소비**(F2)해 프론트 내부 경계도 교차한다 → 깨끗한 disjoint 분할 아님. **단일 Generator 순차**(백엔드 배치-reset → 프론트 3화면). worktree 사용 시 lessons(agent-worktree-stale-base) 준수(현 HEAD 기준 수동 add).

## 8. Evaluation Dimensions

- **functional only** (단일 Evaluator). 파괴 작업 안전성(배치 reset·아카이브 불변량·다중 해제 가드)은 별도 expert pool 없이 §4 기준 내 ★★★ 가중으로 심사(S-B2C-FACILITY 선례 — 단일 Evaluator 가 TOCTOU·아카이브 이중카운트까지 완결). 과도한 machinery 회피.

## 9. Detected Project Type: **Full-stack**

(레포 신호: `frontend/`(React+TS+Vite SPA, 브라우저 진입점) + `backend/src/Wcs.Api`(ASP.NET Core Controllers·서버 라우트)가 동일 레포 공존 — 사용자 표현이 아닌 파일 구조로 판정.)

---

## 10. Verification Scenarios (Full-stack — 필수)

### 10.1 Web/UI 시나리오 (프론트 표면 · 브라우저 클릭스루 · Playwright MCP)

- **U1. NAV 순서 + 발견가능성**: b2c 사이드바 상단 2개가 **데이터 생성·설비 관리** 순으로 노출되고 정상 nav 경로로 도달(직접 URL 아님). 나머지 3개(모니터링·3DS 워드·운영 제어)가 뒤따름.
- **U2. 데이터 생성 — 생성 결과 기본 상태**: 배치 그리드에 **행별 체크박스 컬럼** + **상단 초기화 버튼**(선택 0건이면 비활성/경고) + **하단 디테일 그리드 빈 안내**(배치 미선택).
- **U3. 데이터 생성 — 마스터-디테일 선택 상태**: 상단 배치 행 클릭 → 하단 디테일 그리드에 그 배치의 오더/바코드/수량/할당상태 표시(`orders?batchId` 왕복).
- **U4. 데이터 생성 — 다건 초기화 흐름**: 배치 2건 체크 → 초기화 → **ConfirmDialog**(대상 배치·삭제 범위·비가역·작업자 이름 필수) → 실행 → 그리드/디테일 갱신 + 집계 토스트. in-flight 배치 → 거부 → **강제 초기화 재확인(force) 체이닝**.
- **U5. 설비 관리 — 2패널 기본 상태**: 좌 = 목적지 그리드(**체크박스** + **배정정보 병기**: 배정 오더 수/번호 + 상단 **`해제` 버튼**), 우 = 미할당 오더 그리드(**체크박스**). 초기화 버튼은 **부재**(이관 확인).
- **U6. 설비 관리 — 다중 배정(1:1)**: 좌 목적지 N + 우 오더 M 체크 → `배정` → (OQ-4 규칙대로) 배정 반영(좌 목적지 배정정보 갱신·우 목적지에서 사라짐). 개수 불일치 시 규칙대로 처리(권고 N==M 경고).
- **U7. 설비 관리 — 목적지 레벨 해제**: 좌 목적지 체크 → 상단 `해제` → **ConfirmDialog** → 그 목적지 미시작 오더 해제(진행 중은 스킵·집계 리포트) → 우 미할당 목록에 복귀.
- **U8. 빈/에러 상태**: 배치 0 · 미할당 오더 0 · 목적지 0 · API 에러 행(StateMessage). 배치 미선택 디테일 빈 안내.
- **U9. 다크모드**: **N/A** — B2C 단일 라이트 테마(docs/B2C-DATAGEN.md §4 / B2C-FACILITY.md §5).
- **BLOCKING — 콘솔 캡처**: `page.on('console')`·`page.on('pageerror')` → `screenshots/{sprint}/console.log`. React dev-mode warning(key·validateDOMNesting·update-depth 등)·pageerror·비의도 4xx/5xx = FAIL. 각 시나리오 번호 스크린샷.

### 10.2 Backend/API 시나리오 (백엔드 표면)

- **변경 엔드포인트**: `POST /api/b2c/test-data/reset` — 요청 본문 `{ batchId, force, operatorName? }`(← 기존 `{ sorterChuteNo, force }`).
- **재사용·무변경(회귀 확인)**: `GET /api/b2c/facility/orders?batchId=` · `?assigned=true|false` · `POST /api/b2c/facility/orders/assign` · `/unassign`.
- **Happy path(입력→출력 형상)**: batch reset(force=false·in-flight 없음) → S + counts(archivedPieces·archivedPieceEvents·archivedSorterCommands·resetOrderItems·reopenedOrders). 배치 오더 order_item reserved/sorted=0 · COMPLETED→RUNNING · wcs_order/cell_assignment 보존 · **하드삭제 0**(row count 불변, ArchivedAt 세팅). `orders?batchId` → 그 배치 오더 형상.
- **에러 케이스(적용분만 — pad 금지)**: 미존재 batchId → F. in-flight 존재 batch + force=false → **거부(F + counts.inFlight)**·데이터 무접촉. force=true → 진행(forcedInFlight 반영). 검증 400(batchId 범위). assign/unassign OQ-3 가드(진행 중 → F).

### 10.3 End-to-end 교차 레이어 (2개 이상 레이어 · MANDATORY)

- **E2E-1 (배정→라우팅→디테일→초기화 왕복)**: (BE+FE+Sim3ds+DB)
  ① 슬림 폼으로 배치 생성(미할당 오더 N). ② 설비 2패널에서 오더 일부를 **슈트**, 일부를 **소터 #1(셀)** 에 배정(다중 선택 배정) — 좌 그리드 배정정보 갱신 확인. ③ 각 바코드 IF-05(RCS→WCS) 왕복: 슈트행 OK+chuteNo, 소터행 OK. ④ 데이터 생성 페이지: 그 배치 행 선택 → **하단 디테일 그리드가 예약/할당 상태 반영**. ⑤ 그 배치 체크 → 초기화(in-flight 존재 → refuse → force) → **piece 아카이브(soft-delete)·수량 0·오더/배정 보존·하드삭제 0** 확인 → 재 IF-05 → 재예약(재테스트 가능). 평가자 포트(Sim3ds :1512 · API :5215 · Vite :5190) + SQLite.
- **E2E-2 (회귀·통합)**: 독립 `dotnet test backend/Wcs.sln` from scratch — 비-B2C **330 GREEN 불변**, B2C reset 테스트는 **배치 스코프로 갱신** 후 GREEN, 전체 스위트 GREEN·exit 0. `npx tsc --noEmit` 0 · `npm run lint` 0 · `npm run build` exit 0 · 브라우저 콘솔 0.

---

## 11. Completion Conditions (Evaluator PASS 최소 조건)

1. **NAV 순서**: b2c 사이드바 상단이 데이터 생성 → 설비 관리 순(정상 도달·발견가능성).
2. **데이터 생성 마스터-디테일**: 배치 그리드 행 체크박스 + 상단 초기화(다건) + 하단 디테일 그리드(선택 배치의 오더/바코드/수량/할당) 정상 동작.
3. **초기화 이관 + 배치 스코프**: 설비 페이지에서 reset 제거(고아 엔드포인트 0), 데이터 생성에서 **배치-스코프** 초기화 동작 — 아카이브 시맨틱 불변(하드삭제 0·아카이브 행 제외 회귀 0·CANCELLED 비재개)·in-flight refuse→force·감사(operation_log 거부/force 포함).
4. **설비 2패널 배정/해제**: 좌 목적지(배정정보 병기·체크박스·상단 해제) + 우 미할당 오더(체크박스) + 다중 1:1 배정 + 목적지 레벨 해제(OQ-3 가드·집계 리포트) 정상 동작. 파괴 액션 ConfirmDialog + 작업자 귀속.
5. **E2E-1 통과**(배정→IF-05 라우팅→디테일 반영→배치 초기화 refuse/force→재예약).
6. **회귀 0**: 비-B2C 330 GREEN 불변 + B2C 갱신 테스트 GREEN + 전체 스위트 GREEN, tsc/eslint/build 0, 브라우저 콘솔 0.
7. **무접촉 경계**(`Wcs.PlcGateway`·`Wcs.Core`·실 PLC/COM1/Azure/사용자 DB diff 0)·**마이그레이션 0**·상수 외부화(절대규칙 #7).
8. **문서 갱신**: B2C-DATAGEN.md(reset 배치 스코프·데이터 생성 소속·마스터-디테일) · B2C-FACILITY.md(reset 제거·2패널 배정).
9. **흡수 Minor**: #1/#7(파괴 confirm 게이트·비활성 배지 혼동) 해소. defer 분(#2·N+1 일부·enum·abort)은 tasks/todo.md 잔존(계약 위반 아님).

---

> **Planner self-check — Detected project type: Full-stack. Required scenario slots: 3 (Web/UI 시나리오, Backend/API 시나리오, End-to-end 교차 레이어). All slots filled: yes.**
>
> 보강: 절대규칙 점검 — #1 PLC 쓰기 단일 큐 무접촉(reset/배정은 DB만)·#8 판정 로직 무접촉. **마이그레이션 0 확인**(ArchivedAt 3테이블 기존재). 사용자 지시 4건 전부 스코프 반영(초기화 이관·마스터-디테일·2패널·NAV). Open Question 은 코드에서 답이 안 나오는 진짜 결정 5건(reset replace/additive·설비 잔존 기능·reset 감사 귀속·1:1 페어링+셀 처리·마스터-디테일 인터랙션) — 각 권고 포함. **핵심 발견 F1(소터→배치 스코프 불일치·최소 백엔드 정당화)**·F2(디테일 소스 기존)·F3(목적지 1:N)·F4(벌크=프론트 순차)·F5(고아 방지)를 계약 전제로 명시. 흡수 Minor(#1/#7)와 defer(#2·N+1·enum·abort) 명시. 혼합 배정→초기화 왕복을 E2E-1 필수 슬롯으로. Parallel Modules=N/A(프론트-백엔드 런타임 의존 + 프론트 내부 lib 교차). Evaluation Dimensions=functional only.

## 게이트 확정 (사용자, 2026-07-14 — OQ 최종 답)

- **OQ-1 = 배치 단위로 교체**: reset을 batchId 스코프로 재설계, 기존 소터 단위 reset 폐지(테스트 이관).
  reset 시맨틱(아카이브 소프트삭제·수량 리셋·오더/배정 보존·COMPLETED→RUNNING·in-flight 거부+force·
  archived-exclusion 불변) 전부 보존. 마이그레이션 0.
- **OQ-2 = 설비 관리는 초기화만 제거** — 목적지 CRUD·셀 설정·슈트 제어(clear/pause/resume)는 유지.
- **OQ-3 = reset에 operatorName 추가**(감사 귀속).
- **OQ-4a = min(N,M) 페어링**: 좌측 선택 대상 N개·우측 오더 M개 → 인덱스 순 min(N,M)까지 1:1 배정,
  나머지는 미배정 유지(배정 버튼은 양쪽 ≥1 선택 시 활성).
- **OQ-4b = 좌측 그리드에 소터·슈트 함께 표시, 소터는 드롭다운으로 셀 노출**: 좌측의 "배정 대상"은
  **슈트(리프)** 와 **소터의 개별 셀(드롭다운 펼침 시 체크박스)** 의 혼합. 즉 사용자가 좌측에서
  슈트들 + 소터 셀들을 체크로 골라, 우측 오더들과 min(N,M) 인덱스 페어링. 슈트 배정 = order→dest(chute),
  소터 셀 배정 = order→dest(sorter)+cellNo. (기존 단일 assign 엔드포인트가 order→dest(+cell) 지원 —
  대량은 프론트 순차 호출, OQ-3 가드·감사 보존.) 좌측 그리드에 각 대상의 현재 배정 오더 정보 병기.
- **OQ-5 = 체크박스(다건 초기화)와 행 선택(단건 상세) 분리**.
- **해제**: 좌측 목적지/셀 그리드 상단 "해제" 버튼 — 체크한 대상들의 배정 해제(다건, 순차 unassign).
- **Minor 흡수(S-B2C-FACILITY 피드백)**: 재구축되는 표면에 한해 파괴-액션 confirm 게이트 + 비활성 목적지
  정지-배지 혼동 해소 포함. 나머지 Minor는 todo 잔류.
