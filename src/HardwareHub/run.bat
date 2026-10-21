@echo off
title Hardware HUB Launcher
cd /d "%~dp0"

echo [1/2] Verificando driver de sensores PawnIO...
reg query "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO" >nul 2>&1
if %errorlevel% neq 0 (
    echo [HARDWARE HUB] Instalando driver assinado de sensores PawnIO automaticamente...
    winget install --id namazso.PawnIO -e --silent --accept-package-agreements --accept-source-agreements
)

echo [2/3] Compilando versao mais recente...
dotnet build "%~dp0HardwareHub.csproj" -c Debug --nologo -v q

set EXE_PATH=%~dp0bin\Debug\net9.0-windows\win-x64\HardwareHub.exe
if not exist "%EXE_PATH%" set EXE_PATH=%~dp0src\HardwareHub\bin\Debug\net9.0-windows\win-x64\HardwareHub.exe

echo [3/3] Iniciando o Hardware HUB como Administrador...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process '%EXE_PATH%' -Verb RunAs -WorkingDirectory (Split-Path '%EXE_PATH%')"
