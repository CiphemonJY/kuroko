# Measure ShadowCast capture->screen latency objectively.
#
#   measure-latency.ps1
#
# REQUIRES MOTION ON THE CONSOLE SCREEN. It works by correlating the brightness
# of the frames arriving from the dongle against the brightness of what is
# actually on your monitor, so a paused game or a menu gives it nothing to lock
# onto. Play something (or just move the camera) while it runs.
#
# It runs WINDOWED + ontop, and forces --d3d11-exclusive-fs=no --d3d11-flip=no.
# That combination is required for the screen side to be capturable at all:
# gdigrab reads a screen REGION via GDI, and both exclusive fullscreen and the
# flip-model swapchain bypass the compositor, so a capture of them comes back
# BLACK (screen brightness variance 0, nonsense correlation). ontop keeps other
# windows from being measured instead of the video - don't cover it.
#
# Consequence: the number printed includes one compositor frame (~6-16ms) that
# normal fullscreen playback does NOT pay. Treat it as a slight over-estimate.
#
# What it measures: dongle-delivers-frame -> pixel-lit-on-screen. That is the
# whole software path (decode, render, present, compositor). It does NOT
# include the console's own render time or the dongle's internal capture delay,
# because nothing on this PC can observe those.
#
# How it works: two ffmpeg processes both stamp frames with ABSOLUTE wallclock
# time (-use_wallclock_as_timestamps + -copyts), so their timestamps are
# directly comparable across processes. One logs the captured stream, the other
# screen-grabs the viewer window. Cross-correlating the two brightness series
# gives the lag.
#
# Gotcha that cost an hour: with -copyts, `-t <secs>` is compared against the
# ABSOLUTE timestamp, so it fires instantly and you get 0 frames. Use
# -frames:v instead. Likewise metadata=print's file writes are buffered and
# drop frames - showinfo on stderr is reliable.

param([int]$Seconds = 10)

$ErrorActionPreference = 'Stop'
$work = Join-Path $env:TEMP 'sc_latmeas'
New-Item -ItemType Directory -Path $work -Force | Out-Null
$devLog = Join-Path $work 'dev.txt'
$scrLog = Join-Path $work 'scr.txt'
Remove-Item -LiteralPath $devLog -Force -EA SilentlyContinue
Remove-Item -LiteralPath $scrLog -Force -EA SilentlyContinue

$ff = (Get-Command ffmpeg -EA SilentlyContinue).Source
if (-not $ff) { $ff = "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-9.0-full_build\bin\ffmpeg.exe" }
$mpv = (Get-Command mpv -EA SilentlyContinue).Source
if (-not $mpv) { $mpv = "$env:ProgramFiles\MPV Player\mpv.exe" }
$cfg = Join-Path $PSScriptRoot 'mpv'

Get-Process mpv, ffmpeg -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue
Start-Sleep -Seconds 6

$frames = $Seconds * 60
$rig = Join-Path $work 'rig.cmd'
@"
@echo off
cd /d "%~dp0"
"$ff" -hide_banner -loglevel info -use_wallclock_as_timestamps 1 -copyts -f dshow -vcodec mjpeg -video_size 1920x1080 -framerate 60 -rtbufsize 8M -fflags +nobuffer -i "video=ShadowCast 2" -frames:v $($frames + 2400) -map 0:v -c copy -f matroska -flush_packets 1 -muxdelay 0 pipe:1 -frames:v $($frames + 2400) -map 0:v -vf "crop=iw/2:ih/2,scale=32:18,showinfo" -an -f null NUL 2>dev.txt | "$mpv" - --cache=no --profile=low-latency --swapchain-depth=1 --video-sync=desync --config-dir="$cfg" --title=ShadowCast --no-audio --ontop=yes --d3d11-exclusive-fs=no --d3d11-flip=no --autofit=1280x720
"@ | Set-Content -Path $rig -Encoding ASCII

Write-Host "Starting viewer..." -ForegroundColor Cyan
Start-Process cmd -ArgumentList "/c `"$rig`"" -WorkingDirectory $work -WindowStyle Minimized | Out-Null

$title = $null
foreach ($i in 1..30) {
    Start-Sleep -Milliseconds 700
    $m = Get-Process mpv -EA SilentlyContinue | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
    if ($m -and $m.MainWindowTitle) { $title = $m.MainWindowTitle; break }
}
if (-not $title) { Write-Host "Viewer window never appeared." -ForegroundColor Red; exit 1 }

Write-Host "Capturing $Seconds s - MAKE SURE SOMETHING IS MOVING ON SCREEN." -ForegroundColor Yellow
Start-Process $ff -ArgumentList ('-hide_banner -loglevel info -use_wallclock_as_timestamps 1 -copyts -f gdigrab -framerate 60 -i "title=' + $title + '" -frames:v ' + $frames + ' -vf "crop=iw/2:ih/2,scale=32:18,showinfo" -f null NUL') `
    -WorkingDirectory $work -Wait -NoNewWindow -RedirectStandardError $scrLog
Start-Sleep -Seconds 3
Get-Process mpv, ffmpeg -EA SilentlyContinue | Stop-Process -Force -EA SilentlyContinue

function Read-Series($f) {
    $out = @()
    foreach ($l in Get-Content $f -EA SilentlyContinue) {
        if ($l -match ' pts_time:([0-9.]+).*mean:\[\s*([0-9.]+)') { $out += , @([double]$Matches[1], [double]$Matches[2]) }
    }
    return $out
}
$dev = Read-Series $devLog
$scr = Read-Series $scrLog
Write-Host "`nframes: device=$($dev.Count) screen=$($scr.Count)"
if ($dev.Count -lt 100 -or $scr.Count -lt 100) { Write-Host "Not enough frames captured." -ForegroundColor Red; exit 1 }

function SD($v) { $m = ($v | Measure-Object -Average).Average; [math]::Sqrt((($v | ForEach-Object { [math]::Pow($_ - $m, 2) }) | Measure-Object -Average).Average) }
$dsd = SD ($dev | ForEach-Object { $_[1] })
$ssd = SD ($scr | ForEach-Object { $_[1] })
Write-Host "brightness variation: source=$([math]::Round($dsd,2)) screen=$([math]::Round($ssd,2))"
if ($dsd -lt 0.3) {
    Write-Host "`nSOURCE IS STATIC - nothing to correlate." -ForegroundColor Red
    Write-Host "Play something with motion and run this again." -ForegroundColor Yellow
    exit 1
}

function Interp($s, $t) {
    for ($i = 1; $i -lt $s.Count; $i++) {
        if ($s[$i][0] -ge $t) {
            $a = $s[$i - 1]; $b = $s[$i]
            if ($b[0] -eq $a[0]) { return $a[1] }
            return $a[1] + ($b[1] - $a[1]) * (($t - $a[0]) / ($b[0] - $a[0]))
        }
    }
    return $s[-1][1]
}
$t0 = [math]::Max($dev[0][0], $scr[0][0]) + 0.5
$t1 = [math]::Min($dev[-1][0], $scr[-1][0]) - 1.4   # room for the largest lag tested
Write-Host ("overlap window: {0:N1}s (device {1:N1}s, screen {2:N1}s)" -f ($t1 - $t0), ($dev[-1][0] - $dev[0][0]), ($scr[-1][0] - $scr[0][0]))
if (($t1 - $t0) -lt 3) {
    Write-Host "Not enough overlap between the two captures to correlate." -ForegroundColor Red
    exit 1
}
$grid = @(); for ($t = $t0; $t -lt $t1; $t += 0.008) { $grid += $t }

# Both sides are centre-cropped to the same relative region so window chrome
# and desktop edges cannot dominate the screen-side signal.
# Search out to 1200ms: an answer sitting exactly on the ceiling means the real
# lag is at or past it, not that the lag equals the ceiling.
$best = -2; $bestLag = 0; $curve = @()
foreach ($lagMs in 0..120) {
    $lag = $lagMs * 0.01
    $x = @(); $y = @()
    foreach ($t in $grid) { if (($t + $lag) -lt $t1) { $x += (Interp $dev $t); $y += (Interp $scr ($t + $lag)) } }
    if ($x.Count -lt 50) { continue }
    $mx = ($x | Measure-Object -Average).Average; $my = ($y | Measure-Object -Average).Average
    $num = 0.0; $dx = 0.0; $dy = 0.0
    for ($i = 0; $i -lt $x.Count; $i++) { $a = $x[$i] - $mx; $b = $y[$i] - $my; $num += $a * $b; $dx += $a * $a; $dy += $b * $b }
    if ($dx -gt 0 -and $dy -gt 0) {
        $r = $num / [math]::Sqrt($dx * $dy)
        $curve += [pscustomobject]@{ Lag = $lag * 1000; R = $r }
        if ($r -gt $best) { $best = $r; $bestLag = $lag * 1000 }
    }
}
Write-Host "`ntop correlation peaks:" -ForegroundColor DarkGray
$curve | Sort-Object R -Descending | Select-Object -First 5 | ForEach-Object { "   {0,6:N0}ms  r={1:N3}" -f $_.Lag, $_.R }

Write-Host "`n=== capture -> screen latency ===" -ForegroundColor Green
Write-Host ("  {0} ms   (correlation r={1:N3})" -f [math]::Round($bestLag), $best)
if ($best -lt 0.5) { Write-Host "  Low correlation - treat as unreliable; try again with more motion." -ForegroundColor Yellow }
Write-Host "  (excludes console render time and the dongle's internal delay)" -ForegroundColor DarkGray
