# Pubblica una nuova versione di Varco su GitHub: i PC con Varco installato la scaricano e la installano da soli.
#   powershell -ExecutionPolicy Bypass -File scripts\release.ps1 -Version 0.3.0 [-Notes "cosa cambia"]
#
# Serve la chiave privata di firma (%USERPROFILE%\.varco\varco-update-key.pem): senza, i PC rifiutano l'aggiornamento.
# Fa, nell'ordine: controlli, test, Varco.exe con la versione indicata, firma e verifica, tag, push e release GitHub.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes,
    [string]$Key = (Join-Path $env:USERPROFILE '.varco\varco-update-key.pem')
)
# Non 'Stop': in Windows PowerShell 5.1 git e gh scrivono i messaggi normali su stderr e verrebbero presi per errori.
# Gli errori veri si fermano con Check (codice di uscita) e con throw.
$ErrorActionPreference = 'Continue'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$tag = "v$Version"

function Check($what) { if ($LASTEXITCODE -ne 0) { throw "$what non riuscito (codice $LASTEXITCODE)" } }

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Versione non valida: usa il formato 1.2.3" }
if (-not (Test-Path $Key)) { throw "Chiave di firma non trovata: $Key" }

Push-Location $root
try {
    if (git status --porcelain) { throw 'Ci sono modifiche non committate: fai prima il commit.' }
    if (git tag -l $tag) { throw "Il tag $tag esiste già." }
    $branch = (git rev-parse --abbrev-ref HEAD).Trim()

    Write-Host "== Test"
    dotnet test Varco.slnx -c Release -nologo -v q; Check 'Test'

    Write-Host "== Varco.exe $Version"
    $out = Join-Path $root "artifacts\release\$Version"
    & (Join-Path $PSScriptRoot 'publish.ps1') -Output $out -Version $Version
    $exe = Join-Path $out 'Varco.exe'

    Write-Host "== Firma"
    $tool = Join-Path $root 'artifacts\tools\release'
    dotnet build (Join-Path $root 'tools\Varco.Release') -c Release -nologo -v q -o $tool; Check 'Build dello strumento di firma'
    dotnet (Join-Path $tool 'Varco.Release.dll') sign $exe $Version $Key; Check 'Firma'
    dotnet (Join-Path $tool 'Varco.Release.dll') verify $exe; Check 'Verifica della firma'

    Write-Host "== Pubblicazione $tag"
    git tag -a $tag -m "Varco $Version"; Check 'Tag'
    git push origin $branch; Check 'Push del branch'
    git push origin $tag; Check 'Push del tag'
    $ghArgs = @('release', 'create', $tag, $exe, "$exe.sig", '--title', "Varco $Version")
    if ($Notes) { $ghArgs += @('--notes', $Notes) } else { $ghArgs += '--generate-notes' }
    gh @ghArgs; Check 'Creazione della release'
    Write-Host "Fatto: i PC con Varco installato si aggiorneranno entro qualche ora (o subito con Avanzate → Controlla ora)."
}
finally {
    Pop-Location
}
