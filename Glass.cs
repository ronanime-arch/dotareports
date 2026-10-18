using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DotaReports;

/// <summary>
/// Оформление «Древний + стекло» (макет docs/design/ancient-glass.html): тёплое тёмное стекло, золото, мягкие зелёный
/// и терракота, заголовки антиквой. Размыть игру под слоистым окном WPF нельзя — стекло плотнее макета (≈ 0,9),
/// иначе текст на светлой карте не читается. Шрифты макета, если не установлены, заменяются Palatino / Georgia и Segoe UI.
/// </summary>
static class Glass
{
    public static readonly Color Ink = Color.FromRgb(0xEF, 0xE6, 0xD4);
    public static readonly Color Muted = Color.FromRgb(0xB9, 0xAC, 0x96);
    public static readonly Color Gold = Color.FromRgb(0xD2, 0xA1, 0x5A);
    public static readonly Color GoldLight = Color.FromRgb(0xEF, 0xC2, 0x7E);
    public static readonly Color Good = Color.FromRgb(0xC3, 0xE0, 0xA0);
    public static readonly Color Bad = Color.FromRgb(0xF0, 0xA0, 0x7A);
    public static readonly Color Dark = Color.FromRgb(0x1A, 0x14, 0x0E);

    public static readonly FontFamily Serif = new("Cormorant Garamond, Palatino Linotype, Georgia");
    public static readonly FontFamily Sans = new("Alegreya Sans, Segoe UI");
    static readonly FontFamily Symbols = new("Segoe UI Symbol, Segoe UI");

    public static Color A(byte alpha, Color c) => Color.FromArgb(alpha, c.R, c.G, c.B);
    public static SolidColorBrush B(Color c) => new(c);
    public static SolidColorBrush B(byte alpha, byte r, byte g, byte b) => new(Color.FromArgb(alpha, r, g, b));

    /// <summary>Стеклянная панель: фон, золотистая рамка 0,18 и светлая линия по верху.</summary>
    public static Border Panel(UIElement child, double radius, Thickness padding, byte alpha = 0xE6) => new()
    {
        Background = B(alpha, 0x1A, 0x14, 0x0E),
        BorderBrush = B(0x2E, 0xE8, 0xC8, 0x96),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(radius),
        Child = new Border
        {
            BorderBrush = B(0x1A, 0xFF, 0xE6, 0xBE),
            BorderThickness = new Thickness(0, 1, 0, 0),
            CornerRadius = new CornerRadius(radius),
            Padding = padding,
            Child = child,
        },
    };

    public static TextBlock Text(string text, double size, Color color, bool serif = false, bool bold = false, double top = 0,
        bool italic = false, bool wrap = true) => new()
    {
        Text = text,
        FontFamily = serif ? Serif : Sans,
        FontSize = size,
        FontWeight = bold ? (serif ? FontWeights.Bold : FontWeights.SemiBold) : (serif ? FontWeights.SemiBold : FontWeights.Normal),
        FontStyle = italic ? FontStyles.Italic : FontStyles.Normal,
        Foreground = B(color),
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
        Margin = new Thickness(0, top, 0, 0),
    };

    /// <summary>Плитка — светлая подложка 0,06 внутри панели.</summary>
    public static Border Tile(UIElement child, double top = 0, Thickness? padding = null) => new()
    {
        Background = B(0x0F, 0xFF, 0xF0, 0xDC),
        CornerRadius = new CornerRadius(10),
        Padding = padding ?? new Thickness(10, 8, 10, 8),
        Margin = new Thickness(0, top, 0, 0),
        Child = child,
    };

    /// <summary>Чип-«таблетка»: мелкий текст на подложке (по умолчанию светлой, у хорошего — зелёной, у плохого — терракотовой).</summary>
    public static Border Chip(string text, Color fg, double size = 12)
    {
        var bg = fg == Good ? B(0x29, 0xB5, 0xD9, 0x8F) : fg == Bad ? B(0x2E, 0xE9, 0x8A, 0x5F)
            : fg == GoldLight || fg == Gold ? B(0x2E, 0xD2, 0xA1, 0x5A) : B(0x14, 0xFF, 0xF0, 0xDC);
        return new Border
        {
            Background = bg,
            CornerRadius = new CornerRadius(999),
            Padding = new Thickness(8, 2, 8, 3),
            Margin = new Thickness(0, 0, 5, 5),
            Child = Text(text, size, fg, wrap: false),
        };
    }

    public static WrapPanel Chips(IEnumerable<UIElement> chips, double top = 0)
    {
        var wrap = new WrapPanel { Margin = new Thickness(0, top, 0, 0) };
        foreach (var c in chips) wrap.Children.Add(c);
        return wrap;
    }

    /// <summary>Тир героя по винрейту, как в легенде макета: S от 53%, A 51–53, B 49–51, C ниже 49.</summary>
    public static string Tier(double winrate) => winrate >= 53 ? "S" : winrate >= 51 ? "A" : winrate >= 49 ? "B" : "C";

    /// <summary>Значок тира: квадрат со скруглением (round — кружок), буква антиквой.</summary>
    public static Border TierBadge(double winrate, double size, bool round = false)
    {
        string tier = Tier(winrate);
        var (bg, fg) = tier switch
        {
            "S" => (B(Gold), Dark),
            "A" => (B(0x3D, 0xB5, 0xD9, 0x8F), Good),
            "B" => (B(0x24, 0xFF, 0xF0, 0xDC), Ink),
            _ => (B(0x3D, 0xE9, 0x8A, 0x5F), Bad),
        };
        return new Border
        {
            Width = size, Height = size,
            Background = bg,
            CornerRadius = new CornerRadius(round ? size / 2 : size * 0.26),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = tier, FontFamily = Serif, FontWeight = FontWeights.Bold, FontSize = size * 0.62, Foreground = B(fg),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    /// <summary>Разделитель макета: линия — ромб — линия.</summary>
    public static UIElement Divider(double top = 8, double bottom = 8)
    {
        var grid = new Grid { Margin = new Thickness(2, top, 2, bottom) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var line = B(0x38, 0xE8, 0xC8, 0x96);
        var left = new Rectangle { Height = 1, Fill = line, VerticalAlignment = VerticalAlignment.Center };
        var right = new Rectangle { Height = 1, Fill = line, VerticalAlignment = VerticalAlignment.Center };
        var diamond = new Rectangle
        {
            Width = 6, Height = 6, Stroke = B(Gold), StrokeThickness = 1, Margin = new Thickness(8, 0, 8, 0),
            RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(45),
        };
        Grid.SetColumn(diamond, 1);
        Grid.SetColumn(right, 2);
        grid.Children.Add(left);
        grid.Children.Add(diamond);
        grid.Children.Add(right);
        return grid;
    }

    /// <summary>Фон кнопки без наведения: глазу не виден, но не нулевой — иначе по «пустым» пикселям кнопки клик ушёл бы в игру.</summary>
    public static readonly SolidColorBrush IdleButton = B(0x02, 0x1A, 0x14, 0x0E);

    /// <summary>Кнопка в шапке: ↻ — обновить, ✕ — закрыть. Подсветку при наведении включает <see cref="GlassWindow"/>.</summary>
    public static Border IconButton(string glyph) => new()
    {
        Width = 24, Height = 24,
        CornerRadius = new CornerRadius(7),
        Background = IdleButton, // не совсем прозрачный: слоистое окно ловит клик только по непрозрачным пикселям
        Margin = new Thickness(2, 0, 0, 0),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock
        {
            Text = glyph, FontFamily = Symbols, FontSize = glyph == "✕" ? 11 : 14, Foreground = B(Muted),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        },
    };

    /// <summary>
    /// Шапка окна: значок (цветной квадрат, если задан), заголовок антиквой, чип с хоткеем, ↻ (если есть чем обновить) и ✕.
    /// </summary>
    public static Grid Header(GlassWindow window, string title, double titleSize, Color titleColor, string? hotkey,
        Action? refresh, Color? icon = null, string? subtitle = null, bool serif = true)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (icon is { } ic)
        {
            var square = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(5), Background = B(ic), Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "!", FontWeight = FontWeights.Bold, FontSize = 12, Foreground = B(Dark),
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            grid.Children.Add(square);
        }
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        if (subtitle != null) titles.Children.Add(Text(subtitle, 11.5, Muted, wrap: false));
        titles.Children.Add(Text(title, titleSize, titleColor, serif: serif, wrap: subtitle == null));
        Grid.SetColumn(titles, 1);
        grid.Children.Add(titles);
        if (hotkey != null)
        {
            var chip = new Border
            {
                Background = B(0x14, 0xFF, 0xF0, 0xDC), CornerRadius = new CornerRadius(6), Padding = new Thickness(6, 1, 6, 2),
                Margin = new Thickness(6, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center,
                Child = Text(hotkey, 10.5, Muted, wrap: false),
            };
            Grid.SetColumn(chip, 2);
            grid.Children.Add(chip);
        }
        if (refresh != null)
        {
            var r = IconButton("↻");
            window.AddButton(r, refresh);
            Grid.SetColumn(r, 3);
            grid.Children.Add(r);
        }
        var close = IconButton("✕");
        window.AddButton(close, window.CloseByButton);
        Grid.SetColumn(close, 4);
        grid.Children.Add(close);
        return grid;
    }
}

/// <summary>
/// Окно поверх игры: клики проходят насквозь, фокус не забирает. Кнопки ↻ и ✕ всё же нажимаются: пока окно видно,
/// раз в 80 мс смотрим курсор, и только когда он над кнопкой, окно перестаёт пропускать клики (WS_EX_TRANSPARENT снят).
/// Остальная часть окна игре не мешает — иначе лобби закрывало бы выбор героя у первого слота.
/// </summary>
abstract class GlassWindow : Window
{
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(80) };
    readonly List<(Border Button, Action Click)> _buttons = new();
    IntPtr _hwnd;
    bool _clickable;
    Border? _hover;

    /// <summary>Нажали ✕: по умолчанию — спрятать окно; наследник или хозяин может добавить своё (остановить таймеры).</summary>
    public Action? CloseButtonPressed;

    protected GlassWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE,
                ex | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED);
        };
        _poll.Tick += (_, _) => Poll();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _poll.Start();
            else
            {
                _poll.Stop();
                SetClickable(false);
                SetHover(null);
            }
        };
    }

    /// <summary>Корень содержимого — для снимков окна (--ui-test) без показа на экране.</summary>
    public FrameworkElement Root => (FrameworkElement)Content;

    /// <summary>Перед новой отрисовкой: старые кнопки шапки больше не на экране.</summary>
    protected void ClearButtons()
    {
        _buttons.Clear();
        _hover = null;
    }

    public void AddButton(Border button, Action click)
    {
        _buttons.Add((button, click));
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            click();
        };
    }

    public void CloseByButton()
    {
        Hide();
        CloseButtonPressed?.Invoke();
    }

    /// <summary>Для --click-test: таймер, курсор и где кнопки на экране.</summary>
    public string Diagnose()
    {
        Native.GetCursorPos(out var p);
        var parts = _buttons.Select(b =>
        {
            bool src = PresentationSource.FromVisual(b.Button) != null;
            string rect = "";
            if (src)
            {
                var a = b.Button.PointToScreen(new Point(0, 0));
                var c = b.Button.PointToScreen(new Point(b.Button.ActualWidth, b.Button.ActualHeight));
                rect = $" {a.X:0},{a.Y:0}–{c.X:0},{c.Y:0}";
            }
            return $"[{(b.Button.Child as TextBlock)?.Text} видна={b.Button.IsVisible} источник={src} w={b.Button.ActualWidth:0}{rect}]";
        });
        return $"hwnd={_hwnd != IntPtr.Zero} опрос={_poll.IsEnabled} кликабельно={_clickable} курсор={p.X},{p.Y} кнопок={_buttons.Count} " + string.Join(" ", parts);
    }

    void Poll()
    {
        if (_hwnd == IntPtr.Zero || !Native.GetCursorPos(out var p)) return;
        Border? over = null;
        foreach (var (button, _) in _buttons)
        {
            if (!button.IsVisible || button.ActualWidth <= 0 || PresentationSource.FromVisual(button) == null) continue;
            var a = button.PointToScreen(new Point(0, 0));
            var b = button.PointToScreen(new Point(button.ActualWidth, button.ActualHeight));
            if (p.X >= a.X && p.X < b.X && p.Y >= a.Y && p.Y < b.Y)
            {
                over = button;
                break;
            }
        }
        SetClickable(over != null);
        SetHover(over);
    }

    void SetClickable(bool clickable)
    {
        if (clickable == _clickable || _hwnd == IntPtr.Zero) return;
        _clickable = clickable;
        int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, clickable ? ex & ~Native.WS_EX_TRANSPARENT : ex | Native.WS_EX_TRANSPARENT);
        if (!clickable) Mouse.Capture(null);
    }

    void SetHover(Border? button)
    {
        if (button == _hover) return;
        if (_hover != null)
        {
            _hover.Background = _hoverWas.Background; // как было до наведения: у ↻ и ✕ — серые, у ника — свой цвет
            if (_hover.Child is TextBlock t) t.Foreground = _hoverWas.Foreground;
        }
        _hover = button;
        if (button != null)
        {
            _hoverWas = (button.Background, (button.Child as TextBlock)?.Foreground);
            button.Background = Glass.B(0x1F, 0xFF, 0xF0, 0xDC);
            if (button.Child is TextBlock t) t.Foreground = Glass.B(Glass.Ink);
        }
    }

    (Brush? Background, Brush? Foreground) _hoverWas;
}
