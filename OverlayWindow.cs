using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace DotaReports;

/// <summary>
/// Всплывающее окно поверх игры: клики проходят насквозь (кроме ↻ и ✕), фокус не забирает, само исчезает.
/// Отдельное окно — в процесс Dota ничего не внедряется. Вид — «стекло» из макета: карточка с шапкой
/// или «таблетка» для напоминаний.
/// </summary>
sealed class OverlayWindow : GlassWindow
{
    // цвета текста — палитра макета «Древний + стекло» (Glass); имена прежние, ими пользуются все окна
    public static readonly Color White = Glass.Ink;
    public static readonly Color Gray = Glass.Muted;
    public static readonly Color Red = Glass.Bad;
    public static readonly Color Green = Glass.Good;

    readonly bool _shadow;
    readonly bool _pill;
    readonly byte _alpha;
    readonly DispatcherTimer _autoHide = new();
    readonly DispatcherTimer _keepOnTop = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly double _top;

    /// <param name="top">Отступ от верха экрана — чтобы два таких окна не перекрывали друг друга.</param>
    /// <param name="backgroundAlpha">Непрозрачность фона: меньше 255 — игра просвечивает, текст тогда с тенью.</param>
    /// <param name="pill">«Таблетка» напоминания: кружок цвета напоминания, заголовок и строка под ним.</param>
    public OverlayWindow(double top = 120, byte backgroundAlpha = 0xE6, bool pill = false)
    {
        _top = top;
        _alpha = backgroundAlpha;
        _shadow = backgroundAlpha < 0xE0;
        _pill = pill;
        SizeToContent = SizeToContent.Height;
        Width = 380;
        SizeChanged += (_, _) => PlaceTopRight();
        _autoHide.Tick += (_, _) => HidePopup();
        _keepOnTop.Tick += (_, _) => KeepOnTop();
        CloseButtonPressed = () =>
        {
            _autoHide.Stop();
            _keepOnTop.Stop();
        };
    }

    /// <summary>
    /// Первая строка — заголовок, остальные — текст. Окно само скроется через duration (ноль и меньше — пока не нажмут ✕).
    /// accent — цвет значка (у напоминания — кружка); refresh — что делает ↻ (нет — кнопки нет).
    /// </summary>
    public void ShowPopup(string title, Color titleColor, IEnumerable<(string Text, Color Color)> lines, TimeSpan duration,
        Color? accent = null, Action? refresh = null)
    {
        Build(title, titleColor, lines, accent, refresh);
        if (!IsVisible) Show();
        PlaceTopRight();
        KeepOnTop();
        _keepOnTop.Start();
        _autoHide.Stop();
        if (duration <= TimeSpan.Zero) return; // до ✕: таймер с нулём сработал бы сразу
        _autoHide.Interval = duration;
        _autoHide.Start();
    }

    /// <summary>Собрать содержимое, не показывая (ShowPopup и снимки --ui-test).</summary>
    public void Build(string title, Color titleColor, IEnumerable<(string Text, Color Color)> lines, Color? accent = null, Action? refresh = null)
    {
        ClearButtons();
        var color = accent ?? Glass.Gold;
        Content = _pill ? Pill(title, titleColor, lines, color) : Card(title, titleColor, lines, color, refresh);
    }

    public void HidePopup()
    {
        _autoHide.Stop();
        _keepOnTop.Stop();
        Hide();
    }

    // Карточка: шапка «DotaReports · время» со значком цвета акцента, заголовок антиквой, строки.
    UIElement Card(string title, Color titleColor, IEnumerable<(string Text, Color Color)> lines, Color accent, Action? refresh)
    {
        var body = new StackPanel();
        body.Children.Add(Glass.Header(this, $"DotaReports · {DateTime.Now:HH:mm}", 13, Glass.Muted, null, refresh, accent, serif: false));
        body.Children.Add(Shadowed(Glass.Text(title, 24, titleColor, serif: true, bold: true, top: 6)));
        foreach (var (text, color) in lines)
            body.Children.Add(Shadowed(Glass.Text(text, 14, color, top: 4)));
        return Glass.Panel(body, 16, new Thickness(14, 10, 12, 14), _alpha);
    }

    // «Таблетка» напоминания: кружок цвета напоминания, заголовок антиквой, под ним строки мелко, справа ✕.
    UIElement Pill(string title, Color titleColor, IEnumerable<(string Text, Color Color)> lines, Color accent)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var dot = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(20),
            Background = Glass.B(Glass.A(0x2E, accent)), BorderBrush = Glass.B(Glass.A(0x8C, accent)), BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "◆", FontSize = 15, Foreground = Glass.B(accent),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
        grid.Children.Add(dot);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Shadowed(Glass.Text(title, 21, titleColor, serif: true, bold: true)));
        bool first = true;
        foreach (var (line, color) in lines)
        {
            text.Children.Add(Shadowed(Glass.Text(line, first ? 13 : 11.5, first ? color : Glass.Muted, top: 1)));
            first = false;
        }
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        var close = Glass.IconButton("✕");
        AddButton(close, CloseByButton);
        close.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(close, 2);
        grid.Children.Add(close);
        return Glass.Panel(grid, 28, new Thickness(8, 8, 10, 8), _alpha);
    }

    // на полупрозрачном фоне светлая игра под текстом — тень держит его читаемым
    TextBlock Shadowed(TextBlock t)
    {
        if (_shadow) t.Effect = new DropShadowEffect { Color = Colors.Black, ShadowDepth = 1, BlurRadius = 4, Opacity = 0.9 };
        return t;
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
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }
}
