# Sprint Contract — S-FRONTEND-F2 (SignalR 실시간 + 3DS 워드 뷰(읽기 전용) + oplog 라이브 테일)

> **프론트엔드 F2 스프린트** — `docs/FRONTEND.md` §2(실시간 설계)·§5 페이지 ②·§6 F2가 **범위의 단일 진실. 재설계 금지.**
> WHAT/WHERE/검증만 규정 — 정확한 시그니처·허브 메서드명·컴포넌트 구조·타이밍 상수 값은 **Generator 재량(제약 내)**. 코드 구현 0.
> F1(스캐폴드·정적 서빙·모니터링 읽기·Airbnb 라이트 테마) 병합 완료 위에 실시간 계층을 얹는다.

## 0. 메타

| 항목 | 값 |
|------|-----|
| Sprint ID | S-FRONTEND-F2 |
| Branch | `feat/frontend-f2` |
| Base | `develop` (PR #29까지 병합 — backend/ 구조·F1 모니터링·Airbnb 라이트 테마·시드 가드) |
| Detected Project Type | **Full-stack (Web/UI)** — `.mcp.json` Playwright 존재(F1 신설). Evaluator 브라우저 검증 슬롯 발동. |
| Scaling | **1 Planner / 1 Generator / 1 Evaluator** (단일 페이즈·팬아웃 없음) |
| Test baseline | **164 GREEN** (완료 시 164 전원 GREEN 유지 + 신규 허브 통합 테스트 = 164+N) |
| 스펙 소스 | `docs/FRONTEND.md` §2·§5 페이지 ②·§6 F2 / `docs/DESIGN-airbnb.md`(토큰) / `docs/SPEC.md`(레지스터 맵 D0~D6) |
| 이월 처리 | **F1-CR-M1**(IMonitoringQueries DI AddScoped) · **RESTYLE-CR-M1**(Rausch 버튼 AA) · **RESTYLE-CR-M2**(faint 정보성 텍스트 AA) — 3건 전부 이번 스프린트 IN |

## 1. 목표 (WHAT · 한 줄)

`Wcs.Api`에 SignalR 허브(`/hubs/monitor`)와 relay 서비스를 신설해 **기존 관측 훅을 재사용(신규 폴 루프 0)** 으로 소터 워드(D0~D6+Online) 변화분·operation_log 엔트리를 실시간 push하고, 프론트에 **읽기 전용 3DS 워드 페이지 ②**(변경 하이라이트·재연결 부트스트랩 복구)와 **operation_log 라이브 테일**을 추가하며, TanStack Query를 SignalR 이벤트로 무효화 연동한다. 동시에 이월 3건(DI AddScoped·명암비 2건)을 해소한다. **쓰기·제어·인증은 F3 — 이 스프린트는 관측/뷰 전용.**

**relay 불변식(핵심)**: 훅 콜백은 **논블로킹·예외 격리**, `IHubContext`는 **fire-and-forget**. 관측이 폴(150ms)·핸드셰이크·API 본 동작을 지연시키지 않는다(S-OBSERVABILITY 계약과 동형). **PlcGateway/Wcs.Core 의미·훅 시그니처 0 변경.**

## 2. Scope IN

### 2A. `Wcs.Api` — SignalR 허브 `WcsMonitorHub` (`/hubs/monitor`)
- **신규 파일**(예: `backend/src/Wcs.Api/Hubs/WcsMonitorHub.cs`). `Hub` 파생, 인증 없음(사용자 확정).
- **부트스트랩(§2.1)**: `OnConnectedAsync`에서 `ISorterGatewayRegistry.AllBundles`의 각 `Latest`(전체 D0~D6 + Online) 스냅샷을 **접속 클라이언트에 1회 전송** → 늦게 접속한 클라이언트도 즉시 완전 상태 확보. 재연결 시에도 동일 경로로 복구.
- **구독 그룹**: oplog 테일 구독(그룹 `oplog`)과 소터 워드 구독을 클라이언트가 선택 가능(허브 메서드). **고빈도 `POLL_CHANGE`는 oplog 기본 스트림에서 제외 또는 명시 옵트인**(콘솔/테일 폭주 방지 — DB 정책과 동형). 초기엔 소터 워드 델타는 전량 push + 클라이언트 필터로 단순화 가능(§2.1·§2.2).
- 소터 워드 변화분 델타(reg·old·new·chuteNo)·Online/Offline 전이·하트비트 스냅샷·oplog 엔트리를 클라이언트로 push하는 메시지 계약(메서드명·payload 형상은 Generator 재량, 프론트 타입과 1:1).

### 2B. `Wcs.Api` — relay 서비스 (기존 훅 재사용 · 신규 폴 루프 0)
- **신규 relay 서비스**(IHostedService 등 — HOW는 Generator). 두 소스를 `IHubContext<WcsMonitorHub>`로 fire-and-forget 브로드캐스트:
  - **① 소터 워드 스트림**: `SorterBundleHandle`의 `SubscribeRegisterChange`(reg,old,new)·`SubscribeOnline`·`SubscribeOffline`를 relay가 **추가 구독**(기존 operation_log 구독과 나란히 — 훅은 멀티캐스트 이벤트). 변화분만 push(무변화 0). + **저빈도 하트비트**: 주기적으로 `AllBundles`의 `Latest` 전체 스냅샷 1회 push(델타 유실·재연결 갭 보정).
  - **② operation_log 테일 스트림**: 단일 초크포인트 `OperationLogService`(단일 컨슈머)에서 각 엔트리를 그룹 `oplog`로 브로드캐스트. **DB 영속화와 별개 경로**(기록 실패가 스트림을, 스트림 실패가 기록을 막지 않음). `POLL_CHANGE`는 기본 제외/옵트인(2A).
- **relay 안전 요건(불변식)**: 모든 콜백/브로드캐스트는 **논블로킹·예외 흡수(fail-safe)**. 폴/쓰기/핸드셰이크 스레드에서 직접 호출되므로 예외가 새어나가 루프를 죽이면 안 됨(기존 훅 계약·Program.cs L365-419·PlcGateway `EmitRegisterChanges` try/catch와 동형).
- **⚠ 구독 시점 순서**: `AllBundles`는 `SorterRegistryFactory.StartAsync` 완료 후에만 채워진다. relay 구독은 **레지스트리 초기화 이후**에 이뤄져야 한다(IHostedService 등록 순서로 보장하거나 registry StartAsync 내에서 나란히 구독 — Generator 판단, 검증 필수).

### 2C. `Wcs.Api` — Program.cs 결선 + appsettings 타이밍 외부화
- `builder.Services.AddSignalR()` 등록. relay 서비스·IHubContext 결선.
- **`app.MapHub<WcsMonitorHub>("/hubs/monitor")`** 매핑. 미들웨어/엔드포인트 순서: `UseStaticFiles()`(기존 L207) → `MapControllers()`(L213) → **`MapHub`** → `app.Map("/api/{**rest}", …)` catch-all(L222) → `MapFallbackToFile`(L223). catch-all은 `/api/**`만 매치하므로 `/hubs/monitor`를 삼키지 않음(함정 §5-1 — 검증 결선).
- **신규 타이밍은 전부 appsettings**(절대규칙 #7 — 하드코딩 금지): 하트비트 주기·(있으면)버퍼/스로틀 주기 등을 신규 섹션(예: `Wcs:Monitor:HeartbeatMs`)에 두고 바인딩. 코드 상수 금지.

### 2D. `Wcs.Api` — IMonitoringQueries AddScoped 전환(F1-CR-M1) + operation-log 백로그 엔드포인트
- **F1-CR-M1 해소**: `MonitoringController`가 생성자에서 `new MonitoringQueries(db, registry, status)`로 **요청당 손조립**하던 것을 제거하고, Program.cs에 **`AddScoped<IMonitoringQueries, MonitoringQueries>()`** 등록 → 컨트롤러는 `IMonitoringQueries`를 **주입**받는다. (deps: WcsDbContext scoped·ISorterGatewayRegistry/IDestinationStatusService 싱글톤 — scoped 수명 해석 정상.)
- **operation-log REST 백로그(§2.2·§3.1 — 테일 초기 N행 소스)**: `IMonitoringQueries` + `MonitoringController`에 **읽기 전용** `GET /api/monitor/operation-log?category=&level=&sorterChuteNo=&take=&cursor=` 추가. `operation_log` 테이블 조회(선두 인덱스 `at`/`id` 활용·키셋 커서·take clamp — E7 sorter-commands 패턴 재사용). **AsNoTracking·기존 리포지토리 무변경.** operation_log **스키마 0 변경**(조회만).

### 2E. `frontend` — SignalR 클라이언트 + 페이지 ②(읽기 전용) + oplog 테일 + 무효화
- **@microsoft/signalr 클라이언트 래퍼**(신규 `frontend/src/lib/signalr.ts` 등): 접속·재연결(withAutomaticReconnect)·부트스트랩 스냅샷 수신·델타/전이/하트비트/oplog 이벤트 수신. `/hubs/monitor` 상대 경로(운영=동일 출처, dev=vite proxy).
- **페이지 ② 레지스터 패널(§5 페이지 ②·읽기 전용)**: D0 C_CellNo·D1 C_Seq·D2 R_CellNo·D3 R_Seq·D4 비트(C_Flag·R_Flag·Ready)·D5 CurFloor·D6 TgtFloor·Online. SignalR 스트림으로 갱신, **변경값 하이라이트(깜빡임)** + 각 값 **마지막 변경 시각**. 소터 N대 선택(기존 `useSorters`/소터 목록 재사용). **쓰기/편집 컨트롤 없음**(F3). 신규 라우트(`/sorters` 또는 `/sorters/:destId` — App.tsx Route 추가) + `Layout.tsx` NAV의 "3DS 워드"(현재 `enabled:false`·phase F2 배지) **활성화**.
- **operation_log 라이브 테일(§2.2)**: 하단 패널. category/level 필터, 자동 스크롤 토글, **`POLL_CHANGE` 기본 접힘(옵트인)**. 접속 시 REST 백로그(2D) 로드 후 SignalR로 append(무한 스크롤/테일).
- **TanStack Query ↔ SignalR 무효화(§2.3)**: 행 단위 push 남발 금지 — SignalR API/HANDSHAKE/STATE 이벤트 수신 시 `invalidateQueries`로 배치/오더/in-flight/셀/sorter_command/소터 readiness를 **근실시간 보정**. 고빈도·저지연(워드·oplog)=push, 집계·목록=폴링+이벤트 무효화 원칙 유지.

### 2F. `frontend` — 명암비 2건(이월 · 확정)
- **RESTYLE-CR-M1(버튼)**: `components/ui/button.tsx` `solid` variant의 **안정 상태 fill을 `bg-brand-active`(#e00b41, 4.89:1)로 채택**(현재 `bg-brand` #ff385c=3.52:1). 사용자 확정(스펙 내 토큰·시각 차이 미미). 백색 라벨 AA(≥4.5:1) 충족.
- **RESTYLE-CR-M2(정보성 텍스트)**: **데이터를 담는 정보성 `text-faint`(#929292, 3.11:1)를 `text-muted`(#6a6a6a, 5.41:1)로 치환** — 타임스탬프·시퀀스 컬럼·배정오더·페이저 카운트 등(예: `SortingSection.tsx` C_Seq/R_Seq/C 기입/R 수신/assignedOrderNo·`InFlightSection.tsx` createdAt·`CursorPager.tsx` 위치). **장식/비활성 용도의 faint(비활성 nav `text-faint/70`·off 램프 라벨·순수 라벨·로고 서브캡션)는 DESIGN 문서 스코프상 유지 가능** — Generator가 "데이터 가독성 필요 여부"로 판단, **데이터 담는 것은 전부 AA**. (DESIGN: faint=disabled 전용·very sparingly.)

### 2G. `frontend` — 빌드/개발 결선
- `package.json`에 **`@microsoft/signalr`** 추가(F1에서 의도적 미설치). 런타임 의존 셋 최소 유지.
- `vite.config.ts` dev proxy에 **`/hubs` 추가 + `ws: true`**(websocket proxy) → `http://localhost:5080`. 기존 `/api` proxy 불변.

### 2H. 신규 테스트 (`backend/tests/Wcs.Tests/`)
- **허브 통합 테스트**(WebApplicationFactory 기반 — `MonitoringApiTests`의 인스턴스-고유 in-memory SQLite 팩토리 패턴 재사용): 최소 고정
  - **접속 → 부트스트랩 스냅샷 수신**(AllBundles Latest 1회 전송 확인).
  - **레지스터 변화 → 델타 push 수신**(관측 훅 발화 → 허브 브로드캐스트 → 클라이언트 수신).
  - (가능하면) **operation_log append → 테일 수신** / **POLL_CHANGE 기본 미포함**.
- operation-log REST 엔드포인트(2D) 형상·페이징·필터 통합 테스트(E-시리즈 패턴).
- **⚠ TestServer websocket**: `WebApplicationFactory` TestServer는 SignalR 기본 WebSocket 협상을 그대로 지원하지 않을 수 있음 → HubConnection을 TestServer `HttpMessageHandler`/`WebSocketFactory`로 결선하거나 **LongPolling 트랜스포트 대체** 검토(함정 §5-4).

## 3. Scope OUT (0 변경 — 무변경 가드)

- **PlcGateway/Wcs.Core 의미 0**: `PlcGateway.cs`·`HandshakeOrchestrator.cs`·`Models.cs`(PlcSnapshot·RegisterMap)·관측 훅 시그니처(`OnRegisterChange`/`OnWrite`/`OnStage`/`OnOnline/OfflineTransition`·`SorterBundleHandle.Subscribe*`) **불변**. relay는 **소비만**. `git diff -- backend/src/Wcs.PlcGateway backend/src/Wcs.Core` 빈 출력.
- **쓰기/제어/인증 = F3**: `OpsController` 신설·워드 쓰기(SetTgtFloor/ClearR/CellAssign enqueue)·clear/pause/resume·`OnCleared` 결선·PAUSED/RESUMED 전이·로그인/바인딩 제한 **전부 OUT**. 페이지 ②는 **읽기 전용**(편집 컨트롤 0).
- **RcsController(`/api/v1`) 불변**(IF-05/09/10). RcsPush·DestinationStatusPusher·핸드셰이크 로직 무접촉.
- **operation_log 스키마 0**·**마이그레이션 0**(조회 엔드포인트만 추가). `OperationLogService` 컨슈머 로직은 브로드캐스트 얹기 외 동작(배치·teardown·fail-safe) 불변.
- **DbSeeder 토폴로지 불변**·**appsettings Sorters[]/Provider/ConnectionStrings 값 불변**(신규 `Wcs:Monitor` 타이밍 섹션 추가만).
- **F1 모니터링 표면(E1~E7)·라이트 테마 토큰(index.css @theme) 값 불변**(명암비 2건 치환 외). 신규 상태색 토큰 도입 없음.

## 4. Deliverables & 검증 (Completion Gate)

> **Fresh evidence 의무**: 모든 PASS는 "지금 실제로 돌린" raw 증거(테스트 러너 요약·Playwright 스크린샷/DOM computed 값·`dotnet run`+`Sim3ds` 콘솔·`git diff --stat`)를 `tasks/sprint-feedback.md`에 인용. Generator 보고·추정만으론 PASS 금지. (가중치·Web/UI Full-stack 슬롯.)

**① 실시간 워드 동작 (Playwright · 핵심)**
- `dotnet run --project backend/src/Wcs.Sim3ds`(:1502) + `dotnet run --project backend/src/Wcs.Api`(소터 online) 기동 후 페이지 ②에서:
  - D0~D6 값이 **폴링 없이 SignalR push로 갱신**(핸드셰이크/이동으로 CurFloor·C_Seq·R_Seq·Ready 등 변화)·**변경 하이라이트** 육안 확인.
  - **재연결 시 부트스트랩 스냅샷 복구**(허브 재접속 후 전체 D0~D6+Online 즉시 표시). raw 증거(스크린샷/네트워크 프레임).

**② operation_log 테일 스트림**
- 핸드셰이크/전이 발생 → 하단 테일에 엔트리 append 스트리밍(자동 스크롤). **`POLL_CHANGE` 기본 접힘(옵트인)** 확인(고빈도가 기본 스트림 폭주 안 함).

**③ 기존 164 GREEN + 신규 테스트**
- `dotnet test backend/Wcs.sln` → 기존 164 전원 GREEN + 허브 통합/operation-log 엔드포인트 테스트 GREEN(합계 164+N). 실패 0. raw 요약 인용.

**④ relay 무영향 (핸드셰이크 타이밍 회귀 0)**
- **기존 E2E GREEN이 증거**: E2E 그룹(A~I)·소터 핸드셰이크 통합 테스트가 relay 얹은 뒤에도 GREEN 유지. 동시성/타이밍 취약 스위트는 **≥5회 반복 + stash 대조**로 회귀 귀속(S-E2E-MULTI-AGV·S9 flake 교훈 — 1회 GREEN 신뢰 금지). relay 콜백 논블로킹·예외 격리 소스 확인.

**⑤ 명암비 2건 해소 (computed)**
- 브라우저 computed 색상으로 RESTYLE-CR-M1(버튼 solid fill=#e00b41 → 백 라벨 ≥4.5:1)·RESTYLE-CR-M2(정보성 데이터 텍스트 muted #6a6a6a=5.41:1) **AA 산술 통과** 확인. 장식/비활성 faint 잔존은 데이터 비담지로 정당함 명시.

**⑥ 무변경 가드 (스코프 격리)**
- `git diff --stat` 판독 → 변경이 **§2 IN 파일에만 국한**. `git diff -- backend/src/Wcs.PlcGateway backend/src/Wcs.Core` = 빈 출력(훅 시그니처·판정 의미 불변). 마이그레이션 디렉터리·DbSeeder·RcsController·appsettings Sorters/Provider/ConnectionStrings diff 0(신규 `Wcs:Monitor` 타이밍 섹션 추가만). `/api/{**rest}` catch-all이 `/hubs/monitor`를 삼키지 않음을 negotiate 응답(200/101, not 404)으로 입증.

**Completion**: ①~⑥ 전부 PASS + `tasks/lessons.md`에 F2 교훈(relay 무영향 입증법·TestServer websocket 처리·POLL_CHANGE 옵트인) 1행 + 프로세스/포트 정리(:5080·:5173·:1502 free) + git status 핸드오프 동일.

## 5. 함정 (Traps)

1. **`/api/{**rest}` catch-all(L222) vs `/hubs`**: catch-all은 `/api/**`만 매치하므로 `/hubs/monitor`는 안전하나(F1 리뷰 확인 "catch-all vs /hubs 충돌 없음") **MapHub가 실제로 매핑됐고 negotiate가 404 아님**을 검증 결선. fallback(`index.html`)이 `/hubs`를 삼키지 않게 순서 확인.
2. **UseStaticFiles ↔ SignalR 순서**: `UseStaticFiles`(라우팅 이전 미들웨어) → 엔드포인트(`MapControllers`/`MapHub`). WebApplication 최소 호스팅에서 MapHub가 라우팅 자동 추가 — 순서 역전 주의.
3. **vite dev proxy `/hubs` websocket**: `ws: true` 없으면 dev에서 SignalR 핸드셰이크(101 Upgrade) 실패. `/api` proxy와 별도 항목.
4. **TestServer websocket 한계**: `WebApplicationFactory` TestServer는 SignalR 기본 WebSocket 협상을 그대로 못 할 수 있음 → HubConnection을 TestServer 핸들러(`Server.CreateHandler()`/`WebSocketFactory`)로 결선하거나 **LongPolling 대체**. 무거운 실-Sim 허브 테스트는 직렬 컬렉션 고려(E2E 병렬 부하 flake 교훈).
5. **POLL_CHANGE 폭주**: 150ms 폴에서 레지스터가 자주 변하면 oplog 테일이 폭주 → **기본 스트림에서 제외/옵트인**. 단 소터 워드 스트림(페이지 ②)은 델타가 목적이므로 push 유지하되 **소터별 그룹/구독**으로 관심 없는 클라이언트엔 미전송(선택). fire-and-forget이라도 브로드캐스트 빈도가 relay 스레드/네트워크 부담이 되지 않게.
6. **relay 구독 시점**: `AllBundles`는 `SorterRegistryFactory.StartAsync` 후에만 채워짐 — relay가 그 전에 구독하면 빈 세트. IHostedService 등록 순서 또는 registry StartAsync 내 나란히 구독으로 보장(§2B ⚠).
7. **relay가 본 동작 지연 금지(절대규칙·S-OBSERVABILITY)**: 훅 콜백에서 동기 I/O·블로킹 금지. `IHubContext` fire-and-forget + 예외 흡수. 폴/핸드셰이크 핫패스 비지연을 ④로 실증.
8. **라이브 검증 환경 드리프트(기등재)**: DbSeeder는 소터 `chuteNo=30` 시드 vs `appsettings.Sorters[0].ChuteNo=1` → dev 콜드스타트 시 `SorterRegistryFactory` fail-loud. ①/② 라이브는 F1 Evaluator처럼 `Sorters__0__ChuteNo=30` env override(추적파일 무변경)로 소터 online 확보. frontend 스코프 밖·backend 후속.

## 6. Planner Self-Check

- [x] **Scope IN** = 허브(2A)·relay 서비스(2B)·Program 결선+타이밍 외부화(2C)·IMonitoringQueries AddScoped + operation-log 백로그(2D)·프론트 signalr client·페이지 ② 읽기전용·oplog 테일·무효화(2E)·명암비 2건(2F)·vite proxy/의존/nav(2G)·허브 통합 테스트(2H). 실독 근거: FRONTEND.md §2·§5·§6 / Program.cs(DI·훅 구독 블록 L154-198·L357-419·MapControllers L213·catch-all L222) / OperationLogService.cs(단일 컨슈머) / SorterGatewayRegistry.cs(Subscribe*·Latest·AllBundles) / MonitoringController.cs(손조립 현황·E1~E7) / PlcGateway.cs(EmitRegisterChanges reg명·fail-safe try/catch) / Models.cs(PlcSnapshot D0~D6) / frontend(api.ts·queries.ts·App.tsx·Layout.tsx·index.css @theme·button.tsx·meter.tsx·SortingSection/InFlightSection faint 사용처) / MonitoringApiTests 팩토리 패턴 / feedback-archive F1 CR-MAJOR·sprint-feedback RESTYLE-CR-M1/M2.
- [x] **사용자 확정 반영(재질문 0)**: F2 범위=허브+relay(신규 폴 0)+페이지 ② 읽기전용+oplog 테일+무효화 / 이월 3건 IN(RESTYLE-CR-M1은 **brand-active #e00b41 fill 확정 명기**) / 인증 없음 / @microsoft/signalr 추가.
- [x] **절대규칙 점검**: #1(PLC 쓰기 무관 — relay는 관측만) · #7(하트비트/버퍼 신규 타이밍 appsettings 외부화·2C) · #8(판정 순수함수 무접촉 — 신규 판정 0). relay 콜백 논블로킹·예외격리·`IHubContext` fire-and-forget = S-OBSERVABILITY 계약 동형(2B·함정7).
- [x] **Scope OUT** = PlcGateway/Core 의미·훅 시그니처 0 / 쓰기·제어·인증(F3) / RcsController / operation_log 스키마·마이그레이션 0 / DbSeeder·Sorters/Provider/ConnectionStrings 값 0. 무변경 가드 ⑥ git diff로 입증.
- [x] **검증(가중치·Full-stack 슬롯)**: ①실시간 워드 push+하이라이트+재연결 부트스트랩(Playwright) ②oplog 테일(POLL_CHANGE 접힘) ③164+N GREEN ④relay 무영향(기존 E2E GREEN·≥5회 반복+stash 대조) ⑤명암비 2건 computed AA ⑥무변경 가드. 각 fresh evidence 의무.
- [x] **함정 8종**: catch-all/hubs·미들웨어 순서·vite ws proxy·TestServer websocket·POLL_CHANGE 폭주·relay 구독 시점·relay 본동작 지연 금지·라이브 드리프트 env override. F1 리뷰 i3·S-OBSERVABILITY·S-E2E flake 교훈 결선.
- [x] **코드 구현 0** — WHAT/WHERE/VERIFY만. 허브 메서드명·payload 형상·컴포넌트 구조·타이밍 값·라우팅 shape·TestServer 결선 방식은 Generator 재량(제약 내).
