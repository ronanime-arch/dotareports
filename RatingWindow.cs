using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DotaReports;

/// <summary>Что показывает окно рейтинга — отдельно от отрисовки, чтобы снимать окно на примерных данных (--ui-test).</summary>
sealed record RatingView(
    int LowPriority,
    string Medal,
    (int Games, int Wins, bool AtLeast)? Calibration,
    (int Percent, DateTime At)? Confidence,
    (int Games, int Wins, int RankedGames, int RankedWins)? Today,
    (int Count, bool Wins)? Streak,
    (int? Score, int? Delta, int Commends, int Reports)? Behavior,
    IReadOnlyList<int> Trend,
    (int Wins, int Losses)? Total,
    (int Negative, int Positive, bool NoReports)? Remaining,
    (int Hits, int Predicted)? Prediction,
    string Updated);

/// <summary>
/// Окно слева сверху по хоткею (рейтинг — Ctrl+Alt+S, лобби NEC — Ctrl+Alt+N): висит, пока не нажмёте ещё раз или ✕.
/// Клики проходят насквозь (кроме ↻ и ✕). Ширина прежняя — шире закрыло бы карточку первого слота на выборе героя.
/// </summary>
sealed class RatingWindow : GlassWindow
{
    readonly double _width;

    public RatingWindow(double width = 300)
    {
        _width = width;
        SizeToContent = SizeToContent.Height;
        Width = width;
    }

    double Inner => _width - 2 - 28;   // рамка и поля панели

    /// <summary>Шапка (заголовок, хоткей, ↻ и ✕), разделитель, тело, подвал.</summary>
    public void Render(string title, string? hotkey, IEnumerable<UIElement> body, string footer, Action? refresh)
    {
        ClearButtons();
        var panel = new StackPanel();
        panel.Children.Add(Glass.Header(this, title, 26, Glass.Ink, hotkey, refresh));
        panel.Children.Add(Glass.Divider(8, 6));
        foreach (var e in body) panel.Children.Add(e);
        panel.Children.Add(Glass.Text(footer, 11.5, Glass.Muted, top: 8));
        Content = Glass.Panel(panel, 16, new Thickness(14, 12, 14, 12));
    }

    /// <summary>Рейтинг по макету: плитки 2×2, поведение с графиком, строки итогов.</summary>
    public void RenderRating(RatingView v, Action? refresh)
    {
        var body = new List<UIElement>();
        if (v.LowPriority > 0)
            body.Add(Glass.Tile(Glass.Text($"Low priority: осталось {v.LowPriority} {Words.Plural(v.LowPriority, "игра", "игры", "игр")}", 14, Glass.Bad, bold: true)));

        var tiles = new UniformGrid { Columns = 2, Margin = new Thickness(-3, 0, -3, 0) };
        if (v.Calibration is { } cal)
        {
            tiles.Children.Add(Stat("На калибровке", $"{cal.Wins}–{cal.Games - cal.Wins}",
                $"{cal.Games}{(cal.AtLeast ? "+" : "")} {Words.Plural(cal.Games, "игра", "игры", "игр")} · {100.0 * cal.Wins / cal.Games:0}%"));
            tiles.Children.Add(v.Confidence is { } conf
                ? Stat("Уверенность", $"{conf.Percent}%", $"ранг до: {v.Medal}", ring: conf.Percent / 100.0)
                : Stat("Уверенность", "—", "откройте профиль в Dota"));
        }
        else
            tiles.Children.Add(Stat("Ранг", v.Medal, "", big: 20));
        if (v.Today is { } t)
            tiles.Children.Add(Stat("Сегодня", $"{t.Wins}–{t.Games - t.Wins}",
                $"{t.Games} {Words.Plural(t.Games, "игра", "игры", "игр")}" + (t.RankedGames > 0 ? $" · в рейтинге {t.RankedWins}–{t.RankedGames - t.RankedWins}" : "")));
        if (v.Streak is { } st)
            tiles.Children.Add(Stat("Серия", $"{st.Count} {(st.Wins ? Words.Plural(st.Count, "победа", "победы", "побед") : Words.Plural(st.Count, "поражение", "поражения", "поражений"))}",
                "подряд", color: st.Wins ? Glass.Good : Glass.Bad, big: 20));
        body.Add(tiles);

        if (v.Behavior is { } b)
        {
            var box = new StackPanel();
            var head = new DockPanel();
            var right = Glass.Text($"похвал {b.Commends} · жалоб {b.Reports}", 12, Glass.Muted, wrap: false);
            DockPanel.SetDock(right, Dock.Right);
            head.Children.Add(right);
            head.Children.Add(Glass.Text("Поведение", 12, Glass.Muted, wrap: false));
            box.Children.Add(head);
            var score = new WrapPanel();
            score.Children.Add(Glass.Text(b.Score?.ToString("N0") ?? "—", 30, Glass.Ink, serif: true, bold: true, wrap: false));
            if (b.Delta is int d && d != 0)
            {
                var chip = Glass.Chip($"{d:+#;−#}", d > 0 ? Glass.Good : Glass.Bad, 12.5);
                chip.Margin = new Thickness(8, 10, 0, 0);
                chip.VerticalAlignment = VerticalAlignment.Center;
                score.Children.Add(chip);
            }
            box.Children.Add(score);
            if (v.Trend.Count >= 2)
            {
                box.Children.Add(Chart(v.Trend, Inner - 20, 44));
                box.Children.Add(Glass.Text($"за {v.Trend.Count} {Words.Plural(v.Trend.Count, "сводку", "сводки", "сводок")}: {v.Trend[0]:N0} → {v.Trend[^1]:N0}", 11.5, Glass.Muted, top: 2));
            }
            body.Add(Glass.Tile(box, top: 6));
        }

        var rows = new List<(string, string, Color)>();
        if (v.Total is { } tot && tot.Wins + tot.Losses > 0)
            rows.Add(("Всего", $"{tot.Wins:N0}–{tot.Losses:N0} · {100.0 * tot.Wins / (tot.Wins + tot.Losses):0.0}%", Glass.Ink));
        if (v.Remaining is { } rem)
            rows.Add(("Можно отправить", $"жалоб {rem.Negative} · похвал {rem.Positive}", rem.NoReports ? Glass.Bad : Glass.Ink));
        if (v.Prediction is { } pr && pr.Predicted >= 5)
            rows.Add(("Прогноз угадал", $"{pr.Hits} из {pr.Predicted} · {100.0 * pr.Hits / pr.Predicted:0}%", Glass.Ink));
        if (rows.Count > 0) body.Add(Table(rows));

        Render("Рейтинг", "Ctrl+Alt+S", body, v.Updated, refresh);
    }

    // Плитка с подписью, крупным числом антиквой и строкой под ним; ring — доля для кольца слева (уверенность в ранге).
    static UIElement Stat(string label, string value, string note, Color? color = null, double big = 28, double? ring = null)
    {
        var text = new StackPanel();
        text.Children.Add(Glass.Text(label, 12, Glass.Muted, wrap: false));
        text.Children.Add(Glass.Text(value, big, color ?? Glass.Ink, serif: true, bold: true, wrap: false));
        if (note.Length > 0) text.Children.Add(Glass.Text(note, 11, Glass.Muted));
        UIElement content = text;
        if (ring is double f)
        {
            var dock = new DockPanel();
            var r = Ring(f, 34);
            DockPanel.SetDock(r, Dock.Left);
            dock.Children.Add(r);
            dock.Children.Add(text);
            content = dock;
        }
        var tile = Glass.Tile(content, padding: new Thickness(10, 7, 8, 8));
        tile.Margin = new Thickness(3, 0, 3, 6);
        return tile;
    }

    // Кольцо доли: тусклая окружность и золотая дуга от верха по часовой.
    static UIElement Ring(double fraction, double size)
    {
        fraction = Math.Clamp(fraction, 0, 0.999);
        double r = size / 2 - 3, c = size / 2;
        var canvas = new Canvas { Width = size, Height = size, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        canvas.Children.Add(new Ellipse { Width = 2 * r, Height = 2 * r, Stroke = Glass.B(0x1F, 0xFF, 0xF0, 0xDC), StrokeThickness = 4, Margin = new Thickness(3) });
        double angle = fraction * 2 * Math.PI;
        var end = new Point(c + r * Math.Sin(angle), c - r * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = new Point(c, c - r) };
        figure.Segments.Add(new ArcSegment(end, new Size(r, r), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        canvas.Children.Add(new Path
        {
            Data = new PathGeometry(new[] { figure }), Stroke = Glass.B(Glass.Gold), StrokeThickness = 4,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        });
        return canvas;
    }

    // Ломаная по баллам поведения с заливкой под ней; последняя точка — кружок цвета последнего изменения.
    static UIElement Chart(IReadOnlyList<int> values, double w, double h)
    {
        const double pad = 4;
        int min = values.Min(), max = values.Max();
        double range = Math.Max(max - min, 1);
        var pts = values.Select((v, i) => new Point(pad + i * (w - 2 * pad) / (values.Count - 1),
            max == min ? h / 2 : pad + (max - v) / range * (h - 2 * pad))).ToList();
        var canvas = new Canvas { Width = w, Height = h, Margin = new Thickness(0, 6, 0, 0) };
        var fill = new PathFigure { StartPoint = new Point(pts[0].X, h), IsClosed = true };
        foreach (var p in pts) fill.Segments.Add(new LineSegment(p, false));
        fill.Segments.Add(new LineSegment(new Point(pts[^1].X, h), false));
        canvas.Children.Add(new Path { Data = new PathGeometry(new[] { fill }), Fill = Glass.B(0x24, 0xD2, 0xA1, 0x5A) });
        canvas.Children.Add(new Polyline
        {
            Points = new PointCollection(pts), Stroke = Glass.B(Glass.Gold), StrokeThickness = 2.2,
            StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
        });
        var last = new Ellipse
        {
            Width = 9, Height = 9, StrokeThickness = 2, Stroke = Glass.B(Glass.Dark),
            Fill = Glass.B(values[^1] >= values[^2] ? Glass.Good : Glass.Bad),
        };
        Canvas.SetLeft(last, pts[^1].X - 4.5);
        Canvas.SetTop(last, pts[^1].Y - 4.5);
        canvas.Children.Add(last);
        return canvas;
    }

    // Строки «подпись — значение» на общей плитке, с тонкими разделителями.
    static UIElement Table(List<(string Key, string Value, Color Color)> rows)
    {
        var stack = new StackPanel();
        for (int i = 0; i < rows.Count; i++)
        {
            var row = new DockPanel { Margin = new Thickness(0, i == 0 ? 0 : 6, 0, 0) };
            var value = Glass.Text(rows[i].Value, 12.5, rows[i].Color, wrap: false);
            value.FontWeight = FontWeights.SemiBold;
            DockPanel.SetDock(value, Dock.Right);
            row.Children.Add(value);
            row.Children.Add(Glass.Text(rows[i].Key, 12.5, Glass.Muted, wrap: false));
            stack.Children.Add(row);
            if (i < rows.Count - 1)
                stack.Children.Add(new Rectangle { Height = 1, Fill = Glass.B(0x1A, 0xE8, 0xC8, 0x96), Margin = new Thickness(0, 6, 0, 0) });
        }
        return Glass.Tile(stack, top: 6);
    }

    public void Toggle()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + 20;
        Top = wa.Top + 130;
        Show();
    }
}
