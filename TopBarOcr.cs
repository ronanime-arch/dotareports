using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace DotaReports;

/// <summary>
/// Чей ник под каким портретом верхней полосы в матче Valve: у игроков из coplay слотов нет, поэтому ники под портретами
/// читаем Windows OCR и ищем среди них ники Steam. Движки en и ru (кириллицу en читает латиницей-двойником, ru латиницу —
/// наоборот), полоса увеличена вдвое и втрое — берём лучшее. Тег клана [ABC] липнет к нику, длинный ник оборван «...»:
/// сравниваем начало. По кадрам пика 21.10 (матчи 9055669251, 9055848509).
/// </summary>
static class TopBarOcr
{
    /// <summary>Полоса ников с запасом сверху и снизу: впритык (<see cref="TopBarNames.Band"/>) OCR читает хуже.</summary>
    public static readonly Rectangle Area = new(TopBarNames.Band.X, TopBarNames.Band.Y - 3, TopBarNames.Band.Width, TopBarNames.Band.Height + 6);
    static readonly int[] Scales = { 2, 3 };
    const double MinScore = 0.7; // на кадрах 21.10: свой ник ≥ 0,75, чужие ≤ 0,43

    static Windows.Media.Ocr.OcrEngine[]? _engines;

    static Windows.Media.Ocr.OcrEngine[] Engines()
    {
        if (_engines != null) return _engines;
        var list = new List<Windows.Media.Ocr.OcrEngine>();
        foreach (var prefix in new[] { "en", "ru" })
            if (Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(l => l.LanguageTag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) is { } lang
                && Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(lang) is { } engine)
                list.Add(engine);
        if (list.Count == 0 && Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages() is { } own) list.Add(own);
        return _engines = list.ToArray();
    }

    /// <summary>
    /// Снимок <see cref="Area"/> → левый край ника в каждом слоте и что под слотом прочитано — по строке на каждое чтение
    /// (движок × увеличение). Край — по первому слову OCR (середина по чтениям); слов нет — <see cref="TopBarNames.TextLeft"/>:
    /// тот на пике цепляется за полоску «Стадия выбора» под таймером (слот 5: 1088 вместо 1112).
    /// </summary>
    public static async Task<(int[] Left, string[][] Texts)> ReadAsync(Bitmap area)
    {
        int[] left;
        using (var band = area.Clone(new Rectangle(0, TopBarNames.Band.Y - Area.Y, TopBarNames.Band.Width, TopBarNames.Band.Height), PixelFormat.Format24bppRgb))
            left = TopBarNames.TextLeft(band);
        var engines = Engines();
        if (engines.Length == 0) throw new InvalidOperationException("в Windows нет языка для распознавания текста (OCR)");
        var texts = Enumerable.Range(0, 10).Select(_ => new List<string>()).ToArray();
        var firstX = Enumerable.Range(0, 10).Select(_ => new List<double>()).ToArray();
        foreach (int scale in Scales)
        {
            using var big = new Bitmap(area.Width * scale, area.Height * scale, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(big))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(area, new Rectangle(0, 0, big.Width, big.Height));
            }
            using var png = new MemoryStream();
            big.Save(png, ImageFormat.Png);
            png.Position = 0;
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(png.AsRandomAccessStream());
            using var image = await decoder.GetSoftwareBitmapAsync();
            foreach (var engine in engines)
            {
                var words = new List<(int Slot, double X, string Text)>();
                foreach (var line in (await engine.RecognizeAsync(image)).Lines)
                    foreach (var w in line.Words)
                    {
                        double x = Area.X + w.BoundingRect.X / scale;
                        if (TopBarNames.SlotOf(x + w.BoundingRect.Width / scale / 2) is int slot) words.Add((slot, x, w.Text));
                    }
                for (int s = 0; s < 10; s++)
                {
                    var own = words.Where(w => w.Slot == s).OrderBy(w => w.X).ToList();
                    texts[s].Add(string.Join(" ", own.Select(w => w.Text)));
                    if (own.Count > 0) firstX[s].Add(own[0].X);
                }
            }
        }
        for (int s = 0; s < 10; s++)
            if (firstX[s].Count > 0)
            {
                var xs = firstX[s].OrderBy(x => x).ToList();
                left[s] = (int)Math.Round((xs[(xs.Count - 1) / 2] + xs[xs.Count / 2]) / 2);
            }
        return (left, texts.Select(t => t.ToArray()).ToArray());
    }

    /// <summary>Слот каждого ника (-1 — не найден): сначала самые похожие пары, слот и ник — не больше одного раза.</summary>
    public static int[] Match(IReadOnlyList<string> names, string[][] texts)
    {
        var pairs = new List<(double Score, int Name, int Slot)>();
        for (int n = 0; n < names.Count; n++)
            for (int s = 0; s < texts.Length; s++)
                if (texts[s].Select(t => Score(names[n], t)).DefaultIfEmpty(0).Max() is var best and >= MinScore)
                    pairs.Add((best, n, s));
        var slots = Enumerable.Repeat(-1, names.Count).ToArray();
        var taken = new bool[texts.Length];
        foreach (var (_, n, s) in pairs.OrderByDescending(p => p.Score))
            if (slots[n] < 0 && !taken[s])
                (slots[n], taken[s]) = (s, true);
        return slots;
    }

    /// <summary>Сходство ника Steam с прочитанным, 0–1. Короче трёх знаков — только точное совпадение.</summary>
    public static double Score(string name, string text)
    {
        int cut = text.IndexOf("...", StringComparison.Ordinal) is var dots and >= 0 ? dots : text.IndexOf('…');
        string a = Skeleton(name), b = Skeleton(cut >= 0 ? text[..cut] : text);
        if (a.Length == 0 || b.Length == 0) return 0;
        // дальше ника — тег клана; оборванный ник — сколько его видно
        if (b.Length > a.Length) b = b[..a.Length];
        else if (cut >= 0 && b.Length >= 3) a = a[..b.Length];
        if (a.Length < 3) return a == b ? 1 : 0;
        return 1 - (double)Distance(a, b) / Math.Max(a.Length, b.Length);
    }

    // Без регистра, пробелов и разделителей; кириллица, похожая на латиницу, и цифры, похожие на буквы, — к одному виду
    // (en читает «Генсек» как «reHceK», ru «Yaaa» — как «Уааа»).
    static string Skeleton(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char ch in s.ToLowerInvariant())
        {
            if (char.IsWhiteSpace(ch) || "._-'`,:;".Contains(ch)) continue;
            sb.Append(ch switch
            {
                'а' => 'a', 'в' => 'b', 'г' => 'r', 'е' or 'ё' => 'e', 'з' => '3', 'и' or 'й' => 'u', 'к' => 'k', 'м' => 'm',
                'н' => 'h', 'о' or '0' => 'o', 'п' => 'n', 'р' => 'p', 'с' => 'c', 'т' => 't', 'у' => 'y', 'х' => 'x', 'ь' => 'b',
                '1' or 'i' or '|' or '!' => 'l',
                _ => ch,
            });
        }
        return sb.ToString();
    }

    static int Distance(string a, string b)
    {
        var row = Enumerable.Range(0, b.Length + 1).ToArray();
        for (int i = 1; i <= a.Length; i++)
        {
            int diag = row[0];
            row[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int up = row[j];
                row[j] = Math.Min(Math.Min(row[j] + 1, row[j - 1] + 1), diag + (a[i - 1] == b[j - 1] ? 0 : 1));
                diag = up;
            }
        }
        return row[b.Length];
    }

    /// <summary>Для журнала: кто в каком слоте, кого не нашли и что прочитано в слотах без игрока.</summary>
    public static string Describe(IReadOnlyList<LivePlayer> players, string[][] texts)
    {
        var found = players.Where(p => p.Slot >= 0).OrderBy(p => p.Slot).ToList();
        var lost = players.Where(p => p.Slot < 0).Select(p => p.Name).ToList();
        var empty = Enumerable.Range(0, texts.Length).Where(s => found.All(p => p.Slot != s))
            .Select(s => $"{s}:«{texts[s].FirstOrDefault(t => t.Length > 0) ?? ""}»").ToList();
        return $"ники в верхней полосе: найдено {found.Count} из {players.Count} — " + string.Join(", ", found.Select(p => $"{p.Slot}:{p.Name}"))
               + (lost.Count > 0 ? "; не найдены: " + string.Join(", ", lost) : "")
               + (empty.Count > 0 ? "; слоты без игрока: " + string.Join(", ", empty) : "");
    }
}
