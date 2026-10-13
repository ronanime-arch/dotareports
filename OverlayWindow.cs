using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace DotaReports;

/// <summary>
/// Всплывающее окно поверх игры: клики проходят насквозь, фокус не забирает, само исчезает.
/// Отдельное окно — в процесс Dota ничего не внедряется.
/// </summary>
sealed class OverlayWindow : Window
{
    public static readonly Color White = Color.FromRgb(0xF2, 0xF2, 0xF2);
    public static readonly Color Gray = Color.FromRgb(0x9A, 0xA4, 0xB0);
    public static readonly Color Red = Color.FromRgb(0xFF, 0x6B, 0x57);
    public static readonly Color Green = Color.FromRgb(0x7C, 0xD9, 0x92);

    static readonly Color DefaultAccent = Color.FromRgb(0xB0, 0x40, 0x30);

    readonly StackPanel _panel = new();
    readonly Border _border;
    readonly bool _shadow;
    readonly DispatcherTimer _autoHide = new();
    readonly DispatcherTimer _keepOnTop = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly double _top;
    IntPtr _hwnd;

    /// <param name="top">Отступ от верха экрана — чтобы два таких окна не перекрывали друг друга.</param>
    /// <param name="backgroundAlpha">Непрозрачность фона: меньше 255 — игра просвечивает, текст тогда с тенью.</param>
    public OverlayWindow(double top = 120, byte backgroundAlpha = 0xFF)
    {
        _top = top;
        _shadow = backgroundAlpha < 0xFF;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 380;
        _border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(backgroundAlpha, 0x14, 0x18, 0x21)),
            BorderBrush = new SolidColorBrush(DefaultAccent),
            BorderThickness = new Thickness(0, 0, 0, 3),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16, 12, 16, 12),
            Child = _panel,
        };
        Content = _border;

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE,
                ex | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED);
        };
        SizeChanged += (_, _) => PlaceTopRight();
        _autoHide.Tick += (_, _) => HidePopup();
        _keepOnTop.Tick += (_, _) => KeepOnTop();
    }

    /// <summary>Первая строка — заголовок, остальные — текст. Окно само скроется через duration. accent — цвет полосы снизу.</summary>
    public void ShowPopup(string title, Color titleColor, IEnumerable<(string Text, Color Color)> lines, TimeSpan duration, Color? accent = null)
    {
        _border.BorderBrush = new SolidColorBrush(accent ?? DefaultAccent);
        _panel.Children.Clear();
        _panel.Children.Add(Line(title, 17, titleColor, bold: true));
        foreach (var (text, color) in lines)
            _panel.Children.Add(Line(text, 14, color, top: 4));

        if (!IsVisible) Show();
        PlaceTopRight();
        KeepOnTop();
        _keepOnTop.Start();
        _autoHide.Stop();
        _autoHide.Interval = duration;
        _autoHide.Start();
    }

    public void HidePopup()
    {
        _autoHide.Stop();
        _keepOnTop.Stop();
        Hide();
    }

    void PlaceTopRight()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - ActualWidth - 24;
        Top = wa.Top + _top;
    }

    // Игра в режиме «окно без рамки» может подняться выше — возвращаем окно наверх.
    void KeepOnTop()
    {
        if (_hwnd != IntPtr.Zero)
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    TextBlock Line(string text, double size, Color color, bool bold = false, double top = 0) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = size,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = new SolidColorBrush(color),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, top, 0, 0),
        // на полупрозрачном фоне светлая игра под текстом — тень держит его читаемым
        Effect = _shadow ? new DropShadowEffect { Color = Colors.Black, ShadowDepth = 1, BlurRadius = 4, Opacity = 0.9 } : null,
    };
}
