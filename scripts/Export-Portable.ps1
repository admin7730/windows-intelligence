$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$release = Join-Path $root 'artifacts/release'
$packaging = Join-Path $root 'packaging'
$installer = Join-Path $packaging 'Install-MSIX.ps1'
$package = Join-Path $release 'WindowsIntelligence.msix'
$certificate = Join-Path $release 'WindowsIntelligence-Signing.cer'
$identity = & $installer -PackagePath $package -CertificatePath $certificate -VerifyOnly
$version = ([version]$identity.Version).ToString(3)
$archivePath = Join-Path $release "WindowsIntelligence-$version-x64.zip"

$entries = @(
    $package
    $certificate
    $installer
    (Join-Path $packaging 'Install-MSIX.cmd')
    (Join-Path $packaging 'README.zh-CN.txt')
)
$checksums = Join-Path $release 'SHA256SUMS.txt'
$entries | ForEach-Object {
    $item = Get-Item -LiteralPath $_
    '{0}  {1}' -f (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant(), $item.Name
} | Set-Content -LiteralPath $checksums -Encoding utf8

Add-Type -AssemblyName System.IO.Compression.FileSystem
$partial = "$archivePath.partial"
$file = [IO.File]::Create($partial)
try {
    $zip = [IO.Compression.ZipArchive]::new($file, [IO.Compression.ZipArchiveMode]::Create, $true, [Text.Encoding]::UTF8)
    try {
        foreach ($entry in ($entries + $checksums)) {
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                $zip, $entry, [IO.Path]::GetFileName($entry), [IO.Compression.CompressionLevel]::NoCompression)
        }
    } finally { $zip.Dispose() }
} finally { $file.Dispose() }
Move-Item -LiteralPath $partial -Destination $archivePath -Force
Get-Item -LiteralPath $archivePath
