param([string]$CertificateThumbprint, [string]$TimestampUrl, [switch]$SkipSetup, [switch]$WithoutModels)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location -LiteralPath $root
try {
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $dotnet = Join-Path $root '.tooling/dotnet/dotnet.exe'
    if (!(Test-Path -LiteralPath $dotnet)) { $dotnet = 'dotnet' }
    & cmd.exe /d /c scripts\Build-Native.cmd
    if ($LASTEXITCODE) { throw "Native build failed ($LASTEXITCODE)." }
    $dist = Join-Path $root ('artifacts/publish-' + [guid]::NewGuid().ToString('N'))
    & $dotnet publish src/WindowsIntelligence -c Release -r win-x64 --self-contained -o $dist
    if ($LASTEXITCODE) { throw "Managed build failed ($LASTEXITCODE)." }
    Copy-Item -LiteralPath 'artifacts/native/WinNative.dll' -Destination $dist
    New-Item -ItemType Directory -Force (Join-Path $dist 'Models') | Out-Null
    if (!$WithoutModels) {
        if (!(Test-Path artifacts/models/ASR/tokens.txt) -or !(Test-Path artifacts/models/ASR/SenseVoice/model.int8.onnx) -or
            !(Test-Path artifacts/models/ASR/Qwen3/decoder.int8.onnx) -or !(Test-Path artifacts/models/ASR/silero_vad.onnx) -or
            !(Test-Path artifacts/models/TTS/model.onnx)) { & scripts/Get-Models.ps1 }
        foreach ($model in 'ASR','TTS') {
            $local = Join-Path $root "artifacts/models/$model"
            Copy-Item -LiteralPath $local -Destination (Join-Path $dist 'Models') -Recurse
        }
    }
    Copy-Item -LiteralPath 'docs/third-party.md' -Destination $dist
    & scripts/Pack-Msix.ps1 -AppPath $dist -CertificateThumbprint $CertificateThumbprint -TimestampUrl $TimestampUrl
    if (!$SkipSetup) {
        $iscc = (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source
        if (!$iscc) { $iscc = Join-Path $env:LOCALAPPDATA 'Programs/Inno Setup 6/ISCC.exe' }
        if (!(Test-Path -LiteralPath $iscc)) { $iscc = "${env:ProgramFiles(x86)}/Inno Setup 6/ISCC.exe" }
        if (!(Test-Path -LiteralPath $iscc)) { throw 'Inno Setup 6 compiler not found.' }
        & $iscc (Join-Path $root 'packaging/Installer.iss')
        if ($LASTEXITCODE) { throw "Inno Setup failed ($LASTEXITCODE)." }
        if ($CertificateThumbprint) {
            $signTool = Get-ChildItem "${env:ProgramFiles(x86)}/Windows Kits/10/bin" -Filter signtool.exe -Recurse |
                Where-Object { $_.Directory.Name -eq 'x64' } | Sort-Object FullName -Descending | Select-Object -First 1
            $signArgs = @('sign','/fd','SHA256','/sha1',$CertificateThumbprint)
            if ($TimestampUrl) { $signArgs += @('/tr',$TimestampUrl,'/td','SHA256') }
            & $signTool.FullName @signArgs 'artifacts/release/WindowsIntelligence-Setup.exe'
            if ($LASTEXITCODE) { throw "Setup signature failed ($LASTEXITCODE)." }
        }
    }
    if (!$WithoutModels) { & scripts/Export-Portable.ps1 }
    Write-Output "Publish directory: $dist"
} finally { Pop-Location }
