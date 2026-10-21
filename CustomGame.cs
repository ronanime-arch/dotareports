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

/// <summary>Ваша игра в кастомке для оценки — записанная программой и/или из истории NEC; Gpm 0 — неизвестно (игра только в NEC).</summary>
sealed record HereGame(int Hero, bool? Win, int Kills, int Deaths, int Assists, int Gpm)
{
    public double Kda => (Kills + Assists) / (double)Math.Max(1, Deaths);
}

/// <summary>
/// Кого из трёх брать: оценка (выше — лучше), ваши игры на герое здесь (HereGpm 0 — золота не знаем) и в обычной Доте, его винрейт в Турбо;
/// оценка без отзывов в мастерской и без обычной Доты — чтобы сказать, что решило выбор.
/// </summary>
sealed record CustomChoice(int Hero, double Score, int HereGames, double HereGpm, int HereWins, int HereKnown, double HereKda,
    int UsualGames, int UsualWins, double Turbo, CommunityVoice? Voice = null, double ScoreWithoutVoice = 0, double ScoreWithoutDota = 0)
{
    public string Here => HereGames == 0 ? "здесь ещё не играли"
        : $"здесь {HereGames} {Words.Plural(HereGames, "игра", "игры", "игр")}" + (HereKnown > 0 ? $", побед {HereWins} из {HereKnown}" : "")
          + (HereGpm > 0 ? $" · {HereGpm:0} зол/мин" : "") + $" · KDA {HereKda:0.0}";

    public string Usual => (UsualGames > 0 ? $"в Доте {UsualGames} {Words.Plural(UsualGames, "игра", "игры", "игр")}, {UsualRate:0}% побед" : "в Доте не играли")
        + $" · Турбо {Turbo * 100:0.0}%";

    public double UsualRate => UsualGames > 0 ? 100.0 * UsualWins / UsualGames : 0;
}

/// <summary>Отзывы о герое: сколько разных людей просили его ослабить и за какой период.</summary>
sealed record CommunityVoice(int Authors, string Period)
{
    public string People => $"{Authors} {Words.Plural(Authors, "человек", "человека", "человек")}";
}

/// <summary>
/// Что пишут игроки о кастомке IM Пробуждение (мастерская 3320645051): в сети о ней почти ничего, у китайского сообщества —
/// видео на B站 без текста. Единственный текстовый источник — комментарии в мастерской Steam (469 штук на 17.10.2026):
/// здесь герои, которых просили ослабить хотя бы двое разных людей (один автор — не в счёт). Карту обновили 15.10.2026,
/// жалобы старше — сильных могли уже ослабить, поэтому при них период.
/// </summary>
static class CommunityPicks
{
    public const string Game = "3320645051";
    const double PerAuthor = 0.01, MaxShift = 0.06; // центр приора в «кого брать» — +1 п.п. винрейта за человека, не больше 6

    static readonly Dictionary<string, CommunityVoice> Strong = new()
    {
        ["npc_dota_hero_lycan"] = new(16, "07–10.2026"),
        ["npc_dota_hero_furion"] = new(7, "03–07.2026"),
        ["npc_dota_hero_abaddon"] = new(6, "04–10.2026"),
        ["npc_dota_hero_muerta"] = new(2, "03–10.2026"),
        ["npc_dota_hero_invoker"] = new(2, "03–04.2026"),
        ["npc_dota_hero_pangolier"] = new(2, "04.2026"),
    };

    public static CommunityVoice? Of(string game, string? heroName) =>
        game == Game && heroName != null && Strong.TryGetValue(heroName, out var v) ? v : null;

    public static double Shift(CommunityVoice? v) => v == null ? 0 : Math.Min(MaxShift, PerAuthor * v.Authors);
}

/// <summary>
/// Статистика ваших игр в кастомках (custom_stats.json, последние 500): Stratz и OpenDota кастомки не собирают,
/// поэтому единственные данные — ваши. Dota присылает в GSI героя, K/D/A, GPM, XPM и кто победил (map.win_team) —
/// но кастомка IM Пробуждение победителя почти не присылает: её исходы — из истории NEC (<see cref="AwakenHistory"/>).
/// </summary>
static class CustomStats
{
    const int Keep = 500;
    const int MinGames = 3;        // своих игр в кастомке меньше — советуем по обычной Доте
    const double GpmPrior = 2;     // GPM и KDA героя сглаживаются к вашим средним здесь, как будто ещё 2 игры
    const double OwnPrior = 30;    // обычная Дота: ваши игры на герое, общий винрейт весит как 30 игр
    const double WinPrior = 5;     // победы здесь: ожидание по Доте и игре на герое весит как 5 игр с известным исходом
    const double FormWeight = 0.5; // на герое золота и KDA на 20% больше ваших средних — ожидание побед на 10 п.п. выше
    const double MaxForm = 0.4;    // не больше ±40%: пара удачных игр не перевешивает победы
    const double NecSlack = 20;    // минут: NEC пишет начало игры, мы — конец; между ними длина игры и до 20 мин выбора и итогов

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
    /// Ваши игры в кастомке <paramref name="game"/>: записанные программой и, для IM Пробуждение, из истории NEC — каждая один раз.
    /// У NEC время — начало игры, у нас — конец: та же игра, если тот же герой и NEC начал её за длину игры … +20 мин
    /// до нашей записи (на 15 общих играх 21.10 — за 3,7–5,7 мин сверх длины, K/D/A совпали). Исход и K/D/A — от NEC, золото — наше.
    /// </summary>
    public static List<HereGame> Games(string game, IEnumerable<CustomGameRecord> own, IEnumerable<AwakenMatch> nec)
    {
        var free = game == CommunityPicks.Game ? nec.ToList() : new List<AwakenMatch>();
        var result = new List<HereGame>();
        foreach (var r in own.Where(r => r.Game == game && r.Gpm > 0).OrderBy(r => r.At))
        {
            var start = r.At.ToUniversalTime().AddMinutes(-r.Minutes); // начало по часам игры; NEC — раньше, на выбор героя
            var same = free.Where(m => m.Hero == r.Hero && (start - m.Start.ToUniversalTime()).TotalMinutes is >= 0 and <= NecSlack)
                .MinBy(m => start - m.Start.ToUniversalTime());
            if (same != null) free.Remove(same);
            result.Add(same != null ? new HereGame(r.Hero, same.Win, same.Kills, same.Deaths, same.Assists, r.Gpm)
                : new HereGame(r.Hero, r.Win, r.Kills, r.Deaths, r.Assists, r.Gpm));
        }
        result.AddRange(free.Select(m => new HereGame(m.Hero, m.Win, m.Kills, m.Deaths, m.Assists, 0)));
        return result;
    }

    /// <summary>
    /// Три героя на выбор — от лучшего. Есть хотя бы 3 ваши игры в этой кастомке — по победам на герое здесь (исход
    /// известен у игр из истории NEC), сглаженным к ожиданию: винрейту в обычной Доте, поправленному на то, как герой
    /// у вас идёт здесь — золото в минуту и KDA против ваших средних здесь. Пока исходов на герое нет, решает это ожидание.
    /// Своих игр в кастомке меньше трёх — по вашим играм на герое в обычной Доте, сглаженным к его винрейту в Турбо.
    /// Отзывы о слишком сильных героях (<see cref="CommunityPicks"/>) сдвигают винрейт по Доте вверх: решают, пока своих игр
    /// на героях мало, а игра на сыгранных здесь героях перевешивает.
    /// </summary>
    public static (List<CustomChoice> Choices, bool ByCustom) Rank(DraftData data, string game, IReadOnlyList<int> heroes)
    {
        var here = Games(game, Load(), game == CommunityPicks.Game ? AwakenHistory.Load().Matches : Enumerable.Empty<AwakenMatch>());
        bool byCustom = here.Count >= MinGames;
        var farmed = here.Where(g => g.Gpm > 0).ToList();
        double avgGpm = farmed.Count > 0 ? farmed.Average(g => g.Gpm) : 0;
        double avgKda = here.Count > 0 ? here.Average(g => g.Kda) : 0;
        var names = game == CommunityPicks.Game ? data.HeroInternalNames() : new Dictionary<int, string>();
        var result = new List<CustomChoice>();
        foreach (var hero in heroes)
        {
            var games = here.Where(g => g.Hero == hero).ToList();
            var known = games.Where(g => g.Win != null).ToList();
            int wins = known.Count(g => g.Win == true);
            var heroFarmed = games.Where(g => g.Gpm > 0).ToList();
            double gpm = heroFarmed.Count > 0 ? heroFarmed.Average(g => g.Gpm) : 0;
            double kda = games.Count > 0 ? games.Average(g => g.Kda) : 0;
            // насколько герой у вас лучше среднего здесь: золото и KDA, сглаженные к средним, как будто ещё GpmPrior игр
            double form = Math.Clamp((Above(heroFarmed.Sum(g => g.Gpm), heroFarmed.Count, avgGpm) + Above(games.Sum(g => g.Kda), games.Count, avgKda)) / 2,
                -MaxForm, MaxForm);
            double turbo = data.ModeWinrate(hero, "TURBO") is { } wr ? wr.Winrate / 100 : 0.5;
            var usual = data.Pool.TryGetValue(hero, out var pool) && pool.Games > 0 ? pool : (Games: 0, Wins: 0);
            var voice = CommunityPicks.Of(game, names.GetValueOrDefault(hero));
            double Score(double shift, bool dota = true)
            {
                // без Доты — как будто там на герое не играли, а его винрейт в Турбо — 50%
                double usualRate = dota ? (usual.Wins + OwnPrior * (turbo + shift)) / (usual.Games + OwnPrior) : 0.5 + shift;
                return byCustom ? (wins + WinPrior * (usualRate + FormWeight * form)) / (known.Count + WinPrior) : usualRate;
            }
            result.Add(new CustomChoice(hero, Score(CommunityPicks.Shift(voice)), games.Count, gpm, wins, known.Count, kda,
                usual.Games, usual.Wins, turbo, voice, Score(0), Score(CommunityPicks.Shift(voice), dota: false)));
        }
        return (result.OrderByDescending(c => c.Score).ToList(), byCustom);
    }

    // На сколько долей среднее на герое выше вашего среднего здесь, со сглаживанием; среднего нет — 0.
    static double Above(double sum, int n, double avg) => avg > 0 ? (sum + GpmPrior * avg) / (n + GpmPrior) / avg - 1 : 0;

    /// <summary>Первым его сделали отзывы в мастерской: без них первым был бы другой.</summary>
    public static bool DecidedByVoice(IReadOnlyList<CustomChoice> choices) =>
        choices[0].Voice != null && choices.Skip(1).Any(o => o.ScoreWithoutVoice > choices[0].ScoreWithoutVoice);

    /// <summary>Кто был бы первым без обычной Доты (ваших игр там и винрейта в Турбо): первый обошёл его только ею; null — решили не они.</summary>
    public static CustomChoice? BeatenByDota(IReadOnlyList<CustomChoice> choices) =>
        choices.Skip(1).Where(o => o.ScoreWithoutDota > choices[0].ScoreWithoutDota).MaxBy(o => o.ScoreWithoutDota);

    /// <summary>Почему первый из трёх: на чём основана оценка и как с этим у двух других.</summary>
    public static string Why(IReadOnlyList<CustomChoice> choices, bool byCustom, Func<int, string> name)
    {
        var best = choices[0];
        var rest = choices.Skip(1).ToList();
        string Games(int n) => $"{n} {Words.Plural(n, "игру", "игры", "игр")}";
        if (DecidedByVoice(choices) && best.Voice is { } v)
            return $"в мастерской Steam {v.People} ({v.Period}) просили его ослабить — это перевесило ваши игры и Турбо";
        string voice = best.Voice is { } bv ? $"; в мастерской его просили ослабить {bv.People}" : "";
        // исходы известны хоть у одного из трёх — сравниваем победы, иначе — золото; KDA — всегда
        bool outcomes = choices.Any(c => c.HereKnown > 0);
        string Here(CustomChoice c) => !outcomes ? $"{c.HereGpm:0} зол/мин, KDA {c.HereKda:0.0}"
            : c.HereKnown > 0 ? $"{c.HereWins} из {c.HereKnown}, KDA {c.HereKda:0.0}" : $"KDA {c.HereKda:0.0}, исход неизвестен";
        if (byCustom && best.HereGames > 0)
        {
            string mine = !outcomes ? $"{best.HereGpm:0} золота в минуту и KDA {best.HereKda:0.0} за {Games(best.HereGames)}"
                : best.HereKnown > 0 ? $"{best.HereWins} {Words.Plural(best.HereWins, "победа", "победы", "побед")} из {best.HereKnown}, KDA {best.HereKda:0.0}"
                : $"KDA {best.HereKda:0.0} за {Games(best.HereGames)}, исход неизвестен";
            // по играм здесь первым был бы другой — назвать, чем обычная Дота перевесила
            string Dota(CustomChoice c) => c.UsualGames > 0 ? $"на {name(c.Hero)} у вас {c.UsualRate:0}% побед за {Games(c.UsualGames)}"
                : $"на {name(c.Hero)} не играли, его Турбо {c.Turbo * 100:0.0}%";
            string dota = BeatenByDota(choices) is not { } other ? ""
                : best.UsualGames == 0 && other.UsualGames == 0
                    ? $"; перевесил винрейт в Турбо: {name(best.Hero)} {best.Turbo * 100:0.0}%, {name(other.Hero)} {other.Turbo * 100:0.0}%"
                    : $"; перевесила обычная Дота: {Dota(best)}, {Dota(other)}";
            return $"здесь у вас на нём {mine}; "
                   + string.Join("; ", rest.Select(o => o.HereGames > 0 ? $"{name(o.Hero)} — {Here(o)}" : $"на {name(o.Hero)} здесь не играли")) + dota + voice;
        }
        if (best.UsualGames > 0)
            return $"в Доте у вас на нём {best.UsualRate:0}% побед за {Games(best.UsualGames)}; "
                   + string.Join(", ", rest.Select(o => o.UsualGames > 0 ? $"{name(o.Hero)} — {o.UsualRate:0}% за {Games(o.UsualGames)}" : $"на {name(o.Hero)} не играли")) + voice;
        return $"ни на ком из трёх вы не играли — у него выше винрейт в Турбо: {best.Turbo * 100:0.0}% ("
               + string.Join(", ", rest.Select(o => $"{name(o.Hero)} {o.Turbo * 100:0.0}%")) + ")" + voice;
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

    /// <summary>Где на экране k-я картинка (0–2) в карточке игрока card (0–4).</summary>
    public static Rectangle ThumbArea(int card, int k) => new(CardX0 + CardStep * card + ThumbDx + ThumbStep * k, ThumbY, ThumbW, ThumbH);

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
