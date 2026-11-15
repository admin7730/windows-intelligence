$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location -LiteralPath $root
try {
    $dotnet = if (Test-Path '.tooling/dotnet/dotnet.exe') { '.tooling/dotnet/dotnet.exe' } else { 'dotnet' }
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    & cmd.exe /d /c scripts\Build-Native.cmd
    if ($LASTEXITCODE) { throw 'Native build failed.' }
    $dist = 'artifacts/test-run'
    & $dotnet publish tests/WindowsIntelligence.Tests -c Release -r win-x64 --self-contained -o $dist
    if ($LASTEXITCODE) { throw 'Test build failed.' }
    Copy-Item artifacts/native/WinNative.dll $dist -Force
    New-Item -ItemType Directory -Force (Join-Path $dist 'Models') | Out-Null
    foreach ($model in 'ASR','TTS') {
        Copy-Item -LiteralPath "artifacts/models/$model" -Destination (Join-Path $dist 'Models') -Recurse -Force
    }
    & (Join-Path $dist 'WindowsIntelligence.Tests.exe')
    if ($LASTEXITCODE) { throw "Test suite failed ($LASTEXITCODE)." }
} finally { Pop-Location }
