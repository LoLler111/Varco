#Requires -RunAsAdministrator
# Installa Varco su questo PC: servizio Windows, regole firewall, avvio automatico dell'icona.
# Da eseguire (come amministratore) dalla cartella prodotta da publish.ps1.
param([string]$Target = (Join-Path $env:ProgramFiles 'Varco'))
$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot

foreach ($f in 'Varco.Agent.exe', 'Varco.Service.exe', 'Varco.Tray.exe') {
    if (-not (Test-Path (Join-Path $source $f))) { throw "$f non trovato in ${source}: esegui prima scripts\publish.ps1" }
}

# 1. Ferma l'eventuale versione precedente
if (Get-Service Varco -ErrorAction SilentlyContinue) {
    Stop-Service Varco -Force -ErrorAction SilentlyContinue
}
Get-Process Varco.Tray, Varco.Agent -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500

# 2. Copia i file
if ((Resolve-Path $source).Path -ne $Target) {
    New-Item -ItemType Directory -Force $Target | Out-Null
    Copy-Item (Join-Path $source '*') $Target -Recurse -Force
}
$agent = Join-Path $Target 'Varco.Agent.exe'
$service = Join-Path $Target 'Varco.Service.exe'
$tray = Join-Path $Target 'Varco.Tray.exe'

# 3. Servizio (LocalSystem, avvio automatico, riavvio se cade)
if (-not (Get-Service Varco -ErrorAction SilentlyContinue)) {
    New-Service -Name Varco -BinaryPathName "`"$service`"" -DisplayName 'Varco' -StartupType Automatic `
        -Description 'Condivide mouse e tastiera tra PC della rete locale, con riconnessione automatica.' | Out-Null
}
sc.exe failure Varco reset= 60 actions= restart/1000/restart/1000/restart/5000 | Out-Null

# 4. Firewall: tutti i profili (dopo una riconnessione Windows può classificare la rete come "Pubblica")
Get-NetFirewallRule -DisplayName 'Varco*' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule -DisplayName 'Varco (TCP)' -Direction Inbound -Program $agent -Protocol TCP -LocalPort 47800 -Action Allow -Profile Any | Out-Null
New-NetFirewallRule -DisplayName 'Varco (discovery UDP)' -Direction Inbound -Program $agent -Protocol UDP -LocalPort 47801 -Action Allow -Profile Any | Out-Null

# 5. Icona nella tray all'accesso di ogni utente
Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'Varco' -Value "`"$tray`""

Start-Service Varco
Start-Process $tray
Write-Host "Varco installato in $Target."
Write-Host "Primo PC: icona Varco → Impostazioni → 'Nuovo gruppo' e copia la chiave."
Write-Host "Altri PC: incolla la chiave in 'Unisciti'."
