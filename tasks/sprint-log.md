# Sprint Log — S-AUDIT-B-DEPLOY-HARDENING

(Generator가 `## IMPLEMENTATION COMPLETE` + 변경 요약 + 검증 결과 기록)

## IMPLEMENTATION COMPLETE

### 모듈 1 — 로그 위치 결정성 (Serilog 파일 sink 작업 디렉터리 고정)

**변경 파일**
- `backend/src/Wcs.Api/Program.cs`
  - using 추가: `Microsoft.Extensions.Hosting.WindowsServices`(WindowsServiceHelpers).
  - `builder.Host.UseWindowsService()` 직후·`UseSerilog(...)` **이전**에 작업 디렉터리 고정 블록 삽입.
    서비스 컨텍스트일 때만 `Directory.SetCurrentDirectory(AppContext.BaseDirectory)` 호출.
  - 하단에 순수 헬퍼 `public static class ServiceHostingEnvironment` 추가
    (`ResolveWorkingDirectoryOverride(bool isWindowsService, string baseDirectory)` → 서비스면 baseDirectory, 아니면 null).
    OperationLogClassifier(기존 Program.cs 공개 정적 클래스·테스트 소비) 패턴과 동형.
- `backend/tests/Wcs.Tests/ServiceHostingEnvironmentTests.cs` (신규)
  - 게이트 실효 단위 검증(서비스=경로 반환 / 비서비스=null / baseDirectory echo). Fact 2 + Theory 3 = 5 케이스.

**작업 디렉터리 고정 방식**
- 게이트: `WindowsServiceHelpers.IsWindowsService()`(Q2). 서비스 컨텍스트일 때만 CWD를 exe 인접
  `AppContext.BaseDirectory`(=배포 폴더)로 고정(Q1). dev `dotnet run`·콘솔·WebApplicationFactory 테스트
  호스트는 IsWindowsService=false → CWD 미변경(회귀 0).
- 결과: Serilog 파일 sink의 상대 `path`("logs/wcs-.log", appsettings 값 **유지**)가 sink 생성 시점(builder.Build())의
  CWD 기준으로 해석되므로, 서비스에서 `<배포폴더>\logs\wcs-*.log`로 결정적 생성. sc.exe 기본 CWD(System32\logs) 유입 종료.
- #7 준수: 리터럴 경로 하드코딩 0 — 실행 코드는 `AppContext.BaseDirectory` 파생값만 사용(주석의 `C:\...`·`System32`는
  배경 설명일 뿐 로직 아님). appsettings `Serilog:WriteTo:File:path`는 상대값 그대로.
- 무회귀 근거: 정적 서빙(WebRootPath=ContentRoot/wwwroot — UseWindowsService가 ContentRoot=BaseDirectory로 이미 고정,
  CWD와 무관)·전용 추적 로그(TraceLog `D:\Rcs3dsInterlockingWcsLogs` 절대경로)·operation_log/plc_event(DB) 전부 무영향.

**문서 갱신 (DEPLOY-ONPREM.md)**
- §9-B step3: 로그 확인 경로를 `C:\WINDOWS\System32\logs\wcs-*.log` → `C:\BOWOO\Wcs.Api\logs\wcs-*.log`(=`<DEPLOY_DIR>\logs`)로
  갱신 + 서비스 컨텍스트 CWD 고정 동작 설명 + "과거 System32" 히스토리 각주.
- §10 트러블슈팅: sc.exe 등록 서비스 앱 로그 위치를 System32\logs → `<DEPLOY_DIR>\logs`로 정정.

### 모듈 2 — install-service.ps1 견고화

**변경 파일**
- `scripts/install-service.ps1` (전면 개정, AST 파싱 통과 확인)

**스크립트 변경**
- 비밀번호(SecureString): `-Password` 파라미터 추가. 계정이 내장/가상 서비스 계정
  (LocalSystem·LocalService·NetworkService·`NT SERVICE\*`)이 **아니면** 필수 — 미제공 시 서비스 생성 전 사전 요구·중단
  (sc start 1069 로그온 실패 예방). `sc.exe create ... obj= <계정> password= <값>` 전달(평문은 sc.exe 인자로만 순간 사용).
- SQL 선기동 의존: `-SqlServiceName`(기본값 **MSSQLSERVER**·Q4) → `sc.exe create ... depend= <서비스명>`. 빈 문자열이면 생략.
- 설치 전 사전 SQL 도달성 점검: `Test-SqlReachable`(Invoke-Sqlcmd 우선·sqlcmd fallback)로 대상 DB에 `SELECT 1`.
  실패 시 **서비스 미생성·중단**(5초 무한 크래시 루프 서비스 미등록). 점검 도구 부재 시 `-SkipSqlCheck`로만 강행.
  대상 파라미터화: `-SqlServer`(기본 localhost)·`-SqlDatabase`(기본 Rcs3dsInterlockingWcs)·SQL 인증용 `-SqlUser`/`-SqlPassword`.
- 헤더/주석 정정: (구 :14-15) 자동 Migrate 단언을 "계정 DB 권한(db_owner)·SQL 기동 시에만 성공, 아니면 크래시 루프"로 교정 +
  사전 점검/depend= 근거 명시. (구 :18) "운영 README(P4)" → `docs/DEPLOY-ONPREM.md`(§5·§9-B·§10) 참조로 교체.
  .DESCRIPTION에 현장 표준=수동 sc.exe(§9-B)/NSSM(§5-3)·이 스크립트=대안임을 명시. 기본 계정 LocalSystem 유지(Q5).
- 완료 메시지에 depend= 안내 + "앱 로그: exe 인접 `<배포폴더>\logs\wcs-*.log`" 안내 추가(모듈 1과 정합).

**문서 갱신 (DEPLOY-ONPREM.md §5-3 대안 블록)**
- 대안(프로젝트 내장 스크립트) 블록에 depend=/사전 SQL 점검/비내장 계정 비밀번호/SQL 인증 점검 예시(PowerShell) 반영.
- 이 스크립트=대안, 현장 표준=§9-B 수동 sc.exe 명시. 이 스크립트 등록 서비스도 앱 로그가 `<DEPLOY_DIR>\logs`(모듈 1)임을 명시.

### SCOPE 준수
- SCOPE OUT 무접촉 확인: 운영 README 신규 0·루트 README/CLAUDE.md/master_spec 0·base appsettings Trusted_Connection 변경 0·
  기본 계정 하향 0·묶음 A 0. install-service.ps1 실패 복구 `restart/5000` 루프는 구현 스코프(항목 4~8) 밖이라 미변경.
- `uninstall-service.ps1` 무접촉(변경 불요).

### 검증
- `dotnet build backend/Wcs.sln -c Debug`: **오류 0**. 경고 13개 전부 **기존**(NU1903 SQLite CVE·B2cFacilityService.cs CS8604·
  기존 테스트 xUnit2013) — 내 변경(Program.cs·신규 테스트 파일)에서 **신규 경고 0**.
- `dotnet test backend/Wcs.sln --no-build`: **539 통과 / 0 실패 / 0 건너뜀**, 1m37s, teardown hang 0(프로세스 정상 종료).
  신규 ServiceHostingEnvironmentTests 5 케이스 포함(단독 실행도 GREEN).
- install-service.ps1: `[System.Management.Automation.Language.Parser]::ParseFile` **AST 파싱 오류 0**. 정적 로직 워크스루 완료
  (내장 계정 화이트리스트·비밀번호 게이트·depend= 조건·SELECT 1 3분기(true/false/null) 정확).
- #7: Program.cs 실행 코드 리터럴 경로 하드코딩 0(AppContext.BaseDirectory 파생만) — git diff 확인.
- #8: `git diff --stat -- backend/src/Wcs.Core` **빈 diff**(Wcs.Core 무변경).
- **로그 경로 실증 방법(Evaluator용)**: 순수 게이트를 단위 테스트로 실증(서비스=배포폴더 / 비서비스=null). dev `dotnet run`은
  CWD=프로젝트라 갭 재현 불가하므로, 실 서비스 컨텍스트 재현이 필요하면 (a) `ServiceHostingEnvironment.ResolveWorkingDirectoryOverride(true, <baseDir>)`
  결과로 SetCurrentDirectory 실효 확인, 또는 (b) 실제 sc.exe/Windows Service 기동 후 `<DEPLOY_DIR>\logs\wcs-*.log` 생성·System32\logs 유입 0 관측.

### 변경 파일 요약
- `backend/src/Wcs.Api/Program.cs` (모듈 1)
- `backend/tests/Wcs.Tests/ServiceHostingEnvironmentTests.cs` (모듈 1·신규)
- `scripts/install-service.ps1` (모듈 2)
- `docs/DEPLOY-ONPREM.md` (모듈 1 §9-B/§10 · 모듈 2 §5-3)

## FIX ITER (M1/m1/m5)

코드리뷰(Step 4.5) 후 사용자 결정 = M1 + 값싼 m1·m5만 머지 전 견고화. 이 3건만 수정, 그 외 전부 불변
(모듈 1 로그 게이트·depend=·사전점검 로직·문서 정합 무변경). C# 무변경(PS/MD만) — 539 테스트 무영향.

**M1 (MAJOR·보안 — 자격증명 커맨드라인 leak 제거)** — `scripts/install-service.ps1`
- 문제: 비내장 계정 password가 `sc.exe create ... password= <평문>` 으로 프로세스 커맨드라인(감사 4688·Sysmon·SIEM)에 평문 지속 기록.
- 수정: 서비스 생성을 계정 종류로 분기. **비내장 계정 → `New-Service -Credential (PSCredential) -BinaryPathName -DisplayName -StartupType Automatic -DependsOn <SqlServiceName>`** — 자격증명을 SCM API로 전달(커맨드라인 미노출). 내장/가상 계정(무password) → 기존 `sc.exe create` 유지(원래 자격증명 없음). depend=(≡ -DependsOn)·start= auto(≡ -StartupType Automatic)·환경변수·설명·실패복구(공통 후처리 sc.exe description/failure) 동작·의미 전부 보존.
- 잔여: sqlcmd fallback `-P <평문>`(도달 좁음·Invoke-Sqlcmd 우선이라 통상 미도달)에 커맨드라인 노출 경고 주석 추가(코드 동작 불변).
- 검증: 생성 경로에 `password=` 토큰 잔존 0(정규식 확인)·New-Service 경로 존재 확인.

**m1 (값쌈 — 내장 계정 별칭 화이트리스트 보강)** — `install-service.ps1`
- `$builtinAccounts` 에 `NT AUTHORITY\SYSTEM`(LocalSystem 별칭)·`NT AUTHORITY\LOCAL SERVICE`·`NT AUTHORITY\NETWORK SERVICE`(표시명 공백형) 추가. 이 별칭들이 password 요구로 오거부되지 않음. (-contains 대소문자 무시.)

**m5 (값쌈 — 빈 SecureString 거부)** — `install-service.ps1`
- 비내장 계정 password 가드를 `($null -eq $Password) -or ($Password.Length -eq 0)` 로 확장 — 빈 SecureString 도 null과 동일하게 사전 거부(빈 password→sc start 1069 예방). `-or` 단락평가로 null-safe.

**손대지 않음(sprint-feedback 등재 — 이번 스코프 밖)**: m2(SecureString zero-out)·m3(사전점검 앱DB 대상 정합)·m4(TLS). 모듈 1·depend=·사전점검·문서 정합 무변경.

**문서**: `docs/DEPLOY-ONPREM.md` §5-3 대안 블록 계정 bullet에 New-Service(SCM API·커맨드라인 미노출)·`NT AUTHORITY\SYSTEM` 별칭·비어있지 않은 password 반영.

### FIX ITER 검증
- `install-service.ps1`: `[Parser]::ParseFile` **AST 파싱 오류 0**. 생성 경로 `password=` 토큰 0·New-Service 경로 확인.
- `dotnet test backend/Wcs.sln --no-build`: **539 통과 / 0 실패**(clean 환경 2회 GREEN). C# 무변경이라 무영향 재확인.
- ⚠ 중간 1회 `E2EGroupN_TraceLogTests.N1`(전용 파일 6이벤트 12s 대기) 타임아웃 1건 관측 → **기지(旣知) 타이밍 플레이크**로 귀속:
  ① `--no-build` 동일 바이너리가 GREEN(539)→RED(538/1)→GREEN(539) — 코드 무관 외부 타이밍/워밍업. ② N1 단독 실행·full-suite 내에서는 통과, cold class-filter 재실행에서만 실패(콜드스타트/E2E 부하 민감). ③ 내 변경은 C# 경로 0(PS/MD, Program.cs 게이트는 테스트 호스트에서 IsWindowsService()==false로 no-op). 고아 Sim/포트 점검 결과 잔류 0. (교훈: s9-flake-under-e2e-load·e2e-parallel-load-surfaces-integration-flakes.)
