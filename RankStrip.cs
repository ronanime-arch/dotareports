using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DotaReports;

/// <summary>
/// Однострочная полоска со званиями игроков матча — по центру, сразу под портретами верхней полосы.
/// Клики проходят насквозь, фокус не забирает, сама исчезает.
/// </summary>
sealed class RankStrip : Window
{
    // низ портретов верхней полосы в матче — около 5% высоты экрана (54 px при 1080)
    const double TopShare = 0.05;
    const double NameWidth = 80;

    // цвета медалей по номеру: 1 — Рекрут … 8 — Титан
    static readonly Color[] MedalColors =
    {
        OverlayWindow.Gray,
        Color.FromRgb(0xA8, 0x86, 0x64), Color.FromRgb(0x9F, 0xB4, 0xBE), Color.FromRgb(0x7C, 0xC4, 0xA8), Color.FromRgb(0x6F, 0xB8, 0xE8),
        Color.FromRgb(0x8E, 0xD6, 0x7C), Color.FromRgb(0x7F, 0xA4, 0xFF), Color.FromRgb(0xC8, 0x8C, 0xFF), Color.FromRgb(0xFF, 0xB3, 0x47),
    };

    readonly StackPanel _row = new() { Orientation = Orientation.Horizontal };
    readonly DispatcherTimer _autoHide = new();
    readonly DispatcherTimer _keepOnTop = new() { Interval = TimeSpan.FromSeconds(1) };
    IntPtr _hwnd;

    public RankStrip()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xD8, 0x14, 0x18, 0x21)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 3, 10, 3),
            Child = _row,
        };
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE,
                ex | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED);
        };
        SizeChanged += (_, _) => Place();
        _autoHide.Tick += (_, _) => HideStrip();
        _keepOnTop.Tick += (_, _) => KeepOnTop();
    }

    /// <summary>Ник и звание каждого, от старших званий к младшим; неизвестные — в конце. Сама скроется через duration.</summary>
    public void ShowRanks(IEnumerable<PlayerRank> ranks, TimeSpan duration)
    {
        _row.Children.Clear();
        _row.Children.Add(Text("Звания в матче:", OverlayWindow.Gray, left: 0));
        bool first = true;
        foreach (var r in ranks.OrderByDescending(r => r.RankTier ?? -1))
        {
            if (!first) _row.Children.Add(Text("·", OverlayWindow.Gray));
            first = false;
            var (label, color) = Label(r);
            _row.Children.Add(Text(r.Name.Length > 0 ? r.Name : "#" + r.AccountId, OverlayWindow.White, maxWidth: NameWidth));
            _row.Children.Add(Text(label, color, bold: true, left: 4));
        }

        if (!IsVisible) Show();
        Place();
        KeepOnTop();
        _keepOnTop.Start();
        _autoHide.Stop();
        _autoHide.Interval = duration;
        _autoHide.Start();
    }

    public void HideStrip()
    {
        _autoHide.Stop();
        _keepOnTop.Stop();
        Hide();
    }

    static (string Text, Color Color) Label(PlayerRank r)
    {
        if (r.RankTier is not int tier) return ("?", OverlayWindow.Gray); // OpenDota не ответил
        if (Ranks.Name(tier) is not { } name) return ("без звания", OverlayWindow.Gray);
        return (name + (r.Leaderboard is int place && place > 0 ? $" #{place}" : ""), MedalColors[tier / 10]);
    }

    void Place()
    {
        Left = (SystemParameters.PrimaryScreenWidth - ActualWidth) / 2;
        Top = SystemParameters.PrimaryScreenHeight * TopShare;
    }

    // Игра в режиме «окно без рамки» может подняться выше — возвращаем окно наверх.
    void KeepOnTop()
    {
        if (_hwnd != IntPtr.Zero)
            Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    static TextBlock Text(string text, Color color, bool bold = false, double left = 6, double maxWidth = double.PositiveInfinity) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = 13,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Foreground = new SolidColorBrush(color),
        MaxWidth = maxWidth,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(left, 0, 0, 0),
    };
}
