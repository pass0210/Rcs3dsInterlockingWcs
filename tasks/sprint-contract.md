# Sprint Contract — S-UI-LAYOUT-FIX

> PR #66(S-UI-LAYOUT) 병합 후 사용자가 발견한 뷰포트-맞춤 후속 버그 2건 수정.
> Base: `develop = a088520` (Merge PR #66). 프론트엔드 전용 · 백엔드 diff 0.
> 작성: Planner Subagent, 2026-07-15. WHAT만 기술(구현 기법=HOW는 Generator 재량).

---

## Goal

두 페이지의 **낮은 뷰포트**(사용자 실제 화면 높이, ~680–720px)에서의 레이아웃 결함을 근본 수정한다.

1. **B2C 데이터 생성(`/b2c/test-data`)** — 좌측 '데이터 생성' 폼 카드가 상단 영역의 높이를 초과해
   아래 '배치 상세' 카드 위로 **넘쳐 겹치고(overlap)**, '+ 데이터 생성' 버튼이 배치 상세 카드에 가려/잘린다.
   상단 영역이 하단과 flex 50/50으로 분할되어 짧은 뷰포트에서 상단이 폼보다 작아지는 것,
   그리고 배치 상세가 비어 있어도 절반을 점유하는 것이 원인.
2. **운영 제어(`/ops`)** — WordPanel(3DS 레지스터 워드 D0~D6) + OpsControls 합산 높이가 뷰포트를 초과해
   **페이지(main) 자체가 아래로 삐져나온다(page-level scroll/overflow)**. 내부 `overflow-auto`(OpsPage.tsx:75)가
   있으나 짧은 뷰포트에서 단일 내부 스크롤로 갇히지 못한다.

**성공의 정의는 큰 뷰포트가 아니라 낮은 뷰포트에서의 수치 검증이다** (아래 Verification Scenarios).
이전 스프린트 Evaluator가 큰 뷰포트에서만 `scrollHeight==innerHeight`를 봐서 실제(더 낮은) 뷰포트의
오버플로/오버랩을 놓친 회귀를 반복하지 않는다.

---

## 배경 — 코드 실측(착수 전 확인 완료, load-bearing)

- `B2cDataGenPage.tsx:158-159` — 바깥 컬럼에 **두 개의 `flex-1` 형제**(상단 그리드 `min-h-[220px] flex-1`,
  하단 배치상세 `flex-1`)가 있어 가용 높이를 ~50/50으로 나눈다. 좌측 폼 `Card className="self-start"`(line 161)은
  **자연 높이(natural)**로 렌더되므로, 짧은 뷰포트에서 상단 영역의 ~50% 몫이 폼보다 작아지면 폼이 클램프되지 않고
  아래로 **넘쳐 배치상세 카드 위에 페인트**된다 → '+ 데이터 생성' 버튼(line 460)이 겹쳐/잘림.
- `OpsPage.tsx:75` — 내부 `flex min-h-0 flex-1 flex-col gap-4 overflow-auto`가 단일 스크롤 본문 의도이나,
  짧은 뷰포트에서 WordPanel+OpsControls 스택이 page-level 오버플로를 일으킨다(단일 내부 스크롤로 못 가둠).
- 높이 체인은 **이미 정확**: `index.css` html/body/#root=height:100%, `Layout.tsx` main=`flex min-h-0 flex-1 overflow-auto`.
  → 근본 원인은 **page-local**. 공용 프리미티브 수정 불요.
- 프런트 테스트 러너 **vitest 미설정**(package.json에 test 스크립트 없음). 스크립트: `typecheck`(tsc --noEmit),
  `lint`(eslint), `build`(tsc && vite build).
- 단일 라이트 테마(다크모드 없음). Port source of truth: 리포 루트 `.claude/ports.local.json`.

---

## Scope (specific files)

**변경 surface = Web/UI 전용.** (리포는 Full-stack — `frontend/`(React SPA) + `backend/`(ASP.NET Controllers) —
이지만 이번 스프린트가 만지는 표면은 브라우저 프런트엔드뿐이다. 백엔드/마이그레이션 diff는 **반드시 0**.)

### 편집 허용(주 대상)
- `frontend/src/pages/B2cDataGenPage.tsx` — 버그 (1). 상단(폼+마스터 그리드)/하단(배치 상세) 높이 배분·스크롤 소유권.
- `frontend/src/pages/OpsPage.tsx` — 버그 (2). 레지스터+제어 스택을 단일 바운드 내부 스크롤로 가둠.

### 조건부 편집(엄격히 필요할 때만 · 근거 기록 필수)
- `frontend/src/pages/sections/WordPanel.tsx`, `frontend/src/pages/sections/OpsControls.tsx`
  — 페이지 레벨(래퍼 클래스)로 해결 불가할 때만. 내부 로직/데이터 흐름은 무접촉.

### 원칙적 off-limits (공용 프리미티브 — 블라스트 반경 큼)
- `frontend/src/components/Layout.tsx`, `frontend/src/components/ui/card.tsx`, `frontend/src/index.css`
  — 높이 체인은 이미 정확함이 확인됨. 근본 원인은 page-local. **수정하지 않는다.** 불가피하다고 판단되면
    Generator는 (a) 왜 page-local로 불가능한지 sprint-log.md에 근거를 기록하고,
    (b) 회귀 검증 범위를 **10개 페이지 전체**로 확대해야 한다(아래 Regression 참조).

### 절대 불변
- `backend/**` diff 0 (dotnet 프로젝트 무접촉). `frontend/package.json`·`package-lock.json` 무수정
  (검증용 의존 설치 금지 — Playwright는 MCP 브라우저 도구 사용).

---

## Constraints (Generator drift 방지 — 반드시 준수)

1. **빈 영역이 큰 고정 분율을 점유하지 않는다.** '배치 상세'는 비어 있을 때 50%를 차지하면 안 된다.
   상단(폼 + 마스터 그리드) 영역은 **폼 카드 자연 높이 이상**으로 보장되어 폼이 하단과 절대 겹치지 않아야 한다.
   권장 방향(기법은 Generator 재량): 짧은 폼 카드는 **content-natural 높이**, 마스터 그리드가 그 영역의 유연한
   나머지를 채우며 내부 스크롤, 배치 상세는 **자체 바운드 스크롤**로 남은 높이를 갖는다.
2. **`/ops`**: 짧은 뷰포트에서 레지스터(WordPanel) + 제어(OpsControls) 스택은 **단일 바운드 내부 스크롤 영역**에
   담겨 **페이지(main)는 절대 스크롤하지 않는다.** 소터 선택 바는 고정(`shrink-0`) 유지.
3. **S-UI-LAYOUT 불변식 보존**: header/toolbar = `shrink-0`; 그리드/스크롤 본문 = `flex-1 min-h-0 overflow-auto`;
   **영역당 스크롤 컨테이너 1개**; `sticky thead` 유지(잘림/이중 스크롤 금지).
4. 하드코딩 마법값(px 높이 등)은 지양 — 부득이하면 근거 주석. 기존 디자인 톤/색/타이포는 무변경(회귀 0).

---

## Detected Project Type: **Full-stack**

리포 신호: 브라우저 진입점(`frontend/index.html` + `frontend/src/main.tsx`, 클라이언트 렌더 컴포넌트 트리)
**AND** 서버 라우트/컨트롤러(`backend/src/Wcs.Api` ASP.NET Controllers) 가 같은 리포에 공존 → **Full-stack**.
단, **이번 스프린트의 change surface 는 Web/UI(프런트엔드) 뿐**이며 backend diff 는 0 이다.
따라서 Full-stack 슬롯을 채우되 Backend/API 슬롯은 "이번 스프린트 미접촉(N/A)"으로 정직히 표기한다.

---

## Evaluation Criteria (Evaluator 판정 기준 + 가중치)

Full-stack → per-layer 적용. 이번 스프린트는 프런트 레이아웃 버그픽스이므로 Web/UI criteria 중심.

| # | 기준 | 가중치 | 이번 스프린트 해석 |
|---|------|--------|--------------------|
| 1 | **Functionality (viewport-fit 정확성)** | ★★★ (최우선) | 낮은 뷰포트에서 (a) page/main 스크롤 0, (b) 카드 오버랩 0, (c) '+ 데이터 생성' 버튼 완전 가시·클릭 가능, (d) /ops 단일 내부 스크롤. **수치로** 증명. |
| 2 | **Craft (레이아웃 불변식·회귀 없음)** | ★★★ | S-UI-LAYOUT 불변식(shrink-0 크롬 / flex-1 min-h-0 overflow-auto 본문 / 영역당 스크롤 1개 / sticky thead) 유지. 8개 회귀 페이지 무붕괴. tsc/eslint/build 0. |
| 3 | **Design Quality (기존 톤 보존)** | ★★ | 기존 순백 라이트 테마·간격·타이포 무변경(레이아웃 fix가 디자인을 바꾸지 않음). |
| 4 | **Integration / Data-flow 무단절** | ★★ | 레이아웃 변경이 라이브 데이터 렌더(SignalR 워드, 배치/오더 그리드)를 스크롤 영역 안에서 정상 표시함을 유지(cross-layer 무회귀). backend diff 0. |

**Static checks (전 타입 공통, 독립 재실행 필수)**: `npm run typecheck`(tsc --noEmit), `npm run lint`(eslint),
`npm run build`(tsc && vite build) — 모두 0 error. 프런트 테스트 러너: **vitest 미설정**(package.json에 test 스크립트 없음) →
sprint-feedback.md에 `not configured`로 기록. backend: `git diff --stat -- backend` 가 공란임을 확인.

---

## Completion Conditions (Evaluator PASS 최소 조건 — 전부 AND)

두 페이지 각각을 **최소 3개 낮은 뷰포트**(`1366×720`, `1280×680`, `1300×700`)에서 Playwright MCP로 재현하여
아래를 **수치 증거**(스크린샷 파일 경로 + `browser_evaluate` 반환값 raw)로 뒷받침한다. 큰 뷰포트에서만 통과 = **FAIL**.

- [C1] **B2C — page/main scroll = 0**: 각 뷰포트에서 `<main>` 엘리먼트가 스크롤하지 않는다
  (`main.scrollHeight <= main.clientHeight + 1`) 그리고 `document.scrollingElement.scrollHeight <= innerHeight + 1`.
  (내부 그리드/디테일 영역의 `scrollHeight > clientHeight` 는 허용 — 그게 의도된 내부 스크롤.)
- [C2] **B2C — 카드 오버랩 0**: 폼 카드와 '배치 상세' 카드의 `getBoundingClientRect()`가 세로로 겹치지 않는다
  (`form.bottom <= detail.top + 1`). 모든 3개 뷰포트에서.
- [C3] **B2C — '+ 데이터 생성' 버튼 완전 가시 + 클릭 가능**:
  (i) 버튼 rect 가 뷰포트 안에 완전히 포함(`0 <= top`, `bottom <= innerHeight`) 그리고 어떤 형제 카드에도 가려지지 않음
  (`document.elementFromPoint(중심좌표)` 가 버튼 자신 또는 그 자식) — 3개 뷰포트 전부;
  (ii) 실제 `browser_click`으로 클릭이 성립하고(필수값 미입력 시 경고 토스트가 뜨는 것으로 "도달·클릭 가능" 증명) 예외/차단 없음.
- [C4] **B2C — 빈 배치 상세가 절반을 점유하지 않음**: 배치 미선택(빈) 상태에서 배치 상세 카드 높이가 상단 영역 높이보다
  작거나 같음(빈 영역 50% 점유 해소를 수치로: `detail.height <= topRegion.height`) 또는 상단이 폼 자연높이 이상임을 확인.
- [C5] **/ops — page/main scroll = 0**: [C1]과 동일 측정을 `/ops`에 적용. `<main>` 및 document 무스크롤.
- [C6] **/ops — 단일 바운드 내부 스크롤**: 짧은 뷰포트에서 레지스터+제어 스택을 감싼 스크롤 영역이
  내부 스크롤을 소유(`region.scrollHeight > region.clientHeight`)하고, **그 영역만** 스크롤 가능(main·body는 불변).
  소터 선택 바는 스크롤 중에도 위치 고정(`shrink-0` — 내부 영역 스크롤 전/후 바 rect.top 동일).
- [C7] **/ops — 제어 버튼 도달 가능**: 내부 영역을 끝까지 스크롤하면 OpsControls 마지막 컨트롤('셀 지정' 버튼)이
  완전 가시·클릭 가능(가려짐 0).
- [C8] **콘솔 클린(BLOCKING)**: 두 페이지 검증 중 `page.on('console')`+`pageerror` 캡처를
  `screenshots/{sprint}/console.log`에 저장. React dev-mode warning·pageerror·의도치 않은 4xx/5xx = FAIL.
- [C9] **회귀 없음(8개 페이지)**: `/monitor`, `/sorters`(운영 로그), `/b2c/facility`, `/data-generator`, `/logs`,
  `/comparison`, `/boxes`, `/settings` 를 `1280×680`에서 확인 — page-level(main/body) 스크롤 0(각 페이지의
  내부 스크롤만 허용), 신규 콘솔 오류/오버랩 0. (공용 프리미티브를 만졌다면 10개 전부 재확인.)
- [C10] **정적 검사 0**: `npm run typecheck`, `npm run lint`, `npm run build` 각각 error 0(fresh 실행 raw 인용).
- [C11] **backend diff 0**: `git diff --stat -- backend` 공란. `frontend/package.json`·`package-lock.json` 무변경.

> Port source of truth: Evaluator는 네비게이트 전 **`.claude/ports.local.json`**(리포 루트)의 `vite` 포트를 읽어 URL을 구성한다.
> `localhost:5173` 등 하드코딩 금지. 파일의 sprint 값이 낡았어도 orchestrator가 이번 스프린트로 재기록한 포트를 신뢰.

---

## Parallel Modules
N/A (single module). 두 파일(B2cDataGenPage, OpsPage)은 파일 경계가 분리되지만, 동일한 S-UI-LAYOUT
불변식·공용 검증 하니스를 공유하므로 하나의 Generator가 일관되게 처리하는 편이 낫다. 기본 1/1/1.

## Evaluation Dimensions
functional only (레이아웃/뷰포트-맞춤 단일 차원). 보안·성능 표면 없음 → 단일 Evaluator.

---

## Verification Scenarios (Full-stack · 낮은 뷰포트 · per-page · 수치)

> 공통: Evaluator는 `.claude/ports.local.json`의 vite 포트로 URL 구성. Playwright **MCP** 브라우저 도구 사용
> (package.json 무수정). 각 뷰포트는 `browser_resize`로 설정. 그리드에 실데이터를 채우려면 backend+sim+seed 기동
> 권장(인프라 미실행은 스킵 사유 아님 — 필요시 기동). 단, C1–C7의 **핵심 수치 게이트는 빈/에러 그리드에서도 성립**하며
> 그것이 pass gate다. 스크린샷은 `screenshots/S-UI-LAYOUT-FIX_{YYYYMMDD-HHMMSS}/`에 번호로 저장, 콘솔은 console.log.

### === Full-stack: Applicable Web/UI scenarios (프런트 surface — 이번 스프린트 본체) ===

- **Default state of each surface touched by this sprint**
  - `/b2c/test-data` 기본 진입(배치 미선택): 좌 폼 카드 + 우 마스터 그리드(상단) + 배치 상세(하단, 빈 EmptyRow).
    → 각 뷰포트에서 스크린샷 + rect 측정으로 C1/C2/C3/C4 확인.
  - `/ops` 기본 진입(첫 소터 자동 선택): 소터 선택 바 + WordPanel + OpsControls 스택.
    → 각 뷰포트에서 C5/C6/C7 확인.

- **Each alternate state the sprint introduces (selected / populated / scrolled)**
  - `/b2c/test-data` 마스터 그리드에 배치 rows 존재 시: 그리드 본문만 내부 스크롤(sticky thead 유지),
    상단 영역이 폼 자연높이를 하한으로 유지되어 여전히 오버랩 0(C2).
  - `/b2c/test-data` 배치 행 선택 시: 하단 배치 상세에 오더 로드 → 상세 본문만 내부 스크롤, page/main 스크롤 여전히 0(C1).
  - `/ops` 내부 영역을 하단까지 스크롤한 상태: 소터 바 고정(rect.top 불변), '셀 지정' 버튼 가시·클릭(C6/C7).

- **Relevant empty / error state surfaced by this sprint**
  - `/b2c/test-data` 배치 0건(EmptyRow) — 빈 배치 상세가 절반을 점유하지 않음(C4), 폼 오버랩 0(C2).
  - `/ops` 소터 미등록(noSorters 분기) — 안내 박스가 page 스크롤을 유발하지 않음(C5).

- **Dark mode variant**
  - **N/A** — 프로젝트는 단일 라이트 테마(`index.css`: "단일 테마, 다크모드 없음"). 다크 변형 없음.

- **Key interaction flow after the change (이 스프린트가 산출해야 할 사용자 가시 동작)**
  - B2C: 낮은 뷰포트에서 페이지가 스크롤되지 않고, 폼이 배치 상세를 덮지 않으며, '+ 데이터 생성' 버튼을
    스크롤 없이 즉시 눌러 생성 플로우(검증 토스트)에 도달할 수 있다. (navigate → resize → rect측정 → click → assert)
  - /ops: 낮은 뷰포트에서 페이지가 삐져나오지 않고, 소터 바는 고정된 채 레지스터+제어 스택을 내부 스크롤로
    끝까지 훑어 마지막 제어까지 조작할 수 있다. (navigate → resize → scroll region → rect측정 → assert)

### === Full-stack: Applicable Backend/API scenarios (백엔드 surface) ===

- **N/A — 이번 스프린트는 백엔드를 접촉하지 않는다.** endpoint diff 0, 컨트롤러/마이그레이션 무변경.
  Evaluator는 회귀 확인용으로 `git diff --stat -- backend` 가 공란임을 증거로 남긴다(C11). (검증 인프라로
  backend/sim을 기동하는 것은 그리드 데이터 표시용일 뿐 — 서버 로직 검증 대상 아님.)

### === Full-stack: End-to-end data-flow scenario (2+ 레이어 교차) ===

- **레이아웃 fix가 라이브 데이터 흐름을 끊지 않음(cross-layer 무회귀)**: backend + Sim3ds 기동(시드 포함) 상태에서
  → `/ops`가 PLC→Modbus→SignalR로 흐르는 D0~D6 워드 값을 **내부 스크롤 영역 안에서** 정상 표시·갱신하고
  스크롤로 전량 접근 가능(데이터 경로 무단절) → `/b2c/test-data`가 API(`/api/b2c/...`)로 받은 배치/오더 rows를
  **바운드 스크롤 그리드 안에서** 표시하고 상단 폼과 겹치지 않음. 즉 레이아웃 변경 후에도 각 레이어에서 흘러온
  데이터가 새 스크롤 컨테이너 안에서 온전히 렌더/스크롤됨을 1개 시나리오로 관통 확인.

---

## Regression guard

- 공용 프리미티브(Layout.tsx/card.tsx/index.css)를 **만지지 않으면** 회귀 대상 = 8개 페이지(C9).
- 공용 프리미티브를 **부득이 만졌다면** 회귀 대상 = **10개 페이지 전부**(두 수정 페이지 포함) 재검증 + 근거 기록.
- 회귀 판정: 각 페이지 `1280×680`에서 page-level(main/body) 스크롤 0(내부 스크롤만 허용), 콘솔 오류·오버랩 0.

---

## Open Questions

없음. 초점이 좁은 버그픽스이며 관측 가능한 목표가 명확하다(수치 게이트 C1–C11). 기법(정확한 flex/grid 클래스 배치)은
Generator 재량. 만약 "page 스크롤 0"과 "폼 오버랩 0"을 동시에 만족시키는 것이 특정 극단 뷰포트에서 물리적으로
불가능하다고 판명되면(예: 폼 자연높이+상세 최소높이 합 > 가용높이) — 낮추지 말고 Generator가 근거와 함께
orchestrator를 통해 사용자에게 에스컬레이션한다(요구 완화 금지). 목표 뷰포트(≥680px 높이)에서는 성립 가능으로 판단.

---

> Planner self-check — Detected project type: Full-stack. Required scenario slots: 3 (Applicable Web/UI scenarios, Applicable Backend/API scenarios, End-to-end data-flow scenario). All slots filled: yes.
