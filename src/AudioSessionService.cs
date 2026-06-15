using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Voolime;

internal enum VolumeHotkeyKind
{
    Down,
    Up,
    ToggleMute
}

internal sealed record VolumeChangeResult(
    string DisplayName,
    string Message,
    double Volume,
    bool Muted,
    bool Success)
{
    public static VolumeChangeResult Failed(string displayName, string message) =>
        new(displayName, message, 0, Muted: false, Success: false);
}

internal sealed class AudioSessionService
{
    private const float VolumeStep = 0.02f;

    public VolumeChangeResult Apply(ActiveAppTarget target, VolumeHotkeyKind kind)
    {
        var sessions = EnumerateSessions().ToList();
        var matches = MatchSessions(target, sessions).ToList();

        if (matches.Count == 0)
        {
            return new VolumeChangeResult(target.DisplayName, "La app activa no tiene audio abierto", 0, Muted: false, Success: false);
        }

        var baseVolume = matches.Max(static s => s.Volume);
        var muted = matches.All(static s => s.Muted);

        if (kind == VolumeHotkeyKind.ToggleMute)
        {
            muted = !muted;
            foreach (var session in matches)
            {
                session.VolumeControl.SetMute(muted, Guid.Empty);
            }

            return new VolumeChangeResult(target.DisplayName, muted ? "Silenciado" : "Sonido activado", baseVolume, muted, Success: true);
        }

        var delta = kind == VolumeHotkeyKind.Up ? VolumeStep : -VolumeStep;
        var newVolume = Math.Clamp(baseVolume + delta, 0f, 1f);
        newVolume = MathF.Round(newVolume * 100f) / 100f;

        foreach (var session in matches)
        {
            session.VolumeControl.SetMasterVolume(newVolume, Guid.Empty);
            if (newVolume > 0f)
            {
                session.VolumeControl.SetMute(false, Guid.Empty);
            }
        }

        return new VolumeChangeResult(
            target.DisplayName,
            $"{Math.Round(newVolume * 100)}%",
            newVolume,
            Muted: newVolume <= 0f,
            Success: true);
    }

    private static IEnumerable<AudioSession> MatchSessions(ActiveAppTarget target, IReadOnlyCollection<AudioSession> sessions)
    {
        var context = MatchContext.Create(target);

        foreach (var session in sessions)
        {
            if (IsMatch(session, context))
            {
                yield return session;
            }
        }
    }

    private static bool IsMatch(AudioSession session, MatchContext context)
    {
        if (context.ProcessIds.Contains(session.ProcessId))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(session.ProcessPath) && context.Paths.Contains(NormalizePath(session.ProcessPath)))
        {
            return true;
        }

        if (context.Names.Contains(session.ProcessName))
        {
            return true;
        }

        if (IsDisplayNameMatch(session.DisplayName, context.DisplayName))
        {
            return true;
        }

        return IsRelatedRendererProcess(session, context);
    }

    private static bool IsDisplayNameMatch(string? sessionDisplayName, string targetDisplayName)
    {
        var sessionName = NormalizeComparableText(sessionDisplayName);
        var targetName = NormalizeComparableText(targetDisplayName);
        if (sessionName.Length < 4 || targetName.Length < 4)
        {
            return false;
        }

        return sessionName == targetName ||
               sessionName.Contains(targetName, StringComparison.OrdinalIgnoreCase) ||
               targetName.Contains(sessionName, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRelatedRendererProcess(AudioSession session, MatchContext context)
    {
        if (string.IsNullOrWhiteSpace(session.ProcessPath))
        {
            return false;
        }

        var sessionDirectory = NormalizeDirectory(session.ProcessPath);
        if (string.IsNullOrWhiteSpace(sessionDirectory) || !context.Directories.Contains(sessionDirectory))
        {
            return false;
        }

        return context.Names.Any(targetName => AreProcessNamesRelated(targetName, session.ProcessName));
    }

    private static bool AreProcessNamesRelated(string targetName, string sessionName)
    {
        var target = NormalizeProcessName(targetName);
        var session = NormalizeProcessName(sessionName);
        if (target.Length < 5 || session.Length < 5)
        {
            return false;
        }

        return session.StartsWith(target, StringComparison.OrdinalIgnoreCase) ||
               target.StartsWith(session, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeProcessName(string name)
    {
        var normalized = name.Trim();
        foreach (var suffix in new[] { "Renderer", "Render", "Helper", "Child", "Gpu", "GPU", "Audio" })
        {
            if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && normalized.Length > suffix.Length + 4)
            {
                normalized = normalized[..^suffix.Length];
                break;
            }
        }

        return normalized;
    }

    private static HashSet<string> NormalizePaths(IEnumerable<string> paths) =>
        paths.Select(NormalizePath)
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> NormalizeDirectories(IEnumerable<string> paths) =>
        paths.Select(NormalizeDirectory)
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path).Trim();
        }
        catch
        {
            return path.Trim();
        }
    }

    private static string NormalizeDirectory(string? path)
    {
        var normalized = NormalizePath(path);
        return string.IsNullOrWhiteSpace(normalized) ? string.Empty : Path.GetDirectoryName(normalized) ?? string.Empty;
    }

    private static string NormalizeComparableText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();

    private static IEnumerable<AudioSession> EnumerateSessions()
    {
        var enumerator = (CoreAudio.IMMDeviceEnumerator)(object)new CoreAudio.MMDeviceEnumerator();
        Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(CoreAudio.EDataFlow.eRender, CoreAudio.ERole.eMultimedia, out var device));

        var managerId = typeof(CoreAudio.IAudioSessionManager2).GUID;
        Marshal.ThrowExceptionForHR(device.Activate(ref managerId, CoreAudio.CLSCTX.CLSCTX_ALL, IntPtr.Zero, out var managerObject));
        var manager = (CoreAudio.IAudioSessionManager2)managerObject;

        Marshal.ThrowExceptionForHR(manager.GetSessionEnumerator(out var sessionEnumerator));
        Marshal.ThrowExceptionForHR(sessionEnumerator.GetCount(out var count));

        for (var i = 0; i < count; i++)
        {
            if (sessionEnumerator.GetSession(i, out var control) != 0)
            {
                continue;
            }

            if (control.GetState(out var state) != 0 || state == CoreAudio.AudioSessionState.AudioSessionStateExpired)
            {
                continue;
            }

            var control2 = (CoreAudio.IAudioSessionControl2)control;
            if (control2.IsSystemSoundsSession() == 0)
            {
                continue;
            }

            if (control2.GetProcessId(out var pid) != 0 || pid == 0)
            {
                continue;
            }

            var volume = (CoreAudio.ISimpleAudioVolume)control;
            if (volume.GetMasterVolume(out var level) != 0 || volume.GetMute(out var muted) != 0)
            {
                continue;
            }

            var displayName = string.Empty;
            if (control.GetDisplayName(out var rawDisplayName) == 0 && !string.IsNullOrWhiteSpace(rawDisplayName))
            {
                displayName = rawDisplayName;
            }

            var processPath = NativeMethods.TryGetProcessImagePath((int)pid);
            var processName = GetProcessName((int)pid, processPath);

            yield return new AudioSession(
                (int)pid,
                processName,
                processPath,
                displayName,
                Math.Clamp(level, 0f, 1f),
                muted,
                volume);
        }
    }

    private static string GetProcessName(int pid, string? processPath)
    {
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            return Path.GetFileNameWithoutExtension(processPath);
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            return process.ProcessName;
        }
        catch
        {
            return pid.ToString();
        }
    }

    private sealed record AudioSession(
        int ProcessId,
        string ProcessName,
        string? ProcessPath,
        string? DisplayName,
        float Volume,
        bool Muted,
        CoreAudio.ISimpleAudioVolume VolumeControl);

    private sealed record MatchContext(
        HashSet<int> ProcessIds,
        HashSet<string> Paths,
        HashSet<string> Directories,
        HashSet<string> Names,
        string DisplayName)
    {
        public static MatchContext Create(ActiveAppTarget target) =>
            new(
                target.CandidateProcessIds.ToHashSet(),
                NormalizePaths(target.CandidatePaths),
                NormalizeDirectories(target.CandidatePaths),
                target.CandidateNames.ToHashSet(StringComparer.OrdinalIgnoreCase),
                target.DisplayName);
    }
}

internal static class CoreAudio
{
    [Flags]
    public enum CLSCTX : uint
    {
        CLSCTX_INPROC_SERVER = 0x1,
        CLSCTX_INPROC_HANDLER = 0x2,
        CLSCTX_LOCAL_SERVER = 0x4,
        CLSCTX_REMOTE_SERVER = 0x10,
        CLSCTX_ALL = CLSCTX_INPROC_SERVER | CLSCTX_INPROC_HANDLER | CLSCTX_LOCAL_SERVER | CLSCTX_REMOTE_SERVER
    }

    public enum EDataFlow
    {
        eRender,
        eCapture,
        eAll,
        EDataFlow_enum_count
    }

    public enum ERole
    {
        eConsole,
        eMultimedia,
        eCommunications,
        ERole_enum_count
    }

    public enum AudioSessionState
    {
        AudioSessionStateInactive = 0,
        AudioSessionStateActive = 1,
        AudioSessionStateExpired = 2
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    public sealed class MMDeviceEnumerator
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, uint dwStateMask, out IntPtr ppDevices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr pClient);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, CLSCTX dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);

        [PreserveSig]
        int OpenPropertyStore(uint stgmAccess, out IntPtr ppProperties);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);

        [PreserveSig]
        int GetState(out uint pdwState);
    }

    [ComImport]
    [Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionManager2
    {
        [PreserveSig]
        int GetAudioSessionControl(IntPtr audioSessionGuid, uint streamFlags, out IntPtr sessionControl);

        [PreserveSig]
        int GetSimpleAudioVolume(IntPtr audioSessionGuid, uint streamFlags, out ISimpleAudioVolume audioVolume);

        [PreserveSig]
        int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);

        [PreserveSig]
        int RegisterSessionNotification(IntPtr sessionNotification);

        [PreserveSig]
        int UnregisterSessionNotification(IntPtr sessionNotification);

        [PreserveSig]
        int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr duckNotification);

        [PreserveSig]
        int UnregisterDuckNotification(IntPtr duckNotification);
    }

    [ComImport]
    [Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionEnumerator
    {
        [PreserveSig]
        int GetCount(out int sessionCount);

        [PreserveSig]
        int GetSession(int sessionCount, out IAudioSessionControl session);
    }

    [ComImport]
    [Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionControl
    {
        [PreserveSig]
        int GetState(out AudioSessionState state);

        [PreserveSig]
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);

        [PreserveSig]
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid eventContext);

        [PreserveSig]
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);

        [PreserveSig]
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid eventContext);

        [PreserveSig]
        int GetGroupingParam(out Guid groupingParam);

        [PreserveSig]
        int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);

        [PreserveSig]
        int RegisterAudioSessionNotification(IntPtr newNotifications);

        [PreserveSig]
        int UnregisterAudioSessionNotification(IntPtr newNotifications);
    }

    [ComImport]
    [Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionControl2
    {
        [PreserveSig]
        int GetState(out AudioSessionState state);

        [PreserveSig]
        int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);

        [PreserveSig]
        int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string displayName, ref Guid eventContext);

        [PreserveSig]
        int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);

        [PreserveSig]
        int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string iconPath, ref Guid eventContext);

        [PreserveSig]
        int GetGroupingParam(out Guid groupingParam);

        [PreserveSig]
        int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);

        [PreserveSig]
        int RegisterAudioSessionNotification(IntPtr newNotifications);

        [PreserveSig]
        int UnregisterAudioSessionNotification(IntPtr newNotifications);

        [PreserveSig]
        int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);

        [PreserveSig]
        int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);

        [PreserveSig]
        int GetProcessId(out uint retVal);

        [PreserveSig]
        int IsSystemSoundsSession();

        [PreserveSig]
        int SetDuckingPreference(bool optOut);
    }

    [ComImport]
    [Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ISimpleAudioVolume
    {
        [PreserveSig]
        int SetMasterVolume(float level, ref Guid eventContext);

        [PreserveSig]
        int GetMasterVolume(out float level);

        [PreserveSig]
        int SetMute(bool isMuted, ref Guid eventContext);

        [PreserveSig]
        int GetMute(out bool isMuted);
    }
}
