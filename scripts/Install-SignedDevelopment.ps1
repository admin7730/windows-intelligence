param([switch]$TrustDevelopmentSigner)
$ErrorActionPreference = 'Stop'
$release = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/release'
$msix = Join-Path $release 'WindowsIntelligence.msix'
$cer = Join-Path $release 'WindowsIntelligence-Development.cer'
$certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($cer)
Add-Type -AssemblyName System.Security.Cryptography.Pkcs
$archive = [IO.Compression.ZipFile]::OpenRead($msix)
try {
    $inputStream = $archive.GetEntry('AppxSignature.p7x').Open()
    try {
        $buffer = [IO.MemoryStream]::new()
        $inputStream.CopyTo($buffer)
        $signature = $buffer.ToArray()
        $buffer.Dispose()
    } finally { $inputStream.Dispose() }
    $signed = [Security.Cryptography.Pkcs.SignedCms]::new()
    $signed.Decode($signature[4..($signature.Length - 1)])
    $signed.CheckSignature($true)
    if ($signed.SignerInfos.Count -ne 1 -or $signed.SignerInfos[0].Certificate.Thumbprint -ne $certificate.Thumbprint) {
        throw 'Development certificate does not match the MSIX signer.'
    }
} finally { $archive.Dispose() }
if ($certificate.Subject -ne 'CN=Windows Intelligence Development') { throw 'Unexpected development publisher.' }
if (Get-Process WindowsIntelligence -ErrorAction SilentlyContinue) {
    throw 'Exit Windows Intelligence from its tray menu before upgrading.'
}
$trustPath = 'Cert:\LocalMachine\TrustedPeople\' + $certificate.Thumbprint
if (!(Test-Path -LiteralPath $trustPath)) {
    if (!$TrustDevelopmentSigner) {
        throw "The signing certificate $($certificate.Thumbprint) is not trusted. Use -TrustDevelopmentSigner only after reviewing this local certificate."
    }
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Trusting the reviewed development certificate requires an administrator PowerShell session.'
    }
    Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
}
if (!(Test-Path -LiteralPath $trustPath)) { throw 'The reviewed certificate is not in Trusted People.' }
Add-AppxPackage -Path $msix
$package = Get-AppxPackage WindowsIntelligence.Desktop
if (!$package -or $package.SignatureKind -eq 'None' -or [string]$package.Status -ne 'Ok') {
    throw 'Windows did not report a healthy signed installation.'
}
$package | Select-Object Name, Version, SignatureKind, Status, PackageFamilyName, InstallLocation
