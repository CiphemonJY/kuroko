using System;
using System.Runtime.InteropServices;

namespace ShadowCast;

/// <summary>
/// Per-application volume for the ffplay helper, via the Windows audio session
/// API - the same level the volume mixer shows for that process.
///
/// This is the only way to give the viewer its own volume: ffplay is a separate
/// process launched with -nodisp and no console, so its own volume keys are
/// unreachable, and restarting it with a different -volume would break the
/// audio for half a second on every slider movement.
///
/// Interfaces are declared with every method in vtable order (unused ones as
/// placeholders) because COM dispatches by slot, not by name - omitting one
/// would silently call the wrong function.
/// </summary>
internal static class AudioSession
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        // remaining methods unused
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
                     [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        // remaining methods unused
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr ctl);   // IAudioSessionManager
        int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr vol);      // IAudioSessionManager
        int GetSessionEnumerator(out IAudioSessionEnumerator e);
        // remaining methods unused
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        int GetCount(out int count);
        int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        // --- IAudioSessionControl ---
        int GetState(out int state);
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string v, IntPtr ctx);
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string v, IntPtr ctx);
        int GetGroupingParam(out Guid g);
        int SetGroupingParam(ref Guid g, IntPtr ctx);
        int RegisterAudioSessionNotification(IntPtr n);
        int UnregisterAudioSessionNotification(IntPtr n);
        // --- IAudioSessionControl2 ---
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetProcessId(out uint pid);
        int IsSystemSoundsSession();
        int SetDuckingPreference(bool optOut);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        int SetMasterVolume(float level, ref Guid eventContext);
        int GetMasterVolume(out float level);
        int SetMute(bool mute, ref Guid eventContext);
        int GetMute(out bool mute);
    }

    private static readonly Guid IID_IAudioSessionManager2 =
        new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

    /// <summary>Find the session belonging to a process id.</summary>
    private static ISimpleAudioVolume? FindSession(uint pid)
    {
        var enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumerator();
        // 0 = eRender, 0 = eConsole
        if (enumerator.GetDefaultAudioEndpoint(0, 0, out var device) != 0 || device is null) return null;

        var iid = IID_IAudioSessionManager2;
        if (device.Activate(ref iid, 1 /*CLSCTX_INPROC_SERVER*/, IntPtr.Zero, out var raw) != 0) return null;
        if (raw is not IAudioSessionManager2 mgr) return null;

        if (mgr.GetSessionEnumerator(out var sessions) != 0) return null;
        if (sessions.GetCount(out var n) != 0) return null;

        for (int i = 0; i < n; i++)
        {
            if (sessions.GetSession(i, out var ctl) != 0 || ctl is null) continue;
            if (ctl.GetProcessId(out var spid) == 0 && spid == pid)
                return ctl as ISimpleAudioVolume;   // same object exposes both interfaces
        }
        return null;
    }

    /// <summary>Set volume 0..1 for a process. Returns false if it has no session yet.</summary>
    internal static bool SetVolume(int pid, float level)
    {
        try
        {
            var vol = FindSession((uint)pid);
            if (vol is null) return false;
            var ctx = Guid.Empty;
            level = Math.Clamp(level, 0f, 1f);
            return vol.SetMasterVolume(level, ref ctx) == 0;
        }
        catch { return false; }
    }

    internal static float? GetVolume(int pid)
    {
        try
        {
            var vol = FindSession((uint)pid);
            if (vol is null) return null;
            return vol.GetMasterVolume(out var v) == 0 ? v : null;
        }
        catch { return null; }
    }
}
