# Evaluator — Functional 차원 (S-AUDIT-B-DEPLOY-HARDENING)

**차원**: Functional (로그 경로 해석 정확성 + 스크립트 로직 + 기동)
**판정**: **PASS**
**평가 시각**: 2026-08-03 (Asia/Seoul)
**베이스**: HEAD=99d8038, 브랜치 feat/audit-b-deploy-hardening. 변경은 워킹트리(미커밋) — git diff/status로 ground-truth 확인.
**검증 방식**: 코드 직접 검사 + 독립 재실행(fresh evidence). Generator 보고 문구 불신·직접 재현.

---

## 1. 로그 경로 게이트 정확성 — PASS

### 순수 헬퍼 정확성 (코드 직접 확인)
`backend/src/Wcs.Api/Program.cs:415-427`
```csharp
public static class ServiceHostingEnvironment
{
    public static string? ResolveWorkingDirectoryOverride(bool isWindowsService, string baseDirectory)
        => isWindowsService ? baseDirectory : null;
}
```
- 서비스(`isWindowsService=true`) → `baseDirectory` 반환(그 경로로 CWD 고정 신호). 정확.
- 비서비스(`false`) → `null` 반환(작업 디렉터리 미변경 = 회귀 0). 정확.
- 부작용 0(I/O 없음) — 판정 로직이 SetCurrentDirectory 호출부에서 분리됨.

### ServiceHostingEnvironmentTests (5 케이스) — 독립 GREEN·비공허
`backend/tests/Wcs.Tests/ServiceHostingEnvironmentTests.cs` — Fact 2 + Theory(InlineData 3) = **5 케이스**.
- `ServiceContext_ReturnsBaseDirectory` → `Assert.Equal(baseDir, result)` (실 assert, 공허 아님).
- `NonServiceContext_ReturnsNull_NoOverride` → `Assert.Null(result)`.
- `ServiceContext_EchoesGivenBaseDirectory` × 3 (`C:\BOWOO\Wcs.Api`, `D:\deploy\wcs`, `/opt/wcs`) → `Assert.Equal`.

**독립 격리 재실행 (fresh)**:
```
dotnet test backend/Wcs.sln --filter "FullyQualifiedName~ServiceHostingEnvironmentTests" --no-build
통과!  - 실패: 0, 통과: 5, 건너뜀: 0, 전체: 5, 기간: 23 ms - Wcs.Tests.dll (net10.0)
```

### Program.cs 게이트 블록 위치·순서 (코드 직접 확인)
워킹트리 diff 확인 — 블록은 `Program.cs:41-46`:
```csharp
{
    var workingDirOverride = ServiceHostingEnvironment.ResolveWorkingDirectoryOverride(
        WindowsServiceHelpers.IsWindowsService(), AppContext.BaseDirectory);
    if (workingDirOverride is not null)
        Directory.SetCurrentDirectory(workingDirOverride);
}
```
- **`builder.Host.UseWindowsService()`(:29) 직후**, **`builder.Host.UseSerilog(...)`(:53) 이전**에 위치. 순서 정확.
- 파일 sink 상대경로("logs/")는 `builder.Build()`(:286) 시점에 CWD 기준 해석되므로, 그보다 앞선 :44-45의 SetCurrentDirectory가 서비스 컨텍스트에서만 CWD를 `AppContext.BaseDirectory`(=배포 폴더)로 고정 → `<배포폴더>\logs\wcs-*.log` 결정적 생성. 비서비스는 override=null이라 SetCurrentDirectory 미호출 → 프로젝트/logs 유지.

> Operational note: 실 sc.exe Windows Service 컨텍스트 기동은 본 환경에서 재현 불가(계약 Verification Scenarios "Operational verification"이 명시: dotnet run은 CWD=프로젝트라 갭 재현 못 함). 계약이 승인한 대체 실증 = 순수 게이트 단위 테스트(위 5 GREEN, `true→배포폴더 / false→null`) + 위치·순서 코드 검증. 이 대체 경로는 계약(sprint-contract.md:64-67)과 정합하며 Functional 차원 요구를 충족.

---

## 2. install-service.ps1 로직 — PASS

`scripts/install-service.ps1` 전문 직접 검토 + AST 파싱 + 3분기 워크스루.

### (a) 비내장 계정 password 게이트 (1069 예방)
- 화이트리스트 `:134-139`: `LocalSystem`, `NT AUTHORITY\LocalService`, `LocalService`, `NT AUTHORITY\NetworkService`, `NetworkService`, `NT SERVICE\*`(-like). 계약 요구 집합과 일치.
- `:140-147`: 비내장 계정 && `$null -eq $Password` → `Write-Error` + `exit 1` — **서비스 생성(:189) 이전**에 중단. 1069(로그온 실패) 예방 정확.
- 전달 `:178-179` `obj=` `$Account`, `:185-188` 비내장 계정이면 `password=` `<복호화>` 추가. `sc.exe create` 인자로만 순간 사용(ConvertFrom-SecureStringPlain, 화면/로그 미출력). 정확.

### (b) depend= (SQL 선기동)
- `:52` `$SqlServiceName` 기본값 **MSSQLSERVER**(Q4). `:181-184` 빈/공백 아니면 `depend= <서비스명>` 추가, 빈 문자열이면 생략. 정확.

### (c) 사전 SQL 도달성 점검 (SELECT 1)
- `Test-SqlReachable`(`:75-118`) 3분기: `$true`(도달)/`$false`(불가)/`$null`(점검 도구 부재). Invoke-Sqlcmd 우선·sqlcmd fallback.
- 호출부 `:149-169`:
  - `-SkipSqlCheck` → 경고 후 강행(유일한 우회 경로). 정확.
  - `$null`(도구 없음) → Write-Error + `exit 1`(도달성 미확인이면 서비스 미생성). 정확.
  - `-not $reachable`($false, 불가) → Write-Error + `exit 1` **서비스 미생성·중단**(5초 무한 크래시 루프 예방). 정확.
  - `$true` → "SQL 도달성 OK" 후 진행. 정확.
- 3분기 모두 서비스 생성(:189)보다 앞서 실행 → 크래시 루프 서비스 미등록 보장.

### PowerShell AST 파싱 — 통과 (fresh)
```
[System.Management.Automation.Language.Parser]::ParseFile(install-service.ps1, ...)
AST-PARSE: OK (0 errors, 805 tokens)
```

> 참고(비FAIL·정보): 화이트리스트는 `NT AUTHORITY\SYSTEM`(LocalSystem의 별칭 표기)을 내장으로 인식하지 않아 password를 요구할 수 있으나, 이는 보수적(안전) 방향 실패이며 sc.exe의 정식 obj= 값은 `LocalSystem`이라 실무 결함 아님. 계약 요구 집합(LocalSystem/LocalService/NetworkService/NT SERVICE\*)은 정확히 충족.

---

## 3. /health 무변경 (HTTP 계약 불변) — PASS
- `git diff -- Program.cs | grep -i health` → **매치 0줄**. /health 핸들러(`Program.cs:360-385`, `status/db/sorters` 200 반환) 워킹트리 diff에 미포함 — 계약 무변경 확인.
- Program.cs 워킹트리 diff는 정확히 2 헝크뿐: (1) using + 게이트 블록, (2) ServiceHostingEnvironment 헬퍼 클래스. 그 외 표면 무변경.

---

## 4. 회귀 0 (기능) — PASS
독립 전체 재실행 (fresh, `--no-build` 아님·풀 빌드+테스트):
```
dotnet test backend/Wcs.sln
통과!  - 실패: 0, 통과: 539, 건너뜀: 0, 전체: 539, 기간: 1 m 39 s - Wcs.Tests.dll (net10.0)
(exit code 0 — 프로세스 정상 종료, teardown hang 0)
```
- **산술 일치**: 베이스라인 534 + 신규 5 = **539**. 정확.
- 신규 ServiceHostingEnvironmentTests 5 케이스 격리 재실행도 GREEN(위 §1).
- teardown hang 0 — 전체 스위트 exit 0으로 정상 종료(background 태스크 exit code 0 관측).

---

## 5. #7 / #8 — PASS
- **#7 (경로 리터럴 하드코딩 0)**: Program.cs 실행 코드에서 리터럴 경로 0. `grep '[A-Za-z]:\\|System32|logs/'` 결과 중 실행 코드 라인은 `:45 Directory.SetCurrentDirectory(workingDirOverride)` 하나뿐이며 인자는 `AppContext.BaseDirectory` 파생 변수(리터럴 아님). 나머지 매치(:32/33/35/39/162/410 의 `C:\WINDOWS\System32`·`D:\...`)는 전부 주석(배경 설명). 준수.
- **#8 (Wcs.Core 무변경)**: `git diff --stat -- backend/src/Wcs.Core` = 빈 diff. `git status --porcelain -- backend/src/Wcs.Core` = 빈(untracked 0). tracked+untracked 모두 clean. 준수.

---

## 종합
Functional 차원 5개 항목(로그 경로 게이트 정확성 / 스크립트 로직 3분기 / /health 무변경 / 회귀 0 / #7·#8) 전부 fresh 증거로 충족.

**FUNCTIONAL: PASS**

(단일 RED 없음. 화이트리스트 `NT AUTHORITY\SYSTEM` 별칭 건은 보수적 안전방향 minor 관측이며 blocking 아님.)
