$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location (Join-Path $root 'frontend')
try {
    npm install --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'Falha em npm install.' }
    npm run build
    if ($LASTEXITCODE -ne 0) { throw 'Falha em npm run build.' }
} finally {
    Pop-Location
}
Push-Location $root
try {
    dotnet run --project src/NexFiscal.Web/NexFiscal.Web.csproj
} finally {
    Pop-Location
}
