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
    private bool _disposed;

    public AppController(WpfApplication application)
    {
        _application = application;
        _appIcon = LoadAppIcon();
        _hotkeys = new HotkeyService(HandleHotkey);
        _trayIcon = CreateTrayIcon();
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Salir", null, (_, _) => _application.Shutdown());

        return new Forms.NotifyIcon
        {
            Icon = _appIcon,
            Text = "Voolime - Shift+volumen ajusta la app activa",
            ContextMenuStrip = menu,
            Visible = true
        };
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

    private void HandleHotkey(VolumeHotkeyKind kind)
    {
        _application.Dispatcher.Invoke(() =>
        {
            var target = _windowResolver.GetActiveTarget();
            if (target is null)
            {
                _flyout.ShowStatus("Sin ventana activa", "No se pudo detectar una app", 0, muted: false, null, IntPtr.Zero);
                return;
            }

            VolumeChangeResult result;
            try
            {
                result = _audio.Apply(target, kind);
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
