# Watch for USB device arrival/removal in real time.
# Run it, then plug the dongle in. If NOTHING appears, the device is not
# enumerating at all - which points at cable / port / power / hardware,
# not at drivers or software.

param([int]$Seconds = 45)

function Snap {
    Get-PnpDevice -PresentOnly -EA SilentlyContinue |
        Select-Object -ExpandProperty InstanceId
}

function Describe($id) {
    $d = Get-PnpDevice -InstanceId $id -EA SilentlyContinue
    if ($d) { "[{0}] {1} :: {2}" -f $d.Status, $d.Class, $d.FriendlyName } else { $id }
}

Write-Host "Baseline..." -ForegroundColor DarkGray
$before = Snap
Write-Host "  $($before.Count) devices present`n" -ForegroundColor DarkGray

Write-Host "=== PLUG THE DONGLE IN NOW ===" -ForegroundColor Yellow
Write-Host "Watching for $Seconds seconds. Try: reseat it, a different port," -ForegroundColor Yellow
Write-Host "and a different cable. Ctrl+C to stop early.`n" -ForegroundColor Yellow

$deadline = (Get-Date).AddSeconds($Seconds)
$seen = @{}

while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 700
    $now = Snap

    foreach ($id in ($now | Where-Object { $_ -notin $before })) {
        if (-not $seen.ContainsKey($id)) {
            $seen[$id] = $true
            Write-Host ("  + ARRIVED  {0}" -f (Describe $id)) -ForegroundColor Green
        }
    }
    foreach ($id in ($before | Where-Object { $_ -notin $now })) {
        if (-not $seen.ContainsKey("gone:$id")) {
            $seen["gone:$id"] = $true
            Write-Host ("  - REMOVED  {0}" -f $id) -ForegroundColor DarkYellow
        }
    }
    $before = $now
}

Write-Host "`n=== done ===" -ForegroundColor Cyan
if ($seen.Keys.Count -eq 0) {
    Write-Host "NOTHING arrived or departed." -ForegroundColor Red
    Write-Host "The device is not enumerating. In order of likelihood:" -ForegroundColor Red
    Write-Host "  1. Charge-only USB-C cable (no data lines) - by far the most common"
    Write-Host "  2. Port not supplying enough power, or a hub in the way"
    Write-Host "  3. Dongle needs a LIVE HDMI source before it powers up"
    Write-Host "  4. Failed hardware"
} else {
    Write-Host "Device activity detected - see above." -ForegroundColor Green
}
Read-Host "`nEnter to close"
