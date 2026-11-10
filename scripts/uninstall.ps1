#Requires -Version 5.1
<# Reverts install.ps1: removes the hosts line and the trusted certificate. Run as Administrator (use uninstall.cmd). #>
$ErrorActionPreference = 'Stop'
$root      = Split-Path -Parent $PSScriptRoot
$marker    = '# winboard-news-proxy'
$friendly  = 'winboard-news-proxy'
$hostsFile = Join-Path $env:SystemRoot 'System32\drivers\etc\hosts'

function Test-Admin {
    $p = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    $p.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

try {
    if (-not (Test-Admin)) { throw 'Run as Administrator (use uninstall.cmd).' }

    $lines = Get-Content $hostsFile
    $kept  = $lines | Where-Object { $_ -notmatch [regex]::Escape($marker) }
    if ($kept.Count -ne $lines.Count) {
        Set-Content -Path $hostsFile -Value $kept
        Write-Host '[hosts] entry removed.'
    } else {
        Write-Host '[hosts] no entry found.'
    }

    foreach ($store in 'Root', 'My') {
        Get-ChildItem "Cert:\LocalMachine\$store" | Where-Object { $_.FriendlyName -eq $friendly } | ForEach-Object {
            Remove-Item "Cert:\LocalMachine\$store\$($_.Thumbprint)"
            Write-Host "[cert] removed from $store."
        }
    }

    $certDir = Join-Path $root 'certs'
    if (Test-Path $certDir) { Remove-Item $certDir -Recurse -Force; Write-Host '[certs] local key files deleted.' }

    ipconfig /flushdns | Out-Null
    Write-Host 'Reverted. The board will show the original error again.' -ForegroundColor Green
} catch {
    Write-Host "FAILED: $($_.Exception.Message)" -ForegroundColor Red
} finally {
    Read-Host 'Press Enter to close'
}
