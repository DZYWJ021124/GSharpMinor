# G# minor - local code signing (best-effort)
# Signs the build output with the local publisher cert in CurrentUser\My so
# Windows treats the publisher as trusted. Skips silently if cert is missing.
param(
    [string]$ExePath = (Join-Path $PSScriptRoot '..\Pa1.App\bin\Release\GSharpMinor.exe')
)
$ErrorActionPreference = 'SilentlyContinue'

$thumb = 'F0165CEB45E61ADA45FDE34A819FC93BFD4F999F'
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Thumbprint -eq $thumb }
if (-not $cert) {
    Write-Host '[sign] local code-signing cert not found, skip'
    exit 0
}
if (-not (Test-Path $ExePath)) {
    Write-Host '[sign] exe not found, skip'
    exit 0
}

# Sign with timestamp when online; fall back to no timestamp (valid until cert expiry).
$sig = Set-AuthenticodeSignature -FilePath $ExePath -Certificate $cert -HashAlgorithm SHA256 -TimestampServer 'http://timestamp.digicert.com'
if ($sig.Status -ne 'Valid') {
    $sig = Set-AuthenticodeSignature -FilePath $ExePath -Certificate $cert -HashAlgorithm SHA256
}
Write-Host ('[sign] ' + $sig.Status + '  ' + $ExePath)
exit 0
