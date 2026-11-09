# Compila Varco in una cartella pronta da copiare sugli altri PC.
#   .\scripts\publish.ps1                      → self-contained (non serve installare .NET sui PC)
#   .\scripts\publish.ps1 -FrameworkDependent  → più leggero, richiede ".NET 10 Desktop Runtime" su ogni PC
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\publish\Varco'),
    [switch]$FrameworkDependent
)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$sc = if ($FrameworkDependent) { 'false' } else { 'true' }

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
foreach ($p in 'Varco.Agent', 'Varco.Service', 'Varco.Tray') {
    dotnet publish (Join-Path $root "src\$p\$p.csproj") -c Release -r win-x64 --self-contained $sc -o $Output -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Publish di $p fallito" }
}
Copy-Item (Join-Path $PSScriptRoot 'install.ps1'), (Join-Path $PSScriptRoot 'uninstall.ps1') $Output
Write-Host "Pronto in $((Resolve-Path $Output).Path). Copia la cartella sull'altro PC ed esegui install.ps1 come amministratore."
