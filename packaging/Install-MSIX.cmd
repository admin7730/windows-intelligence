@echo off
setlocal

if not exist "%~dp0Install-MSIX.ps1" goto :missing
if not exist "%~dp0WindowsIntelligence.msix" goto :missing
if not exist "%~dp0WindowsIntelligence-Signing.cer" goto :missing

set "ERRORLEVEL="
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-MSIX.ps1"
if not "%ERRORLEVEL%"=="0" goto :failed

echo.
echo Windows Intelligence installed.
pause
exit /b 0

:missing
echo Windows Intelligence installation files are missing.
echo Download WindowsIntelligence-^<version^>-x64.zip from:
echo https://github.com/admin7730/windows-intelligence/releases/latest
echo Select the installer ZIP under Assets. Source code archives require a build.
echo Use Extract All, then run Install-MSIX.cmd from the extracted folder.
pause
exit /b 1

:failed
echo.
echo Windows Intelligence installation failed. Review the error above.
pause
exit /b 1
