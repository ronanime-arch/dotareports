using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

namespace DotaReports;

/// <summary>
/// Узнаёт героя в слоте верхней полосы по официальному портрету Valve (portraits\{id}.png, 256×144).
/// Калибровка (topbar_calib.py): область слота x+4..x+104, y 2..58 ↔ портрет x 40..240, y 0..112;
/// 12 из 13 известных пиков узнаны первыми из 127. Предвыбор союзника Dota рисует серым полупрозрачным портретом —
/// его сравниваем только по яркости (ReadGray). Если герой в полосе на портрет не похож (Shadow Fiend даже в обычном
/// облике, а также арканы и персоны), — свои эталоны: вырезка области слота 100×56 со скриншота, slots\{id}_{что}.png
/// (Shadow Fiend: 0,99 между двумя скриншотами, с другими героями того матча — не больше 0,26; Riki и Lich —
/// с другими слотами обоих скриншотов не больше 0,34).
/// </summary>
sealed class SlotReader
{
    const int VW = 32, VH = 18;
    const int SlotDx = 4, SlotY = 2, SlotW = 100, SlotH = 56;
    static readonly Rectangle PortraitCrop = new(40, 0, 200, 112);
    // Пустой слот: лучший до ~0,52, но отрыв от второго ≤ 0,07; узнанный герой: 0,43–0,81 с отрывом 0,08–0,35.
    const double MinScore = 0.40, MinGap = 0.08;
    // Поверх портрета Dota рисует плашку звания (рейтинговый All Pick: «Страж IV», экран y 45–62) и значок уровня героя
    // справа (x+84 и дальше, y от ~14) — одинаковые у разных героев, они только сбивали сравнение (Snapfire под «Страж IV»
    // не узнавалась). Сверху — цветная полоска игрока: у своего эталона слота она того же цвета, что у любого героя
    // в этом слоте, и давала +0,2–0,26 совпадения чужим героям (эталон Juggernaut из D4 «узнавался» в предвыборе D4).
    // Эти клетки не сравниваем ни у слота, ни у портрета: в координатах области слота (100×56) —
    // y < 6, y ≥ 43 и (x ≥ 80 при y ≥ 12).
    static readonly bool[] Covered = BuildCovered();
    static readonly int OpenCells = Covered.Count(c => !c);

    // у героя может быть несколько эталонов: портрет Valve и свои вырезки слотов
    readonly Dictionary<int, List<double[]>> _portraits = new();
    readonly Dictionary<int, List<double[]>> _gray = new();   // те же облики по яркости

    public bool Enabled => _portraits.Count > 0;

    public SlotReader()
    {
        if (!Directory.Exists(Paths.Portraits)) return;
        foreach (var file in Directory.GetFiles(Paths.Portraits, "*.png"))
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out var hero)) continue;
            try
            {
                using var bmp = new Bitmap(file);
                if (bmp.Width < PortraitCrop.Right || bmp.Height < PortraitCrop.Bottom) continue;
                var rgb = BoxResize((x, y) =>
                {
                    var c = bmp.GetPixel(PortraitCrop.X + x, PortraitCrop.Y + y);
                    return (c.R, c.G, c.B);
                }, PortraitCrop.Width, PortraitCrop.Height);
                Add(hero, rgb);
            }
            catch (ArgumentException ex)
            {
                Log.Write($"Портрет {file} не прочитан: {ex.Message}");
            }
        }
        // без портретов Valve один эталон узнавался бы в любом слоте (отрыв не до кого)
        if (_portraits.Count == 0) return;
        // и из папки данных, и из seed рядом с программой: в папку данных seed копируется уже после создания читателя
        var files = new[] { Paths.SlotRefs, Path.Combine(AppContext.BaseDirectory, "seed", "slots") }
            .Where(Directory.Exists).SelectMany(d => Directory.GetFiles(d, "*.png"))
            .GroupBy(f => Path.GetFileName(f)).Select(g => g.First());
        foreach (var file in files)
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(file).Split('_')[0], out var hero)) continue;
            try
            {
                using var bmp = new Bitmap(file);
                AddRef(hero, bmp);
            }
            catch (ArgumentException ex)
            {
                Log.Write($"Эталон слота {file} не прочитан: {ex.Message}");
            }
        }
    }

    /// <summary>Область слота на экране (x — начало цветной полоски слота).</summary>
    public static Rectangle Area(int slotX) => new(slotX + SlotDx, SlotY, SlotW, SlotH);

    /// <summary>Вид слота (вырезка 100×56) — ещё один эталон героя, сразу, без перезапуска.</summary>
    public void AddRef(int hero, Bitmap crop)
    {
        if (crop.Width != SlotW || crop.Height != SlotH) return;
        Add(hero, BoxResize((x, y) =>
        {
            var c = crop.GetPixel(x, y);
            return (c.R, c.G, c.B);
        }, SlotW, SlotH));
    }

    void Add(int hero, double[] rgb)
    {
        (_portraits.TryGetValue(hero, out var looks) ? looks : _portraits[hero] = new()).Add(Normalize(rgb, 3));
        (_gray.TryGetValue(hero, out var grays) ? grays : _gray[hero] = new()).Add(Normalize(Gray(rgb), 1));
    }

    /// <summary>Герой в слоте (x — начало цветной полоски слота) или 0, если не узнан уверенно.</summary>
    public (int Hero, double Score, double Gap) Read(ScreenFrame f, int slotX) => Best(Look(f, slotX), _portraits);

    /// <summary>То же только по яркости — для серого предвыбора союзника: цвет там только мешает.</summary>
    public (int Hero, double Score, double Gap) ReadGray(ScreenFrame f, int slotX) => Best(Normalize(Gray(Raw(f, slotX)), 1), _gray);

    // отрыв — до лучшего другого героя: облики одного героя друг другу не соперники
    (int Hero, double Score, double Gap) Best(double[] v, Dictionary<int, List<double[]>> portraits)
    {
        if (!Enabled) return (0, 0, 0);
        int best = 0;
        double s1 = double.MinValue, s2 = double.MinValue;
        foreach (var (hero, looks) in portraits)
        {
            double s = looks.Max(p => Dot(v, p));
            if (s > s1) { s2 = s1; s1 = s; best = hero; }
            else if (s > s2) s2 = s;
        }
        return s1 >= MinScore && s1 - s2 >= MinGap ? (best, s1, s1 - s2) : (0, s1, s1 - s2);
    }

    /// <summary>
    /// Насыщенность цвета в слоте — среднее (max − min) по каналам. На скриншоте пика: серый предвыбор 28,
    /// пустые слоты 25–28, взятые герои 65–152.
    /// </summary>
    public static double Saturation(ScreenFrame f, int slotX)
    {
        double sum = 0;
        int n = 0;
        for (int y = SlotY; y < SlotY + SlotH; y += 2)
            for (int x = slotX + SlotDx; x < slotX + SlotDx + SlotW; x += 2)
            {
                var (r, g, b) = f.At(x, y);
                sum += Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b));
                n++;
            }
        return sum / n;
    }

    /// <summary>Совпадение слота с портретами только этих героев, без порогов.</summary>
    public List<(int Hero, double Score)> Scores(ScreenFrame f, int slotX, IEnumerable<int> heroes)
    {
        var result = new List<(int, double)>();
        if (!Enabled) return result;
        var v = Look(f, slotX);
        foreach (var hero in heroes)
            if (_portraits.TryGetValue(hero, out var looks)) result.Add((hero, looks.Max(p => Dot(v, p))));
        return result;
    }

    static double[] Raw(ScreenFrame f, int slotX) => BoxResize((x, y) => f.At(slotX + SlotDx + x, SlotY + y), SlotW, SlotH);

    static double[] Look(ScreenFrame f, int slotX) => Normalize(Raw(f, slotX), 3);

    // RGB по клеткам → яркость клетки
    static double[] Gray(double[] rgb)
    {
        var v = new double[rgb.Length / 3];
        for (int i = 0; i < v.Length; i++) v[i] = (rgb[3 * i] + rgb[3 * i + 1] + rgb[3 * i + 2]) / 3;
        return v;
    }

    // Корреляция по открытым клеткам: у закрытых в обоих векторах нули.
    static double Dot(double[] v, double[] p)
    {
        double s = 0;
        for (int i = 0; i < v.Length; i++) s += v[i] * p[i];
        return s / (OpenCells * (v.Length / Covered.Length));
    }

    static bool[] BuildCovered()
    {
        var covered = new bool[VW * VH];
        for (int oy = 0; oy < VH; oy++)
            for (int ox = 0; ox < VW; ox++)
            {
                // середина клетки в координатах области слота
                double x = (ox + 0.5) * SlotW / VW, y = (oy + 0.5) * SlotH / VH;
                covered[oy * VW + ox] = y < 6 || y >= 43 || (x >= 80 && y >= 12);
            }
        return covered;
    }

    // Уменьшение до VW×VH усреднением по прямоугольникам (одинаково для экрана и портретов).
    static double[] BoxResize(Func<int, int, (int R, int G, int B)> px, int w, int h)
    {
        var v = new double[VW * VH * 3];
        for (int oy = 0; oy < VH; oy++)
        {
            int y0 = oy * h / VH, y1 = Math.Max(y0 + 1, (oy + 1) * h / VH);
            for (int ox = 0; ox < VW; ox++)
            {
                int x0 = ox * w / VW, x1 = Math.Max(x0 + 1, (ox + 1) * w / VW);
                double r = 0, g = 0, b = 0;
                int n = 0;
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++)
                    {
                        var c = px(x, y);
                        r += c.R; g += c.G; b += c.B; n++;
                    }
                int i = (oy * VW + ox) * 3;
                v[i] = r / n; v[i + 1] = g / n; v[i + 2] = b / n;
            }
        }
        return v;
    }

    // Среднее и разброс — только по открытым клеткам (channels значений на клетку: 3 — RGB, 1 — яркость); закрытые — нули.
    static double[] Normalize(double[] v, int channels)
    {
        bool IsOpen(int i) => !Covered[i / channels];
        double sum = 0, sq = 0;
        int n = 0;
        for (int i = 0; i < v.Length; i++)
            if (IsOpen(i)) { sum += v[i]; n++; }
        double m = sum / Math.Max(n, 1);
        for (int i = 0; i < v.Length; i++)
            if (IsOpen(i)) sq += (v[i] - m) * (v[i] - m);
        double sd = Math.Sqrt(sq / Math.Max(n, 1));
        if (sd < 1e-6) sd = 1;
        var r = new double[v.Length];
        for (int i = 0; i < v.Length; i++) r[i] = IsOpen(i) ? (v[i] - m) / sd : 0;
        return r;
    }
}
