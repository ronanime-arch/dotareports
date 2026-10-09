using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace DotaReports;

/// <summary>
/// Карточка пика у правого края экрана (x ≥ 1720 — правее сетки и слотов верхней полосы,
/// чтобы не попадать в снимки экрана). Клики проходят насквозь, фокус не забирает.
/// </summary>
sealed class TurboCard : Window
{
    const int PoolRows = 5;
    const int MetaRows = 5;
    const int MinPoolGames = 2;

    static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2));
    static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB0));
    static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x7C, 0xD9, 0x92));
    static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x57));
    static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x47));

    readonly StackPanel _panel = new();

    public TurboCard()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Height;
        Width = 190;
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x14, 0x18, 0x21)),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 8, 10, 8),
            Child = _panel,
        };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE,
                ex | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_LAYERED);
        };
    }

    /// <summary>Для --card-test: весь текст карточки построчно.</summary>
    public string DumpText() => string.Join(Environment.NewLine, _panel.Children.OfType<UIElement>().Select(e => e switch
    {
        TextBlock t => new System.Windows.Documents.TextRange(t.ContentStart, t.ContentEnd).Text,
        StackPanel sp => string.Join("  ", sp.Children.OfType<TextBlock>().Select(t => new System.Windows.Documents.TextRange(t.ContentStart, t.ContentEnd).Text)),
        _ => e.ToString() ?? "",
    }));

    public void ShowCard()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 6;
        Top = wa.Top + 120;
        if (!IsVisible) Show();
    }

    static readonly string[] RoleNames = { "любая", "лёгкая", "центр", "сложная", "поддержка", "полная поддержка" };

    public void Render(DraftData data, PickSnapshot snap, string recognition)
    {
        bool allPick = snap.Mode == "ALL_PICK";
        string N(IEnumerable<int> ids) => string.Join(", ", ids.Select(id => data.Heroes.GetValueOrDefault(id, $"#{id}")));

        _panel.Children.Clear();
        _panel.Children.Add(Text(allPick ? "Пик · All Pick" : "Пик · Турбо", 15, Fg, bold: true));
        _panel.Children.Add(Text("роль: " + RoleNames[Math.Clamp(snap.Position, 0, 5)], 11, Dim));

        // в All Pick вражеские пики видны всем, в Турбо — только засвеченные при конфликте выбора
        if (snap.Enemies.Count > 0)
        {
            _panel.Children.Add(Text((allPick ? "Враги: " : "Засвечены у врага: ") + N(snap.Enemies), 12, Accent, top: 4));
            // ваш счёт против этих героев за всё время (Dota Plus, GCPD)
            if (data.Profile is { Loaded: true } prof)
            {
                var vs = snap.Enemies.Where(e => prof.AgainstHero.TryGetValue(e, out var r) && r.Wins + r.Losses >= 5)
                    .Select(e => { var r = prof.AgainstHero[e]; return $"{data.Heroes.GetValueOrDefault(e)} {100.0 * r.Wins / (r.Wins + r.Losses):0}%"; });
                var line = string.Join(", ", vs);
                if (line.Length > 0) _panel.Children.Add(Text("ваши победы против них: " + line, 10, Dim));
            }
        }
        if (snap.Allies.Count > 0)
            _panel.Children.Add(Text("Союзники: " + N(snap.Allies), 12, Good, top: 4));
        if (snap.Planned.Count > 0)
            _panel.Children.Add(Text("Собираются взять: " + N(snap.Planned), 12, Good, top: 2));

        // Предвыбор союзников считаем их героями: учитываем сочетание и не предлагаем.
        var team = snap.Allies.Concat(snap.Planned).ToList();
        var blocked = new HashSet<int>(snap.Unavailable.Concat(snap.Planned));
        var picks = data.TurboPicks(snap.Mode, snap.Position, blocked, snap.Enemies, team);
        bool Playable(TurboPick p) => data.Pool.TryGetValue(p.HeroId, out var g) && g.Games >= MinPoolGames;
        double Score(TurboPick p) => p.Total + data.PersonalBonus(p.HeroId);

        // Главное: кого брать. Из ваших героев (не «редко на роли») — с лучшим итогом, включая вашу игру на нём.
        var best = picks.Where(p => !p.Rare && Playable(p)).OrderByDescending(Score).FirstOrDefault()
                   ?? picks.FirstOrDefault(p => !p.Rare);
        var bestAny = picks.FirstOrDefault(p => !p.Rare);
        if (best != null)
        {
            _panel.Children.Add(Text("Бери: " + data.Heroes.GetValueOrDefault(best.HeroId, $"#{best.HeroId}"), 17, Good, bold: true, top: 6));
            var why = new List<string> { $"{best.Winrate:0.0}% " + (snap.Position > 0 ? "на роли" : allPick ? "в All Pick" : "в Турбо") };
            if (Math.Abs(best.VsRevealed) >= 0.05) why.Add($"против врагов {best.VsRevealed:+0.0;-0.0}");
            if (Math.Abs(best.WithAllies) >= 0.05) why.Add($"с союзниками {best.WithAllies:+0.0;-0.0}");
            double personal = data.PersonalBonus(best.HeroId);
            if (Math.Abs(personal) >= 0.05) why.Add($"ваша игра {personal:+0.0;-0.0}");
            _panel.Children.Add(Text(string.Join(" · ", why), 11, Dim));
            if (bestAny != null && bestAny.HeroId != best.HeroId && bestAny.Total - best.Total >= 1.5)
                _panel.Children.Add(Text($"по статистике сильнее {data.Heroes.GetValueOrDefault(bestAny.HeroId)} ({bestAny.Winrate:0.0}% {bestAny.VsRevealed + bestAny.WithAllies:+0.0;-0.0}), но вы на нём почти не играете", 11, Dim, top: 2));
        }

        var mine = picks.Where(Playable).OrderBy(p => p.Rare).ThenByDescending(Score).Take(PoolRows).ToList();
        _panel.Children.Add(Text("Ваши герои", 12, Dim, top: 6));
        if (mine.Count == 0) _panel.Children.Add(Text("нет доступных", 12, Dim));
        foreach (var p in mine)
        {
            var g = data.Pool[p.HeroId];
            _panel.Children.Add(Row(data, p, $"у вас {g.Games} игр, {100.0 * g.Wins / g.Games:0}%" + (p.Rare ? " · редко на этой роли" : "")));
        }

        _panel.Children.Add(Text(snap.Position > 0 ? $"Сильные на роли «{RoleNames[snap.Position]}»" : (allPick ? "Сильные в All Pick" : "Сильные в Турбо"), 12, Dim, top: 6));
        foreach (var p in picks.Where(p => !p.Rare && !mine.Contains(p)).Take(MetaRows))
            _panel.Children.Add(Row(data, p, null));

        _panel.Children.Add(Text(recognition, 10, Dim, top: 6));
        var notes = data.TurboLabel + (snap.Enemies.Count + snap.Allies.Count > 0 ? "; поправка за союзников и врагов — по всем режимам" : "");
        _panel.Children.Add(Text(notes, 10, Dim, top: 2));
    }

    UIElement Row(DraftData data, TurboPick p, string? note)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 2, 0, 0) };
        var line = new TextBlock { FontSize = 13, Foreground = Fg, TextTrimming = TextTrimming.CharacterEllipsis };
        line.Inlines.Add(data.Heroes.GetValueOrDefault(p.HeroId, $"#{p.HeroId}") + "  ");
        line.Inlines.Add(new System.Windows.Documents.Run($"{p.Winrate:0.0}%") { Foreground = p.Winrate >= 50 ? Good : Bad, FontWeight = FontWeights.SemiBold });
        // поправка за союзников и засвеченных врагов, в процентных пунктах
        double bonus = p.VsRevealed + p.WithAllies;
        if (Math.Abs(bonus) >= 0.05)
            line.Inlines.Add(new System.Windows.Documents.Run($" {bonus:+0.0;-0.0}") { Foreground = bonus > 0 ? Good : Bad });
        sp.Children.Add(line);
        if (note != null) sp.Children.Add(Text(note, 10, Dim));
        return sp;
    }

    static TextBlock Text(string text, double size, Brush color, bool bold = false, double top = 0) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = size,
        Foreground = color,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, top, 0, 0),
    };
}
