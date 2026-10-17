using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Rectangle = System.Drawing.Rectangle;

namespace DotaReports;

/// <summary>
/// Кто из комнаты NEC стоит в какой строке экрана кастомки — по аватарке Steam: на экране та же картинка, что NEC держит
/// в своём кэше (Cache\avatars). Ники тут не помогают: у многих они из значков и иероглифов.
/// Сравниваем целиком, уменьшив до 12×12 (цвет, среднее 0, разброс 1). На записанном матче 17.10: игроки — 0,91–0,96
/// при втором кандидате до 0,56, пустые слоты «Ожидание игрока» — до 0,63.
/// </summary>
sealed class AvatarMatcher
{
    const int N = 12;
    const double MinScore = 0.8, MinGap = 0.25;

    readonly Dictionary<string, double[]> _vectors = new();   // по файлу аватарки: файлы NEC не меняются (имя — хеш)
    List<(NecPlayer? Player, double[] V)> _candidates = new();

    /// <summary>
    /// Кандидаты — игроки комнаты с аватаркой и ваши аккаунты (их строку узнаём, но плашку не ставим).
    /// Одна и та же картинка у двоих — кто из них на экране, не понять: таких не берём.
    /// </summary>
    public void SetCandidates(IEnumerable<NecPlayer> players, IEnumerable<string> ownAvatars)
    {
        var all = players.Where(p => p.Avatar != null).Select(p => ((NecPlayer?)p, p.Avatar!))
            .Concat(ownAvatars.Select(f => ((NecPlayer?)null, f))).ToList();
        var twice = all.GroupBy(c => c.Item2).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var list = new List<(NecPlayer?, double[])>();
        foreach (var (player, file) in all)
            if (!twice.Contains(file) && Load(file) is { } v) list.Add((player, v));
        _candidates = list;
    }

    public int Count => _candidates.Count;

    /// <summary>Игрок с этой аватаркой; null — не узнан уверенно или это вы. Debug — лучший и второй счёт.</summary>
    public NecPlayer? Match(Bitmap image, Rectangle area, out string debug)
    {
        var v = Vector(image, area);
        double s1 = double.MinValue, s2 = double.MinValue;
        NecPlayer? best = null;
        bool own = false;
        foreach (var (player, c) in _candidates)
        {
            double s = Dot(v, c);
            if (s > s1) { s2 = s1; s1 = s; best = player; own = player == null; }
            else if (s > s2) s2 = s;
        }
        debug = $"{(own ? "вы" : best?.Name ?? "-")} {s1:0.00}/{s1 - s2:0.00}";
        return !own && s1 >= MinScore && s1 - s2 >= MinGap ? best : null;
    }

    double[]? Load(string file)
    {
        if (_vectors.TryGetValue(file, out var v)) return v;
        try
        {
            using var bmp = new Bitmap(file);
            return _vectors[file] = Vector(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or System.IO.IOException)
        {
            Log.Write($"NEC: аватарка {file} не прочитана: {ex.Message}");
            return null;
        }
    }

    static double[] Vector(Bitmap src, Rectangle area)
    {
        using var small = new Bitmap(N, N, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(src, new Rectangle(0, 0, N, N), area, GraphicsUnit.Pixel);
        }
        var data = small.LockBits(new Rectangle(0, 0, N, N), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var bytes = new byte[data.Stride * N];
        Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
        small.UnlockBits(data);
        var v = new double[N * N * 3];
        int k = 0;
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N * 3; x++)
                v[k++] = bytes[y * data.Stride + x];
        double mean = v.Average(), sd = Math.Sqrt(v.Sum(a => (a - mean) * (a - mean)) / v.Length);
        if (sd < 1e-6) sd = 1;
        for (int i = 0; i < v.Length; i++) v[i] = (v[i] - mean) / sd;
        return v;
    }

    static double Dot(double[] a, double[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s / a.Length;
    }
}

/// <summary>
/// Где на экране кастомки (1920×1080, по снимкам IM Пробуждение 17.10) аватарки игроков и куда ставить плашку со званием.
/// Экран подключения: справа список из 10 строк, аватарка 48×48 в (1556, 312 + 60·i), плашка — левее списка.
/// Выбор героя: карточки вашей команды по x = 63 + 353·i, аватарка ~61×61 в (карточка + 6, 214), плашка — в шапке справа под ником.
/// </summary>
static class NecScreen
{
    public static readonly Rectangle SetupStrip = new(1556, 312, 48, 60 * 9 + 48);
    public static readonly Rectangle CardStrip = new(63, 214, 353 * 4 + 345, 62);

    /// <summary>Узнанные игроки: номер строки (экран подключения) или карточки (выбор героя) и кто там.</summary>
    public static List<(int Index, NecPlayer Player, string Debug)> Read(Bitmap strip, bool setup, AvatarMatcher matcher)
    {
        var found = new List<(int, NecPlayer, string)>();
        int count = setup ? 10 : 5;
        for (int i = 0; i < count; i++)
        {
            // внутри рамки аватарки: в строке рамка 1 px, у карточки — подсветка своего игрока до 3 px
            var area = setup ? new Rectangle(3, 3 + 60 * i, 42, 42) : new Rectangle(353 * i + 9, 3, 55, 55);
            var p = matcher.Match(strip, area, out var debug);
            if (p != null) found.Add((i, p, debug));
        }
        return found;
    }

    /// <summary>Плашка: правый край и верх, в пикселях экрана.</summary>
    public static (int Right, int Top) Badge(int index, bool setup) =>
        setup ? (1548, 312 + 60 * index + 13) : (63 + 353 * index + 345, 256);
}

/// <summary>Плашки со званием под никами — прозрачное окно на весь экран, клики проходят насквозь, фокус не забирает.</summary>
sealed class RankBadges : Window
{
    readonly Canvas _canvas = new();

    public RankBadges()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        Content = _canvas;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE,
                ex | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED);
        };
    }

    /// <summary>Показать плашки (правый край и верх — в пикселях экрана); пусто — спрятать.</summary>
    public void ShowBadges(IReadOnlyCollection<(int Right, int Top, string Text, Color Color)> badges)
    {
        if (badges.Count == 0)
        {
            Hide();
            return;
        }
        new WindowInteropHelper(this).EnsureHandle();
        // при масштабе Windows не 100% окна WPF меряются не в пикселях
        var toDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        (Left, Top, Width, Height) = (0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        _canvas.Children.Clear();
        foreach (var (right, top, text, color) in badges)
        {
            var pt = toDip.Transform(new Point(right, top));
            var badge = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(Color.FromArgb(0xE6, 0x14, 0x18, 0x21)),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 2),
                Child = new TextBlock
                {
                    Text = text,
                    FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                    FontSize = 13,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new System.Windows.Media.SolidColorBrush(color),
                },
            };
            Canvas.SetRight(badge, Width - pt.X);
            Canvas.SetTop(badge, pt.Y);
            _canvas.Children.Add(badge);
        }
        if (!IsVisible) Show();
    }
}
