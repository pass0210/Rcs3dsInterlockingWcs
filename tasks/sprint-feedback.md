# Sprint Feedback — F1 (프론트엔드 스캐폴드 + 정적 서빙 + 모니터링 읽기 표면) — APPROVED

## Phase 3 Evaluate (Evaluator fresh evidence, branch `feat/frontend-f1`, 2026-07-03)

**최종 판정: APPROVED** — Completion C1~C8 + 무변경 가드 + 검증 시나리오(Slot 1/2/3) 전부 PASS.
Generator 요약을 신뢰하지 않고 모든 증거를 fresh 직접 재실행·관찰. 환경: node v22.17.0 · npm 11.4.2 · .NET SDK 10.0.301 · SQL Server(localhost, 실 DB `Rcs3dsInterlockingWcs` 16셀 field 데이터) · Playwright(standalone chromium, scratchpad 격리 — frontend devDeps 무오염).

> **포트 source-of-truth 주해**: `.claude/ports.local.json` 부재. 단 본 프로젝트는 dev-server 지연 할당이 아니라 committed `appsettings.json`의 `"Urls":"http://0.0.0.0:5080"`로 포트를 **고정**한다(결정적 committed config = source of truth, 추측 아님). 따라서 :5080 사용은 port-policy 위반이 아님. dev proxy 타깃도 vite.config.ts에 명시(:5080).

### Ground-truth (직접 읽음·실행)
- 브랜치 `feat/frontend-f1` 확인. `git status`: tracked 변경 = `.gitignore`(+6)·`src/Wcs.Api/Program.cs`(+20/-1)·`tasks/sprint-contract.md`(Planner)·`tasks/sprint-log.md`(Generator). untracked = `.claude/`·`.mcp.json`·`frontend/`·`src/Wcs.Api/Controllers/MonitoringController.cs`·`src/Wcs.Api/Monitoring/`·`tests/Wcs.Tests/MonitoringApiTests.cs`·`tasks/sprint-skip.txt`(stale·아래 Minor).
- sprint-log.md `## IMPLEMENTATION COMPLETE (F1)`(L1858) 마커 + 실 diff 존재 확인(거짓 핸드오프 아님). 계약 §4 엔드포인트 7개·§5 가중치·Completion C1~C8·검증 시나리오(Full-stack 3 슬롯) 직접 정독. Program.cs 정적 서빙 diff 코드 직접 확인.

---

### C1 (스캐폴드 + 빌드 체인) — PASS
- `npm install` → `added 70 packages ... found 0 vulnerabilities` (exit 0).
- `npx tsc --noEmit` → exit 0 (0 에러).
- `npm run lint`(eslint flat config) → exit 0 (0 에러·0 경고).
- `npm run build`(tsc --noEmit && vite build) → exit 0. `1679 modules transformed`. 산출:
  `../src/Wcs.Api/wwwroot/index.html`(0.46kB) · `assets/index-*.css`(21.46kB) · `assets/index-*.js`(391.43kB). `find src/Wcs.Api/wwwroot`로 3파일 물리 확인. **수동 복사 0**(vite `build.outDir=../src/Wcs.Api/wwwroot`).
- 스택 확인(package.json): Vite6·React19·react-router-dom7·@tanstack/react-query5·@tanstack/react-table8·tailwindcss4(@tailwindcss/vite)·radix tabs·lucide. SignalR 미설치(F2). ✔ 계약 §2 IN-1.

### C6 (회귀 0, base=SqlServer) — PASS (신규 15 결정적 · 잔여는 사전존재 flake·非-F1)
- base `appsettings.json` `"Provider":"SqlServer"` 그대로 `dotnet test Wcs.sln`:
  - `통과! - 실패:0, 통과:161, 건너뜀:0, 전체:161` (146 기존 + 15 신규). exit 0.
- **결정성 fresh 반복(S-E2E 교훈 준수)**: full-suite 총 **~21회** 재실행.
  - RUN 결과: **20회 GREEN(161/161)** + **1회 160/161(단일 테스트 1건 실패)**. flake rate ≈ 5% (1/21).
  - 신규 테스트 결정성 격리 입증 — `dotnet test --filter "FullyQualifiedName~MonitoringApiTests"` **20회 연속 전부 15/15 GREEN (PASS=20 FAIL=0)**. 신규 MonitoringApiTests는 완전 결정적(인스턴스 고유 GUID DB 격리 유효).
- **flake 귀속 판정 = 사전존재·非-F1 (BLOCKING 아님)**:
  - ① 신규 15 테스트는 격리 20/20 GREEN → 이 flake의 소스 아님(구조상 인스턴스별 `MonTest_{Guid}` DB라 교차오염 불가).
  - ② F1 변경(정적 파일 미들웨어 + `/api/{**rest}` NotFound + MapFallbackToFile + 읽기 전용 컨트롤러)은 PLC/Sim/핸드셰이크 **타이밍 경로를 전혀 건드리지 않음** → flake 유발 메커니즘(xUnit 기본 병렬 + 실 Sim/Modbus 타이밍 경합) 증가 0.
  - ③ 이 flake는 memory/lessons에 이미 등재된 **사전존재 known flake 계열**(`s9-flake-under-e2e-load`·`e2e-parallel-load-surfaces-integration-flakes`(IT4b)·`single-sorter-concurrent-handshake-gap`). run 로그에 사전존재 teardown 경합 `System.ObjectDisposedException @ OperationLogService.FlushBatchAsync`(OUT/무변경 파일) 관측 — 동류 teardown 노이즈.
  - ⚠ 정직 보고: 실패한 단일 테스트의 **정확한 이름은 캡처 못 함**(관측된 1회 실패가 요약만 남고 이름 미로그). 그러나 위 3근거(신규 격리 20/20 GREEN + F1의 타이밍-경로 무접촉 + 기 등재 flake 계열)로 **F1 비귀속**이 결정적. team-lead에 후속 todo(사전존재 flake 이름 확정·직렬화)로 이관 권고 — 이미 memory에 tracked.
- NU1903(SQLitePCLRaw.lib.e_sqlite3 2.1.10 transitive) 경고는 **사전존재 의존성 audit**(S-M5-P1 기록) — 코드 경고 아님·회귀 아님.

### C2 (정적 서빙 단일 서버 + fallback 비삼킴) — PASS
- Production + base=SqlServer + RTU(COM1 OFFLINE 전이 — 실 HW 부재·예상, 모니터링은 DB 기반이라 무관) `dotnet run --project src/Wcs.Api` → :5080 listen.
- `GET /` → **200 text/html**(SPA index.html). `GET /monitor`(SPA 딥링크) → **200 text/html**(fallback→index.html). ✔ C2 딥링크.
- **fallback 음성 대조(함정 #1·핵심)**:
  - `GET /api/monitor/does-not-exist` → **404**, content-type **빈값**(text/html 아님·body에 `<html` 없음).
  - `GET /api/v1/does-not-exist` → **404**, content-type 빈값.
  - → `app.Map("/api/{**rest}", ()=>Results.NotFound())`가 fallback보다 우선해 `/api/**` 미매치를 404로 확정. index.html 미삼킴 실 요청 입증.

### C3 (모니터링 API E1~E7 실 16셀 데이터) — PASS
- E1 `/batches` → `[{id:1,workDate:2026-07-01,batchNo:"FIELD-16",waveNo:1,status:"RUNNING",...}]`.
- E2 `/orders` → 16 오더 `0701-CELL-01..16` GENERAL·RUNNING·destinationChuteNo=1·planned=3, **Id 내림차순**(최신순) + order_item 합계 집계 정확.
- E3 `/orders/16/items` → `[{id:16,barcode:"0701-CELL-16",plannedQty:3,reservedQty:0,sortedQty:0}]`.
- E4 `/pieces/in-flight` → in-flight piece(status∈QUERIED/RESERVED/PERMITTED) 반환 + `nextCursor`. take clamp: `take=99999` → 200·항목≤TakeMax(무한로드 아님).
- E5 `/sorters` → `[{destId:1,chuteNo:1,online:false,ready:false,full:false,paused:false}]`(DestinationStatusService 산출·OFFLINE 정합).
- E6 `/sorters/1/cells` → **16셀** `{cellNo,capacity:3,currentQty:0,occupied:true,enabled:true,assignedOrderNo:"0701-CELL-NN"}`(SorterCellQty 재사용 산출). 미존재 destId 999 → **200 `[]`**(일관 정책·500 아님).
- E7 `/sorter-commands?destId=1` → `{items:[],nextCursor:null}`(적재 이력 없음·빈 페이지 정상).
- **IF-05 회귀(RcsController 무변경 입증)**: `POST /api/v1/destination-query` `0701-CELL-02` → `{"result":"OK","chuteNo":1}`; 미존재 `NO-SUCH-BC` → `{"result":"NG","chuteNo":null}`; 범위 밖 pId → 검증 400. 정상 형상 불변.

### C4 (브라우저 표시 — Playwright fresh) — PASS
- standalone Playwright chromium(scratchpad)로 :5080 검증. 스크린샷 보존:
  `screenshots/F1_20260703-115749/` (01-load·02-tabA-orders·03-tabA-expanded·04-tabB-inflight·05-tabC-cells·06-final-tabA + console.log + network-monitor.log).
- **console.log = 완전 공백 → 콘솔 에러 0·pageerror 0·React dev-mode warning 0** (BLOCKING 기준 PASS).
- 스크린샷 육안 판독(READ):
  - **좌측 내비**: WCS 관제/3DS INTERLOCKING · 모니터링(활성) · 3DS 워드[F2 배지·비활성] · 운영 제어[F3 배지·비활성](고아 링크 아님·F2/F3 예고) · 하단 "폴링 3s"+시계.
  - **상단 상태바(StatusRail·시그니처)**: 소터 타일 `3DS #01 RDY FULL PAUSE`(OFFLINE 로즈 틴트).
  - **탭 A**: 배치 select `2026-07-01 · FIELD-16 (W1) — RUNNING` + 상태 필터 + 오더 16행(0701-CELL-01..16, GENERAL·RUNNING·슈트1·진행바). 행 확장 → **오더아이템 (1)** 서브테이블(`0701-CELL-16` 계획3/예약0/분류0) 렌더(바코드 `0701-CELL-*` 실 데이터 확인).
  - **탭 B**: in-flight 테이블(PID·바코드·수량·슈트·AGV·인덕션·상태·등록) — 검증 시점 2 in-flight piece 렌더 + 커서 페이저.
  - **탭 C**: 소터 select + 셀 현황 **16 타일 그리드**(점유 배지·0/3 용량 게이지·assignedOrderNo) + 범례 + 적재 이력 카드 **빈-상태**("적재 이력이 없습니다") + 페이저.
- **폴링 동작**: monitor 재요청 before=11 → 3.5s 후 14 (delta=3) — TanStack Query 3s 폴링 실제 재요청 관측.

### C5 (통합 테스트) — PASS
- `MonitoringApiTests` 15건: E1~E7 형상·집계·상태필터·E4/E7 키셋 커서 페이징·take clamp·잘못된 커서 400·미존재 id 빈배열·fallback 404 음성대조·ClampTake 단위. 전용 `MonitoringWebApplicationFactory`(UseSetting Provider=Sqlite + 인스턴스 고유 in-memory SQLite + EnsureCreated + DbSeeder.Seed). 재실행 15/15 GREEN × 20회.

### C7 (무변경 가드) — PASS
- OUT 파일 `git diff` **0줄**: `RcsController.cs`·`src/Wcs.PlcGateway/`·`src/Wcs.Core/`·`src/Wcs.Sim3ds/`·`src/Wcs.Data/`·`Wcs.Migrations.{Sqlite,SqlServer}`·`appsettings.json`·`appsettings.Development.json`·기존 테스트(`ApiIntegrationTests`·`RcsPushTests`·`ScenarioTests`) 전부 0. (DbSeeder·WcsDbContext·Entities는 Wcs.Data diff 0에 포함.)
- `Program.cs` diff = **+20/-1, 정적 서빙 삽입에 한정**(UseStaticFiles→MapControllers(기존)→`Map("/api/{**rest}",NotFound)`→MapFallbackToFile). DI 배선 무변경(MonitoringController가 기 등록 WcsDbContext+싱글톤 주입받아 요청당 조립 — IMonitoringQueries를 Program에 등록 안 함).
- `.sln`에 frontend 미등록(`grep -c frontend Wcs.sln = 0`). `.gitignore` +node_modules·dist·wwwroot.

### C8 (dev 워크플로 + 도구) — PASS
- `npm run dev` → Vite :5173 ready(826ms). `curl :5173/api/monitor/sorters` → :5080 프록시로 동일 JSON(destId1·chuteNo1). `:5173/api/monitor/sorters/1/cells` → 16셀. vite proxy `/api`→:5080 동작 관측.
- `.mcp.json` 신설(Playwright MCP) 확인.

---

### 검증 시나리오 (Full-stack — 계약 §Verification Scenarios)
- **Slot 1 (Web/UI)**: 기본 상태(내비+상태바+A/B/C) 렌더 / 대체 상태(배치선택·행확장→아이템·페이징·필터·로딩→데이터 전환) / empty·error 상태(적재이력 빈-상태·소터 OFFLINE 표기) — Playwright click-through로 확인(다크모드 N/A 명시 준수). 핵심 흐름(로드→내비→배치→오더→행확장→아이템→3종 폴링 갱신) 재현.
- **Slot 2 (Backend/API)**: E1~E7 해피패스 형상 + 에러케이스(미존재 id 200[]·take clamp·잘못된 커서 400·fallback 404 음성대조) 실 HTTP 왕복 입증(C3).
- **Slot 3 (E2E cross-layer)**: `npm run build`→wwwroot 배치→`dotnet run`(:5080 단일 서버)→브라우저가 :5080 SPA 로드(같은 출처)→`/api/monitor/*` 호출→실 DB 16셀 데이터가 A/B/C에 렌더. dev 경로 대체(:5173 proxy→:5080)도 관통 확인.

---

### 계약 §5 가중치 판정
- **★★★ Integration Quality(30%)**: 단일 :5080에서 SPA+`/api/monitor/*` 공존·같은 출처·카멜케이스 JSON↔프론트 타입 1:1(api.ts 미러). fallback 비삼킴 음성대조 입증. **PASS**.
- **★★★ Per-layer(25%)**: 프론트=블루프린트 그래파이트 SCADA 계기판 테마(시맨틱 색토큰·모노스페이스 판독값·상태 램프·용량 게이지·reduced-motion 존중), 밀집 운영툴 룩·의도된 디자인(AI-slop 아님)·좌측내비+상단상태바로 앱 연결. 백엔드=`/api/monitor/*` 일관 RESTful·읽기 전용·AsNoTracking·키셋 커서·take clamp·기존 리포지토리 무변경(신규 IMonitoringQueries). **PASS**.
- **★★ Craft(20%)**: 회귀 0(신규 결정적)·무변경 가드·tsc/lint/console 0에러·범위 강제(A-3: take clamp+상태필터+키셋 정렬로 풀스캔 방어, 코드+응답 입증). **PASS**.
- **★★ Functionality(25%)**: 16셀 실 데이터가 브라우저 A/B/C 렌더·폴링 갱신·필터/페이징/행확장 동작·dev 워크플로. **PASS**.

### Generator FINDING 검토 — 확인됨(유효)
- `MonitoringWebApplicationFactory`가 기존 `FakeModbusWebApplicationFactory._dbName`(static readonly·단일 공유 DB)을 재사용하지 않고 **인스턴스 고유 `MonTest_{Guid}` DB**를 씀 → 병렬/반복 생성 교차오염 회피. 코드 확인(L448 인스턴스 필드) + 격리 20/20 GREEN이 실증. 공개 헬퍼(FakeModbusMasterForApi·FakeSorterGatewayRegistry·NopSorterRegistryFactory)만 재사용(기존 파일 무변경). todo 후보(그 static을 인스턴스 필드로 승격) 타당.

### 정리(산물 0 잔존)
- dotnet(:5080)·vite(:5173) 프로세스 종료(Stop-Process) — 포트 LISTEN 0(TIME_WAIT만·자동 소멸).
- **DB 복원(QUOTED_IDENTIFIER ON 트랜잭션)**: 검증 중 발견된 산물 정리 → `piece=0·piece_event=0·sorter_command=0·order_item.ReservedQty 합=0·cell=16·active cell_assignment=16`(S-FIELD-SEED 문서화 클린 baseline 복원). ⚠ 이 중 piece Id22(PId12345·RESERVED·`created 02:41`)는 **Generator의 라이브 IF-05 검증 잔존물**("정리 완료" 보고와 불일치·아래 Minor); Id23(PId99)·Id24(PId98)는 Evaluator IF-05 회귀 검증 산물. 전부 제거해 baseline 복원.
- 스크린샷 보존(경로: 위 C4). `git status`가 핸드오프와 동일(+ `screenshots/` = 보존 증거).

### Minor (비차단 — 후속·다음 sprint Generator 참고)
- **[F1-M1]** 사전존재 full-suite flake(~5%·非-F1·S9/IT4b/concurrent-handshake 계열) — 실패 테스트 이름 확정 + xUnit 병렬 직렬화/collection 격리를 별도 스프린트로. (이미 memory tracked.)
- **[F1-M2]** `tasks/sprint-skip.txt`가 이전 docs/frontend-design 스프린트의 stale 잔존물(mtime 10:41·untracked)로 F1 full-contract와 공존. 혼선 방지 위해 제거 권고(pre-commit 10분 freshness상 이미 무효라 bypass는 안 됨).
- **[F1-M3]** Generator 라이브 IF-05 검증 잔존 piece(PId12345)가 field DB에 남아 있었음("정리 완료" 보고와 불일치) — Evaluator가 복원. 라이브 검증 후 DB 클린 확인 습관화.
- **[F1-M4]** JS 번들 391kB(gzip 121kB) — F1 단일 페이지엔 무해하나 F2+ 성장 대비 route-level code-splitting 여지.

---
**APPROVED** — Completion C1~C8 + 무변경 가드 + 검증 시나리오(Slot1/2/3) 전부 fresh evidence로 PASS. 회귀 0(신규 15 결정적·잔여 flake는 非-F1 사전존재). Minor 4건 후속.

## Step 4.5 독립 코드리뷰 (orchestrator, opus, 팀 외부) — BLOCKING 0 / MAJOR 1 / MINOR 5

독립 리뷰어가 Evaluator 미커버 영역(아키텍처·보안·유지보수·성능) 검토 → **BLOCKING 0·머지 가능**. 보안 클린(EF 파라미터화·XSS 벡터 0·경로 탈출 방어·시크릿 0), N+1 없음, A-3 풀스캔 방어 실효, `/api/{**rest}` catch-all vs F2 `/hubs` 충돌 없음(검증), 주석 품질 우수, 테스트 팩토리 격리 설계 정확.

### MAJOR (비차단 — **F2 착수 시 최우선 해소**, F2 Generator 필독)
- **[F1-CR-M1]** `MonitoringController.cs:34`가 `IMonitoringQueries`를 DI 등록 없이 요청마다 `new MonitoringQueries(...)` 손조립 — 대체(모킹·데코레이션) 불가능한 "추상화하지 않는 추상화". 원인 = 계약 C7(Program.cs 정적서빙 한정 무변경 가드)의 부산물. **F2에서 `AddScoped<IMonitoringQueries, MonitoringQueries>()` 등록 + 생성자 주입으로 전환**(F2는 SignalR가 같은 조회를 push하므로 어차피 필요). 인터페이스 제거(정직한 concrete 의존)도 대안.

### MINOR (후속 — F2/F3에서 해당 영역 작업 시)
- **[F1-CR-m1]** E5 GetSorters가 소터당 DI 스코프+집계 쿼리(3s 폴 최중량 경로) — 소터 수 증가 시 배치화 검토. 현 1~2대 무해.
- **[F1-CR-m2]** 프론트↔백 DTO 수동 미러(런타임 검증 0) — 백엔드 필드 개명 시 UI에 undefined 조용히 누설. F2/F3 확장 시 OpenAPI codegen 또는 zod 경계 검증.
- **[F1-CR-m3]** 클라이언트 take 기본값 매직넘버(50/100 인라인) — 공유 상수화.
- **[F1-CR-m4]** DataGrid meta.align 반복 타입 캐스트 — TanStack Table ColumnMeta 모듈 augmentation 1회 선언으로. InFlight 숫자열 정렬 불일치(경미).
- **[F1-CR-m5]** E4_InFlight_LargeTake 테스트가 빈 데이터라 자명 참(clamp 산식은 ClampTake 단위가 입증) — 데이터 삽입 또는 개명.

