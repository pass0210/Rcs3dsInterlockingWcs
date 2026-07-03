# Sprint Contract — S-FE-AIRBNB (프론트 Airbnb 리스타일 · 스타일 전용)

> **스타일 전용 스프린트.** DESIGN-airbnb.md 토큰을 기존 관제 콘솔에 적용한다. **로직·구조·데이터흐름·API·라우팅 0 변경.**
> WHAT/WHERE/검증만 규정 — 정확한 hex 미세조정·유틸리티 vs @theme 토큰 선택 등 구현 메커니즘은 Generator 재량(단, 문서 토큰·의미 보존 제약 내).
> 스타일 스펙의 단일 진실 = **`docs/DESIGN-airbnb.md`**. `docs/FRONTEND.md`(★확정 shadcn/ui+Tailwind 유지)는 스택 계약.

## 0. 메타

| 항목 | 값 |
|------|-----|
| Sprint ID | S-FE-AIRBNB |
| Branch | `feat/frontend-airbnb-restyle` |
| Base | `refactor/backend-folder` (backend/ 구조 — vite outDir `../backend/src/Wcs.Api/wwwroot` 확인) |
| Detected Project Type | **Full-stack**, 단 이 스프린트는 **Web/UI 전용**(frontend/ 스타일만) |
| Scaling | **1 Planner / 1 Generator / 1 Evaluator** (단일 리스타일 — 팬아웃 없음. Evaluator가 Web/UI 슬롯 채움) |
| Test baseline | **161 GREEN** (backend 무변경 → 회귀 대상 아님, git diff 0로 입증) |
| 검증 도구 | `.mcp.json` Playwright(존재 확인, `--headless`) — 스크린샷 대조 필수 |
| 선례 | F1(`tasks/feedback-archive.md` L326~) 프론트 스캐폴드 · S-BACKEND-FOLDER(순수 이동 diff 0 가드 패턴) |

## 1. 목표 (WHAT · 한 줄)

기존 "블루프린트 그래파이트" **다크 계기판 테마**를 DESIGN-airbnb.md의 **순백 캔버스 + 잉크 + Rausch 단일 액센트** 라이트 테마로 리스타일한다. 컴포넌트 구조·데이터 흐름·API·라우팅은 **바이트 단위로 불변**이고, 변경은 `index.css`의 `@theme` 토큰 값·컴포넌트/페이지의 `className`·`package.json`(폰트)·`index.html`(color-scheme)로 **국한**한다.

**핵심 레버리지**: 이 앱은 shadcn 표준 `:root{--primary}` HSL 레이어를 쓰지 않고 **Tailwind4 CSS-first `@theme --color-*` 커스텀 토큰**(`base·panel·elevated·line·ink·muted·faint·accent·online·offline·warn·busy`)을 직접 쓴다. 컴포넌트가 이 시맨틱 토큰명(`bg-panel`·`border-line`·`text-ink`…)을 참조하므로, **`@theme` 토큰 값 재매핑 하나로 다크→라이트가 전면 전환**되고 className 변경은 (a) 문서와 의미가 어긋나는 곳(활성 탭/내비, 라운딩, 그림자, 배지 필형)에만 국한된다.

## 2. Scope IN

### 2A. `frontend/src/index.css` — `@theme` 토큰 재매핑 (최대 레버리지)

문서 토큰은 **고정**(canvas/ink/hairline/muted/Rausch). 상태 의미색(green/amber/info)은 문서 미정의 도메인 색 → **백 배경 대비(WCAG AA)·상호 톤 구분·Rausch와 톤 구분** 제약 하에 재조정(Generator 미세조정 허용). 권고 목표값:

| 토큰(기존명 유지) | 현재값(다크) | 목표값 | 문서 근거 |
|---|---|---|---|
| `--color-base` | `#0d1520` | `#f7f7f7` | surface-soft (앱 셸 바닥 — 백 카드가 도드라지게) |
| `--color-panel` | `#131e2c` | `#ffffff` | **canvas** (카드/서피스) |
| `--color-elevated` | `#18273a` | `#f7f7f7` | surface-soft (헤더/thead/hover 채움) |
| `--color-line` | `#24344c` | `#dddddd` | **hairline** (기본 경계) — 미세 구분엔 `/60` 등 알파 |
| `--color-ink` | `#e7eef8` | `#222222` | **ink** (1차 텍스트) |
| `--color-muted` | `#8fa3c0` | `#6a6a6a` | **muted** (2차) |
| `--color-faint` | `#5f7290` | `#929292` | muted-soft (3차/캡션) |
| `--color-accent` | `#38bdf8`(sky) | `#2563eb`(인디고) | **브랜드 아님** — in-progress 정보색(RESERVED/QUERIED/SENT). 백 대비 확보 |
| `--color-busy` | `#22d3ee`(cyan) | `#0e7490`(틸) | in-progress 정보색(이동중/분류중/근접). accent와 톤 구분 |
| `--color-online` | `#34d399` | `#0a7d33`(녹, 재조정) | 결정 #3 — ONLINE/COMPLETED/여유. **백 대비 확보**(기존 에메랄드는 백에서 저대비) |
| `--color-offline` | `#fb7185` | `#c13515` | 결정 #3 + 문서 **error** — OFFLINE/MISMATCH. **Rausch와 톤 구분(⑥)** |
| `--color-warn` | `#fbbf24` | `#b45309`(황, 재조정) | 결정 #3 — FULL. 백 대비 확보 |
| **신규** `--color-brand` | — | `#ff385c` | **Rausch** — 주요 액션/브랜드/포커스/활성 마커 전용(절제) |
| **신규** `--color-brand-active` | — | `#e00b41` | 프레스 |
| **신규** `--color-brand-disabled` | — | `#ffd1da` | 비활성 CTA |
| **신규** `--color-paused` | — | `#6a6a6a`(회) 또는 `#b45309`(황) | 결정 #3 — PAUSED, **OFFLINE 적과 구분**(StatusRail PAUSE 램프용) |

- **폰트**: `--font-sans` → `'Inter Variable','Inter','Malgun Gothic','Apple SD Gothic Neo',system-ui,-apple-system,sans-serif` (Inter 로컬 번들 + **한글 폴백 명시** — Inter엔 한글 글리프 없음, 타깃 Windows = Malgun Gothic). `--font-mono` **유지**(판독값 tabular-nums 계기 정서 — 색/브랜드 무관, 밀집 가독 기능).
- **라디우스**: 문서(버튼 8px·카드 14px·필/배지 full) 반영 — @theme radius 토큰 재정의 또는 컴포넌트 arbitrary. ⚠ Tailwind 기본 `rounded-lg`=8px라 **바 재매핑만으론 카드 14px 미달**(§5 함정).
- **그림자**: 문서 **단일 티어** `0 0 0 1px rgba(0,0,0,.02), 0 2px 6px 0 rgba(0,0,0,.04), 0 4px 8px 0 rgba(0,0,0,.1)` — @theme shadow 토큰 또는 arbitrary로 정의, Card·StatusRail 타일·Select 드롭다운에 적용. 기존 다크 인셋 그림자 제거.
- **다크 잔재 제거/재조정**: body `background-image` **블루프린트 그리드 제거**(문서: 평면 백, 깊이는 헤어라인). `::selection` sky→Rausch/중립 은은. 스크롤바 다크(`#2c3f5b`)→라이트(`#c1c1c1` 계열). `:focus-visible` accent→ink 또는 brand(가시성 유지). `lamp-pulse`는 `currentColor` 기반이라 구조 불변(색은 램프 클래스에서 green으로).
- **폰트 번들 임포트**: `@import "@fontsource-variable/inter";`를 파일 상단 `@import "tailwindcss";` 인접에 추가(모든 @import는 최상단). ⇒ **`main.tsx`(로직 파일) 미변경** — 폰트 결선을 CSS에 둔다.
- **디스플레이 line-height ~2% 하향**(문서 §Note: Cereal→Inter 보정) — 헤딩 유틸(Layout h1·CardTitle) 적용.

### 2B. 컴포넌트/페이지 className — 문서와 의미 어긋나는 지점만 (구조 불변)

| 파일 | 현재 | 목표(문서 매핑) |
|---|---|---|
| `components/Layout.tsx` | 로고박스 `border-accent/30 bg-accent/10 text-accent`; 활성 내비 `bg-accent/15 text-accent shadow-[…inset]`; 비활성 `text-muted hover:bg-elevated` | 로고 마크=**brand(Rausch)** 1점 액센트; **활성 내비=잉크 텍스트 + Rausch 마커/언더라인**(accent-fill 배경 제거, product-tab-active); 비활성=muted, hover=surface-soft. F2/F3 disabled 칩 유지 |
| `components/StatusRail.tsx` | 타일 `bg-elevated`+`border-line`; PAUSE 램프 `tone="offline"`(적) | 타일=**property-card 유사**(white `panel` + hairline + **14px** + **단일 그림자**) + 상태 **필형 배지**; 램프 online=녹·FULL=황; **PAUSE 램프 tone=offline→paused(회/황)** — 결정 #3·**⑥ 필수**(OFFLINE 적과 구분). `Lamp` tone union에 paused 추가 허용(시각 상수) |
| `components/ui/card.tsx` | `rounded-lg`(8px)·`bg-panel/80`·다크 인셋 그림자 | **14px**·white·**문서 단일 그림자**·hairline 보더; Title 잉크·타이포 스케일(title-md/display-sm) |
| `components/ui/button.tsx` | `solid/outline/ghost`, `rounded-md`(6px) | **primary=Rausch fill·white·8px·height↑**(button-primary, F2/F3 CTA 상속); **secondary=white+잉크 아웃라인 8px**; ghost 재조정; 라디우스 8px |
| `components/ui/badge.tsx` | `rounded-md`(6px) | **`rounded-full`(필형)**(guest-favorite-badge); 톤(online/offline/warn/busy/accent/neutral) 백 배경 대비 재조정 |
| `components/ui/tabs.tsx` | 활성 `data-[state=active]:bg-accent/15 text-accent shadow-[…inset]` | **활성=잉크 텍스트 + 잉크 언더라인**(category-tab-active, accent-fill 제거); List 컨테이너 백/헤어라인 재조정 |
| `components/ui/table.tsx` | thead `bg-elevated text-faint`; TR hover `bg-elevated/50`; `border-line/60` | 백 서피스·**헤어라인 행 구분**·잉크 텍스트·thead surface-soft; hover surface-soft |
| `components/ui/select.tsx` | `bg-elevated`·`border-line`·`rounded-md`·hover accent | white/hairline·8px·포커스 잉크(text-input 유사) |
| `components/ui/meter.tsx` | 트랙 `bg-base` | 트랙=surface-strong(`#f2f2f2`) 가시화; 채움 online/busy/warn 시맨틱 유지(용량 게이지·진행바 **유지**) |
| `components/CursorPager.tsx`·`StateMessage.tsx`·`DataGrid.tsx` | 토큰 참조 | 대부분 @theme 재매핑으로 자동 전환 — 잔여 하드코딩 색/보더만 점검 |
| `pages/MonitorPage.tsx`·`pages/sections/*` | 토큰 참조 + 셀타일 `rounded-md`·서브행 `bg-base/50`·`bg-elevated/60` | 셀 그리드 타일=**14px 라운딩 + 용량 게이지 유지 + 상태색 보더**; 아이템 서브행 mono 색(busy/online) 백 대비 재조정 — 나머지 토큰 자동 전환 |
| `index.html` | `<meta name="color-scheme" content="dark">` | `content="light"` (다크모드 없음). title 불변. (선택)폰트 preload |
| `package.json` | — | `@fontsource-variable/inter` **의존성 추가**(사내망 — CDN 금지, npm 번들). dev/build 스크립트 불변 |

### 2C. Rausch 절제 원칙 (문서 §Overview — 화면의 ~90% 백+잉크)

Rausch(`brand`)는 **① 로고 마크 ② 활성 내비 마커 ③ primary 버튼(F2/F3 상속) ④ (선택)포커스 링**에만. **상태 배지·진행바·in-progress에는 Rausch 금지**(그 자리는 accent/busy 정보색·online/offline/warn 의미색). RESERVED/QUERIED가 Rausch로 물들면 절제 원칙·⑥ 위반.

## 3. Scope OUT (0 변경 — 무변경 가드)

- **backend/ 전체**: **0줄**(git diff 0). 161 GREEN 회귀 대상 아님.
- **`frontend/src/lib/*.ts`**: `api.ts`·`queries.ts`·`format.ts`·`utils.ts`·`status.ts` **0 변경**. 특히 `status.ts`(status→tone 매핑)와 `queries.ts`(POLL_MS·훅) 불변 — 색은 @theme 값·badge 클래스로만 재조정.
- **데이터 흐름·상태관리**: TanStack Query 훅·커서 페이징 스택·행 확장·useEffect 기본선택 로직 불변.
- **API·라우팅**: 엔드포인트·`App.tsx` 라우트·`vite.config.ts`(proxy/outDir) 불변.
- **컴포넌트 구조·props 계약**: JSX 트리·컴포넌트 분해·props 시그니처 불변(예외: StatusRail `Lamp` tone union에 시각 상수 1개 추가 — 데이터 무관).
- **`main.tsx`**: 불변(폰트는 index.css @import).
- **`.mcp.json`·`eslint.config.js`·`tsconfig.json`·`.storybook`(없음)**: 불변.
- **`docs/DESIGN-airbnb.md` 및 기타 문서**: 불변(문서가 스펙, 수정 대상 아님).

## 4. Deliverables & Evaluation Criteria (Completion Gate)

> **Fresh evidence 의무**: 모든 PASS는 "지금 실제로 돌린" raw 출력(Playwright 스크린샷 파일·`npm run build`/`tsc`/`eslint` 라인·`git diff --stat`)을 `tasks/sprint-feedback.md`에 인용. Generator 보고·추정만으론 PASS 금지.

**① 시각 적용 충실도 (최중요 · Playwright 스크린샷 ↔ 문서 토큰 대조)**
- 데스크톱 **1128px**(문서 Desktop 기준) 스크린샷: 모니터링 3탭(작업/이동중/분류) + 행 확장 + 상단 StatusRail.
- 대조 체크리스트: (a) **순백 캔버스**(#fff 지배·다크 잔재 0·블루프린트 그리드 제거) (b) **잉크 텍스트**(#222) (c) **Rausch 절제**(brand는 로고/활성마커/버튼에만, 상태색과 미혼용) (d) **라운딩**(카드/타일 14px·버튼/셀렉트 8px·배지 필형) (e) **헤어라인**(#ddd 행/카드 구분) (f) **단일 그림자 티어**(문서 정확 값) (g) **Inter 적용**(영문 Inter·한글 폴백 정상, 두부 없음).
- **Web/UI 슬롯 필수**: 기본 상태 + **대체 상태**(빈/로딩/에러 — StateMessage 3종 스크린샷) + **반응형**(1128px 데스크톱 위주; 축소 시 셀 그리드 컬럼 감소 정상).

**② 기능 무회귀 (클릭스루 · 콘솔 에러 0)**
- 배치 셀렉트 변경·상태 필터·오더 행 확장→오더아이템 조회·in-flight/sorter-command 커서 페이징(이전/다음)·소터 셀렉트 변경·3초 폴링 갱신 — **전부 F1과 동일 동작**. 브라우저 콘솔 에러/경고 **0**.

**③ 빌드·정적검사 GREEN**
- `cd frontend && npm run build`(=`tsc --noEmit && vite build`) **성공**. `npm run lint`(eslint) **0 에러**(F1 warn 정책 유지·신규 warn 0). 산출물 `backend/src/Wcs.Api/wwwroot/` 정상 생성.

**④ backend 0줄 · 161 GREEN 불변**
- `git diff --stat -- backend/` → **빈 출력**(backend 무변경 = 161 GREEN 자명 보존). 필요 시 확인적 `dotnet test backend/Wcs.sln` 1회 GREEN(회귀 아님·backend diff 0이 1차 증거).

**⑤ 로직 diff 0 (스타일 격리 입증)**
- `git diff -- frontend/src/lib/` → **빈 출력**(api/queries/format/utils/status 불변). `frontend/src/App.tsx`·`frontend/vite.config.ts`·`frontend/src/main.tsx` diff **0**. `git diff` 전체를 판독해 변경이 **className·@theme·index.html color-scheme·package.json 의존성**에만 있음을 확인(JSX 트리 구조·props·데이터 훅 무접촉; StatusRail Lamp tone 상수 1개 예외는 데이터 무관 시각 상수로 명시).

**⑥ 상태색 의미 보존 (육안 · 최중요 회귀)**
- ONLINE/COMPLETED=녹 · **OFFLINE/MISMATCH=적(#c13515, Rausch #ff385c와 톤 구분 육안 확인)** · FULL=황 · **PAUSED=회/황(OFFLINE 적과 구분)**. StatusRail 램프 3종(RDY/FULL/PAUSE)·배지 톤이 오프라인/일시정지/만재를 혼동 없이 구분. accent/busy(정보색)가 Rausch 아님 확인.

**Completion**: ①~⑥ 전부 PASS + **전후 스크린샷 비교 보존**(다크 before / 라이트 after, scratchpad 또는 sprint-feedback 첨부 경로 기록).

## 5. 함정 (Traps)

1. **Tailwind4 @theme 재매핑 함정 — 라디우스**: 기본 `rounded-lg`=8px라 카드 14px 미달. @theme radius 토큰 재정의 또는 `rounded-[14px]` arbitrary 필요. 배지는 `rounded-md`→`rounded-full` 명시 변경(값 재매핑으로 안 됨).
2. **shadcn CSS-var 충돌 = 이 프로젝트엔 N/A**: 표준 shadcn `:root{--primary…}` HSL 레이어가 **없음** — 테마는 전부 `@theme --color-*`. 새 shadcn 변수 레이어를 도입하지 말 것(불필요·혼선). 기존 토큰명 유지 + 값만 교체 + brand/paused 신규 토큰 추가가 정석.
3. **Rausch 오남용 = ⑥/절제 위반**: `accent` 토큰을 Rausch로 매핑하면 statusTone의 RESERVED/QUERIED/SENT(=accent)가 전부 Rausch로 물듦. 반드시 **brand(Rausch) 신규 토큰 분리** + accent/busy는 정보색(청/틸) 유지. `status.ts` 매핑은 **건드리지 말 것**.
4. **PAUSE 램프 적색 잔존**: StatusRail PAUSE가 현재 `tone="offline"`(적) — @theme만 바꾸면 OFFLINE과 동색 → ⑥ 실패. tone을 paused(회/황)로 **명시 변경**(Lamp 시각 상수 추가 허용, 데이터 무관).
5. **Inter 한글 두부(tofu)**: Inter엔 한글 글리프 0 — font stack에 **Malgun Gothic/Apple SD Gothic Neo** 폴백 미명시 시 한글 깨짐. 스크린샷에 한글(모니터링/작업 데이터/여유/만재…) 정상 렌더 확인.
6. **CDN 금지**: Google Fonts `<link>`·`@import url(https://…)` **금지**(사내망). `@fontsource-variable/inter` npm 번들만. 번들 사이즈 증가(F1-M4 391kB 기저에 폰트 추가)는 수용(로컬·오프라인 안전 우선) — 단 variable 서브셋(latin)로 과증 억제.
7. **다크모드 없음**: 단일 라이트(문서: Airbnb 퍼블릭 다크 없음). `color-scheme: dark`→`light`, `@media(prefers-color-scheme:dark)` 분기 도입 금지.
8. **백 배경 저대비**: 기존 상태색(에메랄드/앰버/로즈)은 다크 배경 최적화 값 — 백 배경에서 텍스트 대비 부족. online/warn을 어둡게 재조정(WCAG AA). offline은 문서 error #c13515(이미 백에 적합).
9. **로직 파일 우회 유혹**: 색 분기·매핑을 `status.ts`/`queries.ts`에서 고치고 싶어질 수 있음 — **금지**(⑤). 모든 색은 @theme 값 + className으로.
10. **vite outDir 산출물 오염**: `npm run build`가 `backend/src/Wcs.Api/wwwroot`를 재생성(gitignore) — backend/ **소스** diff와 혼동 말 것(④ `git diff --stat -- backend/`는 tracked 소스 대상, wwwroot는 ignored).

## 6. Planner Self-Check

- [x] **Scope IN** = index.css @theme 값 재매핑(2A, 최대 레버리지) + 의미 어긋 지점 className(2B, 파일별 현재→목표 표) + package.json 폰트 + index.html color-scheme. 실독 근거: index.css·11개 컴포넌트·3 섹션·pages·lib 전수·vite.config·package.json·index.html·api.ts 형상.
- [x] **Scope OUT** = backend 0줄 · lib/*.ts(api/queries/format/utils/status) 0 · 데이터흐름·API·라우팅·컴포넌트 구조·main.tsx 0. 무변경 가드(⑤ git diff) 명시.
- [x] **사용자 확정 5항 반영**: ①문서 토큰 그대로(2A 표) ②Rausch 단일 액센트 절제(2C·함정3) ③상태 의미색 별도 유지·OFFLINE 적≠Rausch(2A·⑥·함정4·8) ④Inter 로컬 번들·한글 폴백(2A·함정5·6) ⑤로직 0 변경(§3·⑤·함정9).
- [x] **검증 6기준** 각 fresh 명령/증거 + Web/UI 슬롯(기본·빈/로딩/에러·반응형 1128px) + 전후 스크린샷 보존. ①시각충실도 최중요, ⑥상태색 의미 회귀 최중요.
- [x] **함정 10개** (라디우스·shadcn N/A·Rausch오남용·PAUSE적색·한글두부·CDN금지·다크모드·백대비·로직우회·outDir오염).
- [x] **절대규칙 무관**: 프론트 스타일 전용 — PLC 쓰기/판정/타이밍 무접촉. backend 0줄로 #1~#8 전부 비해당.
- [x] **코드 구현 0** — WHAT/WHERE/VERIFY만. 정확한 hex 미세조정·유틸 vs @theme 선택은 Generator 재량(문서 토큰·의미·대비 제약 내).
- [x] **Detected Type** Full-stack(이 스프린트 Web/UI 전용) · **Scaling** 1/1/1 · baseline 161 GREEN(backend diff 0로 보존).
