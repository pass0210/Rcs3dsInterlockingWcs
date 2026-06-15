# Sprint Log

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
