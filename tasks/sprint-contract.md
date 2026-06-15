# Sprint Contract — S-M0 (솔루션 구성 + 빌드 그린)

## Goal
기존 6-프로젝트 스켈레톤을 빌드·테스트 가능한 .NET 솔루션으로 배선한다.
이 스프린트 후 `dotnet build` 성공 + `dotnet test` 실행 → 프로젝트가 정의한 "정상 시작점" 도달:
**DepositDecider.Decide 판정 9케이스 RED(NotImplementedException) / Wire_Strings_AreStable 1건 GREEN**.
비즈니스 로직은 작성·변경하지 않는다. M1~M5는 별도 스프린트.

## Implementation Scope (Generator가 만들 것)
1. 루트에 `Wcs.sln` 생성 (`dotnet new sln -n Wcs`)
2. 6개 프로젝트 sln 추가: Wcs.Core, Wcs.PlcGateway, Wcs.Api, Wcs.Data, Wcs.Sim3ds, Wcs.Tests
3. 프로젝트 참조(추가 참조 금지):
   - Wcs.Api → Wcs.Core, Wcs.PlcGateway, Wcs.Data
   - Wcs.PlcGateway → Wcs.Core
   - Wcs.Data → Wcs.Core
   - Wcs.Tests → Wcs.Core
4. NuGet 패키지(추가 금지):
   - Wcs.PlcGateway → FluentModbus
   - Wcs.Sim3ds → FluentModbus
   - Wcs.Tests → xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk
5. TargetFramework은 **net10.0 유지**(SDK 10.0.300 설치됨 — 변경 불필요). 빌드가 net10.0에서 실패함이 증명될 때만 다운그레이드 검토.
6. `dotnet build` 성공 + `dotnet test`로 9 RED / 1 GREEN 분리 확인, 원문 요약을 증거로 보고.

## Out of Scope (이번 스프린트 손대지 말 것)
- `DepositDecider.Decide`는 NotImplementedException 스텁 유지(9 RED가 성공 신호). M1 로직 구현 금지.
- 스켈레톤 로직/시그니처 변경 금지: Models.cs, DepositDecider.cs, DepositDeciderTests.cs, Program.cs(Api), Dtos.cs, appsettings.json, PlcGateway.cs, Sim3ds/Program.cs, Entities.cs — 내용 변경 0
  (허용 편집: dotnet CLI가 .csproj에 기록하는 참조/패키지 항목 + 새 Wcs.sln 뿐)
- DTO 정정(A1/A2/A3/A7)=M3, Sim3ds 동작(B1/B2)=M2, 판정 경계 테스트(C1~C3)=M1, EF Core=M4, Windows Service/Serilog=M5
- 새 테스트 케이스 추가 금지(총 10건 고정: Decide 9 + Wire 1)
- Directory.Build.props / 중앙 패키지 관리 도입 금지(검증에서 기각된 항목)

## Detected Project Type: Backend/API
근거: src/Wcs.Api가 ASP.NET Core 서버 진입점(Program.cs의 WebApplication + app.MapPost 핸들러 3종) + 클래스 라이브러리(Core/PlcGateway/Data) + xUnit 테스트. 브라우저 UI 트리 없음.

## Evaluation Criteria (Backend/API)
1. **Architecture (★★★)**: 참조 그래프가 지정 방향과 정확히 일치, 순환·잉여 참조 0. Wcs.Core 프로젝트 참조 0개. Wcs.Sim3ds는 프로젝트 참조 없음(FluentModbus 패키지만). sln에 정확히 6개 프로젝트.
2. **Craft (★★)**: 패키지가 올바른 프로젝트에만(FluentModbus가 Core/Api/Data로 누출 안 됨, 테스트 패키지는 Wcs.Tests에만). 배선으로 인한 빌드 경고 0. TargetFramework net10.0 유지.
3. **Functionality (★★)**: `dotnet build Wcs.sln` exit 0. `dotnet test`가 xUnit 러너 로드 → Total 10 / Failed 9(Decide의 NotImplementedException) / Passed 1(Wire_Strings_AreStable).

## Completion Conditions (전부 필수)
- C-1. `Wcs.sln`이 루트에 존재하고 6개 프로젝트를 나열
- C-2. `dotnet build Wcs.sln` exit 0(솔루션 전체 그린)
- C-3. `dotnet test` 실행됨(러너 로드 — "no test host" 아님) + 요약 Passed=1, Failed=9, Total=10
- C-4. 통과 1건은 `Wire_Strings_AreStable`; 실패 9건은 Decide 케이스가 **NotImplementedException**으로 실패(컴파일 에러·단언 실패 아님)
- C-5. 참조 그래프·패키지 배치가 Scope와 정확히 일치(.csproj 검사로 확인)
- C-6. 스켈레톤 로직 무변경 — 스켈레톤 .cs/.json의 `git diff`에 내용 편집 없음(.csproj 참조/패키지 추가 + 새 .sln만 diff에 등장)

## Verification Scenarios (Backend/API)
이번 스프린트의 변경 표면은 솔루션/빌드 배선이며, 3개 엔드포인트는 501 스텁(M3 영역, 호출 안 함). 검증 대상을 빌드/테스트 툴체인 수준으로 둔다. N=3.

**엔드포인트(추적용 — M0에서 실행 안 함)**: POST /api/v1/destination-query(IF-05), /deposit-permission(IF-08), /deposit-report(IF-10) — 전부 501 스텁, 무변경.

**검증 대상 (V1~V3)**
- V1 (build): `dotnet build Wcs.sln` → exit 0, "Build succeeded", 에러 0. 증거=빌드 요약 인용.
- V2 (test runner + RED/GREEN): `dotnet test` → xUnit 러너 로드, 요약 "Failed: 9, Passed: 1, Total: 10". (프로세스 exit는 9 RED 설계상 비0이 정상 — **요약 형태로 판정**.) 증거=러너 요약 원문 인용.
- V3 (failure-mode): `dotnet test --filter Decider` → Decide 9건이 **System.NotImplementedException**으로 실패, Wire_Strings_AreStable은 이 필터 실패 집합에 없음. 증거=Decide 케이스 예외 타입 라인 + Wire 통과 라인 인용.

**Error cases (Evaluator가 적극 배제할 것)**
- E1: net10.0 SDK 불일치로 빌드 실패 → TargetFramework가 여전히 net10.0인지 확인(silent 변경 금지)
- E2: `dotnet test` "No test is available"/러너 없음 → 테스트 패키지 오배치 → FAIL
- E3: Decide가 컴파일 에러 또는 단언 실패로 실패(NotImplementedException 아님) → 스켈레톤 변경됨 → FAIL
- E4: Passed≠1 또는 Failed≠9 (Decide 구현됨/테스트 가감) → 범위 위반 → FAIL
- E5: 잉여·잘못된 프로젝트 참조 또는 패키지 누출(예: Core에 FluentModbus, Core가 타 프로젝트 참조) → FAIL

> Planner self-check — Detected project type: Backend/API. Required scenario slots: 3 (endpoints-touched list, happy-path-per-endpoint, error-cases-per-endpoint). All slots filled: yes.
