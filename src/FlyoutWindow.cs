using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;
using Microsoft.Win32;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfImage = System.Windows.Controls.Image;

namespace Voolime;

internal sealed class FlyoutWindow : Window
{
    private const double FlyoutWidth = 304;
    private const double FlyoutHeight = 58;
    private const double BottomMargin = 34;
    private static readonly TimeSpan EntryAnimationDuration = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan IdleVisibleDuration = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan ExitAnimationDuration = TimeSpan.FromMilliseconds(500);

    private readonly WpfImage _appIcon;
    private readonly TextBlock _valueText;
    private readonly Border _root;
    private readonly FrameworkElement _meter;
    private readonly Border _trackBar;
    private readonly ColumnDefinition _fillColumn;
    private readonly ColumnDefinition _emptyColumn;
    private readonly Border _fillBar;
    private readonly DispatcherTimer _hideTimer;
    private IntPtr _activeMonitor;
    private string? _indicatorMonitorDeviceName;
    private double _shownLeft;
    private double _shownTop;
    private double _hiddenTop;
    private int _shownLeftPx;
    private int _shownTopPx;
    private int _hiddenTopPx;
    private int _windowWidthPx;
    private int _windowHeightPx;
    private int _animationVersion;
    private FlyoutAnimationState _animationState = FlyoutAnimationState.Hidden;
    private bool _monitorPositionDirty = true;
    private bool _isDraggingVolume;

    public event Action<double>? VolumeRequested;

    public FlyoutWindow()
    {
        Width = FlyoutWidth;
        Height = FlyoutHeight;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = MediaBrushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;

        var (content, icon, valueText, root, meter, trackBar, fill, empty, fillBar) = BuildContent();
        Content = content;
        _appIcon = icon;
        _valueText = valueText;
        _root = root;
        _meter = meter;
        _trackBar = trackBar;
        _fillColumn = fill;
        _emptyColumn = empty;
        _fillBar = fillBar;

        _hideTimer = new DispatcherTimer { Interval = IdleVisibleDuration };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            SlideOut(++_animationVersion);
        };

        _meter.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _isDraggingVolume = true;
            _meter.CaptureMouse();
            RequestVolumeFromPointer(e.GetPosition(_meter));
            e.Handled = true;
        };
        _meter.PreviewMouseMove += (_, e) =>
        {
            if (_isDraggingVolume && e.LeftButton == MouseButtonState.Pressed)
            {
                RequestVolumeFromPointer(e.GetPosition(_meter));
                e.Handled = true;
            }
        };
        _meter.PreviewMouseLeftButtonUp += (_, e) => StopVolumeDrag(e);
        _meter.LostMouseCapture += (_, _) => _isDraggingVolume = false;
    }

    public void SetIndicatorMonitorDeviceName(string? deviceName)
    {
        _indicatorMonitorDeviceName = string.IsNullOrWhiteSpace(deviceName) ? null : deviceName;
        _monitorPositionDirty = true;
        AppLogger.Info($"Indicator monitor selection changed: {FormatSelection(_indicatorMonitorDeviceName)}.");
        RefreshMonitorPosition();
    }

    public void ShowStatus(string appName, string message, double volume, bool muted, ImageSource? icon)
    {
        _appIcon.Source = icon ?? AppIconProvider.GetFallbackIcon();
        _valueText.Text = FormatValue(message, volume, muted);
        ApplyTheme(muted);

        var clamped = Math.Clamp(volume, 0, 1);
        _fillColumn.Width = new GridLength(Math.Max(clamped, 0.001), GridUnitType.Star);
        _emptyColumn.Width = new GridLength(Math.Max(1 - clamped, 0.001), GridUnitType.Star);

        if (!IsVisible || _monitorPositionDirty)
        {
            PositionOnSelectedMonitor();
        }

        _hideTimer.Stop();

        if (!IsVisible)
        {
            var animationVersion = ++_animationVersion;
            StopAnimations();
            Left = _shownLeft;
            Top = _hiddenTop;
            Opacity = 0;
            Show();
            EnsureOverlayZOrder("initial show");
            SlideIn(animationVersion);
        }
        else
        {
            EnsureOverlayZOrder("status update");
            if (_animationState == FlyoutAnimationState.Entering)
            {
                AppLogger.Info("Flyout updated during entry animation; keeping current animation.");
                return;
            }

            if (_animationState == FlyoutAnimationState.Shown)
            {
                StartIdleTimer(++_animationVersion);
                return;
            }

            SlideToShownPosition(++_animationVersion);
        }
    }

    public void RefreshMonitorPosition()
    {
        _monitorPositionDirty = true;
        if (!IsVisible)
        {
            AppLogger.Info($"Indicator monitor refresh deferred because flyout is hidden. Selection: {FormatSelection(_indicatorMonitorDeviceName)}.");
            return;
        }

        StopAnimations();
        PositionOnSelectedMonitor();
        Left = _shownLeft;
        Top = _shownTop;
        Opacity = 1;
        EnsureOverlayZOrder("monitor refresh");
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(
            hwnd,
            NativeMethods.GWL_EXSTYLE,
            exStyle | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);

        var trueValue = 1;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref trueValue, sizeof(int));

        var cornerPreference = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));

        var backdrop = NativeMethods.DWMSBT_TRANSIENTWINDOW;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

        ApplyTheme(muted: false);
    }

    private void PositionOnSelectedMonitor()
    {
        var (screen, reason) = ResolveIndicatorScreen();
        var monitor = GetMonitorForScreen(screen);
        if (monitor == IntPtr.Zero)
        {
            monitor = NativeMethods.MonitorFromWindow(new WindowInteropHelper(this).Handle, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        }
        _activeMonitor = monitor;

        var info = NativeMethods.MONITORINFO.Create();
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
        {
            AppLogger.Warn($"Could not read monitor info for {screen.DeviceName}; falling back to primary monitor.");
            monitor = GetPrimaryMonitor();
            _activeMonitor = monitor;
            info = NativeMethods.MONITORINFO.Create();
            NativeMethods.GetMonitorInfo(monitor, ref info);
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        var dpi = NativeMethods.GetEffectiveDpi(monitor, hwnd);
        var scale = dpi / 96.0;
        _windowWidthPx = (int)Math.Round(FlyoutWidth * scale);
        _windowHeightPx = (int)Math.Round(FlyoutHeight * scale);
        var marginPx = (int)Math.Round(BottomMargin * scale);

        _shownLeftPx = info.rcWork.Left + (info.rcWork.Width - _windowWidthPx) / 2;
        _shownTopPx = info.rcWork.Bottom - marginPx - _windowHeightPx;
        _hiddenTopPx = Math.Max(_shownTopPx, info.rcMonitor.Bottom - _windowHeightPx);
        _shownLeft = _shownLeftPx / scale;
        _shownTop = _shownTopPx / scale;
        _hiddenTop = _hiddenTopPx / scale;

        Width = FlyoutWidth;
        Height = FlyoutHeight;
        _monitorPositionDirty = false;

        AppLogger.Info(
            "Flyout monitor resolved: " +
            $"selection={FormatSelection(_indicatorMonitorDeviceName)}, reason={reason}, " +
            $"screen={screen.DeviceName}, primary={screen.Primary}, " +
            $"screenBounds={FormatRectangle(screen.Bounds)}, screenWorkingArea={FormatRectangle(screen.WorkingArea)}, " +
            $"monitor=0x{monitor.ToInt64():X}, rcMonitor={FormatRect(info.rcMonitor)}, rcWork={FormatRect(info.rcWork)}, " +
            $"dpi={dpi}, scale={scale:0.###}, " +
            $"windowPx={_windowWidthPx}x{_windowHeightPx}, shownPx=({_shownLeftPx},{_shownTopPx}), hiddenTopPx={_hiddenTopPx}.");
    }

    private static IntPtr GetPrimaryMonitor()
    {
        var screen = Forms.Screen.PrimaryScreen;
        if (screen is null)
        {
            return IntPtr.Zero;
        }

        var bounds = screen.Bounds;
        var point = new NativeMethods.POINT
        {
            X = bounds.Left + Math.Max(bounds.Width / 2, 0),
            Y = bounds.Top + Math.Max(bounds.Height / 2, 0)
        };

        return NativeMethods.MonitorFromPoint(point, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
    }

    private (Forms.Screen Screen, string Reason) ResolveIndicatorScreen()
    {
        var screens = Forms.Screen.AllScreens
            .OrderBy(screen => GetMonitorSortKey(screen.DeviceName))
            .ThenBy(screen => screen.DeviceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        AppLogger.Info($"Detected monitors: {string.Join("; ", screens.Select(DescribeScreen))}.");

        if (!string.IsNullOrWhiteSpace(_indicatorMonitorDeviceName))
        {
            var selected = screens.FirstOrDefault(screen =>
                string.Equals(screen.DeviceName, _indicatorMonitorDeviceName, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(GetDisplayName(screen.DeviceName), _indicatorMonitorDeviceName, StringComparison.OrdinalIgnoreCase));
            if (selected is not null)
            {
                return (selected, "configured monitor");
            }

            AppLogger.Warn($"Configured indicator monitor was not found: {_indicatorMonitorDeviceName}. Falling back to primary monitor.");
        }

        var primary = screens.FirstOrDefault(screen => screen.Primary) ?? Forms.Screen.PrimaryScreen;
        if (primary is not null)
        {
            return (primary, "primary monitor");
        }

        return (Forms.Screen.FromPoint(new Drawing.Point(0, 0)), "fallback monitor");
    }

    private static IntPtr GetMonitorForScreen(Forms.Screen screen)
    {
        var bounds = screen.Bounds;
        var point = new NativeMethods.POINT
        {
            X = bounds.Left + Math.Max(bounds.Width / 2, 0),
            Y = bounds.Top + Math.Max(bounds.Height / 2, 0)
        };

        return NativeMethods.MonitorFromPoint(point, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
    }

    private void EnsureOverlayZOrder(string reason)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            AppLogger.Warn($"Flyout z-order skipped because the window handle is not ready. Reason: {reason}.");
            return;
        }

        Topmost = true;
        var success = NativeMethods.TryMakeTopmostNoActivate(hwnd, out var error);
        AppLogger.Info($"Flyout z-order updated: reason={reason}, hwnd=0x{hwnd.ToInt64():X}, topmostNoActivate={success}, error={error}.");

        Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible)
            {
                var deferredSuccess = NativeMethods.TryMakeTopmostNoActivate(hwnd, out var deferredError);
                AppLogger.Info($"Flyout z-order deferred update: reason={reason}, hwnd=0x{hwnd.ToInt64():X}, topmostNoActivate={deferredSuccess}, error={deferredError}.");
            }
        }, DispatcherPriority.ApplicationIdle);
    }

    private void SlideIn(int animationVersion)
    {
        _animationState = FlyoutAnimationState.Entering;
        AnimateTo(_shownTop, 1, EntryAnimationDuration, EaseOutCubic(), animationVersion, "slide in", () => StartIdleTimer(animationVersion));
    }

    private void SlideToShownPosition(int animationVersion)
    {
        var duration = Math.Abs(Top - _shownTop) < 1 && Opacity >= 0.99
            ? TimeSpan.Zero
            : EntryAnimationDuration;
        _animationState = FlyoutAnimationState.Entering;
        AnimateTo(_shownTop, 1, duration, EaseOutCubic(), animationVersion, "slide to shown position", () => StartIdleTimer(animationVersion));
    }

    private void SlideOut(int animationVersion)
    {
        _animationState = FlyoutAnimationState.Exiting;
        AnimateTo(_hiddenTop, 0, ExitAnimationDuration, EaseInCubic(), animationVersion, "slide out", () =>
        {
            if (animationVersion == _animationVersion)
            {
                _animationState = FlyoutAnimationState.Hidden;
                Hide();
            }
        });
    }

    private void AnimateTo(
        double top,
        double opacity,
        TimeSpan duration,
        IEasingFunction easing,
        int animationVersion,
        string reason,
        Action completed)
    {
        StopAnimations();
        AppLogger.Info($"Flyout animation started: {reason}, fromTop={Top:0.###}, toTop={top:0.###}, fromOpacity={Opacity:0.###}, toOpacity={opacity:0.###}, durationMs={duration.TotalMilliseconds:0}.");

        if (duration <= TimeSpan.Zero)
        {
            Top = top;
            Opacity = opacity;
            if (opacity >= 0.99)
            {
                _animationState = FlyoutAnimationState.Shown;
            }

            completed();
            return;
        }

        var topAnimation = new DoubleAnimation(top, duration)
        {
            EasingFunction = easing
        };
        topAnimation.Completed += (_, _) =>
        {
            if (animationVersion != _animationVersion)
            {
                return;
            }

            Top = top;
            Opacity = opacity;
            AppLogger.Info($"Flyout animation completed: {reason}, top={Top:0.###}, opacity={Opacity:0.###}.");
            EnsureOverlayZOrder($"{reason} completed");
            if (opacity >= 0.99)
            {
                _animationState = FlyoutAnimationState.Shown;
            }

            completed();
        };

        var opacityAnimation = new DoubleAnimation(opacity, duration)
        {
            EasingFunction = easing
        };

        BeginAnimation(TopProperty, topAnimation, HandoffBehavior.SnapshotAndReplace);
        BeginAnimation(OpacityProperty, opacityAnimation, HandoffBehavior.SnapshotAndReplace);
    }

    private void StopAnimations()
    {
        var currentTop = Top;
        var currentOpacity = Opacity;
        BeginAnimation(TopProperty, null);
        BeginAnimation(OpacityProperty, null);
        Top = currentTop;
        Opacity = currentOpacity;
    }

    private static IEasingFunction EaseOutCubic() =>
        new CubicEase { EasingMode = EasingMode.EaseOut };

    private static IEasingFunction EaseInCubic() =>
        new CubicEase { EasingMode = EasingMode.EaseIn };

    private void StartIdleTimer(int animationVersion)
    {
        if (animationVersion != _animationVersion || !IsVisible)
        {
            return;
        }

        _hideTimer.Stop();
        _hideTimer.Interval = IdleVisibleDuration;
        _hideTimer.Start();
    }

    private void StopVolumeDrag(MouseButtonEventArgs e)
    {
        if (!_isDraggingVolume)
        {
            return;
        }

        _isDraggingVolume = false;
        _meter.ReleaseMouseCapture();
        RequestVolumeFromPointer(e.GetPosition(_meter));
        e.Handled = true;
    }

    private void RequestVolumeFromPointer(System.Windows.Point position)
    {
        var width = _meter.ActualWidth;
        if (width <= 0)
        {
            return;
        }

        _hideTimer.Stop();
        _hideTimer.Interval = IdleVisibleDuration;
        _hideTimer.Start();
        VolumeRequested?.Invoke(Math.Clamp(position.X / width, 0, 1));
    }

    private static (FrameworkElement Content, WpfImage Icon, TextBlock ValueText, Border Root, FrameworkElement Meter, Border TrackBar, ColumnDefinition Fill, ColumnDefinition Empty, Border FillBar) BuildContent()
    {
        var fill = new ColumnDefinition { Width = new GridLength(0.5, GridUnitType.Star) };
        var empty = new ColumnDefinition { Width = new GridLength(0.5, GridUnitType.Star) };
        var fillBar = new Border
        {
            Height = 5,
            Background = BrushFrom("#8FBFE8"),
            CornerRadius = new CornerRadius(2.5)
        };

        var meter = new Grid
        {
            Height = 5,
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds = true
        };
        meter.ColumnDefinitions.Add(fill);
        meter.ColumnDefinitions.Add(empty);
        var trackBar = new Border
        {
            Background = BrushFrom("#35414D"),
            CornerRadius = new CornerRadius(2.5)
        };
        meter.Children.Add(trackBar);
        Grid.SetColumnSpan(trackBar, 2);
        meter.Children.Add(fillBar);
        var meterHitTarget = new Grid
        {
            Height = 22,
            VerticalAlignment = VerticalAlignment.Center,
            Background = MediaBrushes.Transparent,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        meterHitTarget.Children.Add(meter);

        var icon = new WpfImage
        {
            Width = 24,
            Height = 24,
            VerticalAlignment = VerticalAlignment.Center
        };

        var valueText = new TextBlock
        {
            Foreground = BrushFrom("#D2DCE6"),
            FontFamily = new WpfFontFamily("Segoe UI Variable Text, Segoe UI"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Right,
            MinWidth = 42,
            VerticalAlignment = VerticalAlignment.Center
        };

        var body = new Grid { VerticalAlignment = VerticalAlignment.Center };
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        body.Children.Add(icon);
        Grid.SetColumn(meterHitTarget, 1);
        meterHitTarget.Margin = new Thickness(14, 0, 12, 0);
        body.Children.Add(meterHitTarget);
        Grid.SetColumn(valueText, 2);
        body.Children.Add(valueText);

        var root = new Border
        {
            Width = FlyoutWidth,
            Height = FlyoutHeight,
            Background = BrushFrom("#EE202832"),
            BorderBrush = MediaBrushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(11),
            ClipToBounds = true,
            Padding = new Thickness(16, 0, 16, 0),
            SnapsToDevicePixels = true,
            Child = body
        };

        return (root, icon, valueText, root, meterHitTarget, trackBar, fill, empty, fillBar);
    }

    private void ApplyTheme(bool muted)
    {
        var theme = FlyoutTheme.Read();
        var acrylicEnabled = TryApplyAcrylic(theme);
        _root.Background = BrushFrom(acrylicEnabled ? WithAlpha(theme.Background, AcrylicAlpha(theme)) : theme.Background);
        _root.BorderBrush = BrushFrom(theme.Border);
        _valueText.Foreground = BrushFrom(theme.Text);
        _trackBar.Background = BrushFrom(theme.Track);
        _fillBar.Background = BrushFrom(muted ? theme.MutedFill : theme.Accent);

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            var darkMode = theme.IsLight ? 0 : 1;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));
        }
    }

    private bool TryApplyAcrylic(FlyoutTheme theme)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        var background = (MediaColor)MediaColorConverter.ConvertFromString(theme.Background);
        return NativeMethods.TryEnableAcrylicBlur(hwnd, AcrylicAlpha(theme), background.R, background.G, background.B);
    }

    private static byte AcrylicAlpha(FlyoutTheme theme) =>
        theme.IsLight ? (byte)0xEA : (byte)0xDE;

    private static string WithAlpha(string color, byte alpha)
    {
        var parsed = (MediaColor)MediaColorConverter.ConvertFromString(color);
        return $"#{alpha:X2}{parsed.R:X2}{parsed.G:X2}{parsed.B:X2}";
    }

    private static string FormatValue(string message, double volume, bool muted)
    {
        if (message.EndsWith("%", StringComparison.Ordinal))
        {
            return message;
        }

        if (muted)
        {
            return "0%";
        }

        return volume > 0 ? $"{Math.Round(volume * 100)}%" : "--";
    }

    private static SolidColorBrush BrushFrom(string color)
    {
        var brush = new SolidColorBrush((MediaColor)MediaColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private static string DescribeScreen(Forms.Screen screen) =>
        $"{screen.DeviceName} primary={screen.Primary} bounds={FormatRectangle(screen.Bounds)} work={FormatRectangle(screen.WorkingArea)}";

    private static string FormatSelection(string? deviceName) =>
        string.IsNullOrWhiteSpace(deviceName) ? "primary" : deviceName;

    private static string FormatRect(NativeMethods.RECT rect) =>
        $"({rect.Left},{rect.Top},{rect.Width},{rect.Height})";

    private static string FormatRectangle(Drawing.Rectangle rectangle) =>
        $"({rectangle.Left},{rectangle.Top},{rectangle.Width},{rectangle.Height})";

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

}

internal sealed record FlyoutTheme(
    bool IsLight,
    string Background,
    string Border,
    string Text,
    string Track,
    string MutedFill,
    string Accent)
{
    public static FlyoutTheme Read()
    {
        var isLight = IsLightAppTheme();
        return new FlyoutTheme(
            isLight,
            isLight ? "#FFF8F9FA" : "#FF202832",
            "#00000000",
            isLight ? "#1F2933" : "#D2DCE6",
            isLight ? "#DDE5EC" : "#35414D",
            isLight ? "#8C98A4" : "#7C8792",
            GetAccentColor());
    }

    private static bool IsLightAppTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
    }

    private static string GetAccentColor()
    {
        if (NativeMethods.DwmGetColorizationColor(out var color, out _) == 0)
        {
            var red = (byte)((color >> 16) & 0xFF);
            var green = (byte)((color >> 8) & 0xFF);
            var blue = (byte)(color & 0xFF);
            return $"#{red:X2}{green:X2}{blue:X2}";
        }

        return "#0078D4";
    }
}

internal enum FlyoutAnimationState
{
    Hidden,
    Entering,
    Shown,
    Exiting
}

internal static class AppIconProvider
{
    private static ImageSource? _fallback;

    public static ImageSource? GetIcon(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            return GetFallbackIcon();
        }

        try
        {
            using var icon = Drawing.Icon.ExtractAssociatedIcon(path);
            if (icon is null)
            {
                return GetFallbackIcon();
            }

            var image = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(24, 24));
            image.Freeze();
            return image;
        }
        catch
        {
            return GetFallbackIcon();
        }
    }

    public static ImageSource? GetFallbackIcon()
    {
        if (_fallback is not null)
        {
            return _fallback;
        }

        var icon = SystemIcons.Application;
        var image = Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromWidthAndHeight(24, 24));
        image.Freeze();
        _fallback = image;
        return _fallback;
    }
}
