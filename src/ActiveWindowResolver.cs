using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Voolime;

internal sealed record ProcessIdentity(
    int ProcessId,
    string ProcessName,
    string? ProcessPath);

internal sealed record OpenApplicationInfo(
    string ProcessName,
    string? ProcessPath,
    string DisplayName);

internal sealed record ActiveAppTarget(
    IntPtr WindowHandle,
    int ProcessId,
    string ProcessName,
    string? ProcessPath,
    string DisplayName,
    IReadOnlyCollection<ProcessIdentity> Candidates)
{
    public IEnumerable<string> CandidateNames =>
        Candidates.Select(c => c.ProcessName).Where(static n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<string> CandidatePaths =>
        Candidates.Select(c => c.ProcessPath).Where(static p => !string.IsNullOrWhiteSpace(p)).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<int> CandidateProcessIds =>
        Candidates.Select(static c => c.ProcessId).Distinct();
}

internal sealed class ActiveWindowResolver
{
    private static readonly HashSet<string> HostProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApplicationFrameHost",
        "WWAHost"
    };

    private readonly int _ownProcessId = Environment.ProcessId;

    public ActiveAppTarget? GetActiveTarget()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
        {
            return null;
        }

        hwnd = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
        NativeMethods.GetWindowThreadProcessId(hwnd, out var rootPid);

        var candidates = CollectCandidates(hwnd)
            .Where(c => c.ProcessId != _ownProcessId)
            .GroupBy(static c => c.ProcessId)
            .Select(static g => g.First())
            .ToList();

        if (rootPid != 0 && candidates.All(c => c.ProcessId != (int)rootPid))
        {
            var root = TryGetProcessIdentity((int)rootPid);
            if (root is not null && root.ProcessId != _ownProcessId)
            {
                candidates.Insert(0, root);
            }
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        var primary = ChoosePrimary(candidates);
        var displayName = GetDisplayName(hwnd, primary);

        return new ActiveAppTarget(
            hwnd,
            primary.ProcessId,
            primary.ProcessName,
            primary.ProcessPath,
            displayName,
            candidates);
    }

    public IReadOnlyList<OpenApplicationInfo> GetOpenApplications()
    {
        var applications = new List<OpenApplicationInfo>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.GetWindowTextLength(hwnd) <= 0)
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || pid == _ownProcessId)
            {
                return true;
            }

            var process = TryGetProcessIdentity((int)pid);
            if (process is not null && !HostProcessNames.Contains(process.ProcessName))
            {
                applications.Add(new OpenApplicationInfo(
                    process.ProcessName,
                    process.ProcessPath,
                    GetDisplayName(hwnd, process)));
            }

            return true;
        }, IntPtr.Zero);

        return applications
            .GroupBy(
                static app => !string.IsNullOrWhiteSpace(app.ProcessPath)
                    ? $"path:{app.ProcessPath}"
                    : $"name:{app.ProcessName}",
                StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(static app => app.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static ProcessIdentity ChoosePrimary(IReadOnlyList<ProcessIdentity> candidates)
    {
        var root = candidates[0];
        if (!HostProcessNames.Contains(root.ProcessName))
        {
            return root;
        }

        return candidates.FirstOrDefault(c => !HostProcessNames.Contains(c.ProcessName)) ?? root;
    }

    private static IEnumerable<ProcessIdentity> CollectCandidates(IntPtr rootHwnd)
    {
        NativeMethods.GetWindowThreadProcessId(rootHwnd, out var rootPid);
        var root = TryGetProcessIdentity((int)rootPid);
        if (root is not null)
        {
            yield return root;
        }

        var childPids = new HashSet<int>();
        NativeMethods.EnumChildWindows(rootHwnd, (child, _) =>
        {
            if (!NativeMethods.IsWindowVisible(child))
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(child, out var childPid);
            if (childPid != 0)
            {
                childPids.Add((int)childPid);
            }

            return true;
        }, IntPtr.Zero);

        foreach (var pid in childPids)
        {
            var identity = TryGetProcessIdentity(pid);
            if (identity is not null)
            {
                yield return identity;
            }
        }
    }

    private static ProcessIdentity? TryGetProcessIdentity(int pid)
    {
        if (pid <= 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            var path = NativeMethods.TryGetProcessImagePath(pid);
            return new ProcessIdentity(pid, process.ProcessName, path);
        }
        catch
        {
            return null;
        }
    }

    private static string GetDisplayName(IntPtr hwnd, ProcessIdentity process)
    {
        var description = TryGetFileDescription(process.ProcessPath);
        if (!string.IsNullOrWhiteSpace(description))
        {
            return description;
        }

        var title = GetWindowTitle(hwnd);
        if (!string.IsNullOrWhiteSpace(title))
        {
            return title;
        }

        return process.ProcessName;
    }

    private static string? TryGetFileDescription(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(info.FileDescription))
            {
                return info.FileDescription;
            }

            return !string.IsNullOrWhiteSpace(info.ProductName) ? info.ProductName : null;
        }
        catch
        {
            return null;
        }
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var length = NativeMethods.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(length + 1);
        _ = NativeMethods.GetWindowText(hwnd, builder, builder.Capacity);
        return builder.ToString();
    }
}
