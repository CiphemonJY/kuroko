-- Quick-clip recording for the Kuroko viewer.
--
-- In view mode the capture is raw yuyv422 (~15 GB/min), so stream-record's
-- no-re-encode dump is only sane for short clips. On stop we hand the raw file
-- to ffmpeg for an NVENC transcode (~70 MB/min) and delete the original, so
-- what you keep is small. For long sessions use: viewer.ps1 -Record

local recording = false
local raw_path = nil

local function capture_dir()
    local dir = mp.get_property("screenshot-directory")
    if not dir or dir == "" then
        dir = (os.getenv("USERPROFILE") or ".") .. "\\Videos\\Kuroko"
    end
    return dir
end

-- Fire-and-forget: transcode raw -> mp4, then delete the raw file.
local function transcode(src)
    local dst = src:gsub("%.mkv$", ".mp4")
    local cmd = string.format(
        'ffmpeg -hide_banner -loglevel error -y -i "%s" -vf format=yuv420p ' ..
        '-c:v h264_nvenc -preset p4 -b:v 25M -c:a aac -b:a 192k "%s" && del "%s"',
        src, dst, src)
    mp.command_native({
        name = "subprocess", playback_only = false, detach = true,
        args = { "cmd", "/c", cmd },
    })
end

local function toggle_record()
    if recording then
        mp.set_property("stream-record", "")
        recording = false
        if raw_path then
            transcode(raw_path)
            mp.osd_message("REC stopped - compressing in background", 3)
            raw_path = nil
        else
            mp.osd_message("REC stopped", 2)
        end
        return
    end

    local dir = capture_dir()
    mp.command_native({
        name = "subprocess", playback_only = false,
        args = { "cmd", "/c", 'if not exist "' .. dir .. '" md "' .. dir .. '"' },
    })

    local path = dir .. "\\" .. os.date("Kuroko-%Y%m%d-%H%M%S.mkv")
    mp.set_property("stream-record", path)

    -- stream-record fails silently on an unwritable path; verify it stuck.
    if mp.get_property("stream-record") == path then
        recording, raw_path = true, path
        mp.osd_message("REC (raw ~15GB/min - short clips only)", 3)
    else
        mp.osd_message("REC failed: cannot write to " .. dir, 4)
    end
end

mp.add_key_binding(nil, "toggle-record", toggle_record)
