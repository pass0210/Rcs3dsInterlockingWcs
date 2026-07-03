# Sprint Contract — F1 (프론트엔드 스캐폴드 + 정적 서빙 + 모니터링 읽기 표면)

> 작성: Planner Subagent · 2026-07-03 · 브랜치 `feat/frontend-f1`
> 본 계약은 **WHAT / WHERE / 검증(Acceptance)** 만 규정한다. **HOW(Vite/shadcn 부트스트랩 방식·컴포넌트 트리·TanStack Query 훅 구성·IMonitoringQueries 쿼리 구현·커서 인코딩·정적 서빙 미들웨어 배선 세부·wwwroot 산출 배치 메커니즘)는 Generator가 결정**한다.
> 3-Tier: Planner(이 문서) → 사용자 확인 → Generator ↔ Evaluator 루프.
> 근거 설계: **`docs/FRONTEND.md` §6 F1 그대로**(재설계 금지 — 계약으로 구체화만). ★확정 결정 6건(shadcn/ui+Tailwind+TanStack Table·인증 없음·안전 3종·frontend 루트+정적 서빙 등)을 전제. develop = PR #25 병합(FRONTEND.md 확정 설계 포함).

---

## 0. 배경 / 전제 (확정 사실 — 추측 아님, 코드/환경 직접 확인됨)

- **툴체인 존재 확인 완료**: `node v22.17.0` + `npm 11.4.2` 설치됨(이 세션에서 실행 확인). `.NET SDK 10.0.301`(net10.0 타깃 정상). → Node 미설치 리스크는 해소됐으나, Generator는 스캐폴드 착수 직전 `node --version`으로 재확인한다(글로벌 규칙).
- **Program.cs 현재 상태**(정적 서빙 미들웨어 없음): `await DbInitializer.ProvisionAsync(app);` → (관측 훅 구독 블록) → `app.MapControllers();` → `app.Run();`. **정적 서빙·SPA fallback 미들웨어가 전혀 없다.** `Program.cs`는 top-level statements + 클래스 선언(`SorterRegistryFactory` 등)이 `app.Run()` 뒤에 이어지는 단일 파일이다. 삽입은 `app.Run()` 앞 구간에서만 한다.
- **컨트롤러 패턴**(`src/Wcs.Api/Controllers/RcsController.cs`): `[ApiController]` + `[Route("api/v1")]`, `sealed class : ControllerBase`, 생성자 주입(`ILogger<T>`·`IOperationLogger`), 핸들러 파라미터에 `[FromServices]` 주입, `[FromBody]` 요청 레코드. **신규 `MonitoringController`는 이 패턴을 따르되 라우트는 `api/monitor`로 분리**한다(RcsController 무변경).
- **재사용 산출원(신규 구현 금지 — 기존 것 호출)**:
  - `IDestinationStatusService.Compute(long destinationId, DestType destType)` → `DestinationReadiness(Ready, Full, Paused, Online, Reason)`. 싱글톤 등록됨(`src/Wcs.Api/Services/DestinationStatusService.cs`). 소터 readiness(online/full/paused/ready) 산출에 그대로 사용.
  - `SorterCellQty.LoadedQtyByCell(db, destId, cellIds)` + `IsCellAtCapacity(capacity, current)` — `internal static`(`src/Wcs.Api/Services/SorterCellQty.cs`, `namespace Wcs.Api`). 셀 현재 투입 수량 산출에 재사용(byte-consistent). MonitoringController도 `Wcs.Api`이므로 접근 가능.
  - `ISorterGatewayRegistry.AllBundles` / `GetBundle(destId)` / `.Latest`(Online 포함) — 소터 목록·온라인 상태 원천.
- **엔티티(조회 대상)**: `WorkBatch`·`WcsOrder`·`OrderItem`·`Piece`·`Cell`·`CellAssignment`·`SorterCommand`(`src/Wcs.Data/Entities.cs`). `WcsDbContext`에 17 DbSet. 오더 진행 집계(planned/reserved/sorted)는 `OrderItem.PlannedQty/ReservedQty/SortedQty` 합.
- **테스트 더블 패턴(회귀 게이트 핵심)**: 통합 테스트는 `WebApplicationFactory<Program>` + **`builder.UseSetting("Database:Provider","Sqlite")`**(Program이 즉시 평가하는 `Database:Provider`를 호스트 세팅으로 override — `ConfigureAppConfiguration`/디스크립터 제거는 무효, lessons.md 2026-06-30) + named in-memory SQLite anchor + `EnsureCreated()` + `DbSeeder.Seed(...)`. base appsettings는 `Database:Provider=SqlServer`. **현재 146 GREEN**. 신규 MonitoringController 통합 테스트도 이 패턴을 그대로 재사용한다.
- **16셀 시드 데이터**: `DbSeeder.Seed` + `scripts/seed-field-16cells.sql`로 소터(chuteNo=1)·셀 16·오더 16·order_item 16·active cell_assignment 16이 존재(S-FIELD-SEED-16CELLS). Evaluator의 브라우저 fresh evidence는 이 실 데이터를 활용할 수 있다.
- **절대규칙 무관 확인**: F1은 **읽기 전용**이라 PLC 쓰기(#1·#2·#3)·핸드셰이크·판정(#8)에 손대지 않는다. 정적 서빙·읽기 조회만 추가. #7(설정 외부화)은 dev proxy 타깃·폴 주기 등 신규 상수를 하드코딩하지 않는 선에서 준수(프론트 폴 주기는 코드 상수 허용 — 백엔드 appsettings 대상 아님, 단 dev proxy 타깃 URL은 vite config에 명시).

---

## 1. Goal (목표)

**(a) 프론트엔드 스캐폴드 확립**: 리포 루트 `frontend/`에 Vite + React + TypeScript SPA 스캐폴드(React Router · TanStack Query · **TanStack Table** · **shadcn/ui + Tailwind CSS**). `vite.config.ts` dev proxy(`/api` → `http://localhost:5080`). Node 산출물(`node_modules`·`dist`)과 `wwwroot`(빌드 산출)를 `.gitignore`에 등재.

**(b) Wcs.Api 단일 서비스 정적 서빙**: `UseStaticFiles()` + SPA fallback(`MapFallbackToFile("index.html")`)을 Program.cs에 삽입해, `npm run build` 산출물을 `src/Wcs.Api/wwwroot`에서 서빙. **API 라우트(`/api/**`)가 우선**하고 fallback이 `/api`를 삼키지 않는다. Windows Service ContentRoot(=`AppContext.BaseDirectory`)에서도 정상 서빙.

**(c) 읽기 전용 모니터링 API 표면**: 신규 `MonitoringController`(`/api/monitor/*`) + 신규 `IMonitoringQueries`(EF `AsNoTracking`, `Wcs.Api`) — **기존 `DbRepositories`·리포지토리 무변경**. F1 페이지가 소비하는 엔드포인트만(§4) 구현. piece·sorter_command 등 대량 테이블은 커서/키셋 페이징 + `take` 상한.

**(d) 모니터링 페이지 ①(폴링)**: FRONTEND.md §5 페이지 ① — **A 작업 데이터**(배치→오더→오더아이템) / **B 로봇 이동중**(in-flight piece) / **C 분류 데이터**(소터별 셀 현황 + sorter_command 이력). TanStack Query **폴링만**(SignalR·실시간 push는 F2). 좌측 내비 + 상단 상태바(소터 Online/Offline)로 페이지가 앱에 연결(고아 페이지 금지).

**(e) Playwright MCP 설정**: `.mcp.json` 신설(Evaluator 브라우저 검증용).

**불변 (동작 보존)**: 기존 146 테스트 무변경 GREEN. RcsController·PlcGateway·Core·기존 DbRepositories·마이그레이션·DbSeeder **0줄 변경**(정적 서빙 위한 Program.cs 최소 삽입 + 신규 파일만). 읽기 조회가 기존 상태·이력을 변경하지 않음(부수효과 0).

---

## 2. Scope — IN / OUT (파일·모듈 구체)

### IN (이번 스프린트가 만드는/만지는 것)

**A. 프론트 스캐폴드 (`frontend/` — 신규, .NET 프로젝트 아님·.sln 무등록)**
1. `frontend/` Vite + React + TS 프로젝트: `package.json`·`vite.config.ts`(dev proxy `/api`→`:5080`)·`tsconfig.json`·`index.html`·`src/`(엔트리·라우터·페이지·API client·shadcn 셋업). 의존: `react` `react-dom` `react-router-dom` `@tanstack/react-query` `@tanstack/react-table` + `tailwindcss`(+shadcn/ui 컴포넌트, radix 프리미티브 수반). 빌드: `vite` `typescript` `@vitejs/plugin-react`. (SignalR·@microsoft/signalr는 F2 — 이번엔 미설치 권장, 설치해도 미사용.)
2. 모니터링 페이지 ①(§5 A/B/C) + 전역 레이아웃(좌측 내비 + 상단 상태바). 데이터 표시는 TanStack Query 폴링. 필터(배치·상태)·행 확장(오더→아이템)·페이징(in-flight/sorter-command)을 TanStack Table로.
3. 프론트 lint/타입체크 스크립트(`tsc --noEmit` + eslint 또는 동등 — Generator 선택). 프론트 단위/컴포넌트 테스트(Vitest)는 **선택**(권장이나 F1 필수 아님 — 필수 검증은 통합 테스트 + Playwright).

**B. 빌드 체인 / 정적 서빙**
4. `npm run build` 산출물이 `src/Wcs.Api/wwwroot`에 배치되어 Wcs.Api 단일 서버가 SPA+API를 함께 제공한다. **복사 자동화 여부(Q6) = 자동(수동 복사 단계 없음)으로 확정** — 단일 문서화된 명령(예: vite `build.outDir`를 wwwroot로 지정 또는 build script)으로 산출. 정확한 메커니즘은 Generator(MSBuild가 npm을 구동하는 결합은 금지 — FRONTEND.md 각하안).
5. `src/Wcs.Api/Program.cs` — `app.Run()` 앞에 정적 서빙 삽입: `UseStaticFiles()` → `MapControllers()`(기존) → `MapFallbackToFile("index.html")`. **fallback이 `/api/**`를 가로채지 않게** 한다(API 우선). ContentRoot/wwwroot 기준 해석 확인.
6. `.gitignore` — `frontend/node_modules/`·`frontend/dist/`·`src/Wcs.Api/wwwroot/` 추가(현재 미등재). `frontend/`의 소스만 커밋.

**C. 읽기 전용 API 표면 (`src/Wcs.Api` — 신규 파일)**
7. `MonitoringController`(`[Route("api/monitor")]`, 읽기 전용, RcsController 패턴) — §4 F1 엔드포인트.
8. `IMonitoringQueries` + EF 구현(`Wcs.Api`, `AsNoTracking`) — 기존 `DbRepositories` 무변경·신규 인터페이스. `IDestinationStatusService`·`SorterCellQty` 재사용. 커서/키셋 페이징 + `take` 상한(설정 상수 or 하드 상한 — Generator, 단 명시).
9. 신규 통합 테스트(`tests/Wcs.Tests/` — 기존 파일 무변경, 신규 파일 추가): MonitoringController 조회 형상·페이징·에러케이스. 기존 `WebApplicationFactory` + `UseSetting("Database:Provider","Sqlite")` + in-memory SQLite 더블 패턴 재사용.

**D. 도구 설정**
10. `.mcp.json`(리포 루트) — Playwright MCP: `{"mcpServers":{"playwright":{"command":"cmd","args":["/c","npx","@playwright/mcp@latest","--headless"],"disabled":false}}}`.

### OUT (이번 스프린트가 절대 건드리지 않는 것)
- **`src/Wcs.Api/Controllers/RcsController.cs`** (IF-05/09/10) — 0줄.
- **`src/Wcs.PlcGateway/**`·`src/Wcs.Core/**`·`src/Wcs.Sim3ds/**`** — 0줄(읽기만·PLC 무관).
- **기존 `DbRepositories`·`Ef*Repository`·`ICellSelector`·`IOrderRepository` 등 기존 리포지토리** — 0줄(신규 `IMonitoringQueries`만 추가).
- **마이그레이션(`Wcs.Migrations.*`)·`DbSeeder.cs`·`WcsDbContext` 매핑** — 0줄. **인덱스 추가 금지**(감사 묶음 C의 `order_item(Barcode)`·`piece(PId,IsActive)` 인덱스는 F1 스코프 **밖** — 명시 제외. F1 조회는 인덱스 없이도 성립하도록 §4의 `take` 상한·상태 필터·정렬로 범위 강제).
- **SignalR/실시간 push**(F2), **워드 쓰기·운영자 제어(clear/pause/resume)·OpsController·인증**(F3). F1은 폴링·읽기·인증 없음.
- **operation-log·alarms·destinations 엔드포인트** — F1 페이지 ①(A/B/C)가 쓰지 않으므로 **F1 제외**(operation-log 테일·알람 배지는 F2 페이지 ②/상태바 확장). FRONTEND.md §3.1 전체 표 중 F1은 §4의 7개만.
- **`src/Wcs.Api/appsettings*.json`** — 정적 서빙은 코드 미들웨어라 설정 변경 불요. (Serilog·DB·RcsPush 설정 0 변경.)
- **다크 모드 토글·테마 전환** — F1 미구현(단일 기본 테마). 후속 페이즈 여지.

---

## 3. Detected Project Type

**Full-stack** — 근거(프로젝트 신호, 사용자 표현 아님): 이 스프린트 후 같은 레포에 **브라우저 대면 진입점**(`frontend/index.html` + React 컴포넌트 트리, `src/Wcs.Api/wwwroot`로 서빙)과 **서버측 라우트/컨트롤러**(`MonitoringController` + 기존 `RcsController`, ASP.NET Core `MapControllers`/`app.Run()`)가 **공존**한다. F1이 브라우저 진입점을 신설하므로 프로젝트 타입이 기존 Backend/API에서 Full-stack으로 전이한다. 스택 경계: TypeScript 프론트 ↔ C# 백엔드 — 경계 검사(API 계약 형상 일치·직렬화 호환)를 검증 시나리오에 포함한다.

---

## 4. API 표면 명세 — F1 엔드포인트 (WHAT을 반환 — 정확한 타입·커서 인코딩은 Generator)

> 전부 `GET /api/monitor/*`, 읽기 전용, `AsNoTracking`. 반환 필드명은 카멜케이스 JSON. **piece·sorter_command는 커서/키셋 페이징 + `take` 상한 필수**(A-3 풀스캔 방어). 반환 형상은 아래 "의미"가 고정, 컬럼명 미세조정은 Generator 가능.

| # | 엔드포인트 | 반환(의미) | 원천 / 재사용 |
|---|---|---|---|
| E1 | `GET /api/monitor/batches` | work_batch 목록: id·workDate·batchNo·waveNo·status·openedAt·closedAt. 최신순 정렬 + take 상한 | `WorkBatch` |
| E2 | `GET /api/monitor/orders?batchId=&status=` | 오더 진행: id·orderNo·orderType·destinationChuteNo·status·plannedQty·reservedQty·sortedQty(order_item 합계) | `WcsOrder` + `OrderItem` 집계. take 상한 |
| E3 | `GET /api/monitor/orders/{id}/items` | 오더아이템: id·barcode·plannedQty·reservedQty·sortedQty | `OrderItem`(OrderId=id) |
| E4 | `GET /api/monitor/pieces/in-flight?take=&cursor=` | 이동중 piece: pId·barcode·qty·destinationChuteNo·agvNo·inductionNo·status·시각. 최신순 | `Piece`(IsActive && Status∈{QUERIED,RESERVED,PERMITTED}). **커서 페이징 + take 상한** |
| E5 | `GET /api/monitor/sorters` | 소터 목록 + readiness: destId·chuteNo·online·ready·full·paused | `ISorterGatewayRegistry.AllBundles` + `IDestinationStatusService.Compute(destId, SORTER_3D)` |
| E6 | `GET /api/monitor/sorters/{destId}/cells` | 셀 현황: cellNo·capacity·currentQty·occupied·enabled·assignedOrderNo? | `Cell`·`CellAssignment`(active) + `SorterCellQty.LoadedQtyByCell`(재사용) |
| E7 | `GET /api/monitor/sorter-commands?destId=&take=&cursor=` | 적재 이력: id·pId·barcode?·cellNo·cSeq·rSeq·status·cWrittenAt·rFlagAt. 최신순 | `SorterCommand` JOIN `Cell`(destId)·`Piece`. **커서 페이징 + take 상한** |

- **페이징 규칙**: E4·E7은 키셋 커서(`id` 또는 `at` 기준)로 페이징하고 `take`에 상한(예: ≤200 — Generator가 상수·명시). 상한 초과 요청은 상한으로 clamp(또는 400 — Generator 정책 명시). E1·E2는 필터 + take 상한으로 범위 강제.
- **레지스터 스냅샷(D0~D6 raw) 노출은 F1 제외** — E5는 identity·online·readiness만. 레지스터 패널은 F2 페이지 ②.
- **SqlServer 전용 SQL 금지**: 조회는 provider-agnostic LINQ(`AsNoTracking`)로만. raw SQL·SqlServer 고유 함수 금지(테스트는 in-memory SQLite 더블 — lessons: 읽기라 실 SqlServer 검증 불요하나 SQLite에서 깨지면 안 됨).

---

## 5. Evaluation Criteria (가중치) — Full-stack 통합 판정 (Evaluator fresh evidence 필수)

> 모든 PASS는 **"지금 실제로 돌렸다"는 fresh tool output**(HTTP 응답 본문·`dotnet test` raw line·`npm run build` 출력·Playwright 스크린샷 파일 경로·`console.log` 발췌)을 sprint-feedback.md에 인용. Generator success 보고·추정만으론 PASS 금지. URL은 `.claude/ports.local.json`에서 읽는다(하드코딩 금지).

### ★★★ Integration Quality (가중치 30%)
- **빌드 체인 단일 서버**: `npm run build` → `src/Wcs.Api/wwwroot` 배치 → `dotnet run --project src/Wcs.Api` → **`:5080` 단일 서버에서 SPA + `/api/monitor/*`가 함께 동작**(브라우저가 :5080에서 SPA 로드 + 같은 출처로 API 호출). API 계약 형상(카멜케이스 JSON)이 프론트 타입과 일치.
- **API 우선·fallback 비삼킴**: `/api/monitor/<존재하지 않는 경로>`가 index.html(HTML 200)로 떨어지지 않고 404 계열(또는 API 에러) — fallback이 `/api`를 가로채지 않음을 실 요청으로 입증(음성 대조).

### ★★★ Per-layer Quality (가중치 25%) — 프론트(Web/UI) + 백엔드(Backend/API) 각각
- **프론트(Web/UI)**: 모니터링 페이지 ①이 A/B/C 3종을 밀집 데이터 레이아웃으로 표시, 좌측 내비 + 상단 상태바로 앱에 연결(고아 페이지 아님). shadcn/ui + Tailwind 일관 사용. **디자인 품질·크래프트**(타이포·간격·대비 — `frontend-design` 스킬 참조). AI-slop 아닌 의도된 밀집 운영툴 룩.
- **백엔드(Backend/API)**: `/api/monitor/*` 일관 네이밍·RESTful·읽기 전용·`AsNoTracking`. 페이징 계약(커서·take 상한) 일관. 기존 리포지토리 무변경(신규 `IMonitoringQueries`).

### ★★ Craft (가중치 20%)
- **회귀 0**: 기존 146 GREEN 유지(base=SqlServer + 테스트 SQLite 더블). 정적 서빙 미들웨어·MonitoringController 추가가 기존 테스트를 깨지 않음(특히 test 호스트에 wwwroot/index.html 부재가 무해).
- **무변경 가드**: RcsController·PlcGateway·Core·기존 DbRepositories·마이그레이션·DbSeeder `git diff` 0줄. Program.cs 변경은 정적 서빙 삽입에 한정.
- **프론트 lint/tsc 0 에러**: `tsc --noEmit`(및 eslint if configured) 클린. 콘솔/pageerror 0(React dev warning·uncaught 없음).
- **범위 강제(A-3)**: in-flight piece·sorter-command 조회가 `take` 상한·상태 필터·정렬로 범위 강제(무한/풀스캔 아님) — 코드·응답으로 입증.

### ★★ Functionality (가중치 25%)
- **모니터링 3종이 실 DB 데이터로 브라우저에 표시**: 16셀 시드 데이터 활용 — 배치 선택 → 오더 표시 → 행 확장 → 오더아이템 / in-flight piece 목록 / 소터 셀 현황(16셀) + sorter_command 이력이 Playwright fresh evidence로 렌더 확인.
- **폴링 갱신**: TanStack Query 폴링으로 데이터가 주기 갱신(로딩/에러 상태 처리). 필터·페이징 상호작용 동작.
- **dev 워크플로**: `npm run dev`(Vite :5173) + proxy로 `/api`가 `:5080`으로 프록시돼 프론트/백 동시 기동 개발이 동작(문서화 + 관찰).

---

## 6. Completion Conditions (최소 통과 — 전부 충족해야 APPROVED)

- **C1 (스캐폴드)**: `frontend/`에 Vite+React+TS + React Router + TanStack Query + TanStack Table + shadcn/ui + Tailwind 스캐폴드 생성. `npm install` 성공 + `npm run build` exit 0(dist/wwwroot 산출). `tsc --noEmit`(및 lint) 0 에러.
- **C2 (정적 서빙 단일 서버)**: `npm run build` 후 `dotnet run --project src/Wcs.Api` 기동 → `:5080`(ports.local.json) 루트에서 SPA index.html 서빙 + SPA 라우트 딥링크가 fallback으로 index.html 반환. **`/api/monitor/*`는 정상 JSON**, `/api/monitor/<미존재>`는 index.html로 안 떨어짐(fallback 비삼킴 음성 대조).
- **C3 (모니터링 API)**: E1~E7 각 엔드포인트가 실 HTTP로 기대 형상 JSON 반환(16셀 시드 데이터 기준 비어있지 않음) + E4·E7 커서 페이징·take 상한 동작. E3(존재하는 오더 id)·E6(존재하는 destId) 정상, 미존재 id/destId는 빈 목록 또는 404(정책 일관).
- **C4 (브라우저 표시 — Playwright fresh)**: `:5080`에서 모니터링 페이지 ① 로드 → A(배치→오더→아이템 확장)·B(in-flight)·C(셀 16·sorter_command) 3종이 실 데이터로 렌더. 좌측 내비/상단 상태바로 페이지 도달(직접 URL 아님). 번호 스크린샷 + `console.log`(pageerror·React warning 0).
- **C5 (통합 테스트)**: MonitoringController 통합 테스트 추가(WebApplicationFactory + `UseSetting("Database:Provider","Sqlite")` + in-memory SQLite 더블) — 조회 형상·페이징·에러케이스 커버. 신규 테스트 GREEN.
- **C6 (회귀 0)**: `dotnet test` **기존 146 + 신규 = 전부 GREEN**·exit 0. base=SqlServer. 정적 서빙/컨트롤러 추가가 회귀 0. (동시성 민감 테스트 있으므로 **fresh ≥3회 반복**으로 결정성 확인 — S-E2E 교훈.)
- **C7 (무변경 가드)**: `git diff` — RcsController·`src/Wcs.PlcGateway/`·`src/Wcs.Core/`·`src/Wcs.Sim3ds/`·기존 DbRepositories·`Wcs.Migrations.*`·`DbSeeder.cs`·`WcsDbContext.cs`·`appsettings*.json` **0줄**. Program.cs 변경은 정적 서빙 삽입에 한정. `.sln`에 프론트 미등록.
- **C8 (dev 워크플로 + 도구)**: `npm run dev` + vite proxy로 `/api`가 `:5080`으로 프록시됨(관찰 또는 문서화된 재현). `.mcp.json` 신설(Playwright MCP). `.gitignore`에 node_modules·dist·wwwroot 등재(빌드 산출물 미커밋).

---

## 7. Parallel Modules / Evaluation Dimensions

- **Parallel Modules**: **N/A (단일 Generator).** 프론트(`frontend/` TS)와 백엔드(`MonitoringController`)가 파일 경계로는 안 겹치나, **API 계약 형상이 강한 공유 인터페이스**라 병렬화하려면 계약을 먼저 동결해야 하고, E2E/통합 검증이 양 계층을 함께 배선해야 한다. 첫 Full-stack 스프린트에서 계약 형상이 반복 조정될 여지가 있어 순차 단일 Generator가 안전(기본 1/1/1 유지 — "When unsure, start with Generate-Verify").
- **Evaluation Dimensions**: **functional only(단일 차원).** Full-stack 통합 평가 기준(§5)이 프론트(Web/UI)·백엔드(Backend/API)·통합·기능을 한 리뷰에서 흡수한다. 보안·성능 민감 신규 표면 없음(읽기 전용·인증 없음은 설계 확정). 4-Tier 독립 code-reviewer(Step 4.5)가 아키텍처·중복·의존성 방향을 별도 검토(런타임 Evaluator와 비중복).

---

## 8. 함정 섹션 (기존 교훈·구조적 트랩 — Generator 필독)

1. **`MapFallbackToFile`이 `/api` 404를 삼키는 함정(핵심)**: fallback은 매치 안 된 모든 요청을 index.html로 보낸다 → `/api/monitor/오타`가 HTML 200으로 응답돼 프론트 fetch가 JSON 파싱 실패로 조용히 깨진다. fallback이 `/api`를 제외하도록 배선(패턴·라우트 순서). C2·§5 Integration에 음성 대조로 검증됨.
2. **테스트 provider override는 `UseSetting`으로만**(lessons.md 2026-06-30 / S-FIELD-SEED): `WebApplicationFactory<Program>`에서 base=SqlServer를 SQLite로 되돌리려면 `builder.UseSetting("Database:Provider","Sqlite")`. `ConfigureAppConfiguration`·EF 디스크립터 제거는 **무효**(Program 즉시 평가·콜백 시점 디스크립터 0). 신규 MonitoringController 테스트도 이 1줄 필수. base=SqlServer 146 GREEN 규칙 준수.
3. **SqlServer 전용 SQL 금지**: 운영=SQL Server지만 F1은 읽기라 실 SqlServer 검증 불필요. 단 조회가 SqlServer 고유 SQL/raw를 쓰면 in-memory SQLite 테스트 더블에서 깨진다 → provider-agnostic LINQ만.
4. **piece 풀스캔(A-3)**: in-flight piece(E4)·sorter-command(E7)는 인덱스가 없다(인덱스 추가는 F1 OUT). `take` 상한 + 상태 필터 + 정렬(키셋)로 범위 강제 — 무한/전건 로드 금지. 감사 묶음 C의 `order_item(Barcode)`·`piece(PId,IsActive)` 인덱스는 **스코프 밖**(명시 제외) — MonitoringController가 이 인덱스에 의존하지 않게 설계.
5. **Windows Service ContentRoot(A-12 유사)**: `UseWindowsService()`가 ContentRoot=`AppContext.BaseDirectory`로 설정 → `UseStaticFiles` 기본 WebRootPath(ContentRoot/wwwroot)가 서비스 배포에서도 유효한지 확인. F1의 blocking 검증은 `dotnet run` 단일 서버(FRONTEND.md F1 Done)이며, 실 Windows Service 배포 검증은 배포 README 명기로 대체(F1 blocking 아님).
6. **wwwroot는 gitignored 빌드 산출물**: fresh clone/CI/test 호스트에 wwwroot·index.html이 없다 → 정적 서빙 미들웨어가 파일 부재에서 무해해야 한다(146 테스트는 wwwroot 없이 GREEN 유지). `dotnet run` 서빙 검증은 `npm run build` 선행 필수.
7. **Node/npm 실행**: 확인된 버전(node v22.17.0/npm 11.4.2)이나 Windows 환경 — npm 스크립트·경로에 공백(`회사 자료`) 포함 경로 주의. 셸은 PowerShell/Git Bash 양쪽 동작 확인.
8. **`.sln` 순수 .NET 유지**: `frontend/`를 `.sln`·`src/Wcs.*` 네이밍에 등록하지 않는다(FRONTEND.md §1.1) — `dotnet build`/`dotnet test`/pre-commit hook 무영향.

---

## 9. 미확정 (구현 중 추측 금지 — 필요 시 사용자 질문)

- **E5 `/sorters` 소터 0대·OFFLINE 시 형상**: 소터가 미기동/OFFLINE(번들 없음)이면 `online:false`로 반환(DestinationStatusService가 이미 그렇게 산출). 빈 목록 vs online:false 항목 — Generator가 일관 정책으로 명시(추가 사용자 확인 불요, 기존 산출 따름).
- **take 상한 값·초과 정책(clamp vs 400)**: Generator가 상수·정책을 정하고 명시(설계 확정 대상 아님 — 방어 로직 선택).
- 그 외 F1 범위 내 미확정 없음(FRONTEND.md §8 6개 질문 전건 확정됨 — §0 전제).

---

> Planner self-check — Detected project type: **Full-stack**. Required scenario slots: **3** (Web/UI frontend scenarios, Backend/API scenarios, End-to-end cross-layer data-flow). All slots filled: **yes**.

---

## Verification Scenarios (Full-stack — mandatory)

### Slot 1 — Web/UI 시나리오 (프론트 surface: 모니터링 페이지 ① + 전역 레이아웃)
- **각 surface 기본 상태**:
  - 모니터링 페이지 ① 최초 로드 — 좌측 내비 + 상단 상태바(소터 Online/Offline) + A/B/C 3개 섹션 기본 렌더(16셀 시드 데이터).
  - A 작업 데이터: 배치 목록/선택 UI + 선택 배치의 오더 테이블(order_no·type·destination·status·planned/reserved/sorted).
  - B 로봇 이동중: in-flight piece 테이블(pId·barcode·qty·chuteNo·agvNo·inductionNo·status·시각).
  - C 분류: 소터 선택 + 셀 현황 테이블(16셀: cellNo·capacity·currentQty·occupied·enabled) + sorter_command 이력 테이블.
- **각 대체 상태(sprint가 도입)**:
  - 배치 선택 시 오더 테이블 갱신(selected) / 오더 행 확장 → order_item 표시(expanded).
  - in-flight·sorter-command 페이징(다음 페이지 로드).
  - 필터 적용(배치·상태) 결과 반영.
  - 로딩 상태(폴링/최초 fetch)와 데이터 도착 전환.
- **관련 empty/error 상태**: 데이터 없는 배치/소터 선택 시 빈-상태 메시지(빈 테이블 crash 아님). API 에러(예: 정지된 백엔드) 시 에러 상태 표시(무한 스피너·앱 크래시 아님).
- **다크 모드**: **N/A** — F1은 다크 모드 토글 미구현(단일 기본 테마). 사유: F1은 모니터링 골격 확립 범위이며 테마 전환은 후속 페이즈 여지(FRONTEND.md F1 Done에 다크모드 없음).
- **핵심 상호작용 흐름(sprint가 만드는 사용자 가시 동작)**: 앱 로드 → 좌측 내비로 모니터링 페이지 이동 → 배치 선택 → 오더 테이블 표시 → 오더 행 확장 → order_item 확인 → in-flight/셀/sorter_command 3종이 폴링으로 갱신되는 것을 관찰. (Playwright: navigate → click(배치) → assert(오더 행) → click(행 확장) → assert(아이템) → 페이징 click → assert. 번호 스크린샷 + console.log 캡처, pageerror·React warning 0.)

### Slot 2 — Backend/API 시나리오 (백엔드 surface: `/api/monitor/*`)
- **엔드포인트(method + path)**: E1 `GET /api/monitor/batches` · E2 `GET /api/monitor/orders?batchId=&status=` · E3 `GET /api/monitor/orders/{id}/items` · E4 `GET /api/monitor/pieces/in-flight?take=&cursor=` · E5 `GET /api/monitor/sorters` · E6 `GET /api/monitor/sorters/{destId}/cells` · E7 `GET /api/monitor/sorter-commands?destId=&take=&cursor=`.
- **엔드포인트별 해피패스(입력→출력 형상)**:
  - E1 → 200 배열[{id,workDate,batchNo,waveNo,status,openedAt,closedAt}] 최신순·take 상한 이하.
  - E2(batchId 지정) → 200 배열[{id,orderNo,orderType,destinationChuteNo,status,plannedQty,reservedQty,sortedQty}] 집계 정확(order_item 합).
  - E3(존재 오더 id) → 200 배열[{id,barcode,plannedQty,reservedQty,sortedQty}].
  - E4 → 200 배열(status∈QUERIED/RESERVED/PERMITTED만) + take 상한 + 커서로 다음 페이지.
  - E5 → 200 배열[{destId,chuteNo,online,ready,full,paused}](DestinationStatusService 산출 일치).
  - E6(존재 destId) → 200 배열 16셀[{cellNo,capacity,currentQty,occupied,enabled,assignedOrderNo?}](SorterCellQty 수량 일치).
  - E7 → 200 배열[{id,pId,barcode?,cellNo,cSeq,rSeq,status,cWrittenAt,rFlagAt}] 최신순 + take 상한 + 커서.
- **엔드포인트별 관련 에러케이스(해당되는 것만 — 패딩 없음)**:
  - E3/E6 미존재 id/destId → 빈 배열 또는 404(정책 일관 — Generator 명시, Evaluator는 500 아님·일관성 확인).
  - E4/E7 `take` 상한 초과 요청 → clamp 또는 400(정책 일관·무한 로드 아님 입증).
  - E4/E7 잘못된 커서 → 400 또는 빈 결과(500 아님).
  - **fallback 비삼킴(음성 대조)**: `GET /api/monitor/<미존재 경로>` → index.html(HTML 200) 반환 안 됨(404 계열).

### Slot 3 — End-to-end cross-layer 데이터 흐름 시나리오 (2+ 계층 관통)
- **빌드→서빙→조회→렌더 관통**: `npm run build`로 프론트 산출물이 `src/Wcs.Api/wwwroot`에 배치 → `dotnet run --project src/Wcs.Api`(:5080) 단일 서버 기동 → 브라우저가 **:5080에서 SPA를 로드**(같은 출처) → SPA가 `/api/monitor/sorters`·`/orders`·`/pieces/in-flight`·`/sorters/{destId}/cells`를 호출 → **실 DB(16셀 시드)의 데이터가 페이지 ① A/B/C 테이블에 렌더**된다. (프론트 라우팅 딥링크 → fallback → index.html → SPA가 다시 API 호출로 데이터 복원까지 관통 확인. dev 경로 대체 검증: `npm run dev`(:5173) + proxy로 `/api`→:5080 동작.)
