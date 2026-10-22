# =====================================================================
# Script para Lançar Atualizações Automáticas no GitHub (CI/CD)
# =====================================================================
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "     Hardware HUB - Publicador de Atualizações (GitHub)    " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Verifica se o Git está instalado
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    $possibleGit = @(
        "$env:ProgramFiles\Git\cmd\git.exe",
        "$env:LOCALAPPDATA\Programs\Git\cmd\git.exe"
    )
    foreach ($p in $possibleGit) {
        if (Test-Path $p) {
            $env:PATH = "$(Split-Path $p);$env:PATH"
            break
        }
    }
}

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    Write-Host "`n[!] O Git não está instalado no seu computador." -ForegroundColor Yellow
    Write-Host "Deseja instalar o Git automaticamente agora via winget? (S/N): " -NoNewline -ForegroundColor Green
    $resp = Read-Host
    if ($resp -match "^[sSyY]") {
        Write-Host "Instalando Git..." -ForegroundColor Cyan
        winget install --id Git.Git -e --silent --accept-package-agreements --accept-source-agreements
        $env:PATH = "$env:ProgramFiles\Git\cmd;$env:PATH"
    } else {
        Write-Host "Operação cancelada. Instale o Git para enviar atualizações." -ForegroundColor Red
        exit 1
    }
}

# 2. Inicializa o repositório se necessário
if (-not (Test-Path "$PSScriptRoot\.git")) {
    Write-Host "`n[Git] Inicializando repositório local..." -ForegroundColor Yellow
    git -C "$PSScriptRoot" init -b main
    git -C "$PSScriptRoot" remote add origin https://github.com/yagocanton21/HardwareHub.git 2>$null
}

# 3. Pergunta a nova versão
$csproj = "$PSScriptRoot\src\HardwareHub\HardwareHub.csproj"
$currentVer = "1.0.1"
if (Test-Path $csproj) {
    $content = Get-Content $csproj -Raw
    if ($content -match '<Version>(.*?)</Version>') {
        $currentVer = $matches[1]
    }
}

Write-Host "`nVersão atual: v$currentVer" -ForegroundColor White
Write-Host "Digite a nova versão (ex: 1.0.2 ou pressione Enter para sugerir): " -NoNewline -ForegroundColor Green
$newVerInput = Read-Host
if ([string]::IsNullOrWhiteSpace($newVerInput)) {
    $parts = $currentVer.Split('.')
    if ($parts.Length -ge 3) {
        $parts[2] = [string]([int]$parts[2] + 1)
        $newVer = $parts -join '.'
    } else {
        $newVer = "$currentVer.1"
    }
} else {
    $newVer = $newVerInput.Trim().TrimStart('v')
}

$tag = "v$newVer"
Write-Host "`n-> Atualizando arquivos para a versão $tag..." -ForegroundColor Yellow

# 4. Atualiza os números de versão no .csproj
if (Test-Path $csproj) {
    $content = Get-Content $csproj -Raw
    $content = [regex]::Replace($content, '<Version>.*?</Version>', "<Version>$newVer</Version>")
    $content = [regex]::Replace($content, '<AssemblyVersion>.*?</AssemblyVersion>', "<AssemblyVersion>$newVer.0</AssemblyVersion>")
    $content = [regex]::Replace($content, '<FileVersion>.*?</FileVersion>', "<FileVersion>$newVer.0</FileVersion>")
    Set-Content $csproj $content -NoNewline
}

# 5. Adiciona arquivos e commita
Write-Host "`n[Git] Criando commit da versão $tag..." -ForegroundColor Yellow
git -C "$PSScriptRoot" add .
git -C "$PSScriptRoot" commit -m "Release $tag: melhorias de telemetria, compatibilidade e autoupdate" 2>$null

# 6. Cria a tag
git -C "$PSScriptRoot" tag -a "$tag" -m "Versão $tag" -f

# 7. Envia para o GitHub
Write-Host "`n[Git] Enviando para o GitHub ($tag)..." -ForegroundColor Cyan
git -C "$PSScriptRoot" push origin main --force
git -C "$PSScriptRoot" push origin "$tag" --force

if ($LASTEXITCODE -eq 0) {
    Write-Host "`n==========================================================" -ForegroundColor Green
    Write-Host " SUCESSO! A versão $tag foi enviada ao GitHub! " -ForegroundColor Green
    Write-Host "==========================================================" -ForegroundColor Green
    Write-Host "`nO GitHub Actions começou a compilar o executável portátil na nuvem." -ForegroundColor White
    Write-Host "Acompanhe o processo em: https://github.com/yagocanton21/HardwareHub/actions" -ForegroundColor Cyan
    Write-Host "Assim que terminar, todos os clientes que abrirem o HardwareHub" -ForegroundColor White
    Write-Host "receberão o aviso de atualização automática e atualizarão com 1 clique!" -ForegroundColor Green
} else {
    Write-Host "`n[AVISO] Verifique se você está autenticado no Git/GitHub para enviar alterações." -ForegroundColor Yellow
}
