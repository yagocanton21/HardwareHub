$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$currentPrincipal = [Security.Principal.WindowsPrincipal]::new($currentIdentity)
if (-not $currentPrincipal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -Wait -ArgumentList @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    exit $LASTEXITCODE
}

function Test-PawnIOInstalled {
    return (Test-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO') -or
           (Test-Path 'HKLM:\SYSTEM\CurrentControlSet\Services\PawnIO') -or
           (Test-Path "$env:windir\System32\drivers\pawnio.sys")
}

Write-Host "[1/3] Finalizando processos anteriores do Hardware HUB..." -ForegroundColor Yellow
try {
    taskkill /F /IM HardwareHub.exe 2>$null
    Get-Process -Name HardwareHub -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
} catch {}
Start-Sleep -Milliseconds 600

$pawnInstalled = Test-PawnIOInstalled
if (-not $pawnInstalled) {
    Write-Host "[Driver] Instalando driver assinado de sensores PawnIO automaticamente..." -ForegroundColor Yellow
    $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
    if ($null -eq $winget) {
        Write-Host "[ERRO] O Winget nao esta disponivel. O HUB tentara o instalador alternativo ao iniciar." -ForegroundColor Red
    } else {
        & $winget.Source install --id namazso.PawnIO -s winget -e --silent --accept-package-agreements --accept-source-agreements
        if ($LASTEXITCODE -ne 0 -or -not (Test-PawnIOInstalled)) {
            Write-Host "[AVISO] Nao foi possivel confirmar a instalacao do PawnIO. O HUB tentara novamente ao iniciar." -ForegroundColor Yellow
        } else {
            Write-Host "[Driver] PawnIO instalado e detectado com sucesso." -ForegroundColor Green
        }
    }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    if (Test-Path "$env:ProgramFiles\dotnet\dotnet.exe") {
        $env:PATH = "$env:ProgramFiles\dotnet;$env:PATH"
    }
}

Write-Host "[2/3] Compilando versao mais recente com novas correcoes..." -ForegroundColor Yellow
$proj = "$PSScriptRoot\src\HardwareHub\HardwareHub.csproj"
if (-not (Test-Path $proj)) { $proj = "$PSScriptRoot\HardwareHub.csproj" }
dotnet build $proj -c Debug --nologo

$exe = "$PSScriptRoot\src\HardwareHub\bin\Debug\net9.0-windows\win-x64\HardwareHub.exe"
if (-not (Test-Path $exe)) {
    $exe = "$PSScriptRoot\bin\Debug\net9.0-windows\win-x64\HardwareHub.exe"
}

Write-Host "[3/3] Iniciando o Hardware HUB como Administrador..." -ForegroundColor Cyan
Start-Process $exe -Verb RunAs -WorkingDirectory (Split-Path $exe)
