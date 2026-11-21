@echo off
setlocal
cd /d "%~dp0.."
for /f "usebackq delims=" %%I in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "WI_VS=%%I"
if not defined WI_VS exit /b 1
call "%WI_VS%\VC\Auxiliary\Build\vcvars64.bat" >nul
if errorlevel 1 exit /b %errorlevel%
if not exist artifacts\native mkdir artifacts\native
pushd artifacts\native
fxc /nologo /O3 /T vs_5_0 /E VS /Fh SceneVS.h /Vn SceneVS "..\..\src\WinNative\Overlay.hlsl"
if errorlevel 1 exit /b %errorlevel%
fxc /nologo /O3 /T ps_5_0 /E PS /Fh ScenePS.h /Vn ScenePS "..\..\src\WinNative\Overlay.hlsl"
if errorlevel 1 exit /b %errorlevel%
cl /nologo /std:c++20 /EHsc /O2 /MT /LD /W4 /DUNICODE /D_UNICODE /DNOMINMAX /DWIN32_LEAN_AND_MEAN /utf-8 /I. "..\..\src\WinNative\Capture.cpp" "..\..\src\WinNative\Overlay.cpp" /link /OUT:WinNative.dll d3d11.lib dxgi.lib dcomp.lib dwmapi.lib CoreMessaging.lib windowsapp.lib user32.lib gdi32.lib ole32.lib shell32.lib propsys.lib %*
set "WI_RESULT=%errorlevel%"
popd
exit /b %WI_RESULT%
