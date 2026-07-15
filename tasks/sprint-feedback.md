# Sprint Feedback — S-UI-LAYOUT-FIX (낮은 뷰포트 폼-오버랩 + /ops 페이지 오버플로 근본수정)

**APPROVED** — Evaluator, 2026-07-15. C1–C11 전부 PASS. 3개 낮은 뷰포트(1366×720·1280×680·1300×700)에서 수치 실증 — 이전 스프린트 회귀(큰 뷰포트만 통과)를 반복하지 않도록 낮은 뷰포트 전수 검증. 정적 0·회귀 0(8페이지)·백엔드 diff 0·package 무변경·콘솔 error/warning/pageerror 0.

핸드오프 마커 = `tasks/sprint-log.md` 말단 `## IMPLEMENTATION COMPLETE — S-UI-LAYOUT-FIX`. HEAD=`a088520`(PR #66 병합 위 미커밋 작업트리, 브랜치 `feat/ui-layout-fix`). 변경 = **프론트 2파일만**(`frontend/src/pages/B2cDataGenPage.tsx`, `frontend/src/pages/OpsPage.tsx`) + tasks docs. 공용 프리미티브(Layout/card/index.css) 무접촉 → 회귀 범위 8페이지 정당(diff --stat로 확인).

## 실측 diff (page-local · 공용 프리미티브 0)
- B2cDataGenPage: 상단 그리드 `min-h-[220px]` 제거(자동 min-height:auto=폼 자연높이 하한) + 하단 배치상세 Card `min-h-[200px]`→`min-h-0`.
- OpsPage: 스크롤 본문 div 에 `[&>*]:shrink-0` 추가(스택 카드가 압축돼 2차 스크롤/잘림 지점이 되는 것 잠금 — 영역당 스크롤 1개 불변식).

## 검증 환경(격리 — 사용자/생성자 포트 무접촉)
- Sim3ds :1512 (TCP, `--transport tcp --port 1512`) · Wcs.Api :5215 (Release, `--urls http://localhost:5215` → 로그 `Now listening on: http://localhost:5215` 확인, 5205 무바인딩) · Vite :5190 (`VITE_API_TARGET=http://localhost:5215 --strictPort`).
- DB = fresh scratch SQLite `scratchpad/wcs_eval_scratch.db`(`Database:Provider=Sqlite`+`SeedOnStartup=true`), 사용자 실 DB/Azure/실 PLC 무접촉. 소터 destId=6 chuteNo=30 transport=Tcp→Sim :1512 **ONLINE**(로그 확인). 증거 = `screenshots/S-UI-LAYOUT-FIX_20260715-170748/`.

## Completion Conditions C1–C11 (raw 수치)

### /b2c/test-data (3 뷰포트)
- **[C1] page/main scroll 0 — PASS.** 1366×720: main 657≤clientH 657 · doc 720≤innerH 720. 1280×680: main 617≤617 · doc 680≤680. 1300×700: main 637≤637 · doc 700≤700.
- **[C2] 카드 오버랩 0 — PASS.** 전 뷰포트 form.bottom=566.25 ≤ detail.top=582.25 (+16 gap).
- **[C3] '+ 데이터 생성' 버튼 가시+클릭 — PASS.** (i) btn top 464.25 ≥0, bottom 504.25 ≤ innerH(720/680/700 전부), `elementFromPoint(center)`=BUTTON(가려짐 0). (ii) 실 `browser_click` → 검증 토스트 "작업일자·배치명·바코드 접두를 모두 입력하세요." 발생(MutationObserver 캡처), 예외 0·데이터 생성 0(early-return).
- **[C4] 빈 배치상세 50% 미점유 — PASS.** detail.height ≤ topRegion.height: 1366→117.75≤483.25 · 1280→77.75≤483.25 · 1300→97.75≤483.25. 상단=폼 자연높이(483.25) 하한 유지.
- **알터네이트(populated)**: SEED 배치 행 선택 → "배치 상세 — SEED #1" 오더 5행 로드, page/main 무스크롤 유지(617≤617·680≤680), 상세 본문 단일 내부 스크롤(sh189/ch32). (04)

### /ops (3 뷰포트, 라이브 소터)
- **[C5] page/main scroll 0 — PASS.** main.scrollTop=0 · main 657/617/637 ≤ clientH 동일 · doc 720/680/700 ≤ innerH 동일.
- **[C6] 단일 바운드 내부 스크롤 + 소터바 고정 — PASS.** 스크롤 영역=수정된 div(`overflow-auto [&>*]:shrink-0`) scrollHeight 845 > clientHeight 569/529/549(canScroll). 영역 bottom까지 스크롤(scrollTop 276/316/296=max) 후에도 소터 선택 바 rect.top 83→83 **불변**(shrink-0). main.scrollTop 0 유지.
- **[C7] 마지막 컨트롤('셀 지정') 도달 — PASS.** 영역 하단 스크롤 후 셀 지정 버튼 완전 가시(1366: top636.25/bot668.25≤720 · 1280: 596.25/628.25≤680 · 1300: 616.25/648.25≤700), `elementFromPoint`=BUTTON. 실 click → 검증 토스트 "셀 번호와 명령 순번을 입력하세요."(PLC 쓰기 0·early-return). 라이브 WordPanel D0~D6+D4 flags + SignalR "실시간 연결됨"(cross-layer 무단절, End-to-end 시나리오).

### 공통
- **[C8] 콘솔 클린(BLOCKING) — PASS.** 내 세션 전체 error 0 / warning 0 / pageerror 0. info 2건=React DevTools 다운로드 안내(dev 무해). 백엔드 기동으로 SignalR/API 정상 왕복 → spurious 네트워크 에러 0. (`console.log`) 주의: 리포 루트에 남아있던 `screenshots/S-B2C-GRID-UX_20260715-121800/console-all.log`(타 스프린트 :5191 에러)는 내 세션 아님 — 혼입 배제.
- **[C9] 회귀 8페이지(1280×680) — PASS.** doc/body 스크롤 0 = 전 8페이지(680≤680). main 바운드: /monitor·/sorters·/b2c/facility·/logs·/comparison·/boxes·/settings 617≤617. **/data-generator**: doc 680≤680(body 무스크롤)이나 main sh652>ch617(35px 내부 오버플로) — **DataGeneratorPage.tsx는 이번 스프린트 무접촉**(`git diff --stat` 공란)이므로 pre-existing 베이스라인, 회귀 아님. 신규 콘솔 오류/오버랩 0.
- **[C10] 정적 검사 0 — PASS.** `npm run typecheck`(tsc --noEmit) exit 0. `npm run lint`(eslint) exit 0. `npm run build`(tsc && vite build, 스크래치 outDir) exit 0 — "✓ built in 6.30s"(경고=선재 chunk>500kB + signalr PURE-annotation, 신규 error 0). wwwroot 무접촉.
- **[C11] backend diff 0 — PASS.** `git diff --stat -- backend` 공란 · `frontend/package.json`·`package-lock.json` diff 공란 · `backend/src/Wcs.Api/wwwroot` git status 공란(빌드→스크래치).

## 판정
**모든 C1–C11 = PASS. 이전 스프린트의 "큰 뷰포트만 통과" 실패를 3개 낮은 뷰포트 수치 게이트로 방지 확인.**

**APPROVED**

## 프런트 테스트 러너
vitest **not configured** (package.json에 test 스크립트 없음 — 계약 명시대로 기록).

---

# Sprint Feedback — S-UI-LAYOUT (뷰포트 맞춤 + 그리드 내부 스크롤 + 3DS 워드 중복 제거)

**APPROVED** — Evaluator, 2026-07-15. 순수 프론트 스프린트. 정적 0·회귀 0·백엔드 diff 0·마이그레이션 0, 전 페이지 브라우저 실증(뷰포트 맞춤·그리드 본문 스크롤·sticky thead·리사이즈), 레지스터 dedup·메뉴 개명 실증, 내 origin 콘솔 error/warning/pageerror 0.

핸드오프 마커 = `tasks/sprint-log.md` 말단(L3732) `## IMPLEMENTATION COMPLETE — S-UI-LAYOUT`. HEAD=`2248f33`(PR #65 병합 위 미커밋 작업트리, 브랜치 `feat/ui-layout`). 변경 = 프론트 15파일 + docs/FRONTEND.md(백엔드/마이그레이션 0). 검증 환경(격리): Sim3ds :1512(TCP) · Wcs.Api :5215(Release·fresh scratch SQLite `wcs-eval.db`·SeedOnStartup·Sorters[0].ChuteNo=30→Sim :1512 ONLINE) · Vite :5190(VITE_API_TARGET=:5215). 포트 5205/1502 미사용(초기 실수로 5205 바인딩 → 즉시 kill 후 `--urls`로 5215 재바인딩·5205 반환). 증거 `screenshots/S-UI-LAYOUT_20260715-145400/`.

## 정적/회귀/무접촉 게이트 (PASS)
- `tsc --noEmit` = 0 · `eslint .` = 0(warning 포함) · `vite build`(스크래치 outDir) = 0. `backend/src/Wcs.Api/wwwroot` 무접촉(git status backend 빈 출력·전 빌드 후 재확인).
- `dotnet test -c Release` = **360/360 GREEN**(직렬 실행 `xUnit.parallelizeTestCollections=false …maxParallelThreads=1`, 0 실패·0 스킵·20s·exit 0). 단일 실행에서 결정적 GREEN — 생성자가 보고한 병렬-부하 flake(IT3a_D4_RMW·B2_RealSimServerRtu) 재현 없음(직렬로 회피, lessons 준수).
- `git diff --stat backend/` 빈 출력 · 신규 마이그레이션 0 · PlcGateway/Core/Data/Sim3ds 소스 무변경. (순수 프론트 계약 준수.)

## 레지스터 dedup + 메뉴 개명 (핵심 불변식 · PASS)
- grep: `WordPanel` import 는 `OpsPage.tsx` 단 1곳(SortersPage 는 주석만) · `OpLogTail` import 는 `SortersPage.tsx` 단 1곳. 브라우저: `/sorters`에 WordPanel/D-레지스터 타일 **부재**, `/ops`에 `3DS 레지스터 워드` **정확히 1개**.
- NAV(b2c): `[데이터 생성, 설비 관리, 모니터링, 운영 로그(/sorters), 운영 제어(/ops)]` — `3DS 워드` 소멸·`운영 로그` 존재. `/sorters` 헤더 title=`운영 로그`·subtitle=`operation_log 실시간 테일 · category/level 필터`. 오펀 소터 Select 제거(필터 select 는 category/level 2개뿐). OpLogTail(앱 유일 op-log 뷰) 생존·동작.

## 브라우저 뷰포트 맞춤 실증(각 페이지 docSH==innerH·pageScroll 0·본문만 스크롤)
- **B2cDataGen(마스터-디테일)**: EVAL-BIG 300오더 → 페이지 fit(900=900), 단일 내부 스크롤=디테일 본문(300행·sh 9334/ch 346). (01)
- **B2cFacility(3단+2패널·최난)**: 페이지 fit, 3 내부 스크롤 영역(목적지 sh377/ch272·좌 배정대상 sh257/ch169·우 미할당 316행 sh10146/ch180) 분산. (02)
- **Monitor 작업**: 100행 fit, 그리드 본문만 스크롤(sh4133/ch689) + **sticky thead**: 400px 스크롤 후 thead top=컨테이너 top(191) 유지·position:sticky(table.tsx overflow-x 제거 효과 실증). (03) / **분류**: 셀·적재 2그리드 flex-1 균등(각 342px·min-h 하한). 
- **/sorters=운영 로그**: WordPanel 부재·OpLogTail 존재. 단축 뷰포트(1280×460)서 op-log 본문 내부 스크롤(sh297/ch258)·페이지 fit(460=460). (04)
- **/ops=운영 제어**: WordPanel 1개+라이브 SignalR(D0~D6·D5 CurFloor=1·D4 Ready=1·`실시간 연결됨`·소터 chuteNo=30 ONLINE) — 브라우저↔API↔SignalR↔Sim3ds 전계층 왕복(S14). 1280×560 리사이즈서 라이브 데이터·연결 유지+페이지 fit+크롬 고정. (05)
- **b2b DataGenerator**: EVAL-B2B 300 디테일 fit·내부 스크롤(sh11015/ch738). (06) **Comparison(wide 10열)**: 313행 내부 스크롤(sh11520/ch689)·페이지 **양축** fit(docSW 1440=innerW·가로 페이지 스크롤 0). (07) **Logs/Boxes**: empty-state fit·레거시 고정 max-height 아티팩트 0(calc→flex 확인). **Settings(대상 제외)**: 정상 렌더·회귀 0.
- 데이터 양극단: 소량(seed·empty)서 조기 스크롤/빈 여백 없음, 넘침(300/313/316)서 페이지 뷰포트 불초과·그리드 본문만 스크롤. 리사이즈(1440×900↔1280×{460,560})서 무붕괴.

## 콘솔(BLOCKING · PASS)
- 전 세션 error 0 / warning 0 / pageerror 0. info 21건은 전부 React DevTools 다운로드 안내(dev 무해)·일부는 :5191 foreign-origin(불산입, lessons foreign-buffer).

## Minor(비차단 · 백로그 유지)
- 이월 grid-a11y 2건(ContextMenu Tab·그리드 컨테이너 tabIndex/role) — 계약 OQ-4 기본 defer대로 미접촉. `tasks/todo.md` 백로그 유지 권고.

---

# Sprint Feedback — S-B2C-GRID-UX (랜딩 정합 + 공용 그리드 상호작용)

**APPROVED (FIX ITER 2 재검증 · 유지)** — Evaluator, 2026-07-15. Step 4.5 코드리뷰 3건(Important 1 + fold 2) 완전 해소·회귀 0·브라우저 실증·콘솔 0. 초회 APPROVED 아래 보존.

---

## FIX ITER 2 재검증 (Step 4.5 코드리뷰 3건 · PASS) — 2026-07-15

핸드오프 마커 = `tasks/sprint-log.md` L3705 말단 `## IMPLEMENTATION COMPLETE — FIX ITER 2 (S-B2C-GRID-UX · Step 4.5 코드리뷰 3건)`. HEAD=`9c6f63a`(불변·미커밋). 변경 파일 = `useRowSelection.ts`(FIX1·2·3) + `B2cFacilityPage.tsx`(FIX2 소터행 결선). 검증 환경(격리): Sim3ds :1512 · Wcs.Api :5215(Release·fresh scratch SQLite `wcs-eval2.db`·seed·Sorters[0].ChuteNo=30) · Vite :5190. 증거 `screenshots/S-B2C-GRID-UX_20260715-121800/07-fixiter2-g3-drag-highlight-menu.png` + console-fixiter2.log.

### 정적/회귀/무접촉 (PASS)
- tsc 0 · eslint 0 · vite build 0(스크래치 outDir·wwwroot 무접촉). `git diff backend/` 빈 출력 · 신규 마이그레이션 0.
- `dotnet test -c Release`: **run1 = 실패 2/통과 358**(`S5RSeqMismatchTests.S5_RSeqMismatch_AlarmAndSorterCommandMismatch` 외 1) → **clean 재run = 360/360 GREEN(실패 0)**. 귀속: (1) 백엔드 byte-identical(diff 0)이라 프론트 변경이 백엔드 Modbus 시나리오 테스트를 논리적으로 깨뜨릴 수 없음, (2) S5 는 타이밍-취약 핸드셰이크 시나리오(lessons: S9 flake·testhost-teardown·e2e-parallel-load), run1 시 잔여 testhost 프로세스 2개 존재. → **비결정 flake, 회귀 아님**.

### FIX 1 (Important) — 전역 stuck user-select 방지 (브라우저 실증 · PASS)
- 코드: `beginDragVisual`/`endDragVisual` 를 `dragVisualRef` 로 멱등화 + `onMove` 에 `e.buttons===0 → onUp()` 조기 종료.
- 실증(합성 PointerEvent, G3): (a) 정상 드래그 중 `body.userSelect='none'` → **창 밖 릴리스 누락(pointerup 미발화) 후 buttons=0 재진입 이동 → '' 복원**(before '' / during 'none' / after ''). (b) 회귀 근원 경로: stuck('none') 상태에서 **복구 없이 새 드래그 → clean pointerup → '' 복원**(멱등 begin 이 오염된 'none' 을 prevUserSelect 로 재저장하지 않음 — verdict "RESTORED, no corruption"). "리로드까지 영구 잠김" 제거 확정.

### FIX 2 (fold) — 소터 펼침 행 드래그-후 오발화 방지 (브라우저 실증 · PASS)
- 코드: 선택-`draggedRef` 재사용(stale 회귀) 폐기 → 자기 이동거리 기반 `expandableRowProps`(pointerdown 좌표 기록 → click 이동 ≥4px 면 억제) 신설, `B2cFacilityPage` 소터 헤더행에 결선.
- 실증(assign-panel 소터 헤더행 정확 타겟팅): (A) **타행(chute) 드래그 직후 소터행 정당 클릭 → 펼침(3셀)** — 생성자가 자가-발견한 stale-flag 오억제 회귀 수정 확인. (B) **소터행에서 드래그 → 토글 안 됨(3셀 유지)**. (C) 드래그 직후 소터행 정당 클릭 → 접힘(0셀). 정당 클릭은 항상 토글·실제 드래그만 억제.

### FIX 3 (fold) — "전체 해제" 완결성 (브라우저 실증 · PASS)
- 코드: `uncheckAll` = `setChecked(new Set())`(소유 Set 전량 클리어). `checkAll` 은 렌더+자격 유지(불변).
- 실증(G2): ①전체 선택(8=활성슈트5+enabled셀3, chute:5 제외) → **소터 접기(셀 DOM 0·`선택 대상` 카운트 8 유지=접힌 셀 Set 잔존 재현)** → **전체 해제 → 선택 대상 0** → 재펼침 시 3셀 전부 uncheck. 접힌 소터 셀까지 완전 클리어 확정.

### 회귀 (핵심 UX 무손상 · PASS)
- 드래그 범위 하이라이트(G3 3행 7·8·9 연속) → 우클릭 ③ 선택된 행 체크 = **하이라이트와 정확 일치**(match). 메뉴 4항목·자격 존중(전체선택(9)·chute:5 제외)·Escape 닫힘 정상.
- 콘솔: 내 origin(:5190) error/warning **0**(현재 네비 0/0, 전세션 덤프 180 error/warning 전부 :5191 foreign-origin — 불산입).

### 미접촉 (지시대로)
- 나머지 2 Minor(ContextMenu Tab 처리·컨테이너 tabIndex/role)는 조정자 백로그 지시대로 미접촉 — 확인. 비차단.

---

## 초회 APPROVED (S-B2C-GRID-UX 전체) — Evaluator, 2026-07-15. 순수 프론트 스프린트. 정적/회귀 0, 백엔드 diff 0·마이그레이션 0, 브라우저 클릭스루로 R1~R4 및 V1~V9 전 시나리오 실증, 내 origin 콘솔 0.

핸드오프 마커 = `tasks/sprint-log.md` 말단 `## IMPLEMENTATION COMPLETE — S-B2C-GRID-UX`. HEAD=`9c6f63a`(PR #64 병합 위 미커밋 작업트리). 브랜치 `feat/b2c-grid-ux`.
검증 환경(격리): Sim3ds :1512(TCP) · Wcs.Api :5215(Release·fresh scratch SQLite `wcs-eval.db`·SeedOnStartup·Sorters[0].ChuteNo=30 소터=Sim :1512) · Vite :5190(VITE_API_TARGET=:5215). 포트: 5205/1502 미사용. 증거 `screenshots/S-B2C-GRID-UX_20260715-121800/`.

---

## 정적 / 회귀 / 무접촉 게이트 (fresh·격리 · PASS)

- **tsc --noEmit = 0** (frontend, fresh).
- **eslint . = 0** (warning 포함 0).
- **vite build = 0** — 스크래치 outDir(`scratchpad/vite-out`)로 산출, `backend/src/Wcs.Api/wwwroot` **무접촉**(`git status wwwroot` 빈 출력 재확인). 1851 modules, exit 0.
- **dotnet test -c Release = 360/360 GREEN** (실패 0·건너뜀 0·21s). 경고=선재 NU1903(SQLitePCLRaw) 뿐 — 회귀 0.
- **무접촉 경계**: `git diff --stat backend/` 빈 출력 · `git status --short backend/` 빈 출력 → 백엔드/서비스/스키마 diff 0, **신규 마이그레이션 0**(순수 프론트 계약 준수). Wcs.PlcGateway/Wcs.Core 무접촉.

## 재사용 / 아키텍처 (코드 판독 · PASS · 사용자 R3 핵심)

- 드래그/메뉴 로직이 **단일 훅 `frontend/src/lib/useRowSelection.ts` + 단일 프리미티브 `frontend/src/components/ui/context-menu.tsx`** 로 통일. 5개 그리드 전부 소비만(중복 로직 0).
- 각 그리드가 **자기 체크 Set 계속 소유**(브리지 패턴): G1 `setChecked`(number) · G2 `setCheckedTargets`(string) · G3 `setCheckedOrders`(number) · b2b `setSummaryChecked`(string)·`setDetailChecked`(number). 체크 모델 재작성 0(최소 침습).
- 기존 S-B2B-2c 페인트-선택(드래그/Shift/Ctrl·`onSelectExact`/`selectDetailExact`) 통합 모델로 **완전 제거**(잔존 참조 0 — grep 확인, tsc 0).
- DOM 기반(`data-rsid`/`data-rseligible`) 설계 → 지연 로딩 셀(G2 소터) 자동 포함·OQ-1 로드행 한정 구조 충족. 이벤트 정리: window 리스너 useEffect cleanup + MutationObserver 콜백 ref 대칭 해제(누수 0). react-refresh 규칙 청정(훅 파일=훅만, 컴포넌트 파일=컴포넌트만).

## 브라우저 클릭스루 — 전 시나리오 (fresh evidence · PASS)

- **V1 랜딩**: `/`→(b2c)`/b2c/test-data`(데이터 생성·G1 3배치 렌더) · B2B 탭→`/data-generator` · b2b 모드 `/`→`/data-generator`(ModeHome). `homePathFor` 단일 소스 3경로 정합. `[01-v1-b2c-landing-datagen.png]`
- **V4 컨텍스트 메뉴 4항목 — 5개 그리드 전부**: aria-label 별 확인 — b2b `배치 요약 메뉴`·`상세 바코드 메뉴` / G1 `생성 결과 배치 메뉴` / G2 `배정 대상 메뉴` / G3 `미할당 오더 메뉴`. 첫 활성항목 자동 포커스([active]), 하이라이트 0이면 ③④ disabled. `[02·06]`
- **V3 드래그 범위 하이라이트**: 실 PointerEvent(down→move>임계→move) — b2b Detail 4행(idx1..4=id 2,1,8,6)·G1 2행·G3 3행 연속 하이라이트. **체크와 시각/상태 분리**(하이라이트 시 checked=[]). 드래그 중 `document.body.style.userSelect='none'`, pointerup 후 `''` 복원(OQ-2). `[03-b2b-detail-drag-highlight-4rows.png]`
- **V5 메뉴 액션 + 자격**: ③선택행 체크(b2b Detail·G3) = 하이라이트 집합과 **정확히 일치**. ①전체 선택(G1·b2b Summary) = 전 행 체크 + 헤더 체크박스 on. **G2 자격 존중**: 비활성 chute:5(deactivate로 준비, `data-rseligible=0`·개별 체크박스 disabled)는 `전체 선택 (5)`에서 **제외**(chute 1,2,3,4,7만 체크, chute:5 미체크). `[04-g2-selectall-skips-inactive-chute.png]`
- **V8 공존**: G1 행 클릭→디테일 로드(EVAL-A 바코드) 정상 · **드래그 후 click 억제**(드래그로 하이라이트해도 디테일 미로드) · G2 소터 행 클릭→셀 3개 펼침(cell:6:1~3, 지연 렌더가 선택에 자동 참여) · 기존 액션 버튼 카운트 반영(G1 `초기화 (3)`·b2b `수신 초기화 (2)`·G3 `배정 (3)`).
- **V9 교차레이어**: G3 드래그+③로 오더 3건 체크 → 배정 → `/api/b2c/facility/orders/assign` 왕복 → **미할당 19→16·배정 4→7**(API 재확인). refetch 후 사라진 오더의 하이라이트·체크 **prune**(OQ-4). (참고: 배정은 기존 `작업자 이름` 필수 게이트 — 공백 시 정상 차단, 회귀 아님.) `[05-v9-facility-after-assign.png]`
- **V6 빈 그리드**: 배치 0(bizDay 2020-01-01) b2b Summary 우클릭 → 메뉴 4항목(③④ disabled)·`전체 선택` no-op(행 0·크래시 0).
- **OQ-3**: 그리드 본문 밖 우클릭 = `contextmenu` defaultPrevented=false·커스텀 메뉴 미개방(네이티브 유지). 본문 안은 메뉴 개방(대체). → 본문 한정 확정.
- **OQ-4**: 필터 변경(b2b Detail 바코드 필터 입력) → 하이라이트 전체 리셋([]), 체크는 유지. refetch(V9) → 사라진 id prune. → id-키 정책 확정.
- **a11y**: 메뉴 role=menu/menuitem, 첫 항목 포커스, disabled 항목 포커스/실행 제외, **Escape로 닫힘**(실증). R4 충족.
- **V7 다크모드 = N/A**(단일 라이트 테마) — 라이트에서 하이라이트(teal 좌측 바)/체크(파랑) 대비 양호(스크린샷 확인).

## 콘솔 (BLOCKING · PASS)

- **내 origin(:5190) = error/warning 0**. 현재 네비게이션 쿼리 0/0, 전 세션 덤프(`console-all.log`) 168 error/warning **전부 `:5191`(생성자 포트) 참조 = foreign-origin 잔류**(영속 브라우저 프로필). 계약상 타 포트 잔류는 불산입.
- 생성자가 보고한 hook-order `TypeError`(`updateCallback`→`useRowSelection.ts:262`)는 스택이 `localhost:5191`(생성자 HMR live-edit) — **fresh load 아님**. 나는 동일 컴포넌트(B2cDataGenPage/useRowSelection)를 :5190 fresh 로드로 드래그·클릭·메뉴·전체선택까지 집중 조작했고 error 0. HMR Fast-Refresh 아티팩트로 귀속(운영/신규마운트 무영향).

---

## Minor (비차단 — todo 등록 권고, 후속)

- (없음 — 계약 요구/게이트 전부 충족. 후속 강화 아이디어: G2/G3 useRowSelection 에 resetKey 미전달 — 현재 route unmount + refetch prune 으로 충분하나, 스코프 필터가 추가되면 명시 resetKey 검토.)

## Code Review Pass (Step 4.5 — 독립 리뷰 + fix iter 2, 2026-07-15)

**최종: Ready to merge = Yes** (초판 "With fixes" → Important 1 + Minor 2 fix 후 Evaluator 재검증 APPROVED 유지).

강점(리뷰): 단일 useRowSelection 훅 + 단일 ContextMenu 프리미티브가 5그리드에 중복 0으로 결선(DOM-driven
data-rsid/rseligible로 lazy 소터 셀도 자동 포섭), 리스너/MutationObserver 누수 0·attr 미관찰로 피드백루프
차단·1000행 O(n) prune, 클릭↔드래그 억제·좌우버튼 분리 견고, ContextMenu portal/clamp/dismiss/키보드 완비,
랜딩 단일소스·리다이렉트 루프 없음, b2b paint-select 잔재 0.

- **[해소] Important — 창밖 드래그 해제 시 userSelect 전역 고착**: onMove e.buttons===0 미스드릴리스 감지 +
  beginDragVisual 멱등(dragVisualRef) — 손상값 재저장 방지. OQ-2 자기 관심사.
- **[해소] Minor — 소터 부모행 드래그 종료 오펼침**: movement-based 가드(expandableRowProps). 첫 시도의
  stale-flag 회귀(진짜 클릭 오억제)를 브라우저 테스트로 잡아 이동거리 기반으로 재설계.
- **[해소] Minor — "전체 해제" 접힌 셀 잔존**: uncheckAll이 Set 전체 클리어(checkAll은 렌더+eligible 유지).

### Minor (비블로킹 — 다음 sprint / todo 등재)
1. ContextMenu Tab 미처리(Escape/Arrow/Home/End만) — Tab 시 메뉴 뒤로 포커스 이동. 표준 메뉴 close/trap 폴리시.
2. 그리드 컨테이너 tabIndex/role 부재 — 키보드 메뉴열기(Shift+F10)가 자식 체크박스 포커스 시에만 동작. R4 최소요건은 충족.

## Code Review Pass (Step 4.5 — 독립 리뷰, 2026-07-15)

**최종: Ready to merge = Yes. Critical 0 · Important 0 · Minor 5.**

강점(리뷰·전 페이지 concrete 검증): table.tsx overflow-x 제거가 sticky thead 재부모화하면서도 넓은 표
(3-way 비교 등) 가로 스크롤 유지(CSS 커플링으로 단일 컨테이너가 양축 처리) / flex min-h-0 체인 전
스크롤 조상에 완비(0-collapse 없음·narrow 폴백 도달가능) / WordPanel dedup 무-dangling(useHubLifecycle가
연결 소유) / useRowSelection이 elementFromPoint+capture scroll이라 새 컨테이너서 스크롤 무관 정확 / 메뉴
개명 완결 / 백엔드 diff 0.

- **[정리완료] 옛 라벨 잔재 3건**(App.tsx:29 주석·B2B-DATAGEN.md:244·FRONTEND.md:12) — orchestrator가 커밋 전 텍스트만 수정(로직 0).

### Minor (비블로킹 — 다음 sprint / todo 등재)
1. SortersPage 상단 span "실시간 operation_log 이벤트 스트림"이 OpLogTail 카드 제목과 중복 — 정리 후보.
2. min-height floor 매직값(168/200/260/160/220px 등) 페이지별 산발 — 공유 토큰 2~3개로 정리 후보.
3. Monitor 섹션 CardContent에 min-w-0 누락(b2b는 있음) — 무해하나 일관성.
4. (이월) ContextMenu Tab 처리 / 그리드 컨테이너 tabIndex·role.

## Code Review Pass (Step 4.5 — 독립 리뷰, 2026-07-15)

**최종: Ready to merge = Yes. Critical 0 · Important 1(I1, 교정완료) · Minor 3.**

강점: 스코프 airtight(코드=2 페이지, 공용 프리미티브·backend 0), 매직 px 제거(추가 아님), fix가 스크린샷으로
독립 확인됨, master `min-h-0`가 행을 부풀리지 않는 추론 타당, `[&>*]:shrink-0`는 세로축만이라 가로 클리핑 없음.

- **[교정완료] I1** — B2cDataGenPage 상단 grid 주석의 사실 오류(`grid-rows-1`=minmax(auto,1fr) 주장). 실제
  Tailwind v4 `grid-rows-1`=minmax(0,1fr)이고 높이 하한은 상단 div `min-height:auto`(no min-h-0) × master
  `min-h-0` × 폼 `self-start`에서 옴. fix-only 사이클로 주석만 교정(런타임 무변경, 정적 0 재확인).
- **[교정완료] M2** — detail `min-h-0`의 ~620px 페이지-스크롤 전환 임계를 주석에 명시(같은 클러스터에 흡수).

### Minor (비블로킹 — 다음 sprint / 백로그)
- **M3**: OpsPage `[&>*]:shrink-0` 임의 variant는 주석 충실하나, 자식이 두 Card로 고정이므로 각 Card에 명시
  `shrink-0`가 더 가독적(선택).
- **M4**: 비-xl(단일 컬럼) 경로는 회귀 없음이나 주석이 xl 중심 — degradation 경로 무주석(정보성).
- **[기존 베이스라인·비회귀] /data-generator 1280×680에서 main 내부 35px 오버플로** — DataGeneratorPage.tsx는
  이 스프린트가 미접촉(diff 0). 도입 아님. 별도 백로그 항목으로 후속 처리.
