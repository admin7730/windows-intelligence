@echo off
setlocal
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-MSIX.ps1"
if errorlevel 1 (
    echo.
    echo Windows Intelligence installation failed. Review the error above.
    pause
    exit /b 1
)
echo.
echo Windows Intelligence installed.
pause
exit /b 0
