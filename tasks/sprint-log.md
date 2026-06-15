# Sprint Log

## IMPLEMENTATION COMPLETE (M1)

### Sprint: S-M1 (판정 엔진 DepositDecider)

### 수행 내용

1. `src/Wcs.Core/DepositDecider.cs` — `Decide`의 `NotImplementedException` 스텁을 SPEC §2 표(7행) 그대로 순수 함수로 구현.
   - 우선순위: Offline → Hold(Full/Paused) → Ready/층 비교
   - 허가(행1): `Online && Hold=None && Ready=1 && CurFloor==agvFloor` → `Allow()` (TgtFloor 무관)
   - 거부 사유: WrongFloor(행2/3) / Busy(행4/5) / Full/Paused(행6) / Offline(행7)
   - TgtFloor 쓰기: `TgtFloor==0 && (CurFloor!=agvFloor || !Ready)` 단 Hold/Offline 제외
   - I/O·DI·정적 가변 상태·DateTime.Now/Random 사용 없음(순수 함수)

2. `tests/Wcs.Tests/DepositDeciderTests.cs` — 경계 테스트 C1~C3 추가(기존 테스트 무변경):
   - C1: TgtFloor 잔류(≠0) 상태에서 층 일치·Ready=1 → 허가, WriteTgtFloor=false
   - C2: Hold(Full/Paused)/Offline → 선기입 조건(Ready=0·TgtFloor=0) 충족해도 WriteTgtFloor=false (Theory 3건)
   - C3: 층 일치·Ready=1이어도 Hold=Full → Allowed=false·Reason=Full·WriteTgtFloor=false (Hold 우선)

### 변경 파일 (2개)

- `src/Wcs.Core/DepositDecider.cs`
- `tests/Wcs.Tests/DepositDeciderTests.cs`

### V1 — 빌드 증거

```
dotnet build Wcs.sln
빌드했습니다.
    경고 0개
    오류 0개
경과 시간: 00:00:04.91
```

### V2 — 테스트 러너 요약 (전체)

```
dotnet test
통과!  - 실패:     0, 통과:    15, 건너뜀:     0, 전체:    15, 기간: 41 ms - Wcs.Tests.dll (net10.0)
```

### V3 — Decider 필터 검증

```
dotnet test --filter Decider
통과!  - 실패:     0, 통과:    15, 건너뜀:     0, 전체:    15, 기간: 40 ms - Wcs.Tests.dll (net10.0)
```

기존 Decide 9케이스 + Wire 1 + 신규 C1~C3 전부 GREEN. 실패 0.

## IMPLEMENTATION COMPLETE (재제출 — M0-1 수정 후)

### Sprint: S-M0 (솔루션 구성 + 빌드 그린)

### M0-1 수정 내역

- 문제: SDK 10.0.300에서 `dotnet new sln -n Wcs`가 `.slnx`(XML) 형식을 기본 생성함. 계약 C-1/V1은 `Wcs.sln`을 요구.
- 조치: `Wcs.slnx` 제거 후 `dotnet new sln -n Wcs --format sln`으로 클래식 `.sln` 재생성, 6개 프로젝트 재추가.
- 결과: 루트에 `Wcs.sln` 단독 존재.

### 수행 내용

1. `dotnet new sln -n Wcs --format sln` → 루트에 `Wcs.sln` 생성 (클래식 형식)
2. 6개 프로젝트 sln 추가: Wcs.Core, Wcs.PlcGateway, Wcs.Api, Wcs.Data, Wcs.Sim3ds, Wcs.Tests
3. 프로젝트 참조 배선 (지정 방향 그대로):
   - Wcs.Api → Wcs.Core, Wcs.PlcGateway, Wcs.Data
   - Wcs.PlcGateway → Wcs.Core
   - Wcs.Data → Wcs.Core
   - Wcs.Tests → Wcs.Core
4. NuGet 패키지 추가:
   - Wcs.PlcGateway → FluentModbus 5.3.2
   - Wcs.Sim3ds → FluentModbus 5.3.2
   - Wcs.Tests → xunit 2.9.3, xunit.runner.visualstudio 3.1.5, Microsoft.NET.Test.Sdk 18.6.0

### 참조/패키지 그래프 요약

```
Wcs.Core          (참조 없음, 패키지 없음)
Wcs.PlcGateway    → Wcs.Core; FluentModbus 5.3.2
Wcs.Data          → Wcs.Core
Wcs.Sim3ds        FluentModbus 5.3.2 (프로젝트 참조 없음)
Wcs.Api           → Wcs.Core, Wcs.PlcGateway, Wcs.Data
Wcs.Tests         → Wcs.Core; xunit 2.9.3, xunit.runner.visualstudio 3.1.5, Microsoft.NET.Test.Sdk 18.6.0
```

### V1 — 빌드 증거

```
dotnet build Wcs.sln
빌드했습니다.
    경고 0개
    오류 0개
경과 시간: 00:00:04.81
```

### V2 — 테스트 러너 요약 (전체)

```
dotnet test Wcs.sln
실패!  - 실패:     9, 통과:     1, 건너뜀:     0, 전체:    10, 기간: 73 ms
```

### V3 — Decider 필터 검증

9건 전부 `System.NotImplementedException : M1: DepositDecider.Decide — see docs/SPEC.md §2`로 실패.
Wire_Strings_AreStable 1건 GREEN 확인. Wire는 FAIL 집합에 없음.

### 스켈레톤 무변경 확인

변경된 파일: `Wcs.sln` (신규) + 각 `.csproj`의 참조/패키지 항목만. 
스켈레톤 `.cs`/`.json` 파일 내용 편집 없음.
