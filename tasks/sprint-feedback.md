# Sprint Feedback — S-FRONTEND-F2 (SignalR 실시간 + 3DS 워드 뷰 + oplog 테일) — APPROVED

## Phase 3 Evaluate (Evaluator fresh evidence, branch `feat/frontend-f2`, working tree, 2026-07-06)

**최종 판정: APPROVED** — 검증 6항 + Full-stack 슬롯 전부 PASS. 전 증거는 Evaluator가 지금 직접 재실행/재관찰한 raw tool output(테스트 러너 요약·실 SignalR 클라이언트 로그·Playwright 스크린샷/DOM·sqlcmd 카운트·git diff·negotiate HTTP). Generator 요약은 신뢰하지 않고 전부 fresh 재현. 코드 수정·커밋 없음.

핸드오프 마커 확인: `tasks/sprint-log.md:3` `## IMPLEMENTATION COMPLETE (S-FRONTEND-F2)` 존재.
라이브 인프라: 실 SqlServer `Rcs3dsInterlockingWcs`(localhost, Windows 인증, 현장 field-16cell 시드 상주) + Sim3ds(:1502 TCP) + Wcs.Api(:5080, Production·`Sorters__0__Transport=Tcp`·시드 게이트 off) + 빌드된 SPA(wwwroot 정적 서빙) + 실 `@microsoft/signalr` 클라이언트(WebSockets) + Playwright(Chromium headless).
스크린샷: `screenshots/S-FRONTEND-F2_20260706-130457/` (01-bootstrap · 02-delta-highlight · 03-handshake-oplog · 04-oplog-filter-handshake · 05-before-restart · 06-disconnected · 07-reconnected · console.log · console-reconnect.log).

---

### ③ 빌드/테스트 — PASS
- **tsc**: `npx tsc --noEmit` → exit 0(에러 0). **eslint**: `npx eslint .` → exit 0. **build**: `npm run build` → exit 0(SPA → `backend/src/Wcs.Api/wwwroot/`; `/*#__PURE__*/` 안내는 signalr 패키지의 rollup 주석 위치 경고로 무해·빌드 성공).
- **`dotnet test backend/Wcs.sln`**: RUN1(빌드포함)·RUN2·RUN3·RUN4·RUN5(--no-build) **전부 `실패:0 통과:169 건너뜀:0 전체:169` exit 0**(각 14~15s). 164 기존 + 5 신규(MonitorHubTests) = 169. NU1903(SQLitePCLRaw)은 선재 transitive advisory·코드 무관.
- **기존 테스트 파일 diff 0**: `git diff --stat -- backend/tests/` = `Wcs.Tests.csproj`만(+SignalR.Client 9.0.5 PackageReference). 신규 `MonitorHubTests.cs`는 untracked. 기존 테스트 본문 0 변경.

### ① 실시간 워드 동작 (라이브·핵심) — PASS
프로토콜 계층(실 `@microsoft/signalr` WebSockets 클라이언트) + 브라우저(Playwright /sorters) 이중 입증.
- **부트스트랩**: 접속 시 `Bootstrap` 스냅샷 1회 수신 — `sorters=1`, 전체 D0~D6+비트+Online+curFloor+tgtFloor. 브라우저: WordPanel이 부트스트랩 값으로 렌더(`스냅샷 대기 중` 소멸·`실시간 연결됨` 배지). [01-bootstrap.png 판독]
- **push 갱신(폴링 아님)**: curl `IF-05→IF-09→IF-10` 1사이클 → **RegisterDelta 21건이 ws 프레임으로 push**(REST 재요청 아님) — `Ready 1→0 / TgtFloor 0→2(정렬) / CurFloor 1→2(Sim 이동) / R_CellNo·R_Seq·R_Flag 0→1→0(C/R 핸드셰이크) / CLEAR_R`. 타이밍 차트 정합·`HS_RSEQ_MATCH` 성공. 브라우저: 사이클 중 `.value-flash` 클래스 **최대 7개 동시**(하이라이트 작동), 각 필드 **"변경 HH:mm:ss" 마지막 변경 시각 7건 갱신**. [03-handshake-oplog.png 판독 — D0~D3·D4비트 전부 "변경 07-06 13:05:04"]
- **재연결 부트스트랩 복구**: API 프로세스 kill→재기동 → 배지 `실시간 연결됨 → 재연결 중… → 실시간 연결됨`, `reconnected=true`, WordPanel 값 복구(curFloor=2·online·`스냅샷 대기 중` 미출현). [05/06/07 판독]
- **콘솔 에러 0**: 정상 세션(eval-browser) `console total=0 errors=0`(favicon data-URI로 /favicon.ico 404 없음). 재연결 창에서만 8건 — 전부 의도적 API-down 구간(04:07:18~24)의 WebSocket 1006 close·ERR_CONNECTION_REFUSED·negotiation-failed(재연결 재시도) — 애플리케이션이 설계대로 처리하는 transient 연결 오류(앱/React 결함 아님, 명시).

### ② operation_log 테일 — PASS
- **REST 백로그**: `GET /api/monitor/operation-log?take=5` → 키셋(Id 내림차순) 항목 반환·기본 스트림에 POLL_CHANGE 미포함(STATE 등만). 코드 판독(`MonitoringQueries.GetOperationLog`): category 미지정 → `Category != POLL_CHANGE`(옵트아웃 기본)·명시 category=POLL_CHANGE만 옵트인·잘못된 category/level → 빈 결과+200(500 아님)·`AsNoTracking`·take clamp(200)·기존 쿼리 무변경.
- **라이브 append**: 프로토콜 33엔트리(API/PLC_WRITE/HANDSHAKE) 실시간 스트리밍. 브라우저: 테일 행 46→58(+12) 자동 append.
- **POLL_CHANGE 옵트인**: 사이클1(기본 구독) POLL_CHANGE=0 / `SubscribePollChange` 후 사이클2 POLL_CHANGE=8 수신 — 기본 미표시·옵트인 실증.
- **필터**: 브라우저에서 category=HANDSHAKE 선택 → 표시 12행 전부 HANDSHAKE(non-match 0). [04-oplog-filter-handshake.png]

### ④ relay 무영향 (핸드셰이크 타이밍 회귀 0) — PASS
- 기존 E2E(A~I)·소터 핸드셰이크 통합 테스트 포함 **full-suite 5회 연속 169/169 GREEN·exit 0·flake 0**(S-E2E/S9 교훈대로 1회 신뢰 금지·≥5회). 타이밍 회귀 0.
- relay 콜백 논블로킹·fire-and-forget·예외 격리 소스 확인: `MonitorRelayService.Broadcast`(try/catch + `ContinueWith(OnlyOnFaulted)` 관찰만)·`OperationLogService.EmitToObservers`(핸들러 try/catch — 스트림 실패가 기록/컨슈머 비차단, SaveChanges 이전 발화). 구독 시점: `[MonitorRelay] 시작 — 소터 1대 구독` 라이브 로그로 AllBundles가 SorterRegistryFactory.StartAsync 이후 유효 실증.

### ⑤ 명암비 이월 2건 — PASS
- **RESTYLE-CR-M1(버튼)**: `button.tsx` solid variant 안정/hover fill `bg-brand-active`(토큰 `--color-brand-active:#e00b41`) — 백 라벨 대비 **4.90:1**(산술 재계산·AA≥4.5 충족). `bg-brand`(#ff385c, 3.52:1) 미달값은 안정 상태에서 제거.
- **RESTYLE-CR-M2(정보성 텍스트)**: 데이터 담는 `text-faint`→`text-muted`(`--color-muted:#6a6a6a`, **5.41:1** AA) — SortingSection(C_Seq/R_Seq/C기입/R수신/배정오더)·InFlightSection(등록 시각)·CursorPager(페이지 카운트). 장식/비활성 faint(#929292, 3.11:1)는 비활성 nav·off 램프·로고 서브캡션 등 **비-데이터**에만 잔존(정당). 브라우저 렌더 스크린샷으로 버튼/muted 텍스트 육안 확인.

### ⑥ 무변경 가드 (스코프 격리) — PASS
- `git diff -- backend/src/Wcs.PlcGateway backend/src/Wcs.Core` = **빈 출력**(훅 시그니처·판정 의미 불변). RcsController·마이그레이션(Sqlite/SqlServer)·DbSeeder·WcsDbContext diff = **빈 출력**.
- `appsettings.json` = `Wcs:Monitor:HeartbeatMs=5000` 섹션 추가만(Sorters/Provider/ConnectionStrings 불변). operation_log **스키마 0**(GetOperationLog는 조회만).
- `OperationLogService`: 훅 시그니처 무변경 — `OnEntry` 이벤트 추가 + fail-safe `EmitToObservers`(기존 `FlushBatchAsync` 앞 발화, 배치/teardown/fail-safe 동작 불변, 브로드캐스트 얹기 전용).
- **F1-CR-M1 해소**: `AddScoped<IMonitoringQueries, MonitoringQueries>()` 등록 + MonitoringController 생성자 주입(요청당 `new MonitoringQueries(...)` 손조립 폐지) — diff 판독 확인.
- **negotiate 결선**: 라이브 `POST /hubs/monitor/negotiate?negotiateVersion=1` → **HTTP 200**(404 아님, `/api/{**rest}` catch-all·MapFallbackToFile가 /hubs를 삼키지 않음) + availableTransports(WebSockets/SSE/LongPolling). 통합 테스트 ④(MonitorHubTests)도 negotiate 200 고정.

### Full-stack 슬롯 — PASS
- **FE(Web/UI)**: /sorters 읽기 전용 워드 뷰 — Airbnb 라이트 테마 일관·타이포/간격 정돈·읽기전용/온라인 배지·D4 비트 램프. NAV "3DS 워드" 활성(고아 아님·F2 배지 제거). 콘솔 0.
- **BE(API)**: WcsMonitorHub(부트스트랩·그룹 구독)·MonitorRelayService(기존 훅 재사용·신규 폴 0)·operation-log REST(키셋·필터)·DI AddScoped. camelCase payload가 프론트 TS 타입과 1:1.
- **Integration/E2E**: IF-05/09/10 실 핸드셰이크 → SignalR push → DOM 갱신+하이라이트+테일 append의 계층 관통 흐름을 라이브로 관찰. 계약 무효화 연동(useMonitorHub) 결선.

### 정리 검증 (검증 산물 클린 복원)
- 실 DB 전후 대사(sqlcmd): **piece=0·piece_event=0·sorter_command=0·active_assign=16/16·ReservedQty=0·SortedQty=0·operation_log=20(maxid=174)·alarm=10(maxid=16)** — 기동 전 baseline 동일 복원(사용자 자체 실행 이력 oplog/alarm 불변 유지·검증 생성분만 삭제, 필터인덱스 QUOTED_IDENTIFIER 준수). 검증 중 released된 셀 배정 3행(cell 1/2/3) 복원.
- 프로세스/포트: :5080·:1502·:5173 free·오펀 0. 임시 스크립트(eval-*.mjs)·eval-tmp·logs/ 삭제. 스크린샷은 gitignored(`screenshots/`)라 트리 무영향.
- **git 핸드오프 동일**: 23 modified + 10 untracked = 착수 시점과 동일. (주의: 검증용 dev 의존 playwright 설치가 tracked `package.json`/`package-lock.json`을 건드려, `@microsoft/signalr`만 남도록 Generator 의도분을 충실히 재구성함 — package.json `+@microsoft/signalr@^8.0.17`·package-lock +175줄로 원 diff와 일치 확인.)

---

**결론: ①~⑥ + Full-stack 슬롯 전부 PASS → APPROVED.** relay가 폴/핸드셰이크 핫패스를 지연시키지 않고(5회 GREEN·소스), 관측 훅 재사용으로 신규 폴 루프 0, 페이지 ②·oplog 테일이 실 push로 동작하며, 이월 3건(DI·명암비 2건) 해소·무변경 가드 유지.

---

## Fix Iteration 검증 (코드리뷰 M1·M2 해소 — Evaluator 델타 검증, 2026-07-06 14:12) — PASS · **APPROVED 유지**

Generator fix-only iteration(sprint-log `## FIX ITERATION COMPLETE` 마커 확인)을 델타 검증. 전 증거 fresh.

### 1. diff 범위 — PASS
- **tracked diff 무변동**: `git diff --stat` 소스 파일 전부 1차 평가 시점과 삽입/삭제 수 동일(tasks/* 문서만 변동). 
- **untracked mtime 판독**: fix 창(14:00)에 변경된 파일 = `frontend/src/lib/signalr.ts`(14:00:24) + `backend/src/Wcs.Api/Services/MonitorRelayService.cs`(14:00:57) **단 2개**. 나머지 신규 소스 8개 전부 11:11~12:32(1차 평가 이전) 유지.
- **내용 대조(1차 평가 때 캡처한 전문과 비교)**: `MonitorRelayService.cs`는 `_opLogHandler` 필드 주석 블록(L38-44)만 확장(M1 — StopAsync 해제는 OnEntry 한정·PLC 훅 구독은 host-lifetime 싱글톤 의도 명문화), **실행 코드 바이트 동일**. `signalr.ts`는 M2 3변경(+19줄)만: ① `withAutomaticReconnect({nextRetryDelayInMilliseconds})` 무한 상한 백오프(0/2s/10s/30s→30s 고정·항상 숫자 반환) ② `connect()` 성공 경로 `startPromise=null` 리셋 ③ `onclose` 안전망(2s 후 `connect()` 재기동). 헤드(1-120)·테일(229-317) 바이트 동일.

### 2. 빌드/테스트 — PASS
- `dotnet test backend/Wcs.sln` fresh 1회 → **169/169 GREEN·exit 0**(15s).
- `npx tsc --noEmit` 0 · `npx eslint .` 0 · `npm run build` OK — **신규 번들 해시 `index-eFo6i05T.js`**(1차 `index-CE2Ggv6x.js`와 상이 → 검증 대상 SPA에 fix 포함 확증).

### 3. M2 핵심 재현 (직접·라이브) — PASS
Sim3ds(:1502)+API(:5080 Production·Tcp)+fix 포함 빌드 SPA. Playwright로 /sorters 열어둔 채(`framenavigated` 감시로 리로드 계수):
```
t+2s  CONNECTED badge=실시간 연결됨
t+2s  API kill — 65s 유지 (구 기본 정책 소진점 ~42s 초과)
      outage+15s/+42s/+55s/+65s badge=재연결 중… (4회 샘플 전부 — 포기 없음)
t+69s API 재기동 → t+73s up
t+87s badge=실시간 연결됨 · navDelta=0 (framenavigated 0회 = 새로고침 0)
      bootstrapRecovered=true (WordPanel 값 렌더·'스냅샷 대기 중' 없음) · pageErrors=0
=== M2 RESULT === verdict: PASS
```
- 구 정책이면 42s 시점에 재시도 소진→영구 disconnected였을 창(+42s/+55s/+65s)에서 **계속 재연결 중** — 무한 백오프 실증.
- **총 다운타임 65s(>60s)** 후 **새로고침 없이** 자체 재접속 + 부트스트랩 복구(서버 OnConnectedAsync Bootstrap 재전송 경로). Generator의 framenavigated 입증과 동등 수준 재현.
- 스크린샷: `screenshots/S-FRONTEND-F2_20260706-130457/m2-01-connected.png · m2-02-down65s.png · m2-03-recovered.png · console-m2.log`(m2-03 판독 — 배지 연결됨·워드 패널 복구 육안 확인).

### 4. 정리·클린 복원 — PASS
- 실 DB baseline 재확인(sqlcmd): piece=0·piece_event=0·sorter_command=0·active_assign=16/16·Reserved/Sorted=0·**oplog=20/maxid=174·alarm=10/maxid=16**(M2 세션 STATE 행 삭제).
- 포트 5080/1502/5173 free·오펀 0. 검증 스크립트·eval-tmp·logs 삭제.
- **package 파일 무접촉(1차 근접실패 교훈 적용)**: playwright를 `npm i --no-save`로 설치 → `npm prune`으로 제거. `git diff --stat -- package*` = Generator의 `@microsoft/signalr` diff(+1/+176)만 유지. git status = 핸드오프 동일(25 M + 10 untracked).

**결론: M1(주석)·M2(영구 재연결) fix 검증 전항 PASS — APPROVED 유지.** MINOR 5건은 기존 이연 그대로.

## Step 4.5 독립 코드리뷰 — BLOCKING 0 / MAJOR 2(fix-only iter로 해소) / MINOR 5(이연)

**MAJOR(해소됨)**: M2 영구 재연결 부재(자동 재연결 ~42s 소진 후 포기·startPromise 이중 차단) → 무한 상한 백오프+성공 리셋+onclose 안전망으로 수정, 65s+ 다운 자동 복구 라이브 입증(Eval·Gen 각각). M1 relay 주석 오해 소지 → opLog 한정 해제·PLC 훅 host-lifetime convention 명문화. 리뷰어 fix 재확인: 이중 트리거 경합 없음·신규 결함 0.

### MINOR (이연 — F3/후속에서 해당 영역 작업 시)
- **[F2-CR-m1]** OpLogTail 백로그(실 Id)↔라이브(Id null) 중복 dedup 없음 — Id 기준 dedup 권고.
- **[F2-CR-m2]** SorterWordDto 투영이 Hub·Relay 2곳 복제 — 공용 매퍼 추출.
- **[F2-CR-m3]** 하트비트마다 무변화에도 전체 commit(전 타일 재조정) — 동일 스냅샷 스킵 고려.
- **[F2-CR-m4]** Heartbeat·SorterTransition push·재연결 Bootstrap 재전송 자동 테스트 미커버.
- **[F2-CR-m5]** @microsoft/signalr v8 ↔ 서버 net10 버전 스프레드(와이어 호환·동작 무해) — 정렬 고려.
- **정보성(F3 필수 인계)**: 허브 완전 무인증 — oplog 스트림(barcode/pId/detail)이 동일 출처 전 클라이언트에 노출. F3에서 정책 재확인(사용자 확정=인증 없음·내부망 전제). OnEntry "논블로킹 계약" 문서화 권고.
