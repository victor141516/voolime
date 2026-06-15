using System;
using System.Drawing;
using System.Diagnostics;
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
    private readonly Icon _appIcon;
    private readonly Forms.NotifyIcon _trayIcon;
    private readonly Forms.ToolStripMenuItem _shiftModifierItem;
    private readonly Forms.ToolStripMenuItem _controlModifierItem;
    private readonly Forms.ToolStripMenuItem _altModifierItem;
    private bool _disposed;

    public AppController(WpfApplication application)
    {
        _application = application;
        _appIcon = LoadAppIcon();
        _hotkeys = new HotkeyService(HandleHotkey, AppSettings.LoadActivationModifier());
        (_trayIcon, _shiftModifierItem, _controlModifierItem, _altModifierItem) = CreateTrayIcon();
        UpdateModifierChecks();
    }

    private (Forms.NotifyIcon TrayIcon, Forms.ToolStripMenuItem Shift, Forms.ToolStripMenuItem Control, Forms.ToolStripMenuItem Alt) CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        var activationKey = new Forms.ToolStripMenuItem("Activation key");
        var shift = CreateModifierItem("Shift", ActivationModifier.Shift);
        var control = CreateModifierItem("Control", ActivationModifier.Control);
        var alt = CreateModifierItem("Alt", ActivationModifier.Alt);
        activationKey.DropDownItems.AddRange(new Forms.ToolStripItem[] { shift, control, alt });

        menu.Items.Add(activationKey);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => _application.Shutdown());

        var trayIcon = new Forms.NotifyIcon
        {
            Icon = _appIcon,
            Text = $"Voolime - {GetModifierLabel(_hotkeys.Modifier)}+volume adjusts the active app",
            ContextMenuStrip = menu,
            Visible = true
        };

        return (trayIcon, shift, control, alt);
    }

    private Forms.ToolStripMenuItem CreateModifierItem(string text, ActivationModifier modifier) =>
        new(text, null, (_, _) => SetActivationModifier(modifier))
        {
            CheckOnClick = false
        };

    private void SetActivationModifier(ActivationModifier modifier)
    {
        _hotkeys.SetModifier(modifier);
        AppSettings.SaveActivationModifier(modifier);
        UpdateModifierChecks();
    }

    private void UpdateModifierChecks()
    {
        _shiftModifierItem.Checked = _hotkeys.Modifier == ActivationModifier.Shift;
        _controlModifierItem.Checked = _hotkeys.Modifier == ActivationModifier.Control;
        _altModifierItem.Checked = _hotkeys.Modifier == ActivationModifier.Alt;
        _trayIcon.Text = $"Voolime - {GetModifierLabel(_hotkeys.Modifier)}+volume adjusts the active app";
    }

    private static string GetModifierLabel(ActivationModifier modifier) =>
        modifier switch
        {
            ActivationModifier.Control => "Ctrl",
            ActivationModifier.Alt => "Alt",
            _ => "Shift"
        };

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
        _application.Dispatcher.Invoke(() =>
        {
            var target = _windowResolver.GetActiveTarget();
            if (target is null)
            {
                _flyout.ShowStatus("No active window", "No app detected", 0, muted: false, null, IntPtr.Zero);
                return;
            }

            VolumeChangeResult result;
            try
            {
                result = _audio.Apply(target, press.Kind, press.IsHeldRepeat);
            }
            catch (Exception ex)
            {
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
        });
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
}
