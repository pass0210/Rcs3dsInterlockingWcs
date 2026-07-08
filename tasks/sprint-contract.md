# Sprint Contract — S-B2B-3b (조회 프론트엔드: 로그·비교·박스 3화면)

> Planner Subagent · 2026-07-08
> B2B 조회 프론트엔드: S-B2B-3a(PR #43)에서 병합된 읽기 전용 조회 API를 소비하는
> 3개 화면(로그 조회 · 결과 비교 · 박스 조회)을 구현하고, 현재 비활성인 B2B 내비 항목 3개를 점등한다.
> **백엔드 무접촉**(3a 완료·병합됨). **설정(Settings) 화면은 이번 스프린트에서 만들지 않는다**(계속 비활성).

---

## Questions for user (제안 기본값 — 이의 없으면 그대로 진행)

아래는 실질적 분기점이며, 각각 **권장 기본값**을 제시한다. 모두 비차단 — 사용자가 별도 지시 없으면 기본값으로 확정한다.

1. **로그 화면 레이아웃 (탭 vs 2열)**
   - 원본(`PROGRAM_STRUCTURE.md §6.2`)은 **탭이 아니라 투입·분류 2열 병렬 카드**(INPUT/SORT 항상 동시 표시, api-calls 미포함)다.
   - 그러나 이번엔 로그 종류가 3개(투입/분류/API 호출 이력)이고, 투입↔분류 대조 목적은 별도 "결과 비교" 화면이 담당한다.
   - **권장 기본값: 탭 방식** — `투입(INPUT) / 분류(SORT) / API 호출 이력` 3탭(기존 `components/ui/tabs.tsx` 재사용). 3종을 한 화면에 깔끔히 수용.

2. **API 호출 이력(E3 `/api/logs/api-calls`)을 로그 화면에 포함할지**
   - 원본 Logs 화면엔 없었으나, 본 태스크는 6개 엔드포인트 전부 소비(client fn 포함)를 명시.
   - **권장 기본값: 포함** — 로그 화면의 3번째 탭으로. (E3는 `date` 파라미터만 받고 `archived` 없음 — 아카이브 필터는 이 탭에서 숨김/비활성.)

3. **컬럼 필터·통합검색 재현 수준**
   - 원본은 컬럼별 텍스트 필터 + 통합검색(모든 필드 OR 매칭, Esc 초기화) + (비교 화면) 상태 필터 버튼 3종(전체/불일치/누락, 카운트 배지)을 가짐.
   - **권장 기본값: 재현** — 컬럼별 텍스트 필터(기존 `FilterCell` 재사용) + 화면당 통합검색 입력 1개 + 비교 화면 상태 필터(전체/불일치/누락 + 카운트 배지). **미재현**(스코프 밖): 우클릭 컨텍스트 메뉴·드래그 다중선택(원본 DataGenerator 전용, 로그/비교/박스엔 원래 없음), 라벨 인쇄(설정과 함께 이연).

---

## Goal

S-B2B-3a에서 병합된 6개 읽기 전용 조회 엔드포인트를 소비하는 **3개 B2B 프론트 화면**을 만들고,
현재 비활성(`enabled:false`, `to:'#'`, `phase:'B2B-3'`)인 B2B 내비 항목 3개(로그 조회·결과 비교·박스 조회)를
실제 라우트로 점등한다. 기존 프론트 인프라(UI 컴포넌트·UiModeProvider·TanStack Query·테이블/상태 컴포넌트)를
그대로 재사용하며, 새 무거운 UI 라이브러리를 도입하지 않는다. 폐쇄망(외부 CDN·외부 fetch 금지)·단일 라이트 테마
·기존 스타일/컨벤션(한글 주석·컴포넌트 관용구)을 준수한다. **백엔드는 손대지 않는다.**

---

## Implementation Scope

### A. 내비 점등 (Layout.tsx — 외과적 수정)
- `frontend/src/components/Layout.tsx` `NAV_SETS.b2b`에서:
  - `로그 조회`·`결과 비교`·`박스 조회` 3개를 `enabled:true` + `phase:null` + 실제 `to` 경로(예: `/logs`, `/comparison`, `/boxes`) + 적절한 `subtitle`로 활성화.
  - `설정`은 **그대로 비활성 유지**(`enabled:false`, `to:'#'`, `phase:'B2B-3'` 유지 — 배지 라벨 변경 불요).
- **b2c NAV_SET·StatusRail·ModeToggle·헤더 컨트롤·PollIndicator는 무접촉**(회귀 0). B2C 동작/외형 변경 금지.

### B. 라우트 추가 (App.tsx)
- `frontend/src/App.tsx`의 `<Route element={<Layout/>}>` 하위에 3개 라우트 추가(`/logs`, `/comparison`, `/boxes` — Layout `to`와 일치). 기존 라우트(`/monitor`·`/sorters`·`/data-generator`·`ModeHome`·`*` fallback) 무접촉.

### C. 타입 API 클라이언트 (신규 lib 모듈)
- 6개 엔드포인트에 대한 typed fetch 클라이언트 + TanStack Query 훅 추가(`frontend/src/lib/testData.ts` 관용구 미러 — 조회는 throw→Query 에러 표면화, camelCase DTO 미러). 응답 형상은 백엔드 DTO(`backend/src/Wcs.Api/B2B/QueryDtos.cs`)와 **정확히 일치**:
  - E1 `GET /api/logs/input?bizDay=&archived=` → `TestLogRow[]`
  - E2 `GET /api/logs/sort?bizDay=&archived=` → `TestLogRow[]`
  - E3 `GET /api/logs/api-calls?date=` → `ApiCallLogRow[]` (최대 500건, archived 없음)
  - E4 `GET /api/logs/export?bizDay=&batch=` → **xlsx 바이너리 다운로드**(blob + `Content-Disposition` 파일명 파싱 → `<a download>`; 오류 시 400 JSON `message`를 토스트로 표면화). bizDay 필수(전역 bizDay가 항상 존재).
  - E5 `GET /api/test-data/comparison?bizDay=&archived=` → `ComparisonRow[]`
  - E6 `GET /api/boxes?bizDay=&batch=` → `BoxRow[]` (각 행에 `items: BoxItemRow[]`)
- 아카이브 필터 3상태(`active|all|archivedOnly`)는 기존 `ArchiveFilter` 타입(`lib/testData.ts`)과 동일 어휘 재사용.
- 전역 `bizDay` + `autoRefresh/refreshInterval`(UiModeProvider)를 데이터 생성 화면과 **동일하게** 존중(`refetchInterval = autoRefresh ? refreshInterval : false`, `placeholderData: keepPreviousData`).

### D. 화면 1 — 로그 조회 (`/logs`)
- 탭 3개: **투입(INPUT)** / **분류(SORT)** / **API 호출 이력**(권장 기본값 §Q1·Q2).
- 투입/분류 탭: 전역 bizDay 기반 조회 + **아카이브 필터 컨트롤**(active|all|archivedOnly) + 컬럼별 텍스트 필터(`FilterCell` 재사용) + 통합검색 1개. 표시 컬럼은 `TestLogRow` 필드(바코드·인덕션/슈트(`equipmentNo`)·PID·상태·사유·로그시각·(파생)슈트·수신시각·보관여부).
- API 호출 이력 탭: `date`(전역 bizDay를 date로 매핑, "전체" 옵션 허용) 기반, `ApiCallLogRow` 표시(엔드포인트·메서드·상태코드·소요ms·호출시각·client ip·error·(긴) req/res 본문은 truncate/monospace/wrap로 폭 처리). 아카이브 필터 비노출.
- **Excel 다운로드 버튼**: E4 호출(전역 bizDay + 선택적 batch). 다운로드 트리거 + 실패 토스트.
- 로딩/에러/빈 상태는 기존 `StateMessage`(`LoadingRow`/`ErrorRow`/`EmptyRow`) 재사용.

### E. 화면 2 — 결과 비교 (`/comparison`)
- 전역 bizDay 기반 `ComparisonRow[]` 조회 + 아카이브 필터.
- 3단(투입/분류/결과) 구성의 한 행 표시 + 상태 시각 구분:
  - 행: `isMissing`→경고색(누락), `!isMatch && hasSort && hasResult`→위험색(불일치).
  - 셀: `hasInput/hasSort/hasResult`가 false인 칸은 "누락 셀" 시각 표시.
  - 배지/범례: 일치=초록 / 누락=회색 / 불일치=빨강.
- 상태 필터 버튼 3종(전체 / 불일치[`isMatch===false && hasSort && hasResult`] / 누락[`isMissing===true`]) + 각 카운트 배지. 컬럼 필터 + 통합검색.

### F. 화면 3 — 박스 조회 (`/boxes`)
- 전역 bizDay(필수) 기반 `BoxRow[]` 조회 + 선택적 batch 필터.
- **마스터-디테일**: 좌측 박스 목록(박스번호·슈트·마감시간·생성시각·내품수), 좌측 행 클릭 → 우측에 해당 박스 `items`(바코드·수량) 표시.
- batch 필터 변경 시 선택 박스 해제(좌우 불일치 방지).
- 로딩/에러/빈 상태 + (선택 전) "박스를 선택하세요" 안내.

### 비스코프 (건드리지 않음)
- **설정(Settings) 화면 — 만들지 않음.** 자동생성 미이식 + 인쇄 B2B-2c 이연 → 이식할 백엔드 설정 계약 없음. 내비 항목은 비활성 유지.
- 백엔드(컨트롤러·서비스·DTO·마이그레이션·appsettings) 일체 무변경.
- B2C 화면/내비/StatusRail/SignalR 허브 로직 무변경.
- 라벨 인쇄, 우클릭 컨텍스트 메뉴, 드래그 다중선택 — 이번 스코프 밖.

---

## Evaluation Criteria (Web/UI dimension — Full-stack 프론트 계층)

가중치는 프로젝트 성격(기존 디자인 시스템 준수형 기능 화면)에 맞춰 조정:

1. **일관성/디자인 시스템 준수 (★★★)** — 색·타이포·간격·컴포넌트가 기존 화면(DataGenerator·Monitor)과 한 몸으로 읽히는가. CLAUDE.md "Consistency Over Preference": 새 시각 언어 발명이 아니라 기존 `ui/*`·`StateMessage`·`FilterCell` 관용구 재사용. AI-slop 패턴(무근거 그라디언트/이모지/일관 없는 여백) 없음.
2. **통합 품질 (★★★)** — 프론트 타입이 백엔드 DTO(camelCase `TestLogRow`/`ApiCallLogRow`/`ComparisonRow`/`BoxRow`/`BoxItemRow`) 형상과 정확히 일치. 아카이브 필터·bizDay·batch 파라미터가 계약대로 전달. 6개 엔드포인트 실 왕복 정상.
3. **Craft (★★)** — 타입 계층/간격 일관, 로딩·에러·빈 상태 3종 모두 처리(무한 스피너/흰 화면 금지), 긴 값(api-call 본문) 폭 처리, 비교 화면 색/배지 대비 충분, 접근성(aria-label·checkbox 관용구) 유지.
4. **기능성 (★★)** — 사용자가 내비에서 3화면을 **발견·도달**할 수 있고(고아 페이지 아님), 각 화면의 필터/탭/마스터-디테일/Excel 다운로드가 실제로 동작. 전역 bizDay·자동 새로고침 존중.

**절대 게이트(어느 하나라도 실패 시 FAIL):**
- 폐쇄망: 외부 CDN/폰트/스크립트/원격 fetch **0건**(네트워크 로그로 확인 — 앱 자신의 `/api`·`/hubs` 외 외부 호스트 요청 금지).
- B2C 무접촉: b2c 내비/StatusRail/모니터·소터 화면 회귀 0.
- 설정 화면 미생성 + 설정 내비 비활성 유지.
- 콘솔 0 에러(아래 Completion Conditions의 console 캡처 규칙).

---

## Completion Conditions (Evaluator PASS 최소 조건)

1. **정적 검사(프론트)** — Evaluator가 직접 재실행:
   - `npm run build`(tsc + vite 빌드) 클린(에러 0).
   - 프로젝트 설정 린터(eslint) 클린(에러 0). type-check 클린.
   - 기록: `tasks/sprint-feedback.md`에 pass/fail/not-configured.
2. **기존 테스트 회귀 0** — `dotnet test backend/Wcs.sln` 전량 GREEN(프론트 전용 변경이나 백엔드 무접촉 실증). 프론트에 테스트 러너가 구성돼 있으면 함께 실행.
3. **브라우저 클릭스루(필수·Playwright)** — 아래 Verification Scenarios의 각 시나리오를 navigate→click→fill→assert 사이클로 재현, 번호 스크린샷(`screenshots/S-B2B-3b_{YYYYMMDD-HHMMSS}/01-*.png …`) 저장하고 **스크린샷을 실제로 판독**.
   - 포트 source-of-truth: 프론트 dev 서버 포트는 `frontend/vite.config.ts`의 `server.port`(현재 5173) 또는 `.claude/ports.local.json`(존재 시). `/api`는 vite proxy로 백엔드(:5205)에 연결.
   - 백엔드는 **반드시 기동**(검증 스킵/코드리뷰 대체 금지). 화면에 표시할 대표 B2B 데이터(투입/분류 로그·work_result·박스+내품·api_call_log)가 있는 상태여야 populated + 비교 일치/불일치/누락 + 빈 상태를 모두 보일 수 있다.
   - **현장 DB 오염 금지(lessons 2026-07-03 상시 적용)**: 실 SqlServer 현장 DB에 붙이지 말 것. 시드된 scratch DB(별도 이름) 또는 Sim/테스트 데이터로 기동하고, `ASPNETCORE_ENVIRONMENT=Production`(자동 시드 미발동) 명시. 자동 시드 의존 금지.
4. **콘솔/dev 경고 캡처(BLOCKING)** — `page.on('console')`·`page.on('pageerror')` 등록해 `screenshots/S-B2B-3b_.../console.log` 저장. React dev 경고(key/validateDOMNesting/update-depth 등)·pageerror·처리되지 않은 4xx/5xx = BLOCKING FAIL(앱이 의도적으로 처리·표시하는 케이스만 명시 예외 — sprint-feedback.md에 의도 명시).
5. **폐쇄망 확인** — 브라우저 네트워크 요청 목록에 앱 출처(`/api`·`/hubs`·정적 자산) 외 외부 호스트 요청 0건.
6. **브랜치 규율(process)** — develop 직접 커밋 금지. feature 브랜치에서 작업(현재 base = `develop`).

---

## Parallel Modules
N/A (single module). 3개 화면이 파일 단위로는 독립적이나 `Layout.tsx`·`App.tsx`·신규 lib 클라이언트를 공유 편집하므로 fan-in 병합 오버헤드가 이득을 상쇄. 기본 1 Generator.

## Evaluation Dimensions
functional only. 보안/성능 민감 백엔드 표면 무변경(읽기 전용 3a는 별도 검증 완료). 단일 기능/UI 차원.

---

## Detected Project Type: Full-stack

프로젝트 신호: 브라우저 진입점(`frontend/` React SPA + `index.html`) **와** 서버 라우트/컨트롤러(`backend/src/Wcs.Api/Controllers/**`)가 같은 레포에 공존 → Full-stack. 단, **이번 스프린트의 변경 표면은 프론트엔드 전용**(3화면 + 내비 점등 + 라우트 + api 클라이언트 추가)이며 백엔드는 무접촉(3a 병합분 소비만).

---

## Verification Scenarios (Full-stack — 필수)

### 슬롯 1 — 프론트엔드 Web/UI 시나리오 (이번 스프린트가 만지는 프론트 표면)

**공통 다크 모드**: **N/A** — 프로젝트는 단일 라이트 테마(다크 모드 미지원, `uiMode.ts`·기존 화면 모두 단일 테마). 다크 변형 검증 대상 없음.

**내비 점등 (Layout)**
- 기본 상태: B2B 모드에서 좌측 내비에 `데이터 생성`(활성) + `로그 조회`·`결과 비교`·`박스 조회`(활성, 배지 없음) + `설정`(비활성, `B2B-3` 배지·클릭 불가)이 보인다.
- 대체 상태: 각 활성 항목 클릭 시 해당 라우트로 이동하고 헤더 타이틀/서브타이틀이 갱신, 활성 표시(inset 브랜드 바)가 붙는다.
- 회귀(무접촉 확인): B2C 모드 토글 시 내비가 `모니터링/3DS 워드/운영 제어(F3 비활성)`로 원상 복귀, StatusRail 정상.

**화면 1 — 로그 조회 (`/logs`)**
- 기본 로드: 전역 bizDay 기준 투입 탭이 로그 행을 표시.
- 대체 상태: (탭 전환) 분류·API 호출 이력 탭으로 전환 시 각 데이터 표시 / (아카이브 필터 변경) active→all→archivedOnly 전환 시 행 집합 변화(보관 행 포함/제외) / (컬럼 필터·통합검색) 입력 시 행 축소 / (Excel 다운로드 트리거) 버튼 클릭 → xlsx 파일 다운로드 발생(파일명·다운로드 이벤트 확인).
- 빈/에러 상태: 데이터 없는 bizDay 선택 시 EmptyRow 표시 / 백엔드 오류(예: 비존재 날짜 400) 시 ErrorRow에 메시지 표시(흰 화면·무한 스피너 아님).
- 핵심 상호작용: 투입 탭 로드 → 분류 탭 전환 → 아카이브 all로 변경(보관 행 등장) → 통합검색으로 축소 → Excel 다운로드 클릭 → 다운로드 성공.

**화면 2 — 결과 비교 (`/comparison`)**
- 기본 로드: 전역 bizDay 기준 3단 비교 표가 행별로 표시.
- 대체 상태: (상태 필터) 전체→불일치→누락 전환 시 표가 필터되고 각 카운트 배지가 갱신 / (아카이브 필터 변경) 행 집합 변화 / (컬럼 필터·통합검색) 축소.
- 시각 구분(핵심): **일치 행**(초록 배지, 3자 존재+슈트 일치), **불일치 행**(빨강, 슈트 다름), **누락 행/셀**(회색·누락 셀 표시)이 스크린샷에서 시각적으로 구별됨을 판독으로 확인.
- 빈/에러 상태: 데이터 없는 bizDay → EmptyRow / 400 → ErrorRow.
- 핵심 상호작용: 로드 → "불일치" 필터 클릭(불일치 행만·배지 카운트 일치) → "누락" 필터 → "전체" 복귀.

**화면 3 — 박스 조회 (`/boxes`)**
- 기본 로드: 전역 bizDay 기준 좌측 박스 목록 표시(우측은 "박스를 선택하세요" 안내).
- 대체 상태: (마스터-디테일 확장) 좌측 박스 행 클릭 → 우측에 해당 박스 내품(바코드·수량) 표시 / (batch 필터 변경) 목록 변화 + 선택 박스 해제(우측 안내로 복귀).
- 빈/에러 상태: 박스 없는 bizDay → EmptyRow / 400 → ErrorRow.
- 핵심 상호작용: 로드 → 박스 A 클릭(내품 표시) → 박스 B 클릭(내품 갱신) → batch 필터 변경(선택 해제 확인).

### 슬롯 2 — 백엔드 Backend/API 시나리오
**N/A (사유)**: 이번 스프린트는 백엔드를 변경하지 않는다. 6개 엔드포인트(E1~E6)는 S-B2B-3a(PR #43, develop 병합)에서 이미 자동 테스트 + 검증 완료(feedback-archive S-B2B-3a 참조). 프론트는 이 계약을 소비만 한다. 단, 슬롯 3의 E2E에서 프론트↔실 엔드포인트 왕복은 실제로 검증한다.

### 슬롯 3 — 계층 교차 E2E 데이터 흐름 (2계층 이상)
- **흐름**: 브라우저(React 화면) → `/api` (vite proxy 또는 단일 포트 wwwroot) → ASP.NET Core 컨트롤러(`LogController`/`BoxesController`/`TestDataController`) → `ILogService`/`ILogExportService`/`IBoxService` → DB. 실제로 기동된 백엔드 + 대표 데이터로 각 화면이 **실 HTTP 응답을 렌더**함을 확인한다(코드 판독·목 데이터 금지).
- 최소 왕복 3종 명시 검증:
  1. 로그 화면 Excel 다운로드: 브라우저 클릭 → `GET /api/logs/export?bizDay=` → 200 xlsx 바이너리 + `Content-Disposition` → 파일 다운로드.
  2. 비교 화면 아카이브 왕복: active vs all 전환 시 백엔드 필터(`archived`)가 반영돼 행 집합이 달라짐(3a의 아카이브 3상태 계약을 프론트가 실제로 소비).
  3. 박스 마스터-디테일: `GET /api/boxes?bizDay=` 응답의 `items` 배열이 우측 디테일에 정확히 표시(중첩 DTO 왕복).

> Planner self-check — Detected project type: Full-stack. Required scenario slots: 3 (Frontend Web/UI scenarios, Backend/API scenarios [N/A with reason], Cross-layer E2E data-flow). All slots filled: yes.
