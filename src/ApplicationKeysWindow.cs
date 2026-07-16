using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WpfButton = System.Windows.Controls.Button;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfImage = System.Windows.Controls.Image;
using WpfKeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace Voolime;

internal sealed class ApplicationKeysWindow : Window
{
    private readonly ApplicationKeyService _service;
    private readonly StackPanel _rows = new();
    private readonly TextBlock _status = new();
    private string? _capturingAppId;

    public ApplicationKeysWindow(ApplicationKeyService service)
    {
        _service = service;
        Title = "Application Keys";
        Width = 560;
        Height = 480;
        MinWidth = 480;
        MinHeight = 320;
        ResizeMode = ResizeMode.CanResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = AppIconProvider.GetFallbackIcon();
        Content = BuildContent();

        PreviewKeyDown += HandlePreviewKeyDown;
        Closed += HandleClosed;
        _service.AssignmentsChanged += HandleAssignmentsChanged;
        RefreshApplications();
    }

    private UIElement BuildContent()
    {
        var theme = FlyoutTheme.Read();
        Background = Brush(theme.Background);
        Foreground = Brush(theme.Text);

        var root = new Grid { Margin = new Thickness(20, 18, 20, 16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new Grid();
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock
        {
            Text = "Application Keys",
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });

        var refresh = new WpfButton
        {
            Content = "Refresh",
            Padding = new Thickness(14, 6, 14, 6),
            MinWidth = 80
        };
        refresh.Click += (_, _) => RefreshApplications();
        Grid.SetColumn(refresh, 1);
        heading.Children.Add(refresh);
        root.Children.Add(heading);

        var description = new TextBlock
        {
            Text = "Hold an enabled key together with your configured keyboard or mouse activation keys to control that application.",
            Margin = new Thickness(0, 8, 0, 14),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.76
        };
        Grid.SetRow(description, 1);
        root.Children.Add(description);

        var scroll = new ScrollViewer
        {
            Content = _rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 0, 8, 0)
        };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);

        var footer = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _status.Text = "Changes are saved automatically.";
        _status.VerticalAlignment = VerticalAlignment.Center;
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Opacity = 0.72;
        footer.Children.Add(_status);

        var close = new WpfButton
        {
            Content = "Close",
            IsDefault = true,
            MinWidth = 86,
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(16, 0, 0, 0)
        };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        footer.Children.Add(close);
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        return root;
    }

    private void RefreshApplications()
    {
        CancelCapture();
        _service.Refresh();
        RenderRows();
        _status.Text = "Changes are saved automatically.";
    }

    private void RenderRows()
    {
        _rows.Children.Clear();
        var entries = _service.Entries;
        if (entries.Count == 0)
        {
            _rows.Children.Add(new TextBlock
            {
                Text = "No open or recently audible applications were found.",
                Margin = new Thickness(4, 18, 4, 18),
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.72
            });
            return;
        }

        foreach (var entry in entries)
        {
            _rows.Children.Add(CreateRow(entry));
        }
    }

    private UIElement CreateRow(ApplicationKeySetting entry)
    {
        var row = new Grid
        {
            Margin = new Thickness(0, 0, 0, 6),
            Background = Brush(FlyoutTheme.Read().IsLight ? "#0B000000" : "#18FFFFFF")
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(102) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(38) });

        var enabled = new WpfCheckBox
        {
            IsChecked = entry.Enabled,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            ToolTip = entry.VirtualKey.HasValue ? "Enable this application key" : "Choose a key before enabling"
        };
        enabled.Click += (_, _) =>
        {
            var requested = enabled.IsChecked == true;
            if (!_service.TrySetEnabled(entry.AppId, requested, out var error))
            {
                enabled.IsChecked = entry.Enabled;
                _status.Text = error;
            }
        };
        row.Children.Add(enabled);

        var icon = new WpfImage
        {
            Source = AppIconProvider.GetIcon(entry.ProcessPath),
            Width = 24,
            Height = 24,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        Grid.SetColumn(icon, 1);
        row.Children.Add(icon);

        var name = new TextBlock
        {
            Text = entry.DisplayName,
            Margin = new Thickness(6, 11, 10, 11),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = entry.DisplayName
        };
        Grid.SetColumn(name, 2);
        row.Children.Add(name);

        var key = new WpfButton
        {
            Content = entry.VirtualKey.HasValue
                ? ApplicationKeyValidation.GetDisplayName(entry.VirtualKey.Value)
                : "Choose key",
            Margin = new Thickness(3, 7, 3, 7),
            Padding = new Thickness(8, 4, 8, 4),
            Tag = entry.AppId
        };
        key.Click += (_, _) => BeginCapture(entry.AppId, key);
        Grid.SetColumn(key, 3);
        row.Children.Add(key);

        var clear = new WpfButton
        {
            Content = "×",
            FontSize = 17,
            Margin = new Thickness(3, 7, 3, 7),
            Padding = new Thickness(0),
            ToolTip = "Clear key",
            IsEnabled = entry.VirtualKey.HasValue
        };
        clear.Click += (_, _) =>
        {
            if (_service.TrySetKey(entry.AppId, null, out var error))
            {
                _status.Text = $"Cleared the key for {entry.DisplayName}.";
            }
            else
            {
                _status.Text = error;
            }
        };
        Grid.SetColumn(clear, 4);
        row.Children.Add(clear);

        return row;
    }

    private void BeginCapture(string appId, WpfButton button)
    {
        CancelCapture();
        _capturingAppId = appId;
        button.Content = "Press a key…";
        button.Focus();
        _status.Text = "Press a letter, number, or punctuation key. Press Esc to cancel.";
    }

    private void HandlePreviewKeyDown(object sender, WpfKeyEventArgs e)
    {
        if (_capturingAppId is null)
        {
            return;
        }

        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            CancelCapture();
            _status.Text = "Key selection canceled.";
            RenderRows();
            return;
        }

        var virtualKey = KeyInterop.VirtualKeyFromKey(key);
        if (!ApplicationKeyValidation.IsAllowed(virtualKey))
        {
            _status.Text = "Use a letter, number, or punctuation key. Modifier, function, navigation, and editing keys are not allowed.";
            return;
        }

        var appId = _capturingAppId;
        CancelCapture();
        if (_service.TrySetKey(appId, virtualKey, out var error))
        {
            _status.Text = $"Assigned {ApplicationKeyValidation.GetDisplayName(virtualKey)}.";
        }
        else
        {
            _status.Text = error;
            RenderRows();
        }
    }

    private void CancelCapture()
    {
        _capturingAppId = null;
    }

    private void HandleAssignmentsChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RenderRows);
            return;
        }

        RenderRows();
    }

    private void HandleClosed(object? sender, EventArgs e)
    {
        _service.AssignmentsChanged -= HandleAssignmentsChanged;
        PreviewKeyDown -= HandlePreviewKeyDown;
    }

    private static SolidColorBrush Brush(string color) =>
        new((WpfColor)WpfColorConverter.ConvertFromString(color));
}
