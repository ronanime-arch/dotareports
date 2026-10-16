using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace DotaReports;

/// <summary>
/// Блок «ПОЗИЦИИ ВРАГА» на экране героя (после вашего пика): иконки уже известных вражеских героев в красной подсветке,
/// пять ячеек по порядку вражеских слотов, центры x = 1152 + 40·k, y = 353 (1920×1080).
/// Сравнение с официальными иконками Valve (icons\{id}.png, 32×32 RGBA) по непрозрачной части, сдвиг ±2 px.
/// На скриншотах: AA, Skywrath, Drow, Invoker, Venomancer, Terrorblade, SF — узнаны с отрывом 0,07–0,33;
/// у пустой ячейки отрыв ≤ 0,02 и нет красной подсветки.
/// </summary>
sealed class EnemyIconReader
{
    const int Size = 28, X0 = 1152, StepX = 40, Y = 353;
    const double MinScore = 0.55, MinGap = 0.05;

    readonly Dictionary<int, (double[] Rgb, int N)> _icons = new();

    public bool Enabled => _icons.Count > 0;

    public EnemyIconReader()
    {
        if (!Directory.Exists(Paths.Icons)) return;
        foreach (var file in Directory.GetFiles(Paths.Icons, "*.png"))
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out var hero)) continue;
            try
            {
                using var src = new Bitmap(file);
                using var bmp = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(src, 0, 0, Size, Size);
                }
                // непрозрачные пиксели иконки; на месте прозрачных — NaN (не сравниваем)
                var rgb = new double[Size * Size * 3];
                int n = 0;
                for (int y = 0; y < Size; y++)
                    for (int x = 0; x < Size; x++)
                    {
                        var c = bmp.GetPixel(x, y);
                        int i = (y * Size + x) * 3;
                        if (c.A > 200) { rgb[i] = c.R; rgb[i + 1] = c.G; rgb[i + 2] = c.B; n++; }
                        else rgb[i] = rgb[i + 1] = rgb[i + 2] = double.NaN;
                    }
                if (n >= 20) _icons[hero] = (rgb, n);
            }
            catch (ArgumentException ex)
            {
                Log.Write($"Иконка {file} не прочитана: {ex.Message}");
            }
        }
    }

    /// <summary>Герои в ячейках блока «ПОЗИЦИИ ВРАГА» по порядку вражеских слотов; 0 — ячейка пуста или не узнана.</summary>
    public int[] Read(ScreenFrame f)
    {
        var found = new int[5];
        if (!Enabled) return found;
        for (int k = 0; k < 5; k++)
        {
            int cx = X0 + StepX * k;
            if (!RedGlow(f, cx, Y)) continue;
            int best = 0;
            double s1 = double.MinValue, s2 = double.MinValue;
            foreach (var (hero, icon) in _icons)
            {
                double s = double.MinValue;
                for (int dy = -2; dy <= 2; dy += 2)
                    for (int dx = -2; dx <= 2; dx += 2)
                        s = Math.Max(s, Ncc(f, cx + dx - Size / 2, Y + dy - Size / 2, icon.Rgb));
                if (s > s1) { s2 = s1; s1 = s; best = hero; }
                else if (s > s2) s2 = s;
            }
            if (s1 >= MinScore && s1 - s2 >= MinGap) found[k] = best;
        }
        return found;
    }

    // Красная подсветка вокруг иконки врага: точки на окружности радиуса 15 заметно краснее остального.
    static bool RedGlow(ScreenFrame f, int cx, int cy)
    {
        int red = 0;
        for (int a = 0; a < 12; a++)
        {
            double ang = a * Math.PI / 6;
            var (r, g, b) = f.At(cx + (int)Math.Round(15 * Math.Cos(ang)), cy + (int)Math.Round(15 * Math.Sin(ang)));
            if (r - Math.Max(g, b) > 40) red++;
        }
        return red >= 5;
    }

    static double Ncc(ScreenFrame f, int x0, int y0, double[] icon)
    {
        double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
        int n = 0;
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                int i = (y * Size + x) * 3;
                if (double.IsNaN(icon[i])) continue;
                var (r, g, b) = f.At(x0 + x, y0 + y);
                foreach (var (a, c) in new[] { (r, icon[i]), (g, icon[i + 1]), (b, icon[i + 2]) })
                {
                    sa += a; sb += c; saa += a * a; sbb += c * c; sab += a * c; n++;
                }
            }
        double va = saa - sa * sa / n, vb = sbb - sb * sb / n;
        return va < 1e-6 || vb < 1e-6 ? 0 : (sab - sa * sb / n) / Math.Sqrt(va * vb);
    }
}
