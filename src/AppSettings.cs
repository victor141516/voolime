using System.IO;
using System.Text.Json;

namespace Voolime;

internal static class AppSettings
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        AllowTrailingCommas = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    };

    private static string SettingsFilePath => Path.Combine(AppDataRoot, "settings.json");

    private static string AppDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Voolime");

    public static ActivationModifiers LoadKeyboardActivationModifiers() =>
        ParseModifiers(
            LoadSettings().KeyboardActivationModifiers,
            ActivationModifiers.Shift);

    public static void SaveKeyboardActivationModifiers(ActivationModifiers modifiers)
    {
        lock (Sync)
        {
            var settings = LoadSettings();
            settings.KeyboardActivationModifiers = FormatModifiers(modifiers);
            SaveSettings(settings);
        }
    }

    public static ActivationModifiers LoadMouseActivationModifiers() =>
        ParseModifiers(
            LoadSettings().MouseActivationModifiers,
            ActivationModifiers.Control | ActivationModifiers.Shift);

    public static void SaveMouseActivationModifiers(ActivationModifiers modifiers)
    {
        lock (Sync)
        {
            var settings = LoadSettings();
            settings.MouseActivationModifiers = FormatModifiers(modifiers);
            SaveSettings(settings);
        }
    }

    public static string? LoadIndicatorMonitorDeviceName()
    {
        var value = LoadSettings().IndicatorMonitorDeviceName;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static void SaveIndicatorMonitorDeviceName(string? deviceName)
    {
        lock (Sync)
        {
            var settings = LoadSettings();
            settings.IndicatorMonitorDeviceName = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName;
            SaveSettings(settings);
        }
    }

    private static SettingsFile LoadSettings()
    {
        lock (Sync)
        {
            Directory.CreateDirectory(AppDataRoot);
            if (!File.Exists(SettingsFilePath))
            {
                var settings = new SettingsFile();
                SaveSettings(settings);
                return settings;
            }

            try
            {
                var json = File.ReadAllText(SettingsFilePath);
                return JsonSerializer.Deserialize<SettingsFile>(json, JsonOptions) ?? new SettingsFile();
            }
            catch (Exception ex)
            {
                AppLogger.Warn($"Failed to read settings file. Defaults will be used. {ex.Message}");
                return new SettingsFile();
            }
        }
    }

    private static void SaveSettings(SettingsFile settings)
    {
        Directory.CreateDirectory(AppDataRoot);
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(SettingsFilePath, json);
    }

    private static ActivationModifiers ParseModifiers(string[]? values, ActivationModifiers defaultModifiers)
    {
        if (values is null)
        {
            return defaultModifiers;
        }

        var modifiers = ActivationModifiers.None;
        foreach (var value in values)
        {
            if (Enum.TryParse<ActivationModifiers>(value, ignoreCase: true, out var modifier))
            {
                modifiers |= modifier;
            }
            else
            {
                AppLogger.Warn($"Unknown activation modifier in settings file: {value}");
            }
        }

        return modifiers;
    }

    private static string[] FormatModifiers(ActivationModifiers modifiers)
    {
        var values = new List<string>();
        if (modifiers.HasFlag(ActivationModifiers.Control))
        {
            values.Add(nameof(ActivationModifiers.Control));
        }

        if (modifiers.HasFlag(ActivationModifiers.Shift))
        {
            values.Add(nameof(ActivationModifiers.Shift));
        }

        if (modifiers.HasFlag(ActivationModifiers.Alt))
        {
            values.Add(nameof(ActivationModifiers.Alt));
        }

        return values.ToArray();
    }

    private sealed class SettingsFile
    {
        public string[] KeyboardActivationModifiers { get; set; } =
        [
            nameof(ActivationModifiers.Shift)
        ];

        public string[] MouseActivationModifiers { get; set; } =
        [
            nameof(ActivationModifiers.Control),
            nameof(ActivationModifiers.Shift)
        ];

        public string? IndicatorMonitorDeviceName { get; set; }
    }
}
