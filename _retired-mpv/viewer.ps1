# ShadowCast viewer - raw yuyv422 1080p60 via mpv (GPU render, WASAPI audio).
#
#   viewer.ps1              launch (lowest latency)
#   viewer.ps1 -Record      record the session to H.264 while watching
#   viewer.ps1 -Raw         uncompressed pin: ~10% more fine detail, but the
#                           USB link cannot sustain it (stalls up to 228ms)
#   viewer.ps1 -Safe        more buffering if the picture stutters
#   viewer.ps1 -Fps 30      lower framerate (more headroom)
#   viewer.ps1 -Size 1280x720
#   viewer.ps1 -ListOnly    show connected capture devices and exit
#   viewer.ps1 -Pick        choose video/audio from the full device list
#   viewer.ps1 -Ffplay      fall back to the old ffplay path
#
# In the window: h = key help, f = fullscreen, s = screenshot, r = quick clip,
# c = scanlines, p = pixel-perfect, i = stats/fps, q = quit.
#
# The ShadowCast only exposes a video pin when a live HDMI source is present.
# If it isn't found this REFUSES to fall back to a webcam - it says the capture
# device is missing instead of silently showing the wrong camera.

param(
    [int]$Fps = 60,
    [string]$Size = '1920x1080',
    [switch]$ListOnly,
    [switch]$Pick,
    [switch]$Record,
    [switch]$Raw,
    [switch]$Safe,
    [switch]$Ffplay
)

$ErrorActionPreference = 'Stop'

$wingetBin = "$env:LOCALAPPDATA\Microsoft\WinGet\Packages\Gyan.FFmpeg_Microsoft.Winget.Source_8wekyb3d8bbwe\ffmpeg-9.0-full_build\bin"
function Find-Exe($name, [string[]]$fallbacks) {
    $p = (Get-Command $name -EA SilentlyContinue).Source
    if ($p) { return $p }
    foreach ($f in $fallbacks) { if (Test-Path $f) { return $f } }
    return $null
}
# NOTE: PowerShell variables are case-insensitive - a local named $ffplay would
# collide with the [switch]$Ffplay parameter. Hence the *Exe suffixes.
$ffmpegExe = Find-Exe 'ffmpeg' @("$wingetBin\ffmpeg.exe")
$ffplayExe = Find-Exe 'ffplay' @("$wingetBin\ffplay.exe")
$mpvExe    = Find-Exe 'mpv'    @("$env:ProgramFiles\MPV Player\mpv.exe", "$env:LOCALAPPDATA\Programs\mpv\mpv.exe")
if (-not $ffmpegExe) { throw "ffmpeg not found (needed to enumerate devices)." }
if (-not $mpvExe -and -not $ffplayExe) { throw "Neither mpv nor ffplay found." }
if (-not $mpvExe) { $Ffplay = $true }

# A capture device - never a webcam / virtual cam.
$CaptureRe = 'shadowcast|genki|capture|hdmi|mirabox|elgato|cam link'
$ExcludeRe = 'webcam|virtual|obs| ir '

function Get-DShowDevices {
    $tmp = Join-Path $env:TEMP ("dshow_" + [guid]::NewGuid().ToString('N') + ".txt")
    Start-Process $ffmpegExe -ArgumentList '-hide_banner','-list_devices','true','-f','dshow','-i','dummy' `
        -Wait -NoNewWindow -RedirectStandardError $tmp | Out-Null
    $lines = Get-Content $tmp -EA SilentlyContinue
    Remove-Item $tmp -EA SilentlyContinue
    $video = @(); $audio = @()
    foreach ($l in $lines) {
        if ($l -match '"([^"]+)"\s*\((video|audio|none)\)') {
            if ($Matches[2] -eq 'audio') { $audio += $Matches[1] } else { $video += $Matches[1] }
        }
    }
    return @{ Video = $video; Audio = $audio }
}

# The dshow device is exclusive AND takes a moment to release. Launching while
# a previous viewer is still shutting down fails with "Could not run graph
# (device already in use)", so wait for our own players to exit first.
$stale = @(Get-Process mpv, ffplay, ffmpeg -EA SilentlyContinue)
if ($stale.Count) {
    Write-Host "Waiting for a previous viewer to release the device..." -ForegroundColor DarkGray
    foreach ($i in 1..10) {
        Start-Sleep -Milliseconds 700
        if (-not (Get-Process mpv, ffplay, ffmpeg -EA SilentlyContinue)) { break }
    }
}

$dev = Get-DShowDevices

if ($ListOnly) {
    Write-Host "`nVideo devices:" -ForegroundColor Cyan; $dev.Video | ForEach-Object { Write-Host "  - $_" }
    Write-Host "`nAudio devices:" -ForegroundColor Cyan; $dev.Audio | ForEach-Object { Write-Host "  - $_" }
    exit 0
}

function Select-From($list, $label) {
    Write-Host "`nSelect $label device:" -ForegroundColor Cyan
    for ($i = 0; $i -lt $list.Count; $i++) { Write-Host ("  [{0}] {1}" -f $i, $list[$i]) }
    $s = Read-Host "Number (Enter = skip)"
    if ([string]::IsNullOrWhiteSpace($s)) { return $null }
    return $list[[int]$s]
}

$vid = $dev.Video | Where-Object { $_ -match $CaptureRe -and $_ -notmatch $ExcludeRe } | Select-Object -First 1
if (-not $vid -and $Pick -and $dev.Video.Count) { $vid = Select-From $dev.Video 'video' }
if (-not $vid) {
    Write-Host "`nNo capture device found." -ForegroundColor Red
    Write-Host "The ShadowCast only appears when the console/PC is powered on and" -ForegroundColor Yellow
    Write-Host "sending live HDMI. Turn the source on, then rerun." -ForegroundColor Yellow
    Write-Host "(Seen: $($dev.Video -join ', '))" -ForegroundColor DarkGray
    Read-Host "`nEnter to close"; exit 1
}

$aud = $dev.Audio | Where-Object { $_ -match 'shadowcast|genki|digital audio|hdmi|capture' } | Select-Object -First 1
if (-not $aud -and $Pick -and $dev.Audio.Count) { $aud = Select-From $dev.Audio 'audio' }

Write-Host "`nVideo: $vid  ($Size @ ${Fps}fps raw yuyv422)" -ForegroundColor Green
Write-Host "Audio: $(if ($aud) { $aud } else { '(none)' })" -ForegroundColor Green

# Why these settings:
#  * raw yuyv422, not MJPEG. MJPEG is unreachable here - the dshow demuxer has
#    NO codec/pin option (only video_size/pixel_format/framerate/audio_buffer_
#    size). ffmpeg.exe's `-vcodec mjpeg` is a CLI-only special case; passing it
#    to ffplay forces a JPEG decoder onto raw frames ("No JPEG data found in
#    image" -> no video window), and mpv rejects it ("Could not set AVOption").
#    Don't reintroduce it.
#  * rtbufsize 256M. Raw 1080p60 is ~249MB/s, so 64M is only a ~0.25s cushion
#    and USB jitter overflows it ("real-time buffer too full! frame dropped").
#  * mpv over ffplay. ffplay blits on the CPU via SDL and drops frames under
#    load; mpv renders on the GPU (gpu-next/libplacebo) with WASAPI event-mode
#    audio, which is the headroom that stops the drops + audio tearing.
#  * NO -vf setpts / --untimed: they race video ahead of audio.

if ($Ffplay) {
    $in = if ($aud) { "video=$vid`:audio=$aud" } else { "video=$vid" }
    $a = @('-hide_banner','-loglevel','warning','-f','dshow','-rtbufsize','256M',
           '-pixel_format','yuyv422','-video_size',$Size,'-framerate',"$Fps")
    if ($aud) { $a += @('-audio_buffer_size','80') }
    $a += @('-i',$in,'-fflags','nobuffer','-flags','low_delay','-framedrop',
            '-analyzeduration','0','-probesize','500000','-window_title','ShadowCast')
    Write-Host "`n[ffplay] f = fullscreen, q = quit, m = mute.`n" -ForegroundColor DarkGray
    & $ffplayExe @a
    exit
}

# mpv needs the whole dshow URL as ONE argument - it contains spaces, so build
# the command line as a pre-quoted string (an array gets split on spaces).
$url = "av://dshow:video=$vid" + $(if ($aud) { ":audio=$aud" } else { "" })

# fflags=+nobuffer must be repeated here: mpv's low-latency profile adds it via
# demuxer-lavf-o-add, but this -o= assignment replaces the whole map.
# rtbufsize IS THE LATENCY KNOB, not just overflow protection. Frames that the
# renderer hasn't taken yet sit in this queue, so its size is a hard ceiling on
# how far behind the picture can fall: at raw 1080p60 (~249MB/s), 256MB = 1.03s
# of queued video, and it really did sit 63-84% full - that was the ~0.5s lag.
# 8MB caps the queue at ~0.03s (2 frames). A big buffer does not prevent
# stutter, it just converts dropped frames into permanent lag; for gameplay,
# dropping a frame is always better. Tested at 1080p60: 256MB/24MB/8MB all zero
# drops, so take the smallest. Use -Safe if a heavy scene makes it stutter.
$rtbuf = if ($Safe) { 256000000 } else { 8000000 }
$opts = "pixel_format=yuyv422,video_size=$Size,framerate=$Fps,rtbufsize=$rtbuf,fflags=+nobuffer"
# 80ms of dshow audio buffering. Do NOT shrink this to chase lag: 30ms caused
# audible tearing (underruns). With untimed in mpv.conf this no longer feeds
# into video latency, so a comfortable audio buffer is free.
if ($aud) { $opts += ",audio_buffer_size=80" }

$cfgDir = Join-Path $PSScriptRoot 'mpv'
$capDir = Join-Path $env:USERPROFILE 'Videos\ShadowCast'
if (-not (Test-Path $capDir)) { New-Item -ItemType Directory -Path $capDir -Force | Out-Null }

# --- MJPEG display mode (DEFAULT) ----------------------------------------
# Raw yuyv422 1080p60 is ~249MB/s (~2Gbps) - about 60% of practical USB3
# bandwidth - and the dongle cannot keep that up. Measured over 15s at 1080p60:
#
#            frames   mean    jitter   worst stall
#   raw        887    16.9ms  12.1ms   228.6ms   <- 14-frame freeze, 1.6% lost
#   mjpeg      901    16.7ms  10.3ms   107.7ms   <- perfect 60fps
#
# Those stalls are upstream of the player, so NO mpv counter sees them (its
# demuxer queue measured 0.00s while the picture still trailed). MJPEG is
# ~25MB/s - 10x less - and is what Genki Arcade used (its bundle asks Chromium
# for 1920x1080@60, which selects MJPEG on Windows). Costs ~10% fine detail;
# chroma is 4:2:2 either way, so colour resolution is unchanged.
# mpv cannot select the MJPEG pin (libavformat exposes no codec selector), so
# ffmpeg owns the device and stream-copies into mpv over a pipe.
if (-not $Raw) {
    $inSpec = "video=$vid" + $(if ($aud) { ":audio=$aud" } else { "" })
    $maps   = if ($aud) { '-map 0:v -map 0:a' } else { '-map 0:v' }
    $line = '"' + $ffmpegExe + '" -hide_banner -loglevel warning -f dshow -vcodec mjpeg' +
            " -video_size $Size -framerate $Fps -rtbufsize 8M" +
            $(if ($aud) { ' -audio_buffer_size 80' } else { '' }) +
            ' -fflags +nobuffer -flags low_delay' +
            ' -i "' + $inSpec + '" ' +
            # flush_packets forces the muxer to write each packet immediately
            # instead of batching; muxdelay/max_delay 0 stop it holding packets
            # to interleave A/V. Without these the pipe adds its own buffer on
            # top of the capture, which is the whole thing we are avoiding.
            "$maps -c copy -f matroska -flush_packets 1 -muxdelay 0 -max_delay 0 - | " +
            '"' + $mpvExe + '" - --cache=no --demuxer-lavf-o=fflags=+nobuffer' +
            ' --profile=low-latency --audio-buffer=0.05 --swapchain-depth=1' +
            # video-sync=desync: the low-latency profile paces video off the
            # AUDIO clock, so the ~130ms audio pipeline (80ms dshow + 50ms mpv)
            # becomes video latency too. desync paces off the system clock
            # instead. This failed on the raw path (audio died), but on this
            # pipe it is verified good: audio-pts advanced 5.03s in 5s (playing,
            # not just initialised) and avsync held -23.1ms across 40s with
            # 0.1ms drift - no A/V separation, because both streams come off the
            # same device clock. -Safe reverts to audio-paced video.
            $(if ($Safe) { '' } else { ' --video-sync=desync' }) +
            ' "--config-dir=' + $cfgDir + '" "--screenshot-directory=' + $capDir + '"'

    # A binary pipe must go through cmd - PowerShell's pipeline corrupts it.
    $cmdFile = Join-Path $env:TEMP ("scmj_" + [guid]::NewGuid().ToString('N') + ".cmd")
    Set-Content -Path $cmdFile -Value "@echo off`r`n$line" -Encoding ASCII
    Write-Host "`nMJPEG mode: ~25MB/s instead of ~249MB/s over USB." -ForegroundColor Cyan
    Start-Process cmd -ArgumentList "/c `"$cmdFile`"" -Wait -WindowStyle Minimized
    Remove-Item $cmdFile -EA SilentlyContinue
    exit
}

# --- record mode ---------------------------------------------------------
# The dshow device is EXCLUSIVE - a second process cannot open it - so a
# separate recorder alongside the viewer is impossible. Instead ffmpeg owns the
# device, encodes to disk with NVENC (~70MB/min vs ~15GB/min raw), and tees a
# stream-copy to mpv for live display. This uses the MJPEG pin, which only
# ffmpeg.exe can select. Costs a little latency, so it is not the default.
if ($Record) {
    if (-not $aud) { Write-Host "No audio device - recording video only." -ForegroundColor Yellow }
    $outFile = Join-Path $capDir ("ShadowCast-" + (Get-Date -Format 'yyyyMMdd-HHmmss') + ".mp4")
    $inSpec  = "video=$vid" + $(if ($aud) { ":audio=$aud" } else { "" })
    $maps    = if ($aud) { '-map 0:v -map 0:a' } else { '-map 0:v' }
    $acodec  = if ($aud) { '-c:a aac -b:a 192k' } else { '' }

    # NVENC cannot take the yuvj422p that MJPEG decodes to - format=yuv420p is
    # required or it fails with "YUV422P not supported / No capable devices".
    $line = '"' + $ffmpegExe + '" -hide_banner -loglevel warning -f dshow -vcodec mjpeg' +
            " -video_size $Size -framerate $Fps -rtbufsize 256M" +
            $(if ($aud) { ' -audio_buffer_size 80' } else { '' }) +
            ' -i "' + $inSpec + '" ' +
            "$maps -vf format=yuv420p -c:v h264_nvenc -preset p1 -tune ull -b:v 25M $acodec " +
            # Fragmented MP4: a plain mp4 writes its moov atom only on clean
            # exit, so an unplug/crash mid-recording leaves an unplayable file
            # ("moov atom not found"). Fragments keep it playable at any point.
            '-movflags +frag_keyframe+empty_moov+default_base_moof ' +
            '-f mp4 -y "' + $outFile + '" ' +
            "$maps -c copy -f matroska - | " +
            '"' + $mpvExe + '" - --profile=low-latency --cache=no --demuxer-lavf-o=fflags=+nobuffer' +
            ' "--config-dir=' + $cfgDir + '" --title=ShadowCast-REC'

    # A binary pipe must go through cmd - PowerShell's pipeline corrupts it.
    $cmdFile = Join-Path $env:TEMP ("screc_" + [guid]::NewGuid().ToString('N') + ".cmd")
    Set-Content -Path $cmdFile -Value "@echo off`r`n$line" -Encoding ASCII

    Write-Host "`nRECORDING -> $outFile" -ForegroundColor Magenta
    Write-Host "Close the mpv window to stop.`n" -ForegroundColor DarkGray
    Start-Process cmd -ArgumentList "/c `"$cmdFile`"" -Wait -WindowStyle Minimized
    Remove-Item $cmdFile -EA SilentlyContinue
    if (Test-Path $outFile) {
        Write-Host "Saved: $outFile ($([math]::Round((Get-Item $outFile).Length/1MB,1)) MB)" -ForegroundColor Green
    }
    exit
}

$argstr = '"' + $url + '"' +
          ' --demuxer-lavf-o=' + $opts +
          ' "--config-dir=' + $cfgDir + '"' +
          ' "--screenshot-directory=' + $capDir + '"' +
          # The GPU queues frames before presenting; the default depth of 3 can
          # hold ~2 extra frames (~33ms). Retested with the small capture queue
          # above: zero drops, audio fine. -Safe restores the default.
          $(if ($Safe) { '' } else { ' --swapchain-depth=1' })

# -Safe trades latency back for headroom (the big capture queue is set above).
if ($Safe) {
    $argstr += ' --audio-buffer=0.1'
    Write-Host "Safe mode: ~1s capture buffer - smoother under load, more lag." -ForegroundColor Yellow
}

Write-Host "Saving to: $capDir" -ForegroundColor DarkGray
Write-Host "`n[mpv]  h = key help   f = fullscreen   s = screenshot   r = record   q = quit`n" -ForegroundColor DarkGray

# Auto-reconnect: mpv exits 0 when you quit deliberately (q). Any other code
# means the stream died - usually the dongle was unplugged or the source turned
# off - so wait for the device to come back and resume.
while ($true) {
    $proc = Start-Process $mpvExe -ArgumentList $argstr -PassThru -Wait
    if ($proc.ExitCode -eq 0) { break }

    Write-Host "`nSignal lost (exit $($proc.ExitCode)). Waiting for the device... Ctrl+C to stop." -ForegroundColor Yellow
    $back = $false
    foreach ($i in 1..60) {
        Start-Sleep -Seconds 2
        if ((Get-DShowDevices).Video -contains $vid) { $back = $true; break }
    }
    if (-not $back) { Write-Host "Device did not return. Exiting." -ForegroundColor Red; break }
    Write-Host "Device back - reconnecting." -ForegroundColor Green
}
