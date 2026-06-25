# Sprint Contract — S-FOLDER-ORG (src 폴더 구조 정리 / 순수 파일 이동)

> 작성: Planner Subagent · 2026-06-25 · 사용자 확정 3건 반영
> 성격: **순수 구조 정리 스프린트 — 동작 보존(behavior-preserving) 리팩터링.**
> 핵심 산출물 = "어느 파일을 어디로 옮길지"의 매핑 표. 코드 본문/네임스페이스/로직 변경은 0.
> 사용자 확인 대기 (Phase 1 게이트).

---

## Goal

`src/**` 전체를 **역할별 폴더로 정리**한다. Wcs.Api는 MVC 레이어(Controllers/Services/Repositories/Dtos/Infrastructure)로 그룹핑하고,
실익이 있는 다른 프로젝트(PlcGateway의 Modbus 어댑터군)도 폴더로 그룹핑한다.
**평면 네임스페이스를 유지** — 모든 파일의 네임스페이스 선언은 기존 그대로(`Wcs.Api`, `Wcs.PlcGateway`, …),
따라서 이번 작업은 **순수 파일 이동(`git mv`)** 이며 네임스페이스·using·코드 본문은 **단 1줄도 바뀌지 않는다**.
빌드·테스트·런타임 동작은 이동 전후 완전히 동일해야 한다.

비-goal(명시적 제외): 네임스페이스 재구성, using 정리, 코드 리팩터링, 로직 변경, 테스트 단언 변경,
Modbus 레지스터맵/핸드셰이크/판정 로직 의미 변경, EF 마이그레이션/스냅샷/디자인타임 변경, `.sln` 구조 변경, 신규 파일 생성, 파일 분할.

---

## 사전 조사 결과 (Generator는 이 사실에 의존해도 됨 — Planner가 직접 확인)

1. **모든 7개 csproj가 SDK-style 암묵 글로빙**이다 (`<Project Sdk="Microsoft.NET.Sdk[.Web]">`).
   어떤 csproj에도 `<Compile Include>` / `<Compile Remove>` 항목이 **없다**.
   → **하위 폴더로 파일을 옮겨도 csproj 편집이 전혀 필요 없다.** SDK가 `**/*.cs`를 자동 포함한다.
   (`obj/`,`bin/`은 SDK가 자동 제외 — 이동 대상 아님.)
2. **`.sln`(Wcs.sln)은 `.csproj` 경로만 참조**하고, `.csproj` 파일들은 **이동하지 않는다**(프로젝트 루트 유지).
   → `.sln` 편집 불필요. 솔루션 폴더 구조(NestedProjects)도 무변경.
3. **`Directory.Build.props` / `.editorconfig` 없음** — 경로 기반으로 파일을 글로빙/제외하는 설정이 솔루션에 존재하지 않는다.
4. **`Program.cs`(top-level statements) + `ProgramPartial.cs`(`public partial class Program {}`)는 프로젝트 루트 유지(확정).**
   - top-level statements는 컴파일 단위 하나에만 존재 가능(CS8803). 위치는 컴파일 동작에 영향 없으나 루트 유지가 사용자 확정.
   - `Program.cs`는 파일-스코프 네임스페이스가 없고(top-level), 내부에 `SorterRegistryFactory`/`SorterConfig`/`SorterTimingOverride`/`TimingOptions`/`PlcPollingHostedAdapter` 클래스가 **같은 파일에 동거**한다. 이 클래스들을 별도 파일로 쪼개는 것은 "파일 분할"이며 **본 스프린트 범위 밖**(순수 이동만). `Program.cs`는 통째로 루트 유지.
5. **네임스페이스 invariant 확인됨**(이동 후 grep이 이동 전과 동일해야 함):
   - Wcs.Api: `RcsController.cs`만 `namespace Wcs.Api.Controllers;`, 나머지 12개 전부 `namespace Wcs.Api;` (Program/ProgramPartial은 파일-스코프 네임스페이스 없음 — top-level/partial).
   - Wcs.PlcGateway: 6개 전부 `namespace Wcs.PlcGateway;`
   - 폴더는 디렉터리일 뿐 — 네임스페이스는 폴더와 무관하게 기존 선언을 그대로 둔다(C# 평면 네임스페이스 허용).

---

## Implementation Scope (WHAT / WHERE) — 프로젝트별 파일→폴더 매핑 표

> 규칙(전 항목 공통):
> - **반드시 `git mv <현재경로> <목표경로>`** 를 사용한다(rename 이력 보존 + git rename 감지). 일반 mv+add 금지.
> - 목표 폴더가 없으면 `git mv`가 자동 생성. (필요 시 Generator가 mkdir 후 `git mv` 해도 무방하나 git이 add/delete가 아닌 rename으로 감지되어야 함.)
> - **파일 본문은 0줄 변경**. 네임스페이스·using·주석·로직 손대지 않는다.
> - **csproj/.sln 편집 0** (SDK 글로빙 확인됨 — 사전조사 #1,#2).
> - 파일명 자체는 변경하지 않는다(폴더만 바뀜).

### ① Wcs.Api — MVC 레이어 (사용자 확정, 그대로 적용) — **이동 대상**

현재 모든 `.cs`가 `src/Wcs.Api/` 평면에 있음(RcsController만 이미 Controllers/). 아래로 그룹핑:

| 현재 경로 | 목표 경로 | 비고 |
|---|---|---|
| `src/Wcs.Api/Controllers/RcsController.cs` | `src/Wcs.Api/Controllers/RcsController.cs` | **이미 위치 정확 — 이동 없음(유지)** |
| `src/Wcs.Api/DestinationStatusService.cs` | `src/Wcs.Api/Services/DestinationStatusService.cs` | Services/ |
| `src/Wcs.Api/DestinationStatusPusher.cs` | `src/Wcs.Api/Services/DestinationStatusPusher.cs` | Services/ |
| `src/Wcs.Api/ChuteCapacityService.cs` | `src/Wcs.Api/Services/ChuteCapacityService.cs` | Services/ |
| `src/Wcs.Api/SorterCellQty.cs` | `src/Wcs.Api/Services/SorterCellQty.cs` | Services/ |
| `src/Wcs.Api/RcsPushClient.cs` | `src/Wcs.Api/Services/RcsPushClient.cs` | Services/ |
| `src/Wcs.Api/Repositories.cs` | `src/Wcs.Api/Repositories/Repositories.cs` | Repositories/ (인터페이스) |
| `src/Wcs.Api/DbRepositories.cs` | `src/Wcs.Api/Repositories/DbRepositories.cs` | Repositories/ (EF 구현) |
| `src/Wcs.Api/Dtos.cs` | `src/Wcs.Api/Dtos/Dtos.cs` | Dtos/ |
| `src/Wcs.Api/SorterGatewayRegistry.cs` | `src/Wcs.Api/Infrastructure/SorterGatewayRegistry.cs` | Infrastructure/ |
| `src/Wcs.Api/WcsTeardownGuard.cs` | `src/Wcs.Api/Infrastructure/WcsTeardownGuard.cs` | Infrastructure/ |
| `src/Wcs.Api/WcsOptions.cs` | `src/Wcs.Api/Infrastructure/WcsOptions.cs` | Infrastructure/ |
| `src/Wcs.Api/Program.cs` | `src/Wcs.Api/Program.cs` | **루트 유지(확정)** |
| `src/Wcs.Api/ProgramPartial.cs` | `src/Wcs.Api/ProgramPartial.cs` | **루트 유지(확정)** |

이동 파일 수: **11개** (RcsController·Program·ProgramPartial은 제자리 유지).

### ② Wcs.PlcGateway — Modbus 어댑터 그룹핑 (실익 있음, 사용자 제시 방향) — **이동 대상**

| 현재 경로 | 목표 경로 | 비고 |
|---|---|---|
| `src/Wcs.PlcGateway/IModbusMaster.cs` | `src/Wcs.PlcGateway/Modbus/IModbusMaster.cs` | Modbus/ |
| `src/Wcs.PlcGateway/ModbusMasterFactory.cs` | `src/Wcs.PlcGateway/Modbus/ModbusMasterFactory.cs` | Modbus/ |
| `src/Wcs.PlcGateway/ModbusTcpMaster.cs` | `src/Wcs.PlcGateway/Modbus/ModbusTcpMaster.cs` | Modbus/ |
| `src/Wcs.PlcGateway/ModbusRtuMaster.cs` | `src/Wcs.PlcGateway/Modbus/ModbusRtuMaster.cs` | Modbus/ |
| `src/Wcs.PlcGateway/PlcGateway.cs` | `src/Wcs.PlcGateway/PlcGateway.cs` | **루트 유지** (폴링/큐 코어) |
| `src/Wcs.PlcGateway/HandshakeOrchestrator.cs` | `src/Wcs.PlcGateway/HandshakeOrchestrator.cs` | **루트 유지** (핸드셰이크 코어) |

이동 파일 수: **4개** (Modbus 어댑터 4종을 `Modbus/`로). PlcGateway·HandshakeOrchestrator는 게이트웨이 코어로 루트 유지.

### ③ Wcs.Core — **폴더 없음 / 유지 권장** (이동 0)

2파일(`DepositDecider.cs`, `Models.cs`). 응집도 높고 폴더 실익 미미 → **무변경 권장**(사용자 확정 3: "2~3파일 프로젝트는 최소화/유지").
판정 엔진 순수 함수 핵심 — 위치 변경 무의미.

### ④ Wcs.Data — **폴더 없음 / 유지 권장** (이동 0)

3파일(`WcsDbContext.cs`, `Entities.cs`, `DbSeeder.cs`). EF 디자인타임 민감(마이그레이션 두 프로젝트가 `WcsDbContext`를 ProjectReference로 모델 참조) →
파일 이동 자체는 디자인타임에 무해하나(타입은 폴더 무관) **실익 < 위험**. **무변경 권장**.

### ⑤ Wcs.Sim3ds — **폴더 없음 / 유지 권장** (이동 0)

2파일(`Program.cs`, `SimServer.cs`). 시뮬레이터 — 폴더 실익 없음. **무변경 권장**.

### ⑥ Wcs.Migrations.Sqlite / Wcs.Migrations.SqlServer — **무변경(확정)** (이동 0)

이미 `Migrations/` 구조 + EF 디자인타임 팩토리(`*DesignTimeFactory.cs`)·스냅샷·`<RootNamespace>` 민감.
사용자 확정 3: **무변경 권장**. 어떤 파일도 옮기지 않는다.

### ⑦ tests/Wcs.Tests — **범위 밖** (사용자 요청은 src 한정)

이동 0. 단 Completion에서 "테스트가 이동된 타입을 여전히 발견(컴파일·실행)"을 검증해야 함.

---

## 절대 무변경 (위반 시 즉시 FAIL)

- **코드 본문·네임스페이스 선언·using 지시문·주석·로직·테스트 단언** — 0줄 변경.
  (이동 파일의 `git diff -M` 내용 hunk는 비어 있어야 한다 — rename only.)
- **Modbus 레지스터맵·핸드셰이크·판정 로직의 의미** — 0 변경(파일 위치만 바뀜).
- **EF 마이그레이션/스냅샷/디자인타임 팩토리** — Migrations 두 프로젝트 전체 무변경. `WcsDbContext`/`Entities`도 무변경 권장(Data 유지).
- **csproj · .sln · appsettings · 패키지 참조** — 0 변경(사전조사로 불필요 확인됨).
- **CLAUDE.md 절대규칙(PLC 단일 큐, TgtFloor 가드, Ready 의미 등)** — 코드 의미 무변경이므로 자동 보존.

---

## Evaluation Criteria (가중치)

| # | 기준 | 가중치 | 판정 방법 |
|---|---|---|---|
| ① | **동작 보존** | ★★★ | `dotnet build` 경고 0·오류 0 · `dotnet test` 전체 GREEN(이동 전 통과 수와 동일) · 테스트 호스트 exit 0(teardown hang 0) · 회귀 0 |
| ② | **순수 이동 입증** | ★★★ | `git status --find-renames`(또는 `git diff -M --stat`)가 **rename(R)만** 표시, add/delete 아님 · 이동 파일 내용 diff 0(rename hunk 비어 있음) · 네임스페이스 선언 grep이 이동 전후 **완전 동일** |
| ③ | **MVC 레이어 정확성** | ★★ | Wcs.Api 11개 파일이 약속된 폴더(Services/Repositories/Dtos/Infrastructure)에 정확히 배치 · Controllers/RcsController 유지 · Program/ProgramPartial 루트 유지 · PlcGateway Modbus 4종이 `Modbus/`에 |
| ④ | **csproj/솔루션 무결성** | ★★ | csproj·.sln **편집 0**으로도 빌드/테스트 발견 정상(SDK 글로빙 검증) · 빌드 산출물 동일 |
| ⑤ | **문서/참조 정합** | ★ (문서, 비차단) | CLAUDE.md "솔루션 구조"가 새 폴더와 **모순되면** 정정. (현 "솔루션 구조"는 프로젝트별 역할만 기술하고 내부 폴더는 언급 안 함 → 모순 없음. 선택적 보강만, 동작 아님) |

---

## Completion Conditions (Evaluator PASS 최소 조건 — 전부 충족)

1. `dotnet build` → **경고 0 / 오류 0**.
2. `dotnet test` → **전체 GREEN** (이동 직전 통과 테스트 수와 동일, 0 회귀) · 테스트 프로세스 **exit 0**(hang/crash 0 — lessons/todo의 teardown 채널 경쟁 회귀 없음).
3. `git status --find-renames`(또는 `git status` + `git diff -M`)에 **신규(??)/삭제(D) 단독 항목이 없고 rename(R)만** — 즉 모든 이동이 rename으로 감지.
4. 이동된 모든 파일의 **내용 diff가 0**: `git diff -M` 에서 이동 파일에 `+`/`-` 본문 라인이 나오면 FAIL(rename hunk만 허용).
5. **네임스페이스 선언 grep 불변**: 이동 전후 `^namespace` grep 결과(파일별 선언 문자열)가 동일.
   - 이동 전 기준값(사전조사 #5): Wcs.Api = 11×`namespace Wcs.Api;`(이동) + 1×`namespace Wcs.Api.Controllers;`(RcsController, 유지) + Program/ProgramPartial 2개(네임스페이스 없음) / PlcGateway = 6×`namespace Wcs.PlcGateway;`.
6. **EF 디자인타임 무영향**: Migrations 두 프로젝트 컴파일 성공. (선택 강검증: 인프라 가능 시 `dotnet ef migrations list` 또는 `dotnet ef dbcontext info`가 이동 전과 동일 결과.)
7. Migrations 두 프로젝트 + Core/Data/Sim3ds(유지 권장 프로젝트)는 **git status에 어떤 변경도 없어야** 함(무변경 확약).

---

## Parallel Modules (optional)

N/A (단일 정리 작업). 프로젝트별 폴더 이동은 논리적으로 독립적이나 규모가 작고(이동 15파일) 순차 `git mv` + 단일 빌드/테스트가 더 안전 — 병렬화 실익 없음. 기본 1 Generator.

## Evaluation Dimensions (optional)

functional only (동작 보존 + 순수성 입증). 보안/성능 표면 변화 0(코드 의미 무변경) → 단일 차원 검토. 기본 1 Evaluator.

---

## Detected Project Type: **Backend/API**

(프로젝트 신호: 서버측 컨트롤러 `Controllers/RcsController.cs` + ASP.NET Core 진입점 `Program.cs` 존재, 브라우저 향 UI 트리(HTML 셸/클라이언트 렌더 뷰) 부재. 사용자 표현이 아닌 repo 구조로 판별.)

---

## Verification Scenarios (Backend/API — 본 스프린트 표면 = 파일 이동 + 빌드/테스트 보존 + 순수성 입증)

> 본 스프린트는 HTTP 엔드포인트 동작을 **변경하지 않는다**(순수 이동). 따라서 엔드포인트 행위 검증의 목적은
> "이동 후에도 동일하게 동작/컴파일됨" 입증이다. 시나리오는 이동 표면(빌드·테스트·git 순수성·디자인타임)에 맞춰 N=8로 결정.

### Backend/API 슬롯

**(a) 이번 스프린트가 건드린 엔드포인트 목록 (method + path)** — *행위 무변경, 코드 위치만 이동*:
- IF-05 계열(투입 가부), IF-09(도착 보고), IF-10(핸드셰이크) — 전부 `Controllers/RcsController.cs` 내. **라우트/시그니처 무변경, 핸들러 파일도 무변경(RcsController 유지)**.
  핸들러가 의존하는 Services/Repositories/Dtos/Infrastructure 타입들의 **파일이 이동**할 뿐 → 컴파일·DI 해석이 이동 후에도 동일해야 함.
- 정확한 라우트 문자열은 Generator/Evaluator가 `Controllers/RcsController.cs`에서 확인(본 스프린트는 라우트를 바꾸지 않으므로 "이동 전 == 이동 후"가 검증 포인트).

**(b) 엔드포인트별 happy path (기대 입력 → 기대 출력 shape)** — *이동 전후 불변*:
- 기존 통합 테스트(`tests/Wcs.Tests`의 WebApplicationFactory 기반 IF-05/09/10 시나리오 + Sim3ds 연동)가 **이동 전과 동일하게 GREEN**. 응답 shape(필드 `pId,agvNo,barcode,inductionNo,chuteNo,qty,timeStamp` 등)·상태코드 불변.
- DI 컨테이너 구성(`Program.cs`의 AddScoped/AddSingleton/AddHostedService 배선)이 이동된 타입(`DestinationStatusService`,`RcsPushClient`,`SorterRegistryFactory` 등)을 여전히 해석 → 앱 부팅 성공(WebApplicationFactory 기동).

**(c) 엔드포인트별 관련 에러 케이스 (적용되는 것만 — 패딩 금지)** — *이동 전후 불변*:
- 기존 테스트에 존재하는 검증실패 400 · FULL/PAUSED NG · OFFLINE 결과 경로가 **이동 후에도 동일 결과**. (본 스프린트는 새 에러 케이스를 도입하지 않음 — 기존 테스트의 통과/실패 분포가 0 변화.)

### 구조-정리 전용 추가 시나리오 (이 스프린트의 본질 표면)

1. **빌드 무결성**: clean 후 `dotnet build` → 경고 0/오류 0. (csproj 미편집으로도 성공 = SDK 글로빙 검증.)
2. **테스트 보존**: `dotnet test` 전체 GREEN, 통과 수 = 이동 전 통과 수, 프로세스 exit 0(teardown hang 0).
3. **git rename 순수성**: `git diff -M --stat` / `git status --find-renames` 가 이동 파일을 전부 rename(R100 등)으로 표시, 내용 hunk 0.
4. **네임스페이스 불변**: 이동 전후 `^namespace` grep 결과 동일(사전조사 #5 기준값과 일치).
5. **Wcs.Api MVC 배치 확인**: 11개 이동 파일이 Services/Repositories/Dtos/Infrastructure에 정확 배치, Controllers/Program/ProgramPartial 위치 정확.
6. **PlcGateway Modbus 배치 확인**: 4종 어댑터가 `Modbus/`에, PlcGateway/HandshakeOrchestrator 루트 유지.
7. **EF 디자인타임 무영향**: Migrations 두 프로젝트 컴파일 성공 + (인프라 가능 시) `dotnet ef` 메타 조회가 이동 전과 동일. Migrations/Core/Data/Sim3ds git status 무변경.
8. **테스트 프로젝트의 타입 발견**: `tests/Wcs.Tests`가 이동된 `Wcs.Api.*` 타입(DTO·Service·Registry 등)을 여전히 참조·컴파일·실행(평면 네임스페이스 유지로 using 변경 불필요).

---

## 미확정/질문 (확정 3건은 재질문 안 함)

- **(확인 요청, 비차단)** Wcs.Api `Services/` 그룹에 `SorterCellQty.cs`·`RcsPushClient.cs`를 포함했다(둘 다 서비스성 코드). 사용자 명시 목록 "DestinationStatusService·DestinationStatusPusher·ChuteCapacityService·SorterCellQty·RcsPushClient" 5개를 Services로 지정 → 그대로 반영. 이견 없으면 진행.
- **(제안, 비차단)** Core(2)·Data(3)·Sim3ds(2)는 폴더 실익 < 도입 비용으로 **무변경 권장**. 사용자가 "전체 src honoring"을 원하면 이 셋도 그룹핑 가능하나, 무의미한 폴더 강제 금지 원칙(확정 3)에 따라 유지를 기본값으로 둠. 강제 그룹핑 원하면 알려줄 것.

---

> Planner self-check — Detected project type: Backend/API. Required scenario slots: 8 (endpoints-touched, happy-path-per-endpoint, error-cases-per-endpoint, build-integrity, test-preservation, git-rename-purity, namespace-invariance, type-discovery — MVC/Modbus 배치·EF-디자인타임 보강 포함). All slots filled: yes.
