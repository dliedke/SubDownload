@echo off
rem Wrapper para desinstalar clicando duas vezes (sem precisar mudar a
rem execution policy do PowerShell nem rodar como administrador).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1"
echo.
pause
