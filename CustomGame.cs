using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DotaReports;

/// <summary>Ваша игра в кастомке: герой, исход (null — не узнали: вышли раньше конца), K/D/A, золото и опыт в минуту, минуты.</summary>
sealed record CustomGameRecord(string Game, DateTime At, int Hero, bool? Win, int Kills, int Deaths, int Assists, int Gpm, int Xpm, int Minutes);

/// <summary>Кого из трёх брать: оценка (выше — лучше) и строки для карточки.</summary>
sealed record CustomChoice(int Hero, double Score, string Here, string Usual);

/// <summary>
/// Статистика ваших игр в кастомках (custom_stats.json, последние 500): Stratz и OpenDota кастомки не собирают,
/// поэтому единственные данные — ваши. Dota присылает в GSI героя, K/D/A, GPM, XPM и кто победил (map.win_team).
/// </summary>
static class CustomStats
{
    const int Keep = 500;
    const int MinGames = 3;        // своих игр в кастомке меньше — советуем по обычной Доте
    const double GpmPrior = 2;     // GPM героя сглаживается к вашему среднему, как будто ещё 2 игры
    const double OwnPrior = 30;    // обычная Дота: ваши игры на герое, общий винрейт весит как 30 игр

    public static List<CustomGameRecord> Load()
    {
        try
        {
            if (File.Exists(Paths.CustomStats))
                return JsonSerializer.Deserialize<List<CustomGameRecord>>(File.ReadAllText(Paths.CustomStats)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Log.Write("custom_stats.json не прочитан: " + ex.Message);
        }
        return new();
    }

    public static void Add(CustomGameRecord r)
    {
        var all = Load();
        all.Add(r);
        try
        {
            File.WriteAllText(Paths.CustomStats, JsonSerializer.Serialize(all.TakeLast(Keep).ToList()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("custom_stats.json не записан: " + ex.Message);
        }
    }

    /// <summary>
    /// Три героя на выбор — от лучшего. Есть хотя бы 3 ваши игры в этой кастомке — по золоту в минуту на герое
    /// (против ботов побеждают почти всегда, а GPM показывает, насколько герой у вас разгоняется), сглаженному
    /// к вашему среднему; иначе — по вашим играм на герое в обычной Доте, сглаженным к его винрейту в Турбо.
    /// </summary>
    public static (List<CustomChoice> Choices, bool ByCustom) Rank(DraftData data, string game, IReadOnlyList<int> heroes)
    {
        var here = Load().Where(r => r.Game == game && r.Gpm > 0).ToList();
        bool byCustom = here.Count >= MinGames;
        double avgGpm = here.Count > 0 ? here.Average(r => r.Gpm) : 0;
        var result = new List<CustomChoice>();
        foreach (var hero in heroes)
        {
            var games = here.Where(r => r.Hero == hero).ToList();
            string hereText;
            if (games.Count == 0) hereText = "здесь ещё не играли";
            else
            {
                var known = games.Where(r => r.Win != null).ToList();
                double kda = games.Average(r => (r.Kills + r.Assists) / (double)Math.Max(1, r.Deaths));
                hereText = $"здесь {games.Count} {Words.Plural(games.Count, "игра", "игры", "игр")}"
                           + (known.Count > 0 ? $", побед {known.Count(r => r.Win == true)} из {known.Count}" : "")
                           + $" · {games.Average(r => r.Gpm):0} зол/мин · KDA {kda:0.0}";
            }
            double turbo = data.ModeWinrate(hero, "TURBO") is { } wr ? wr.Winrate / 100 : 0.5;
            var usual = data.Pool.TryGetValue(hero, out var pool) && pool.Games > 0 ? pool : (Games: 0, Wins: 0);
            double usualRate = (usual.Wins + OwnPrior * turbo) / (usual.Games + OwnPrior);
            string usualText = (usual.Games > 0 ? $"в Доте {usual.Games} {Words.Plural(usual.Games, "игра", "игры", "игр")}, {100.0 * usual.Wins / usual.Games:0}%" : "в Доте не играли")
                               + $" · Турбо {turbo * 100:0.0}%";
            double score = byCustom ? (games.Sum(r => r.Gpm) + GpmPrior * avgGpm) / (games.Count + GpmPrior) + usualRate : usualRate;
            result.Add(new CustomChoice(hero, score, hereText, usualText));
        }
        return (result.OrderByDescending(c => c.Score).ToList(), byCustom);
    }
}

/// <summary>
/// Кастомка с выбором героя из трёх (мастерская 3320645051: у каждого игрока карточка «Герой» с тремя картинками).
/// Картинки — те же портреты Valve, что в portraits\ (256×144), уменьшенные до 107×60; сравниваем целиком, 32×18,
/// без правого верхнего угла (там галочка у выбранного). По скриншоту 1920×1080: карточка игрока i (team_slot) —
/// x = 63 + 353·i, картинки — x = карточка + 3 + 113·k, y 323–383. Не узнан уверенно хоть один — не тот экран.
/// </summary>
sealed class ChoiceReader
{
    const int CardX0 = 63, CardStep = 353, ThumbDx = 3, ThumbStep = 113, ThumbY = 323, ThumbW = 107, ThumbH = 60;
    const int VW = 32, VH = 18;
    const double MinScore = 0.6, MinGap = 0.1;

    readonly Dictionary<int, double[]> _portraits = new();
    static readonly bool[] Covered = BuildCovered();
    static readonly int OpenCells = Covered.Count(c => !c);

    /// <summary>Лучшие совпадения последнего чтения — в журнал, если так и не узнали (подобрать пороги).</summary>
    public string LastScores { get; private set; } = "";
    public bool Enabled => _portraits.Count > 0;

    public ChoiceReader()
    {
        if (!Directory.Exists(Paths.Portraits)) return;
        foreach (var file in Directory.GetFiles(Paths.Portraits, "*.png"))
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out var hero)) continue;
            try
            {
                using var bmp = new Bitmap(file);
                _portraits[hero] = Vector(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
            }
            catch (Exception ex) when (ex is ArgumentException or ExternalException)
            {
                Log.Write($"Портрет {file} не прочитан: {ex.Message}");
            }
        }
    }

    /// <summary>Три героя на выбор в карточке игрока card (0–4) прямо с экрана; null — хоть один не узнан уверенно.</summary>
    public int[]? ReadScreen(int card)
    {
        var strip = new Rectangle(CardX0 + CardStep * card + ThumbDx, ThumbY, ThumbStep * 2 + ThumbW, ThumbH);
        using var bmp = new Bitmap(strip.Width, strip.Height, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(strip.Location, Point.Empty, strip.Size);
        var heroes = new int[3];
        var scores = new List<string>();
        bool sure = true;
        for (int k = 0; k < 3; k++)
        {
            // без рамки в 1 px
            var v = Vector(bmp, new Rectangle(ThumbStep * k + 1, 1, ThumbW - 2, ThumbH - 2));
            int best = 0;
            double s1 = double.MinValue, s2 = double.MinValue;
            foreach (var (hero, p) in _portraits)
            {
                double s = Dot(v, p);
                if (s > s1) { s2 = s1; s1 = s; best = hero; }
                else if (s > s2) s2 = s;
            }
            heroes[k] = best;
            scores.Add($"{best} {s1:0.00}/{s1 - s2:0.00}");
            if (s1 < MinScore || s1 - s2 < MinGap) sure = false;
        }
        LastScores = string.Join(", ", scores);
        return sure && heroes.Distinct().Count() == 3 ? heroes : null;
    }

    // Уменьшение до VW×VH усреднением и нормировка (среднее 0, разброс 1) по открытым клеткам.
    static double[] Vector(Bitmap bmp, Rectangle area)
    {
        var data = bmp.LockBits(area, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var bytes = new byte[data.Stride * area.Height];
        Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
        bmp.UnlockBits(data);
        var v = new double[VW * VH * 3];
        for (int oy = 0; oy < VH; oy++)
        {
            int y0 = oy * area.Height / VH, y1 = Math.Max(y0 + 1, (oy + 1) * area.Height / VH);
            for (int ox = 0; ox < VW; ox++)
            {
                int x0 = ox * area.Width / VW, x1 = Math.Max(x0 + 1, (ox + 1) * area.Width / VW);
                double r = 0, g = 0, b = 0;
                int n = 0;
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        int i = y * data.Stride + x * 3;
                        b += bytes[i]; g += bytes[i + 1]; r += bytes[i + 2]; n++;
                    }
                int j = (oy * VW + ox) * 3;
                v[j] = r / n; v[j + 1] = g / n; v[j + 2] = b / n;
            }
        }
        double sum = 0, sq = 0;
        int open = 0;
        for (int i = 0; i < v.Length; i++)
            if (!Covered[i / 3]) { sum += v[i]; open++; }
        double m = sum / Math.Max(open, 1);
        for (int i = 0; i < v.Length; i++)
            if (!Covered[i / 3]) sq += (v[i] - m) * (v[i] - m);
        double sd = Math.Sqrt(sq / Math.Max(open, 1));
        if (sd < 1e-6) sd = 1;
        for (int i = 0; i < v.Length; i++) v[i] = Covered[i / 3] ? 0 : (v[i] - m) / sd;
        return v;
    }

    static double Dot(double[] a, double[] b)
    {
        double s = 0;
        for (int i = 0; i < a.Length; i++) s += a[i] * b[i];
        return s / (OpenCells * 3);
    }

    // правый верхний угол — галочка у выбранного героя
    static bool[] BuildCovered()
    {
        var covered = new bool[VW * VH];
        for (int oy = 0; oy < VH; oy++)
            for (int ox = 0; ox < VW; ox++)
                covered[oy * VW + ox] = ox >= VW * 0.85 && oy < VH * 0.3;
        return covered;
    }
}
