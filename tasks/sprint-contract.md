# Sprint Contract — S-DEV-SEED-GUARD (자동 시드 전면 차단 · 실사고 재발 방지)

> **미니 스프린트.** 2026-07-03 실사고(Development 기동 → dev 시드가 현장 SqlServer DB 오염 → SorterRegistry fail-loud로 전체 기동 거부; 감사 E⑤/A-4 실현) 재발 방지.
> **방향(사용자 확정)**: 자동 시드 전면 차단 — 시드는 **명시 설정(`SeedOnStartup=true`) 또는 스크립트로만**. **SQLite 테스트 더블은 유지**(속도 우선), 재발 방지는 **SQLite를 런타임 재도입하지 않고** 게이트 로직으로 해결.
> WHAT/WHERE/검증만 규정 — 정확한 시그니처·로그 문안은 Generator 재량(제약 내).

## 0. 메타

| 항목 | 값 |
|------|-----|
| Sprint ID | S-DEV-SEED-GUARD |
| Branch | `fix/dev-seed-guard` |
| Base | `develop` (PR #28까지 병합) |
| Detected Project Type | Full-stack (이 스프린트는 **backend 설정/시드-게이트 전용**) |
| Scaling | **1 Planner / 1 Generator / 1 Evaluator** (좁은 버그 수정 — 팬아웃 없음) |
| Test baseline | **161 GREEN** (완료 시 161 전원 GREEN 유지 + 신규 게이트 테스트 = 161+N) |
| 근거 감사 | `tasks/audit-20260701-full.md` A-4(MAJOR/docs, E⑤ 예측) · A-19(MINOR/docs, Development.json 허위 주석) |

## 1. 목표 (WHAT · 한 줄)

콜드스타트 자동 시드가 **환경(ASPNETCORE_ENVIRONMENT=Development)만으로 암묵 발동**하던 경로를 제거해, 시드는 **명시 `Database:SeedOnStartup=true`일 때만** 실행되도록 한다. `appsettings.Development.json`의 위험한 `SeedOnStartup=true`와 허위 주석을 바로잡고, 신규 회귀 테스트로 "Development 기동 + SeedOnStartup 미명시 → 시드 미실행"을 고정한다.

**사고의 본질**: 시드 게이트가 `seedOnStartup ?? IsDevelopment()`였다 — 즉 `SeedOnStartup` 미지정 시 **환경이 Development이면 자동 on**. Development.json은 `SeedOnStartup=true`를 켰고 **연결/Provider 오버라이드가 없어** base의 `Provider=SqlServer` + 현장 연결문자열(`Rcs3dsInterlockingWcs`)로 직행 → dev 시드가 **실 현장 DB에 주입**. 환경 기반 암묵 발동을 근절하는 것이 재발 방지의 본질이다.

## 2. Scope IN

### 2A. `backend/src/Wcs.Api/Startup/DbInitializer.cs` — 시드 게이트: 환경 암묵 발동 제거 (핵심)
- L89 `var seedEnabled = seedOnStartup ?? app.Environment.IsDevelopment();` → **`IsDevelopment()` fallback 제거**. 게이트는 **명시 `SeedOnStartup==true`일 때만 true**(null/false/미지정 = 시드 안 함).
- **게이트 판정을 순수 정적 함수로 추출**(예: `public static bool ShouldSeed(bool? seedOnStartup)` → `seedOnStartup == true`). I/O·`WebApplication`·DI 의존 0 — CLAUDE.md 절대규칙 #8(순수 함수·테스트가 스펙) 정신. `ProvisionAsync`는 이 함수를 호출하도록 배선.
  - ⚠ **회귀 테스트가 이 함수를 직접 호출한다**(§4-③). 전 인메모리 팩토리는 `IsInMemorySqlite`에서 `ProvisionAsync`를 조기 no-op하므로(L57-62) **게이트는 어떤 테스트로도 호스트 경유로는 관측 불가** — 추출 없이는 회귀 고정 불가.
- L87·L98-99·L103-106 로그·주석: 환경 트리거 서술 제거. else 브랜치 안내문에서 "또는 ASPNETCORE_ENVIRONMENT=Development" 삭제(`SeedOnStartup=true`만 안내).
- **(판단·경량 이중방어)** 시드 실행 직전, 대상이 **비 in-memory(실 파일/SqlServer)**이면 **눈에 띄는 WARNING 로그 1줄**(provider + 대상 DB/연결 요약) — Fail Loud. **거부(throw)는 하지 않음**(명시 `SeedOnStartup=true`는 정당한 요청이므로 차단하면 "명시 설정으로만" 방향과 모순). Generator가 과설계로 판단하면 로그 1줄로 최소화.

### 2B. `backend/src/Wcs.Api/appsettings.Development.json` — 위험 기본값·허위 주석 정정
- L5 `"SeedOnStartup": true` → **`false`** (또는 키 제거 — 게이트가 명시 true만 보므로 부재=시드 안 함). Development 기동이 자동으로 현장 DB를 시드하지 않도록.
- L2 허위 주석 정정(A-19): "dotnet run 기본 환경" → **"launchSettings.json 부재로 `dotnet run` 기본 환경은 Production. 이 파일은 ASPNETCORE_ENVIRONMENT=Development를 명시 설정한 경우에만 적용됨."**
- L4 `_comment_SeedOnStartup`: **경고 주석**으로 교체 — "자동 시드 금지(환경만으로 발동 안 함). dev 시드가 필요하면 SeedOnStartup=true를 명시하고, **반드시 Provider/ConnectionStrings를 dev 전용으로 오버라이드**(현장 SqlServer DB 오염 방지). base는 Provider=SqlServer·현장 연결문자열임."

### 2C. `backend/src/Wcs.Api/appsettings.json` — base 주석 동기화 (값 불변)
- L99 `_comment_SeedOnStartup`의 "명시 true 또는 ASPNETCORE_ENVIRONMENT=Development일 때만 시드. null(미지정)이면 Development 환경에서만 자동 on." → **"명시 `SeedOnStartup=true`일 때만 시드. 환경 기반 암묵 시드 없음(2026-07-03 현장 DB 오염 사고 재발 방지). null/미지정=시드 안 함."** `SeedOnStartup=false` 값은 불변.

### 2D. 신규 회귀 테스트 (`backend/tests/Wcs.Tests/`)
- 추출된 게이트 함수(`ShouldSeed`)를 직접 호출하는 xUnit 테스트를 **신규 파일 또는 기존 적합 파일**에 추가. 최소 고정:
  - `ShouldSeed(null) == false` — **미명시(Development 포함 어떤 환경이든) → 시드 안 함**(사고의 핵심 회귀 방지).
  - `ShouldSeed(false) == false`.
  - `ShouldSeed(true) == true` — 명시 경로만 시드.
- SQLite·호스트·DB 불요(순수 bool). 기존 인메모리 더블 배선은 **불변**(§3).

## 3. Scope OUT (0 변경 — 무변경 가드)

- **SQLite 테스트 더블 제거 없음.** 7개 인메모리 팩토리(`ApiIntegrationTests`·`RcsPushTests`·`MonitoringApiTests`·`ScenarioTests`×2·`E2EInfrastructure`)의 `UseSetting("Database:Provider","Sqlite")` + `EnsureCreated()` + `DbSeeder.Seed(...)` 배선 **불변**.
- **`DbSeeder.cs` 토폴로지 불변** — 슈트 1~5·SORTER_3D chuteNo=30·TEST-BARCODE-* 오더·셀·AGV 시드 데이터 **0 변경**.
  - 특히 `SeedWorkBatchAndOrders`의 `First(ChuteNo==1 && CHUTE)` 크래시(현장-토폴로지 DB에서 "Sequence contains no elements")는 **이 스프린트 OUT**. 자동 시드가 차단되면 사고 경로에선 도달 불가. → `tasks/todo.md`에 "DbSeeder First(ChuteNo==1&&CHUTE)는 명시 SeedOnStartup=true를 현장-토폴로지 DB에 걸면 여전히 크래시 — FirstOrDefault+skip 하드닝 검토"로 등재만.
- **`appsettings.json` Sorters[](ChuteNo=1)·Provider(SqlServer)·ConnectionStrings·기타 값 불변.**
- **마이그레이션 0** — 스키마·EF 모델 무접촉. `MigrateOnStartup` 경로 불변.
- **frontend 0** — `git diff -- frontend/` 빈 출력.
- **`Program.cs` 배선 불변**(L163 `ProvisionAsync(app)` 호출 위치·순서 그대로).

## 4. Deliverables & 검증 (Completion Gate)

> **Fresh evidence 의무**: 모든 PASS는 "지금 실제로 돌린" raw 출력(테스트 러너 요약·`dotnet run` 콘솔 로그·DB 카운트 쿼리·`git diff --stat`)을 `tasks/sprint-feedback.md`에 인용. Generator 보고·추정만으론 PASS 금지.

**① 전체 테스트 GREEN**
- `dotnet test backend/Wcs.sln` → **기존 161 전원 GREEN 유지 + 신규 게이트 테스트 GREEN**(합계 161+N). 실패 0. (게이트 fallback 제거는 인메모리 no-op 경로라 기존 테스트에 영향 0 — 이를 결과로 입증.)

**② 신규 회귀 테스트가 사고 핵심을 고정**
- `ShouldSeed(null)==false`(미명시→시드 안 함), `(false)==false`, `(true)==true` 3케이스 GREEN. raw 출력 인용.

**③ Development 기동 라이브 재현 — 실 DB에 시드 0행 (음성 재현)**
- ⚠ **안전 제약**: 현장 운영 DB(`Rcs3dsInterlockingWcs`)에 절대 붙이지 말 것. **빈 스크래치 SqlServer DB**(별도 DB명, ConnectionStrings 임시 오버라이드)로 재현하거나, 최소한 시드 삽입이 물리적으로 발생하지 않음을 로그로 입증.
- `ASPNETCORE_ENVIRONMENT=Development`로 `dotnet run --project backend/src/Wcs.Api` 기동 → 확인:
  - (a) 콘솔에 **"[DbInitializer] 시드 게이트 off …"** 로그 출현(시드 미실행).
  - (b) 대상 DB `destination`(및 시드 테이블) **행 0**(사고 시나리오의 오염이 발생하지 않음) — 카운트 쿼리 raw 인용.
  - (c) 빈 DB + 시드 off이므로 활성 SORTER_3D 부재 → **SorterRegistry fail-loud throw 없이 정상 기동**(사고의 기동 거부 증상도 소멸). 기동 로그 인용.
- **대조(사고 재현 확인용, 선택)**: 임시로 `SeedOnStartup=true` + 빈 스크래치 DB로 1회 기동 시 시드가 실행됨을 로그로 확인 → 명시 경로는 살아있음 입증(수행 후 원복).

**④ 무변경 가드 (스코프 격리 입증)**
- `git diff --stat` 판독 → 변경이 **`DbInitializer.cs` · `appsettings.Development.json` · `appsettings.json`(주석) · 신규 테스트 파일**에만 국한. `git diff -- frontend/` = 빈 출력. `git diff -- backend/src/Wcs.Data/DbSeeder.cs` = **빈 출력**(토폴로지 불변). 마이그레이션 디렉터리 diff 0. Sorters[]·Provider·ConnectionStrings diff 0.

**Completion**: ①~④ 전부 PASS + `tasks/todo.md`에 DbSeeder First 크래시 하드닝 항목 등재 + `tasks/lessons.md`에 "환경만으로 자동 시드 발동 = 실 DB 오염 벡터; 명시 설정으로만 · Development.json은 반드시 Provider/연결 오버라이드 동반" 교훈 1행 추가.

## 5. 함정 (Traps)

1. **호스트 경유 게이트 테스트 불가**: 전 인메모리 팩토리는 `IsInMemorySqlite`에서 `ProvisionAsync`를 조기 no-op(L57-62). `WebApplicationFactory`로 "시드 안 됨"을 관측하려 해도 애초에 시드 코드에 도달하지 않음 + 팩토리는 별도로 `DbSeeder.Seed`를 항상 호출. → **게이트를 순수 함수로 추출해 직접 단위 테스트**(§2A·§2D). 호스트 경유 관측 시도 금지.
2. **SQLite 런타임 재도입 금지**(사용자 확정): 회귀 테스트에 실 파일 SQLite/DB를 새로 붙이지 말 것 — 순수 bool 게이트로 충분. 인메모리 더블 배선도 불변(§3).
3. **키 제거 vs false**: Development.json에서 `SeedOnStartup`를 제거해도 게이트가 "명시 true만"이므로 안전(부재=시드 안 함). 단 base(false)를 상속하므로 명시 `false`가 의도를 더 뚜렷이 함 — Generator 재량이나 **절대 true로 남기지 말 것**.
4. **거부(throw)로 확대 금지**: 비 in-memory 시드에 대한 방어는 **WARNING 로그**까지. 명시 `SeedOnStartup=true`를 throw로 막으면 "명시 설정으로만 시드" 방향과 모순되고 정당한 dev 시드를 깨뜨림.
5. **현장 DB 접속 금지**: ③ 라이브 재현에서 base ConnectionStrings(`Rcs3dsInterlockingWcs`)에 그대로 붙지 말 것 — 빈 스크래치 DB로. 사고를 다시 재현시키지 말 것.
6. **DbSeeder 손대지 말 것**: First 크래시가 눈에 띄어도 이 스프린트는 게이트만 — 토폴로지·시더 로직 수정은 OUT(todo 등재로 이관). 무변경 가드 ④에서 DbSeeder diff 0 검증.

## 6. Planner Self-Check

- [x] **Scope IN** = DbInitializer 게이트(env fallback 제거 + 순수 함수 추출 + 로그/주석, 2A) · Development.json(SeedOnStartup=false + 허위 주석 2건 정정, 2B) · base appsettings 주석 동기화(2C) · 신규 순수 게이트 회귀 테스트(2D). 실독 근거: DbInitializer.cs L55-108·Development.json·appsettings.json L94-104·ApiIntegrationTests 팩토리 배선·audit A-4/A-19·grep(IsDevelopment/SeedOnStartup 전 참조).
- [x] **핵심 설계 판단**: 전 인메모리 팩토리가 ProvisionAsync를 조기 no-op → 게이트는 호스트 경유 관측 불가 → **순수 함수 추출 + 직접 단위 테스트**가 유일한 회귀 고정 경로(함정1). 사용자의 "인메모리 더블 게이트 검증" 의도를 순수 추출로 더 정확히 충족(SQLite 재도입 0).
- [x] **사용자 확정 반영**: ①SQLite 더블 유지(§3·함정2) ②자동 시드 전면 차단=env 암묵 발동 제거, 명시 true만(2A·②) ③재질문 없음.
- [x] **Scope OUT** = SQLite 제거 0 · DbSeeder 토폴로지/로직 0(First 크래시는 todo 이관) · Sorters[]/Provider/ConnectionStrings 0 · 마이그레이션 0 · frontend 0 · Program.cs 배선 0. 무변경 가드 ④ git diff로 입증.
- [x] **검증 4기준** 각 fresh 증거: ①161+N GREEN ②게이트 3케이스 ③Development 라이브 음성 재현(시드 0행·정상 기동·안전 제약) ④무변경 가드. Completion에 todo·lessons 등재 포함.
- [x] **후보 판정**: #1 IN(2B) · #2 IN(2A, fallback 제거=본질) · #3 IN-경량(WARNING 로그만, 거부 아님) · #4 IN(2D, 순수 추출로 재정의) · #5 OUT+todo(사용자 권장대로).
- [x] **절대규칙 무관**: PLC 쓰기/TgtFloor/Ready/타이밍 무접촉. 시드 게이트=순수 함수(#8 정신). backend 설정·기동 프로비저닝 한정.
- [x] **코드 구현 0** — WHAT/WHERE/VERIFY만. 정확한 함수 시그니처·로그 문안·키 제거 여부는 Generator 재량(제약 내).
