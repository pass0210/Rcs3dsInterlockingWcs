# Evaluator 차원: Deployment-safety & Regression — S-AUDIT-B-DEPLOY-HARDENING

## 판정: **PASS** (이 차원 단독)

평가 대상 브랜치 `feat/audit-b-deploy-hardening` (HEAD 99d8038, working tree).
변경 표면(git status, 코드/문서/스크립트만): `backend/src/Wcs.Api/Program.cs`,
`backend/tests/Wcs.Tests/ServiceHostingEnvironmentTests.cs`(신규),
`scripts/install-service.ps1`, `docs/DEPLOY-ONPREM.md`.
아래는 전부 **이번에 직접 실행한 fresh 증거**다.

---

## 1. 로그 위치 결정성 (가중 35%) — PASS

### (1) 코드 흐름 확정 — SetCurrentDirectory가 Serilog sink 생성 이전
`Program.cs` 순서를 직접 대조:
- `:29` `builder.Host.UseWindowsService();`
- `:41-46` 작업 디렉터리 고정 블록
  ```csharp
  var workingDirOverride = ServiceHostingEnvironment.ResolveWorkingDirectoryOverride(
      WindowsServiceHelpers.IsWindowsService(), AppContext.BaseDirectory);
  if (workingDirOverride is not null)
      Directory.SetCurrentDirectory(workingDirOverride);
  ```
- `:53-55` `builder.Host.UseSerilog((context, services, configuration) => ...)` — **3-인자 지연(deferred) 델리게이트 형식**. 로거/파일 sink는 콜백 등록 시점이 아니라 서비스 프로바이더 빌드 시점(`builder.Build()`)에 인스턴스화된다.
- `:286` `var app = builder.Build();` — 여기서 File sink가 생성되고 상대 `path`("logs/wcs-.log")가 **그 시점의 CWD 기준**으로 절대화된다.

`SetCurrentDirectory`(45)는 `builder.Build()`(286)보다 앞서 실행되므로, 서비스 컨텍스트에서 sink가 열릴 때 CWD는 이미 `AppContext.BaseDirectory`(배포 폴더)로 고정돼 있다 → 상대 `logs/`가 `<배포폴더>\logs`로 결정적 해석. `Program.cs :1-55`에 SetCurrentDirectory 이전 조기 부트스트랩 로거(`CreateBootstrapLogger`) 없음 확인 → 조기에 System32\logs로 파일을 여는 경로 없음.

### (2) 순수 게이트 단위 테스트 — GREEN
`ServiceHostingEnvironment.ResolveWorkingDirectoryOverride(isWindowsService, baseDirectory)` = `isWindowsService ? baseDirectory : null` (부작용 0). `ServiceHostingEnvironmentTests` 5 케이스(Fact 2 + Theory 3)가 전체 539 스위트에 포함되어 GREEN(§3 참조).

### (3) 실 서비스 컨텍스트 "동등 재현" (Sprint Contract 명시 허용 경로) — 실증 PASS
dev `dotnet run`은 CWD=프로젝트라 갭을 재현 못 하므로(감사 노트), 앱과 **동일 버전 Serilog.Sinks.File 6.0.0** + **동일 상대 path 패턴 "logs/wcs-.log"** + **byte-identical 게이트 로직**으로 서비스 초기 CWD(System32)→override→sink 생성 시퀀스를 폐기용 프로젝트로 재현. 실행 raw 출력:
```
== 시나리오 A: 서비스 컨텍스트(isWindowsService=true) ==
  초기 CWD = ...\Temp\svc-logrepro-ef82af61\System32
  [PASS] 게이트가 배포폴더 반환(비-null)
  override 후 CWD = ...\svc-logrepro-ef82af61\BOWOO\Wcs.Api
  배포폴더/logs 파일: 1  |  System32모사/logs 파일: 0
  [PASS] 로그가 배포폴더/logs 에 생성됨(>=1)
  [PASS] System32모사/logs 유입 0건
    -> ...\BOWOO\Wcs.Api\logs\wcs-20260803.log (63 bytes)
== 시나리오 B: 비서비스 컨텍스트(isWindowsService=false) — CWD 미변경 회귀 0 ==
  [PASS] 게이트가 null 반환(CWD 미변경 신호)
  CWD(불변) = ...\svc-logrepro-ef82af61\project
  [PASS] 비서비스에서 CWD 가 프로젝트 그대로(미변경)
  프로젝트/logs 파일: 1 (dev 로그는 프로젝트/logs 유지)
  [PASS] dev 로그가 프로젝트/logs 에 유지(배포폴더로 새지 않음)
ALL CHECKS PASS
```
→ 서비스 컨텍스트: 상대 `logs/`가 배포 폴더로 결정적 생성(1건), **System32-모사 CWD 유입 0건**(유입 종료 실증·논리 둘 다 확정). 비서비스: CWD 미변경 → dev 로그 프로젝트/logs 유지(회귀 0).

### (4) #7 경로 하드코딩 0 — PASS
`git diff -- Program.cs` 검토: 실행 코드는 `AppContext.BaseDirectory` 파생값만 사용. 주석 내 `C:\WINDOWS\System32`·`D:\`·`<배포폴더>`는 배경 설명일 뿐 로직 아님. 리터럴 경로 하드코딩 0.

---

## 2. 문서-코드 정합 (가중 20%) — PASS
`git diff -- docs/DEPLOY-ONPREM.md` 라인 대조:
- **§9-B step3(로그 확인)**: `C:\WINDOWS\System32\logs\wcs-*.log` → `C:\BOWOO\Wcs.Api\logs\wcs-*.log`(=`<DEPLOY_DIR>\logs`) + `WindowsServiceHelpers.IsWindowsService()` 게이트 설명 + "과거 System32" 히스토리 각주. 코드 새 동작과 일치.
- **§10(트러블슈팅)**: sc.exe 등록 서비스 앱 로그 위치를 System32\logs → `C:\BOWOO\Wcs.Api\logs`(=`<DEPLOY_DIR>\logs`)로 정정. 일치.
- **§5-3(대안 블록)**: password/`-Account`·depend=`-SqlServiceName MSSQLSERVER`·사전 SQL 점검(`SELECT 1` 실패 시 서비스 미생성)·비내장 계정 `-Password` 필수·SQL 인증 `-SqlUser/-SqlPassword`·`-SkipSqlCheck` 예시가 `install-service.ps1` 실제 파라미터/로직과 정합. "이 스크립트=대안, 현장 표준=§9-B 수동 sc.exe" 명시. 이 스크립트 등록 서비스 앱 로그도 `<DEPLOY_DIR>\logs`임을 명시(모듈 1과 정합).

문서-코드 격차 0.

---

## 3. 회귀 0 (가중 15%) — PASS
- **dev/콘솔/테스트 호스트**: `IsWindowsService()=false` → 게이트 null → `SetCurrentDirectory` 미호출 → CWD 불변. 단위 테스트 + §1(3) 시나리오 B로 실증. 로그 동작 불변.
- **정적 서빙**: WebRootPath=ContentRoot/wwwroot, `UseWindowsService`가 서비스 컨텍스트에서 ContentRoot=BaseDirectory로 고정 → CWD와 무관. dev에선 CWD 자체가 불변이라 영향 0.
- **전용 추적 로그(TraceLog)**: `appsettings.json:132` `"Directory": "D:\\Rcs3dsInterlockingWcsLogs"` **절대경로** 확인 → CWD 무관·무영향.
- **operation_log/plc_event**: DB 기록 → CWD 무관.
- **전체 스위트**: `dotnet test backend/Wcs.sln --no-build` → **실패: 0, 통과: 539, 건너뜀: 0** (1m34s, teardown hang 0). Generator 주장(539 GREEN)을 독립 재실행으로 확인.
- **빌드 신규 경고 0**: `dotnet build -c Debug` → 오류 0. 잔여 경고는 전부 기존 NU1903(SQLitePCLRaw CVE). 전체 출력 grep: `warning (CS|xUnit|CA)` 0건, `Program.cs`/`ServiceHostingEnvironment` 참조 경고 0건. 변경 표면 신규 경고 0.
- **#8 Wcs.Core diff 0**: `git diff --stat -- backend/src/Wcs.Core` **빈 diff** 확인.

---

## 4. 크래시루프 예방(배포 안전) — PASS
`install-service.ps1` 로직 순서 직접 검토(정적 워크스루):
- **사전 SQL 점검**(`:149-169`)이 **`sc.exe create`(`:171-190`)보다 앞**. `Test-SqlReachable`가 `$false`(연결 불가)면 `Write-Error` + `exit 1` → 서비스 **미생성**(5초 무한 재시작 서비스 미등록). 점검 도구(`Invoke-Sqlcmd`/`sqlcmd`) 부재(`$null`)면 `-SkipSqlCheck` 없이는 중단.
- **비내장 계정 password 게이트**(`:132-147`): 화이트리스트(LocalSystem·LocalService·NetworkService·`NT SERVICE\*`) 외 계정 + `-Password` 미제공 → create 이전 `exit 1`(sc start 1069 예방).
- **PowerShell AST 파싱**: `[System.Management.Automation.Language.Parser]::ParseFile` → **오류 0**.
- `depend= MSSQLSERVER`(기본값, `:52`) → 부팅 순서 크래시 예방.

---

## 5. SCOPE 준수 (가중 5%) — PASS
`git status --porcelain` 확인:
- 운영 README 신규 **0**. 루트 README/CLAUDE.md/master_spec **0**(grep 무매치).
- base `appsettings*.json` 변경 **0**(Trusted_Connection 무접촉).
- 기본 계정 LocalSystem 유지(하향 0, Q5 준수).
- 묶음 A 무접촉. `uninstall-service.ps1` 무접촉.
- 변경은 in-scope 4파일(Program.cs·신규 테스트·install-service.ps1·DEPLOY-ONPREM.md)뿐.

---

## 절대규칙
- **#7** 경로/설정 하드코딩 0 — 실행 코드 `AppContext.BaseDirectory` 파생만(§1-4). Serilog `path`는 appsettings 상대값 유지. ✅
- **#8** Wcs.Core diff 0 — 빈 diff 확인. ✅
- **#1** 무관(PLC 쓰기 큐 무변경). 기본 계정 LocalSystem 유지(스코프 준수·하향 안 함). ✅

## 결론
이 차원(Deployment-safety & Regression)의 모든 평가 기준(로그 위치 결정성·문서-코드 정합·회귀 0·크래시루프 예방·SCOPE·절대규칙 #7/#8)을 fresh 증거로 충족한다. **PASS.**
(APPROVED는 Functional 차원 PASS와의 AND — 이 파일은 Deployment-safety 차원 단독 판정.)

---

## FIX ITER (M1/m1/m5) 재검증 — PASS (2026-08-03)
코드리뷰(Step 4.5) 후 M1(MAJOR·보안) + m1·m5 견고화. 변경 표면 = `install-service.ps1` + `DEPLOY-ONPREM.md §5-3`만(C# 무변경). 이 3건 정확성 + 회귀 0만 재검증. 아래는 이번에 직접 실행한 fresh 증거.

### M1 (보안·핵심 — 자격증명 커맨드라인 leak 제거) — PASS
- **`password=` 토큰 완전 제거**: `grep -niE "password=" install-service.ps1` → 유일 매치가 **`:177` 주석**(`sc.exe create ... password= <평문>` 은 ... 금지 — 금지 사유 설명). **실행 경로(서비스 생성)에 `password=` 토큰 0건.** ✅
- **비내장 계정 → `New-Service -Credential`**(SCM API·커맨드라인 미노출): `:196-213` — `$cred = New-Object PSCredential($Account, $Password)` → `New-Service @newSvcParams`(Name·BinaryPathName·DisplayName·**StartupType=Automatic**·**Credential**·(선택)**DependsOn=$SqlServiceName**). ✅
- **내장/가상 계정(무password) → 기존 `sc.exe create` 유지**: `:181-195`(`if ($isBuiltinAccount)` 분기, `& sc.exe @scArgs`). 원래 자격증명 없음. ✅
- **양 경로 동등성**: `start= auto`(sc:186) ≡ `StartupType Automatic`(New-Service:204). `depend=`(sc:192) ≡ `DependsOn`(New-Service:210). 두 생성 경로가 모두 **공통 후처리**(`:216-224`: `sc.exe description`·`sc.exe failure reset=86400 actions=restart/5000×3`·`Environment` 멀티스트링 레지스트리)로 수렴 → 설명·실패복구·환경변수 동작·의미 보존. ✅
- **sqlcmd fallback `-P <평문>`**: `:109-110`에 커맨드라인 노출 경고 주석만 추가(Invoke-Sqlcmd 우선·통상 미도달). `:112` 실행 코드 동작 불변. ✅

### m1 (내장 계정 별칭 화이트리스트 보강) — PASS
`$builtinAccounts`(`:137-141`)에 `NT AUTHORITY\SYSTEM`(:138)·`NT AUTHORITY\LOCAL SERVICE`(:139)·`NT AUTHORITY\NETWORK SERVICE`(:140) 추가. `-contains` 대소문자 무시 → 이 별칭들이 password 요구로 오거부되지 않음. ✅

### m5 (빈 SecureString 사전 거부) — PASS
password 가드(`:145`) = `($null -eq $Password) -or ($Password.Length -eq 0)`. `-or` 단락평가로 null-safe(왼쪽 true면 `$Password.Length` 미평가) — 빈 SecureString도 null과 동일하게 create 이전 차단(빈 password→sc start 1069 예방). ✅

### 회귀/파싱 — PASS
- **AST 파싱**: `[System.Management.Automation.Language.Parser]::ParseFile` → **오류 0**(재실행).
- **전체 스위트**: `dotnet test backend/Wcs.sln`(clean build+test) → **실패: 0, 통과: 539, 건너뜀: 0**(1m30s). 내 독립 단일 full-suite 재실행에서 **N1 타임아웃 미재현·539 GREEN** → Generator의 "N1 12s 타임아웃 = 기지 타이밍 flake" 귀속 **확인**(단일 RED 없음·안티-flake 교훈 준수, 단일 RED로 FAIL 안 함).
- **#8 Wcs.Core diff 0**: `git diff --stat -- backend/src/Wcs.Core` **빈 diff**(재확인).
- **모듈 1 로그 게이트 무변경**: `Program.cs :42-45`(`ResolveWorkingDirectoryOverride`+`SetCurrentDirectory`) 및 순수 헬퍼(:425) 그대로 — C# 무변경. **#7 유지**(경로 리터럴 0).
- **depend=/사전점검 로직 무변경**: `Test-SqlReachable`(`:75-120`)·SELECT 1 사전점검(`:153-173`, create 이전)·depend= 두 경로 보존. diff가 M1/m1/m5에 국한.
- **문서 §5-3 정합**: 계정 bullet이 `New-Service -Credential`(SCM API·커맨드라인 미노출·감사 4688/Sysmon/SIEM leak 없음)·`NT AUTHORITY\SYSTEM` 별칭·**비어있지 않은** `-Password` 명시. 내장 계정은 `sc.exe create`. 스크립트 동작과 정합·격차 0. ✅

### FIX ITER 판정: **PASS** (변경 3건 정확·회귀 0). 이 차원 최종 **PASS**.
