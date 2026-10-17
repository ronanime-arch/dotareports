using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DotaReports;

/// <summary>Окно слева сверху по хоткею (рейтинг — Ctrl+Alt+S, лобби NEC — Ctrl+Alt+N): висит, пока не нажмёте ещё раз. Клики проходят насквозь.</summary>
sealed class RatingWindow : Window
{
    readonly StackPanel _panel = new();

    public RatingWindow(double width = 300)
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
        Width = width;
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

    /// <param name="trend">Баллы поведения по сводкам, старые первыми — мини-график перед подвалом (от двух точек).</param>
    public void Render(string title, IEnumerable<(string Text, Color Color)> lines, string footer, IReadOnlyList<int>? trend = null)
    {
        _panel.Children.Clear();
        _panel.Children.Add(Line(title, 16, OverlayWindow.White, bold: true));
        foreach (var (text, color) in lines)
            _panel.Children.Add(Line(text, 13, color, top: 4));
        if (trend is { Count: >= 2 }) _panel.Children.Add(Chart(trend));
        _panel.Children.Add(Line(footer, 10, OverlayWindow.Gray, top: 8));
    }

    // Ломаная по баллам на всю ширину окна (300 − отступы 12+12); ровный ряд — посередине.
    static Canvas Chart(IReadOnlyList<int> values)
    {
        const double W = 276, H = 48, Pad = 3;
        int min = values.Min(), max = values.Max();
        double range = Math.Max(max - min, 1);
        var points = new PointCollection();
        for (int i = 0; i < values.Count; i++)
        {
            double y = max == min ? H / 2 : Pad + (max - values[i]) / range * (H - 2 * Pad);
            points.Add(new Point(i * W / (values.Count - 1), y));
        }
        var color = values[values.Count - 1] >= values[0] ? OverlayWindow.Green : OverlayWindow.Red;
        var canvas = new Canvas { Width = W, Height = H, Margin = new Thickness(0, 6, 0, 0) };
        canvas.Children.Add(new Polyline { Points = points, Stroke = new SolidColorBrush(color), StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round });
        return canvas;
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
