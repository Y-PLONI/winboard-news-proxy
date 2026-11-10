# Read-only health check (no admin needed): shows whether each piece of the setup is in place.
$hostName = 'windows.msn.com'
$marker   = '# winboard-news-proxy'
$friendly = 'winboard-news-proxy'
$root     = Split-Path -Parent $PSScriptRoot

$hosts = Get-Content (Join-Path $env:SystemRoot 'System32\drivers\etc\hosts') -ErrorAction SilentlyContinue
"hosts entry     : " + [bool]($hosts | Where-Object { $_ -match [regex]::Escape($marker) })
"cert trusted    : " + [bool](Get-ChildItem Cert:\LocalMachine\Root | Where-Object { $_.FriendlyName -eq $friendly })
"pfx present     : " + (Test-Path (Join-Path $root 'certs\server.pfx'))
try { "DNS            : " + ((Resolve-DnsName $hostName -Type A -ErrorAction Stop | Where-Object Type -eq 'A').IPAddress -join ', ') } catch { 'DNS            : error' }
try {
    $r = Invoke-WebRequest "https://$hostName/healthz" -UseBasicParsing -TimeoutSec 5
    "server (https)  : $($r.StatusCode) $($r.Content)"
} catch { "server (https)  : not reachable ($($_.Exception.Message))" }
$log = Join-Path $root 'logs\requests.log'
if (Test-Path $log) { "`nlast requests:"; Get-Content $log -Tail 8 }
