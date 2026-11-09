#Requires -RunAsAdministrator
# Rimuove Varco. Con -KeepConfig la configurazione (chiave di gruppo, layout) resta in %ProgramData%\Varco.
param([string]$Target = (Join-Path $env:ProgramFiles 'Varco'), [switch]$KeepConfig)
$ErrorActionPreference = 'Continue'

if (Get-Service Varco -ErrorAction SilentlyContinue) {
    Stop-Service Varco -Force
    sc.exe delete Varco | Out-Null
}
Get-Process Varco.Tray, Varco.Agent -ErrorAction SilentlyContinue | Stop-Process -Force
Get-NetFirewallRule -DisplayName 'Varco*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Remove-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'Varco' -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
if (Test-Path $Target) { Remove-Item $Target -Recurse -Force }
if (-not $KeepConfig) { Remove-Item (Join-Path $env:ProgramData 'Varco') -Recurse -Force -ErrorAction SilentlyContinue }
Write-Host 'Varco rimosso.'
