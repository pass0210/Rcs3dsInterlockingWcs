<#
.SYNOPSIS
    WCS API를 Windows Service로 등록한다 (M5 — UseWindowsService 호스팅).

.DESCRIPTION
    sc.exe로 서비스를 생성하고 자동 시작(start=auto)·복구(실패 시 재시작)·설명·SQL 의존(depend=)을 설정한다.
    실행 파일은 게시(publish) 산출물의 Wcs.Api.exe(자기 완결형 또는 프레임워크 의존)를 가리킨다.
    운영 배포 경로·서비스 계정은 아래 플레이스홀더를 환경에 맞게 수정 후 실행.

    ※ 현장 표준은 이 스크립트가 아니라 수동 sc.exe(docs/DEPLOY-ONPREM.md §9-B) 또는 NSSM(§5-3)이다.
      이 스크립트는 그 대안(프로젝트 내장 자동화)이며, password/depend=/사전 SQL 점검을 포함한다.

    사전 준비(게시):
      dotnet publish backend/src/Wcs.Api/Wcs.Api.csproj -c Release -r win-x64 --self-contained false -o C:\BOWOO\Wcs.Api
    그 후 관리자 PowerShell에서 이 스크립트 실행.

    DB 프로비저닝: 서비스 기동 시 DbInitializer가 자동 Migrate(Database:MigrateOnStartup=true)로 스키마를 보장한다.
    단, 자동 Migrate는 **서비스 실행 계정이 대상 DB에 스키마 변경 권한(db_owner 권장)을 가지고 SQL이 기동 중일
    때만** 성공한다. 권한 부재·SQL 미기동이면 기동 시 MigrateAsync가 throw → 서비스가 재시작 간격으로 크래시
    루프한다. 이를 예방하기 위해 이 스크립트는 (1) 서비스 생성 전 SQL 도달성(SELECT 1)을 점검하고 실패 시 서비스를
    만들지 않으며, (2) depend=로 SQL 서비스 선기동을 강제한다. 운영은 SeedOnStartup=false(기본)라 테스트 시드는
    삽입되지 않는다.

.NOTES
    관리자 권한 필요. 배포 절차·트러블슈팅 상세는 docs/DEPLOY-ONPREM.md
    (§5 설정/서비스 등록·§9-B 현장 표준 재배포·§10 트러블슈팅) 참조.
#>
[CmdletBinding()]
param(
    # ── 운영 환경에 맞게 수정할 플레이스홀더 ──────────────────────────────────
    [string] $ServiceName = "WcsApi",
    [string] $DisplayName = "BOWOO WCS API (3DS Interlocking)",
    [string] $Description  = "물류 테스트라인 WCS — RCS HTTP API + 3DS Modbus 마스터.",

    # 게시 산출물의 실행 파일 경로(플레이스홀더 — 운영 배포 경로로 변경).
    [string] $BinPath = "C:\BOWOO\Wcs.Api\Wcs.Api.exe",

    # 서비스 실행 계정(플레이스홀더). 기본 LocalSystem.
    # LocalSystem·LocalService·NetworkService·가상 서비스 계정(NT SERVICE\*) 외의 도메인/로컬 사용자
    # 계정을 쓰면 -Password (SecureString)가 필수다.
    # 예: -Account "DOMAIN\svc-wcs" -Password (Read-Host -AsSecureString).
    [string] $Account = "LocalSystem",

    # 비내장 계정 로그온 비밀번호(SecureString). 내장/가상 서비스 계정이면 불필요.
    [System.Security.SecureString] $Password,

    # 환경 변수(ASPNETCORE_ENVIRONMENT). 운영은 Production(시드 off).
    [string] $Environment = "Production",

    # 로컬 SQL Server 서비스명(depend= 로 선기동 보장). 기본 인스턴스=MSSQLSERVER / Express=MSSQL$SQLEXPRESS.
    # 빈 문자열이면 depend= 를 생략한다.
    [string] $SqlServiceName = "MSSQLSERVER",

    # ── 설치 전 SQL 도달성 사전 점검(SELECT 1) 대상 ─────────────────────────────
    # 앱이 연결할 대상 DB에 지금 연결 가능한지 확인해 크래시 루프 서비스 등록을 예방한다.
    [string] $SqlServer   = "localhost",
    [string] $SqlDatabase = "Rcs3dsInterlockingWcs",
    # SQL 인증으로 점검하려면(운영 appsettings가 SQL 로그인 사용) 아래 지정. 미지정 시 통합 인증(Trusted).
    [string] $SqlUser,
    [System.Security.SecureString] $SqlPassword,
    # 점검 도구(Invoke-Sqlcmd/sqlcmd) 부재/불가 시에도 강행하려면 지정(크래시 루프 위험 감수).
    [switch] $SkipSqlCheck
)

$ErrorActionPreference = "Stop"

# ── 헬퍼: SecureString → 평문(호출 직전에만 순간 복호화, 화면/로그 출력 금지) ──────
function ConvertFrom-SecureStringPlain {
    param([System.Security.SecureString] $Secure)
    if ($null -eq $Secure) { return $null }
    return (New-Object System.Net.NetworkCredential("", $Secure)).Password
}

# ── 헬퍼: SQL 도달성 점검(SELECT 1) — $true=도달 / $false=불가 / $null=점검 도구 없음 ──
function Test-SqlReachable {
    param(
        [string] $Server,
        [string] $Database,
        [string] $User,
        [System.Security.SecureString] $Pass
    )
    # 1) Invoke-Sqlcmd(SqlServer 모듈) 우선.
    $invoke = Get-Command Invoke-Sqlcmd -ErrorAction SilentlyContinue
    if ($null -ne $invoke) {
        try {
            $p = @{
                ServerInstance         = $Server
                Database               = $Database
                Query                  = "SELECT 1"
                ConnectionTimeout      = 5
                TrustServerCertificate = $true
                ErrorAction            = "Stop"
            }
            if (-not [string]::IsNullOrEmpty($User)) {
                $p["Username"] = $User
                $p["Password"] = (ConvertFrom-SecureStringPlain $Pass)
            }
            Invoke-Sqlcmd @p | Out-Null
            return $true
        } catch {
            Write-Warning "Invoke-Sqlcmd SELECT 1 실패: $($_.Exception.Message)"
            return $false
        }
    }
    # 2) sqlcmd fallback.
    $sqlcmd = Get-Command sqlcmd -ErrorAction SilentlyContinue
    if ($null -ne $sqlcmd) {
        if (-not [string]::IsNullOrEmpty($User)) {
            # ⚠ 주의: sqlcmd -P <평문> 은 커맨드라인에 노출된다(도달 좁음 — Invoke-Sqlcmd 우선이라 통상 미도달).
            #   SQL 인증 사전점검이 필요하면 SqlServer 모듈(Invoke-Sqlcmd) 설치를 권장(커맨드라인 미노출).
            $plain = ConvertFrom-SecureStringPlain $Pass
            & sqlcmd -S $Server -d $Database -U $User -P $plain -C -l 5 -b -Q "SELECT 1" | Out-Null
        } else {
            & sqlcmd -S $Server -d $Database -E -C -l 5 -b -Q "SELECT 1" | Out-Null
        }
        return ($LASTEXITCODE -eq 0)
    }
    # 3) 점검 도구 없음.
    return $null
}

if (-not (Test-Path $BinPath)) {
    Write-Error "실행 파일을 찾을 수 없습니다: $BinPath`n먼저 dotnet publish로 게시한 뒤 -BinPath를 실제 경로로 지정하세요."
    exit 1
}

# 이미 등록돼 있으면 안내 후 종료(중복 생성 방지).
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -ne $existing) {
    Write-Warning "서비스 '$ServiceName'가 이미 존재합니다. 재설치하려면 먼저 uninstall-service.ps1을 실행하세요."
    exit 1
}

# ── 계정 검증: 비내장 계정은 비밀번호 필수(sc start 1069 로그온 실패 예방) ──────
# 내장/가상 서비스 계정(비밀번호 불요): LocalSystem(+NT AUTHORITY\SYSTEM 별칭)·LocalService·
#   NetworkService(표시명 공백형 포함)·NT SERVICE\*. (-contains 는 대소문자 무시.)
$builtinAccounts = @(
    "LocalSystem",                 "NT AUTHORITY\SYSTEM",
    "NT AUTHORITY\LocalService",   "NT AUTHORITY\LOCAL SERVICE",   "LocalService",
    "NT AUTHORITY\NetworkService", "NT AUTHORITY\NETWORK SERVICE", "NetworkService"
)
$isBuiltinAccount = ($builtinAccounts -contains $Account) -or ($Account -like "NT SERVICE\*")
if (-not $isBuiltinAccount) {
    # null 또는 빈 SecureString(Length 0) 모두 거부 — 빈 password 도 sc start 1069(로그온 실패)를 유발.
    if (($null -eq $Password) -or ($Password.Length -eq 0)) {
        Write-Error ("계정 '$Account'은 내장 계정이 아니므로 (비어있지 않은) 로그온 비밀번호가 필요합니다.`n" +
            "  -Password (Read-Host -AsSecureString) 형태로 다시 실행하세요.`n" +
            "  (미제공/빈 값이면 sc start 가 1069(로그온 실패)로 실패합니다.)")
        exit 1
    }
}

# ── 설치 전 SQL 도달성 사전 점검(크래시 루프 서비스 등록 예방) ──────────────────
if ($SkipSqlCheck) {
    Write-Warning "SQL 도달성 사전 점검을 건너뜁니다(-SkipSqlCheck). SQL 미기동/권한부재 시 서비스가 크래시 루프할 수 있습니다."
} else {
    Write-Host "SQL 도달성 사전 점검: 서버='$SqlServer' DB='$SqlDatabase' (SELECT 1)"
    $reachable = Test-SqlReachable -Server $SqlServer -Database $SqlDatabase -User $SqlUser -Pass $SqlPassword
    if ($null -eq $reachable) {
        Write-Error ("SQL 점검 도구(Invoke-Sqlcmd 또는 sqlcmd)를 찾을 수 없어 도달성을 확인할 수 없습니다.`n" +
            "  SqlServer 모듈 설치(Install-Module SqlServer) 또는 sqlcmd 설치 후 재실행하거나,`n" +
            "  위험을 감수하고 -SkipSqlCheck 로 강행하세요(SQL 미기동 시 서비스가 크래시 루프).")
        exit 1
    }
    if (-not $reachable) {
        Write-Error ("SQL 서버에 연결할 수 없습니다(서버='$SqlServer' DB='$SqlDatabase').`n" +
            "  서비스를 생성하지 않고 중단합니다(5초 간격 무한 크래시 루프 예방).`n" +
            "  SQL Server 기동 상태·TCP/IP·방화벽·DB 존재·계정 권한(db_owner)·연결문자열을 확인 후 재실행하세요.`n" +
            "  (SQL 인증을 쓰는 운영 구성은 -SqlUser/-SqlPassword 로 실제 앱 자격증명으로 점검하세요.)")
        exit 1
    }
    Write-Host "SQL 도달성 OK."
}

# ── 서비스 생성 ─────────────────────────────────────────────────────────────
# ★ 보안(M1): 서비스 계정 비밀번호를 어떤 커맨드라인에도 싣지 않는다.
#   `sc.exe create ... password= <평문>` 은 프로세스 커맨드라인(감사 이벤트 4688·Sysmon·SIEM)에
#   평문으로 지속 기록되므로 금지. 비내장 계정은 New-Service 로 -Credential 을 SCM API 에 전달한다(미노출).
#   내장/가상 서비스 계정(비밀번호 없음)은 기존 sc.exe create 유지(커맨드라인에 자격증명 없음).
Write-Host "서비스 생성: $ServiceName -> $BinPath (계정=$Account)"
if ($isBuiltinAccount) {
    # sc.exe 옵션은 '<옵션>= <값>'(등호 뒤 공백) 형식이라 배열 인자로 각 토큰을 분리해 전달한다.
    $scArgs = @(
        "create", $ServiceName,
        "binPath=", "`"$BinPath`"",
        "start=", "auto",
        "obj=", $Account,
        "DisplayName=", $DisplayName
    )
    if (-not [string]::IsNullOrWhiteSpace($SqlServiceName)) {
        # 로컬 SQL 선기동 보장 — SQL 서비스가 뜬 뒤 WCS가 기동(부팅 순서 크래시 예방).
        $scArgs += @("depend=", $SqlServiceName)
    }
    & sc.exe @scArgs
    if ($LASTEXITCODE -ne 0) { Write-Error "sc.exe create 실패(코드 $LASTEXITCODE)"; exit $LASTEXITCODE }
} else {
    # 비내장 계정 — 자격증명을 커맨드라인 없이 SCM API 로 전달(New-Service -Credential).
    # start= auto ≡ -StartupType Automatic / depend= ≡ -DependsOn (기존 동작·의미 보존).
    $cred = New-Object System.Management.Automation.PSCredential($Account, $Password)
    $newSvcParams = @{
        Name           = $ServiceName
        BinaryPathName = "`"$BinPath`""
        DisplayName    = $DisplayName
        StartupType    = "Automatic"
        Credential     = $cred
        ErrorAction    = "Stop"
    }
    if (-not [string]::IsNullOrWhiteSpace($SqlServiceName)) {
        # 로컬 SQL 선기동 보장(depend= 등가) — SQL 서비스 기동 후 WCS 기동.
        $newSvcParams["DependsOn"] = $SqlServiceName
    }
    New-Service @newSvcParams | Out-Null
}

# 이하 공통 후처리(자격증명 없음): 설명·실패 복구 정책·환경변수. 두 생성 경로 모두 동일 적용.
sc.exe description $ServiceName "$Description" | Out-Null

# 실패 복구: 1차/2차/이후 재시작(5초 후), 카운터 1일마다 리셋.
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null

# ASPNETCORE_ENVIRONMENT를 서비스별 환경변수(Environment 멀티스트링)로 주입.
$svcKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
New-ItemProperty -Path $svcKey -Name "Environment" -PropertyType MultiString `
    -Value @("ASPNETCORE_ENVIRONMENT=$Environment") -Force | Out-Null

Write-Host "완료. 시작: sc.exe start $ServiceName  |  상태: sc.exe query $ServiceName"
Write-Host "환경: ASPNETCORE_ENVIRONMENT=$Environment (Production이면 dev 시드 off)"
if (-not [string]::IsNullOrWhiteSpace($SqlServiceName)) {
    Write-Host "의존: depend=$SqlServiceName (이 SQL 서비스가 먼저 기동해야 WCS가 뜸)"
}
$deployDir = Split-Path -Parent $BinPath
Write-Host "앱 로그: 서비스 컨텍스트에서는 exe 인접 폴더 '$deployDir\logs\wcs-*.log' 에 생성됩니다."
