# 배포 Runbook — 사내 Windows 온프레미스 (단일 포트, HTTP) + 로컬 SQL Server

> 물류 테스트라인 **WCS(Rcs3dsInterlockingWcs)** 를 사내 Windows PC 한 대에
> 백엔드+프론트(단일 포트 **5205**) + **로컬 SQL Server** + **3DS PLC(Modbus) 마스터** 로 올리는 온프레미스 구성.
> **구성**: Windows 서비스(NSSM) + 앱(사내망 HTTP) + DB(같은 PC 의 로컬 SQL Server) + 3DS PLC 직결(RS-485 또는 TCP).
> 앱·DB 가 같은 PC 에 있어 **운영 중 인터넷 불필요**(빌드 시 GitHub/npm 받을 때만 필요).

> ⚠️ 이 앱은 단순 API 가 아니라 **3DS Sorter PLC 의 Modbus 마스터**다 — 배포 PC 가 PLC 와 물리적으로
> 연결(RS-485 COM 또는 TCP)돼 있어야 실제 분류가 동작한다(연결 없이도 앱은 뜨고 UI/API 는 되지만 소터 OFFLINE).

> 환경값(사내 실값으로 교체):
> - **서버 주소**: `192.168.0.150` (이 PC 의 사내 고정 IP) — 사내 사용자/RCS 접속 주소. 포트 `5205`
> - `<DEPLOY_DIR>` : 배포 폴더 (본 문서 기준 `C:\BOWOO\Wcs.Api`)
> - `<SQL_INSTANCE>` : SQL Server 인스턴스 (Express 기본 `localhost\SQLEXPRESS`, 기본 인스턴스면 `localhost`)
> - `<SQL_USER>` / `<SQL_PW>` : 앱 전용 SQL 로그인 계정/비밀번호 (2장에서 생성)

---

## 0. 사전 준비

- [ ] 고정 사내 IP `192.168.0.150` 확보
- [ ] 절전/최대절전/자동 로그오프 끄기 (24/7 상시 가동)
- [ ] 관리자 권한 PowerShell
- [ ] **3DS PLC 연결 경로 확보** — RS-485(USB-RS485 어댑터 + COM 포트) 또는 PLC IP(TCP) 도달
- [ ] (빌드용) 이 PC 가 GitHub/npm 다운로드 가능 — 운영에는 불필요, 빌드 시에만

> ⚠️ **앱 제어(WDAC) 환경 주의**: 사내 PC 에 **WDAC(Windows Defender Application Control)가 Enforce** 로
> 걸려 있으면 서명 안 된 실행 파일(`Wcs.Api.exe`)이 "애플리케이션 제어 정책에서 차단" 됩니다. → **11장** 먼저 확인.
> 진단: `Get-CimInstance -ClassName Win32_DeviceGuard -Namespace root\Microsoft\Windows\DeviceGuard | Select CodeIntegrityPolicyEnforcementStatus` (2=Enforce).

---

## 1. 필요 소프트웨어 설치

> 앱은 빌드 산출물(publish 폴더)을 복사해 서비스로 돌리므로, **Git / .NET SDK / Node.js 는 빌드 PC 에만** 필요.
> SQL Server / NSSM 은 운영 PC 에 필요.

### (권장) 이 사내 PC 에서 직접 빌드 + 운영
| 도구 | 용도 |
|---|---|
| **SQL Server 2022 Express** | 로컬 DB |
| **SSMS** | DB 관리/데이터 이전 |
| **NSSM** | Windows 서비스 등록 (5장) |
| **Git** | 소스 받기/업데이트 |
| **.NET 10 SDK** | 백엔드 빌드(런타임 포함) |
| **Node.js LTS (20+)** | 프론트 빌드(`npm run build`) |

### (대안) 별도 개발 PC 에서 빌드 후 복사 — 이 PC 엔 운영만
- 이 PC: **SQL Server Express** + **SSMS** + (framework-dependent 배포 시) **ASP.NET Core Runtime 10.x** + **NSSM**
- 개발 PC: .NET 10 SDK + Node.js + Git
- self-contained 배포(§4)면 이 PC 에 .NET 런타임도 불요(단, WDAC 서명 이슈는 별개 — 11장).

### 설치 (winget, 관리자 PowerShell) — 설치 후 PowerShell 새로 열기
```powershell
winget install --id Git.Git -e --source winget
winget install --id Microsoft.DotNet.SDK.10 -e --source winget
winget install --id OpenJS.NodeJS.LTS -e --source winget
winget install --id Microsoft.SQLServerManagementStudio -e --source winget
```
| 도구 | winget ID | 수동 다운로드 |
|---|---|---|
| SQL Server 2022 Express | `Microsoft.SQLServer.2022.Express` | <https://www.microsoft.com/sql-server/sql-server-downloads> → Express |
| SSMS | `Microsoft.SQLServerManagementStudio` | <https://aka.ms/ssmsfullsetup> |
| Git for Windows | `Git.Git` | <https://gitforwindows.org> (Git Bash 포함) |
| .NET 10 SDK | `Microsoft.DotNet.SDK.10` | <https://dotnet.microsoft.com/download/dotnet/10.0> → **SDK x64** |
| Node.js LTS (20+) | `OpenJS.NodeJS.LTS` | <https://nodejs.org> → **LTS .msi** |

**NSSM 은 수동 설치 권장:** <https://nssm.cc/download> → zip 해제 → `win64\nssm.exe` 를 `C:\tools\` 로 복사 후 시스템 PATH 에 `C:\tools` 추가 → 새 PowerShell 에서 `nssm version`.

> 사내 보안정책상 외부 다운로드가 막혀 있으면 인터넷 되는 PC 에서 받아 USB 로 이관.

설치 확인:
```powershell
git --version
dotnet --info          # .NET 10 SDK
node --version         # v20+
nssm version
Get-Service 'MSSQL*'   # SQL Server 서비스 Running
```

---

## 2. SQL Server 설정 (SQL 인증)

### 2-1. 혼합 모드(Mixed Mode) 인증
SSMS 접속 → 서버 우클릭 → **속성 → 보안 → "SQL Server 및 Windows 인증 모드"** → 확인 → SQL 서비스 재시작:
```powershell
Restart-Service 'MSSQL$SQLEXPRESS'   # 기본 인스턴스면 'MSSQLSERVER'
```

### 2-2. TCP/IP 프로토콜 활성화(Express 기본 비활성)
**SQL Server 구성 관리자** → SQL Server 네트워크 구성 → `<인스턴스>` 프로토콜 → **TCP/IP "사용"** → SQL 서비스 재시작.

### 2-3. 앱 전용 로그인 + DB 생성
SSMS 새 쿼리(`<SQL_PW>` 는 강한 비밀번호):
```sql
CREATE LOGIN [<SQL_USER>] WITH PASSWORD = N'<SQL_PW>', CHECK_POLICY = ON;
GO
-- 빈 DB 로 시작(권장). 앱 첫 기동 시 DbInitializer 가 스키마를 전부 생성한다.
CREATE DATABASE [Rcs3dsInterlockingWcs];
GO
USE [Rcs3dsInterlockingWcs];
CREATE USER [<SQL_USER>] FOR LOGIN [<SQL_USER>];
ALTER ROLE db_owner ADD MEMBER [<SQL_USER>];   -- 마이그레이션이 테이블/인덱스를 만드므로 db_owner
GO
```

---

## 3. 데이터베이스 초기화 — 빈 DB(권장) 또는 BACPAC 이전

이 프로젝트는 **EF Core 마이그레이션 + `MigrateOnStartup`** 이라, 온프레미스에선 **빈 DB 로 시작**하는 것이 가장 단순하다.

### 3-A. (권장) 빈 DB + 자동 마이그레이션 + 마스터데이터 시드
1. 2-3 에서 만든 빈 `Rcs3dsInterlockingWcs` 사용.
2. 앱 첫 기동 시 `MigrateOnStartup=true` 가 **모든 테이블/인덱스를 자동 생성**(빈 DB 약 10초).
3. 운영 시드는 off(`SeedOnStartup=false`)라 **마스터데이터(소터/셀/슈트)는 직접 넣어야** 한다:
   - **설비 관리 UI** 에서 등록, 또는
   - 현장 시드 SQL 실행(20셀·가용 15):
     ```powershell
     sqlcmd -S localhost\SQLEXPRESS -d Rcs3dsInterlockingWcs -U <SQL_USER> -P <SQL_PW> -C -f 65001 -i scripts\seed-field-20cells.sql
     ```
     (소터 ChuteNo=1 destination + 셀 1~20(가용 1~15) 을 멱등 생성. 현장 구성에 맞게 스크립트 상단 `@availMax`/ChuteNo 조정.)

### 3-B. (선택) 기존 DB 를 BACPAC 으로 이전
기존 DB(Azure SQL 등)의 스키마+데이터를 옮길 때만. BACPAC 은 `__EFMigrationsHistory` 를 포함하므로 이후 앱이 "pending 만 적용" 으로 인식한다.
- **내보내기**(원본 접속 가능한 곳): SSMS → DB 우클릭 → **태스크 → 데이터 계층 응용 프로그램 내보내기…** → `.bacpac`.
- **가져오기**(로컬): SSMS → 데이터베이스 우클릭 → **데이터 계층 응용 프로그램 가져오기…** → 이름 `Rcs3dsInterlockingWcs`.
  - ⚠️ **"스키마 모델을 로드할 수 없습니다 / Sql170… 플랫폼 서비스가 잘못됨"** 오류가 나면, BACPAC 이 로컬 SSMS/DacFx 보다 최신 SQL 버전에서 만들어진 것이다. **최신 SqlPackage** 로 임포트:
    ```powershell
    dotnet tool install -g microsoft.sqlpackage
    sqlpackage /Action:Import /SourceFile:"C:\경로\db.bacpac" /TargetServerName:"localhost\SQLEXPRESS" `
      /TargetDatabaseName:"Rcs3dsInterlockingWcs" /TargetUser:"<SQL_USER>" /TargetPassword:"<SQL_PW>" /TargetTrustServerCertificate:True
    ```
  - import 는 DB 를 새로 만들므로 2-3 의 `CREATE DATABASE` 는 건너뛰고, **import 후** 2-3 의 USER 매핑 블록만 실행.

### 3-C. 초기화 검증
```sql
USE [Rcs3dsInterlockingWcs];
SELECT TOP 5 * FROM __EFMigrationsHistory ORDER BY MigrationId DESC;   -- 마이그레이션 적용 이력
SELECT COUNT(*) destinations FROM destination;                         -- 소터/슈트(마스터데이터)
SELECT COUNT(*) cells FROM cell;
```

---

## 4. 사내 PC 에서 빌드

> 개발은 개발 PC → GitHub `git push`, **빌드·운영은 이 사내 PC**. 아래는 Git Bash.
> ★ 이 프로젝트는 **프론트 빌드 산출물이 `backend/src/Wcs.Api/wwwroot` 로 자동 배치**(vite `build.outDir`)돼
>   publish 에 포함된다 — **프론트를 먼저 빌드**하면 되고, 별도 복사 단계는 없다.

```bash
# 최초 1회만 — 소스 받기 (이후 재배포는 git pull)
git clone https://github.com/bowoosystem/Rcs3dsInterlockingWcs.git
cd Rcs3dsInterlockingWcs
git checkout main          # 운영 릴리스 브랜치

# 1) 프론트 빌드 → backend/src/Wcs.Api/wwwroot (자동 배치)
cd frontend && npm ci && npm run build && cd ..

# 2) 백엔드 publish (wwwroot 포함) — framework-dependent(.NET 10 런타임 가정)
dotnet publish backend/src/Wcs.Api/Wcs.Api.csproj -c Release -r win-x64 --self-contained false -o publish
```
> **대안 (런타임 설치 없이)**: `--self-contained true` → 런타임 포함, 대상 PC 에 .NET 불요 + EF Core 버전 드리프트 차단.
> (단, WDAC Enforce 환경에선 self-contained/framework 무관하게 **서명 안 된 실행 파일이 차단**된다 — 11장.)

배포 폴더로 복사: `./publish` 전체를 `<DEPLOY_DIR>`(예: `C:\BOWOO\Wcs.Api`) 로 복사.

---

## 5. 설정 파일 + NSSM 서비스 등록 (자동 시작)

> ⚠️ **연결문자열은 NSSM 환경변수에 넣지 말 것.** `;` `=` 공백 때문에 깨져서 크래시한다.
> 대신 **`appsettings.Production.json`** 파일에 둔다.

### 5-1. `<DEPLOY_DIR>\appsettings.Production.json` 생성
(`appsettings.json` 위에 병합 오버라이드 — 아래 운영값만 넣으면 됨. 실값으로 교체)
```json
{
  "ConnectionStrings": {
    "WcsDb": "Server=localhost\\SQLEXPRESS;Database=Rcs3dsInterlockingWcs;User Id=<SQL_USER>;Password=<SQL_PW>;TrustServerCertificate=True;Encrypt=False;Connection Timeout=30"
  },
  "Database": {
    "Provider": "SqlServer",
    "MigrateOnStartup": true,
    "SeedOnStartup": false
  },
  "Urls": "http://0.0.0.0:5205",
  "Sorters": [
    {
      "ChuteNo": 1,
      "Transport": "Rtu",
      "PortName": "COM3",
      "BaudRate": 9600,
      "Parity": "Even",
      "StopBits": "One",
      "UnitId": 1,
      "ReadTimeoutMs": 1000,
      "WriteTimeoutMs": 1000,
      "PollIntervalMs": 150,
      "OfflineAfterFailures": 3
    }
  ],
  "Wcs": {
    "InductionFloorMap": { "1": 1, "2": 2 },
    "ChuteStatePush": {
      "FloorHosts": {},
      "Path": "/api/UpdateChuteState"
    }
  }
}
```
> - `ConnectionStrings:WcsDb` 가 이 프로젝트의 키다(`DefaultConnection` 아님).
> - 로컬 SQL 은 `Encrypt=False` + `TrustServerCertificate=True` 권장. 비밀번호 평문이므로 파일 권한을 OS 로 제한.
> - **PLC**: RS-485 면 `Transport:Rtu` + `PortName/BaudRate/Parity/StopBits/UnitId`(★VEICHI 실측값). 네트워크 PLC 면 `"Transport":"Tcp","Host":"192.168.0.50","Port":502`.
> - **RCS 푸시(IF-08)**: RCS 수신 호스트가 준비되면 `ChuteStatePush.FloorHosts` 에 `{"1":"http://<rcs-1f>:3000","2":"http://<rcs-2f>:3000"}`. 비우면 DORMANT(정상 기동·푸시만 안 나감).
> - 이 파일은 서버 로컬 전용(저장소 커밋 안 함). 재배포 시 publish 복사로 덮이지 않게 보존(9장).
> - **추적 로그(S-TRACE-LOG-VIEWER)**: 6개 핸드셰이크 이벤트 전용 로그는 기본 `D:\Rcs3dsInterlockingWcsLogs`(없으면 자동 생성)에 `[N] {json}` 1줄/이벤트로 쌓인다 — 설정 불요(기본값). 경로/롤링/보존 변경만 `TraceLog` 섹션 오버라이드. `/trace` 화면에서 실시간 확인.
> - **안착 지연(S-IF10-CWRITE-SETTLE-DELAY)**: `Timing:SettleDelayMs` 기본 **0(비활성=현행)**. 3DS PLC 낙하 지연이 없어 제품 안착 전 소터 이동이 문제면, IF-10 수신~안착 물리 소요를 실측해 그 ms 를 양수로 넣어 활성(소터별은 `Sorters[].Timing.SettleDelayMs`). 응답 타이밍엔 영향 없음(지연은 백그라운드 C 기입 전에만).

### 5-2. (배포 전 1회) 직접 실행으로 설정 검증
```powershell
cd C:\BOWOO\Wcs.Api
$env:ASPNETCORE_ENVIRONMENT="Production"
.\Wcs.Api.exe
```
`[DbInitializer] Migrate 완료` + `Now listening on: http://0.0.0.0:5205` → 정상(`Ctrl+C` 종료 후 5-3).
`SqlException` 이면 2장 SQL 설정/계정 확인. **"애플리케이션 제어 정책에서 차단"** 이면 11장(WDAC).

### 5-3. NSSM 서비스 등록 — 환경변수는 단순 값만
```powershell
nssm install WcsApi "C:\BOWOO\Wcs.Api\Wcs.Api.exe"
nssm set WcsApi AppDirectory "C:\BOWOO\Wcs.Api"

# 연결문자열/PLC/층맵은 appsettings.Production.json 에서 읽음 — 여기선 환경만
nssm set WcsApi AppEnvironmentExtra "ASPNETCORE_ENVIRONMENT=Production" "ASPNETCORE_URLS=http://0.0.0.0:5205"

# 지연 자동 시작(부팅 시 SQL Server 먼저 뜬 뒤) + 크래시 재시작
nssm set WcsApi Start SERVICE_DELAYED_AUTO_START
nssm set WcsApi AppExit Default Restart
nssm set WcsApi AppRestartDelay 5000

# 로그 캡처(선택) — 앱 자체 Serilog 는 <DEPLOY_DIR>\logs\wcs-*.log 에도 남음
nssm set WcsApi AppStdout "C:\BOWOO\Wcs.Api\logs\nssm-out.log"
nssm set WcsApi AppStderr "C:\BOWOO\Wcs.Api\logs\nssm-err.log"

nssm start WcsApi
Start-Sleep 8
Get-NetTCPConnection -LocalPort 5205 -ErrorAction SilentlyContinue   # 5205 LISTEN 확인
```
> 이미 등록돼 있으면 설정만 교체 후 `Restart-Service WcsApi`.
> **대안(프로젝트 내장)**: NSSM 대신 `scripts\install-service.ps1 -BinPath C:\BOWOO\Wcs.Api\Wcs.Api.exe -Environment Production` (sc.exe + UseWindowsService). NSSM 이 로그/재시작 제어가 더 편해 권장.

---

## 6. Windows 방화벽 (인바운드 5205)

```powershell
New-NetFirewallRule -DisplayName "WCS API 5205" `
  -Direction Inbound -Protocol TCP -LocalPort 5205 -Action Allow `
  -Profile Domain,Private
```
> 사내망만 허용. 더 좁히려면 `-RemoteAddress <RCS_IP>,<사내대역>`.

---

## 7. 접속 확인

| URL | 기대 결과 |
|---|---|
| 이 PC: `http://localhost:5205/health` | `{"status":"ok","db":true,"sorters":[...]}` (**db:true** = 로컬 SQL 접속 성공) |
| 다른 사내 PC: `http://192.168.0.150:5205/` | 프론트엔드 SPA (모니터링 UI) |
| 다른 사내 PC: `http://192.168.0.150:5205/api/monitor/destinations` | 목적지 목록 |

```powershell
curl http://localhost:5205/health          # db:true / sorters 배열 확인
```
> 소터가 `sorters:[]` 면 마스터데이터 미등록(3-A) 또는 PLC 미연결. 로그 `logs\wcs-*.log` 에서 소터 online 여부 확인.

---

## 8. RCS 연동 안내 (3DS Interlocking — IF-05/09/10 + IF-08)

| IF | 메서드 | URL | 용도 |
|:---:|:---:|---|---|
| IF-05 | `POST` | `http://192.168.0.150:5205/api/v1/destination-query` | 투입 가부/목적지 질의 |
| IF-09 | `POST` | `http://192.168.0.150:5205/api/v1/arrival-report` | AGV 도착 보고 |
| IF-10 | `POST` | `http://192.168.0.150:5205/api/v1/deposit-report` | 틸트/분류 보고 |
| IF-08 | `PUT`(WCS→RCS) | `{FloorHosts 의 호스트}/api/UpdateChuteState` | 슈트 수용상태 아웃바운드 푸시(층별 호스트) |

> IF-08 은 WCS 가 RCS 로 **내보내는** 방향이며 `ChuteStatePush.FloorHosts` 설정 시 활성(미설정=DORMANT).
> API 정의 상세: `docs/wcs_rcs_interface_kr.html`, `docs/wcs_3ds_unified_sequence.html`.

---

## 9. 업데이트 절차 (재배포)

개발 PC 에서 `git push` 후, **사내 PC** 에서:
```bash
git pull
cd frontend && npm ci && npm run build && cd ..          # 프론트 → wwwroot 자동
dotnet publish backend/src/Wcs.Api/Wcs.Api.csproj -c Release -r win-x64 --self-contained false -o publish
```
```powershell
nssm stop WcsApi
Copy-Item C:\BOWOO\Wcs.Api\appsettings.Production.json C:\BOWOO\Wcs.Api\appsettings.Production.json.bak -Force  # 안전 백업
Copy-Item -Recurse -Force <클론폴더>\publish\* C:\BOWOO\Wcs.Api\    # 덮어쓰기(로컬 설정 보존)
nssm start WcsApi
```
> ⚠️ `appsettings.Production.json` 보존: `-Force` 덮어쓰기는 유지됨. **전체 삭제·`robocopy /MIR` 금지**.
> ⚠️ 일부 DLL 만 교체 금지(버전 불일치 크래시) — 항상 publish **전체** 교체.
> DB 마이그레이션은 앱 시작 시 `DbInitializer` 가 로컬 DB 에 자동 적용(테이블/인덱스 추가만, 데이터 보존).
> 참고: 마이그레이션은 릴리스마다 다르며 앱이 자동 적용한다(과거 예: `AddHotPathIndexes`·`AddPieceArchivedAt`·`AddSorterCommandProcessingTimes`). **안착지연·추적로그 릴리스는 스키마 마이그레이션 0**(코드/설정만).

### 9-B. (현장 표준 — self-contained + sc.exe) 재배포
> 현재 사내 PC 는 **.NET 런타임 미설치 + WDAC** 라 **self-contained 게시**로 배포하고, 서비스는 **sc.exe 로 등록된 `WcsApi`** 다(nssm 아님 — nssm 명령은 이 서비스에 안 먹는다). 게시 산출물은 개발/빌드 PC 에서 **`D:\프로그램\publish-sc`** 로 만들어 현장으로 옮긴다.

**1) 빌드 PC — self-contained 게시(프론트 먼저):**
```bash
git checkout main && git pull
npm --prefix frontend run build        # → backend/src/Wcs.Api/wwwroot 자동 배치(별도 복사 없음)
dotnet publish backend/src/Wcs.Api/Wcs.Api.csproj -c Release -r win-x64 --self-contained true -o "D:/프로그램/publish-sc"
```
> 산출물 `D:\프로그램\publish-sc` 전체를 현장 PC 로 이관(USB/네트워크). self-contained 라 현장에 .NET 런타임 불요.

**2) 현장 PC — 서비스 교체(관리자 PowerShell):**
```powershell
sc.exe stop WcsApi
sc.exe query WcsApi        # STATE = STOPPED 확인(완전히 멈춘 뒤 복사 — 안 그러면 exe/dll 잠겨 복사 실패)

Copy-Item C:\BOWOO\Wcs.Api\appsettings.Production.json C:\BOWOO\Wcs.Api\appsettings.Production.json.bak -Force   # 안전 백업
Copy-Item -Recurse -Force <이관한 publish-sc 경로>\* C:\BOWOO\Wcs.Api\    # 덮어쓰기(Production.json 보존)

sc.exe start WcsApi
sc.exe query WcsApi        # STATE = RUNNING 확인
```
> ⚠️ 반드시 **STOPPED 확인 후 복사**. `robocopy /MIR`·폴더 전체 삭제 금지(Production.json 유실). `Copy-Item -Force` 덮어쓰기만.
> ⚠️ publish-sc 에는 `appsettings.Production.json` 이 없으므로 `-Force` 덮어써도 현장 Production.json 은 보존된다(1의 백업은 안전망).
> `Stop-Service WcsApi` / `Start-Service WcsApi` / `Get-Service WcsApi` 순정 cmdlet 도 동일.

**3) 로그 확인** — sc.exe 서비스는 작업디렉토리가 `C:\WINDOWS\System32` 라 앱의 상대 `logs/` 가 **거기로** 풀린다:
```powershell
Get-ChildItem C:\WINDOWS\System32\logs\wcs-*.log | Sort LastWriteTime -Desc | Select -First 1 FullName, LastWriteTime
# 전용 추적 로그(S-TRACE-LOG-VIEWER)는 설정 경로(기본 D:\Rcs3dsInterlockingWcsLogs):
Get-ChildItem D:\Rcs3dsInterlockingWcsLogs\trace-*.log
```
> (NSSM 등록 서비스면 §5-3 대로 `AppDirectory`=`C:\BOWOO\Wcs.Api` 라 로그가 `C:\BOWOO\Wcs.Api\logs\` — 등록 방식에 따라 위치가 다르다.)

---

## 10. 트러블슈팅

### 서비스가 안 뜸 / 바로 멈춤
```powershell
nssm status WcsApi
Get-Content C:\BOWOO\Wcs.Api\logs\nssm-err.log -Tail 40
Get-Content C:\BOWOO\Wcs.Api\logs\wcs-*.log -Tail 40
```
> **sc.exe 등록 서비스**(현장 표준·9-B)면 `nssm status` 대신 `sc.exe query WcsApi`, 앱 로그는 작업디렉토리(system32) 기준 **`C:\WINDOWS\System32\logs\wcs-*.log`** 에 있다. `-Tail` 은 상태 푸시/폴 스팸에 묻히니 `Select-String -Path <log> -Pattern "IF05|IF09|IF10|HANDSHAKE|FULL|ERROR|WARN|OFFLINE"` 로 필터해서 본다.

### 서비스는 Running 인데 5205 안 열림 / `ConnectionString 초기화되지 않음`
- 원인: 연결문자열을 NSSM 환경변수에 넣어 깨짐 → 크래시 루프.
- 해결: 연결문자열을 `appsettings.Production.json`(5-1)으로, NSSM 환경변수는 `ASPNETCORE_ENVIRONMENT`/`ASPNETCORE_URLS` 만(5-3) → `Restart-Service WcsApi`.

### DB 연결 실패 (`/health` 의 `db:false`) — 로컬 SQL Server
- `Get-Service 'MSSQL*'` 실행 중인지 / 혼합 모드(2-1) / TCP(2-2) / `<SQL_INSTANCE>` 정확한지(`localhost\SQLEXPRESS` vs `localhost`) / 로그인·권한(db_owner, 2-3) / `TrustServerCertificate=True;Encrypt=False` / `wcs-*.log` 의 SqlException.
- **마이그레이션 타임아웃(30s)**: DB 가 "스키마는 있는데 `__EFMigrationsHistory` 없음" 비정상 상태면 발생 → 빈 DB 로 재생성(3-A) 또는 SqlPackage/스크립트로 정상화.

### 소터 OFFLINE (`sorters` 비었거나 상태 이상)
- 마스터데이터 미등록(3-A) — 설비 관리 UI / seed SQL 로 소터·셀 등록.
- PLC 연결 — `Transport`/`PortName`/`BaudRate`/`UnitId`(RTU) 또는 `Host`/`Port`(TCP), 케이블·전원.

### 다른 PC 에서 접속 안 됨 (localhost 는 됨)
- 방화벽 5205(6장) / `ASPNETCORE_URLS`(또는 appsettings `Urls`)=`http://0.0.0.0:5205` / 원격에서 `Test-NetConnection 192.168.0.150 -Port 5205`.

### `npm` 빌드 시 "스크립트를 실행할 수 없습니다 / npm.ps1"
- PowerShell 실행 정책 → `Set-ExecutionPolicy -Scope CurrentUser RemoteSigned` 또는 **Git Bash** 에서 빌드.

---

## 11. ⚠️ WDAC(앱 제어) 환경 배포 — 서명 안 된 실행 파일 차단

사내 PC 에 **WDAC(Windows Defender Application Control)가 Enforce** 로 배포돼 있으면, 서명 안 된 `Wcs.Api.exe`(self-contained/framework 무관)가 **"애플리케이션 제어 정책에서 이 파일을 차단했습니다"** 로 막힌다. 이는 **정당한 회사 보안 통제**이므로 우회하지 말고 정식 경로로 해결한다.

**진단:**
```powershell
Get-CimInstance -ClassName Win32_DeviceGuard -Namespace root\Microsoft\Windows\DeviceGuard |
  Select-Object CodeIntegrityPolicyEnforcementStatus            # 2 = Enforce
Get-AuthenticodeSignature C:\BOWOO\Wcs.Api\Wcs.Api.exe | Select Status   # NotSigned = 차단 대상
Get-WinEvent -LogName 'Microsoft-Windows-CodeIntegrity/Operational' -MaxEvents 20 |
  Where-Object Id -in 3077,3076 | Select TimeCreated, Message  # 어떤 파일이 차단됐는지
```

**해결 (택1 — IT/보안팀 협의 필요):**
1. **IT 허용목록(allowlist) 등록** — 가장 확실. WCS 실행 파일을 WDAC 정책에 **게시자(서명자)/파일 해시/경로** 규칙으로 추가 요청.
2. **회사 코드서명 인증서로 서명** — 그 서명자가 WDAC 정책에 신뢰돼 있어야 통과:
   ```powershell
   signtool sign /fd SHA256 /a /tr http://timestamp.digicert.com /td SHA256 `
     C:\BOWOO\Wcs.Api\Wcs.Api.exe C:\BOWOO\Wcs.Api\Wcs.*.dll
   ```
3. **WDAC 미적용 서버에 배포** — 실제 운영 서버가 별도 머신이고 앱 제어 정책이 없다면 그 서버에 배포.

> WDAC 정책은 IT 가 중앙 배포·UEFI 잠금일 수 있어 임의 해제는 부적절/불가. 반드시 IT 채널로 진행.

---

## 12. 참고
- 온프레미스/설정 키 상세: `backend/src/Wcs.Api/appsettings.json` 주석
- 스펙: `docs/SPEC.md` · `docs/ERD.md`
- RCS 인터페이스: `docs/wcs_rcs_interface_kr.html` · 통합 시퀀스: `docs/wcs_3ds_unified_sequence.html`
- 서비스 스크립트: `scripts/install-service.ps1` · `scripts/uninstall-service.ps1` · 현장 시드: `scripts/seed-field-20cells.sql`
