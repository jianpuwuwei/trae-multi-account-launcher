@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"

if not exist "bin\TraeSandbox.exe" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
    if errorlevel 1 (
        pause
        exit /b 1
    )
)

"bin\TraeSandbox.exe" dual 2
if errorlevel 1 pause
exit /b %ERRORLEVEL%
