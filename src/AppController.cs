using System;
using System.Drawing;
using System.Diagnostics;
using System.Reflection;
using Forms = System.Windows.Forms;
using WpfApplication = System.Windows.Application;

namespace Voolime;

internal sealed class AppController : IDisposable
{
    private readonly WpfApplication _application;
    private readonly ActiveWindowResolver _windowResolver = new();
    private readonly AudioSessionService _audio = new();
    private readonly FlyoutWindow _flyout = new();
    private readonly HotkeyService _hotkeys;
    private readonly StartupShortcutService _startupShortcut = new();
    private readonly UpdateService _updateService = new();
    private readonly Icon _appIcon;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly Forms.ToolStripMenuItem _startWithWindowsItem;
    private readonly ModifierMenuItems _keyboardModifierItems;
    private readonly ModifierMenuItems _mouseModifierItems;
    private ActiveAppTarget? _flyoutTarget;
    private bool _disposed;

    public AppController(WpfApplication application)
    {
        _application = application;
        _appIcon = LoadAppIcon();
        _hotkeys = new HotkeyService(
            HandleHotkey,
            AppSettings.LoadKeyboardActivationModifiers(),
            AppSettings.LoadMouseActivationModifiers());
        _flyout.VolumeRequested += HandleFlyoutVolumeRequested;
        (_trayIcon, _startWithWindowsItem, _keyboardModifierItems, _mouseModifierItems) = CreateTrayIcon();
        UpdateModifierChecks();
        _updateService.CheckOnStartup(_application);
    }

    private (Forms.NotifyIcon TrayIcon, Forms.ToolStripMenuItem StartWithWindows, ModifierMenuItems Keyboard, ModifierMenuItems Mouse) CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Opening += (_, _) => UpdateStartupShortcutCheck();

        var startWithWindows = new Forms.ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartWithWindows())
        {
            CheckOnClick = false
        };

        menu.Items.Add("Open Volume Mixer", null, (_, _) => OpenLegacyVolumeMixer());
        menu.Items.Add("Open Playback and Recording Devices", null, (_, _) => OpenPlaybackAndRecordingDevices());
        menu.Items.Add(new Forms.ToolStripSeparator());

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

        return (trayIcon, startWithWindows, keyboardModifiers, mouseModifiers);
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
        AppLogger.Info($"Hotkey received: {press.Kind}, held repeat: {press.IsHeldRepeat}.");
        _application.Dispatcher.Invoke(() =>
        {
            var target = _windowResolver.GetActiveTarget();
            if (target is null)
            {
                AppLogger.Warn("No active window was detected for a hotkey press.");
                _flyoutTarget = null;
                _flyout.ShowStatus("No active window", "No app detected", 0, muted: false, null, IntPtr.Zero);
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
                icon,
                target.WindowHandle);
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
            icon,
            target.WindowHandle);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
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
