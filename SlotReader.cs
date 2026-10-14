using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

namespace DotaReports;

/// <summary>
/// Узнаёт героя в слоте верхней полосы по официальному портрету Valve (portraits\{id}.png, 256×144).
/// Калибровка (topbar_calib.py): область слота x+4..x+104, y 2..58 ↔ портрет x 40..240, y 0..112;
/// 12 из 13 известных пиков узнаны первыми из 127. Полупрозрачный предвыбор союзника узнаётся, если он не слишком бледный.
/// Не узнаются особые облики героев (персоны, арканы) — у них другой портрет.
/// </summary>
sealed class SlotReader
{
    const int VW = 32, VH = 18;
    const int SlotDx = 4, SlotY = 2, SlotW = 100, SlotH = 56;
    static readonly Rectangle PortraitCrop = new(40, 0, 200, 112);
    // Пустой слот: лучший до ~0,52, но отрыв от второго ≤ 0,07; узнанный герой: 0,43–0,81 с отрывом 0,08–0,35.
    const double MinScore = 0.40, MinGap = 0.08;

    readonly Dictionary<int, double[]> _portraits = new();

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
                _portraits[hero] = Normalize(BoxResize((x, y) =>
                {
                    var c = bmp.GetPixel(PortraitCrop.X + x, PortraitCrop.Y + y);
                    return (c.R, c.G, c.B);
                }, PortraitCrop.Width, PortraitCrop.Height));
            }
            catch (ArgumentException ex)
            {
                Log.Write($"Портрет {file} не прочитан: {ex.Message}");
            }
        }
    }

    /// <summary>Герой в слоте (x — начало цветной полоски слота) или 0, если не узнан уверенно.</summary>
    public (int Hero, double Score, double Gap) Read(ScreenFrame f, int slotX)
    {
        if (!Enabled) return (0, 0, 0);
        var v = Look(f, slotX);
        int best = 0;
        double s1 = double.MinValue, s2 = double.MinValue;
        foreach (var (hero, p) in _portraits)
        {
            double s = Dot(v, p);
            if (s > s1) { s2 = s1; s1 = s; best = hero; }
            else if (s > s2) s2 = s;
        }
        return s1 >= MinScore && s1 - s2 >= MinGap ? (best, s1, s1 - s2) : (0, s1, s1 - s2);
    }

    /// <summary>Совпадение слота с портретами только этих героев, без порогов.</summary>
    public List<(int Hero, double Score)> Scores(ScreenFrame f, int slotX, IEnumerable<int> heroes)
    {
        var result = new List<(int, double)>();
        if (!Enabled) return result;
        var v = Look(f, slotX);
        foreach (var hero in heroes)
            if (_portraits.TryGetValue(hero, out var p)) result.Add((hero, Dot(v, p)));
        return result;
    }

    static double[] Look(ScreenFrame f, int slotX) => Normalize(BoxResize((x, y) => f.At(slotX + SlotDx + x, SlotY + y), SlotW, SlotH));

    static double Dot(double[] v, double[] p)
    {
        double s = 0;
        for (int i = 0; i < v.Length; i++) s += v[i] * p[i];
        return s / v.Length;
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

    static double[] Normalize(double[] v)
    {
        double m = v.Average();
        double sd = Math.Sqrt(v.Sum(a => (a - m) * (a - m)) / v.Length);
        if (sd < 1e-6) sd = 1;
        return v.Select(a => (a - m) / sd).ToArray();
    }
}
