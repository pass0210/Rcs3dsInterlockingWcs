# [Sprint Contract] S-B2C-GRID-UX — B2C 프론트 후속 UX (랜딩 + 공용 그리드 상호작용)

> 작성: Planner Subagent · 2026-07-15. 사용자 지시 위, 방금 병합된 **S-B2C-UX(PR #64)** 위. **순수 프론트 스프린트**(백엔드/스키마/마이그레이션 0 목표).
> 근거(직접 확인): 프로젝트 CLAUDE.md · tasks/lessons.md · tasks/workflow-agents.md(계약 템플릿) · tasks/sprint-feedback.md(S-B2C-UX APPROVED + Minor 5건) · docs/FRONTEND.md · frontend/src/lib/uiMode.ts · App.tsx · Layout.tsx · pages/B2cDataGenPage.tsx · B2cFacilityPage.tsx · lib/b2cFacility.ts · b2cTestData.ts · components/ui/* · pages/sections/{SummaryGrid,DetailGrid}.tsx · index.css · .claude/ports.local.json.
> 게이트 완료 — 아래 "확정 요구"는 재질문 금지. "Open Questions"만 Phase 1 게이트에서 사용자 사인오프 필요.

---

## Goal

B2C 사용 흐름을 개선한다. 두 축:

1. **랜딩 정합** — 프로그램 실행/ B2C 모드 진입 시 **데이터 생성 페이지가 먼저** 뜨게 한다(현재는 모니터링). "생성 → 설비" 관리 흐름의 시작점과 첫 착지를 일치시킨다. (S-B2C-UX Evaluator Minor #1 흡수 — 당시 "/monitor 유지, 사용자 이견 시 변경" 판단을 사용자 이견으로 뒤집음.)
2. **공용 그리드 상호작용** — 모든 B2C 체크박스 그리드에서 우클릭 컨텍스트 메뉴(4항목) + 드래그 범위 하이라이트 선택 + 하이라이트 행 일괄 체크/해제를 **재사용 가능한 단일 훅/프리미티브**로 제공한다. 그리드마다 중복 구현하지 않는다.

---

## 확정 요구 (게이트 완료 — 재질문 금지)

### R1. 랜딩 변경
- 프로그램 실행 및 B2C 모드 진입 시 첫 착지 = **데이터 생성 페이지**(`/b2c/test-data`). 현재 `frontend/src/lib/uiMode.ts` `homePathFor('b2c')` 가 `/monitor` 를 반환하는 것을 데이터 생성 라우트 반환으로 변경.
- `homePathFor` 는 리다이렉트/토글 공용 단일 소스(App.tsx `ModeHome` 의 `/` 및 미매칭 `*` 리다이렉트, Layout `ModeToggle` 의 b2c 전환에 모두 반영됨) — 이 한 곳 변경으로 3경로 전부 정합되어야 한다.
- `frontend/src/App.tsx` `ModeHome` 리다이렉트 주석(현재 "b2c→/monitor")도 새 착지와 일치하도록 갱신.
- **B2B 랜딩은 불변**(`/data-generator`).

### R2. 공용 그리드 상호작용 — 대상: 모든 B2C 체크박스 그리드
적용 대상(사용자 명시) — 3개 그리드:
- **G1** 데이터 생성 페이지 "생성 결과 — 최근 배치" 그리드 (`B2cDataGenPage`). (헤더 전체선택 체크박스 + 행별 체크박스 보유.)
- **G2** 설비 관리 좌측 "배정 대상"(슈트 리프 + 소터 셀) 그리드 (`B2cFacilityPage` / `OrderAssign2Panel`). (헤더 전체선택 없음, 비활성 행 존재.)
- **G3** 설비 관리 우측 "미할당 오더" 그리드 (동). (헤더 전체선택 없음, `!canReassign` 비활성 행 존재.)

각 그리드에 다음을 제공한다:
- **우클릭 컨텍스트 메뉴 4항목**:
  1. **전체 선택** — 그리드의 모든 (체크 가능한) 행을 체크.
  2. **전체 해제** — 모든 행 체크 해제.
  3. **선택된 행 체크** — 현재 하이라이트된 행들에만 체크 적용.
  4. **선택된 행 해제** — 하이라이트된 행들에만 체크 해제 적용.
- **행 선택(하이라이트)은 체크박스와 별개 개념**: 드래그로 **연속 행 범위**를 하이라이트한다. 하이라이트는 체크 상태와 시각적으로 구분된다. 메뉴 ③④는 하이라이트된 행에, ①②는 전체 행에 작용한다.
- **드래그 = 범위 선택(하이라이트)** 방식으로 확정(페인트 토글 아님). 앵커 행 → 현재 행 사이 연속 범위가 하이라이트됨.
- **행 자격(eligibility) 존중**: 현재 그리드들은 조건부로 비활성화된 체크박스를 가진다(G2 비활성 슈트/비활성 셀, G3 진행 중 오더 `!canReassign`). 일괄 체크(①③)는 개별 체크박스와 **동일한 비활성 조건**을 따라야 한다 — 비활성 행을 체크하면 안 된다(개별 조작과 불일치 금지).

### R3. 재사용 구조
- 재사용 가능한 **공용 훅 + 컨텍스트 메뉴 프리미티브**로 구현(사용자 예시: `useRowSelection` + `ContextMenu`). 그리드마다 드래그/메뉴 로직을 중복하지 않는다.
- 기존 UI 프리미티브(`frontend/src/components/ui/*`) 패턴·Tailwind 토큰·TanStack 재사용. 현재 `components/ui` 에 컨텍스트 메뉴 프리미티브는 **없음**(신설) — 기존 `Dialog`(portal + focus trap + Escape) 패턴과 정합하게 신설.
- 각 그리드는 **자기 체크 상태(현재 `Set<number>`/`Set<string>`)를 계속 소유**하고, 훅은 하이라이트 + 4액션을 그 체크 상태에 연결하는 브리지 역할(체크 모델 재작성 금지 — 최소 침습).

### R4. 접근성
- 마우스 전용 기능(드래그·우클릭)이라도 **최소한의 키보드 대안**과 합리적 aria 제공. 컨텍스트 메뉴는 키보드로 열고/탐색/실행 가능(role=menu/menuitem, Escape 닫힘, 포커스 관리 — 기존 Dialog 규약 준용). 전체 선택/해제는 마우스 없이도 도달 가능.

---

## Implementation Scope (WHAT — Generator 가 HOW 결정)

- **A. 랜딩(R1)**: `homePathFor('b2c')` 반환 경로 변경 + `App.tsx` 주석 정합. B2B 경로 불변. (uiMode.ts 단일 소스이므로 파급 3경로 자동 정합 — 회귀 없이.)
- **B. 공용 프리미티브(R2/R3/R4)**: 재사용 훅(하이라이트 범위 상태 + 4액션 핸들러) + 컨텍스트 메뉴 프리미티브(위치 지정 팝오버·4항목·키보드/aria). 기존 `components/ui`·Tailwind·`cn` 유틸·portal 패턴 재사용.
- **C. 3개 그리드 결선(R2)**: G1/G2/G3 각각에 드래그 하이라이트 + 우클릭 메뉴 + 4액션을 결선. 기존 체크 Set·기존 액션 버튼(초기화/배정/해제) 카운트가 새 체크 결과를 그대로 반영. 기존 상호작용(행 클릭=디테일 로드[G1], 소터 행 클릭=펼침[G2], 개별 체크박스 토글) 무손상 공존.
- **D. 문서**: `docs/B2C-DATAGEN.md`·`docs/B2C-FACILITY.md` 에 그리드 상호작용(우클릭 4항목·드래그 하이라이트·자격 존중) + 랜딩 변경 반영. 랜딩 정책이 `docs/FRONTEND.md` §5(페이지/화면)에 언급되면 정합.
- **E. 포트/증거**: `.claude/ports.local.json` 을 본 스프린트 role/port 로 갱신(하드코딩 금지). Playwright 증거는 기록된 포트에서 생성.

---

## Out of Scope (건드리지 않음)

- **비-그리드 단일 옵션 체크박스**: 모니터링 `OpLogTail`(POLL_CHANGE 포함·자동스크롤 토글), `SettingsPage`(바코드 값 표시), `LogsPage`(전체 기간), `Layout` B2B 자동 새로고침 토글 — 다중 행 선택 그리드가 아니므로 대상 아님.
- **백엔드/스키마/마이그레이션/EF/DTO**: 0 접촉 목표. `Wcs.PlcGateway`·`Wcs.Core`·실 PLC/COM1·Azure/사용자 DB·기존 API 엔드포인트 무접촉. (불가피 사유 발생 시 즉시 orchestrator 경유 사용자 승인 — 임의 확장 금지.)
- **가상화(virtualization) 도입**: 현 DOM-렌더 모델 유지(OQ-1 참조).
- **B2B 그리드 결선**: OQ-5 로 사용자 결정 대기(프리미티브는 재사용 가능하게 만들되, b2b 결선은 사인오프 전 착수 금지).

---

## Open Questions (진짜 새 결정 — Phase 1 게이트 사인오프 필요; Planner 권고 포함)

> Planner 는 옵션 나열이 아니라 권고를 제시한다. 사용자가 권고를 확정하면 그대로, 이견이면 조정.

- **OQ-1 · 대량 그리드에서 드래그/전체선택의 대상 범위.**
  사실: 현 그리드는 **가상화 없음** — 최대 `take=1000`(S-B2C-UX `ORDERS_FETCH_MAX`) 행을 전부 `overflow-auto` 컨테이너에 렌더하고, 1000 도달 시 이미 절단(Fail-Loud) 배너가 뜬다. 즉 "렌더된 행 = 로드된 데이터(≤1000)"로 동일.
  **권고**: 하이라이트·전체선택은 **현재 로드된 행(≤1000, 전부 DOM에 존재)** 기준으로 동작. 1000 초과분은 애초 로드되지 않으므로 "전체 선택"에 포함될 수 없고, 이는 기존 절단 배너가 이미 고지함(추가 조치 불요). 본 스프린트에 가상화·서버측 전체선택 도입 안 함(사내 데스크톱 툴·1000행 DOM 수용). → 확정 요청.

- **OQ-2 · 드래그 중 브라우저 기본 텍스트 선택 억제 · 스크롤 중 드래그 확장.**
  **권고**: 드래그 진행 중 네이티브 텍스트 선택 억제(user-select 억제). 스크롤 컨테이너 안에서 포인터 아래 행까지 범위가 따라오도록 확장 지원(휠 스크롤 병행 허용). **엣지 자동 스크롤(포인터가 상/하단 경계에 닿으면 자동 스크롤)은 본 스프린트 제외**(복잡도) — 필요하면 후속. → 엣지 자동 스크롤 필요 여부 확정 요청.

- **OQ-3 · 컨텍스트 메뉴가 브라우저 기본 우클릭을 대체하는 범위.**
  **권고**: 커스텀 메뉴는 **그리드 본문(행 영역) 내부에서만** 기본 우클릭을 대체한다. 그리드 밖은 브라우저 기본 유지. → 확정 요청.

- **OQ-4 · 하이라이트 선택 상태의 초기화 시점.**
  사실: 그리드는 refetch 가 잦다(배정/해제/초기화 후 `invalidateQueries`, `keepPreviousData`). 하이라이트가 위치/구식 행을 가리키면 오작동.
  **권고**: 하이라이트는 **안정 행 식별자(id) 기준**으로 유지하되, refetch 로 사라진 id 는 프루닝하고, **라우트 이동·스코프 변경(예: G1 디테일 대상 배치 변경)·필터 변경 시 전체 초기화**. 체크 상태(기존 Set·안정 id)는 기존과 동일하게 유지. → 정책 확정 요청(대안: refetch 시 하이라이트 완전 초기화 — 더 단순하나 사용성 다소 저하).

- **OQ-5 · B2B 그리드 포함 여부.**
  사실: 사용자는 "모든 체크박스 그리드"라 했으나 맥락은 B2C. **B2B DataGeneratorPage 에는 실제 다중선택 체크박스 그리드가 2개**(`SummaryGrid` 배치 목록 + `DetailGrid` 바코드 목록, 둘 다 헤더 전체선택 + 행별 체크박스) 존재한다.
  **권고**: 프리미티브는 재사용 가능하게 만들므로 b2b 2그리드 확장의 한계 비용은 낮다 → **b2b 2그리드도 본 스프린트에 포함**(일관성·"모든 그리드" 문언 충족)하되, 이 경우 검증 표면이 b2b DataGeneratorPage 로 확장됨을 명시. 사용자가 B2C 한정을 원하면 b2b 결선은 후속으로 분리(프리미티브는 그대로 재사용). → 포함/제외 확정 요청.

---

## Evaluation Criteria (Evaluator 판정 기준 · 가중)

프로젝트 타입 = Full-stack 이나 본 스프린트 변경 표면은 순수 Web/UI 이므로 Web/UI 4기준 중심 + 회귀/정적/교차레이어 게이트:

1. **Functionality (★★★)** — R1~R4 동작이 3(또는 5)개 그리드에서 정확히 작동: 랜딩 착지, 우클릭 4항목, 드래그 연속 하이라이트, ③④=하이라이트·①②=전체, 비활성 행 존중, 기존 상호작용(클릭·펼침·개별 체크) 무손상. 빈/에러 그리드에서 안전.
2. **Reuse / Architecture Originality (★★★)** — 드래그/메뉴 로직이 **단일 훅+프리미티브**로 통일되고 그리드별 중복이 없는가(사용자 R3 핵심). 기존 `components/ui`·Dialog 패턴·Tailwind 토큰과 정합적인가. AI-slop/과설계 아님.
3. **Craft (★★)** — 타입 안정성, 이벤트 정리(리스너/pointer capture 누수 0), aria/키보드 대안, 포커스 관리. 콘솔 0(React dev warning·pageerror·의도치 않은 4xx/5xx 0).
4. **Integration / Regression (★★)** — 새 선택 UX 가 기존 벌크 백엔드 경로(배정/해제/초기화)를 정확히 구동(교차 레이어). 백엔드 360 스위트 GREEN 회귀 0. 백엔드/스키마/마이그레이션 diff 0.

---

## Completion Conditions (Evaluator 통과 최소 조건)

1. **랜딩**: 최초 실행/모드=b2c/"/"/미매칭 경로 진입이 데이터 생성 페이지로 착지(브라우저 실증). B2B 진입은 `/data-generator` 불변. `homePathFor` 단일 소스 + App.tsx 주석 정합.
2. **그리드 상호작용**: G1/G2/G3(그리고 OQ-5 확정 시 b2b 2그리드) 전부에서 우클릭 4항목·드래그 하이라이트·③④ 하이라이트 적용·①② 전체 적용·비활성 행 존중 — 브라우저 클릭스루로 각 그리드 실증.
3. **재사용**: 드래그/메뉴 구현이 단일 훅+프리미티브(그리드는 소비만). 그리드별 중복 로직 없음(코드 확인).
4. **기존 동작 보존**: 개별 체크박스 토글, G1 행 클릭 디테일 로드, G2 소터 행 펼침, 기존 액션 버튼 카운트 반영 모두 정상.
5. **정적/회귀(fresh·격리)**: `tsc --noEmit` 0 · `eslint .` 0 · `vite build` 0(스크래치 outDir·wwwroot 무접촉) · 백엔드 `dotnet test` 360/360 GREEN(회귀 0).
6. **콘솔 BLOCKING 0**: 검증 origin 에서 error/warning/pageerror 0 (console.log 첨부).
7. **무접촉 경계**: 백엔드/스키마/마이그레이션/`Wcs.PlcGateway`/`Wcs.Core`/실 PLC/사용자 DB diff 0. 포트 하드코딩 0(`.claude/ports.local.json` source of truth — 평가자 5215/1512/5190 · 생성자 5216/1513/5191).
8. **문서**: B2C-DATAGEN.md·B2C-FACILITY.md(+필요 시 FRONTEND.md) 갱신 정합.

---

## Parallel Modules
N/A (single module — 프론트 단일 표면, 공용 프리미티브 하나에 3그리드가 의존해 병렬 분할 시 공유 파일 충돌). Generate-Verify 기본 1/1/1.

## Evaluation Dimensions
functional only (단일 차원 — 순수 프론트 UX·보안/성능 민감 표면 무접촉). Web/UI 브라우저 검증 + 정적/회귀 게이트로 충분.

---

## Detected Project Type: Full-stack
근거(레포 신호): 브라우저 진입점(`frontend/` Vite React SPA + `index.html`)과 서버측 라우트/컨트롤러(`backend/src/Wcs.Api` ASP.NET Core Controllers)가 동일 레포에 공존. (단, **본 스프린트 변경 표면은 순수 Web/UI** — 백엔드 슬롯은 아래에서 N/A 처리.)

## Verification Scenarios (Full-stack · 필수)

=== Applicable Web/UI scenarios (프론트 표면) ===
- **[기본 상태] V1 — 랜딩**: 앱 최초 실행(모드=b2c 기본) 및 "/"·미매칭(`*`) 경로 접근 → 데이터 생성 페이지(`/b2c/test-data`) 표시. 모드 토글 b2b→b2c 전환도 동일 착지. B2B 진입 → `/data-generator` 불변. (스크린샷)
- **[기본 상태] V2 — 그리드 기본 렌더**: G1(생성 결과)·G2(배정 대상)·G3(미할당 오더)가 기존과 동일하게 렌더(하이라이트 없음·메뉴 닫힘·기존 체크 동작 유지). (스크린샷)
- **[대체 상태] V3 — 드래그 범위 하이라이트**: 각 그리드에서 드래그로 연속 행 범위가 하이라이트(체크박스 상태와 시각 구분). 텍스트 드래그 선택 억제 확인(OQ-2). (스크린샷·before/after)
- **[대체 상태] V4 — 컨텍스트 메뉴 열림**: 그리드 본문 우클릭 → 4항목 메뉴 표시(브라우저 기본 메뉴 대체·그리드 내부만, OQ-3). 그리드 밖 우클릭 → 네이티브 유지. (스크린샷)
- **[대체 상태] V5 — 메뉴 액션 적용 후**: ③ 선택행 체크(하이라이트 행만 체크됨) / ④ 선택행 해제 / ① 전체 선택 / ② 전체 해제. **비활성 행(G2 비활성 슈트·셀, G3 진행 중 오더)은 ①③에서 체크되지 않음** 실증. (단계별 번호 스크린샷)
- **[빈/에러 상태] V6**: 오더/배치 0인 빈 그리드에서 우클릭 메뉴·드래그·전체선택(=0건) 안전(크래시/콘솔 0). 로딩/에러 행 위 상호작용 무해.
- **[다크모드] V7 — N/A**: 프로젝트는 단일 라이트 테마(`frontend/src/index.css` "단일 테마, 다크모드 없음", `prefers-color-scheme`/`dark:`/`data-theme` 미사용) → 다크모드 변형 없음. 라이트 테마에서 하이라이트/메뉴/체크 대비(가독성) 확인으로 대체.
- **[핵심 상호작용 흐름] V8**: 드래그 하이라이트 → 우클릭 → ③ 선택행 체크 → 기존 액션 버튼(G1 초기화(N)/ G2·G3 배정·해제 카운트)이 새 체크수 반영. G1 행 클릭(디테일 로드)·G2 소터 펼침·개별 체크박스가 드래그/메뉴와 충돌 없이 공존. 하이라이트 초기화 시점(OQ-4: refetch/라우트/스코프) 관측.

=== Applicable Backend/API scenarios (백엔드 표면) ===
- **N/A** — 순수 프론트 스프린트. 백엔드 엔드포인트/서비스/스키마/마이그레이션 추가·변경 0(제약). 검증은 기존 360 스위트 회귀 GREEN + 백엔드 diff 0 확인으로 갈음.

=== End-to-end data-flow scenario (2+ 레이어 교차) ===
- **V9 — 선택 UX → 기존 벌크 백엔드 → refetch 반영**: 설비 관리에서 (a) G3 미할당 오더 드래그 하이라이트 → 우클릭 ③ 선택행 체크, (b) G2 대상 체크 → **배정** → 백엔드 `/api/b2c/facility/orders/assign` 왕복 → refetch 후 좌 현재배정·우 목록·데이터 생성 디테일에 반영. 이어 (c) G2 대상 드래그 하이라이트 → 우클릭 ③ 체크 → **해제** → `/orders/unassign` 왕복 → 미할당 복귀. 즉 새 선택 메커니즘이 기존 벌크 경로(FE↔API↔DB)를 정확히 구동함을 교차 레이어로 실증(진행 중 오더 스킵 가드 등 기존 동작 회귀 0).

> Planner self-check — Detected project type: Full-stack. Required scenario slots: 7 (Web/UI default-state[V1,V2], Web/UI alternate-states[V3,V4,V5], Web/UI empty·error[V6], Web/UI dark-mode[V7 = N/A 근거명시], Web/UI key-interaction[V8], Backend/API[N/A 근거명시 — 프론트 전용], E2E cross-layer[V9]). All slots filled: yes.

## 게이트 확정 (사용자, 2026-07-15 — OQ 최종 답)
- **OQ-1 = 로드된 행 기준**: 드래그/전체선택은 DOM에 렌더된 행(≤take=1000) 대상, 가상화 없음, 기존 truncation 배너로 캡 고지.
- **OQ-2 = 텍스트선택 억제 + 스크롤 추종**(엣지 자동스크롤은 이번 제외).
- **OQ-3 = 그리드 본문 내부에서만** 우클릭 네이티브 메뉴 대체.
- **OQ-4 = id-키 하이라이트**: refetch 시 prune, 라우트/스코프/필터 변경 시 전체 리셋.
- **OQ-5 = b2b도 포함**: 공용 훅/ContextMenu 프리미티브를 b2b DataGeneratorPage의 SummaryGrid·DetailGrid에도 결선(전체 UX 일관). 검증에 b2b 그리드 브라우저 클릭스루 포함.
- **랜딩(R1)**: homePathFor('b2c') → /b2c/test-data(데이터 생성). b2b 불변. S-B2C-UX Minor #1 해소.
