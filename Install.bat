@echo off
rem Wrapper para instalar clicando duas vezes (sem precisar mudar a
rem execution policy do PowerShell nem rodar como administrador).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
echo.
pause
