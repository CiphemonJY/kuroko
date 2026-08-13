# Launch the MediaFoundation-path viewer (mf-viewer.html).
#
# Why this exists: the normal viewer captures through ffmpeg's DirectShow
# input. DirectShow reaches a modern UVC device through a legacy compatibility
# layer that can add its own buffering, and that buffering happens BEFORE any
# timestamp we can read - measure-latency.ps1 cannot see it. Browsers capture
# through MediaFoundation instead, which is the route the old Genki Arcade
# (Electron/Chromium) used. If Genki felt snappier, this is the likely reason.
#
#   mf-viewer.ps1          serve + open in the browser
#   mf-viewer.ps1 -Port N  use a specific port
#
# getUserMedia needs a secure context, so file:// will NOT get camera access -
# it has to be served over http://localhost, which browsers treat as secure.
# Close the normal viewer first: the capture device is exclusive.

param(
    [int]$Port = 8777,
    [switch]$BrowserAudio,      # let Chrome play the audio instead of ffplay
    [int]$AudioDelayMs = 50     # ffplay's audio path is shorter than the
                                # browser's video path, so audio arrives early;
                                # this pushes it back to match. Raise if audio
                                # still leads, lower if it now lags.
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

if (-not (Test-Path (Join-Path $root 'mf-viewer.html'))) { throw "mf-viewer.html not found next to this script." }

# Free the capture device.
Get-Process mpv, ffmpeg, ffplay -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue

$py = (Get-Command python -EA SilentlyContinue).Source
if (-not $py) { $py = (Get-Command py -EA SilentlyContinue).Source }
if (-not $py) { throw "python not found - needed to serve over localhost." }

# Actually fetch the page rather than trusting a listening socket - a stale or
# dead server can still hold the port, and then the app opens to nothing.
function Test-Server {
    try { (Invoke-WebRequest -Uri "http://localhost:$Port/mf-viewer.html" -UseBasicParsing -TimeoutSec 3).StatusCode -eq 200 }
    catch { $false }
}

if (Test-Server) {
    Write-Host "Reusing server on $Port" -ForegroundColor DarkGray
} else {
    Write-Host "Serving $root on http://localhost:$Port" -ForegroundColor Cyan
    Start-Process $py -ArgumentList "-m http.server $Port --bind 127.0.0.1" -WorkingDirectory $root -WindowStyle Minimized | Out-Null
    $ok = $false
    foreach ($i in 1..15) { Start-Sleep -Milliseconds 500; if (Test-Server) { $ok = $true; break } }
    if (-not $ok) { throw "Server did not come up on port $Port." }
}

# --- audio -----------------------------------------------------------------
# The dongle's audio pin is 44.1kHz-only and every output device on this box is
# locked at 48kHz, so something must resample. Chrome corrects the resulting
# clock drift in periodic jumps, heard as a pop every few seconds, and it is not
# reachable from page code - MediaStreamSource hands JS audio that is already
# converted. ffplay's aresample=async instead stretches the audio by fractions
# of a percent continuously, so drift is absorbed rather than snapped away.
# The video and audio pins are SEPARATE dshow devices, so ffplay can hold audio
# while the browser holds video (verified).
$url = "http://localhost:$Port/mf-viewer.html"
if (-not $BrowserAudio) {
    $ffplay = (Get-Command ffplay -EA SilentlyContinue).Source
    if (-not $ffplay) { $ffplay = "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-9.0-full_build\bin\ffplay.exe" }
    if (Test-Path $ffplay) {
        Get-Process ffplay -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
        # adelay first, then the adaptive resampler: delay the stream, then let
        # aresample absorb clock drift on what comes out.
        $af = if ($AudioDelayMs -gt 0) { "adelay=$($AudioDelayMs):all=1,aresample=async=1000" }
              else { "aresample=async=1000" }
        $aArgs = '-hide_banner -loglevel error -nodisp -autoexit ' +
                 '-fflags +nobuffer -flags low_delay ' +
                 '-f dshow -audio_buffer_size 50 -i "audio=Digital Audio Interface (2- ShadowCast 2)" ' +
                 "-af $af"
        Start-Process $ffplay -ArgumentList $aArgs -WindowStyle Hidden | Out-Null
        $url += '?audio=external'
        Write-Host "Audio: ffplay, adaptive resampling, +${AudioDelayMs}ms delay." -ForegroundColor Cyan
    } else {
        Write-Host "ffplay not found - falling back to browser audio." -ForegroundColor Yellow
    }
} else {
    Write-Host "Audio: browser (Chrome resampler)." -ForegroundColor DarkGray
}

# Prefer Edge/Chrome in app mode: no tabs or address bar, and --app keeps the
# window clean for a fair latency comparison against the mpv viewer.
# ${env:...} braces are required here: "$env:ProgramFiles(x86)" parses as
# $env:ProgramFiles followed by a literal "(x86)".
$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
if (-not (Test-Path $edge)) { $edge = "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe" }

if (Test-Path $edge) {
    Start-Process $edge -ArgumentList "--app=$url" | Out-Null
    Write-Host "Opened in Edge (app mode)." -ForegroundColor Green
} else {
    Start-Process $url | Out-Null
    Write-Host "Opened in the default browser." -ForegroundColor Green
}

Write-Host "`nClick 'Start capture', then allow camera/microphone access." -ForegroundColor Yellow
Write-Host "Press f in the page for fullscreen, Esc to leave it." -ForegroundColor DarkGray
