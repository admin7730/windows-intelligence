param(
    [string]$PackagePath = (Join-Path $PSScriptRoot 'WindowsIntelligence.msix'),
    [string]$CertificatePath = (Join-Path $PSScriptRoot 'WindowsIntelligence-Signing.cer'),
    [switch]$VerifyOnly,
    [string]$ErrorLog
)

$ErrorActionPreference = 'Stop'

function Test-TrustedSigner([string]$Thumbprint) {
    $store = [Security.Cryptography.X509Certificates.X509Store]::new('TrustedPeople', 'LocalMachine')
    try {
        $flags = [Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly -bor
            [Security.Cryptography.X509Certificates.OpenFlags]::OpenExistingOnly
        $store.Open($flags)
        return $store.Certificates.Find([Security.Cryptography.X509Certificates.X509FindType]::FindByThumbprint, $Thumbprint, $false).Count -gt 0
    } catch [Security.Cryptography.CryptographicException] {
        return $false
    } finally { $store.Close() }
}

try {
    $package = (Resolve-Path -LiteralPath $PackagePath).ProviderPath
    $publicCertificate = (Resolve-Path -LiteralPath $CertificatePath).ProviderPath
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if ($PSVersionTable.PSEdition -eq 'Core') {
        Add-Type -AssemblyName System.Security.Cryptography.Pkcs
    } else {
        Add-Type -AssemblyName System.Security
    }
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($publicCertificate)
    if ($certificate.HasPrivateKey) { throw 'The installer requires a public certificate (.cer).' }

    $zip = [IO.Compression.ZipFile]::OpenRead($package)
    try {
        $signatureEntry = $zip.GetEntry('AppxSignature.p7x')
        $manifestEntry = $zip.GetEntry('AppxManifest.xml')
        if (!$signatureEntry -or !$manifestEntry) { throw 'The file is not a signed MSIX package.' }
        $signatureStream = $signatureEntry.Open()
        $manifestStream = $manifestEntry.Open()
        $buffer = [IO.MemoryStream]::new()
        try {
            $signatureStream.CopyTo($buffer)
            $signature = $buffer.ToArray()
            $manifest = [Xml.XmlDocument]::new()
            $manifest.XmlResolver = $null
            $manifest.Load($manifestStream)
        } finally {
            $buffer.Dispose()
            $manifestStream.Dispose()
            $signatureStream.Dispose()
        }
        if ($signature.Length -le 4 -or [Text.Encoding]::ASCII.GetString($signature, 0, 4) -ne 'PKCX') {
            throw 'The MSIX signature format is invalid.'
        }
        $cms = [Security.Cryptography.Pkcs.SignedCms]::new()
        $cms.Decode([byte[]]$signature[4..($signature.Length - 1)])
        $cms.CheckSignature($true)
        if ($cms.SignerInfos.Count -ne 1 -or $cms.SignerInfos[0].Certificate.Thumbprint -ne $certificate.Thumbprint) {
            throw 'The public certificate does not match the MSIX signer.'
        }
        $identity = $manifest.Package.Identity
        if ($identity.Name -ne 'WindowsIntelligence.Desktop' -or $identity.Publisher -ne $certificate.Subject) {
            throw 'The MSIX package identity or publisher does not match Windows Intelligence.'
        }
    } finally { $zip.Dispose() }

    if ($VerifyOnly) {
        [pscustomobject]@{ Name = $identity.Name; Version = $identity.Version; Publisher = $certificate.Subject; Thumbprint = $certificate.Thumbprint }
        return
    }

    if (!(Test-TrustedSigner $certificate.Thumbprint)) {
        Write-Host "Windows needs administrator approval to trust the Windows Intelligence signer: $($certificate.Thumbprint)"
        # Pass only the verified public bytes to the elevated process. It never executes a script from the download folder.
        $import = @'
$ErrorActionPreference = 'Stop'
try {
    $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new([Convert]::FromBase64String('__PUBLIC_BYTES__'))
    $store = [Security.Cryptography.X509Certificates.X509Store]::new('TrustedPeople', 'LocalMachine')
    $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
    try { $store.Add($certificate) } finally { $store.Close(); $certificate.Dispose() }
} catch { exit 1 }
'@
        $import = $import.Replace('__PUBLIC_BYTES__', [Convert]::ToBase64String($certificate.RawData))
        $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($import))
        $powerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $child = Start-Process -FilePath $powerShell -ArgumentList @('-NoProfile','-NonInteractive','-EncodedCommand',$encoded) -Verb RunAs -WindowStyle Hidden -Wait -PassThru
        if ($child.ExitCode -ne 0 -or !(Test-TrustedSigner $certificate.Thumbprint)) {
            throw 'Windows did not add the certificate to Local Machine Trusted People.'
        }
    }

    # Install for the original Windows user. Only the certificate import requires elevation.
    Add-AppxPackage -Path $package -ForceApplicationShutdown
    $installed = Get-AppxPackage -Name $identity.Name | Where-Object { $_.Publisher -eq $identity.Publisher -and $_.Version -eq $identity.Version } | Select-Object -First 1
    if (!$installed -or [string]$installed.Status -ne 'Ok') { throw 'Windows did not report a healthy installation of this version.' }
    $installed | Select-Object Name,Version,Publisher,Status
} catch {
    if ($ErrorLog) { $_ | Out-String | Set-Content -LiteralPath $ErrorLog -Encoding UTF8 }
    throw
}
