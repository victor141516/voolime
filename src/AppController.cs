using System;
using System.Drawing;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;

namespace Voolime;

internal sealed class AppController : IDisposable
{
    private readonly WpfApplication _application;
    private readonly ActiveWindowResolver _windowResolver = new();
    private readonly AudioSessionService _audio = new();
    private readonly ApplicationKeyService _applicationKeys;
    private readonly FlyoutWindow _flyout = new();
    private readonly HotkeyService _hotkeys;
    private readonly DispatcherTimer _applicationDiscoveryTimer;
    private readonly StartupShortcutService _startupShortcut = new();
    private readonly UpdateService _updateService = new();
    private readonly Icon _appIcon;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly Forms.ToolStripMenuItem _startWithWindowsItem;
    private readonly Forms.ToolStripMenuItem _showIndicatorOnItem;
    private readonly ModifierMenuItems _keyboardModifierItems;
    private readonly ModifierMenuItems _mouseModifierItems;
    private ActiveAppTarget? _flyoutTarget;
    private ApplicationKeysWindow? _applicationKeysWindow;
    private string? _indicatorMonitorDeviceName;
    private bool _disposed;

    public AppController(WpfApplication application)
    {
        _application = application;
        _appIcon = LoadAppIcon();
        _applicationKeys = new ApplicationKeyService(_audio, _windowResolver);
        _applicationKeys.Refresh();
        _hotkeys = new HotkeyService(
            HandleHotkey,
            AppSettings.LoadKeyboardActivationModifiers(),
            AppSettings.LoadMouseActivationModifiers());
        _applicationKeys.AssignmentsChanged += HandleApplicationKeysChanged;
        UpdateHotkeyApplicationKeys();
        _applicationDiscoveryTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _applicationDiscoveryTimer.Tick += HandleApplicationDiscoveryTick;
        _applicationDiscoveryTimer.Start();
        _indicatorMonitorDeviceName = AppSettings.LoadIndicatorMonitorDeviceName();
        _flyout.SetIndicatorMonitorDeviceName(_indicatorMonitorDeviceName);
        _flyout.VolumeRequested += HandleFlyoutVolumeRequested;
        (_trayIcon, _startWithWindowsItem, _showIndicatorOnItem, _keyboardModifierItems, _mouseModifierItems) = CreateTrayIcon();
        SystemEvents.DisplaySettingsChanged += HandleDisplaySettingsChanged;
        UpdateModifierChecks();
        _updateService.CheckOnStartup(_application);
    }

    private (Forms.NotifyIcon TrayIcon, Forms.ToolStripMenuItem StartWithWindows, Forms.ToolStripMenuItem ShowIndicatorOn, ModifierMenuItems Keyboard, ModifierMenuItems Mouse) CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Opening += (_, _) =>
        {
            UpdateStartupShortcutCheck();
            UpdateMonitorMenu();
        };

        var startWithWindows = new Forms.ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartWithWindows())
        {
            CheckOnClick = false
        };
        var showIndicatorOn = new Forms.ToolStripMenuItem("Show Indicator On");

        menu.Items.Add("Open Volume Mixer", null, (_, _) => OpenLegacyVolumeMixer());
        menu.Items.Add("Open Playback and Recording Devices", null, (_, _) => OpenPlaybackAndRecordingDevices());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(showIndicatorOn);
        menu.Items.Add("Application Keys...", null, (_, _) => ShowApplicationKeys());

        var keyboardModifiers = CreateModifierMenu("Keyboard Activation Key", ToggleKeyboardModifier);
        var mouseModifiers = CreateModifierMenu("Mouse Activation Key", ToggleMouseModifier);

        menu.Items.Add(keyboardModifiers.Root);
        menu.Items.Add(mouseModifiers.Root);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(startWithWindows);
        menu.Items.Add("Exit", null, (_, _) => _application.Shutdown());

        var trayIcon = new Forms.NotifyIcon
        {
            Icon = _appIcon,
            Text = GetTrayText(),
            ContextMenuStrip = menu,
            Visible = true
        };

        UpdateMonitorMenu(showIndicatorOn);
        return (trayIcon, startWithWindows, showIndicatorOn, keyboardModifiers, mouseModifiers);
    }

    private void UpdateMonitorMenu() =>
        UpdateMonitorMenu(_showIndicatorOnItem);

    private void UpdateMonitorMenu(Forms.ToolStripMenuItem root)
    {
        root.DropDownItems.Clear();

        root.DropDownItems.Add(new Forms.ToolStripMenuItem("Primary Monitor", null, (_, _) => SetIndicatorMonitorDeviceName(null))
        {
            Checked = IsPrimaryIndicatorMonitorSelected(),
            CheckOnClick = false
        });

        var screens = Forms.Screen.AllScreens
            .OrderBy(screen => GetMonitorSortKey(screen.DeviceName))
            .ThenBy(screen => screen.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (screens.Length == 0)
        {
            return;
        }

        root.DropDownItems.Add(new Forms.ToolStripSeparator());
        for (var index = 0; index < screens.Length; index++)
        {
            var screen = screens[index];
            var label = $"Monitor {index + 1}";
            var displayName = GetDisplayName(screen.DeviceName);
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                label += $" - {displayName}";
            }

            if (screen.Primary)
            {
                label += " (primary)";
            }

            var deviceName = screen.DeviceName;
            root.DropDownItems.Add(new Forms.ToolStripMenuItem(label, null, (_, _) => SetIndicatorMonitorDeviceName(deviceName))
            {
                Checked = string.Equals(_indicatorMonitorDeviceName, deviceName, StringComparison.OrdinalIgnoreCase),
                CheckOnClick = false
            });
        }
    }

    private bool IsPrimaryIndicatorMonitorSelected()
    {
        if (string.IsNullOrWhiteSpace(_indicatorMonitorDeviceName))
        {
            return true;
        }

        return !Forms.Screen.AllScreens.Any(screen =>
            string.Equals(screen.DeviceName, _indicatorMonitorDeviceName, StringComparison.OrdinalIgnoreCase));
    }

    private void SetIndicatorMonitorDeviceName(string? deviceName)
    {
        _indicatorMonitorDeviceName = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName;
        AppLogger.Info($"Tray monitor selection clicked: {FormatIndicatorMonitorSelection(_indicatorMonitorDeviceName)}.");
        AppSettings.SaveIndicatorMonitorDeviceName(_indicatorMonitorDeviceName);
        _flyout.SetIndicatorMonitorDeviceName(_indicatorMonitorDeviceName);
        UpdateMonitorMenu();
    }

    private static ModifierMenuItems CreateModifierMenu(string text, Action<ActivationModifiers> toggle)
    {
        var root = new Forms.ToolStripMenuItem(text);
        var shift = CreateModifierItem("Shift", ActivationModifiers.Shift, toggle);
        var control = CreateModifierItem("Control", ActivationModifiers.Control, toggle);
        var alt = CreateModifierItem("Alt", ActivationModifiers.Alt, toggle);
        root.DropDownItems.AddRange(new Forms.ToolStripItem[] { shift, control, alt });
        return new ModifierMenuItems(root, shift, control, alt);
    }

    private static Forms.ToolStripMenuItem CreateModifierItem(string text, ActivationModifiers modifier, Action<ActivationModifiers> toggle) =>
        new(text, null, (_, _) => toggle(modifier))
        {
            CheckOnClick = false
        };

    private void ToggleKeyboardModifier(ActivationModifiers modifier)
    {
        var modifiers = ToggleModifier(_hotkeys.KeyboardModifiers, modifier);
        _hotkeys.SetKeyboardModifiers(modifiers);
        AppSettings.SaveKeyboardActivationModifiers(modifiers);
        UpdateModifierChecks();
    }

    private void ToggleMouseModifier(ActivationModifiers modifier)
    {
        var modifiers = ToggleModifier(_hotkeys.MouseModifiers, modifier);
        _hotkeys.SetMouseModifiers(modifiers);
        AppSettings.SaveMouseActivationModifiers(modifiers);
        UpdateModifierChecks();
    }

    private static ActivationModifiers ToggleModifier(ActivationModifiers current, ActivationModifiers modifier) =>
        current.HasFlag(modifier) ? current & ~modifier : current | modifier;

    private void UpdateModifierChecks()
    {
        UpdateModifierMenuChecks(_keyboardModifierItems, _hotkeys.KeyboardModifiers);
        UpdateModifierMenuChecks(_mouseModifierItems, _hotkeys.MouseModifiers);
        _trayIcon.Text = GetTrayText();
    }

    private static void UpdateModifierMenuChecks(ModifierMenuItems items, ActivationModifiers modifiers)
    {
        items.Shift.Checked = modifiers.HasFlag(ActivationModifiers.Shift);
        items.Control.Checked = modifiers.HasFlag(ActivationModifiers.Control);
        items.Alt.Checked = modifiers.HasFlag(ActivationModifiers.Alt);
    }

    private string GetTrayText() =>
        $"Voolime {GetAppVersionLabel()} - key: {GetModifierLabel(_hotkeys.KeyboardModifiers)}, wheel: {GetModifierLabel(_hotkeys.MouseModifiers)}";

    private static string GetAppVersionLabel()
    {
        var assembly = typeof(AppController).Assembly;
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var metadataIndex = informationalVersion.IndexOf('+', StringComparison.Ordinal);
            return metadataIndex >= 0
                ? informationalVersion[..metadataIndex]
                : informationalVersion;
        }

        var version = assembly.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{Math.Max(version.Build, 0)}";
    }

    private void UpdateStartupShortcutCheck() =>
        _startWithWindowsItem.Checked = _startupShortcut.IsEnabled();

    private void ToggleStartWithWindows()
    {
        try
        {
            var enabled = _startupShortcut.Toggle();
            _startWithWindowsItem.Checked = enabled;
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to toggle startup shortcut.", ex);
            Forms.MessageBox.Show(
                $"Could not update the startup shortcut.\n\n{ex.Message}",
                "Voolime",
                Forms.MessageBoxButtons.OK,
                Forms.MessageBoxIcon.Error);
            UpdateStartupShortcutCheck();
        }
    }

    private void HandleDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        AppLogger.Info("Display settings changed; refreshing indicator monitor.");
        _application.Dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            _flyout.RefreshMonitorPosition();
            UpdateMonitorMenu();
        });
    }

    private static void OpenLegacyVolumeMixer()
    {
        try
        {
            Process.Start(new ProcessStartInfo("sndvol.exe") { UseShellExecute = true });
            AppLogger.Info("Opened legacy volume mixer.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to open legacy volume mixer.", ex);
            Forms.MessageBox.Show(
                $"Could not open the legacy volume mixer.\n\n{ex.Message}",
                "Voolime",
                Forms.MessageBoxButtons.OK,
                Forms.MessageBoxIcon.Error);
        }
    }

    private static void OpenPlaybackAndRecordingDevices()
    {
        try
        {
            Process.Start(new ProcessStartInfo("rundll32.exe")
            {
                Arguments = "shell32.dll,Control_RunDLL mmsys.cpl,,0",
                UseShellExecute = true
            });
            AppLogger.Info("Opened legacy playback and recording devices.");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Failed to open legacy playback and recording devices.", ex);
            Forms.MessageBox.Show(
                $"Could not open playback and recording devices.\n\n{ex.Message}",
                "Voolime",
                Forms.MessageBoxButtons.OK,
                Forms.MessageBoxIcon.Error);
        }
    }

    private void ShowApplicationKeys()
    {
        if (_applicationKeysWindow is { IsVisible: true })
        {
            _applicationKeysWindow.Activate();
            return;
        }

        _applicationKeysWindow = new ApplicationKeysWindow(_applicationKeys);
        _applicationKeysWindow.Closed += (_, _) => _applicationKeysWindow = null;
        _applicationKeysWindow.Show();
        _applicationKeysWindow.Activate();
    }

    private void HandleApplicationDiscoveryTick(object? sender, EventArgs e) =>
        _applicationKeys.Refresh();

    private void HandleApplicationKeysChanged(object? sender, EventArgs e) =>
        UpdateHotkeyApplicationKeys();

    private void UpdateHotkeyApplicationKeys() =>
        _hotkeys.SetApplicationKeys(_applicationKeys.EnabledByVirtualKey.Keys);

    private static string GetModifierLabel(ActivationModifiers modifiers)
    {
        if (modifiers == ActivationModifiers.None)
        {
            return "Off";
        }

        var parts = new List<string>();
        if (modifiers.HasFlag(ActivationModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (modifiers.HasFlag(ActivationModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (modifiers.HasFlag(ActivationModifiers.Alt))
        {
            parts.Add("Alt");
        }

        return string.Join("+", parts);
    }

    private static int GetMonitorSortKey(string deviceName)
    {
        for (var index = deviceName.Length - 1; index >= 0; index--)
        {
            if (!char.IsDigit(deviceName[index]))
            {
                return int.TryParse(deviceName[(index + 1)..], out var value)
                    ? value
                    : int.MaxValue;
            }
        }

        return int.TryParse(deviceName, out var fallbackValue)
            ? fallbackValue
            : int.MaxValue;
    }

    private static string GetDisplayName(string deviceName)
    {
        const string displayPrefix = "\\\\.\\";
        return deviceName.StartsWith(displayPrefix, StringComparison.Ordinal)
            ? deviceName[displayPrefix.Length..]
            : deviceName;
    }

    private static string FormatIndicatorMonitorSelection(string? deviceName) =>
        string.IsNullOrWhiteSpace(deviceName) ? "Primary Monitor" : deviceName;

    private static Icon LoadAppIcon()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            try
            {
                processPath = Process.GetCurrentProcess().MainModule?.FileName;
            }
            catch
            {
                processPath = null;
            }
        }

        if (!string.IsNullOrWhiteSpace(processPath))
        {
            try
            {
                var icon = Icon.ExtractAssociatedIcon(processPath);
                if (icon is not null)
                {
                    return icon;
                }
            }
            catch
            {
                // Fall through to a cloned system icon.
            }
        }

        return (Icon)SystemIcons.Application.Clone();
    }

    private void HandleHotkey(VolumeHotkeyPress press)
    {
        AppLogger.Info($"Hotkey received: {press.Kind}, held repeat: {press.IsHeldRepeat}, application key: {press.ApplicationVirtualKey?.ToString("X2") ?? "none"}.");
        _application.Dispatcher.Invoke(() =>
        {
            ActiveAppTarget? target;
            if (press.ApplicationVirtualKey.HasValue &&
                _applicationKeys.EnabledByVirtualKey.TryGetValue(press.ApplicationVirtualKey.Value, out var assignment))
            {
                target = _applicationKeys.CreateTarget(assignment);
            }
            else
            {
                target = _windowResolver.GetActiveTarget();
            }

            if (target is null)
            {
                AppLogger.Warn("No active window was detected for a hotkey press.");
                _flyoutTarget = null;
                _flyout.ShowStatus("No active window", "No app detected", 0, muted: false, null);
                return;
            }

            _flyoutTarget = target;
            VolumeChangeResult result;
            try
            {
                result = _audio.Apply(target, press.Kind, press.IsHeldRepeat);
            }
            catch (Exception ex)
            {
                AppLogger.Error("Volume change failed.", ex);
                result = VolumeChangeResult.Failed(target.DisplayName, ex.Message);
            }

            AppLogger.Info($"Volume result for {result.DisplayName}: {result.Message}, success: {result.Success}.");

            var icon = AppIconProvider.GetIcon(target.ProcessPath);
            _flyout.ShowStatus(
                result.DisplayName,
                result.Message,
                result.Volume,
                result.Muted,
                icon);
        });
    }

    private void HandleFlyoutVolumeRequested(double volume)
    {
        var target = _flyoutTarget;
        if (target is null)
        {
            return;
        }

        VolumeChangeResult result;
        try
        {
            result = _audio.SetVolume(target, volume);
        }
        catch (Exception ex)
        {
            AppLogger.Error("Flyout volume change failed.", ex);
            result = VolumeChangeResult.Failed(target.DisplayName, ex.Message);
        }

        var icon = AppIconProvider.GetIcon(target.ProcessPath);
        _flyout.ShowStatus(
            result.DisplayName,
            result.Message,
            result.Volume,
            result.Muted,
            icon);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        SystemEvents.DisplaySettingsChanged -= HandleDisplaySettingsChanged;
        _applicationDiscoveryTimer.Stop();
        _applicationDiscoveryTimer.Tick -= HandleApplicationDiscoveryTick;
        _applicationKeys.AssignmentsChanged -= HandleApplicationKeysChanged;
        _applicationKeys.Save();
        _applicationKeysWindow?.Close();
        _hotkeys.Dispose();
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _appIcon.Dispose();
        _flyout.Close();
    }

    private sealed record ModifierMenuItems(
        Forms.ToolStripMenuItem Root,
        Forms.ToolStripMenuItem Shift,
        Forms.ToolStripMenuItem Control,
        Forms.ToolStripMenuItem Alt);
}
