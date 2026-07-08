# Sprint Contract — S-B2B-2c (데이터 생성 페이지 A4 라벨 인쇄 + 고급 다중선택 + 설정 페이지 부활)

> Planner Subagent · 2026-07-08
> B2B-2b(PR 병합분)에서 이연됐던 데이터 생성 페이지의 2개 조각(A4 라벨 인쇄 · 포인터 고급 다중선택)을
> 구현하고, 현재 비활성(`B2B-3` 배지)인 4번째 B2B 내비 항목 **설정**을 점등해 **인쇄 설정** 화면으로 부활시킨다.
> 원본 청사진(`docs/PROGRAM_STRUCTURE.md` §6.1·§6.4·§7.1·§7.4)의 인쇄/설정 동작을 **재현**하되(복사 아님),
> **자동생성 설정은 이식 대상 아님**(불변 결정 — 절대 추가하지 않는다).

---

## ⚠ Questions for user (착수 전 확인 — 각 항목에 권장 기본값. 답이 없으면 기본값으로 진행)

원본 문서가 **명시**한 점(추가 질문 불요, 확정 사실로 계약에 반영):
- **DUAL 바코드가 인코딩하는 두 값 = `barcode`(상단) + `barcode2`(하단)**. `PROGRAM_STRUCTURE.md`
  L530·L602가 "`barcode2` 있으면 좌측을 상/하 두 블록으로 분기해 듀얼 바코드 인쇄"라고 명시.
  `barcode2`가 없으면 단일 바코드. 5컬럼 엑셀 양식(`BizDay,Batch,Barcode,Barcode2,ChuteNo`)·
  `DetailRow.barcode2:string|null`이 뒷받침. → **추측 아님·확정.**
- **라벨 정밀 치수 = A4 2열×4행(8칸/페이지), 라벨 99.14×67.48mm, 페이지 여백 상13.97/하13.03/좌4.83/우4.94mm,
  모서리 반경 4mm**(L599). → 이 기하는 재현 대상(source of truth).

진짜 갈림길(사용자 확인 요망):

- **Q1 — 인쇄 설정이 실제로 제어하는 범위(가장 중요).** 원본은 모순을 안고 있다: `handlePrint`는
  A4 2×4 / 99.14×67.48mm를 **하드코딩**했는데, Settings(§6.4)의 `PrintWidth/PrintHeight` 프리셋은
  100×100 / 100×150 / 80×40(mm)이라 A4 2×4 라벨과 무관한 별개 규격이다(원본에서 설정↔인쇄가 완전 결선되지 않았음).
  **권장 기본값**: **A4 2×4 / 99.14×67.48mm를 정본(canonical) 라벨 레이아웃으로 픽셀-정확히 재현**하고,
  설정은 (a) **바코드 심볼로지 선택**(기본 `CODE128`), (b) **바코드 값 텍스트 표시 on/off**,
  (c) **라벨 프리셋 선택기**(기본이자 필수 옵션 = "A4 2×4 (99.14×67.48mm)")를 호스팅.
  원본의 100×100/100×150/80×40 프리셋으로의 **레이아웃 재-플로우는 이번 스코프에서 제외**(선택기에 항목으로
  노출은 가능하나, 반드시 정확히 렌더돼야 하는 "must-work" 대상은 A4 2×4 하나). 재-플로우가 필요하면 후속 스프린트.
- **Q2 — 설정 영속화 위치.** **권장 = localStorage**(백엔드 설정 계약이 없다 — 자동생성 설정을 이식하지
  않으므로 `/test-data/auto-config` 서버 저장 경로를 되살릴 이유가 없음). `uiMode.ts`/`UiModeProvider`의
  화이트리스트-가드 localStorage 패턴을 그대로 미러링. 백엔드 서버 영속화는 **하지 않음**(불필요·스코프 확대).
- **Q3 — 설정 페이지가 인쇄 설정만 담는가, 일반 환경설정도 담는가.** **권장 = 인쇄 설정 전용.**
  기존 UiMode 토글(업무일자·자동새로고침·간격)은 이미 B2B 헤더(`B2bHeaderControls`)에 살아 있으므로,
  설정 페이지에 중복 배치하면 **이중 소스**가 된다. 설정 페이지는 인쇄에 집중하고 헤더 토글은 건드리지 않는다.

> 아래 계약 본문은 Q1/Q2/Q3의 **권장 기본값이 확정됐다는 가정**으로 작성됐다. 사용자가 다른 선택을 하면 해당 슬롯만 갱신한다.

---

## Goal (목표)

B2B 데이터 생성 페이지에서 **선택한 테스트데이터 행을 A4 라벨(2×4, 99.14×67.48mm, DUAL 바코드)로 인쇄**할 수
있게 하고, **드래그-선택 / Shift-범위 / Ctrl(Cmd)-토글 포인터 다중선택**을 상세 그리드에 추가하며,
비활성 상태인 **설정** 내비를 점등해 **인쇄 설정**(심볼로지·값표시·라벨 프리셋)을 관리하는 화면으로 부활시킨다.
전 기능은 **폐쇄망(인터넷 미연결)에서 동작**해야 한다 — 바코드 라이브러리를 로컬 번들/벤더링하고 외부 요청 0.

---

## Implementation Scope (Generator가 구현할 것 — WHAT, not HOW)

### Part A — A4 라벨 인쇄 (데이터 생성 페이지)
- A1. 데이터 생성 페이지(`DataGeneratorPage`)의 **상세 그리드(선택된 배치)**에 **인쇄 버튼**을 추가한다.
  선택된 상세 행(`detailChecked` 집합)을 인쇄 대상으로 삼는다. 삭제 버튼과 동일한 위치·컨벤션(아이콘+선택 수 배지)을 따른다.
- A2. **A4 라벨 레이아웃을 정밀 재현**: 2열×4행(8칸/페이지), 라벨 99.14×67.48mm, 페이지 여백
  상13.97/하13.03/좌4.83/우4.94mm, 모서리 반경 4mm. 8칸 초과 선택 시 **페이지 분할**(다음 A4 페이지로 넘김).
- A3. **DUAL 바코드**: 각 라벨은 `barcode`를 렌더하고, `barcode2`가 존재하면 상/하 두 블록으로 분기해
  `barcode`(상)+`barcode2`(하) 두 바코드를 그린다. `barcode2`가 null이면 단일 바코드. 라벨에 `chuteNo`(3자리
  zero-pad 표기)를 함께 표시한다(원본 라벨 텍스트 재현 — 그 밖의 표기 필드는 Generator 재량, 원본과 크게 벗어나지 않게).
- A4. **바코드 렌더는 로컬 벤더링**(폐쇄망 필수): 바코드 라이브러리를 npm 의존성으로 추가해 앱 번들에 포함(권장)
  하거나 `public/`에 UMD 로컬 사본을 배치한다 — **어느 방식이든 외부 CDN/네트워크 요청 0**이어야 한다.
  렌더 결과는 SVG 또는 canvas. (구현 방식은 Generator 결정; Planner는 "외부 요청 0" 결과만 요구.)
- A5. **인쇄 트리거**: 선택 0건이면 인쇄하지 않고 토스트로 안내(원본 동작 재현). 선택 있으면 A4 인쇄 뷰/프리뷰를
  띄우고 브라우저 인쇄 대화상자를 호출한다(실제 프린터 대화상자 자동화는 불가하나 **인쇄용 DOM/프리뷰는 검증 가능**해야 함).
- A6. **인쇄 설정 소비**: A2~A3 렌더는 설정 페이지(Part C)에서 저장한 심볼로지·값표시·프리셋을 반영한다.
- A7. **XSS 안전**: 라벨 텍스트는 React 렌더(또는 `textContent`) 경로로만 삽입 — `innerHTML` 직접 조립 금지(원본 원칙 재현).

### Part B — 고급 포인터 다중선택 (상세 그리드)
- B1. 기존 체크박스 선택(B2B-2b에서 shipped된 행별 체크 + "보이는 전체 선택")을 **유지**하면서, 상세 그리드
  행에 **포인터 제스처 선택**을 추가한다. 제스처는 기존 선택 집합(`detailChecked: Set<number>`)을 직접 조작해야 하며,
  인쇄(Part A)·삭제(기존)가 동일 집합을 소비한다.
- B2. **일반 클릭/드래그**: 행을 클릭하면 단일 선택 + 드래그 시작, 드래그(포인터가 다른 행 위로 이동)하면
  연속 범위를 실시간 갱신. **그리드 밖에서 마우스를 떼도** `window` 레벨 종료로 드래그가 정상 종료돼야 한다.
- B3. **Shift+클릭**: anchor(직전 클릭 행)~클릭 행까지 연속 범위 선택.
- B4. **Ctrl/Cmd+클릭**: 개별 행 토글을 누적(비연속 선택).
- B5. 행 체크박스 자체 클릭, 컬럼 필터 입력 클릭 등 기존 상호작용과 **버블링 충돌이 없어야** 한다(기존
  `FilterInput`의 `stopPropagation` 패턴 존중). 필터로 숨겨진 행은 선택 대상에서 제외(보이는 행 기준).

### Part C — 설정 페이지 부활 (인쇄 설정)
- C1. `Layout.tsx`의 b2b `NAV_SETS`에서 **설정** 항목(현재 `enabled:false`·`phase:'B2B-3'`)을 **활성화**하고
  `/settings` 라우트를 `App.tsx`에 등록한다. 새 페이지 컴포넌트를 추가한다.
- C2. 설정 페이지는 **인쇄 설정**을 호스팅: (a) 바코드 심볼로지 선택(기본 CODE128), (b) 바코드 값 텍스트
  표시 on/off, (c) 라벨 프리셋 선택기(기본·정본 = "A4 2×4 (99.14×67.48mm)"). 원본 §6.4의 "용지 비율 시각화"류
  미리보기는 있으면 좋으나 필수 아님(Craft 가점). **자동생성 규칙 관련 UI는 절대 추가 금지.**
- C3. **영속화 = localStorage**(Q2 권장). `uiMode.ts`의 화이트리스트-가드 로드/세이브 패턴을 미러링해
  손상값·범위이탈은 기본값 폴백(앱 크래시 0). 저장 후 **새로고침(reload) 시 값이 유지**돼야 한다.
- C4. 설정 값은 Part A 인쇄가 소비하는 **단일 소스**여야 한다(설정 변경 → 다음 인쇄에 반영).

### 공통 제약
- 폐쇄망: 바코드 라이브러리 로컬화, **외부 CDN/네트워크 요청 0**. (레포에 현재 CSP 강제는 없으나, 향후 CSP
  `script-src 'self'` 하에서도 동작하도록 self-호스팅만 사용 — 인라인 `eval`/원격 스크립트 금지.)
- B2C(모니터링·소터) 화면·동작 **무접촉**. `Layout.tsx` 수정은 b2b `NAV_SET` 항목 활성화 + 라우트 추가로 한정.
- 기존 스타일·컨벤션·한글 주석 준수. 기존 UI 인프라(`components/ui/*` — button/select/card/dialog/table 등) 재사용.
- 백엔드 무접촉(아래 검증 슬롯의 git-diff 증거 요구).

### Out of Scope (명시적 제외)
- 자동생성 규칙 설정(모드 라디오·슈트범위·슈트당개수·고정바코드·미리보기)·`/test-data/auto-config` 엔드포인트 부활 — **이식 안 함(불변 결정).**
- 원본 100×100/100×150/80×40 라벨 프리셋으로의 레이아웃 재-플로우(Q1 기본값 — 후속).
- 우클릭 컨텍스트 메뉴 기반 "선택영역 반영"(원본 §6.1의 2단 선택 모델) — 포인터 제스처가 `detailChecked`를
  직접 조작하므로 불필요. 도입 시 별도 스프린트.
- 백엔드 코드·스키마·마이그레이션 변경. 기존 B2B 조회/관리 API 변경.

---

## Detected Project Type: Full-stack

프로젝트 신호: 브라우저 진입점(`frontend/` React SPA + `index.html`)과 서버 라우트/컨트롤러
(`backend/src/Wcs.Api` ASP.NET Core Controllers)가 **동일 레포**에 공존 → Full-stack.

**단, 이번 스프린트의 변경 표면은 프론트엔드 전용이다**(인쇄·선택·설정 = 클라이언트 렌더 + localStorage).
백엔드는 **무접촉**(인쇄 뷰가 소비하는 `/api/test-data/detail` 데이터는 기존 API가 그대로 제공 — 검증을 위해
**실행**은 하되 **수정하지 않는다**). 따라서 Backend/API 검증 슬롯은 N/A + git-diff 무변경 증거로 대체한다.

---

## Verification Scenarios (Full-stack — 필수)

> 포트 source of truth: `.claude/ports.local.json` 부재(현재 없음). 계약이 **`frontend/vite.config.ts`의
> `server.port`(5173) + dev proxy 대상(백엔드 5205)** 를 포트 source-of-truth로 명시 허용한다
> (S-B2B-3b 선례 — 하드코딩 아님). Evaluator는 이 값에서 URL을 구성한다.
> 현장 DB 오염 3중 차단(S-B2B-3b/lessons 2026-07-03 준수): 백엔드를 **별도 scratch SQLite 파일**(Generator와
> 다른 이름)·`Database__Provider=Sqlite` override·`Sorters__0__Transport=Tcp`(COM1/RTU 실 3DS PLC 무접촉)로
> `ASPNETCORE_ENVIRONMENT=Production`(SeedOnStartup=false) 기동 후 python sqlite3로 시드한다.

### === Applicable Web/UI scenarios (프론트 변경 표면) ===

- **각 surface의 기본 상태(default state)**:
  - W1. 데이터 생성 페이지 상세 그리드 기본(선택 0) — 인쇄 버튼이 **비활성**, 기존 체크박스/전체선택 정상 렌더.
  - W2. **설정** 페이지 기본 — 인쇄 설정 폼이 기본값(프리셋="A4 2×4 (99.14×67.48mm)", 심볼로지=CODE128, 값표시 on)으로
    렌더. b2b 내비의 **설정 항목이 점등**(더 이상 `B2B-3` 배지·`cursor-not-allowed` 아님)되고 `/settings`로 라우팅됨.

- **이 스프린트가 도입하는 각 대체 상태(alternate states)**:
  - W3. **포인터 선택 상태**: (a) 상세 그리드에서 여러 행 위로 **드래그** → 연속 범위가 선택(하이라이트+체크)되고 선택 수
    배지가 증가; (b) **Shift+클릭** → anchor~클릭 연속 범위 선택; (c) **Ctrl/Cmd+클릭** → 개별 행 비연속 토글.
    각 제스처 후 인쇄/삭제 버튼이 활성화되고 카운트가 정확.
  - W4. **인쇄 프리뷰 상태**: 선택 있는 상태에서 인쇄 실행 → A4 라벨 인쇄 뷰가 렌더. `barcode2` 보유 행은 **DUAL 바코드**
    (barcode+barcode2 두 개의 바코드 엘리먼트), 미보유 행은 단일 바코드, `chuteNo` 표기 존재. 라벨 기하(2열, 라벨
    99.14×67.48mm에 해당하는 치수/그리드 클래스), 8칸 초과 시 페이지 분할이 DOM에서 확인됨.
  - W5. **설정 변경·영속 상태**: 심볼로지/값표시/프리셋을 변경·저장 → **페이지 새로고침 후에도 값 유지**
    (localStorage `wcs.ui` 또는 인쇄 전용 키). 변경한 설정이 이후 인쇄 프리뷰(W4)에 반영됨.

- **이 스프린트가 표출하는 빈/오류 상태(empty / error state)**:
  - W6. **선택 0 인쇄 가드**: 아무것도 선택하지 않고 인쇄 시도 → 인쇄 뷰가 뜨지 않고 토스트 경고("인쇄할 항목을 선택하세요"류).
    설정 폼에 유효하지 않은 입력이 가능한 필드가 있으면(예: 범위 이탈 프리셋 값) 크래시 없이 폴백/거부.

- **다크 모드 변형**:
  - N/A — 앱은 라이트 전용(`index.html` `color-scheme: light`, `src/` 전역에 `dark:`·`prefers-color-scheme`·
    `data-theme`·테마 토글 **0건**). 다크 변형 검증 대상 없음.

- **변경 후 핵심 상호작용 흐름(사용자에게 보이는, 이 스프린트가 만들어내는 동작)**:
  - W7. B2B 모드 → 데이터 생성 → 배치 선택(상세 로드) → **드래그/Shift/Ctrl로 행 선택** → **인쇄** → A4 DUAL 바코드
    라벨 프리뷰 확인 → 내비 **설정** 이동 → 심볼로지 변경·저장 → 데이터 생성으로 복귀·재인쇄 → 프리뷰에 변경 반영.
    (각 단계 번호 스크린샷 + 콘솔 `[N/total]` 로그 — Evaluator click-through 의무.)

### === Applicable Backend/API scenarios (백엔드 변경 표면) ===

- **N/A — 이 스프린트는 백엔드 엔드포인트를 추가·변경하지 않는다.**
  증거 요구(무접촉 판정): `git diff --numstat develop -- backend/` **빈 출력**(또는 0/0) + `git status`에
  신규 migration/ModelSnapshot 변경 **0건**. 백엔드는 인쇄 뷰가 소비할 `/api/test-data/detail`(기존)을 제공하기
  위해 **실행만** 하며 수정하지 않는다. (스키마·계약 무훼손이 이 슬롯의 통과 조건.)

### === End-to-end 데이터 흐름 시나리오(2계층 이상 교차) ===

- **E1**. 시드된 상세 데이터(백엔드 `GET /api/test-data/detail`, 무수정)가 → 프론트 상세 그리드로 흘러 렌더되고
  (그중 최소 1행은 `barcode2` 보유) → 사용자가 **포인터 제스처로 부분 선택** → **인쇄**하면, 선택한 **바로 그 행들**이
  A4 라벨 뷰에 DUAL/단일 바코드로 렌더된다. 즉 데이터가 **백엔드→프론트 그리드→인쇄 DOM**을 관통함을 검증.
  (백엔드는 무수정·scratch DB·Tcp override로 실행; 현장 DB 무접촉.)

> Planner self-check — Detected project type: Full-stack. Required scenario slots: 3 (Web/UI[W1~W7], Backend/API[N/A+git-diff 증거], End-to-end[E1]). All slots filled: yes.

---

## Evaluation Criteria (Evaluator 판정 기준 · Full-stack)

1. **Integration Quality (★★★)** — 백엔드 상세 데이터 → 그리드 → 선택 집합 → 인쇄 DOM의 데이터 흐름이
   일관(E1). 선택 집합(`detailChecked`)이 인쇄·삭제·체크박스·포인터 제스처의 **단일 소스**. 설정 값이 인쇄가
   소비하는 단일 소스. **백엔드 무접촉**(git-diff 증거).
2. **Per-layer Quality (★★★)** — 프론트(Web/UI 기준): 인쇄 라벨의 디자인/기하 정확성(A4 2×4, 99.14×67.48mm),
   포인터 선택의 사용성, 설정 폼의 완성도. 기존 페이지 스타일·컨벤션과 시각적 일관. AI-슬롭 아닌 의도적 재현.
3. **Craft (★★)** — 타입 안전(tsc clean), lint clean, 화이트리스트-가드 localStorage(손상값 폴백), 드래그
   종료(window mouseup) 등 엣지 처리, XSS 안전 렌더, 필터-숨김 행 선택 제외, 페이지 분할.
4. **Functionality (★★)** — 인쇄 실제 렌더(바코드 엘리먼트 존재)·포인터 3제스처 정확·설정 영속·내비 점등이
   실사용 경로로 동작. **폐쇄망(외부 요청 0)**. B2C 회귀 0.

### 필수 검증(스킵 불가 — workflow-agents §Evaluator)
- **정적 검사 독립 재실행**: `npm run build`(tsc --noEmit + vite build) 통과, `npm run lint`(eslint) clean,
  `npm run typecheck` clean. 결과를 sprint-feedback.md에 raw로 기록.
- **기존 테스트 green**: `dotnet test backend/Wcs.sln` 회귀 0(백엔드 무접촉 실증). 프론트 테스트가 있으면 재실행.
- **Playwright 브라우저 click-through(의무)**: W1~W7 각 시나리오를 navigate→click/drag/shift/ctrl→assert로
  코드 재현. 특히:
  - 포인터 제스처: 실제 드래그(pointer down→move over rows→up), Shift+click, Ctrl/Cmd+click을 구동하고
    선택 카운트·하이라이트를 단정.
  - 인쇄: 인쇄 뷰/프리뷰 DOM에서 **바코드 엘리먼트(svg/canvas/img) 개수**(DUAL 행=2, 단일 행=1), A4 그리드
    기하(2열·라벨 치수 관련 computed style/클래스), 페이지 분할을 단정 + 스크린샷 이미지 판독. (프린터 대화상자
    자동화 불가 → 인쇄용 DOM/프리뷰 단정으로 갈음.)
  - 설정 영속: 값 변경→저장→`reload()`→값 유지 단정 + localStorage 값 확인.
  - 내비: 설정 항목이 점등·라우팅됨 + B2C 토글 시 b2b NAV 원복·MonitorPage 정상(회귀 0).
- **콘솔/네트워크 캡처(BLOCKING)**: `page.on('console')`+`page.on('pageerror')`를 `screenshots/{sprint}/console.log`에
  저장. React dev 경고·pageerror·의도치 않은 4xx/5xx는 스크린샷이 정상이어도 FAIL.
- **폐쇄망 0-외부요청(BLOCKING)**: `page.on('request')` 또는 network 로그로 **애플리케이션 출처(self/proxy) 밖
  요청 0**을 실증(바코드 라이브러리가 CDN이 아닌 로컬에서 로드됨). 폰트 등 기존 자산의 시스템 폴백은 회귀 아님.
- **번호 스크린샷**(`01-*.png` … `NN-end.png`) + `screenshots/{sprint}_{YYYYMMDD-HHMMSS}/` 저장(덮어쓰기 금지).
- **검증 산물 정리**: MCP 산출물(스크린샷·console·network)·scratch DB·오펀 프로세스(5173/5205/1502)를 정리해
  핸드오프 시점 `git status`를 원복(S-B2B-3b lessons).

---

## Completion Conditions (Evaluator PASS 최소 조건)

1. `npm run build`·`npm run lint`·`npm run typecheck` 전부 clean(경고 0 목표; 기존 부채 경고는 귀속 분리).
2. `dotnet test backend/Wcs.sln` 회귀 0.
3. W1~W7 전 시나리오 Playwright click-through 통과 + 번호 스크린샷 판독으로 시각 확인.
4. 인쇄: 선택 행이 A4 2×4 / 99.14×67.48mm 라벨로 렌더, DUAL(barcode+barcode2)/단일 바코드 정확, chuteNo 표기,
   8칸 초과 페이지 분할, 선택 0 가드 동작.
5. 포인터 선택: 드래그-범위 / Shift-범위 / Ctrl(Cmd)-토글 3제스처 정확 + 그리드 밖 mouseup 종료 + 기존
   체크박스/전체선택 병존 + 필터-숨김 행 제외.
6. 설정: 내비 점등·`/settings` 라우팅, 인쇄 설정 폼 렌더, 변경값 reload 후 유지(localStorage), 인쇄에 반영.
   **자동생성 UI 0건**.
7. 폐쇄망: 브라우저 세션 중 외부 출처 네트워크 요청 0(바코드 라이브러리 로컬 로드).
8. 콘솔 error/warning/pageerror/React 경고 0(정상 페이지 기준).
9. **백엔드 무접촉**: `git diff --numstat develop -- backend/` 빈 출력 + migration/snapshot 변경 0.
10. B2C 무접촉: 모드 토글 후 b2b NAV 원복 + MonitorPage/StatusRail 정상 렌더(코드 diff만으론 불충분·브라우저 실측).

---

## Parallel Modules (Generator fan-out)

**N/A (단일 모듈).** Part A(인쇄)·Part B(포인터 선택)·Part C(설정)는 boundary-clean하지 않다 —
A와 B는 `DataGeneratorPage`/`DetailGrid`의 동일 선택 상태·동일 그리드를 공유하고, A와 C는 인쇄-설정 계약을
공유한다. 동시 파일 쓰기 충돌 위험 → 단일 Generator 순차 구현.

## Evaluation Dimensions (Evaluator expert pool)

**functional only.** 단, 기능 검증 안에 **폐쇄망(0 외부요청)** 과 **B2C/백엔드 무접촉**을 BLOCKING 항목으로 포함.
보안/성능 전용 병렬 평가는 불요(프론트 렌더·localStorage 표면, 신규 서버 표면 0).

---

## Absolute Rules (위반 = FAIL)
- **폐쇄망**: 외부 CDN/네트워크 요청 0. 바코드 라이브러리는 self-호스팅(번들 또는 `public/` 로컬 사본)만.
- **B2C 무접촉**: 모니터링·소터·StatusRail·헤더 B2C 경로 변경 0. `Layout.tsx`는 b2b NAV 활성화 + 라우트 추가만.
- **백엔드 무접촉**: `backend/` diff 0 (git-numstat 증거). 스키마/마이그레이션/기존 API 무훼손.
- **자동생성 설정 미이식**: 자동생성 규칙 UI·엔드포인트를 절대 추가하지 않는다(불변 결정).
- 기존 스타일·컨벤션·**한글 주석** 준수. 기존 UI 인프라 재사용(신규 UI 프리미티브 남발 금지).
- Generator/Evaluator는 `git push`/`--no-verify`/브랜치 삭제/`CLAUDE.md`·`tasks/workflow-*.md` 수정 금지.

> Planner self-check — Detected project type: Full-stack. Required scenario slots: 3 (Web/UI, Backend/API, End-to-end). All slots filled: yes.
