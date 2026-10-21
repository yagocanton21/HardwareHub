Write-Host "[1/3] Finalizando processos anteriores do Hardware HUB..." -ForegroundColor Yellow
try {
    taskkill /F /IM HardwareHub.exe 2>$null
    Get-Process -Name HardwareHub -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
} catch {}
Start-Sleep -Milliseconds 600

$pawnInstalled = Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO'
if (-not $pawnInstalled) {
    Write-Host "[Driver] Instalando driver assinado de sensores PawnIO automaticamente..." -ForegroundColor Yellow
    winget install --id namazso.PawnIO -e --silent --accept-package-agreements --accept-source-agreements
}

Write-Host "[2/3] Compilando versao mais recente com novas correcoes..." -ForegroundColor Yellow
$proj = "$PSScriptRoot\HardwareHub.csproj"
if (-not (Test-Path $proj)) { $proj = "$PSScriptRoot\src\HardwareHub\HardwareHub.csproj" }
dotnet build $proj -c Debug --nologo

$exe = "$PSScriptRoot\bin\Debug\net9.0-windows\win-x64\HardwareHub.exe"
if (-not (Test-Path $exe)) {
    $exe = "$PSScriptRoot\..\..\bin\Debug\net9.0-windows\win-x64\HardwareHub.exe"
}

Write-Host "[3/3] Iniciando o Hardware HUB como Administrador..." -ForegroundColor Cyan
Start-Process $exe -Verb RunAs -WorkingDirectory (Split-Path $exe)
