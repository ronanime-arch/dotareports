using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace DotaReports;

/// <summary>Окно рейтинга слева сверху по Ctrl+Alt+S: висит, пока не нажмёте ещё раз. Клики проходят насквозь.</summary>
sealed class RatingWindow : Window
{
    readonly StackPanel _panel = new();

    public RatingWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 300;
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x14, 0x18, 0x21)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10, 12, 10),
            Child = _panel,
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE,
                ex | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED);
        };
    }

    public void Render(string title, IEnumerable<(string Text, Color Color)> lines, string footer)
    {
        _panel.Children.Clear();
        _panel.Children.Add(Line(title, 16, OverlayWindow.White, bold: true));
        foreach (var (text, color) in lines)
            _panel.Children.Add(Line(text, 13, color, top: 4));
        _panel.Children.Add(Line(footer, 10, OverlayWindow.Gray, top: 8));
    }

    public void Toggle()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + 20;
        Top = wa.Top + 130;
        Show();
    }

    static TextBlock Line(string text, double size, Color color, bool bold = false, double top = 0) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = size,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = new SolidColorBrush(color),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, top, 0, 0),
    };
}
