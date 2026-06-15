# Sprint Feedback — S-M0 (솔루션 구성 + 빌드 그린)

APPROVED

## 판정: APPROVED (재제출 후 — 2026-06-15)

M0-1(솔루션 파일명) 수정 확인. 모든 검증 시나리오를 계약 문자 그대로의 명령으로 재실행하여 PASS.

### 재검증 증거 (ground truth, 2차)
- 파일: 루트에 `Wcs.sln`(클래식 Format Version 12.00) 단독 존재, `.slnx` 제거됨. 6개 프로젝트 나열 확인 → C-1 PASS.
- V1: `dotnet build Wcs.sln` → 경고 0 / 오류 0 / exit 0.
- V2: `dotnet test Wcs.sln` → 러너 로드(테스트 파일 1개 일치), 실패 9 / 통과 1 / 전체 10.
- V3: `dotnet test Wcs.sln --filter Decider` → 9 Decide 케이스 전부 System.NotImplementedException, Wire_Strings_AreStable는 FAIL 집합에 없음.
- E1: net10.0 6개 csproj 전부 유지(다운그레이드 0).
- C-6: `git diff --stat '*.cs' '*.json'` 비어 있음. 변경 = 5개 csproj + 신규 Wcs.sln + 워크플로 산출물뿐. sln 재생성/프로젝트 재추가가 스켈레톤/참조 그래프를 훼손하지 않음.
- E5: 참조 그래프·패키지 배치 1차 검증과 동일하게 계약 정합(누출·잉여·순환 0).

---

## (1차) 판정: CHANGES REQUESTED (1건 must-fix) — 해소됨

빌드/테스트 실질 목표(빌드 그린 + 9 RED / 1 GREEN)는 **달성**. 그러나 계약의 산출물 이름·
검증 명령이 문자 그대로 충족되지 않음(C-1 + V1 명령). 아래 1건 수정 후 재제출 요망.

---

## Verification Scenarios — PASS/FAIL (Ground Truth)

환경: `dotnet --list-sdks` → `9.0.308`, `10.0.300` 설치 확인. net10.0 유효 (E1 배제).

### V1 (build) — 부분 FAIL (명령 문자열) / PASS (실질)
- **계약 문자 그대로** `dotnet build Wcs.sln` 실행 →
  ```
  MSBUILD : error MSB1009: 프로젝트 파일이 없습니다.
  스위치: Wcs.sln
  EXIT=1
  ```
  → 루트 파일이 `Wcs.slnx`(SDK 10.0.300 기본 XML 형식)이라 `Wcs.sln`이라는 파일은 존재하지 않음.
- **정규 명령(CLAUDE.md 기준)** `dotnet build` (파일명 생략, 자동 탐색) →
  ```
  빌드했습니다.  경고 0개  오류 0개   경과 시간: 00:00:05.16   EXIT=0
  ```
  6개 프로젝트 전부 빌드 성공. 실질 빌드 그린은 달성.

### V2 (test runner + RED/GREEN) — PASS
- `dotnet test` (자동 탐색) → 러너 로드됨(E2 배제: "No test is available" 아님).
- 요약: `실패: 9, 통과: 1, 건너뜀: 0, 전체: 10` → C-3/E4 정확히 일치.
- 9건 전부 `System.NotImplementedException : M1: DepositDecider.Decide — see docs/SPEC.md §2`
  (E3 배제: 컴파일 에러·단언 실패 아님). 통과 1건 = `Wire_Strings_AreStable`.

### V3 (failure-mode) — PASS
- `dotnet test --filter Decider` → 9개 Decide 케이스 전부 `System.NotImplementedException`으로 실패.
  - Row1_Ready_FloorMatch_Allows, Row2_FloorDiffer_TgtZero_WritesTgtFloor,
    Row3_FloorDiffer_TgtBusy_DoesNotOverwrite, Row4_Busy_SameFloor_StillBusy,
    Row4_Busy_TgtZero_PrewritesReturnFloor, Row5_Busy_TgtBusy_NoWrite,
    Row6_Hold_Denies_WithoutWrite(Full), Row6_Hold_Denies_WithoutWrite(Paused),
    Row7_Offline_OverridesEverything
- `Wire_Strings_AreStable`는 [FAIL] 집합에 **없음** → V3 충족.

---

## Error cases (적극 배제 결과)
- **E1 (net10.0 silent 다운그레이드)**: 배제. 6개 csproj 전부 `<TargetFramework>net10.0</TargetFramework>`,
  csproj diff에 TargetFramework 변경 0.
- **E2 (러너 없음)**: 배제. 러너 정상 로드, Total 10 인식.
- **E3 (컴파일/단언 실패)**: 배제. 9건 전부 NotImplementedException 스택트레이스
  (`DepositDecider.cs:line 15`).
- **E4 (Passed≠1 / Failed≠9)**: 배제. 정확히 Passed=1, Failed=9, Total=10.
- **E5 (잉여 참조 / 패키지 누출)**: 배제. csproj 정적 검사:
  - Wcs.Core: 프로젝트 참조 0, 패키지 0 ✓
  - Wcs.Api → Core, PlcGateway, Data (패키지 0) ✓
  - Wcs.Data → Core (패키지 0) ✓
  - Wcs.PlcGateway → Core; FluentModbus 5.3.2 ✓
  - Wcs.Sim3ds: 프로젝트 참조 0; FluentModbus 5.3.2 ✓
  - Wcs.Tests → Core; xunit 2.9.3 + xunit.runner.visualstudio 3.1.5 + Microsoft.NET.Test.Sdk 18.6.0 ✓
  - FluentModbus가 Core/Api/Data로 누출 안 됨, 테스트 패키지는 Wcs.Tests에만. 순환·잉여 0.
  - slnx에 정확히 6개 프로젝트.

---

## Completion Conditions
- C-1: **FAIL(문자 그대로)**. 계약은 "`Wcs.sln`이 루트에 존재"라 명시했으나 실제 파일은 `Wcs.slnx`.
  6개 프로젝트 나열은 충족. → 산출물 이름이 계약과 불일치.
- C-2: PASS(실질). `dotnet build` exit 0. 단, 계약 문자 그대로 `dotnet build Wcs.sln`은 MSB1009로 exit 1.
- C-3: PASS. 러너 로드 + Passed=1/Failed=9/Total=10.
- C-4: PASS. 통과 1건 Wire_Strings_AreStable, 실패 9건 NotImplementedException.
- C-5: PASS. 참조 그래프·패키지 배치가 Scope와 정확히 일치(E5 참조).
- C-6: PASS. `git diff --stat '*.cs' '*.json'` 비어 있음. 변경=5개 csproj의 참조/패키지 추가 +
  신규 Wcs.slnx + 워크플로 산출물(.claude/, tasks/sprint-log.md)뿐. csproj diff에 로직 편집 0.

---

## MUST-FIX (1건)
**M0-1 — 솔루션 파일명이 계약 산출물(C-1) 및 V1/V2 검증 명령과 불일치.**
- 사실: `dotnet new sln -n Wcs`(계약 지정 명령)가 SDK 10.0.300에서 `Wcs.slnx`(XML 형식)를 생성.
  계약의 C-1/V1은 `Wcs.sln`을 전제 → 문자 그대로 `dotnet build Wcs.sln` 실행 시 MSB1009 실패.
- 빌드/테스트 실질 목표는 자동 탐색(`dotnet build`/`dotnet test`)으로 달성되므로 **코드 결함 아님**.
  순전히 산출물 이름 vs 툴체인 출력 불일치이며, 생성자가 보고에서 이 발산을 명시하지 않음.
- **조치(택1, 권장 ①)**:
  1. `Wcs.slnx` → `Wcs.sln`(클래식 형식)으로 생성하여 계약 C-1 및 V1 명령을 문자 그대로 충족.
     예: 기존 slnx 제거 후 `dotnet new sln -n Wcs --format sln` 또는 6개 프로젝트 재추가.
     (자동 탐색을 깨지 않도록 루트에 단일 솔루션 파일만 남길 것.)
  2. `.slnx` 유지가 의도적이라면, 계약 C-1/V1을 `Wcs.slnx`로 정정하는 것에 대해 명시적 사유를
     sprint-log.md에 기록하고 사용자 승인을 요청. (이 경우 재계획 영역.)
- 재제출 시 검증: `dotnet build Wcs.sln`(또는 정정된 파일명) exit 0 + `dotnet test`로 9 RED/1 GREEN 재확인.

다른 모든 항목(빌드 그린, RED/GREEN 분리, 참조 그래프, 패키지 배치, 스켈레톤 무변경, net10.0 유지)은
PASS. 이 1건만 해소하면 APPROVED 가능.
