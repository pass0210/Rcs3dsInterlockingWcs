<#
.SYNOPSIS
    WCS API를 Windows Service로 등록한다 (M5 — UseWindowsService 호스팅).

.DESCRIPTION
    sc.exe로 서비스를 생성하고 자동 시작(start=auto)·복구(실패 시 재시작)·설명을 설정한다.
    실행 파일은 게시(publish) 산출물의 Wcs.Api.exe(자기 완결형 또는 프레임워크 의존)를 가리킨다.
    운영 배포 경로·서비스 계정은 아래 플레이스홀더를 환경에 맞게 수정 후 실행.

    사전 준비(게시):
      dotnet publish src/Wcs.Api/Wcs.Api.csproj -c Release -r win-x64 --self-contained false -o C:\BOWOO\Wcs.Api
    그 후 관리자 PowerShell에서 이 스크립트 실행.

    DB 프로비저닝: 서비스 기동 시 DbInitializer가 자동 Migrate(Database:MigrateOnStartup=true)로
    스키마를 보장한다. 운영은 SeedOnStartup=false(기본)라 테스트 시드는 삽입되지 않는다.

.NOTES
    관리자 권한 필요. 사용법 상세는 운영 README(P4)에서 보강 예정.
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
    # 도메인 계정 사용 시: -Account "DOMAIN\svc-wcs" -Password (Read-Host -AsSecureString) 형태로 확장.
    [string] $Account = "LocalSystem",

    # 환경 변수(ASPNETCORE_ENVIRONMENT). 운영은 Production(시드 off).
    [string] $Environment = "Production"
)

$ErrorActionPreference = "Stop"

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

# binPath= 에 환경변수를 직접 넣을 수 없으므로 서비스 환경은 레지스트리/머신 환경에 의존.
# 여기서는 binPath만 등록하고, ASPNETCORE_ENVIRONMENT는 서비스 생성 후 레지스트리에 주입.
Write-Host "서비스 생성: $ServiceName -> $BinPath"
sc.exe create $ServiceName binPath= "`"$BinPath`"" start= auto obj= $Account DisplayName= "$DisplayName"
if ($LASTEXITCODE -ne 0) { Write-Error "sc.exe create 실패(코드 $LASTEXITCODE)"; exit $LASTEXITCODE }

sc.exe description $ServiceName "$Description" | Out-Null

# 실패 복구: 1차/2차/이후 재시작(5초 후), 카운터 1일마다 리셋.
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null

# ASPNETCORE_ENVIRONMENT를 서비스별 환경변수(Environment 멀티스트링)로 주입.
$svcKey = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
New-ItemProperty -Path $svcKey -Name "Environment" -PropertyType MultiString `
    -Value @("ASPNETCORE_ENVIRONMENT=$Environment") -Force | Out-Null

Write-Host "완료. 시작: sc.exe start $ServiceName  |  상태: sc.exe query $ServiceName"
Write-Host "환경: ASPNETCORE_ENVIRONMENT=$Environment (Production이면 dev 시드 off)"
