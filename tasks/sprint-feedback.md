# Sprint Feedback — S-M1 (판정 엔진 DepositDecider)

## 판정: APPROVED

Evaluator가 GROUND TRUTH(직접 명령 실행 + 소스 검사)로 검증함. Generator 요약은 신뢰하지 않고 전부 재실행.

## Verification Scenarios

### V1 (build) — PASS
`dotnet build Wcs.sln` (Bash 경유 — PowerShell 권한 거부로 Git Bash 사용):
```
빌드했습니다.
    경고 0개
    오류 0개
경과 시간: 00:00:03.60
----EXIT: 0----
```
exit 0, "빌드했습니다.", 경고 0 / 오류 0. PASS.

### V2 (full test GREEN) — PASS
`dotnet test`:
```
통과!  - 실패:     0, 통과:    15, 건너뜀:     0, 전체:    15, 기간: 44 ms - Wcs.Tests.dll (net10.0)
```
실패 0 / 통과 15 / 전체 15.
케이스 구성: 기존 Decide 9 (Row1, Row2, Row3, Row4a, Row4b, Row5 = 6 Fact; Row6 Theory = 2; Row7 = 1)
+ Wire 1 + 신규 C1(1) + C2(Theory 3) + C3(1) = 15. 전부 GREEN. PASS.

### V3 (경계 케이스 충실) — PASS
`dotnet test --filter Decider`:
```
통과!  - 실패:     0, 통과:    15, 건너뜀:     0, 전체:    15, 기간: 42 ms - Wcs.Tests.dll (net10.0)
```
C1/C2/C3 소스 검사 — 계약 기대값과 정확히 일치:
- C1 `C1_Row1_TgtFloorResidual_StillAllows`: Snap(ready:true, cur:1, tgt:1), agvFloor:1
  → Allowed=true, Reason=None, WriteTgtFloor=false. (tgt 잔류 ≠0 포함) 일치.
- C2 `C2_HoldOrOffline_BlocksTgtFloorWrite` [Theory]: ready:false, cur:2, tgt:0 + Full/Paused/Offline
  → 각각 Allowed=false, Reason=Full/Paused/Offline, WriteTgtFloor=false. (선기입 조건 충족해도 차단) 일치.
- C3 `C3_HoldOverridesReadyAndFloorMatch`: Snap(ready:true, cur:1, tgt:0), agvFloor:1 + Full
  → Allowed=false, Reason=Full, WriteTgtFloor=false. (층일치·Ready=1인데 Hold 우선) 일치.
PASS.

## Error cases (적극 배제)

### E1 (비순수) — PASS (배제됨)
`DepositDecider.cs`: static class / static Decide, 필드·DI·정적 가변 상태 없음, DateTime.Now/Random 없음, I/O 없음 — 순수 함수.
테스트의 `DateTimeOffset.UtcNow`는 Snap 헬퍼의 `At` 생성에만 쓰이며 판정 로직과 무관(Decide 내부 아님).
`Wcs.Core.csproj`: PackageReference/ProjectReference 0개 — 의존성 0 유지. PASS.

### E2 (회귀) — PASS (배제됨)
기존 Decide 9 + `Wire_Strings_AreStable` 전부 GREEN 유지. 실패 0. PASS.

### E3 (표 밖 동작) — PASS (배제됨)
SPEC §2 표 7행과 코드 대조:
- 우선순위 Offline → Hold(Full/Paused) → Ready/층 — 코드 분기 순서 일치.
- 행1 Allow(쓰기X) / 행2 WrongFloor+agvFloor기입 / 행3 WrongFloor 쓰기X(핑퐁차단)
  / 행4 Busy+agvFloor기입 / 행5 Busy 쓰기X / 행6 Full|Paused 쓰기X / 행7 Offline 쓰기X — 7행 모두 일치.
- TgtFloor 쓰기 조건 `TgtFloor==0 && (CurFloor!=agvFloor || Ready==0)`, Hold/Offline은 선행 우선순위로 차단되어 쓰기 도달 안 함.
- Hold/Offline 입력에서 WriteTgtFloor=false 확인(C2/C3/Row6/Row7).
- Decide는 TgtFloor를 클리어하지 않음(write 값만 설정, 0 클리어/리셋 경로 없음). PASS.

### E4 (범위 외 변경) — PASS (배제됨)
`git diff --name-only HEAD` 중 코드 surface(src/tests):
```
src/Wcs.Core/DepositDecider.cs
tests/Wcs.Tests/DepositDeciderTests.cs
```
정확히 둘. `Models.cs` 등 기타 src/tests 무변경, untracked 코드 파일 0.
(추가로 보인 tasks/sprint-contract.md·tasks/sprint-log.md는 3-Tier 하네스 산출물 — 코드 surface 밖, 계약 E4 범위 아님.) PASS.

### E5 (C1~C3 누락/불일치) — PASS (배제됨)
세 케이스 모두 추가됨, 기대값 정확 일치(V3 참조). PASS.

## Completion Conditions
- C-1 build exit 0 — 충족
- C-2 dotnet test 0 실패 + --filter Decider 0 실패 — 충족
- C-3 순수 함수 + Wcs.Core 의존성 0 — 충족
- C-4 변경 파일 = DepositDecider.cs + DepositDeciderTests.cs 둘(코드 surface) — 충족
- C-5 표 밖 동작 없음, Hold/Offline WriteTgtFloor=false, 클리어 없음 — 충족

전부 충족 → **APPROVED**.
