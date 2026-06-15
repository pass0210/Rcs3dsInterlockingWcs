# Sprint Contract — S-M1 (판정 엔진 DepositDecider)

## Goal
`Wcs.Core`의 `DepositDecider.Decide`를 docs/SPEC.md §2 판정 표(7행) 그대로 **순수 함수**로 구현해
`DepositDeciderTests`의 Decide 판정 케이스를 전부 GREEN으로 만든다. 더해 하네스 검증에서 도출한
경계 테스트 **C1~C3**(표를 정확히 인코딩하는 케이스)을 추가하고 GREEN으로 만든다.
표에 없는 동작은 추가하지 않는다. M1은 판정 로직만 — I/O·API·PLC 없음.

## Implementation Scope (Generator가 만들 것)
1. `src/Wcs.Core/DepositDecider.cs` — `Decide`의 `NotImplementedException` 스텁을 SPEC §2 표 구현으로 교체.
   - 우선순위: **Offline → Hold(Full/Paused) → Ready/층 비교**
   - 허가(행1): `Online && Hold=None && Ready=1 && CurFloor==agvFloor` → `Allow()` (TgtFloor 무관, 쓰기 없음)
   - 거부 사유: `Ready=1 && CurFloor!=agvFloor` → `WrongFloor` / `Ready=0` → `Busy` / `Hold=Full|Paused` → `Full|Paused` / `!Online` → `Offline`
   - TgtFloor 쓰기 조건: `TgtFloor==0 && (CurFloor!=agvFloor || Ready==0)` **단 Hold/Offline이면 절대 안 씀**. 값 = agvFloor.
   - WCS는 TgtFloor를 클리어하지 않는다(Decide는 쓰기 여부만 결정).
2. `tests/Wcs.Tests/DepositDeciderTests.cs` — 경계 테스트 3건 추가(TASKS.md M1):
   - **C1** 행1 경계: `Snap(ready:true, cur:1, tgt:1), agvFloor:1` → `Allowed`, `WriteTgtFloor=false` (이동완료 후 TgtFloor 잔류 ≠0 포함)
   - **C2** 행4 쓰기 차단: `Snap(ready:false, cur:2, tgt:0)` + `Full`/`Paused`/`Offline` → 거부 + `WriteTgtFloor=false` (Hold/Offline이 선기입 차단)
   - **C3** 행6 강한 경계: `Snap(ready:true, cur:1, tgt:0), agvFloor:1` + `Full` → `Allowed=false`·`Reason=Full`·`WriteTgtFloor=false` (층일치·Ready=1인데 Hold 우선)

## Out of Scope (손대지 말 것)
- `Models.cs`(PlcSnapshot/DepositDecision/DenyReason/RegisterMap) 시그니처 변경 금지 — 그대로 사용.
- IF-08 `allowed=true → reason="READY"` 와이어 주입은 **M3**(API 계층). M1은 `DenyReason.ToWire` 무변경.
- API/DTO(M3), PlcGateway, Sim3ds, Data 무변경.
- 표에 없는 동작 추가 금지(예: Decide가 TgtFloor 클리어, Hold/Offline 중 쓰기, agvFloor 외 값 기입).
- `Wcs.Core`의 의존성 0 유지(프로젝트 참조·패키지 추가 금지).

## Detected Project Type: Backend/API
(M1의 변경 표면은 `Wcs.Core` 순수 판정 로직 + xUnit 단위 테스트. HTTP 엔드포인트 없음 — 검증은 단위 테스트 실행.)

## Evaluation Criteria
1. **정확성(★★★)**: `DepositDeciderTests`의 Decide 9케이스 + 신규 C1~C3 전부 GREEN, `Wire_Strings_AreStable` 회귀 없음(GREEN 유지). 전체 `dotnet test` 0 실패.
2. **순수성(★★★)**: `Decide`는 순수 함수 — I/O·DI·정적 가변 상태·시간/난수 의존 없음. `Wcs.Core` 의존성 0 유지.
3. **스펙 충실(★★)**: SPEC §2 표 7행과 정확히 일치, 우선순위 순서 정확. 표에 없는 동작 0.
4. **장인성(★★)**: 표를 그대로 읽히는 분기/스위치, 죽은 코드·중복 없음, 기존 코딩 컨벤션 일치.

## Completion Conditions (전부 필수)
- C-1. `dotnet build Wcs.sln` exit 0.
- C-2. `dotnet test` → **0 실패**(기존 Decide 9 + Wire 1 + 신규 C1~C3 전부 GREEN). `--filter Decider`도 전부 GREEN.
- C-3. `Decide`가 순수 함수 — I/O/DI/가변 정적 상태 없음. `Wcs.Core.csproj`에 참조·패키지 추가 0.
- C-4. 변경 파일은 `src/Wcs.Core/DepositDecider.cs` + `tests/Wcs.Tests/DepositDeciderTests.cs` **둘 뿐**. `Models.cs` 등 기타 무변경(`git diff` 확인).
- C-5. 표에 없는 동작 없음 — 특히 Hold/Offline 입력에서 `WriteTgtFloor=false`, Decide가 TgtFloor를 클리어하지 않음.

## Verification Scenarios
M1의 검증 표면은 단위 테스트(판정 로직). HTTP 엔드포인트는 이 스프린트에 없음(M3).

- **V1 (build)**: `dotnet build Wcs.sln` → exit 0, 경고/오류 0. 증거=빌드 요약 인용.
- **V2 (full test GREEN)**: `dotnet test` → `실패: 0`, 전체 케이스 GREEN(기존 10 + 신규 C1~C3). 증거=러너 요약 원문.
- **V3 (경계 케이스 충실)**: `dotnet test --filter Decider` 전부 GREEN, 그리고 C1·C2·C3가 실제로 추가됐고 기대값(위 Scope)대로 통과함을 테스트 소스로 확인.

**Error cases (Evaluator 적극 배제)**
- E1: `Decide`에 I/O·DI·정적 가변 상태·DateTime.Now/Random 등 비순수 요소 도입 → FAIL
- E2: 기존 테스트 회귀(특히 `Wire_Strings_AreStable`) 또는 일부 Decide 케이스 여전히 RED → FAIL
- E3: 표 밖 동작 — Hold/Offline에서 `WriteTgtFloor=true`, Decide가 TgtFloor 클리어, 우선순위 어긋남 → FAIL
- E4: `DepositDecider.cs`·테스트 파일 외 변경(Models.cs 등) 또는 `Wcs.Core`에 참조/패키지 추가 → FAIL
- E5: C1~C3 미추가 또는 기대값 불일치 → FAIL

> Planner self-check — Detected project type: Backend/API. Required scenario slots: 3 (unit-test build / full-suite GREEN / boundary-case fidelity; HTTP happy·error-path N/A — no endpoints this sprint, deferred to M3). All slots filled: yes.
