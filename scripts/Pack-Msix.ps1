param(
    [Parameter(Mandatory)][string]$AppPath,
    [string]$CertificateThumbprint,
    [string]$TimestampUrl
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$sdk = Get-ChildItem "${env:ProgramFiles(x86)}/Windows Kits/10/bin" -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName 'x64/MakeAppx.exe') } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$sdk) { throw 'Windows SDK MakeAppx.exe not found.' }
$makeAppx = Join-Path $sdk.FullName 'x64/MakeAppx.exe'
$signTool = Join-Path $sdk.FullName 'x64/SignTool.exe'
$release = Join-Path $root 'artifacts/release'
New-Item -ItemType Directory -Force -Path $release | Out-Null
$scratchRoot = (Resolve-Path -LiteralPath ([IO.Path]::GetTempPath())).Path
$scratch = Join-Path $scratchRoot ('wi-msix-' + [guid]::NewGuid().ToString('N'))
$stage = Join-Path $scratch 'payload'
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$msix = Join-Path $scratch 'WindowsIntelligence.msix'
$publishedMsix = Join-Path $release 'WindowsIntelligence.msix'
$pendingMsix = Join-Path $release ('WindowsIntelligence-' + [guid]::NewGuid().ToString('N') + '.msix.partial')
$development = !$CertificateThumbprint
try {
    Copy-Item -Path (Join-Path $AppPath '*') -Destination $stage -Recurse
    & (Join-Path $PSScriptRoot 'New-Assets.ps1') -Directory (Join-Path $stage 'Assets')
    Copy-Item -LiteralPath (Join-Path $root 'packaging/Public') -Destination $stage -Recurse
    [xml]$manifest = Get-Content -LiteralPath (Join-Path $root 'packaging/Package.appxmanifest') -Raw
    if ($development) {
        $cert = Get-ChildItem Cert:/CurrentUser/My | Where-Object {
            $_.Subject -eq $manifest.Package.Identity.Publisher -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(10) -and
            (@($_.EnhancedKeyUsageList | ForEach-Object { $_.ObjectId.Value }) -contains '1.3.6.1.5.5.7.3.3')
        } | Sort-Object NotAfter -Descending | Select-Object -First 1
        if (!$cert) {
            $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $manifest.Package.Identity.Publisher `
                -CertStoreLocation Cert:/CurrentUser/My -KeyExportPolicy NonExportable -HashAlgorithm SHA256 -NotAfter (Get-Date).AddYears(1)
        }
        $cer = Join-Path $release 'WindowsIntelligence-Development.cer'
        Export-Certificate -Cert $cert -FilePath $cer -Force | Out-Null
    } else {
        $cert = Get-Item -LiteralPath "Cert:/CurrentUser/My/$CertificateThumbprint"
        $manifest.Package.Identity.Publisher = $cert.Subject
    }
    $manifest.Save((Join-Path $stage 'AppxManifest.xml'))
    $packLog = Join-Path $release 'makeappx.log'
    & $makeAppx pack /d $stage /p $msix /o *> $packLog
    if ($LASTEXITCODE) { Get-Content $packLog -Tail 30; throw "MakeAppx failed ($LASTEXITCODE)." }
    $signArgs = @('sign','/fd','SHA256','/sha1',$cert.Thumbprint)
    if ($TimestampUrl) { $signArgs += @('/tr',$TimestampUrl,'/td','SHA256') }
    & $signTool @signArgs $msix
    if ($LASTEXITCODE) { throw "SignTool failed ($LASTEXITCODE)." }
    Copy-Item -LiteralPath $msix -Destination $pendingMsix
    Move-Item -LiteralPath $pendingMsix -Destination $publishedMsix -Force
    Export-Certificate -Cert $cert -FilePath (Join-Path $release 'WindowsIntelligence-Signing.cer') -Force | Out-Null
    Get-FileHash -Algorithm SHA256 -LiteralPath $publishedMsix
} finally {
    if (Test-Path -LiteralPath $pendingMsix) { Remove-Item -LiteralPath $pendingMsix -Force }
    $verifiedScratch = (Resolve-Path -LiteralPath $scratch).Path
    $verifiedParent = [IO.Path]::GetDirectoryName($verifiedScratch)
    if ($verifiedParent -ieq $scratchRoot.TrimEnd('\') -and [IO.Path]::GetFileName($verifiedScratch) -match '^wi-msix-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $verifiedScratch -Recurse -Force
    }
}
