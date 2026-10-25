using System;
using System.Collections.Generic;
using System.Linq;

namespace DotaReports;

/// <summary>
/// Блок «ВЫБЕРИТЕ ЛИНИЮ» на экране героя (после вашего пика): иконки всей своей команды, в том числе выбравших после вас.
/// Иконки стоят по линиям, а не по слотам, и съезжают с каждым новым пиком (одна — x ≈ 1275, пять — 1155 + 46·k; y 232–261),
/// поэтому ячейки ищем по кадру: столбцы полосы, заметно отличные от фона панели. Ваша ячейка — на ярко-зелёной подложке:
/// её пропускаем, а без неё ряд не читаем (блока на экране нет или кадр затемнён).
/// Иконка бывает цветной (так её и сравниваем с icons\{id}.png — по яркости), а бывает зелёным силуэтом: его яркость повторяет
/// самый сильный канал иконки, а не её яркость, и без контура (9060067401: Slardar 0,79 против 0,43, Disruptor 0,69 против 0,34).
/// </summary>
sealed class AllyIconReader
{
    const int Size = 32, Y0 = 232, Y1 = 262, X0 = 1135, X1 = 1375, BgY = 227;
    // У вашей подложки 153–312 ярко-зелёных пикселей, у чужих ячеек на кадрах 0, у самых ярких иконок (Rubick, 108, 155) — до 52
    const int MinW = 14, MaxW = 40, FgDiff = 60, FgRows = 4, OwnGlow = 100;
    const double GreenShare = 0.3;   // зелёных среди отличных от фона: у силуэтов 0,43–0,97, у цветных иконок 0
    const int Shift = 3, LX0 = X0 - Size, LY0 = Y0 - Size / 2, LW = X1 - X0 + 2 * Size, LH = Y1 - Y0 + Size;
    // На записанных пиках и снимках свои герои — 0,65–0,90 с отрывом от второго 0,06–0,47 (силуэты Zeus 0,52 и Invoker
    // 0,62 при Morphling 0,61 — не узнаны); лучший чужой герой — до 0,66.
    const double MinScore = 0.6, MinGap = 0.05;

    /// <summary>
    /// Для --grid-test: кадры записаны до 25.10 и обрезаны по x = 1320 — ваша ячейка могла остаться за краем,
    /// тогда ряд читаем и без неё (ваш герой отсеивается по GSI).
    /// </summary>
    public static bool CutFrames;

    /// <summary>Для --grid-test: и вашу ячейку сравнивать с иконками — сверить, что подсвечен ваш герой (GSI).</summary>
    public static bool Verbose;

    // шаблоны: цветная иконка — яркость на всей непрозрачной части; силуэт — самый сильный канал без контура (NaN — не сравниваем)
    readonly Dictionary<int, (double[] Luma, double[] Value)> _icons = new();

    public bool Enabled => _icons.Count > 0;

    /// <summary>Для --grid-test: ячейки последнего прочитанного кадра (x, лучший и второй герой, «своя»).</summary>
    public string Debug { get; private set; } = "";

    public AllyIconReader()
    {
        foreach (var (hero, rgb) in EnemyIconReader.LoadIcons(Size))
        {
            var luma = new double[Size * Size];
            var value = new double[Size * Size];
            for (int y = 0; y < Size; y++)
                for (int x = 0; x < Size; x++)
                {
                    int i = y * Size + x;
                    double r = rgb[i * 3], g = rgb[i * 3 + 1], b = rgb[i * 3 + 2];
                    luma[i] = Luma(r, g, b); // NaN остаётся NaN
                    bool inner = true;
                    for (int dy = -1; dy <= 1 && inner; dy++)
                        for (int dx = -1; dx <= 1 && inner; dx++)
                        {
                            int xx = x + dx, yy = y + dy;
                            inner = xx >= 0 && yy >= 0 && xx < Size && yy < Size && !double.IsNaN(rgb[(yy * Size + xx) * 3]);
                        }
                    value[i] = inner ? Math.Max(r, Math.Max(g, b)) : double.NaN;
                }
            _icons[hero] = (luma, value);
        }
    }

    /// <summary>Герои своей команды в ряду, кроме вашего; пусто — ряда на экране нет.</summary>
    public List<int> Read(ScreenFrame f)
    {
        var found = new List<int>();
        Debug = "";
        if (!Enabled) return found;

        // фон панели — медиана строки над иконками (тёмно-синяя; сетка героев, загрузка и т. п. — не она)
        var row = Enumerable.Range(X0, X1 - X0).Select(x => f.At(x, BgY)).OrderBy(c => c.R + c.G + c.B).ToList();
        var bg = row[row.Count / 2];
        if (bg.R + bg.G + bg.B > 240 || bg.B < bg.R) return found;

        var cols = new List<(int A, int B)>();
        int start = -1;
        for (int x = X0; x <= X1; x++)
        {
            int fg = 0;
            if (x < X1)
                for (int y = Y0; y < Y1; y++)
                    if (Fg(f.At(x, y), bg)) fg++;
            if (fg >= FgRows && start < 0) start = x;
            else if (fg < FgRows && start >= 0) { cols.Add((start, x - 1)); start = -1; }
        }
        var cells = cols.Where(c => c.B - c.A + 1 is >= MinW and <= MaxW).ToList();
        var own = cells.Select(c => Glow(f, c.A, c.B)).ToList();
        int owns = own.Count(n => n >= OwnGlow);
        if (owns > 1 || owns == 0 && !(CutFrames && cells.Count > 0))
        {
            Debug = $"своей ячейки нет ({string.Join(" ", cells.Select((c, i) => $"{c.A}–{c.B}:{own[i]}"))})";
            return found;
        }

        var luma = new double[LW * LH];
        var value = new double[LW * LH];
        for (int y = 0; y < LH; y++)
            for (int x = 0; x < LW; x++)
            {
                var (r, g, b) = f.At(LX0 + x, LY0 + y);
                luma[y * LW + x] = Luma(r, g, b);
                value[y * LW + x] = Math.Max(r, Math.Max(g, b));
            }
        (int Best, double S1, int Second, double S2) Match(int cx, bool silhouette)
        {
            var frame = silhouette ? value : luma;
            int best = 0, second = 0;
            double s1 = double.MinValue, s2 = double.MinValue;
            foreach (var (hero, icon) in _icons)
            {
                var tpl = silhouette ? icon.Value : icon.Luma;
                double s = double.MinValue;
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -Shift; dx <= Shift; dx++)
                        s = Math.Max(s, Ncc(frame, cx + dx - Size / 2 - LX0, (Y0 + Y1) / 2 + dy - Size / 2 - LY0, tpl));
                if (s > s1) { s2 = s1; second = best; s1 = s; best = hero; }
                else if (s > s2) { s2 = s; second = hero; }
            }
            return (best, s1, second, s2);
        }

        var parts = new List<string>();
        for (int i = 0; i < cells.Count; i++)
        {
            var (a, b) = cells[i];
            int cx = (a + b + 1) / 2;
            if (own[i] >= OwnGlow)
            {
                // подложка зелёная — силуэт ли сама иконка, по доле зелёного не понять: в тесте пишем оба сравнения
                if (!Verbose) { parts.Add($"{cx}: своя"); continue; }
                var (hs, ss, _, _) = Match(cx, true);
                var (hc, sc, _, _) = Match(cx, false);
                parts.Add($"{cx}: своя (силуэт {hs} {ss:0.00}, цвет {hc} {sc:0.00})");
                continue;
            }
            bool silhouette = GreenPart(f, a, b, bg) >= GreenShare;
            var (best, s1, second, s2) = Match(cx, silhouette);
            bool ok = s1 >= MinScore && s1 - s2 >= MinGap;
            if (ok) found.Add(best);
            parts.Add($"{cx} {(silhouette ? "силуэт" : "цвет")}: {best} {s1:0.00} / {second} {s2:0.00}{(ok ? "" : " ✗")}");
        }
        Debug = (owns == 0 ? "своя за краем · " : "") + string.Join(" · ", parts);
        return found;
    }

    static bool Fg((int R, int G, int B) c, (int R, int G, int B) bg) =>
        Math.Abs(c.R - bg.R) + Math.Abs(c.G - bg.G) + Math.Abs(c.B - bg.B) > FgDiff;

    // Ярко-зелёная подложка вашей ячейки: пиксели, где зелёный намного сильнее красного и синего.
    static int Glow(ScreenFrame f, int a, int b)
    {
        int n = 0;
        for (int y = Y0 - 2; y < Y1 + 2; y++)
            for (int x = a; x <= b; x++)
            {
                var (r, g, bl) = f.At(x, y);
                if (g > 140 && g - Math.Max(r, bl) > 80) n++;
            }
        return n;
    }

    // Доля зелёных пикселей среди отличных от фона: у силуэта почти все, у цветной иконки мало.
    static double GreenPart(ScreenFrame f, int a, int b, (int R, int G, int B) bg)
    {
        int fg = 0, green = 0;
        for (int y = Y0; y < Y1; y++)
            for (int x = a; x <= b; x++)
            {
                var c = f.At(x, y);
                if (!Fg(c, bg)) continue;
                fg++;
                if (c.G - Math.Max(c.R, c.B) > 25) green++;
            }
        return fg == 0 ? 0 : (double)green / fg;
    }

    static double Luma(double r, double g, double b) => 0.299 * r + 0.587 * g + 0.114 * b;

    static double Ncc(double[] frame, int x0, int y0, double[] icon)
    {
        double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0;
        int n = 0;
        for (int y = 0; y < Size; y++)
            for (int x = 0; x < Size; x++)
            {
                double c = icon[y * Size + x];
                if (double.IsNaN(c)) continue;
                double a = frame[(y0 + y) * LW + x0 + x];
                sa += a; sb += c; saa += a * a; sbb += c * c; sab += a * c; n++;
            }
        double va = saa - sa * sa / n, vb = sbb - sb * sb / n;
        return va < 1e-6 || vb < 1e-6 ? 0 : (sab - sa * sb / n) / Math.Sqrt(va * vb);
    }
}
