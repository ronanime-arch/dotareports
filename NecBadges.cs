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
/// Сначала — среди свежих записей NEC (это почти наверняка комната), затем — среди всех, кого NEC держит в кэше:
/// знакомых он заново не пишет, и по свежим из 10 ловились 5–6. Среди всех второе место выше (на 252 кандидатах
/// игроки дают 0,77–0,93 при отрыве 0,2–0,36), поэтому там нужен высокий счёт. «?» — у многих, и не только
/// стандартная Steam (у одного игрока своя картинка «?»), — такие аватарки никого не выдают и не сравниваются.
/// </summary>
sealed class AvatarMatcher
{
    const int N = 12;
    const double MinScore = 0.8, MinGap = 0.25;
    const double AllScore = 0.82, AllScoreSmall = 0.75, AllGap = 0.15, LikeDefault = 0.85;

    readonly Dictionary<string, double[]> _vectors = new();   // по файлу аватарки: файлы NEC не меняются (имя — хеш)
    List<(NecPlayer? Player, double[] V)> _recent = new(), _all = new();
    double[]? _default;

    /// <summary>
    /// Кандидаты: свежие игроки комнаты, все известные NEC, ваши аккаунты (их строку узнаём, но значок не ставим)
    /// и стандартная аватарка «?» — с ней сравниваем, чтобы такие не выдавали никого.
    /// Одна и та же картинка у двоих — кто из них на экране, не понять: таких не берём.
    /// </summary>
    public void SetCandidates(IEnumerable<NecPlayer> recent, IEnumerable<NecPlayer> all, IEnumerable<string> ownAvatars, string? defaultAvatar)
    {
        _default = defaultAvatar != null ? Load(defaultAvatar) : null;
        var own = ownAvatars.ToList();
        _recent = Build(recent, own);
        _all = Build(all, own);
    }

    List<(NecPlayer?, double[])> Build(IEnumerable<NecPlayer> players, List<string> own)
    {
        var all = players.Where(p => p.Avatar != null).Select(p => ((NecPlayer?)p, p.Avatar!))
            .Concat(own.Select(f => ((NecPlayer?)null, f))).ToList();
        var twice = all.GroupBy(c => c.Item2).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        var list = new List<(NecPlayer?, double[])>();
        foreach (var (player, file) in all)
            if (!twice.Contains(file) && Load(file) is { } v && !LooksDefault(v)) list.Add((player, v));
        return list;
    }

    bool LooksDefault(double[] v) => _default != null && Dot(v, _default) > LikeDefault;

    public int Count => _all.Count;

    /// <summary>Игрок с этой аватаркой; null — не узнан уверенно или это вы. Debug — лучший и второй счёт.</summary>
    public NecPlayer? Match(Bitmap image, Rectangle area, out string debug) => Match(image, area, out debug, out _, out _);

    /// <param name="sure">Узнан уверенно — игрок или вы (для подбора сдвига строк, где ваша строка тоже в счёт).</param>
    /// <param name="fresh">Узнан уверенно среди свежих записей NEC (комнаты) — игрок или вы.</param>
    /// <param name="small">Аватарка 32 px (выбор команды): совпадения ниже — 0,67–0,72 у игроков при отрыве 0,4+,
    /// чужие и «?» — до 0,58 при отрыве до 0,27 (снимок 17.10), поэтому пороги свои.</param>
    public NecPlayer? Match(Bitmap image, Rectangle area, out string debug, out bool sure, out bool fresh, bool small = false)
    {
        fresh = false;
        var v = Vector(image, area);
        if (LooksDefault(v))
        {
            (debug, sure) = ("«?»", false);
            return null;
        }
        // 1) свежие записи NEC — это комната: пороги мягче
        var (p1, s1, g1, own1) = Best(v, _recent);
        if (s1 >= (small ? 0.62 : MinScore) && g1 >= (small ? 0.3 : MinGap))
        {
            (debug, sure, fresh) = ($"{(own1 ? "вы" : p1?.Name ?? "-")} {s1:0.00}/{g1:0.00}", true, true);
            return own1 ? null : p1;
        }
        // 2) все известные NEC — кандидатов много, второе место выше: нужен высокий счёт
        var (p2, s2, g2, own2) = Best(v, _all);
        sure = s2 >= (small ? AllScoreSmall : AllScore) && g2 >= AllGap;
        debug = $"{(own2 ? "вы" : p2?.Name ?? "-")} {s2:0.00}/{g2:0.00} (все)";
        return !own2 && sure ? p2 : null;
    }

    static (NecPlayer? Player, double Score, double Gap, bool Own) Best(double[] v, List<(NecPlayer? Player, double[] V)> candidates)
    {
        double s1 = double.MinValue, s2 = double.MinValue;
        NecPlayer? best = null;
        bool own = false;
        foreach (var (player, c) in candidates)
        {
            double s = Dot(v, c);
            if (s > s1) { s2 = s1; s1 = s; best = player; own = player == null; }
            else if (s > s2) s2 = s;
        }
        return candidates.Count == 0 ? (null, 0, 0, false) : (best, s1, s1 - (s2 == double.MinValue ? 0 : s2), own);
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


/// <summary>Какой экран кастомки: подключение (список из 10), выбор команды (две колонки по 5), выбор героя (карточки).</summary>
enum NecLayout { Connect, Teams, Cards }

/// <summary>Узнан на экране кастомки: экран, номер строки или карточки, кто, сдвиг панели команд, счёт сравнения.</summary>
sealed record ScreenHit(NecLayout Layout, int Index, NecPlayer Player, int Shift, string Debug)
{
    /// <summary>Силы Света (0) или Тьмы (1) — видно только на экране выбора команды; иначе null.</summary>
    public int? Team => Layout == NecLayout.Teams ? (Index < 5 ? 0 : 1) : null;
}

/// <summary>
/// Что и где нарисовать: иконка ранга (и текст — в плашке), X/Y — левый край (Right — правый, Center — середина) и верх,
/// px экрана; Icon 0 — плашка без иконки.
/// </summary>
sealed record BadgeSpot(int X, int Y, bool Right, int RankTier, string? Text, Color Color, double Icon,
    bool Center = false, double FontSize = 14);

/// <summary>
/// Где на экране кастомки (1920×1080, по снимкам IM Пробуждение 17.10) аватарки игроков и куда ставить значок ранга.
/// Подключение: справа список из 10 строк, аватарка 48×48 в (1556, 312 + 60·i) — иконка перед аватаркой.
/// Выбор команды: «Силы Света» и «Силы Тьмы» по 5 строк, аватарка 32×32 в (1146, 412 + 34·i) и (1146, 635 + 34·i) —
/// иконка перед аватаркой; панель может сидеть выше или ниже (без кнопки «Заполнить слоты ботами»), сдвиг подбираем.
/// Выбор героя: карточки вашей команды по x = 63 + 353·i, аватарка ~61×61 в (карточка + 6, 214); слева соседняя
/// карточка, поэтому значок с текстом — в шапке справа под ником.
/// </summary>
static class NecScreen
{
    public static readonly Rectangle SetupStrip = new(1556, 312, 48, 60 * 9 + 48);
    public static readonly Rectangle CardStrip = new(63, 214, 353 * 4 + 345, 62);
    const int TeamsTop = 412, DireTop = 635, TeamsRow = 34, TeamsShiftUp = 60, TeamsShiftDown = 20;
    // снимаем с запасом по вертикали: сдвиг панели подбирается по тому, где аватарки узнаются лучше
    public static readonly Rectangle TeamsStrip = new(1146, TeamsTop - TeamsShiftUp, 32, DireTop + 4 * TeamsRow + 32 - TeamsTop + TeamsShiftUp + TeamsShiftDown);

    public static Rectangle Strip(NecLayout layout) => layout switch
    {
        NecLayout.Connect => SetupStrip,
        NecLayout.Teams => TeamsStrip,
        _ => CardStrip,
    };

    /// <summary>
    /// Узнанные игроки: номер строки или карточки, кто там, сдвиг панели команд и счёт сравнения (Debug).
    /// До выбора героя экран может быть любым из двух: раскладка не того экрана ложится на картинку кастомки, и среди
    /// всех 250 из кэша NEC кто-нибудь да совпадёт (mary 0,79 в «выборе команды» на снимке подключения 17.10). Поэтому
    /// раскладку берём, только если в ней уверенно узнан хоть кто-то из свежих записей NEC (или вы): в раскладке
    /// не того экрана таких не бывает (там 0), а свежих в комнате бывает всего 3–4.
    /// </summary>
    public static List<ScreenHit> Read(Bitmap strip, NecLayout layout, AvatarMatcher matcher)
    {
        int shift = layout == NecLayout.Teams ? TeamsShift(strip, matcher) : 0;
        var found = new List<ScreenHit>();
        int count = layout == NecLayout.Cards ? 5 : 10, fresh = 0;
        for (int i = 0; i < count; i++)
        {
            var p = matcher.Match(strip, Avatar(layout, i, shift), out var debug, out _, out bool f, layout == NecLayout.Teams);
            if (f) fresh++;
            if (p != null) found.Add(new ScreenHit(layout, i, p, shift, debug));
        }
        return layout == NecLayout.Cards || fresh >= MinFresh ? found : new List<ScreenHit>();
    }

    const int MinFresh = 1;

    /// <summary>Значки узнанного игрока (после того как, например, догрузилось звание).</summary>
    public static BadgeSpot[] Spots(ScreenHit hit) => Spots(hit.Layout, hit.Index, hit.Shift, hit.Player);

    /// <summary>
    /// Звание тем, чей профиль NEC не загружал (знакомых он не перечитывает), — сами с OpenDota по аккаунту;
    /// ответы запоминаются (LiveMatch), так что каждый игрок спрашивается один раз.
    /// </summary>
    public static async System.Threading.Tasks.Task<List<ScreenHit>> WithRanksAsync(List<ScreenHit> hits)
    {
        var result = new List<ScreenHit>();
        foreach (var h in hits)
        {
            if (h.Player.HasProfile || NecLobby.AccountOf(h.Player.SteamId) is not { } account) { result.Add(h); continue; }
            int tier = await LiveMatch.RankAsync(account);
            result.Add(tier > 0 ? h with { Player = h.Player with { RankTier = tier } } : h);
        }
        return result;
    }

    /// <summary>Для --badge-test: лучший и второй счёт по каждой строке (и подобранный сдвиг панели команд).</summary>
    public static IEnumerable<string> Debug(Bitmap strip, NecLayout layout, AvatarMatcher matcher)
    {
        int shift = layout == NecLayout.Teams ? TeamsShift(strip, matcher) : 0;
        if (layout == NecLayout.Teams) yield return $"сдвиг панели команд: {shift}";
        for (int i = 0; i < (layout == NecLayout.Cards ? 5 : 10); i++)
        {
            matcher.Match(strip, Avatar(layout, i, shift), out var debug, out _, out _, layout == NecLayout.Teams);
            yield return $"  строка {i + 1}: {debug}";
        }
    }

    // Аватарка внутри снимка: в строке рамка 1 px, у карточки — подсветка своего игрока до 3 px.
    static Rectangle Avatar(NecLayout layout, int i, int shift) => layout switch
    {
        NecLayout.Connect => new Rectangle(3, 3 + 60 * i, 42, 42),
        NecLayout.Teams => new Rectangle(2, TeamsShiftUp + shift + RowTop(i) - TeamsTop + 2, 28, 28),
        _ => new Rectangle(353 * i + 9, 3, 55, 55),
    };

    static int RowTop(int i) => i < 5 ? TeamsTop + TeamsRow * i : DireTop + TeamsRow * (i - 5);

    // Сдвиг панели команд (px, минус — выше): тот, при котором уверенно узнано больше всего строк (и вы в том числе).
    static int TeamsShift(Bitmap strip, AvatarMatcher matcher)
    {
        int Count(int shift)
        {
            int count = 0;
            for (int i = 0; i < 10; i++)
            {
                matcher.Match(strip, Avatar(NecLayout.Teams, i, shift), out _, out bool sure, out _, small: true);
                if (sure) count++;
            }
            return count;
        }
        // обычно панель на своём месте: тогда без перебора (кандидатов — все игроки из кэша NEC, перебор недешёвый)
        if (Count(0) >= 3) return 0;
        int best = 0, bestCount = -1;
        for (int shift = -TeamsShiftUp; shift <= TeamsShiftDown; shift += 2)
        {
            int count = Count(shift);
            if (count > bestCount || (count == bestCount && Math.Abs(shift) < Math.Abs(best))) (best, bestCount) = (shift, count);
        }
        return best;
    }

    // Иконка ранга — перед аватаркой; у карточки союзника слева соседняя карточка — на левом нижнем углу аватарки,
    // а звание текстом — в шапке справа под ником.
    static BadgeSpot[] Spots(NecLayout layout, int i, int shift, NecPlayer p)
    {
        if (p.RankTier / 10 is < 1 or > 8) return Array.Empty<BadgeSpot>(); // без медали — у ника ничего, только шум
        var (text, color) = NecLobby.Badge(p);
        return layout switch
        {
            NecLayout.Connect => new[] { new BadgeSpot(1556 - 4 - 48, 312 + 60 * i, false, p.RankTier, null, color, 48) },
            NecLayout.Teams => new[] { new BadgeSpot(1146 - 2 - 34, RowTop(i) + shift - 1, false, p.RankTier, null, color, 34) },
            _ => new[]
            {
                new BadgeSpot(63 + 353 * i - 6, 214 + 61 - 40 + 4, false, p.RankTier, null, color, 40),
                new BadgeSpot(63 + 353 * i + 345, 254, true, p.RankTier, text, color, 0),
            },
        };
    }
}

/// <summary>
/// Иконка ранга: значок медали Dota, как в клиенте (<see cref="RankArt"/>, без звёзд). Значок ещё не скачан —
/// кружок цвета медали (Рекрут — бронза … Легенда — золото, Божество — лёд, Титан — красный) с числом звёзд.
/// </summary>
static class RankIcon
{
    static readonly Color[] Medal =
    {
        Color.FromRgb(0x6B, 0x62, 0x58), // нет
        Color.FromRgb(0xA5, 0x7A, 0x4F), // Рекрут
        Color.FromRgb(0x7F, 0xA0, 0x7A), // Страж
        Color.FromRgb(0x4F, 0xA6, 0xA0), // Рыцарь
        Color.FromRgb(0x5F, 0x94, 0xD6), // Герой
        Color.FromRgb(0xD9, 0xA4, 0x41), // Легенда
        Color.FromRgb(0x9C, 0x7B, 0xD4), // Властелин
        Color.FromRgb(0xA8, 0xDC, 0xEC), // Божество
        Color.FromRgb(0xD9, 0x53, 0x3F), // Титан
    };

    public static FrameworkElement Build(int rankTier, double size)
    {
        int medal = rankTier / 10 is var m and >= 1 and <= 8 ? m : 0, stars = rankTier % 10;
        if (medal > 0 && RankArt.Medal(medal) is { } art)
            return new System.Windows.Controls.Image { Source = art, Width = size, Height = size, Stretch = System.Windows.Media.Stretch.Uniform };
        var c = Medal[medal];
        Color Shade(double k) => Color.FromRgb((byte)(c.R * k), (byte)(c.G * k), (byte)(c.B * k));
        var light = Color.FromRgb((byte)Math.Min(255, c.R + 60), (byte)Math.Min(255, c.G + 60), (byte)Math.Min(255, c.B + 60));
        string mark = medal == 0 ? "?" : medal == 8 ? "★" : stars is >= 1 and <= 5 ? stars.ToString() : "";
        // светлые медали — тёмная цифра, тёмные — светлая
        bool bright = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B > 150;
        var grid = new Grid { Width = size, Height = size };
        grid.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Fill = new System.Windows.Media.RadialGradientBrush(light, Shade(0.75)) { GradientOrigin = new Point(0.35, 0.3) },
            Stroke = Glass.B(Glass.Dark), StrokeThickness = Math.Max(1.5, size / 14),
        });
        grid.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Margin = new Thickness(size * 0.12), Stroke = Glass.B(0x66, 0xFF, 0xF0, 0xDC), StrokeThickness = 1,
        });
        grid.Children.Add(new TextBlock
        {
            Text = mark, FontFamily = Glass.Serif, FontWeight = FontWeights.Bold, FontSize = size * 0.56,
            Foreground = Glass.B(bright ? Glass.Dark : Glass.Ink),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, size * 0.04),
        });
        return grid;
    }
}

/// <summary>Значки ранга у игроков на экране кастомки — прозрачное окно на весь экран, клики проходят насквозь, фокус не забирает.</summary>
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

    /// <summary>Показать значки (координаты — в пикселях экрана); пусто — спрятать.</summary>
    public void ShowBadges(IReadOnlyCollection<BadgeSpot> spots)
    {
        if (spots.Count == 0)
        {
            Hide();
            return;
        }
        new WindowInteropHelper(this).EnsureHandle();
        // при масштабе Windows не 100% окна WPF меряются не в пикселях
        var toDip = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        (Left, Top, Width, Height) = (0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        Fill(_canvas, spots, Width, toDip);
        if (!IsVisible) Show();
    }

    /// <summary>Разложить значки по холсту шириной width (DIP); toDip — из пикселей экрана. Нужен и для --badge-test.</summary>
    public static void Fill(Canvas canvas, IEnumerable<BadgeSpot> spots, double width, System.Windows.Media.Matrix toDip)
    {
        canvas.Children.Clear();
        foreach (var s in spots)
        {
            var pt = toDip.Transform(new Point(s.X, s.Y));
            double icon = toDip.Transform(new System.Windows.Vector(s.Icon, 0)).X;
            FrameworkElement el;
            if (s.Text == null)
                el = RankIcon.Build(s.RankTier, icon);
            else
            {
                // плашка: звание (и иконка, если задана), тёмное стекло с золотистой рамкой
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                if (s.Icon > 0)
                {
                    var i = RankIcon.Build(s.RankTier, icon);
                    i.Margin = new Thickness(0, 0, 6, 0);
                    row.Children.Add(i);
                }
                row.Children.Add(new TextBlock
                {
                    Text = s.Text, FontFamily = Glass.Sans, FontSize = s.FontSize, FontWeight = FontWeights.SemiBold,
                    Foreground = Glass.B(s.Color), VerticalAlignment = VerticalAlignment.Center,
                });
                el = new Border
                {
                    Background = Glass.B(0xF2, 0x1A, 0x14, 0x0E),
                    BorderBrush = Glass.B(0x80, 0xD2, 0xA1, 0x5A),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(999),
                    Padding = s.Icon > 0 ? new Thickness(3, 2, 10, 3) : new Thickness(s.FontSize < 13 ? 7 : 10, 1, s.FontSize < 13 ? 7 : 10, 2),
                    Child = row,
                };
            }
            if (s.Right) Canvas.SetRight(el, width - pt.X);
            else if (s.Center)
            {
                el.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(el, pt.X - el.DesiredSize.Width / 2);
            }
            else Canvas.SetLeft(el, pt.X);
            Canvas.SetTop(el, pt.Y);
            canvas.Children.Add(el);
        }
    }
}
