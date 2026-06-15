using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Interop;
using Drawing = System.Drawing;
using Microsoft.Win32;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfImage = System.Windows.Controls.Image;

namespace Voolime;

internal sealed class FlyoutWindow : Window
{
    private readonly WpfImage _appIcon;
    private readonly TextBlock _valueText;
    private readonly Border _root;
    private readonly Border _trackBar;
    private readonly ColumnDefinition _fillColumn;
    private readonly ColumnDefinition _emptyColumn;
    private readonly Border _fillBar;
    private readonly DispatcherTimer _hideTimer;

    public FlyoutWindow()
    {
        Width = 304;
        Height = 58;
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

        var (content, icon, valueText, root, trackBar, fill, empty, fillBar) = BuildContent();
        Content = content;
        _appIcon = icon;
        _valueText = valueText;
        _root = root;
        _trackBar = trackBar;
        _fillColumn = fill;
        _emptyColumn = empty;
        _fillBar = fillBar;

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1250) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            FadeOut();
        };
    }

    public void ShowStatus(string appName, string message, double volume, bool muted, ImageSource? icon, IntPtr activeWindow)
    {
        _appIcon.Source = icon ?? AppIconProvider.GetFallbackIcon();
        _valueText.Text = FormatValue(message, volume, muted);
        ApplyTheme(muted);

        var clamped = Math.Clamp(volume, 0, 1);
        _fillColumn.Width = new GridLength(Math.Max(clamped, 0.001), GridUnitType.Star);
        _emptyColumn.Width = new GridLength(Math.Max(1 - clamped, 0.001), GridUnitType.Star);

        PositionOnActiveMonitor(activeWindow);

        if (!IsVisible)
        {
            Opacity = 0;
            Show();
        }

        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(80))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });

        _hideTimer.Stop();
        _hideTimer.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(
            hwnd,
            NativeMethods.GWL_EXSTYLE,
            exStyle | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TRANSPARENT);

        var trueValue = 1;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref trueValue, sizeof(int));

        var cornerPreference = NativeMethods.DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, ref cornerPreference, sizeof(int));

        var backdrop = NativeMethods.DWMSBT_TRANSIENTWINDOW;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

        ApplyTheme(muted: false);
    }

    private void PositionOnActiveMonitor(IntPtr activeWindow)
    {
        var monitor = NativeMethods.MonitorFromWindow(activeWindow, NativeMethods.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero)
        {
            monitor = NativeMethods.MonitorFromWindow(new WindowInteropHelper(this).Handle, NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        }

        var info = NativeMethods.MONITORINFO.Create();
        NativeMethods.GetMonitorInfo(monitor, ref info);

        var hwnd = new WindowInteropHelper(this).Handle;
        var dpi = hwnd == IntPtr.Zero ? 96u : NativeMethods.GetDpiForWindow(hwnd);
        var scale = dpi / 96.0;
        var widthPx = Width * scale;
        var heightPx = Height * scale;
        const double marginPx = 28;

        Left = (info.rcWork.Left + (info.rcWork.Width - widthPx) / 2) / scale;
        Top = (info.rcWork.Bottom - marginPx - heightPx) / scale;
    }

    private void FadeOut()
    {
        var animation = new DoubleAnimation(0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        animation.Completed += (_, _) =>
        {
            if (Opacity <= 0.01)
            {
                Hide();
            }
        };
        BeginAnimation(OpacityProperty, animation);
    }

    private static (FrameworkElement Content, WpfImage Icon, TextBlock ValueText, Border Root, Border TrackBar, ColumnDefinition Fill, ColumnDefinition Empty, Border FillBar) BuildContent()
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
        Grid.SetColumn(meter, 1);
        meter.Margin = new Thickness(14, 0, 12, 0);
        body.Children.Add(meter);
        Grid.SetColumn(valueText, 2);
        body.Children.Add(valueText);

        var root = new Border
        {
            Background = BrushFrom("#EE202832"),
            BorderBrush = MediaBrushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(11),
            ClipToBounds = true,
            Padding = new Thickness(16, 0, 16, 0),
            SnapsToDevicePixels = true,
            Child = body
        };

        return (root, icon, valueText, root, trackBar, fill, empty, fillBar);
    }

    private void ApplyTheme(bool muted)
    {
        var theme = FlyoutTheme.Read();
        _root.Background = BrushFrom(theme.Background);
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
