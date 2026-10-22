@echo off
title Hardware HUB Launcher
cd /d "%~dp0"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run.ps1"
exit /b %errorlevel%
