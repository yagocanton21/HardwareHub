# ============================================================
# Script para Gerar o Executável Portátil Único do Hardware HUB
# ============================================================
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " [Hardware HUB] Gerando Executavel Portatil Unico (.EXE)  " -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

$outputDir = "$PSScriptRoot\dist\HardwareHub_Portable"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    if (Test-Path "$env:ProgramFiles\dotnet\dotnet.exe") {
        $env:PATH = "$env:ProgramFiles\dotnet;$env:PATH"
    }
}

Get-Process -Name HardwareHub -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600

Write-Host "`n[1/2] Compilando e embutindo .NET 9, bibliotecas e sensores..." -ForegroundColor Yellow

dotnet publish "$PSScriptRoot\src\HardwareHub\HardwareHub.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $outputDir

if ($LASTEXITCODE -eq 0) {
    # Remove arquivo .pdb de depuracao se existir, deixando apenas o executavel limpo
    Remove-Item "$outputDir\*.pdb" -Force -ErrorAction SilentlyContinue

    $exe = Get-Item "$outputDir\HardwareHub.exe"
    $sizeMb = [Math]::Round($exe.Length / 1MB, 1)

    # Sincroniza automaticamente com a pasta na Area de Trabalho, se ela existir
    $desktopDir = [System.IO.Path]::Combine([System.Environment]::GetFolderPath("Desktop"), "HardwareHub_Portable")
    if (Test-Path $desktopDir) {
        Copy-Item -Path $exe.FullName -Destination "$desktopDir\HardwareHub.exe" -Force
        Write-Host " -> Copiado automaticamente para a Area de Trabalho: $desktopDir\HardwareHub.exe" -ForegroundColor Green
    }

    Write-Host "`n[2/2] Sucesso absoluto! Executavel gerado:" -ForegroundColor Green
    Write-Host " -> Arquivo: $($exe.FullName)" -ForegroundColor White
    Write-Host " -> Tamanho: $sizeMb MB (Totalmente autonomo)" -ForegroundColor White
    Write-Host "`nBasta copiar esse arquivo 'HardwareHub.exe' para qualquer pendrive e rodar em qualquer PC com Windows 10/11!" -ForegroundColor Cyan
} else {
    Write-Host "`n[ERRO] Falha ao compilar executavel portatil." -ForegroundColor Red
}
