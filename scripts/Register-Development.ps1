param([Parameter(Mandatory)][string]$PublishDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$path = (Resolve-Path -LiteralPath $PublishDirectory).Path
if (!(Test-Path -LiteralPath (Join-Path $path 'WindowsIntelligence.exe'))) { throw 'Select a Windows Intelligence publish directory.' }
& (Join-Path $PSScriptRoot 'New-Assets.ps1') -Directory (Join-Path $path 'Assets')
Copy-Item -LiteralPath (Join-Path $root 'packaging/Public') -Destination $path -Recurse -Force
$manifest = Join-Path $path 'AppxManifest.xml'
Copy-Item -LiteralPath (Join-Path $root 'packaging/Package.appxmanifest') -Destination $manifest -Force
Add-AppxPackage -Register $manifest -ForceApplicationShutdown
Get-AppxPackage 'WindowsIntelligence.Desktop' | Select-Object Name,PackageFullName,PackageFamilyName,SignatureKind,Status
