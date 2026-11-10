#Requires -Version 5.1
<#
  Installs the local redirect for windows.msn.com. MUST run as Administrator (use install.cmd, it elevates).
  Changes made (all reversible with uninstall.ps1):
    1. hosts file:  127.0.0.1 windows.msn.com   (tagged with a marker comment; hosts is backed up first)
    2. A self-signed certificate for windows.msn.com ONLY (not a CA, cannot sign anything else),
       added to LocalMachine\Root so the Widgets WebView2 trusts the local server.
       The private key lives only in certs\server.pfx inside this repo folder (git-ignored).
#>
$ErrorActionPreference = 'Stop'
$root     = Split-Path -Parent $PSScriptRoot
$hostName = 'windows.msn.com'
$marker   = '# winboard-news-proxy'
$friendly = 'winboard-news-proxy'
$certDir  = Join-Path $root 'certs'
$hostsFile = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'

function Test-Admin {
    $p = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

try {
    if (-not (Test-Admin)) { throw 'Run as Administrator (use install.cmd).' }
    New-Item -ItemType Directory -Force $certDir | Out-Null

    # ---- 1. certificate ----
    $pfx = Join-Path $certDir 'server.pfx'
    $trusted = Get-ChildItem Cert:\LocalMachine\Root | Where-Object { $_.FriendlyName -eq $friendly }
    if ($trusted -and (Test-Path $pfx) -and (Test-Path (Join-Path $certDir 'pass.txt'))) {
        Write-Host '[cert] already installed, skipping.'
    } else {
        $trusted | ForEach-Object { Remove-Item "Cert:\LocalMachine\Root\$($_.Thumbprint)" }
        $cert = New-SelfSignedCertificate -Subject "CN=$hostName" -DnsName $hostName `
            -CertStoreLocation Cert:\LocalMachine\My -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
            -NotAfter (Get-Date).AddYears(5) -KeyExportPolicy Exportable -FriendlyName $friendly
        $bytes = New-Object byte[] 18
        [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
        $pass = [Convert]::ToBase64String($bytes)
        Export-PfxCertificate -Cert $cert -FilePath $pfx -Password (ConvertTo-SecureString $pass -AsPlainText -Force) | Out-Null
        $cer = Join-Path $certDir 'server.cer'
        Export-Certificate -Cert $cert -FilePath $cer | Out-Null
        Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
        Remove-Item "Cert:\LocalMachine\My\$($cert.Thumbprint)"   # keep the key only in the pfx
        [IO.File]::WriteAllText((Join-Path $certDir 'pass.txt'), $pass)
        Write-Host "[cert] created and trusted ($($cert.Thumbprint))."
    }

    # ---- 2. hosts ----
    $current = Get-Content $hostsFile -Raw
    if ($current -match [regex]::Escape($marker)) {
        Write-Host '[hosts] entry already present.'
    } else {
        Copy-Item $hostsFile "$hostsFile.winboard.bak" -Force
        $prefix = if ($current.EndsWith("`n")) { '' } else { "`r`n" }
        [IO.File]::AppendAllText($hostsFile, "$prefix127.0.0.1 $hostName $marker`r`n")
        Write-Host "[hosts] added: 127.0.0.1 $hostName (backup: hosts.winboard.bak)"
    }

    ipconfig /flushdns | Out-Null
    Write-Host ''
    Write-Host 'Done. Now start the server (start-server.cmd), then open the Widgets board and press Refresh.' -ForegroundColor Green
} catch {
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
} finally {
    Read-Host 'Press Enter to close'
}
