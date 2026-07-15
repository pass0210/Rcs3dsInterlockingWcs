# Sprint Contract — S-UI-LAYOUT (프론트 레이아웃 정리: 뷰포트 맞춤 + 그리드 내부 스크롤 + 3DS 워드 중복 제거)

> 작성: Planner Subagent, 2026-07-15. 사용자 지시(3건) 기반. 순수 프론트 스프린트(백엔드/스키마/마이그레이션 0 목표).
> 베이스: PR #65까지 병합된 develop 위. WHAT만 기술(구현 방식=HOW는 Generator 재량).
> **이 계약은 Open Questions(§OQ) 사용자 확정 후 착수한다** — 특히 OQ-1(/sorters 운명)·OQ-2(다중 그리드 높이 배분)·OQ-3(b2b 포함 여부)는 스코프를 가르는 결정 사항.

---

## Goal

각 페이지가 데이터량과 무관하게 **뷰포트(브라우저 가시영역) 안에 딱 맞고**, 넘치는 데이터는 **페이지 전체가 아니라 그리드 내부에서만 스크롤**되도록 전 데이터-그리드 페이지의 레이아웃을 일관되게 정리한다. 동시에 `/sorters`(3DS 워드)와 `/ops`(운영 제어)에 **중복 렌더되는 3DS 레지스터 워드 표시를 제거**해 레지스터 값 표시를 `/ops`에만 남기고, 콘텐츠가 빠지는 3DS 워드 메뉴의 라벨·역할을 재정의한다.

---

## 배경 — 코드 실측(착수 전 확인 완료, load-bearing)

### A. 3DS 레지스터 워드 중복은 "실재"하며 문자 그대로의 중복이다
- `/sorters`(`frontend/src/pages/SortersPage.tsx`): 소터 `Select` + `ConnBadge` + **`<WordPanel state={wordState} />`** + `<OpLogTail />`.
- `/ops`(`frontend/src/pages/OpsPage.tsx`): 소터 `Select` + `ReadinessStrip` + `ConnBadge` + **`<WordPanel state={wordState} />`** + `<OpsControls />`.
- 두 페이지가 **동일한 `WordPanel` 컴포넌트**(`frontend/src/pages/sections/WordPanel.tsx` — "3DS 레지스터 워드" 카드: D0/D1/D2/D3/D5/D6 RegTile + D4 비트램프 + Online)를 그대로 렌더한다. `WordPanel` 임포트처는 정확히 이 두 파일뿐(grep 확인).
- 즉 `/ops`는 **이미 레지스터 값을 표시**한다. 따라서 요구 2는 "레지스터를 /ops로 이관"이 아니라 **"/sorters에서 WordPanel을 제거(중복 삭제)"** 이다.
- ★ 리스크 해소: `/ops`의 WordPanel은 이미 `useMonitorState()`(SignalR) + `monitor.sorters.get(destId)`로 완전 결선돼 있다. **이관·이동이 없으므로 새 구독 생성·중복 구독·누수 위험이 애초에 없다** — 한쪽 인스턴스를 삭제할 뿐이다. `useHubLifecycle()`(Layout.tsx)이 앱 수명 동안 연결을 유지하므로 SortersPage에서 WordPanel을 빼도 스트림은 무영향.

### B. `/sorters`에서 WordPanel을 빼면 남는 것
- 소터 `Select` — **WordPanel의 destId만 구동**했다(OpLogTail은 destId를 쓰지 않음). WordPanel 제거 시 **고아**가 된다.
- `ConnBadge` — SignalR 연결 상태 배지.
- `<OpLogTail />`(`frontend/src/pages/sections/OpLogTail.tsx`) — **operation_log 라이브 테일**. 소터 종속 아님(전역). category/level 필터·POLL_CHANGE 옵트인·자동 스크롤·500행 캡·REST 백로그+SignalR append. **앱 전체에서 이 컴포넌트가 사는 유일한 곳이 SortersPage**(grep 확인 — Monitor·Ops에 없음). ⇒ `/sorters`를 통째로 삭제하면 operation_log 테일 기능이 앱에서 사라진다.

### C. 뷰포트/스크롤 현황 — 페이지마다 제각각(이 스프린트가 통일할 "혼재")
- 높이 체인: `index.css`에 `html, body, #root { height: 100% }` 확립됨. `Layout.tsx`의 `<main className="… flex-1 overflow-auto p-5">`가 **현재 페이지 레벨 스크롤 컨테이너** — 콘텐츠가 넘치면 페이지 전체가 스크롤(사이드바+상단 헤더는 main 밖이라 이미 고정).
- 페이지별 처리 방식(불일치):

  | 페이지 | 현재 스크롤 처리 | 문제 |
  |---|---|---|
  | MonitorPage (3탭: 작업/이동중/분류) | sticky thead 테이블, CardContent `p-0`, **max-height 없음** | 무한 증가 → main(페이지) 스크롤 |
  | SortersPage | WordPanel(타일, 캡 없음) + OpLogTail(`max-h-[340px]`) | WordPanel 캡 없음 → main 스크롤 |
  | OpsPage | WordPanel + OpsControls | 내부 스크롤 없음 → 짧은 뷰포트서 main 스크롤 |
  | B2cDataGenPage (마스터-디테일) | master/detail 그리드 `overflow-auto`, **max-height 없음** | 무한 증가 → main 스크롤 |
  | B2cFacilityPage (3단 스택 + 2패널) | 목적지 그리드 캡 없음 · 배정 2패널 `max-h-[440px]` 고정px | 혼재·페이지 매우 김 |
  | LogsPage (b2b, 탭) | `max-h-[calc(100vh-260px)]` 매직넘버 | 헤더 높이 바뀌면 깨짐 |
  | ComparisonPage (b2b) | `max-h-[calc(100vh-280px)]` 매직넘버 | 동 |
  | BoxesPage (b2b, 마스터-디테일) | `max-h-[calc(100vh-220px)]` ×2 | 동 |
  | DataGeneratorPage (b2b, 마스터-디테일) | `max-h-[calc(100vh-220px)]` ×2 | 동 |
  | SettingsPage (b2b, 인쇄 미리보기) | 데이터 그리드 아님 — 이미 flex/min-h-0 | 대상 아님(후보 제외) |

- 요구 1의 목표 패턴(WHAT): main이 **고정 뷰포트-높이 컨텍스트**를 제공하고(페이지 레벨 스크롤 제거), 각 데이터-그리드 페이지가 **세로 flex 레이아웃**이 되어 헤더/툴바/탭바/필터는 **고정**되고 **그리드 본문만 유일한 스크롤 영역**이 된다(sticky thead는 `ui/table.tsx`에 이미 존재). 매직넘버 `calc(100vh-{220,260,280}px)`와 고정 `max-h-[440px]`/`max-h-[340px]`를 flex 기반(`flex-1 min-h-0`류) 규칙으로 대체. **정확한 Tailwind 클래스·헬퍼 프리미티브 도입 여부는 Generator 재량.**

---

## Implementation Scope (Generator가 수행할 것 — WHAT)

### R1. 뷰포트 맞춤 + 그리드 내부 스크롤 (전 데이터-그리드 페이지)
- (R1-a) **공통 레이아웃**: `Layout.tsx`의 `main`이 페이지 레벨 스크롤을 유발하지 않고, 자식 페이지에 **뷰포트에 바운드된 높이 컨텍스트**를 제공하도록 조정. 사이드바·상단 헤더 고정은 유지(회귀 0).
- (R1-b) **페이지 레벨 패턴 적용**: 각 데이터-그리드 페이지를 세로 flex 레이아웃으로 전환 — 페이지 내 **비스크롤 크롬(제목/툴바/탭/필터/상태 배지/액션 버튼)은 고정**, **그리드 본문(테이블 바디)만 overflow 스크롤**. 헤더 행(thead)은 스크롤 중 상단 고정(sticky, 기존 유지).
- (R1-c) 적용 대상(B2C 필수): `MonitorPage`(+ 3개 섹션 WorkData/InFlight/Sorting), `SortersPage`(R2 재정의 후 형태), `OpsPage`, `B2cDataGenPage`, `B2cFacilityPage`.
- (R1-d) b2b 페이지(`LogsPage`·`ComparisonPage`·`BoxesPage`·`DataGeneratorPage`) 적용 여부·범위는 **OQ-3**로 확정 후 결정(매직넘버 calc → flex 패턴 대체가 기본 권고안). `SettingsPage`(인쇄 미리보기·비-그리드)는 대상 제외.
- (R1-e) **다중 그리드 페이지 높이 배분 규칙**은 **OQ-2**로 확정(마스터-디테일=B2cDataGen/Boxes/DataGenerator, 3단 스택+2패널=B2cFacility, 좌우 2패널 각 그리드).
- (R1-f) **매우 작은 뷰포트 최소 높이**: 그리드 본문이 0으로 붕괴하지 않도록 하한 보장(구체 임계·처리는 OQ-2에 종속 — 하한 미만이면 해당 영역만 스크롤).
- 데이터량 불변식: 그리드가 **비었을 때·소량일 때** 페이지가 어색하게 늘어나거나 빈 스크롤이 생기지 않고, **넘칠 때** 페이지가 뷰포트를 넘지 않고 그리드 본문만 스크롤된다(적음/넘침 양극단 모두 검증 — §Verification).

### R2. 3DS 레지스터 워드 표시 중복 제거 (레지스터 값 표시는 /ops에만)
- (R2-a) `SortersPage`에서 **`<WordPanel />` 제거**. (`/ops`는 이미 WordPanel을 렌더하므로 별도 이관 없음.)
- (R2-b) WordPanel 제거로 고아가 된 소터 `Select`(destId 구동 전용) 처리 — **OQ-1의 후보에 종속**(제거/유지/이관).
- (R2-c) `/ops`의 WordPanel·데이터 소스(SignalR 훅)는 **무접촉**(회귀 0). 중복 구독·누수 0(이관이 없으므로 자명하나 Evaluator가 실증).

### R3. 3DS 워드 메뉴 이름/역할 재정의 (또는 제거)
- (R3-a) `Layout.tsx`의 `NAV_SETS.b2c`에서 `/sorters` 항목의 `label`("3DS 워드")·`title`("3DS 워드값")·`subtitle`("D0~D6 레지스터 실시간 관찰"), 및 라우트(App.tsx)·페이지 형태를 **OQ-1 확정안대로** 변경(라벨/역할 재정의 또는 메뉴·라우트 제거).
- (R3-b) 어느 후보든 **고아 페이지·죽은 라우트·네비에서 도달 불가한 컴포넌트가 남지 않아야** 한다(harness: 발견 가능성·네비 연결 필수).

---

## Explicit Non-Goals / 무접촉 경계 (위반 시 사전 보고)
- **백엔드 0**: `backend/**` diff 0, 신규 마이그레이션 0. `git diff backend/` 빈 출력이 게이트. `Wcs.PlcGateway`·`Wcs.Core`·실 PLC·사용자 운영 DB 무접촉. API 계약(`/api/v1/*`·`/api/monitor/*`·`/api/ops/*`) 불변.
- **SignalR/폴링 훅 무변경**: `useMonitorState`/`useHubLifecycle`/`useSorters`/TanStack Query 훅의 동작·구독 로직 변경 금지(레지스터 중복 제거는 UI 컴포넌트 삭제이지 데이터 소스 변경이 아님).
- **기능/데이터 로직 무변경**: OpsControls의 안전 3종 제어, B2cFacility의 오더 할당·셀 설정, 데이터 생성·초기화, 로그/비교/박스 조회 로직 무변경 — **레이아웃/컨테이너 스크롤만** 손댄다.
- **디자인 토큰/테마 무변경**: 단일 라이트 테마(다크 N/A). Airbnb 라이트 팔레트·Card/Table 프리미티브 톤 유지. 신규 색·폰트 도입 금지.
- **공용 그리드 상호작용(S-B2C-GRID-UX) 무회귀**: `useRowSelection`/`context-menu` 드래그 하이라이트·우클릭 4액션·자격 존중이 스크롤 컨테이너 재구성 후에도 정확히 동작(특히 스크롤 발생 시 좌표/드래그 판정·MutationObserver prune).

---

## Open Questions (착수 전 사용자 확정 — 진짜 결정 사항)

### OQ-1 ★ `/sorters`(3DS 워드)의 운명 — 3후보 (요구 2·3 스코프를 가름)
WordPanel 제거 후 SortersPage에 남는 실질 콘텐츠 = **OpLogTail(operation_log 라이브 테일)** + ConnBadge (+ 고아 소터 Select). OpLogTail은 앱 전체에서 여기에만 존재하는 유용한 독립 뷰다.

- **(a) 완전 제거** — 라우트 `/sorters` + NAV 항목 + `SortersPage.tsx` 삭제.
  - 남는 것: 없음. **이동/보존**: OpLogTail을 옮기지 않으면 **operation_log 테일이 앱에서 소멸**. (옮긴다면 어디로? /monitor 4번째 탭 또는 /ops 하단 — 그러면 그건 (c) 변형.)
- **(b) 라벨·역할 재정의(권고)** — `/sorters`를 **운영 로그 페이지**로 재정의.
  - 남는 것: OpLogTail(주 콘텐츠) + ConnBadge(라이브 스트림 상태 근거). **제거**: WordPanel + 고아 소터 Select.
  - NAV 변경: label "3DS 워드" → 예 **"운영 로그"**/"이벤트 로그"/"동작 로그", title/subtitle 동반 변경(예 subtitle "operation_log 실시간 테일 · category/level 필터").
- **(c) /ops로 흡수 후 제거** — OpLogTail을 `/ops` 하단(OpsControls 아래)으로 옮기고 `/sorters` 삭제.
  - 이동: OpLogTail → /ops. **단점**: /ops가 WordPanel+OpsControls+OpLogTail로 길어져 **요구 1(뷰포트 맞춤)과 상충**, 또한 OpLogTail은 소터 비종속인데 /ops는 소터 선택 게이트 뒤에 있어 배치가 어색.

- **Planner 권고 = (b)**. 근거: (1) 진짜 유용한 콘텐츠(operation_log 테일) 보존, (2) 진짜 중복(WordPanel)만 제거, (3) 메뉴에 실질 역할 부여 = 요구 3의 "재정의 또는 제거"에 가장 깔끔한 답, (4) 요구 1(뷰포트 맞춤)과 상충 없음(단일 그리드 페이지로 fit 쉬움). **사용자 확정 필요**: (b) 채택 시 새 메뉴 라벨 문구 지정 요청.

### OQ-2 ★ 뷰포트 맞춤 적용 방식·다중 그리드 높이 배분
- (2-1) **공통 vs 페이지별**: `Layout.tsx`(main)에서 뷰포트 바운드 컨텍스트만 제공하고 **페이지별로 flex 컬럼 채택**(각 페이지 그리드 구성이 달라 완전 공통 래퍼는 부적합) — 권고. 반복되는 "스크롤 카드" 헬퍼 프리미티브 추출은 Generator 재량(중복 감축 목적일 때만).
- (2-2) **마스터-디테일**(B2cDataGen: 상단 2열[짧은 폼 + 마스터 그리드] + 하단 디테일 그리드; Boxes/DataGenerator: 좌우 2열 각 그리드): 두 그리드 높이 배분 규칙? 권고안 = 각 그리드 `flex-1 min-h-0`로 가용 높이 균등 분할(짧은 생성 폼은 자연 높이 고정). **사용자 확정**: 균등 vs 마스터 우선(디테일 더 크게) 등.
- (2-3) **B2cFacilityPage(가장 어려운 페이지)**: 세로 3단(작업자 바 + 목적지 그리드 + 오더 할당 2패널[좌우 2 그리드])이라 한 뷰포트에 다 넣으면 각 영역이 과도하게 작아진다. 후보: (i) 페이지를 뷰포트 캡 + 각 영역 내부 스크롤(영역별 min-height), (ii) **이 페이지만 페이지 스크롤 예외 허용**, (iii) 재구성(탭/접이). Planner 권고 = (i) 시도하되 하한(min-height) 미만이면 (ii)로 폴백. **사용자 확정**: "각 페이지 뷰포트 맞춤"을 이 3단 페이지에도 강제할지, 예외를 둘지.
- (2-4) **main overflow 정책**: 전 페이지 fit이면 `main`을 overflow-hidden으로 둘 수 있으나, (2-3)에서 페이지 스크롤 예외를 허용하면 main은 페이지가 필요 시 스크롤 가능해야 한다. (2-3) 결정에 종속.

### OQ-3 ★ b2b 페이지도 뷰포트 맞춤 대상인가
- 사용자는 "각 페이지"라 했다. b2b(`/data-generator`·`/logs`·`/comparison`·`/boxes`)는 **이미** 매직넘버 `calc(100vh-Npx)`로 내부 스크롤 중(목표를 부분 충족하나 헤더 높이 변화에 취약).
- Planner 권고 = **포함**하되(매직 calc → flex `flex-1 min-h-0` 패턴으로 대체해 일관·견고화), b2b는 별도 토글 모드라 블라스트 반경을 줄이고 싶으면 **후속 단계로 분리** 가능. `SettingsPage`(비-그리드 인쇄 미리보기)는 제외. **사용자 확정**: b2b 포함/제외/후속분리.

### OQ-4 이월 Minor(S-B2C-GRID-UX) 흡수 여부
- 이월 2건: (1) ContextMenu Tab 미처리(Tab이 메뉴 밖으로 포커스) — 키보드/a11y, 레이아웃 무관 → **defer 권고**. (2) 그리드 컨테이너 tabIndex/role 부재 — a11y; 이 스프린트가 그리드 스크롤 컨테이너를 재구성하므로 **소폭 인접**(같은 컨테이너를 편집) → 저비용 흡수 가능하나 스코프 집중 위해 기본 **defer 권고**. **사용자 확정**: 흡수 여부(기본=둘 다 미접촉, 백로그 유지).

---

## Evaluation Criteria (Web/UI 4기준 — 가중치 표기)
1. **Design Quality (★★★)** — 뷰포트 맞춤 후에도 밀도·여백·계층이 무너지지 않는가. 고정 크롬 ↔ 스크롤 본문 경계가 의도적이고 정돈돼 보이는가(Airbnb 라이트 톤 유지).
2. **Originality (★★★)** — 매직넘버·고정 px 하드코딩을 flex 기반의 의도적 규칙으로 대체(AI-slop한 임시 `calc()` 남발이 아니라 일관된 시스템). 재사용(중복 스크롤 처리 통일).
3. **Craft (★★)** — 스크롤 컨테이너 경계 정확성(가로 스크롤 페이지 바디 발생 0, 세로는 그리드 본문에만), sticky thead 유지, 리사이즈 시 무붕괴, 매우 작은/큰 뷰포트 처리, 콘솔 0(React dev warning·pageerror·의도치 않은 4xx/5xx 0).
4. **Functionality (★★)** — 모든 기존 기능(제어/할당/생성/조회/드래그·우클릭 선택) 무회귀, 레지스터 값은 /ops에서만 관찰 가능, 3DS 워드 메뉴가 OQ-1 확정안대로 도달 가능·역할 명확, operation_log 테일 접근 가능(소멸 아님).

---

## Completion Conditions (Evaluator PASS 최소 조건)
- **정적(fresh·독립 실행)**: `tsc --noEmit` = 0, `eslint .`(warning 포함) = 0, `vite build` = 0(스크래치 outDir, `backend/src/Wcs.Api/wwwroot` 무접촉 `git status` 빈 출력).
- **회귀·무접촉 게이트**: `dotnet test -c Release` = **360/360 GREEN**(비결정 flake는 clean 재run으로 귀속 — lessons 준수). `git diff --stat backend/` 빈 출력, 신규 마이그레이션 0.
- **브라우저 실증(Playwright MCP, 포트=`.claude/ports.local.json` 소스)**: §Verification Scenarios 전건을 click-through로 실증(navigate→resize→관찰), 번호 스크린샷 + console.log 첨부. 데이터 **적음/넘침 양극단** 모두에서 페이지가 뷰포트 내에 들어오고 그리드 본문만 스크롤됨을 시각 확인.
- **콘솔(BLOCKING)**: 내 origin에서 error/warning 0(React dev warning·pageerror·의도치 않은 network 4xx/5xx 0).
- **핵심 불변식**: 레지스터 값 표시가 `/ops`에만 존재(SortersPage 경로에 WordPanel 부재), 3DS 워드 메뉴가 OQ-1 확정안과 일치, operation_log 테일 접근 가능(OQ-1 (a)에서 이관 결정 시 이관처에서 확인).

---

## Parallel Modules
N/A (단일 모듈). 뷰포트 패턴은 `Layout.tsx`(공통 main)와 각 페이지가 공유하며, R2/R3의 SortersPage·OpsPage·Layout NAV 편집이 R1의 Layout 편집과 같은 파일에 수렴 → 파일 경계 분할 불가. 순차 단일 Generator.

## Evaluation Dimensions
functional only(단일 차원). UI 레이아웃 리팩터 — 보안/성능 민감 표면 없음. 기본 1/1/1.

---

## Detected Project Type: **Full-stack**
근거(프로젝트 신호): 리포에 브라우저 진입점(`frontend/index.html` + 클라이언트 렌더 트리 `frontend/src/main.tsx`)과 서버측 라우트/컨트롤러(`backend/src/Wcs.Api` ASP.NET MVC Controllers)가 **동시에** 존재 = Full-stack 신호. 단, **이 스프린트의 변경 표면은 클라이언트 렌더 트리에 100% 한정**되며 백엔드 변경 0이 하드 제약(게이트). 아래 슬롯은 이 사실을 반영해 채운다.

### Verification Scenarios (Full-stack 슬롯 — 필수)

**=== 프론트(Web/UI) 시나리오 — 이 스프린트의 주 표면 ===**

- **각 대상 페이지의 기본 상태(뷰포트 맞춤)**:
  - S1 `/monitor`(3탭 각각): 넘치는 오더/piece/셀 데이터로 채운 상태에서 **페이지가 뷰포트 내에 들어오고 그리드 본문만 세로 스크롤**, 상단 탭바·배치/상태 필터·CardHeader 고정, thead sticky 유지, 페이지 바디 가로 스크롤 0.
  - S2 `/ops`: 소터 선택 후 WordPanel(레지스터 값 관찰 가능) + OpsControls가 뷰포트에 맞고, 짧은 뷰포트에서도 크롬 붕괴 없이 처리(그리드형 아님이므로 OQ-2 하한 규칙 적용 확인).
  - S3 `/b2c/test-data`(마스터-디테일): 좌 생성 폼 고정 + 우 마스터 그리드 + 하단 디테일 그리드가 OQ-2 배분대로 각자 내부 스크롤, 페이지 fit.
  - S4 `/b2c/facility`(3단+2패널): OQ-2(2-3) 확정안대로 fit 또는 예외 처리됨을 실증.
  - S5 `/sorters`(OQ-1 확정 후 형태): 확정안(예 (b) 운영 로그 페이지)대로 렌더, 레지스터 워드 **부재**, OpLogTail 내부 스크롤·페이지 fit.
  - (OQ-3 포함 시) S6 `/logs`·`/comparison`·`/boxes`·`/data-generator`: 매직 calc 제거 후 flex 패턴으로 fit + 그리드 내부 스크롤.
- **이 스프린트가 도입하는 대체 상태(적음/넘침/리사이즈)**:
  - S7 **데이터 소량**: 각 페이지에서 행이 적을 때 빈 스크롤·과도한 여백·페이지 늘어짐이 없다.
  - S8 **데이터 넘침**: 대량 행에서 페이지 높이가 뷰포트를 넘지 않고 그리드 본문만 스크롤(스크롤바가 그리드 내부에 생김).
  - S9 **뷰포트 리사이즈**: 창을 좁게/짧게(예 매우 작은 높이) 리사이즈해도 크롬 고정 + 그리드 본문 스크롤 유지, 하한 미만이면 OQ-2(2-3/1-f) 폴백대로 동작.
- **관련 empty/error 상태**: S10 각 그리드의 로딩/에러/빈 상태(LoadingRow/ErrorRow/EmptyRow)가 스크롤 컨테이너 재구성 후에도 정상 표시(레이아웃 깨짐 0).
- **다크 모드**: **N/A** — 프로젝트는 단일 라이트 테마(`index.html` `color-scheme: light`, `index.css` 라이트 단일). 검증 제외 사유 명시.
- **핵심 상호작용 흐름(이 스프린트가 낳는 사용자 가시 동작)**:
  - S11 레지스터 값이 **`/ops`에서만** 관찰됨 — `/sorters`(또는 OQ-1 확정 경로)에는 WordPanel이 없음을 네비게이션으로 확인.
  - S12 3DS 워드 메뉴가 OQ-1/R3 확정안대로 라벨·역할을 가지며 네비에서 도달 가능(고아·죽은 라우트 0).
  - S13 S-B2C-GRID-UX 그리드 상호작용(드래그 범위 하이라이트·우클릭 4액션·자격 존중)이 **스크롤 발생 상태에서도** 정확 동작(좌표/드래그 판정 무회귀).

**=== 백엔드(Backend/API) 시나리오 ===**
- **N/A — 이 스프린트는 백엔드 표면을 건드리지 않는다(하드 제약).** 검증 슬롯 대체물 = **무접촉·무회귀 게이트**: `git diff backend/` 빈 출력 + 신규 마이그레이션 0 + `dotnet test -c Release` 360/360 GREEN(Evaluator 독립 재실행). 엔드포인트·happy path·에러 케이스 신규 시나리오 없음(신규/변경 엔드포인트 0).

**=== 최소 1개 교차-레이어(E2E) 데이터 흐름 시나리오 ===**
- S14: `Sim3ds`(:sim 포트) + `Wcs.Api`(:api 포트, fresh scratch SQLite·seed) 기동 → 브라우저(:vite)에서 `/ops` 진입 → **실시간 SignalR로 소터 레지스터 값(D0~D6)이 WordPanel에 흐르는 것을 관찰**하면서 뷰포트를 리사이즈 → 라이브 데이터 표시가 유지되며 페이지가 뷰포트에 맞고 크롬 고정이 깨지지 않음을 실증(브라우저↔API↔SignalR↔Sim 전 계층 왕복). 포트는 `.claude/ports.local.json`에서 읽음(하드코딩 금지).

> Planner self-check — Detected project type: Full-stack. Required scenario slots: 4 (Web/UI 기본상태·대체상태·empty/error·핵심흐름 + Backend/API(N/A 게이트) + E2E 교차레이어; 다크모드=N/A 사유명시). All slots filled: yes.

## 게이트 확정 (사용자, 2026-07-15 — OQ 최종 답)
- **OQ-1 = '운영 로그'로 개명 유지**: /sorters에서 WordPanel(D0~D6 레지스터 표시) 삭제 → 페이지엔 OpLogTail(운영 로그 실시간 tail) 유지. NAV 라벨 '3DS 워드' → '운영 로그'(title/subtitle도 로그 기준으로). 라우트 유지(운영 로그가 앱에서 유일하게 여기 존재하므로 페이지 제거 금지). WordPanel은 /ops에만 잔존(useMonitorState 단일 인스턴스). 오펀 소터 Select 정리.
- **OQ-2 = 공통 레이아웃 일괄**: Layout.tsx main이 뷰포트 바운드 높이 제공 + 각 페이지 = 헤더/필터/툴바 shrink-0 + 그리드 body flex-1 min-h-0 overflow-auto. 현재 max-h/calc/무제한 3종 혼재를 단일 flex 패턴으로 통일. 다중 그리드 페이지(데이터생성 마스터-디테일·설비 3스택+2패널) 높이 배분 규칙 명시, 최소 높이 하한.
- **OQ-3 = b2b도 포함**: b2b 5페이지의 calc(100vh-N) 매직값을 공통 flex 패턴으로 전환.
- 백엔드/마이그레이션 0(순수 프론트 — 레지스터 dedup은 컴포넌트 삭제).
