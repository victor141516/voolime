using System.IO;
using System.Text;

namespace Voolime;

internal sealed class ApplicationKeyService
{
    private static readonly Dictionary<string, int> PreferredProcessKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = 'C',
        ["msedge"] = 'E',
        ["msteams"] = 'T',
        ["ms-teams"] = 'T',
        ["teams"] = 'T',
        ["ciscocollabhost"] = 'W',
        ["webex"] = 'W',
        ["wmplayer"] = 'M'
    };

    private static readonly string[] PopularAudioProcessFragments =
    [
        "chrome", "msedge", "firefox", "brave", "opera", "spotify", "vlc", "discord",
        "steam", "msteams", "teams", "webex", "zoom", "wmplayer", "itunes", "applemusic",
        "amazonmusic", "primevideo", "netflix"
    ];

    private readonly AudioSessionService _audio;
    private readonly ActiveWindowResolver _windowResolver;
    private readonly Dictionary<string, ApplicationKeySetting> _assignments;
    private HashSet<string> _currentlyObservedAudioIds = new(StringComparer.OrdinalIgnoreCase);

    public ApplicationKeyService(AudioSessionService audio, ActiveWindowResolver windowResolver)
    {
        _audio = audio;
        _windowResolver = windowResolver;
        _assignments = AppSettings.LoadApplicationKeys()
            .GroupBy(static assignment => assignment.AppId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.First().Clone(), StringComparer.OrdinalIgnoreCase);
    }

    public event EventHandler? AssignmentsChanged;

    public IReadOnlyList<ApplicationKeySetting> Entries =>
        _assignments.Values
            .Where(IsRelevantEntry)
            .OrderByDescending(static assignment => assignment.Enabled)
            .ThenByDescending(static assignment => assignment.AudioObservationCount)
            .ThenBy(static assignment => assignment.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(static assignment => assignment.Clone())
            .ToArray();

    public IReadOnlyDictionary<int, ApplicationKeySetting> EnabledByVirtualKey =>
        _assignments.Values
            .Where(static assignment => assignment.Enabled && assignment.VirtualKey.HasValue)
            .GroupBy(static assignment => assignment.VirtualKey!.Value)
            .ToDictionary(static group => group.Key, static group => group.First().Clone());

    public void Refresh()
    {
        var now = DateTimeOffset.UtcNow;
        var changed = false;
        IReadOnlyList<AudioApplicationInfo> audioApplications;
        IReadOnlyList<OpenApplicationInfo> openApplications;

        try
        {
            audioApplications = _audio.DiscoverApplications();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to discover audio applications. {ex.Message}");
            audioApplications = [];
        }

        try
        {
            openApplications = _windowResolver.GetOpenApplications();
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"Failed to discover open applications. {ex.Message}");
            openApplications = [];
        }

        var observedAudioIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var application in audioApplications)
        {
            var appId = GetAppId(application.ProcessName);
            observedAudioIds.Add(appId);
            changed |= AddOrUpdate(
                appId,
                application.ProcessName,
                application.ProcessPath,
                application.DisplayName,
                now,
                audioObserved: true,
                audioActive: application.IsAudioActive);
        }

        foreach (var application in openApplications)
        {
            var appId = GetAppId(application.ProcessName);
            var matchingAudioAssignment = _assignments.Values.FirstOrDefault(assignment =>
                observedAudioIds.Contains(assignment.AppId) &&
                AreSameApplication(assignment, application));
            if (matchingAudioAssignment is not null)
            {
                if (_assignments.TryGetValue(appId, out var duplicate) &&
                    !duplicate.IsUserConfigured &&
                    duplicate.AudioObservationCount == 0)
                {
                    _assignments.Remove(appId);
                    changed = true;
                }

                matchingAudioAssignment.LastSeenUtc = now;
                continue;
            }

            changed |= AddOrUpdate(
                appId,
                application.ProcessName,
                application.ProcessPath,
                application.DisplayName,
                now,
                audioObserved: false,
                audioActive: false);
        }

        _currentlyObservedAudioIds = observedAudioIds;
        changed |= RebuildAutomaticAssignments();

        if (changed)
        {
            Save();
            AssignmentsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool TrySetKey(string appId, int? virtualKey, out string? error)
    {
        error = null;
        if (!_assignments.TryGetValue(appId, out var assignment))
        {
            error = "The application is no longer available.";
            return false;
        }

        if (virtualKey.HasValue && !ApplicationKeyValidation.IsAllowed(virtualKey.Value))
        {
            error = "Use a letter, number, or punctuation key.";
            return false;
        }

        if (virtualKey.HasValue)
        {
            var duplicate = _assignments.Values.FirstOrDefault(other =>
                !string.Equals(other.AppId, appId, StringComparison.OrdinalIgnoreCase) &&
                other.VirtualKey == virtualKey);
            if (duplicate is not null)
            {
                error = $"{ApplicationKeyValidation.GetDisplayName(virtualKey.Value)} is already assigned to {duplicate.DisplayName}.";
                return false;
            }
        }

        assignment.VirtualKey = virtualKey;
        assignment.Enabled = virtualKey.HasValue && assignment.Enabled;
        assignment.IsUserConfigured = true;
        SaveAndNotify();
        return true;
    }

    public bool TrySetEnabled(string appId, bool enabled, out string? error)
    {
        error = null;
        if (!_assignments.TryGetValue(appId, out var assignment))
        {
            error = "The application is no longer available.";
            return false;
        }

        if (enabled && !assignment.VirtualKey.HasValue)
        {
            error = "Choose an application key first.";
            return false;
        }

        assignment.Enabled = enabled;
        assignment.IsUserConfigured = true;
        SaveAndNotify();
        return true;
    }

    public ActiveAppTarget CreateTarget(ApplicationKeySetting assignment)
    {
        var identity = new ProcessIdentity(0, assignment.ProcessName, assignment.ProcessPath);
        return new ActiveAppTarget(
            IntPtr.Zero,
            0,
            assignment.ProcessName,
            assignment.ProcessPath,
            assignment.DisplayName,
            [identity]);
    }

    public void Save() => AppSettings.SaveApplicationKeys(_assignments.Values);

    private bool AddOrUpdate(
        string appId,
        string processName,
        string? processPath,
        string displayName,
        DateTimeOffset now,
        bool audioObserved,
        bool audioActive)
    {
        if (!_assignments.TryGetValue(appId, out var assignment))
        {
            assignment = new ApplicationKeySetting
            {
                AppId = appId,
                ProcessName = processName,
                ProcessPath = processPath,
                DisplayName = GetCleanDisplayName(displayName, processName),
                AudioObservationCount = audioObserved ? 1 : 0,
                LastSeenUtc = now
            };
            _assignments.Add(appId, assignment);
            return true;
        }

        assignment.ProcessName = processName;
        assignment.ProcessPath = string.IsNullOrWhiteSpace(processPath) ? assignment.ProcessPath : processPath;
        assignment.DisplayName = GetCleanDisplayName(displayName, processName);
        assignment.LastSeenUtc = now;
        if (audioObserved)
        {
            assignment.AudioObservationCount = Math.Max(1, assignment.AudioObservationCount);
            if (audioActive)
            {
                assignment.AudioObservationCount++;
            }
        }

        return false;
    }

    private bool RebuildAutomaticAssignments()
    {
        var candidates = _assignments.Values
            .Where(assignment => !assignment.IsUserConfigured && IsAutoAssignmentAvailable(assignment))
            .OrderByDescending(GetPopularAudioPriority)
            .ThenByDescending(static assignment => assignment.AudioObservationCount)
            .ThenBy(static assignment => assignment.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        var usedKeys = _assignments.Values
            .Where(static assignment => assignment.IsUserConfigured && assignment.VirtualKey.HasValue)
            .Select(static assignment => assignment.VirtualKey!.Value)
            .ToHashSet();
        var desiredKeys = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);

        foreach (var assignment in candidates)
        {
            var preferredKey = GetPreferredKey(assignment);
            desiredKeys[assignment.AppId] = preferredKey.HasValue && usedKeys.Add(preferredKey.Value)
                ? preferredKey
                : null;
        }

        var changed = false;
        foreach (var assignment in _assignments.Values.Where(static assignment => !assignment.IsUserConfigured))
        {
            var newKey = desiredKeys.GetValueOrDefault(assignment.AppId);
            if (assignment.VirtualKey != newKey || assignment.Enabled)
            {
                assignment.VirtualKey = newKey;
                assignment.Enabled = false;
                changed = true;
            }
        }

        return changed;
    }

    private bool IsAutoAssignmentAvailable(ApplicationKeySetting assignment) =>
        _currentlyObservedAudioIds.Contains(assignment.AppId) ||
        assignment.AudioObservationCount > 0 &&
        !string.IsNullOrWhiteSpace(assignment.ProcessPath) &&
        File.Exists(assignment.ProcessPath);

    private bool IsRelevantEntry(ApplicationKeySetting assignment) =>
        assignment.IsUserConfigured ||
        assignment.AudioObservationCount > 0 ||
        assignment.LastSeenUtc >= DateTimeOffset.UtcNow.AddMinutes(-5);

    private static int? GetPreferredKey(ApplicationKeySetting assignment)
    {
        if (PreferredProcessKeys.TryGetValue(assignment.ProcessName, out var preferred))
        {
            return preferred;
        }

        var displayName = assignment.DisplayName;
        if (displayName.Contains("Google Chrome", StringComparison.OrdinalIgnoreCase))
        {
            return 'C';
        }

        if (displayName.Contains("Microsoft Edge", StringComparison.OrdinalIgnoreCase))
        {
            return 'E';
        }

        if (displayName.Contains("Microsoft Teams", StringComparison.OrdinalIgnoreCase))
        {
            return 'T';
        }

        if (displayName.Contains("Cisco Webex", StringComparison.OrdinalIgnoreCase))
        {
            return 'W';
        }

        if (displayName.Contains("Windows Media Player", StringComparison.OrdinalIgnoreCase))
        {
            return 'M';
        }

        var first = assignment.DisplayName.FirstOrDefault(char.IsLetterOrDigit);
        if (first == default)
        {
            first = assignment.ProcessName.FirstOrDefault(char.IsLetterOrDigit);
        }

        if (first == default || first > 127)
        {
            return null;
        }

        var virtualKey = char.ToUpperInvariant(first);
        return ApplicationKeyValidation.IsAllowed(virtualKey) ? virtualKey : null;
    }

    private static int GetPopularAudioPriority(ApplicationKeySetting assignment) =>
        PopularAudioProcessFragments.Any(fragment =>
            assignment.ProcessName.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            ? 1
            : 0;

    private static string GetAppId(string processName) =>
        $"process:{processName.Trim().ToLowerInvariant()}";

    private static string GetCleanDisplayName(string displayName, string processName) =>
        string.IsNullOrWhiteSpace(displayName) ? processName : displayName.Trim();

    private static bool AreSameApplication(ApplicationKeySetting assignment, OpenApplicationInfo application)
    {
        if (!string.Equals(assignment.DisplayName, application.DisplayName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var assignmentDirectory = GetDirectory(assignment.ProcessPath);
        var applicationDirectory = GetDirectory(application.ProcessPath);
        return !string.IsNullOrWhiteSpace(assignmentDirectory) &&
               string.Equals(assignmentDirectory, applicationDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(path));
        }
        catch
        {
            return Path.GetDirectoryName(path);
        }
    }

    private void SaveAndNotify()
    {
        Save();
        AssignmentsChanged?.Invoke(this, EventArgs.Empty);
    }
}

internal static class ApplicationKeyValidation
{
    private static readonly HashSet<int> PunctuationKeys =
    [
        0x6A, 0x6B, 0x6D, 0x6E, 0x6F,
        0xBA, 0xBB, 0xBC, 0xBD, 0xBE, 0xBF, 0xC0,
        0xDB, 0xDC, 0xDD, 0xDE, 0xE2
    ];

    public static bool IsAllowed(int virtualKey) =>
        virtualKey is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x60 and <= 0x69 ||
        PunctuationKeys.Contains(virtualKey);

    public static string GetDisplayName(int virtualKey)
    {
        if (virtualKey is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A)
        {
            return ((char)virtualKey).ToString();
        }

        var scanCode = NativeMethods.MapVirtualKey((uint)virtualKey, 0);
        var name = new StringBuilder(32);
        if (scanCode != 0 && NativeMethods.GetKeyNameText((int)(scanCode << 16), name, name.Capacity) > 0)
        {
            return name.ToString();
        }

        return $"Key {virtualKey:X2}";
    }
}
