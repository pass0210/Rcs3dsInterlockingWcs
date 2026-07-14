# Sprint Contract — S-B2C-DATAGEN (B2C 테스트 데이터 생성·초기화 페이지)

> 작성: Planner Subagent · 2026-07-13 · 사용자 백로그 2a(승인됨).
> **WHAT만 정의**(기술 상세는 Generator 판단). 아래 **Open Questions 는 사용자 게이트에서 결정**되어야 착수한다 — 특히
> 초기화(reset) 의미·범위와 아카이브 정책은 도메인 결정이라 이 계약만으로 확정하지 않는다.
> 근거 문서: `docs/ERD.md`(17테이블·이력 불변 원칙)·`docs/SPEC.md`·`docs/B2B-DATAGEN.md`(B2B 데이터생성 선례 계약)·
> `docs/FRONTEND.md`(프론트 스택·단일 라이트 테마)·`scripts/seed-field-20cells.sql`(현재 수동 시드의 형태)·
> `backend/src/Wcs.Api/Controllers/B2B/TestDataController.cs`(관리 API 패턴)·`frontend/src/pages/DataGeneratorPage.tsx`(페이지 패턴)·
> `frontend/src/lib/uiMode.ts`·`frontend/src/components/Layout.tsx`(B2C/B2B 토글·NAV).

---

## 배경 (WHY — 계약 아님, 문맥)

- B2B 에는 데이터 생성·초기화 UI 가 이미 있다(`TestDataController` + `DataGeneratorPage`, 계약=`docs/B2B-DATAGEN.md`).
  **B2C(실시간 3D 소터)에는 없다** — 현재 B2C 테스트 데이터는 개발자가 `scripts/seed-field-20cells.sql` 을 `sqlcmd` 로 직접
  실행하고, 재테스트 초기화도 수동 SQL(피스·이력 삭제 + `order_item` 수량 리셋)로 처리한다.
- RCS(협력사)가 테스트 서버로 연동 테스트 중이며 "데이터 리셋" 요청이 오면 운영자가 UI 에서 처리할 수 있어야 한다.
- 대상 도메인 모델(`docs/ERD.md`): `destination`(SORTER_3D)·`cell`·`cell_assignment`·`wcs_order`·`order_item`·`piece`·
  `piece_event`·`sorter_command`. 현재 시드 형태 = 오더/바코드 `0701-CELL-01~NN`, `plannedQty=3`(현장) 또는 대량,
  소터 destination 1개에 셀 1~N 각 오더 사전 배정(N↔N).

---

## Goal

B2C(3D 소터) 도메인 테스트 데이터를 **UI 에서 파라미터로 생성**하고, **재테스트 준비용으로 초기화**할 수 있는
백엔드 관리 API + React 페이지를 제공한다. 개발자의 수동 `sqlcmd`/수동 삭제 SQL 절차를 운영자가 브라우저에서
대체할 수 있게 한다. 생성 데이터는 실제 IF-05(RCS→WCS) 투입 판정에서 유효하게 소비 가능해야 하고, 초기화 후에는
같은 데이터로 재투입(재테스트)이 가능해야 한다.

---

## Implementation Scope

### A. 백엔드 — B2C 테스트 데이터 관리 API (프론트 전용 · RCS 계약 아님)
1. **신규 컨트롤러 + 서비스** — B2B `TestDataController`/`ITestDataService` 패턴을 따르되 **B2C 모델(오더·아이템·셀 배정·
   피스·이력)** 에 맞게. 라우트 접두는 기존과 무충돌해야 한다(B2B `/api/test-data`·RCS `/api/v1`·`/api/monitor`·`/api/ops`
   와 겹치지 않게 — 권고 `/api/b2c/test-data/*`, 정확 세그먼트는 OQ7). 관리 액션 응답 = `{status:"S"|"F", message, ...counts}`,
   조회 = 원시 JSON(camelCase). 프론트 성공 판정은 `res.ok && body.status==="S"`(200 F 를 성공 오인 금지 — B2B-DATAGEN §7.1 함정).
2. **엔드포인트(계약)** — 아래 3종이 코어(상세는 OQ 반영):
   - `POST …/generate` — 오더+아이템(바코드)+셀 배정 생성(파라미터화). 생성 로직은 순수 함수로 분리(I/O 의존 최소·테스트 가능, 절대규칙 #8 정신).
   - `GET  …/summary`  — 현재 B2C 테스트 데이터 상태 요약(소터/셀/오더/수량/진행중 피스 집계).
   - `POST …/reset`    — 재테스트 준비 초기화(OQ1·OQ2·OQ3 의미에 따름).
   - (선택) `GET …/detail?destinationId=` — 소터별 셀·배정·오더 상세 그리드용.
3. **생성 알고리즘(WHAT)** — 파라미터: 대상 소터(destinationId 또는 sorterChuteNo), 셀 범위/개수(또는 cellNos),
   `plannedQty`, 바코드/오더 패턴(현재 `0701-CELL-NN` 규약 재현 또는 파라미터화 — OQ4·OQ5), 배치 식별(workDate/batchNo/waveNo).
   셀↔오더 배정 규칙(현재 N↔N 결정적 배정)·멱등성 여부는 OQ4 확정값 기준. 생성이 `destination`/`cell`(Capacity·Enabled)까지
   만드는지 시드 전제인지 = OQ6.
4. **파괴 작업 감사** — reset(및 필요 시 generate)은 `operation_log` 에 1행 기록(카테고리/액션 = OQ8; 권고: 기존 `STATE`
   재사용으로 마이그레이션 0). 실패/거부도 기록. B2C `OpsController` 의 `IOperationLogger` 사용 패턴 재사용.
5. **DI 배선** — 신규 서비스 `AddScoped` append(기존 배선 무접촉). 컨트롤러가 판정/PLC 를 직접 호출하지 않음.

### B. 프론트 — B2C 데이터 생성·초기화 페이지
6. **신규 페이지** — 생성 폼 + 현재 상태 요약 + 초기화 버튼. 기존 `DataGeneratorPage`/`sections/*` 및 `components/ui`
   (Card·Button·Select·`ConfirmDialog`·`useToast`·TanStack Query) 재사용. 신규 UI 프리미티브 도입 최소화.
7. **라우트 등록** — `App.tsx` 에 신규 경로 추가.
8. **사이드바 등록** — `Layout.tsx` `NAV_SETS` 에 항목 추가. **B2C vs B2B 메뉴 세트 배치 = OQ6**(권고: B2C 세트 — B2C 도메인
   데이터이므로). bizDay 전역 상태(`uiMode`)와의 관계 정의(B2C 데이터는 workDate 기반 — OQ5 반영).
9. **파괴 작업 가드(UI)** — 초기화 = danger `ConfirmDialog` + 대상/삭제 범위 명시("되돌릴 수 없음"). 진행 중(in-flight) 작업 존재 시 경고 표기(OQ3).

### C. 스키마·마이그레이션 (조건부)
10. **기본 = 마이그레이션 0**(권고 reset 이 하드삭제+수량 UPDATE 면 스키마 무변경). **OQ1 에서 아카이브(soft-delete) 정책이
    선택되면** `piece`/`piece_event`/`sorter_command` 에 `archived_at` 추가 → 양 provider 마이그레이션
    (`Wcs.Migrations.SqlServer`·`Wcs.Migrations.Sqlite`, 직전 `AddHotPathIndexes` 선례) + B2C 17테이블 add-only. **OQ1 결정 후** 확정.

### D. 문서 (선택 · 권고)
11. 게이트에서 확정된 결정을 `docs/B2C-DATAGEN.md`(B2B-DATAGEN.md 대응물)에 캡처 — 후속 Generator/Evaluator 의 단일 근거.

---

## Evaluation Criteria (가중치)

1. **도메인 정합성 (★★★)** — 생성 데이터가 ERD 모델·현재 시드 형태와 일치하고 **IF-05 판정에서 유효 오더로 소비 가능**.
   초기화가 게이트 확정 의미(OQ1·OQ2·OQ3)를 정확히 구현 — 삭제/리셋 범위가 스펙과 일치, 재테스트 가능 상태 복원.
2. **파괴 작업 안전성 (★★★)** — 확인 다이얼로그·범위 명시·진행 중 작업 가드(OQ3)·전수 `operation_log` 감사. 무접촉 경계
   준수(실 PLC/COM1/Azure/사용자 DB·PlcGateway/Core/핸드셰이크 diff 0).
3. **패턴 일관성 (★★)** — B2B 관리 API/페이지 패턴 및 기존 프론트 프리미티브 재사용. 하드코딩 금지(절대규칙 #7 — 포트·수량·패턴 등).
4. **회귀 0 + 아키텍처 (★★)** — 기존 330 테스트 GREEN 불변 + 신규 단위/통합/E2E. 신규 API 별도 라우트·무충돌. tsc/eslint 0·콘솔 error 0.

---

## Completion Conditions (통과 최소 조건)

- 생성 API 로 만든 데이터가 `summary`/모니터링에서 확인되고, **IF-05 로 실제 투입 판정에 사용 가능**(피스 예약·`order_item.reserved` 증가) — Sim3ds TCP + SQLite 로 실증.
- 초기화 API 실행 후 재테스트 가능 상태(게이트 확정 의미대로: 권고 기본 = 피스·이력 제거 + `reserved/sorted=0` + 배정 유지)가 실증되고, 같은 데이터로 재투입 성공.
- 파괴 작업이 확인 다이얼로그 없이 실행되지 않으며 `operation_log` 에 기록됨. 진행 중 작업 가드 동작(OQ3 확정대로).
- 프론트 페이지가 확정 메뉴 세트에서 진입되고 생성→요약갱신→초기화 플로우가 브라우저에서 동작(콘솔 error 0).
- `dotnet test backend/Wcs.sln` 회귀 0(기존 330 + 신규). 무접촉 경계 git diff 0. 마이그레이션은 OQ1 결정에 따라 0 또는 양 provider 동일 델타.

---

## Parallel Modules

N/A (single module). 프론트가 백엔드의 **delivered API 형상**에 의존하므로(B2B 가 2a 백엔드→2b 프론트로 분리한 이유와 동일)
한 Generator 가 **백엔드 우선 → 프론트** 순으로 진행. 단일 파일 이중 기록 위험 없음.

## Evaluation Dimensions

functional only. 파괴 작업 안전성은 Evaluation Criteria #2 + Step 4.5 코드리뷰(보안/SQL injection/삭제 범위)로 커버 — 별도 병렬 차원 불요.

## Detected Project Type

**Full-stack** — 리포에 브라우저 진입점(`frontend/` React SPA + `index.html`)과 서버 라우트/컨트롤러(`backend/src/Wcs.Api/Controllers/*`)가
같은 리포에 공존. 이 스프린트는 두 표면(신규 React 페이지 + 신규 API 컨트롤러)을 모두 만진다.

---

## Verification Scenarios (Full-stack — 실재현, 최종 DOM/헬스핑 대체 불가)

### === Web/UI (프론트 표면) ===
- **각 표면 기본 상태**: B2C 데이터 생성 페이지 최초 로드 — (좌) 생성 폼(대상 소터·셀 범위/개수·plannedQty·바코드/오더 패턴·배치),
  (중/우) 현재 상태 요약(소터·셀 총/가용/배정·오더 상태별 수·reserved/sorted 합·진행중 피스 수), 초기화 버튼. 데이터 없을 때 빈 상태 안내 문구.
- **스프린트가 도입하는 대체 상태**: (1) 생성 성공 → 성공 토스트 + 요약 즉시 갱신, (2) 초기화 클릭 → danger 확인 다이얼로그(삭제 범위·되돌릴 수 없음 명시),
  (3) 진행 중(in-flight) 작업 존재 → 경고 배지/문구(OQ3 확정대로), (4) 조회 로딩 상태.
- **빈/에러 상태**: 대상 소터/셀 없음 안내 · 생성 파라미터 검증 실패 토스트(범위/패턴/개수) · "초기화할 데이터 없음" · API 실패 토스트(`{status:"F", message}` 노출).
- **다크모드 변형**: **N/A** — 프로젝트는 단일 라이트 테마(`docs/FRONTEND.md`·`frontend/src/index.css` "다크모드 없음" 명시).
- **핵심 인터랙션 플로우**: 폼 입력 → 생성 → 요약이 오더 N개·셀 배정 N건·plannedQty 반영으로 갱신 → 초기화 → 확인 → 요약이 재테스트 준비 상태(진행중 피스 0·reserved/sorted 0·배정 유지)로 갱신.

### === Backend/API (서버 표면) ===
- **엔드포인트(method + path)**: `POST /api/b2c/test-data/generate` · `GET /api/b2c/test-data/summary` · `POST /api/b2c/test-data/reset` (+ 선택 `GET /api/b2c/test-data/detail`). 정확 접두는 OQ7.
- **엔드포인트별 happy path**:
  - `generate`: 유효 파라미터(대상 소터·셀 N·plannedQty·패턴) → 200 `{status:"S"}` + 생성 건수(오더/아이템/배정). DB 에 실제 행 생성 확인.
  - `summary`: → 현재 집계 원시 JSON(데이터 없으면 0/빈 배열).
  - `reset`: 대상 지정 → 200 `{status:"S"}` + 처리 건수(삭제/리셋). DB 상태가 확정 의미대로 전이.
- **엔드포인트별 관련 에러 케이스**(적용 대상만 — 패딩 금지):
  - `generate` **400**: 파라미터 검증 실패(셀 범위/개수 상한·패턴 형식) → `{status:"F", message}`.
  - `generate` **200 F**: 대상 소터 미존재/비 SORTER_3D → 비즈니스 실패.
  - `reset` **가드**(OQ3): 진행 중 작업 존재 시 `force` 없이는 거부(200 F 또는 409 — OQ3 확정). 데이터 파괴 안 됨을 단언.
  - `reset` **200 F**: 초기화 대상 0건 / 대상 미지정.

### === End-to-end (2+ 계층 교차 — Sim3ds TCP + SQLite, 실 PLC/사용자 DB 무접촉) ===
- **생성→소비→초기화→재테스트 데이터 플로우**: (1) `generate` API 로 오더+아이템+셀 배정 생성 → (2) 그 바코드로 **IF-05(RCS→WCS) 호출**
  → 유효 오더로 판정되어 `piece` 예약(RESERVED) + `order_item.reserved` 증가(생성 데이터 실사용 가능 입증) → (가능 시 IF-10 핸드셰이크로 `sorter_command`/`sorted` 생성)
  → (3) `reset` API → 진행중 피스·이력 제거 + `reserved/sorted=0` + 배정 유지(확정 의미대로) → (4) 같은 바코드로 재차 IF-05 → 재예약 성공(재테스트 가능).
  프론트 페이지에서 (1)·(3) 을 실제 클릭으로도 재현(브라우저 검증).

---

## Open Questions (★ 사용자 게이트에서 결정 — 미해결 시 착수 금지)

> 도메인/정책 결정이라 Planner 가 임의 확정하지 않는다. 각 항목에 **권고(default)** 를 제시하되, 사용자가 게이트에서 확정한다.

- **OQ1 — 초기화 의미·아카이브 정책 (가장 중요)**: 재테스트 준비 reset 이 `piece`/`piece_event`/`sorter_command` 를
  **(A) 하드삭제** 하고 `order_item.reserved/sorted` 를 0 으로 UPDATE 할지(= 현재 개발자 수동 SQL 재현, 마이그레이션 0),
  **(B) 아카이브(soft-delete)** 로 보존할지(= B2B reset 이 사용자·사수 확정으로 채택한 `archived_at` 패턴, 단 B2C 3테이블에
  `archived_at` 신설 + 양 provider 마이그레이션 필요). **긴장 지점**: ERD 원칙 3 은 `piece_event`/`sorter_command` 를
  **append-only 이력(UPDATE 금지)** 으로 규정 — 하드삭제는 "테스트 데이터 초기화" 성격상 정당화되나 이력 불변 원칙과 상충하고,
  B2B 는 동일 긴장에서 (B) 를 택했다. **권고: (A) 하드삭제**(테스트 데이터·재테스트 목적·수동 SQL 충실 재현·마이그레이션 0).
  사용자가 B2B 와의 정책 일관성을 우선하면 (B).
- **OQ2 — 초기화 범위**: reset 이 (a) 대상 소터/배치 데이터만, (b) 전체 B2C SORTER 데이터, (c) 선택 배치 목록을 지우는지.
  그리고 `wcs_order`/`cell_assignment` 자체는 **보존**(수량만 리셋·배정 유지 → 즉시 재테스트)인지, 오더까지 제거인지.
  **권고: 대상 소터 지정 + 오더·배정 보존(수량 리셋), 피스·이력만 제거** — 수동 SQL·재테스트 편의와 정합.
- **OQ3 — 진행 중 작업 가드**: reset 시 in-flight 피스(status ∈ QUERIED/RESERVED/PERMITTED/CELL_ASSIGNED/LOADED)나
  진행 중 핸드셰이크가 있을 때 (a) 차단(fail-loud), (b) 경고 후 `force` 로만 허용, (c) 무조건 진행. **권고: (b)** — 기본 거부 +
  명시 force 확인. (핸드셰이크 런타임 상태는 DB 만으로 완전 판정 불가 → in-flight 피스 존재로 근사.)
- **OQ4 — 생성 파라미터·멱등성**: 파라미터 집합(셀 개수/범위·plannedQty·capacity·배치·패턴)의 확정 목록과, 생성이
  시드처럼 **멱등**(재실행 시 카운트 불변)인지 **가산**(매번 새 오더 추가)인지. **권고: 멱등 upsert**(시드 SQL 과 동형, N↔N 결정적 배정).
- **OQ5 — 바코드/오더 규약**: 현재 `orderNo == barcode == 0701-CELL-NN` 결합을 유지할지, 바코드 패턴을 별도 파라미터로 열지.
  workDate(bizDay 전역)·배치명(`FIELD-16` 등) 파생 규칙. **권고: 현재 규약 재현 + 배치/일자 파라미터화**.
- **OQ6 — destination/cell 생성 범위 & 메뉴 세트**: 생성이 `destination`(SORTER_3D)·`cell`(Capacity/Enabled)까지 만드는지,
  시드로 존재한다고 전제하는지. 페이지가 **B2C 메뉴 세트** vs B2B 세트 vs 양쪽 중 어디에 놓이는지. **권고: 셀/소터 없으면
  생성(시드 SQL 동작 흡수), 페이지는 B2C 세트**(B2C 도메인 데이터). bizDay 전역 상태 연동 여부도 여기서 확정.
- **OQ7 — 라우트 접두**: `/api/b2c/test-data/*`(권고) vs 다른 세그먼트. 기존 `/api/test-data`(B2B)·`/api/v1`(RCS)·`/api/monitor`·`/api/ops` 무충돌 필수.
- **OQ8 — operation_log 카테고리**: reset 감사를 기존 `STATE` 재사용(마이그레이션 0·권고) vs 신규 카테고리(CHECK 제약 변경 → 양 provider 마이그레이션). **권고: STATE 재사용**.

---

## Constraints / 무접촉 (절대)

- **무접촉 코드**: `Wcs.PlcGateway`·`Wcs.Core`·`HandshakeOrchestrator` diff 0. Modbus 레지스터 맵 불변. 컨트롤러가 Modbus/판정 직접 호출 금지(절대규칙 #1·#8).
- **무접촉 환경**: 실 3DS PLC/COM1/Azure/사용자 로컬 DB 무접촉. 검증은 **Sim3ds TCP + SQLite** 만.
- **검증 포트(하드코딩 금지 — 동적/설정 기반)**: 평가자 backend `:5215`/Sim `:1512`/Vite `:5190`대(`--strictPort`), 생성자 backend `:5216`/Sim `:1513`/Vite `:5191`대. **`:5205`/`:1502` 는 사용자 소유 — 사용 금지**.
- **하드코딩 금지**(절대규칙 #7): 포트·수량·패턴·타이밍은 파라미터/설정.
- **마이그레이션**: 필요 시(OQ1=B) 양 provider 동일 델타 + B2C 17테이블 add-only. 불필요하면 0.
- **회귀 0**: 현재 330 GREEN 불변. Generator 는 핸드오프 전 전체 스위트 통과 필수.
- **파괴 작업**: reset 은 Sim3ds/SQLite 상대로만 실증. 사용자 DB·현장 데이터에 절대 실행하지 않음.

---

> Planner self-check — Detected project type: Full-stack. Required scenario slots: 3 (Web/UI, Backend/API, End-to-end). All slots filled: yes.

---

## 게이트 확정 (사용자, 2026-07-13 — 이 섹션이 OQ의 최종 답)

- **OQ1 = (B) 아카이브** — reset은 piece/piece_event/sorter_command를 하드삭제하지 않고 `archived_at`
  soft-delete로 보존(B2B reset 정책 일관). → **스코프 §C 발동**: 3테이블 `archived_at` 컬럼 신설 + 양
  provider 마이그레이션(AddHotPathIndexes 뒤 체이닝). 모든 활성 조회 경로(IF-05/09/10·모니터·핸드셰이크·
  집계)가 archived 행을 제외하도록 정합 — **기존 판정·수량 산출이 archived 행을 읽으면 회귀**(셀 currentQty·
  SorterFull·오더 완료 판정 등 sorter_command COMPLETED JOIN 경로 특히 주의).
- **OQ2 = 오더·배정 보존** — 수량(reserved/sorted)만 0 리셋, cell_assignment 유지, 대상 소터 지정 가능.
- **OQ3 = (b) 기본 거부 + force** — in-flight 피스 존재 시 기본 F(사유 반환), UI 경고 확인 후 force 재요청만 허용.
- **OQ4 = 멱등** — 같은 파라미터 재실행 시 카운트 불변(upsert, N↔N 결정적 배정).
- **OQ5~OQ8 = Planner 권고 채택** — 현 규약(orderNo==barcode) 재현+배치/일자 파라미터화 / 셀·소터 없으면
  생성 + B2C 메뉴 세트 / `/api/b2c/test-data/*` / operation_log `STATE` 재사용.
