# Sprint Feedback — S-DEV-SEED-GUARD (자동 시드 전면 차단 · 실사고 재발 방지) — APPROVED

## Phase 3 Evaluate (Evaluator fresh evidence, branch `fix/dev-seed-guard`, working tree, 2026-07-03)

**최종 판정: APPROVED** — 검증 6기준(코드 판독·테스트·음성 재현·양성 대조·현장 DB 무접촉·무변경 가드) 전부 PASS. 전 증거는 Evaluator가 지금 직접 재실행/재관찰한 raw tool output(테스트 러너 요약·dotnet run 콘솔 로그·sqlcmd 카운트·git diff). Generator 요약은 신뢰하지 않고 전부 fresh 재현. 코드 수정·커밋 없음.

핸드오프 마커 확인: `tasks/sprint-log.md:1959` `## IMPLEMENTATION COMPLETE (S-DEV-SEED-GUARD)` 존재.
검증 인프라: SQL Server localhost(Windows 인증). 빈 스크래치 DB `WcsSeedGuardEval`(Generator의 `WcsSeedGuardTest`와 다른 이름) + `ConnectionStrings__WcsDb` env 오버라이드로 현장 DB `Rcs3dsInterlockingWcs` **무접속**. Backend/API — 라이브 dotnet run + 실 SqlServer 쿼리.

---

### ① 코드 판독 (DbInitializer + appsettings diff) — PASS
- **`IsDevelopment()` fallback 제거 확정**: `git diff` 판독 — `var seedEnabled = seedOnStartup ?? app.Environment.IsDevelopment();` 삭제, `if (ShouldSeed(seedOnStartup))`로 교체. 코드 경로에 `IsDevelopment` 참조 0(잔존 2건은 전부 주석·XML doc 서술 — grep 확인).
- **`ShouldSeed` 순수 함수**: `public static bool ShouldSeed(bool? seedOnStartup) => seedOnStartup == true;` — I/O·WebApplication·DI 의존 0(절대규칙 #8 정신). `null`/`false`/미지정 전부 `false`.
- **WARNING은 시드 거부 아님(로그)**: 비 in-memory 시드 직전 `log.LogWarning(...)` 1줄(provider+database+dataSource) 후 **곧바로 `DbSeeder.Seed(db, ...)` 실행** — throw/거부 없음. 명시 true는 정당한 요청으로 통과(함정4 준수).
- **appsettings 2종 diff = 주석+false 값만**: `appsettings.Development.json` = `_comment`(launchSettings 부재→기본 Production 정정)·`_comment_SeedOnStartup`(경고 주석) + `SeedOnStartup: true→false`. `appsettings.json` = `_comment_SeedOnStartup` 1줄만 변경(값 `false`·Sorters[]·Provider=SqlServer·ConnectionStrings 전부 불변, git diff로 확인).

### ② 전체 테스트 164/164 GREEN ≥2회 + 신규 게이트 3케이스 — PASS
- `dotnet build backend/Wcs.sln` → **경고 0 / 오류 0** (6s).
- `dotnet test backend/Wcs.sln --no-build` **RUN 1** → `실패:0, 통과:164, 건너뜀:0, 전체:164` (14s, exit 0).
- **RUN 2** (재실행, 독립) → `실패:0, 통과:164, 건너뜀:0, 전체:164` (14s, exit 0). teardown 클린(blame 시퀀스 파일 미생성·hang 0).
- 신규 `DbSeedGateTests` filter → `실패:0, 통과:3, 건너뜀:0` (18ms): `ShouldSeed_Null_ReturnsFalse`·`_False_ReturnsFalse`·`_True_ReturnsTrue`. 기존 161 + 신규 3 = 164 정확. 기존 테스트 파일 diff 0(§⑥).

### ③ Development 라이브 음성 재현 — 실 DB 시드 0행 — PASS
빈 스크래치 DB `WcsSeedGuardEval` + `ASPNETCORE_ENVIRONMENT=Development` + `ConnectionStrings__WcsDb` 오버라이드로 `dotnet run --project backend/src/Wcs.Api` 기동(현장 DB 미접속). 콘솔 로그 raw:
```
[INF] [DbInitializer] 콜드스타트 자동 Migrate 시작 (provider=Microsoft.EntityFrameworkCore.SqlServer).
[INF] [DbInitializer] Migrate 완료 — 스키마 보장됨.
[INF] [DbInitializer] 시드 게이트 off(운영 안전) — 빈 스키마만 프로비저닝. dev 시드가 필요하면 Database:SeedOnStartup=true를 명시하십시오(환경만으로는 발동하지 않음).
[INF] [ChuteCapacity] 인메모리 집계 초기화 완료. 슈트 수=0
[INF] [SorterRegistry] SORTER_3D destination 0대 조회됨 / 초기화 완료 — 소터 0대
[INF] Now listening on: http://0.0.0.0:5080 / Application started. / Hosting environment: Development
```
- (a) **시드 게이트 off 로그 출현** — 시드 미실행. ✓
- (b) 스크래치 DB **도메인 테이블 17종 전부 0행**(sqlcmd: `DOMAIN_TABLE_COUNT=17, TOTAL_DOMAIN_ROWS=0`). destination=0·piece=0·cell=0·agv=0·wcs_order=0·order_item=0·cell_assignment=0. `__EFMigrationHistory` 2행(Initial+AddOperationLog)은 마이그레이션 bookkeeping이지 시드 아님. ✓
- (c) 빈 DB+시드 off → SORTER_3D 0대 → **fail-loud throw 없이 정상 기동**(:5080 LISTENING, Development). 사고의 기동 거부 증상 소멸. ✓

### ④ 양성 대조 — 명시 true 경로 생존 + 사고 정밀 재현 — PASS
같은 스크래치 DB + `Database__SeedOnStartup=true` 기동. 콘솔 로그 raw:
```
[WRN] [DbInitializer] Database:SeedOnStartup=true — 비 in-memory DB에 dev 시드를 주입합니다. provider=...SqlServer, database=WcsSeedGuardEval, dataSource=localhost. ⚠ 현장 운영 DB라면 마스터데이터가 오염될 수 있습니다 — Provider/ConnectionStrings가 dev 전용으로 오버라이드됐는지 반드시 확인하십시오.
[INF] [DbInitializer] dev 시드 적용됨 (트리거: Database:SeedOnStartup=true 명시).
[INF] [ChuteCapacity] 슈트 수=6 / [SorterRegistry] SORTER_3D destination 1대 조회됨
[FTL] [SorterRegistry] SORTER_3D destination(id=6 chuteNo=30)에 대한 appsettings Sorters[] 항목 없음 — 기동 불가(fail-loud).
```
- **WARNING 로그 출현 + 시드 실행됨** — 게이트가 명시 true를 막지 않음(throw 없이 통과). ✓
- 부수: 시드가 SORTER_3D chuteNo=30 생성 → appsettings `Sorters[ChuteNo=1]`과 미스매치 → SorterRegistry fail-loud. **2026-07-03 현장 사고 메커니즘을 스크래치 DB에서 정밀 재현**(명시 경로가 살아있음 + 오버라이드 없는 명시 true의 위험성 동시 입증).

### ⑤ 현장 DB `Rcs3dsInterlockingWcs` 무접촉 — PASS
검증 전 baseline과 검증 후 카운트 **동일**(sqlcmd 양측):
`destination=1, piece=0, cell=16, agv=0, wcs_order=16, order_item=16, cell_assignment=16` → 변동 0. 두 라이브 기동 모두 `ConnectionStrings__WcsDb`를 `WcsSeedGuardEval`로 명시 오버라이드해 현장 DB에 연결하지 않음. 검증 종료 후 스크래치 DB `WcsSeedGuardEval` **DROP** 완료(잔존 0), 포트 5080 free, 오펀 프로세스 0.

### ⑥ 무변경 가드 (스코프 격리) — PASS
- `git diff --stat`: 코드 변경 = `DbInitializer.cs`·`appsettings.Development.json`·`appsettings.json`(주석 1줄) + 신규 `DbSeedGateTests.cs`. 그 외는 tasks 문서(sprint-log/contract/todo/lessons)만.
- `git diff -- backend/src/Wcs.Data/DbSeeder.cs` = **빈 출력**(토폴로지 불변).
- `git diff -- frontend/` = **빈 출력**. 마이그레이션(Sqlite/SqlServer) diff = **빈 출력**.
- appsettings.json `Sorters[]`·`Provider=SqlServer`·`ConnectionStrings` diff 0. 기존 테스트 파일 변경 0(신규 파일만 추가).

### Completion 부수 등재 확인
- `tasks/todo.md`: `## S-DEV-SEED-GUARD 후속`에 DbSeeder `First(ChuteNo==1&&CHUTE)` 크래시 `FirstOrDefault`+skip 하드닝 항목 등재됨(git diff 확인). ✓
- `tasks/lessons.md`: "환경만으로 자동 시드 발동 = 실 DB 오염 벡터; 명시 설정으로만·Development.json은 반드시 Provider/연결 오버라이드 동반" 1행 추가됨. ✓

**결론: ①~⑥ 전부 PASS. APPROVED.** 이 스프린트는 S-M5-P1 archive(line 39)가 명시한 `SeedOnStartup ?? IsDevelopment()` 설계 — 즉 사고의 근본 원인 — 를 순수 함수 게이트(`ShouldSeed(bool?)==true`)로 정확히 제거하고, 음성/양성 라이브 재현으로 재발 방지를 실증했다.

---

# Sprint Feedback — S-FE-AIRBNB (프론트 Airbnb 리스타일 · 스타일 전용) — APPROVED

## Phase 3 Evaluate (Evaluator fresh evidence, branch `feat/frontend-airbnb-restyle`, working tree, 2026-07-03)

**최종 판정: APPROVED** — 검증 6기준 + Web/UI 슬롯 전부 PASS. 전 증거는 Evaluator가 지금 직접 재실행/재관찰한 raw tool output(Playwright computed style·스크린샷·명령 출력). Generator 요약은 신뢰하지 않고 전부 fresh 재현. 코드 수정·커밋 없음.

핸드오프 마커 확인: `tasks/sprint-log.md:1932` `## IMPLEMENTATION COMPLETE (S-FE-AIRBNB)` 존재.
검증 인프라: 백엔드 :5080(Sqlite dev seed, `Database__Provider=Sqlite` + `Sorters__0__ChuteNo=30` env override — 기존 dev 기동 드리프트는 스코프 밖 기등재) + vite dev :5173, Playwright 1128px 라이브 구동. 포트 정책: `ports.local.json` 부재이나 :5080=`appsettings.Urls`·:5173=`vite.config.server.port` committed config source-of-truth(F1 교훈) → 위반 아님.

---

### ③ 빌드·정적검사 GREEN — PASS
- `npx tsc --noEmit` → **exit 0**.
- `npm run lint`(eslint .) → **exit 0** (신규 warn 0).
- `npm run build`(tsc --noEmit && vite build) → **exit 0**, 1679 modules. 산출 `../backend/src/Wcs.Api/wwwroot/`: `index.html`(0.48KB)·`assets/index-*.css`(22.64KB)·`assets/index-*.js`(391.40KB) + Inter woff2 서브셋 8종. (런타임 폰트 fetch는 latin 서브셋만 — 아래 ① 참조.)

### ④ backend 0줄 · 161 GREEN 불변 — PASS
- `git diff --stat -- backend/` → **빈 출력**(backend 소스 0줄, git status에도 backend/ 변경 0).
- 확인적 `dotnet test backend/Wcs.sln` → **실패 0 · 통과 161 · 건너뜀 0 · 전체 161 · exit 0**(13s). NU1903(SQLitePCLRaw 2.1.10) 경고 10건은 **선재 부채**(base develop, 본 스프린트·frontend 무관, todo 기등재).

### ⑤ 로직 diff 0 (스타일 격리) — PASS
- `git diff -- frontend/src/lib/` → **빈 출력**(api·queries·format·utils·status 전부 0줄. status.ts 매핑 불변 재확인).
- `git diff -- frontend/src/App.tsx frontend/src/main.tsx frontend/vite.config.ts` → **빈 출력**.
- 변경 15파일 = `index.html`(color-scheme dark→light 1줄)·`package.json`+`package-lock.json`(@fontsource-variable/inter 의존성)·`index.css`(@theme 재매핑)·ui 6종(card/button/badge/tabs/table/select/meter)·Layout·StatusRail·2섹션(Sorting/WorkData). diff 직접 판독: **전부 className·@theme 토큰·arbitrary radius/shadow·color-scheme·의존성만**. JSX 트리 구조·props 시그니처·데이터 훅·useEffect·페이징 로직 무접촉.
- 유일한 시그니처 변경 = `StatusRail.tsx` `Lamp` tone union에 `'paused'` 1개 추가 — 계약 §3 명시 예외(데이터 무관 시각 상수). 확인.

### ① 시각 적용 충실도 (최중요 · Playwright 1128px) — PASS
computed style raw(`screenshots/AIRBNB_eval/computed.json`·`token-colors.json`):
- **순백 캔버스**: `body` bg = `rgb(247,247,247)`(#f7f7f7), `body` background-image = **`none`**(블루프린트 그리드 제거). 카드 bg = `rgb(255,255,255)`(#fff). 다크 잔재 0.
- **잉크 텍스트**: h1 color = `rgb(34,34,34)`(#222).
- **라운딩**: 카드/셀타일/서브행 `border-radius: 14px`(4셀 타일 전수 14px)·버튼/셀렉트 `8px`·배지 `rounded-full`(pill, 계기판식 rounded-md 아님).
- **헤어라인**: `border-line` = `rgb(221,221,221)`(#ddd), thead/행 구분·카드 보더에 적용.
- **단일 그림자 티어**: 카드·StatusRail 타일 box-shadow = `rgba(0,0,0,0.02) 0 0 0 1px, rgba(0,0,0,0.04) 0 2px 6px 0, rgba(0,0,0,0.1) 0 4px 8px 0` — 문서 정확 값 일치. 다크 인셋 그림자 제거.
- **Inter + 한글 폴백**: h1 font-family = `"Inter Variable", Inter, "Malgun Gothic", "Apple SD Gothic Neo", system-ui, …` weight 600. 스크린샷 육안: 한글(WCS 관제·실시간 모니터링·작업 데이터·로봇 이동중·분류 현황·오더아이템·폴링 3s 등) **두부(tofu) 0**, 영문/숫자 Inter 정상. (F1 대비: F1은 Segoe UI, after는 Inter 적용.)
- **활성 탭 = 잉크 언더라인**: activeTab color `rgb(34,34,34)`·border-bottom `2px rgb(34,34,34)`·weight 600(accent-fill/box 제거 확인).
- 스크린샷: `01-work-data`·`02-work-expanded`·`03-inflight`·`04-sorting`·`05-sorting-full`·`06-statusrail`(`screenshots/AIRBNB_eval/`). before(다크)=`screenshots/F1_20260703-115749/` 대조 관찰: 딥블루 그래파이트+블루프린트 그리드 → 순백+헤어라인으로 전면 전환 확인.

**Rausch 절제(2C) — PASS(오염 0)**: 전 DOM 요소 스캔(`rauschUsage`)에서 `rgb(255,56,92)`(#ff385c) 사용처 = **정확히 2곳**: ①로고 마크 div `bg-brand` ②활성 내비 링크 '모니터링' `box-shadow inset 3px 0 0 0` 좌측 마커. 상태 배지·테이블·진행바·in-progress·페이저에 Rausch **0건**. (primary 버튼은 이 관제 화면에 렌더 CTA 없음 — button.tsx solid=bg-brand 정의만 존재, 화면 오염 없음.)

### ⑥ 상태색 의미 보존 (육안 + hex) — PASS
token 유틸 클래스 computed 해상(`token-colors.json`):
- `bg-brand`(Rausch) = `rgb(255,56,92)` #ff385c
- `bg-offline`(OFFLINE/MISMATCH) = `rgb(193,53,21)` #c13515 → **Rausch와 hex·톤 구분**(brick-red vs hot-pink)
- `bg-paused`(PAUSE 램프) = `rgb(106,106,106)` #6a6a6a → **OFFLINE 적과 명확 구분**(중립 회, 함정4 해소). StatusRail PAUSE tone offline→paused 코드 변경 확인.
- `bg-online` = `rgb(10,125,51)` #0a7d33(녹) · `bg-warn` = `rgb(180,83,9)` #b45309(황) · `bg-busy` = `rgb(14,116,144)` #0e7490(틸) · accent = #2563eb(인디고, index.css @theme·source 확인) — 전부 상호 톤 구분, Rausch 아님.
- **라이브 관측**: (a) StatusRail `06-statusrail.png` — 오프라인 소터 "3DS #30" 좌측 dot이 brick-red(#c13515), 로고 hot-pink(#ff385c)와 육안 확연 구분. (b) 분류 현황 `04-sorting.png` 셀 범례 pill: 여유=녹·근접=틸·만재=황·비활성=회 — 4색 구분·Rausch 미혼용. (c) 오더 배지 RUNNING = busy 틸 pill(status.ts RUNNING→busy), 서브행 예약=틸/분류=녹.
- 대비: 백(#fff/#f7f7f7) 배경 위 잉크 #222·상태색 전부 가독(AA 스팟 체크 OK).
- ⚠ 관측 한계(비차단): 현재 소터 offline이라 RDY/FULL/PAUSE 램프가 전부 off(bg-line #ddd)로 점등색을 라이브로 못 봄 → CSS 유틸 해상(bg-paused #6a6a6a ≠ bg-offline #c13515)으로 대체 입증 + 코드 tone 변경 확인으로 갈음. 점등 상태 육안은 후속(소터 online+paused 시드 필요).

### ② 기능 무회귀 (클릭스루 · 콘솔) — PASS
- **탭 전환**: 작업 데이터 / 로봇 이동중 / 분류 현황 3탭 전부 클릭 전환·렌더(스크린샷 01·03·04).
- **행 확장→오더아이템**: ORD-005 expander 클릭 → 서브행 "오더아이템 (1)" + `TEST-BARCODE-PAUSED`(계획10·예약0틸·분류0녹) 조회 성공(`02-work-expanded.png`, 14px white 서브카드).
- **셀렉트**: 배치(2026-07-03·SEED·W1·RUNNING)·상태 필터·소터(3DS #30 오프라인) 셀렉트 렌더·값 반영.
- **페이징**: in-flight/sorter-command 커서 페이저 이전/다음 렌더, 빈 목록에서 disabled(정상 동작).
- **폴링 재요청**: 좌하단 "폴링 3s" + 타임스탬프가 스크린샷 걸쳐 15:37:25→26→27→28 증가 = 3초 폴링 라이브 동작.
- **콘솔(`screenshots/AIRBNB_eval/console.log`)**: 총 3줄 = `[vite] connecting/connected`(HMR)·React DevTools info뿐. **error 0·warning 0·pageerror 0·network 4xx/5xx 0**. (Generator 언급 favicon 404는 내 networkidle 관측창에서 미발현 — 무해·전가.)
- **대체 상태(Web/UI 슬롯)**: 빈 상태 2종 라이브 캡처 — in-flight "현재 이동중인 piece가 없습니다"(`03`), sorter-command "적재 이력이 없습니다"(`04`), Inbox 아이콘. 로딩/에러는 `StateMessage.tsx`가 토큰 전용(Loading=text-muted·Error=text-offline·Empty=text-faint)이라 라이트 테마 자동 전환(하드코딩 다크색 0, 소스 확인) — 빈 상태 라이트 렌더 입증으로 갈음.
- **반응형**: 1128px(문서 Desktop 기준) 전수 검증. 셀 그리드 3열 정상.

**다크 잔재 스캔**: `frontend/src/` 전역 grep(bg-slate/gray/zinc/sky·다크 hex·rgba(56,189,…) 구 sky) → 잔재 0. `text-white`는 로고 아이콘·primary 버튼(on-brand 백색)뿐 = 정당.

---

## Minor / 후속 (APPROVED 비차단 — 다음 sprint Generator 읽음)
- **[S-FE-AIRBNB] PAUSE/RDY/FULL 램프 점등색 라이브 미관측**: 현 시드가 소터 offline이라 램프 off(회)만 봄. bg-paused(#6a6a6a)≠bg-offline(#c13515) CSS 해상 + tone 코드 변경으로 갈음 입증했으나, 소터 online+paused/full 시드로 점등 육안 확인은 후속 권고.
- **[S-FE-AIRBNB] 로딩/에러 StateMessage 라이브 미강제**: 빈 상태 2종은 라이브 캡처. 로딩/에러는 토큰 전용 소스라 자동 전환이나 강제 트리거 캡처는 미수행(비차단).
- **[S-FE-AIRBNB][기존부채·backend] dev 콜드스타트 드리프트**: DbSeeder가 소터 chuteNo=30 시드 vs `appsettings.Sorters[0].ChuteNo=1` → SorterRegistryFactory fail-loud. 검증은 `Sorters__0__ChuteNo=30` env override(추적파일 무변경)로 우회. frontend 스코프 밖·기등재, backend 후속.
- **[S-FE-AIRBNB][기존부채] NU1903** SQLitePCLRaw 2.1.10 high-severity advisory(빌드 경고 10) — 본 스프린트 무관, todo 기등재.

## 핸드오프 상태 (검증 후 정리 완료)
- 프로세스/포트: :5080·:5173 dev 서버 kill, 둘 다 **FREE** 확인. 신규 orphan 없음. 검증용 Sqlite(scratchpad)·screenshots만 산물.
- git 상태 불변: 15 frontend 파일 M + `tasks/sprint-contract.md`·`tasks/sprint-log.md`(M, 하네스 산출물) + `.claude/`·`docs/DESIGN-airbnb.md`(??). 커밋/브랜치 조작 없음.

## Step 4.5 독립 코드리뷰 (orchestrator — Evaluator의 무대상 판단 기각 후 실행) — BLOCKING 0 / MAJOR 2 / MINOR 5

스코프 격리·다크 잔재 제거·Rausch 절제·상태색 분리는 견고(리뷰 확인). 실질 결함은 접근성 2건 — 값은 스펙 출처이나 적용이 WCAG AA 본문(4.5:1) 미달:

### MAJOR (비차단 — F2 착수 시 처리, F2 Generator 필독)
- **[RESTYLE-CR-M1]** 백 텍스트 on Rausch primary 버튼 = 3.52:1 (button.tsx solid, 13px 라벨). Airbnb 브랜드 고유 조합 + 계약이 명시 지시한 트레이드오프. AA 엄수 결정 시 안정 상태 fill을 brand-active(#e00b41, 4.89:1)로 — 스펙 이탈 없이 통과 가능. 사용자 판정 대기.
- **[RESTYLE-CR-M2]** faint(#929292)=3.11:1이 정보성 캡션·타임스탬프·시퀀스 컬럼에 광용 — DESIGN 문서는 이 색을 "disabled 전용·very sparingly"로 스코프. 정보성 텍스트는 muted(#6a6a6a, 5.41:1)로 교체 권고(F2에서 1클래스 치환 수준).

### MINOR (후속)
- meter.tsx #f2f2f2 하드코딩×3 → @theme 토큰(--color-track) / rounded-[14px]×4 → --radius-card 토큰화 / shadow-card 주석의 Select 죽은 참조 제거 / 폰트 import를 @fontsource-variable/inter/latin.css로(현재 전 서브셋 dist 방출 — 런타임은 unicode-range로 latin만 다운로드라 무해) / 배지 warn·accent 틴트 위 4.39·4.49(경계 미달 — 틴트 /8 또는 텍스트 진하게).

상태색 본체 전부 AA 통과(online 5.26·offline 5.54·warn 5.02·busy 5.36·accent 5.17·muted 5.41·ink 15.9 — WCAG 산술).

## Step 4.5 독립 코드리뷰 — BLOCKING 0 / MAJOR 0 / MINOR 1

ShouldSeed 순수성·bool? 시그니처(null→false 명시가 사고 방지 핵심)·WARNING 로그(대상 DB 식별·비밀 미노출)·주석 정확성(launchSettings 부재 사실 확인)·테스트 전수 커버 전부 양호. MINOR 1(주석 stale 라인번호 L57→이름 기반 참조)은 커밋 전 orchestrator가 오타급으로 즉시 정정(리뷰어 권고). 정보성: DbInitializer 배너 '5개 팩토리' 카운트는 사전존재 주석 — 후속.
