# Sprint Feedback — S-B2B-2b (프론트: B2C/B2B UI 토글 + /data-generator 페이지 + 아카이브 필터 UI)

**APPROVED** — Evaluator, 2026-07-08 (1 iteration to pass).

브랜치 `feat/b2b-2b-datagen-frontend`(읽기 전용 — 커밋/수정/브랜치전환/fix 없음).
핸드오프 마커: `tasks/sprint-log.md:2628` `## IMPLEMENTATION COMPLETE (B2B-2b)`.
근거: `docs/B2B-DATAGEN.md`(§4·§5·§7)·`tasks/sprint-contract.md`(S-B2B-2b + 사용자 확정 Q-A[인쇄·고급선택 2c 연기]).
프로젝트 타입: **Full-stack**(변경 표면은 프론트 전용, 백엔드 소비만). 원본 `BowooTestBatchSystem_v2`·`TEST_ORDER_DB` 대조는 범위 밖(pwd 경계).

검증 환경: 격리 스크래치 SQLite(`scratchpad/wcs-eval.db`, env override `Database__Provider=Sqlite`) + `Sorters[0]` chuteNo=30 **Tcp**(no-Sim → OFFLINE)로 기동. **현장 SqlServer·COM1 실 PLC 무접촉·IF-09/10 트리거 0**. vite dev :5173 proxy→:5080. (`.claude/ports.local.json` 부재 → 아래 Minor 참조.)

---

## 정적 게이트 (fresh, 독립 실행) — PASS

    $ npx tsc --noEmit        → tsc_exit=0
    $ npx eslint .            → eslint_exit=0
    $ dotnet test backend/Wcs.sln → 통과! 실패:0 통과:271 건너뜀:0 (15s)

- tsc 0 · eslint 0(경고 0). 백엔드 회귀 스위트 **271/271 GREEN**(NU1903 SQLite transitive 취약성 경고 8건은 선재 — 이 스프린트 무관, 코드 경고 아님).

## 무접촉·신규의존 0 게이트 — PASS

- `git diff HEAD` 결과: **백엔드 0줄 · frontend/package.json 0줄 · package-lock.json 0줄**. 신규 npm 의존 0(jsbarcode 미설치·`@radix-ui/react-dialog` 미추가 — dialog 는 radix 미의존 자작 주장 검증됨. 기존 `@radix-ui/react-tabs`만 존재).
- 기존 B2C lib/pages 무접촉 확인(diff 0): `lib/api.ts`·`MonitorPage.tsx`·`SortersPage.tsx`·`lib/signalr.ts`·`lib/useMonitorHub.ts`.
- 변경 = frontend/src만: `App.tsx`(+20)·`Layout.tsx`(+153)·`main.tsx`(+12) additive 수정 + 신규 10파일(uiMode·testData·toast·UiModeProvider·ToastProvider·ui/dialog·DataGeneratorPage·sections/{GenerateForm,SummaryGrid,DetailGrid}). 브랜치 커밋은 docs 2파일뿐(선커밋).
- main.tsx Provider 계층 `QueryClientProvider > UiModeProvider > ToastProvider > BrowserRouter > App` — QueryClient 옵션 무변경(additive). App.tsx 기존 /monitor·/sorters 라우트 보존 + `/data-generator` 추가 + `*`/`/`→`<ModeHome>`. Layout `useHubLifecycle()`·PollIndicator 상시 유지(B2C SignalR lifecycle 무접촉).

## 스코프 준수(Q-A 2c 연기) — PASS

- grep(frontend/src): `JsBarcode`·`window.open`·`contextmenu`/`onContextMenu`·`onMouseEnter`/`onMouseDown`·`shiftKey`/`ctrlKey`/`metaKey`·`drag*`·`print(` **전부 0건**. A4 인쇄·로컬 JsBarcode 벤더링·드래그/Shift/Ctrl 고급선택·우클릭 컨텍스트 메뉴 **미구현 확인** = 2c 이연 준수(스코프 위반 0). 체크박스 다중선택만 구현(계약대로).

---

## 검증 시나리오 결과 (Full-stack — fresh browser evidence · Playwright)

### (a) B2C/B2B 토글 + localStorage 유지 — PASS
- `/`→`/monitor`(B2C 기본 랜딩·`ModeHome` 리다이렉트). 사이드바 B2C 세트(모니터링·3DS 워드·운영제어 **F3** disabled 배지)+타이틀 "실시간 모니터링"+StatusRail. 스샷 `01-b2c-monitor.png`.
- "B2B 생성" 토글 → `/data-generator` 이동. B2B 세트(데이터 생성 + 로그/비교/박스/설정 **B2B-3** disabled 배지)+타이틀 "데이터 생성"+헤더 bizDay(native `<input type=date>`)·autoRefresh 토글·간격 Select. 스샷 `02-b2b-datagen-landing.png`.
- **localStorage 유지**: 전체 페이지 재로드(`page.goto /`) 후 `/data-generator` 로 재랜딩(mode=b2b 복원). B2C 재토글 → `/monitor` 복귀. 스샷 `10-b2c-restored.png`.

### (b) 데이터 생성 라운드로빈 (E2E-1) — PASS
- 폼(배치 007·슈트 "1-4, 7"·개수 12) 제출 → 요약에 `2026-07-08 · 007 · 12` 노출 + 폼 리셋. 스샷 `04-generate-success-toast.png`.
- 행 클릭 → 상세 12행. 슈트 분포 실측(DOM): 001×3·002×3·003×2·004×2·007×2 = 12 → 라운드로빈 [1,2,3,4,7] 정확 배분. 스샷 `03-detail-populated.png`.
- **성공 토스트**: 유효 생성 시 success-tone 토스트(border-online) 렌더·message="Success"(백엔드 body.message 충실 표면화). in-page 결정적 캡처 `{appeared:true, text:"Success", isSuccessTone:true}`.

### (c) ★ 아카이브 가시화 (E2E-2, 사수 요구) — PASS
- 픽스처: 007 test_data id 1·2 에 INPUT+SORT 로그(status OK·archived_at NULL) 4건 직접 시딩.
- active 필터: 해당 2행 투입/분류=**OK**(07-08 14:21:07) 표시(나머지 dash). 스샷 `05-archive-active-with-status.png`.
- 요약 007 체크 → 수신 초기화 → **danger 확인 다이얼로그**(scrim·AlertTriangle·취소/초기화). 스샷 `06-confirm-dialog.png`.
- 초기화 실행(200) → active 뷰 2행 **dash 로 사라짐**(archived 로그 제외) → `보관만`(archivedOnly) 전환 → **동일 2행 OK 재노출**(active 미노출). 스샷 `07-archivedonly-shows-reset.png`.
- **DB 진실 대조**: test_log 007 = 4행 전부 `archived_at != NULL`(활성 0) = **하드삭제 0·소프트삭제 보존**. test_data 007 = 10행(삭제 2행은 하드삭제·등록원장). "삭제해도 보여줘" 가시화 end-to-end(frontend→API→DB→frontend) 입증.

### (d) 엑셀 업로드 + 400 검증실패 error 토스트 — PASS
- 3행 xlsx(신양식 5컬럼) 업로드 → 요약 `UPL · 3` 노출(DB UPL 3행). 스샷 `08-upload-success.png`.
- **성공판정 `res.ok && body.status==="S"` 검증**: chuteNos="abc" 생성 제출 → HTTP **400**(network #69) → **error 토스트** message="ChuteNos may only contain digits, commas, hyphens, and spaces."(body.message 충실 표면화·200 F/400 을 실패로 표면화). in-page 결정적 캡처. 폼 미리셋(실패 시 값 유지). 스샷 `09-error-toast-400.png`·`09b`.

### 회귀 0 (B2C) — PASS
- `/monitor` 렌더·오더 테이블·StatusRail·PollIndicator 정상. `/sorters` 워드패널(D0~D6·D4 비트분해)·operation_log 라이브 테일·**"실시간 연결됨"(SignalR 허브 접속)** 정상 = useHubLifecycle 무접촉 실증. 스샷 `11-b2c-sorters-signalr.png`.

### 콘솔 게이트 (BLOCKING) — PASS
- pageerror 0 · React dev-mode warning 0 · app console.error 0. 세션 유일 error 4건 = **의도적 400** network 로그(`/api/test-data/generate`, 앱이 error 토스트로 표면화 — §7.1 sanction, JS 에러 아님). warning 0. info = React DevTools 배너뿐.

---

## Minor (비차단 — 다음 스프린트 Generator 참고, APPROVED 무관)

1. **성공 토스트 message = 백엔드 "Success"(영문)**: `B2BApiResponse.Ok()` 기본 message 를 프론트가 그대로 표시(§7.1 "body.message 표면화" 충실). 한글 UX 일관성 관점에선 아쉬우나 백엔드 무접촉 스코프 밖(백엔드 message 개선은 후속). 프론트 폴백 `'완료되었습니다.'` 은 body.message 부재 시에만 발동.
2. **`.claude/ports.local.json` 부재**: 포트 소스오브트루스 파일이 없음. 다만 orchestrator 가 스프린트 태스크에 5080/5173/1502 를 명시했고 vite.config·appsettings Urls 가 동 포트를 고정하며, 서버 2개를 Evaluator 가 직접 격리 기동(sibling 프로젝트 서버 접속 아님)이라 false-PASS 위험 0. 포트 정책상 orchestrator 가 파일을 생성하는 것이 정석.
3. **`.playwright-mcp/` 미ignore**: Playwright MCP 산출 디렉터리가 프로젝트 루트에 untracked 로 생성됨(스냅샷/콘솔 로그). `screenshots/` 는 gitignore 되나 `.playwright-mcp/` 는 아님 → orchestrator 가 .gitignore 에 추가 권장(Evaluator 는 read-only 라 미수정).
4. ToastProvider 의 `timers` Map 은 언마운트 시 일괄 clear 하지 않으나, 루트 상시 마운트라 실효 누수 0(정보성).

## 스크린샷 (screenshots/S-B2B-2b_20260708-141900/ — gitignored)
01-b2c-monitor · 02-b2b-datagen-landing · 03-detail-populated · 04-generate-success-toast ·
05-archive-active-with-status · 06-confirm-dialog · 07-archivedonly-shows-reset ·
08-upload-success · 09-error-toast-400 · 09b-error-toast-visible · 10-b2c-restored · 11-b2c-sorters-signalr.

---

## 종합 판정

**Overall PASS — APPROVED.** 계약의 6개 완료조건(토글+localStorage·회귀 0·생성/관리 플로우·아카이브 필터·인쇄 게이트[2c 이연이라 N/A]·정적게이트)이 fresh evidence 로 전항 충족. Integration(res.ok && status==="S" 성공판정·200 F/400 실패 표면화·archived 파라미터·camelCase 정합)·프론트 Per-layer(Airbnb 토큰 일관·의도적 밀집 운영툴 정서·AI slop 아님)·Craft(tsc/eslint 0·콘솔 0·localStorage 화이트리스트 가드·컬럼필터 stopPropagation·AbortSignal·refetch 무효화)·Functionality(토글→생성→업로드→조회→reset→delete→아카이브 가시 전 경로) 모두 통과. 무접촉(백엔드·package·B2C lib/pages diff 0)·신규의존 0·스코프(인쇄·고급선택 미구현) 준수. Minor 4건은 비차단.

## Code Review (4-Tier Step 4.5 — S-B2B-2b 프론트)
- **[Important·fix] #1 Dialog 포커스 트랩 부재** — 파괴적 확인 모달인데 포커스 이동/트랩/복원 없어 배경 트리거 재발동 위험. 계약 명시분 → fix(포커스 트랩+복원).
- **[fix 동봉] #2 onCancel useCallback 안정화 / #4 error 토스트 role=alert / #6 barcodeCount 클라 상한.**
- **[Minor·todo] #3 ModeToggle tab 시맨틱 오용**(tabpanel 없음 → aria-pressed/nav+aria-current 권장) / **#5 includes·FilterCell 중복**(공용 추출) / **#7 bizDay 정규식 형식만(달력검증 아님 — native date+백엔드 400이 백스톱)** / **#8 body 스크롤잠금 대상이 main 실스크롤과 어긋남**(경미).
- 보안(XSS) 0·크래시 0 — dangerouslySetInnerHTML 전무, 사용자입력 JSX 이스케이프, 엑셀 FormData 서버전송만.

---

## FIX ITER 2 재검증 (code-review 갭 #1·#2·#4·#6) — PASS · APPROVED 유지

Evaluator delta 재검증, 2026-07-08 (read-only). 프론트 4파일만 변경(dialog.tsx·DataGeneratorPage.tsx·ToastProvider.tsx·GenerateForm.tsx). 핸드오프: `sprint-log.md` `## FIX ITER 2 (Dialog 포커스 트랩 + 접근성/UX 저비용)`. 최상단 S-B2B-2b APPROVED 보존.

### 정적·무접촉·회귀 (fresh)
- `npx tsc --noEmit` 0 · `npx eslint .` 0. 백엔드 `dotnet test` **271/271 GREEN 불변**(14s).
- `git diff HEAD` — 백엔드·frontend/package.json·package-lock.json **0줄**(신규 npm 의존 0). 기존 B2C(/monitor·/sorters·lib/api) diff 0. 프론트 = App/Layout/main 수정 + 10신규 additive(fix-iter2 는 그중 4파일만 재수정).

### #1 포커스 트랩(브라우저·핵심) — PASS
- (a) danger 다이얼로그(수신 초기화) 오픈 시 초기 포커스 = **"취소"(첫 포커서블·파괴적 액션 안전 기본), inside dialog=true**.
- (b) **Tab → "초기화"(마지막) → Tab wrap "취소"(첫) → Shift+Tab wrap "초기화"(마지막)** — 실 키 입력(Playwright keyboard), 매 스텝 `dialog.contains(activeElement)=true`. **양방향 트랩·배경 유출 0**. 스샷 `fixiter2/01-focus-trap-open.png`(초기화 포커스링 가시).
- (c) **Esc → 다이얼로그 닫힘 + 포커스 복원 = 트리거 "수신 초기화 (1)"**(BUTTON). 확인(초기화) 클릭 경로 → 리셋 성공으로 트리거 disabled → **포커스 = `<main>`(tabindex=-1) 폴백**(body 유실 0·배경 재발동 차단). 스샷 `fixiter2/02-focus-restore-after-confirm.png`.
- cleanup: `useEffect([open, onClose])` return 에서 keydown 리스너 해제·overflow 복원·포커스 복원 — onClose 안정화로 언마운트/재실행 churn 없음(아래 #2). 코드 판독 확인.

### #2 onClose 안정화(effect churn 제거) — PASS
- `ConfirmDialog.handleClose = useCallback(…, [onCancel])` + `busyRef`(busy 를 deps 밖으로) → busy 토글이 handleClose 재생성 안 함. `DataGeneratorPage.closePending = useCallback(() => setPending(null), [])` 안정 참조로 `onCancel` 전달. Dialog effect deps `[open, onClose]` 가 open 변화에만 재실행(매 렌더 churn 제거). busy 중 Escape/백드롭 닫기 무시(busyRef 가드) — 기존 busy 잠금 보존. 코드 판독 + 런타임(트랩 정상 동작)으로 확인.

### #4 토스트 톤별 role — PASS(런타임)
- error 토스트(생성 400) → **`role="alert"` / `aria-live="assertive"`**, text="ChuteNos may only contain digits, commas, hyphens, and spaces.".
- warning 토스트(#6) → **`role="status"` / `aria-live="polite"`**. MutationObserver 대신 노드 폴링으로 결정적 캡처.

### #6 count>10000 사전 차단 + noValidate — PASS(런타임)
- barcodeCount=20000 제출 → 경고 토스트 `role=status/polite` "바코드 개수는 10000 이하여야 합니다." + **`/generate` 네트워크 미발화(generateNetworkFired:false, genBefore=genAfter=0)** — 서버 400 왕복 전 클라 차단. `<form noValidate>` 로 네이티브 말풍선 대신 앱 토스트 일원화(코드 확인, min/max 스피너 힌트 유지).

### 콘솔·B2C 회귀 — PASS
- pageerror 0 · React dev-mode warning 0 · app console.error 0. 세션 유일 error 1건 = 의도적 400 network 로그(#4 error-tone 테스트, §7.1 sanction). #6 은 network 미발화라 무에러.
- B2C 회귀 0: `/sorters` h1="3DS 워드값" · 워드패널(C_CellNo~TgtFloor·D4 비트) · **SignalR "실시간 연결됨"=true**(useHubLifecycle 무접촉 실증) · B2C 세트(모니터링·3DS 워드·운영제어 F3). fix-iter2 는 Layout 미접촉이라 자연 보존. 스샷 `fixiter2/03-b2c-sorters-regression.png`.

### 정리
- dev서버·백엔드 종료 · 포트 5080/5173/1502 free · 격리 SQLite(scratchpad·현장 DB 무접촉·IF-09/10 트리거 0) · 스샷 `screenshots/S-B2B-2b_fixiter2_20260708-151200/`(gitignored). 커밋/수정/fix 0.

**FIX ITER 2 재검증 결과: 4개 갭(#1 포커스 트랩·#2 onClose 안정화·#4 토스트 role·#6 count 상한) 전부 fresh evidence PASS. S-B2B-2b APPROVED 유지.**
