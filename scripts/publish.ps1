# Crea publish\Varco.exe: un unico file che fa da setup, icona, servizio e agent.
# Non serve installare .NET sui PC: il runtime è incluso nel file.
#   powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
#   -Version 1.2.3 per una versione diversa da quella in Directory.Build.props (lo usa scripts\release.ps1).
param([string]$Output = (Join-Path $PSScriptRoot '..\publish'), [string]$Version)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }
$extra = @()
if ($Version) { $extra += "-p:Version=$Version" }
dotnet publish (Join-Path $root 'src\Varco.App\Varco.App.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -o $Output -nologo -v q @extra
if ($LASTEXITCODE -ne 0) { throw 'Publish fallito' }

$exe = Join-Path (Resolve-Path $Output).Path 'Varco.exe'
Write-Host ("Pronto: {0} ({1:N0} MB)" -f $exe, ((Get-Item $exe).Length / 1MB))
Write-Host "Copialo su ogni PC e aprilo con un doppio clic."
