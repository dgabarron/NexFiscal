# NexFiscal V1 - Etapa 01 (SAP HANA): compila, testa e inicia o servico local.
# Uso (na raiz do repositorio):
#   powershell -ExecutionPolicy Bypass -File .\scripts\Run-NexFiscalLocal.ps1
# Opcional: -SkipTests para pular os testes automatizados.
# Este arquivo usa apenas ASCII de proposito (compatibilidade com Windows PowerShell 5.1).
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

function Assert-Tool($name, $hint) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) { throw "$name nao encontrado. $hint" }
}
function Invoke-Step($label, [scriptblock]$action) {
    Write-Host "==> $label" -ForegroundColor Cyan
    & $action
    if ($LASTEXITCODE -ne 0) { throw "Falha em: $label" }
}

Assert-Tool 'dotnet' 'Instale o .NET SDK 8 (https://dotnet.microsoft.com/download/dotnet/8.0).'
Assert-Tool 'npm' 'Instale o Node.js 20 ou superior (https://nodejs.org).'

Push-Location (Join-Path $root 'frontend')
try {
    Invoke-Step 'Instalando dependencias do frontend' { npm ci --no-audit --no-fund }
    Invoke-Step 'Compilando o frontend' { npm run build }
    if (-not $SkipTests) { Invoke-Step 'Testes do frontend' { npm test } }
} finally { Pop-Location }

Push-Location $root
try {
    Invoke-Step 'Compilando o backend' { dotnet build .\NexFiscal.sln -c Release --nologo -v quiet }
    if (-not $SkipTests) { Invoke-Step 'Testes do backend' { dotnet test .\NexFiscal.sln -c Release --no-build --nologo -v quiet } }
    Write-Host ''
    Write-Host 'NexFiscal iniciando. Abra http://127.0.0.1:5187 no navegador deste computador.' -ForegroundColor Green
    Write-Host 'Para encerrar, pressione Ctrl+C nesta janela.' -ForegroundColor Green
    Write-Host ''
    dotnet run --project .\src\NexFiscal.Web\NexFiscal.Web.csproj -c Release --no-build
} finally { Pop-Location }
