# Sprint Feedback — S-B2B-3b (조회 프론트엔드: 로그·비교·박스 3화면 + 내비 점등)

**APPROVED** — Evaluator, 2026-07-08 (1 iteration to pass).

브랜치 `feat/b2b-3b-frontend-pages` (working tree, 미커밋). Evaluator 는 코드를 고치지 않음(read-only 검증).
Ground truth = git HEAD/status + 실제 코드 판독 + 실 stack 브라우저 클릭스루. Generator 요약은 신뢰하지 않고 전부 독립 재현.

핸드오프 확인: `tasks/sprint-log.md`에 `## IMPLEMENTATION COMPLETE — S-B2B-3b` 마커 존재(L3) → 활성화 정당.

---

## 검증 환경 (fresh)

- 백엔드: `dotnet run --project backend/src/Wcs.Api --no-launch-profile`, **`ASPNETCORE_ENVIRONMENT=Production`**,
  **시드 scratch SQLite**(`scratchpad/wcs-b2b-eval.db` — `dotnet ef database update`로 4개 마이그레이션 적용 후 python sqlite3로 시드).
  `Database__Provider=Sqlite` · `ConnectionStrings__WcsDb=Data Source=<scratch>` · `Database__SeedOnStartup=false`(자동 시드 미발동) ·
  **`Sorters__0__Transport=Tcp`**(현장 DB `Rcs3dsInterlockingWcs` 무접촉 · COM1/RTU 실 3DS PLC 무접촉 — lessons 2026-07-03/현장 PLC 준수). :5205 listen.
- 프론트: Vite dev `:5173`(vite.config.ts server.port=5173 = 포트 source-of-truth, `.claude/ports.local.json` 부재 → 계약이 vite.config 명시 허용). `/api`·`/hubs` → :5205 proxy.
- 시드 요약: test_data 5 · test_log 10(INPUT 6[활성5+보관1] · SORT 4[활성3+보관1]) · work_result 4(활성3+보관1) · box 3(B1×2·B2×1) · box_item 6 · api_call_log 7(2026-07-07 6 + 07-06 1).

## Completion Conditions

### #1 정적 검사(프론트) — PASS
- `npm run lint`(eslint .) → **exit 0, 0 error / 0 warning**(fresh, Evaluator 재실행).
- `npm run build`(tsc --noEmit + vite build) → **exit 0**. type-check 클린. 유일 경고 = `@microsoft/signalr` `/*#__PURE__*/` 주석(2건) + chunk>500kB — **선재**(F2 signalr 의존성, 이 스프린트 무관, index js 517kB). 이 스프린트 코드 경고 0.

### #2 기존 테스트 회귀 0 — PASS
- `dotnet test backend/Wcs.sln` → **실패 0 / 통과 288 / 건너뜀 0 / exit 0**(fresh 재실행). 백엔드 무접촉 실증.
- NU1903(SQLitePCLRaw.lib.e_sqlite3 2.1.10) 경고 5건 = **선재 전이 취약성 부채**(feedback-archive 다수 기록·이 스프린트 무관).

### #3 브라우저 클릭스루(Playwright, 실 stack) — PASS
스크린샷 17장 `screenshots/S-B2B-3b_20260708-172912/`(01~17), console.log·network-all.log 동봉. 각 시나리오 navigate→click/fill→assert 재현·판독.

**내비 점등(Layout)** — `01-b2b-nav-datagen.png`
- B2B 모드: `데이터 생성`·`로그 조회`(/logs)·`결과 비교`(/comparison)·`박스 조회`(/boxes) 활성 링크 + `설정` 비활성(generic "B2B-3 예정" 툴팁 + `B2B-3` 배지, 링크 아님). ✔
- 각 활성 링크 클릭 시 라우트 이동 + 헤더 타이틀/서브타이틀 갱신 + inset 브랜드바 active 표시 확인.

**화면 1 — 로그 조회(/logs)** — `02`~`07`
- 기본 로드: 투입 탭 5행(equipmentNo=인덕션·PID·상태·로그시각·등록슈트 파생·수신시각 파생·활성 배지). ✔
- 탭 전환: 분류(SORT) 탭 3행, API 호출 이력 탭 6행(상태코드 배지 201/200/500/400). **Excel 툴바·아카이브 필터는 API 탭에서 미노출**(계약 §D 정확). ✔
- 아카이브 필터(분류 탭): active **3** → all **4**(보관 1행 등장) → archivedOnly **1**(0707-05 보관). API 왕복으로 행집합 변화 실증. ✔
- 통합검색: "0707-02" 입력 → 3→**1**행 축소. ✔
- **Excel 다운로드**: 투입 탭 버튼 클릭 → 브라우저 다운로드 `input_sort_logs_2026-07-07.xlsx` 발생(Content-Disposition filename + RFC5987 filename* 파싱). 다운로드 파일 검증 = **유효 xlsx**(6778B · magic `50 4B 03 04` · zip 10엔트리 incl. xl/worksheets/sheet1.xml). 성공 토스트. ✔

**화면 2 — 결과 비교(/comparison)** — `08`~`10`
- 3단(투입/분류/결과) + 판정 표, 5행: 0707-01 **일치**(초록 배지), 0707-02 **불일치**(빨강 배지·행 핑크틴트, sort 002≠result 099), 0707-03 **누락**(회색 배지·행 앰버틴트·"누락" 셀 마커), 0707-04 일치, 0707-05 누락. 스크린샷 판독으로 **일치/불일치/누락 시각 구별 확인**. ✔
- 상태 필터: 전체 **5** → 불일치 **1** → 누락 **2**, 카운트 배지 계약 술어 정확 반영. ✔
- 아카이브 왕복: active→all 전환 시 0707-05(보관된 sort+result)가 **누락→일치**로 전이, 누락 카운트 **2→1**. 백엔드 archived 필터 소비 실증. ✔

**화면 3 — 박스 조회(/boxes)** — `11`~`15`
- 기본: 좌 목록 3박스(BOX-001 내품2·BOX-002 내품1·BOX-003 내품3) + 우 "박스를 선택하세요". ✔
- 마스터-디테일: BOX-001 클릭 → 우 내품 2행(0707-01×1·0707-02×2). BOX-003 클릭 → 3행(0707-03×3·0707-10×1·0707-11×1). 중첩 items[] 왕복 정확. ✔
- batch 필터 "B1" → 좌 목록 2박스(BOX-003[B2] 제외) + **선택 해제**(우 "박스를 선택하세요" 복귀). ✔

**빈/에러 상태** — `15`, `16`
- 빈: bizDay=2026-07-01 → 0행 EmptyRow "표시할 박스가 없습니다"(흰화면/무한스피너 아님). ✔
- 에러: bizDay=2026-02-30(프론트 정규식 통과·백엔드 TryParseExact 거부 400) → ErrorRow "데이터를 불러오지 못했습니다 — Invalid date: 2026-02-30"(백엔드 message 표면화·fail-loud). ✔

**계층 교차 E2E(슬롯3) — 3종 전부 실 왕복 확인**: (1) Excel 클릭→GET /api/logs/export→200 xlsx+Content-Disposition→파일다운로드 (2) 비교 active↔all 백엔드 archived 반영 (3) 박스 items 중첩 DTO 우측 표시. curl 왕복도 6엔드포인트 전수 확인(E1 5/6/1·E2 3·E3 6/7·E5 active 2match/1mismatch/2missing→all missing 2→1·E6 3/2·E4 200 6778B·empty 0·error 400).

### #4 콘솔/dev 경고 캡처(BLOCKING) — PASS
- populated 페이지(로그 투입/분류/API·비교·박스) 전 구간 **console error 0 · warning 0 · pageerror 0 · React dev 경고 0**(key/validateDOMNesting/update-depth 부재). 세션 전체 console.log 판독.
- 유일 error = 에러상태 테스트의 **의도된 400**(invalid date 2026-02-30) 3건(React Query 기본 3회 재시도) — 앱이 ErrorRow 로 명시 처리·표시하는 케이스 → **계약 명시 예외**(BLOCKING 아님). 처리되지 않은 4xx/5xx·uncaught 0.

### #5 폐쇄망 확인 — PASS
- 전체 네트워크 요청(static 포함) **전부 `http://localhost:5173` 동일 출처**. 외부/CDN 호스트 **0건**. Inter 폰트는 로컬 `/node_modules/@fontsource-variable/inter/*.woff2`(CDN 아님). `/api`·`/hubs`는 vite proxy(동일 출처). (line71 `/api/boxes` ERR_ABORTED = React Query 요청 취소, 직후 line73 200 성공 — 정상.)

### #6 브랜치 규율 — PASS
- 작업 브랜치 `feat/b2b-3b-frontend-pages`(base develop). develop 직접 커밋 아님.

## 절대 게이트
- 폐쇄망 외부요청 0: **PASS**(위 #5).
- B2C 무접촉: **PASS** — B2C 토글 → 내비 `모니터링/3DS 워드/운영 제어(F3 비활성)` 원상 복귀, StatusRail("소터 상태") 정상 렌더, MonitorPage(작업데이터/로봇이동중/분류현황 탭) 정상(`17-b2c-regression.png`). Layout.tsx 의 b2b NAV_SET 항목만 수정·b2c 배열/StatusRail/ModeToggle/PollIndicator 무접촉(코드 판독 확인).
- 설정 화면 미생성 + 설정 내비 비활성 유지: **PASS**(Layout.tsx 설정 `enabled:false`·`phase:'B2B-3'` 유지, 라우트 미추가).
- 콘솔 0 에러: **PASS**(위 #4).

## 무접촉 가드(ground truth)
- `git diff --stat -- backend/` **빈 출력** · `git status --porcelain -- backend/` **빈 출력** → 백엔드 0줄. ✔
- 변경 표면 = frontend 수정2(App.tsx·Layout.tsx) + 신규8(logs.ts·search-input.tsx·ArchiveSelect.tsx·LogsPage.tsx·ComparisonPage.tsx·BoxesPage.tsx·TestLogGrid.tsx·ApiCallLogGrid.tsx) + 태스크파일(sprint-contract/log). 계약 스코프와 정확히 일치.

## 통합 품질(코드 판독)
- 프론트 TS 인터페이스(TestLogRow/ApiCallLogRow/ComparisonRow/BoxRow/BoxItemRow)가 `QueryDtos.cs` 5 record 와 **camelCase 필드 1:1 정확 일치**(Pid→pid 포함). 6 엔드포인트 경로·파라미터(bizDay·archived·date·batch) 계약대로 결선. ArchiveFilter 는 lib/testData.ts 어휘 재사용. bizDay·autoRefresh/refreshInterval(UiModeProvider)를 DataGenerator 동형으로 존중(`refetchInterval=autoRefresh?interval:false`·`keepPreviousData`). 기존 ui/*·StateMessage·FilterCell·format 재사용 — 새 시각언어 발명 0(디자인시스템 준수).

## Minor (비블로킹 — APPROVED 무관)
- (계약 스코프 내 결함 미발견.) 참고: 에러상태에서 native `<input type=date>`가 2026-02-30을 빈값으로 표시(브라우저 제약)하나, 실 사용자 경로에선 date picker가 비존재 날짜를 못 만들므로 실질 영향 0. 결함 아님.

---

## FIX ITER 1 재검증 — 코드리뷰 #1(리스트 key)·#2(a11y role)
- 코드리뷰(Step 4.5)에서 Important 1건(#1 ComparisonPage 리스트 key `${batch} ${barcode}` 비유니크 → 배치 내 동일 바코드 중복 시 React 중복 key 콘솔 에러 = BLOCKING 콘솔 게이트) + a11y Minor 1건(#2 status 필터 버튼 `role="tab"` 오용) 발견 → fix-only iter.
- Generator 수정: ComparisonPage.tsx만 변경 — key에 맵 인덱스 추가(`${batch}-${barcode}-${i}`, BoxesPage 패턴 정합) + 부수로 발견된 stray NUL 바이트(write 아티팩트) 제거(전 10파일 스캔 0), status 필터를 `role="group"`+`aria-pressed` 토글로 교체(LOG 탭의 Radix Tabs는 무접촉). eslint 0/0, npm build clean, 백엔드 무접촉.
- **Evaluator 재검증 결과(실 확인)**: **중복 바코드까지 시드**한 /comparison + /logs + /boxes 전 세션 콘솔 **0 errors / 0 warnings — 중복 key 경고 없음, 회귀 없음.** 즉 이전 잠복 경로(중복 key)를 실제로 발현시켜 게이트 통과 확인. (재검증 도중 사용자 요청으로 orchestrator가 세션을 일시정지 → 본 결과는 Evaluator가 정지 직전 보고한 실제 판독을 orchestrator가 기록·영속화한 것.)
- 이연 Minor 6건(ArchiveSelect↔DataGenerator DRY·박스 선택 잔존·검색 haystack·그룹 구분선·Tabs 이중간격·미가상화)은 tasks/todo.md 등재.

**APPROVED (fix iter 1 재검증 포함)**
