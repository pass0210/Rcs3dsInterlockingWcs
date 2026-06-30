# Sprint Contract — S-M5-P1 (콜드스타트 프로비저닝 + Windows Service 호스팅)

> 작성: Planner Subagent · 2026-06-29 · 방식 확정(재질문 금지): M5는 P1 우선·라이브 구동은 M5 포함.
> 이 계약은 **WHAT/WHERE/검증만** 규정한다. 구현 방법(조건부 적용 메커니즘·훅 시그니처·플래그/환경 게이트 방식)은 Generator가 결정한다.
> 최우선 제약: **기존 전체 테스트 회귀 0**(사용자 제시 기준선 146 — Generator가 sprint 착수 시 `dotnet test` baseline 실측·기록).

---

## 0. M5 전체 페이즈 분할안 (요약 — P1만 이번 계약)

M5("운영 준비")는 직교 축이 5개라 한 스프린트에 다 넣지 않는다. 아래로 분할하고 **P1만 본 계약**으로 작성한다.

| 페이즈 | 스코프 | unblock 대상 | 비고 |
|--------|--------|-------------|------|
| **P1 (본 계약)** | 콜드스타트 자동 provision(Migrate+dev 시드) + Windows Service 호스팅(`UseWindowsService`) + 재시작 레지스터 재독 확인 | **라이브 `dotnet run`·실 3DS HW 테스트의 직접 unblocker** | TASKS.md M5 "운영 자동 Migrate"(Program.cs:133 갭) + "Windows Service 호스팅"(Program.cs:20 TODO) |
| P2 (제안) | Serilog 구조화 로깅(레지스터 변화 + API 원문, rolling) | 운영 관측성 | TASKS.md M5. ILogger→Serilog 교체. P1 무관 직교 축. |
| P3 (제안) | OFFLINE 중 IF-05/08 응답 정책 **확정·검증** | 운영 정책 명문화 | ⚠ §6에서 판정: IF-08 폴링은 재설계로 폐지(push가 OFFLINE을 ready=false로 전달) → **"IF-08 부분 이미 충족"**. IF-05 OFFLINE 소터 NG 동작만 코드 확인해 "이미 충족 vs 추가" 판정 필요. 대부분 검증 스프린트일 가능성. |
| P4 (제안) | 운영 README(서비스 등록·설치·RTU config·트러블슈팅) | 운영 인수인계 | TASKS.md M5 Done 항목. 서비스 등록 스크립트는 P1에 포함(아래 §3.4 참조) — README는 그 스크립트 사용법까지 포함해 P4로. |
| P5 (제안) | 이연 findings 정리(F1b 핸드셰이크 직렬화 / IT4b 병렬격리 컬렉션 / 만재센서 / SPEC §7 문서부채 / dead-code 정리) | 기술부채 | tasks/todo.md 전 항목. 비차단. |

**서비스 등록 스크립트 배치 제안**: P1에 **포함**(아래 §3.4). 이유 — "Windows Service 호스팅"이 P1이고, 호스팅이 동작함을 입증하려면 등록 스크립트가 같은 스프린트에 있어야 Done(콜드스타트→서비스 기동) 검증이 자기완결적이다. 사용법 문서화(README)만 P4로 분리.

---

## 1. Goal (P1)

빈 DB 상태의 production `dotnet run`(및 Windows Service)이 **자동으로 DB 스키마를 프로비저닝하고(콜드스타트 Migrate) 개발/빈-DB 한정 시드를 적용해 정상 기동**하도록 만든다. 직전 E2E 스프린트에서 발견된 크래시(빈 `wcs.db` 기동 시 `ChuteCapacityService`가 `no such table: chute_detail`로 죽음)를 정식 해소한다. 동시에 `builder.Host.UseWindowsService()`로 Windows Service 호스팅을 활성화하되 콘솔/테스트 실행을 깨지 않는다.

**이것이 실 3DS 하드웨어 테스트의 직접 unblocker다** — 라이브 스택이 빈 DB에서 기동조차 못 하면 현장 테스트가 불가능하다.

---

## 2. Detected Project Type

**Backend / API (.NET, ASP.NET Core Minimal-host + MVC Controller, EF Core, Modbus 게이트웨이).**
신호: `src/Wcs.Api`(ASP.NET Core, `Program.cs` + `Controllers/`), `src/Wcs.Data`(EF Core `WcsDbContext`·`DbSeeder`), `src/Wcs.Migrations.{Sqlite,SqlServer}`(EF 마이그레이션 분리 어셈블리, 각 3개 마이그레이션), `src/Wcs.PlcGateway`(FluentModbus), `tests/Wcs.Tests`(xUnit, [Fact]/[Theory] 139건 + Theory 전개분 ⇒ 실행 ≈146). TargetFramework `net10.0`.
→ Verification Scenarios 슬롯 = **콜드스타트→정상 시나리오 + 회귀 가드**. UI/Playwright 슬롯 = N/A(헤드리스 백엔드).

---

## 3. Implementation Scope (WHAT / WHERE)

### 3.1 콜드스타트 자동 Migrate (핵심)
- **WHAT**: 실 호스트 기동 시 `WcsDbContext`에 대해 `Database.Migrate()`(또는 동등)를 실행해 빈/구버전 DB에 스키마를 적용한다. 적용 대상 provider는 appsettings `Database:Provider`(Sqlite/SqlServer) 분기를 따른다 — 마이그레이션 어셈블리는 이미 Program.cs:49/53에서 provider별로 지정됨(`Wcs.Migrations.Sqlite`/`Wcs.Migrations.SqlServer`).
- **WHERE**: `src/Wcs.Api/Program.cs` — `app.Build()` 이후 `app.Run()` 이전 startup 구간(현 Program.cs:133 "운영 자동 Migrate는 M5" 주석 지점). 필요 시 별도 startup 훅/확장 메서드 신규 파일 허용(예: `src/Wcs.Api/Startup/DbInitializer.cs` — 파일명·구조는 Generator 재량).
- **⚠ 최우선 무파손 제약(테스트 경로)**: 5개 테스트 팩토리(`FakeModbusWebApplicationFactory`·`SimWebApplicationFactory`(ScenarioTests)·`RcsPush…Factory`·`E2EWebApplicationFactory`·`ApiIntegrationTests.cs:1291/1373`의 인라인 팩토리)가 **모두 동일 패턴**으로 DB를 주입한다: `DbContextOptions<WcsDbContext>`/`WcsDbContext` 서비스 descriptor 제거 → named in-memory SQLite 재등록 → 별도 anchor 연결로 `db.Database.EnsureCreated()` + `DbSeeder.Seed(...)` 직접 호출. 콜드스타트 Migrate 코드가 이 경로에서 실행되면 (a) `EnsureCreated`로 만든 스키마에 `Migrate()`가 `__EFMigrationHistory` 부재로 충돌/중복 적용하거나 (b) 시드가 중복 삽입(UNIQUE 위반)될 수 있다. **따라서 자동 Migrate+시드는 실 호스트 startup에서만 실행되고 테스트 호스트에서는 실행되지 않아야 한다**(환경 게이트·플래그·테스트 오버라이드 등 — 메커니즘은 Generator가 설계·정당화). 테스트 배선 파일은 **수정 대상이 아니다**(무변경 가드). 단, 테스트가 깨지지 않도록 게이트를 추가하는 최소 배선(예: 테스트 팩토리가 이미 설정하는 환경/구성으로 자동 분기)이 필요하면 테스트 인프라 파일 1곳 이내 최소 변경은 허용하되, 사유를 sprint-feedback에 명시.

### 3.2 dev / empty-DB 한정 시드 훅
- **WHAT**: 라이브 `dotnet run`이 동작하려면 기준정보·최소 오더 시드가 필요하다(직전 E2E에서 빈 DB 크래시의 두 번째 원인). `DbSeeder.Seed(WcsDbContext, agvFloorMap?)`는 **이미 존재**(src/Wcs.Data/DbSeeder.cs)하나 Program.cs가 호출하지 않는다. 콜드스타트 시 dev/빈-DB 한정으로 이 시드를 호출한다.
- **운영 안전 게이트(WHAT·필수)**: 운영(production)은 실제 마스터데이터를 쓰므로 **테스트 시드(슈트 1~5·소터 chuteNo=30·TEST-BARCODE-* 오더 등)를 절대 자동 삽입하면 안 된다**. 시드 적용은 환경(Development) 또는 명시적 설정 플래그로 게이트한다(예: `Database:SeedOnStartup` 또는 `ASPNETCORE_ENVIRONMENT=Development` — 키 이름·게이트 방식은 Generator 결정, appsettings 키는 하드코딩 금지 절대규칙 #7 준수, `_comment_`로 의도 문서화). 기본값은 **운영 안전쪽**(시드 off)으로 둔다.
- **WHERE**: `src/Wcs.Api/Program.cs`(또는 §3.1 startup 훅) + `src/Wcs.Api/appsettings.json`(시드 게이트 키 신규) + 필요 시 `appsettings.Development.json` 신규. `DbSeeder` 본문은 무변경(이미 멱등).

### 3.3 Windows Service 호스팅 (`UseWindowsService`)
- **WHAT**: `builder.Host.UseWindowsService()`를 활성화(Program.cs:20 `TODO(M5)`). Windows Service로 실행 시 서비스 호스트로, 콘솔로 실행 시 콘솔로 동작하는 표준 패턴(`UseWindowsService`는 비-서비스 컨텍스트에서 no-op이라 콘솔/테스트 무파손이 기본이나, **WebApplicationFactory 테스트 호스트에서 부작용이 없음을 명시 확인**). `Microsoft.Extensions.Hosting.WindowsServices` 패키지 참조 추가(`src/Wcs.Api/Wcs.Api.csproj`).
- **WHERE**: `src/Wcs.Api/Program.cs` + `src/Wcs.Api/Wcs.Api.csproj`.

### 3.4 서비스 등록 스크립트
- **WHAT**: Windows Service 등록/해제 스크립트(`sc.exe create … binPath= …` 또는 PowerShell `New-Service`). 서비스 이름·실행 경로·시작 모드(자동)·설명 포함. 운영 배포 경로·계정은 플레이스홀더 + 주석.
- **WHERE**: 신규 — `scripts/`(예: `scripts/install-service.ps1`·`scripts/uninstall-service.ps1`) 또는 프로젝트 관행에 맞는 위치(Generator 재량). 사용법 상세 문서화는 P4(README)로 이연.

### 3.5 재시작 레지스터 재독 동기화 (확인 후 WHAT)
- **WHAT**: TASKS.md M5 "WCS 재시작 시 레지스터 재독 동기화". **코드 확인 결과**: 소터별 `PlcPollingService`가 `StartAsync`에서 폴 루프를 돌며 매 주기 `Latest` 스냅샷을 갱신한다(SorterRegistryFactory.StartAsync가 기동 시 폴링 시작). 즉 콜드스타트 시 게이트웨이가 이미 현재 레지스터값을 재독한다. **추가 동기화가 필요한지(예: 기동 직후 첫 스냅샷이 채워지기 전 들어온 IF-09/IF-10이 stale snapshot으로 오동작하는지) Generator가 코드로 확인**해 (a) 이미 충족이면 그 근거를 sprint-feedback에 명시하고 무변경, (b) 갭이 있으면 최소 WHAT(예: 기동 시 첫 폴 완료 대기 게이트)을 추가. **새 동기화 메커니즘을 추측으로 만들지 말 것** — 갭 없으면 "이미 충족" 판정이 정답.

### 3.6 무변경 가드 (절대 건드리지 말 것)
- **판정 의미 불변**: `Wcs.Core`(DepositDecider·RegisterMap) 순수성·판정 로직(절대규칙 #1~#5, #8) 무변경.
- **Modbus 레지스터 맵**(D4 비트·D5·D6 의미·주소 오프셋) 무변경.
- **C/R 핸드셰이크**(HandshakeOrchestrator·PlcPollingService 본문) 무변경 — DI 배선만.
- **기존 테스트 배선 무변경**(원칙). §3.1의 게이트 배선 최소 예외만 허용(사유 명시 의무).
- **스키마 정의 무변경**: `WcsDbContext.OnModelCreating`·`Entities.cs`·`DbSeeder` 토폴로지·기존 마이그레이션(`Wcs.Migrations.*` 3개) 무변경. **새 마이그레이션 생성 금지**(P1은 스키마 변경 없음 — 기존 마이그레이션을 적용만 한다).
- **API 필드명·엔드포인트**(IF-05/09/10, `/api/v1/*`) 무변경.

---

## 4. Evaluation Criteria (가중치)

| # | 기준 | 가중치 | 합격선(Fresh evidence 필수) |
|---|------|--------|------|
| ① | **콜드스타트 자동 provision 정확성** | 30% | 빈 DB(또는 파일 없음)에서 startup이 Migrate로 스키마 생성 → dev 시드 적용 → 기동 성공. `ChuteCapacityService` 등 startup 서비스가 `no such table` 없이 동작. 실제 또는 통합 시나리오로 입증. |
| ② | **기존 테스트 회귀 0 (테스트 경로 무파손)** | 30% | `dotnet test` 전체 GREEN — baseline 수(≈146, Generator 실측 기록) 유지. 자동 Migrate가 테스트 in-memory SQLite 경로를 타지 않음을 입증(테스트 호스트에서 Migrate/시드 미실행 또는 무해함). |
| ③ | **호스팅 조건부 (콘솔/테스트 무파손)** | 15% | `UseWindowsService` 추가 후 콘솔 `dotnet run` 정상·WebApplicationFactory 테스트 정상. 서비스 컨텍스트 외 no-op 확인. |
| ④ | **dev 시드 게이트 (운영 안전)** | 15% | 운영 기본값에서 테스트 시드 미삽입(게이트 off 시 빈 스키마만). Development/플래그 on 시에만 시드. 게이트 키 appsettings 외부화(하드코딩 0). |
| ⑤ | **재시작 레지스터 재독 동기화** | 10% | §3.5 판정 — "이미 충족"이면 근거, 갭이면 최소 fix. 추측 신규 메커니즘 0. |

**감점 트리거**: 새 마이그레이션 임의 생성 / 테스트 배선 광범위 수정 / `DbSeeder` 토폴로지 변경 / 운영에 테스트 시드 무조건 삽입 / 판정·Modbus맵·핸드셰이크 본문 변경 / 빈 DB 라이브 기동 미입증.

---

## 5. Completion (Done — P1)

- `dotnet build` 경고 0 / 에러 0.
- `dotnet test` 전체 GREEN — baseline(≈146) 유지, 회귀 0.
- **콜드스타트→정상 시나리오 통과**: 빈 DB → 자동 provision(Migrate + dev 시드) → 기동 → IF-05/핸드셰이크 동작(실 또는 통합 테스트로 입증). 직전 크래시(`no such table: chute_detail`) 해소 입증.
- **라이브 `dotnet run`이 빈 DB에서 기동 성공** — 직전 E2E 발견 크래시가 재현되지 않음(orchestrator가 라이브 기동으로 육안 확인; APPROVED 후 step).
- 서비스 등록 스크립트 존재(`scripts/`).
- 변경은 Program.cs·startup 훅·appsettings 키·csproj 참조·스크립트에 국한(무변경 가드 §3.6 준수).

---

## 6. 미확정 사항 / 판정 (질문 또는 코드-확정)

- **OFFLINE 중 IF-05/08 응답 정책 — P3 이연이나 현 상태 판정**: IF-08(deposit-permission 폴링)은 RCS 재설계로 **폐지**됨(Program.cs:132, ApiIntegrationTests `If08_DepositPermission_Removed_Returns404Or405`). 현재 IF-08은 WCS→RCS **아웃바운드 push**(`DestinationStatusPusher`)로 대체됐고, push는 `DestinationStatusService.Compute().Ready`를 전이 기준으로 산출한다 — OFFLINE(소켓 끊김/폴 실패)은 스냅샷 `Online=false`로 ready 산출에 반영되어 RCS에 ready=false로 전달된다. **따라서 "OFFLINE 중 IF-08 응답 정책"은 재설계로 이미 충족**(별도 IF-08 응답 경로 없음). 남는 것은 "OFFLINE 소터에 대한 **IF-05** dispatch NG 동작"이며, 이는 P3에서 `DestinationStatusService`/IF-05 NG 필터 코드를 확인해 "이미 충족 vs 추가"를 판정한다. **P1 스코프 아님** — 여기 기록만.
- **콜드스타트 Migrate vs 테스트 EnsureCreated 게이트 메커니즘**: Generator가 결정(환경 변수 `ASPNETCORE_ENVIRONMENT` / 설정 플래그 / 테스트의 기존 구성 신호 등). 이미 모든 테스트 팩토리가 `ConfigureServices`로 DB descriptor를 교체하므로, 자동 Migrate를 "DI에 등록된 DbContext가 in-memory가 아닐 때만" 또는 "설정 플래그로만" 조건화하는 등 다수 안이 있음 — 방법 선택·정당화는 Generator·Evaluator 루프에서.
- **시드 게이트 기본값**: 운영 안전(off)이 기본. dev에서 자동 on 하는 트리거(Development 환경 여부)는 Generator 결정.
- **이 외 추측 금지**: 새 동기화·새 마이그레이션·새 판정은 만들지 않는다.

---

## 7. Verification Scenarios (타입 슬롯: 콜드스타트 + 회귀)

| VS | 시나리오 | 입증 방식 |
|----|----------|----------|
| VS-1 | 빈 DB 콜드스타트 → Migrate로 스키마 생성 → dev 시드 → 기동 성공 | 통합 테스트(임시 파일/빈 SQLite로 실 startup 경로) 또는 라이브 기동 로그 |
| VS-2 | 콜드스타트 후 IF-05/핸드셰이크 정상 — `chute_detail` 등 테이블 존재 | 통합 또는 라이브 |
| VS-3 | 기존 5개 팩토리 테스트 경로 무파손 — in-memory SQLite + EnsureCreated + DbSeeder 그대로, Migrate 미실행 | `dotnet test` 전체 GREEN |
| VS-4 | `UseWindowsService` 추가 후 콘솔 실행·테스트 호스트 정상(no-op 확인) | `dotnet run` 콘솔 기동 + `dotnet test` |
| VS-5 | 시드 게이트 off(운영 기본) → 테스트 시드 미삽입(빈 스키마만) | 통합(게이트 off 분기) |
| VS-6 | 재시작 레지스터 재독 — 기동 후 첫 스냅샷 채워짐(§3.5 판정 결과 반영) | 통합 또는 코드 근거 |

---

## 8. Parallel / Eval Dimensions

- **Parallel Modules**: 단일 모듈(Program.cs startup 중심) — 기본 1 Generator. 굳이 팬아웃 불필요(콜드스타트·호스팅·시드가 모두 같은 startup 경로에 수렴).
- **Evaluation Dimensions**: ①provision 정확성 ②회귀 0 ③호스팅 조건부 ④시드 게이트 ⑤재독 — 단일 Evaluator가 5차원 모두 fresh evidence로 검증(전문가 풀 팬아웃 불요).
- **스케일링**: 기본 1/1/1 유지. (스코프가 startup 단일 경로로 좁고, 최우선 리스크가 "테스트 회귀"라 분산보다 단일 책임 추적이 유리.)

---

## 9. Planner self-check

- [x] WHAT/WHERE/검증만 기술 — 구현 방법(게이트 메커니즘·훅 시그니처·플래그 키 이름) 미결정, Generator 위임 명시.
- [x] 필수 선행 직접 읽음: TASKS.md M5 / CLAUDE.md / SPEC §7·§7-A / lessons.md / todo.md(이연 전부) / Program.cs(호스팅·DI·DbContext 등록·TODO 마커) / WcsDbContext·DbSeeder / Migrations.{Sqlite,SqlServer}(각 3 마이그레이션 확인) / appsettings.json / 5개 테스트 팩토리 DB 배선(ApiIntegrationTests·E2EInfrastructure·ScenarioTests·RcsPushTests + grep 전수).
- [x] 최우선 제약 = **테스트 146 무파손**을 ②30% 가중치 + 무변경 가드 + VS-3로 3중 고정.
- [x] M5 페이즈 분할안 상단(§0) 요약 + 서비스 스크립트 배치 제안(P1 포함, README는 P4).
- [x] OFFLINE 정책을 코드로 판정 — "IF-08 부분 재설계로 이미 충족, IF-05 부분만 P3 확인" 명시(재질문 회피).
- [x] 라이브 M5 포함·M5 P1 우선은 재질문 안 함(사용자 확정).
- [x] 추측 금지 항목(새 마이그레이션·새 동기화·새 판정) 명문화.
- [ ] **사용자 확인 대기**: 페이즈 분할안 + P1 스코프 승인 → 승인 후 Generator↔Evaluator 루프 진입.
