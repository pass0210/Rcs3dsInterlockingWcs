<#
.SYNOPSIS
    WCS API Windows Service를 중지·삭제한다 (M5).

.DESCRIPTION
    서비스를 중지한 뒤 sc.exe delete로 제거한다. 미존재 시 안내만 하고 정상 종료.

.NOTES
    관리자 권한 필요.
#>
[CmdletBinding()]
param(
    [string] $ServiceName = "WcsApi"
)

$ErrorActionPreference = "Stop"

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -eq $existing) {
    Write-Host "서비스 '$ServiceName'가 존재하지 않습니다 — 삭제할 것 없음."
    exit 0
}

if ($existing.Status -ne "Stopped") {
    Write-Host "서비스 중지: $ServiceName"
    Stop-Service -Name $ServiceName -Force
    # sc.exe delete는 STOP 완료 후가 안전 — 상태가 Stopped가 될 때까지 짧게 대기.
    (Get-Service -Name $ServiceName).WaitForStatus("Stopped", (New-TimeSpan -Seconds 30))
}

Write-Host "서비스 삭제: $ServiceName"
sc.exe delete $ServiceName
if ($LASTEXITCODE -ne 0) { Write-Error "sc.exe delete 실패(코드 $LASTEXITCODE)"; exit $LASTEXITCODE }

Write-Host "완료."
