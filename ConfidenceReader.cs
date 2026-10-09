using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;

namespace DotaReports;

/// <summary>
/// «Уверенность в ранге» (калибровка) из профиля Dota: полоса справа вверху — тёмная рамка, жёлтая заливка слева,
/// остальное коричневое (72, 59, 30). Процент = доля жёлтого: на скриншоте 33 из 223 px = 15%, как пишет сама Dota.
/// Valve не отдаёт это число ни в GCPD, ни в API — только на экране профиля.
/// </summary>
static class ConfidenceReader
{
    /// <summary>Где искать полосу на экране 1920×1080 (с запасом по высоте и сдвигу).</summary>
    public static readonly Rectangle ScreenArea = new(1340, 60, 320, 270);

    public static int? Read(Bitmap bmp)
    {
        // Полоса ~20 px высотой: доверяем, если её нашли хотя бы в 3 строках подряд; крайние строки сглажены
        // и короче на пиксель, поэтому берём самую длинную заливку (иначе 14% вместо 15%).
        int run = 0, best = -1;
        for (int y = 0; y < bmp.Height; y++)
        {
            if (RowPercent(bmp, y) is { } p && (run == 0 || Math.Abs(p - best) <= 2))
            {
                run++;
                best = Math.Max(best, p);
            }
            else if (run >= 3) return best;
            else { run = 0; best = -1; }
        }
        return run >= 3 ? best : null;
    }

    static int? RowPercent(Bitmap bmp, int y)
    {
        for (int x0 = 1; x0 < bmp.Width - 240; x0++)
        {
            if (!Dark(bmp.GetPixel(x0 - 1, y)) || Dark(bmp.GetPixel(x0, y))) continue;   // левая рамка → начало полосы
            int end = -1;
            for (int x = x0 + 205; x < Math.Min(bmp.Width, x0 + 240); x++)
                if (Dark(bmp.GetPixel(x, y))) { end = x; break; }
            if (end < 0) continue;
            int len = end - x0;
            if (len < 215 || len > 232) continue;   // полоса 223 px
            // между тёмной рамкой и заливкой — ободок до 3 px (на скриншоте рамка до 1385, жёлтое с 1388)
            int start = x0;
            while (start < x0 + 3 && !Yellow(bmp.GetPixel(start, y))) start++;
            int k = start;
            while (k < end && Yellow(bmp.GetPixel(k, y))) k++;
            int brown = 0, rest = end - k;
            for (int x = k; x < end; x++) if (Brown(bmp.GetPixel(x, y))) brown++;
            // жёлтая заливка обязательна (без неё похожих коричневых полос в интерфейсе много),
            // остаток полосы — коричневый, кроме белых цифр процента
            if (k - start < 2 || (rest > 0 && brown < rest * 0.8)) continue;
            return (int)Math.Round(100.0 * (k - start) / (end - start));
        }
        return null;
    }

    static bool Dark(Color c) => Math.Max(c.R, Math.Max(c.G, c.B)) < 60;
    static bool Yellow(Color c) => c.R > 200 && c.G > 140 && c.B < 140;
    static bool Brown(Color c) => Math.Abs(c.R - 72) < 30 && Math.Abs(c.G - 59) < 30 && Math.Abs(c.B - 30) < 25;

    public static Bitmap CaptureScreen()
    {
        var bmp = new Bitmap(ScreenArea.Width, ScreenArea.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(ScreenArea.Location, Point.Empty, ScreenArea.Size);
        return bmp;
    }

    public static (int Percent, DateTime At)? LoadLast()
    {
        try
        {
            if (!File.Exists(Paths.RankConfidence)) return null;
            var parts = File.ReadAllText(Paths.RankConfidence).Split('|');
            return (int.Parse(parts[0]), DateTime.Parse(parts[1], CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or FormatException or IndexOutOfRangeException)
        {
            return null;
        }
    }

    public static void Save(int percent) =>
        File.WriteAllText(Paths.RankConfidence, $"{percent}|{DateTime.Now.ToString("s", CultureInfo.InvariantCulture)}");
}
