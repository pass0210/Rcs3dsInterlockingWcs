# Sprint Contract — S-B2B-2b (프론트: B2B 데이터 생성/관리 페이지 + B2C/B2B UI 토글 + 아카이브 필터 UI)

> Branch: `feat/b2b-2b-datagen-frontend` · Base: `develop` @ PR #39 병합(B2B-2a 백엔드 test-data 관리 API + archived_at 존재)
> 작성: Planner Subagent · 2026-07-08
> 스펙 근거(정본): `docs/B2B-DATAGEN.md` §4(화면)·§5(토글)·**§7(2b 착수 보강 — delivered API 정합·통합지점·다크모드)** +
> `docs/FRONTEND.md`(우리 프론트 스택·정적서빙·shadcn 확정) + `docs/DESIGN-airbnb.md`(디자인 토큰).
> **Generator·Evaluator는 pwd 밖(원본 `BowooTestBatchSystem_v2`) 접근 금지** — 위 문서 + 우리 프론트 기존 코드 +
> 실 `/api/test-data/*`(구동)만이 유일 근거.

## Goal

B2B-2a(백엔드)가 이미 제공하는 `/api/test-data/*` 관리 API 위에 **프론트 전용 계층**을 이 프로젝트 스택으로 재개발한다.
(1) **B2C/B2B UI 토글**(헤더/사이드바 메뉴 세트 전환 — UI 전환만), (2) **B2B 데이터 생성/관리 페이지**(`/data-generator`:
생성 폼·엑셀 업로드·요약/상세 그리드·수신초기화/삭제), (3) **아카이브 필터 UI**(상세 그리드 `active|all|archivedOnly`
3상태 — 사수 요구 "삭제해도 보여줘" 가시화). 기존 B2C 프론트(MonitorPage·SortersPage·기존 Layout 동작) 및 **백엔드 0 변경**
(B2B 페이지·라우트·Context·UI 프리미티브 추가만).

## 확정 방침 (사용자 — 불변)

1. **B2C/B2B UI 토글 = UI 전환만**: 헤더/사이드바에서 메뉴 세트 전환. 백엔드 API 는 양쪽 상시 활성(모드 게이트 없음).
   - **B2C 세트**: 모니터링(`/monitor`) · 3DS 워드(`/sorters`) · 운영 제어(현 disabled `F3` 배지 유지).
   - **B2B 세트**: 데이터 생성(`/data-generator`) · (후속 B2B-3: 로그·비교·박스·설정 — disabled phase 배지로 예고).
   - 상태 = **React Context + localStorage**(`mode`(`b2c`|`b2b`) + `bizDay` + `autoRefresh`). 손상값 화이트리스트 폴백.
   - 기존 B2C 메뉴(모니터링·소터) **무접촉**, B2B 메뉴 추가.
2. **자동생성 UI 없음(제외 확정)**: `auto-config`·`preview-chutes` 화면 없음. **수동 생성(슈트범위+바코드개수 라운드로빈)·
   엑셀 업로드만**.
3. **아카이브 필터 UI**: 상세 그리드에 `active|all|archivedOnly` 3상태 필터 → detail 조회 `archived` 파라미터 전달.
4. **완전 분리·무접촉**: 기존 B2C 프론트(MonitorPage·SortersPage·기존 Layout 동작) + 백엔드 **0 변경**. B2B 페이지·라우트·
   Context·UI 프리미티브(Dialog/Toast) **추가만**.

## Non-Goals (S-B2B-2b 범위 밖)

- 백엔드 변경 일체(B2B-2a 병합분을 **소비만**). `/api/test-data/*` 계약·아카이브 서비스·마이그레이션은 이미 존재(불변).
- B2B-3 후속 페이지: 로그 조회 · 결과 3-way 비교 · 박스 조회 · 설정(자동생성/인쇄 설정) — **disabled phase 배지로만 예고**.
- 자동생성 전체(`auto_generate_config` 등) — 미이식 확정(불변).
- **A4 라벨 인쇄 + 드래그/Shift/Ctrl 고급 다중선택**의 범위 귀속은 **Q-A 게이트**(아래) — 확정 후 반영.

## Scope 게이트 (사용자 확정 필요 — Question)

- **Q-A [인쇄 + 고급 다중선택 범위] — 권장: (a) 2b 는 코어, 인쇄·고급선택은 2c 로 분리**
  - 배경: 이번 스프린트 표면이 이미 크다(토글+Context+localStorage / Dialog·Toast 신규 / 3분할 페이지 / 요약·상세 그리드 /
    생성·업로드·reset·delete / 아카이브 필터). **A4 인쇄**(팝업·로컬 JsBarcode vendoring·폐쇄망 CSP·mm 정밀 인쇄 CSS·듀얼바코드·
    XSS DOM 조립)와 **드래그/Shift/Ctrl+우클릭 컨텍스트 메뉴**는 `docs/B2B-DATAGEN.md §4.4·§4.6` 상 구현량이 가장 크고 코어와 직교.
  - (a) **[권장]** 2b = 코어(토글 + Context + 생성/업로드/요약/상세 + **체크박스 다중선택** + reset/delete + **아카이브 필터** +
    Dialog/Toast). **A4 인쇄 + 드래그/Shift/Ctrl+컨텍스트 메뉴 → S-B2B-2c 후속.** 근거: 인쇄는 격리된 최대 복잡 조각이고,
    고급 포인터 선택의 유일 기능 목적(행 선택→reset/delete/인쇄)은 **체크박스 컬럼으로 이미 충족**. 코어를 더 빨리 검증·de-risk.
  - (b) 2b = 전부 포함(원본 충실 완전 이식 — 인쇄·고급선택 포함). 리스크: 최대 표면이 단일 5-iter cap 아래. 브라우저 E2E 가
    인쇄 팝업 + 외부 CDN 요청 0 단정까지 커버해야 함.
  - (c) 2b = 코어 + 고급선택(드래그/Shift/Ctrl+컨텍스트 메뉴)까지, **A4 인쇄만** 2c 로. 포인터 선택 UX 를 코어로 볼 때의 중간안.
  - **권장 (a)**. 아래 Implementation Scope 는 (a) 기준으로 작성하고, 인쇄·고급선택 항목은 `[Q-A]` 로 표시 — 확정 값에 따라 편입/이연.

## Implementation Scope (Generator가 할 일 — WHAT)

1. **UI 상태 Context**: `UiModeProvider`(React Context + localStorage) — `mode`(`b2c`|`b2b`) + `bizDay` + `autoRefresh`(+간격).
   화이트리스트 가드로 손상 localStorage 값 폴백. `main.tsx` Provider 계층(`QueryClientProvider > BrowserRouter`)에 삽입.
2. **B2C/B2B 토글**(`docs/B2B-DATAGEN.md §5`): `Layout.tsx` 의 하드코딩 `NAV` 를 **모드별 2세트** + 헤더 타이틀을 **모드/활성
   페이지 기반 동적**으로. 토글 컨트롤(헤더/사이드바). **disabled+phase 배지 패턴 재사용**. 헤더에 **bizDay(native `<input type="date">`)
   + autoRefresh 토글(+간격)** 추가(B2B 화면용). 기존 B2C 라우트·페이지·SignalR lifecycle 동작 보존.
3. **라우팅**(`App.tsx`): `/data-generator` 라우트 추가. `/` 리다이렉트를 **활성 모드 기본 페이지**로(b2c→`/monitor`,
   b2b→`/data-generator`). `*` 폴백 동작 보존.
4. **프론트 인프라(신규·최소)**: shadcn-style **확인 다이얼로그**(reset/delete danger) + 경량 **토스트**(success/warning/error)
   — `components/ui` 에 추가. 원본 `UiContext`(비차단 토스트 + await confirm) 개념 재현. 무거운 라이브러리 금지.
5. **test-data API 클라이언트/훅**: `/api/test-data` BASE 별도 클라이언트(POST body·DELETE body·multipart·query) 또는 TanStack
   Query 훅. **성공 판정 = `res.ok && body.status==="S"`**(200 F·400 은 실패 토스트로 `body.message` 노출 — `docs/B2B-DATAGEN.md §7.1`).
   기존 `/api/monitor` 클라이언트 무접촉.
6. **DataGenerator 페이지**(`/data-generator`, `docs/B2B-DATAGEN.md §4·§7.1`): 3분할 레이아웃 —
   - 좌: **생성 폼**(전역 bizDay 표시 + 배치 + 슈트범위(힌트 "쉼표 구분, 범위는 하이픈") + 바코드개수, Enter 제출, 미입력 경고 토스트,
     성공 시 요약 리로드 + 폼 리셋) + **엑셀 업로드**(`accept=".xlsx,.xls"`, multipart `file`, 성공/실패 토스트).
   - 중: **요약 그리드**(날짜·배치·수량·수신시간, 컬럼 텍스트 필터, 행 체크박스, 행 클릭 시 상세 로드·선택상태 초기화).
   - 우: **상세 그리드**(바코드·슈트·투입상태·투입시간·분류상태·분류시간, 컬럼 필터, 행 체크박스 다중선택).
   - **수신 초기화**: 요약 체크 배치들 상세 `Promise.all` 병렬 조회 → id 취합 → `POST /reset` (확인 다이얼로그·danger).
   - **삭제**: 상세 체크 행 → 확인 다이얼로그(danger) → `DELETE /api/test-data`(ids). 성공 토스트.
   - `[Q-A]` **드래그/Shift/Ctrl 다중선택 + 우클릭 컨텍스트 메뉴**(§4.4) — Q-A (b)/(c) 확정 시 편입, (a) 면 2c 이연.
7. **아카이브 필터 UI**(§3.4·§4.5): 상세 그리드에 `active|all|archivedOnly` 3상태 토글 → detail 조회 `archived` 전달. 기본 active.
   reset/delete 직후 archived 행을 archivedOnly 로 확인.
8. `[Q-A]` **A4 라벨 인쇄**(§4.6): 체크된 상세 행 → 팝업 A4 라벨(99.14×67.48mm·2×4·듀얼바코드·XSS DOM 조립),
   **로컬 `frontend/public/JsBarcode.all.min.js` vendoring**(외부 CDN 금지·폐쇄망) + `npm i jsbarcode`. Q-A (b)/... 확정 시 편입, (a)/(c)... 이연.

> 기술 세부(컴포넌트 분해·훅 구조·상태 형상·TanStack Table 채용 여부)는 **Generator 재량**. 계약은 형상·완료조건·검증만 고정.

## Evaluation Criteria (Full-stack — 가중치)

프론트 표면이 주 대상이므로 Web/UI 기준을 프론트에, 통합 기준을 계약 소비에 적용.
1. **Integration Quality (★★★)**: 프론트가 delivered `/api/test-data/*` 계약(§7.1)에 정확 정합 — 200 F/400 실패 시맨틱을
   실패로 표면화, 필드명(camelCase)·`archived` 파라미터 정확. UI→API→DB→UI 데이터 흐름 일관.
2. **Per-layer Quality — 프론트 (★★★, Web/UI)**: Design Quality(디자인 토큰 일관·`docs/DESIGN-airbnb.md` 정합) + Originality
   (AI slop 아님·의도적 밀집 운영툴 정서) + 토글/그리드/다이얼로그/토스트의 응집.
3. **Craft (★★)**: 타입 안전(`tsc --noEmit` 0)·`eslint .` 0·콘솔 error/pageerror 0·React dev-mode warning 0·컬럼 필터
   버블링 처리·localStorage 손상값 가드·요청 취소(AbortController/refetch)·XSS 방지(`[Q-A]` 인쇄 편입 시 DOM 조립).
4. **Functionality (★★)**: 사용자가 토글로 메뉴 세트를 바꾸고, 생성/업로드/조회/reset/delete 를 완수하며, 아카이브 필터로
   삭제분을 archivedOnly 에서 확인. 기존 B2C 페이지(monitor/sorters) 회귀 0.

## Completion Conditions (Evaluator PASS 최소 조건)

- [ ] **토글**: B2C↔B2B 전환 시 사이드바 **메뉴 세트 · 헤더 타이틀 · 기본 진입 경로**가 바뀌고, mode/bizDay/autoRefresh 가
      localStorage 로 새로고침 후에도 유지. 백엔드 모드 게이트 없음.
- [ ] **회귀 0**: 기존 B2C 페이지(`/monitor`·`/sorters`) 렌더·라우팅·SignalR·폴링 동작 보존. 백엔드 diff 0(빌드·기존 테스트 스위트 GREEN 불변).
- [ ] **생성/관리 플로우**: `/data-generator` 에서 생성 폼 제출(라운드로빈)·엑셀 업로드·요약/상세 조회·체크박스 다중선택·
      reset(확인 다이얼로그)·delete(확인 다이얼로그) 동작. 200 F/400 실패가 실패 토스트로 표면화(`body.message`).
- [ ] **아카이브 필터**: 상세 `active|all|archivedOnly` 전환 시 delete/reset 한 로그·결과가 **archivedOnly 에만** 노출(active 미노출).
      (사수 요구 "삭제해도 보여줘" 가시 — 스크린샷 증거.)
- [ ] `[Q-A]` (편입 확정 시) 인쇄 팝업이 **로컬 `/JsBarcode.all.min.js` 만** 로드(network 외부 CDN 요청 0)·A4 규격 렌더 /
      드래그·Shift·Ctrl 선택 + 우클릭 컨텍스트 메뉴 동작.
- [ ] **정적 게이트**: `tsc --noEmit` 0 · `eslint .` 0 · 브라우저 콘솔 error/pageerror 0 · React dev-mode warning 0.

## Parallel Modules

N/A (single module) — 토글/Context/Layout·페이지·UI 인프라가 공유 파일(`main.tsx`·`App.tsx`·`Layout.tsx`·`components/ui`)을
상호 의존하며 경계-청정 분할 불가. 기본 1 Generator.

## Evaluation Dimensions

functional only (Web/UI) — 단일 프론트 표면. 보안/성능 별도 차원 불요(백엔드 무접촉·읽기중심 관리툴, 폐쇄망). 기본 1 Evaluator.

## Detected Project Type: Full-stack

(레포 신호: `frontend/`(브라우저 진입 SPA) + `backend/src/Wcs.Api`(서버 라우트/컨트롤러)가 동일 레포에 공존 → Full-stack.
단, **본 스프린트의 변경 표면은 프론트 전용**이며 백엔드는 소비만 한다.)

## Verification Scenarios (Full-stack — mandatory)

> Evaluator 는 **fresh evidence 의무**: 백엔드(`dotnet run --project backend/src/Wcs.Api`) + 프론트(`npm run dev`) 동시 기동
> 후 Playwright 로 직접 실행한 출력(번호 스크린샷 + `console.log`)으로 판정. 포트는 `.claude/ports.local.json`(orchestrator 할당)에서
> 읽어 URL 구성(하드코딩 금지). ⚠ 백엔드 기동 시 COM1 실 PLC 폴링 시도 가능하나 읽기 무해 — **IF-09/10 트리거 금지**. B2B 화면은
> test-data(DB)만 쓰므로 소터 OFFLINE 이어도 검증 가능.

### 프론트 Web/UI 시나리오 (이 스프린트가 건드리는 표면)

- **각 표면 기본 상태**:
  1. B2C 모드(기본 진입) — 사이드바에 기존 B2C 세트(모니터링·3DS 워드·운영제어 F3 disabled 배지), 헤더 타이틀 B2C. (회귀 보존 확인)
  2. B2B 모드 — 사이드바에 B2B 세트(데이터 생성 활성 + 로그/비교/박스/설정 disabled phase 배지), 헤더 타이틀 "데이터 생성" + bizDay·autoRefresh 컨트롤.
  3. `/data-generator` 기본 — 3분할(좌 생성폼+업로드 / 중 요약 그리드 / 우 상세 그리드), 데이터 로드 전 그리드 비어있음.
- **이 스프린트가 도입하는 대체 상태들**:
  4. 토글 전환 B2C→B2B, B2B→B2C — 메뉴 세트·타이틀·기본 랜딩 변경 + 새로고침 후 localStorage 유지.
  5. 요약 행 선택 → 우측 상세 그리드 로드(populated).
  6. 체크박스 다중선택(체크 행 하이라이트). `[Q-A]` 편입 시: 드래그/Shift/Ctrl 선택 + 우클릭 컨텍스트 메뉴 열림.
  7. 아카이브 필터 3상태(active / all / archivedOnly) — 상세 그리드 내용 차이.
  8. 확인 다이얼로그 열림(reset/delete danger).
  9. 토스트 표출(success / 검증-warning / error).
- **관련 빈/에러 상태**:
  10. summary/detail 0건 → 빈 상태 메시지. generate 검증 실패(예: barcodeCount 0·잘못된 chuteNos)·detail bizDay/batch 누락(400)·
      upload 잘못된 파일 → 각각 실패 토스트(`body.message`). (Network 4xx/200 F 는 앱이 의도적으로 표시 — 콘솔 error 아님을 명시.)
- **다크 모드 변형**: **N/A** — 프로젝트는 단일 라이트 테마(index.css "단일 테마, 다크모드 없음"). 원본 헤더 다크모드 토글 미이식.
- **변경 후 핵심 상호작용 흐름**: 생성 폼(슈트범위+개수) 제출 → 성공 토스트 + 요약에 새 배치 노출 → 요약 행 클릭 → 상세에 라운드로빈
  배분된 슈트별 행 노출.

### 백엔드 Backend/API 시나리오 (이 스프린트가 건드리는 표면)

- **이 스프린트가 수정하는 엔드포인트**: **없음** — 백엔드 무접촉. 프론트가 **소비**(수정 아님)하는 계약: `POST /generate` ·
  `GET /summary` · `GET /detail` · `POST /reset` · `DELETE /api/test-data` · `POST /upload`(형상·시맨틱 `docs/B2B-DATAGEN.md §7.1`).
- **회귀 게이트**: `dotnet test backend/Wcs.sln` — 기존 전체 스위트(B2C·B2B-1·B2B-2a) **GREEN 불변**(코드 diff 0으로 자연 보존, Evaluator 확인).
- **소비 계약 준수(E2E 로 간접 검증)**: 200 F/400 실패 시맨틱을 프론트가 실패로 표면화(happy=`status:"S"`, error=400·200 F → 토스트).

### 계층 횡단 E2E 데이터 흐름 (2개 이상 계층)

- **E2E-1 생성**: UI 생성 폼 입력 → `POST /api/test-data/generate` → DB insert(라운드로빈) → `GET /summary`·`/detail` 리로드 →
  화면 반영(frontend → API → DB → frontend). 라운드로빈 슈트 배분이 상세에 반영됨을 스크린샷으로 확인.
- **E2E-2 아카이브 가시(사수 요구)**: 상세 행 선택 → `DELETE`(또는 요약 선택 → `POST /reset`) → 연관 로그/결과 `archived_at` 마킹 →
  아카이브 필터 `archivedOnly` 전환 시 그 행이 archivedOnly 에만 노출·active 에는 미노출(frontend → API → DB → frontend). 스크린샷 증거.

## Risks & Mitigation

- **무접촉 위반(최대 리스크)**: Layout/App/main 토글 개편이 기존 B2C 라우트·SignalR·폴링 동작을 바꾸면 안 됨 → 기존 페이지
  렌더·라우팅 회귀 시나리오(1)로 확인. 백엔드 파일 diff 0.
- **응답 시맨틱 오인**: 단순 `res.ok` 로 200 F 를 성공 처리하면 사수 요구 위반 → §7.1 성공 판정(`res.ok && status==="S"`) 강제·시나리오(10).
- **localStorage 손상값**: mode/bizDay/autoRefresh 파싱 실패 시 화이트리스트 폴백(앱 크래시 금지).
- **신규 의존 남발**: Dialog/Toast 는 shadcn-style 자작, 날짜는 native `<input type="date">`. `[Q-A]` 인쇄 편입 시에만 `jsbarcode`
  로컬 vendoring(CDN 금지·CSP).
- **폐쇄망 CSP**(`[Q-A]` 인쇄): 인쇄 팝업이 외부 CDN 을 참조하면 사내망에서 로드 실패 → 로컬 `/JsBarcode.all.min.js` 만·network 0 단정.
- **스코프 과대**: Q-A (b) 전부 포함 시 단일 5-iter cap 초과 위험 → (a) 권장(코어 우선, 인쇄·고급선택 2c 분리).

## Planner self-check

- [x] delivered 2a API 실측(`TestDataController`·`TestDataDtos`·`TestDataService`·`ApiResponse`) → 6 엔드포인트 형상·**200 F/400 실패
      시맨틱**·`archived` 파라미터·camelCase 필드를 `docs/B2B-DATAGEN.md §7.1`로 보강 캡처. 원본 화면 인쇄 규격(§4.6: 99.14×67.48mm·
      2×4·여백·로컬 JsBarcode)·다중선택 인터랙션(§4.4)이 원본 `DataGenerator.jsx`와 **일치**함을 grep 대조 확인(수정/커밋 없음).
- [x] 현 프론트 실측(`Layout.tsx` 하드코딩 NAV+타이틀 / `App.tsx` Routes / `main.tsx` Provider·전역 스토어 부재 / `components/ui`
      Dialog·Toast 부재 / index.css **단일 라이트 테마·다크모드 없음** / vite proxy `/api`→:5080 / `frontend/public` 부재 / jsbarcode 미설치)
      → §7.2·§7.3 보강 + Implementation Scope·검증 시나리오에 반영.
- [x] 다크모드 슬롯 = **N/A(근거: 프로젝트 단일 라이트 테마)** 로 정직 표기 — 원본 다크모드 토글 미이식.
- [x] Q-A(인쇄·고급선택 범위) 를 대안 3안 + 권장(a: 코어 우선·인쇄/고급선택 2c 분리)으로 게이트. Implementation Scope 는 (a) 기준,
      인쇄·고급선택 항목은 `[Q-A]` 로 표시(확정 값에 따라 편입/이연). 기술 세부는 Generator 몫.
- [x] Parallel Modules = N/A(공유 파일 상호의존) · Evaluation Dimensions = functional only → 기본 1/1/1.
- [ ] **사용자 확정 대기(Phase 1→2 게이트)**: Q-A(인쇄+고급 다중선택 범위 = 2b 코어만 vs 전부 vs 코어+고급선택). 확정 후 Generator 착수.

> Planner self-check — Detected project type: Full-stack. Required scenario slots: 8 (프론트 Web/UI: 기본상태·대체상태·빈/에러상태·
> 다크모드(N/A)·핵심흐름 / 백엔드: 수정엔드포인트(없음-무접촉)·소비계약&회귀 / E2E: 계층횡단 2건). All slots filled: yes.

── ★ 사용자 확정 (2026-07-08, Phase 1→2 게이트) ──────────────────────────────
Q-A: A4 라벨 인쇄 + 드래그/Shift/Ctrl 고급 다중선택 → **2c로 연기**. 
이번 S-B2B-2b 스코프 = B2C/B2B 토글(Context+localStorage: mode+bizDay+autoRefresh) + Layout 통합(B2B 메뉴 추가·기존 B2C 메뉴 무접촉) + /data-generator 페이지(생성폼 슈트범위+바코드개수·엑셀업로드·요약/상세 그리드·삭제/초기화·**체크박스 다중선택**·아카이브 필터 active|all|archivedOnly) + 최소 신규 infra(shadcn-style Dialog/Toast·native date input) + lib/api.ts test-data API(성공판정 res.ok && body.status==="S").
2c(후속): A4 라벨 인쇄(로컬 JsBarcode 벤더링·폐쇄망 CSP·A4 2×4 정밀치수·듀얼바코드) + 드래그/Shift/Ctrl 고급 포인터 선택.
무접촉: 기존 B2C 프론트(MonitorPage·SortersPage·기존 Layout 동작)·백엔드 0 변경. B2B 페이지·라우트·Context 추가만.
