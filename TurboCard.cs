using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DotaReports;

/// <summary>
/// Карточка пика у правого края экрана (x ≥ 1720 — правее сетки и слотов верхней полосы,
/// чтобы не попадать в снимки экрана). Клики проходят насквозь (кроме ↻ и ✕), фокус не забирает.
/// Закрыли ✕ — до следующего пика не появляется. Вид — «стекло» из макета; значки S/A/B/C — тир по винрейту.
/// </summary>
sealed class TurboCard : GlassWindow
{
    const int PoolRows = 5;
    const int MetaRows = 5;
    const int MinPoolGames = 2;
    const double DraftFitGap = 1.0; // «Под этот драфт» — если поправки за драфт лучше, чем у «Бери», хотя бы на 1 п.п.

    StackPanel _panel = new();

    /// <summary>Кого советует «Бери» в последней отрисовке; 0 — никого (вы уже выбрали или советовать не из кого).</summary>
    public int Recommended { get; private set; }

    /// <summary>Закрыта ✕ в этом пике — не показывать, пока не начнётся следующий (<see cref="ResetDismissed"/>).</summary>
    public bool Dismissed { get; private set; }

    /// <summary>Что делает ↻: перерисовать карточку пика или перечитать три героя на выбор.</summary>
    public Action? Refresh { get; set; }

    public TurboCard()
    {
        SizeToContent = SizeToContent.Height;
        Width = 190;
        CloseButtonPressed = () => Dismissed = true;
    }

    public void ResetDismissed() => Dismissed = false;

    /// <summary>Для --card-test: весь текст карточки построчно.</summary>
    public string DumpText()
    {
        var lines = new List<string>();
        void Walk(object node)
        {
            if (node is TextBlock t) lines.Add(t.Text);
            else if (node is DependencyObject d)
                foreach (var child in LogicalTreeHelper.GetChildren(d)) Walk(child);
        }
        Walk(Content);
        return string.Join(Environment.NewLine, lines.Where(l => l.Length > 0));
    }

    public void ShowCard()
    {
        if (Dismissed) return;
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 6;
        Top = wa.Top + 120;
        if (!IsVisible) Show();
    }

    /// <summary>
    /// В кастомке — широкой полосой под карточками игроков: они до y ≈ 820, кнопка «Повторный выбор героя» до 850,
    /// справа с x 1234 — чат. Ширина — до чата. Координаты — экрана, не рабочей области: панель задач Dota закрывает.
    /// </summary>
    public void ShowChoices()
    {
        if (Dismissed) return;
        Left = ChoicesLeft;
        Top = ChoicesTop; // выше не поднимаем — закроет кнопку; не влезает — ужата в RenderChoices
        if (!IsVisible) Show();
    }

    const double ChoicesLeft = 63, ChoicesTop = 856, ChoicesWidth = 1165;
    const double MinScale = 0.85; // мельче текст уже не прочитать

    /// <summary>Сколько полосе места под кнопкой «Повторный выбор героя» — до низа экрана.</summary>
    public static double ChoicesRoom => SystemParameters.PrimaryScreenHeight - ChoicesTop - 4;

    // Высота содержимого при ширине полосы — до показа окна.
    double ContentHeight()
    {
        Root.Measure(new Size(ChoicesWidth, double.PositiveInfinity));
        return Root.DesiredSize.Height;
    }

    void Begin(double width, double radius, Thickness padding)
    {
        Width = width;
        ClearButtons();
        _panel = new StackPanel();
        Content = Glass.Panel(_panel, radius, padding);
    }

    /// <summary>
    /// Кастомка с выбором из трёх: кого брать и почему — по вашим играм здесь, а пока их мало — в обычной Доте;
    /// по колонке на героя: ваши игры и что даёт Пробуждение (кастомка IM) на 0, 10 и 15 минуте.
    /// Длинные описания не влезают под кнопку «Повторный выбор героя» — плотнее, а не хватит — мельче (не ниже <see cref="MinScale"/>).
    /// </summary>
    public void RenderChoices(DraftData data, IReadOnlyList<int> heroes, string game, Func<int, List<string>[]?> awakening)
    {
        var (choices, byCustom) = CustomStats.Rank(data, game, heroes);
        RenderChoices(data, game, choices, byCustom, awakening);
    }

    /// <summary>Три героя уже оценены (<see cref="CustomStats.Rank"/>; в --ui-test — заданы руками, худший случай по высоте).</summary>
    public void RenderChoices(DraftData data, string game, List<CustomChoice> choices, bool byCustom, Func<int, List<string>[]?> awakening)
    {
        Recommended = choices[0].Hero;
        double room = ChoicesRoom;
        BuildChoices(data, game, choices, byCustom, awakening, compact: false);
        if (ContentHeight() <= room) return;
        BuildChoices(data, game, choices, byCustom, awakening, compact: true);
        // с масштабом текст переносится по ширине 1165/s — высота падает быстрее масштаба, поэтому подбираем шагами
        for (double s = 1; s > MinScale && ContentHeight() > room;)
        {
            s = Math.Max(MinScale, s - 0.025);
            Root.LayoutTransform = new ScaleTransform(s, s);
        }
    }

    // compact — шапка в одну строку, «здесь» и «в Доте» одной строкой, отступы меньше
    void BuildChoices(DraftData data, string game, List<CustomChoice> choices, bool byCustom, Func<int, List<string>[]?> awakening, bool compact)
    {
        Begin(ChoicesWidth, 16, compact ? new Thickness(12, 6, 10, 8) : new Thickness(14, 10, 12, 12));
        string Name(int id) => data.Heroes.GetValueOrDefault(id, $"#{id}");
        string basis = CustomStats.DecidedByVoice(choices) ? "по отзывам в мастерской Steam"
            : byCustom ? "по вашим играм здесь" + (game == CommunityPicks.Game && choices.Any(c => c.HereKnown > 0) ? ", исходы — из истории NEC" : "")
            : "по вашим играм в обычной Доте (здесь их пока меньше трёх)";
        string why = CustomStats.Why(choices, byCustom, Name);
        if (compact)
        {
            _panel.Children.Add(Glass.Header(this, "Бери " + Name(choices[0].Hero), 18, Glass.Ink, null, Refresh));
            _panel.Children.Add(Glass.Text($"{char.ToUpper(basis[0])}{basis[1..]} · почему: {why}", 12, Glass.Ink, top: 1));
        }
        else
        {
            _panel.Children.Add(Glass.Header(this, Name(choices[0].Hero), 24, Glass.Ink, null, Refresh, subtitle: "БЕРИ · " + basis));
            _panel.Children.Add(Glass.Text("Почему: " + why, 13, Glass.Ink, top: 4));
        }

        var columns = new Grid { Margin = new Thickness(-3, compact ? 4 : 6, -3, 0) };
        for (int i = 0; i < choices.Count; i++)
        {
            var c = choices[i];
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var col = new StackPanel();
            var title = Glass.Text(Name(c.Hero), compact ? 16 : 18, i == 0 ? Glass.Good : Glass.Ink, serif: true, bold: true);
            // отзывы в мастерской — в строке имени: строк у полосы под кнопкой в обрез
            if (c.Voice is { } v)
                title.Inlines.Add(new System.Windows.Documents.Run($"  просили ослабить: {v.People}, {v.Period}")
                    { FontFamily = Glass.Sans, FontSize = 11, FontWeight = FontWeights.Normal, Foreground = Glass.B(Glass.GoldLight) });
            col.Children.Add(title);
            if (compact) col.Children.Add(Glass.Text(c.Here + " · " + c.Usual, 11.5, Glass.Muted));
            else
            {
                col.Children.Add(Glass.Text(c.Here, 11.5, Glass.Muted));
                col.Children.Add(Glass.Text(c.Usual, 11.5, Glass.Muted));
            }
            if (awakening(c.Hero) is { } stages)
                for (int k = 0; k < stages.Length; k++)
                    if (stages[k].Count > 0)
                        col.Children.Add(Glass.Text($"{(k == 0 ? "сразу" : AwakeningInfo.StageMinutes[k] + " мин")}: {string.Join("; ", stages[k])}", 11.5, Glass.GoldLight, top: compact ? 1 : 3));
            var tile = Glass.Tile(col, padding: compact ? new Thickness(8, 4, 8, 5) : null);
            tile.Margin = new Thickness(3, 0, 3, 0);
            if (i == 0) // «Бери» — яркой рамкой, как прежде вокруг картинки на экране
            {
                tile.BorderBrush = Glass.B(PickFrame.FrameColor);
                tile.BorderThickness = new Thickness(2);
            }
            Grid.SetColumn(tile, i);
            columns.Children.Add(tile);
        }
        _panel.Children.Add(columns);
    }

    static readonly string[] RoleNames = { "любая", "лёгкая", "центр", "сложная", "поддержка", "полная поддержка" };

    /// <param name="ownHero">Ваш герой из GSI (0 — ещё не выбран): после пика карточка показывает прогноз и состав, а не что брать.</param>
    public void Render(DraftData data, PickSnapshot snap, string recognition, int ownHero = 0)
    {
        Begin(190, 14, new Thickness(10, 8, 8, 10)); // после кастомки карточка была широкой полосой
        Recommended = 0;
        bool allPick = snap.Mode == "ALL_PICK";
        string H(int id) => data.Heroes.GetValueOrDefault(id, $"#{id}");
        // Предвыбор союзников считаем их героями: учитываем сочетание и не предлагаем.
        var team = snap.Allies.Concat(snap.Planned).Where(h => h != ownHero).ToList();

        _panel.Children.Add(Glass.Header(this, "Пик", 22, Glass.Ink,
            snap.Mode switch { "ALL_PICK" => "All Pick", "TURBO" => "Турбо", _ => "режим ?" }, Refresh));
        _panel.Children.Add(Glass.Chips(new[] { Glass.Chip("роль: " + RoleNames[Math.Clamp(snap.Position, 0, 5)], Glass.GoldLight, 11) }, top: 4));

        // шанс победы по драфту — как только известно хотя бы двое героев и ясен режим (винрейты Турбо и обычной игры разные)
        if (snap.Mode != null && data.WinProbability(snap.Mode, team, snap.Enemies, ownHero) is { } chance && chance.KnownAllies + chance.KnownEnemies >= 2)
        {
            var color = chance.P >= 0.52 ? Glass.Good : chance.P <= 0.48 ? Glass.Bad : Glass.Ink;
            _panel.Children.Add(Glass.Text("Шанс победы по драфту", 11, Glass.Muted, top: 2));
            var line = new DockPanel();
            var big = Glass.Text($"{chance.P:0%}", 30, color, serif: true, bold: true, wrap: false);
            big.Margin = new Thickness(0, -4, 8, 0);
            DockPanel.SetDock(big, Dock.Left);
            line.Children.Add(big);
            line.Children.Add(Bar(chance.P));
            _panel.Children.Add(line);
            _panel.Children.Add(Glass.Text($"известно: ваших {chance.KnownAllies}, их {chance.KnownEnemies}" +
                (chance.Parts.Count > 0 ? " · " + string.Join(" · ", chance.Parts) : ""), 10, Glass.Muted));
        }

        // в All Pick вражеские пики видны всем, в Турбо — только засвеченные при конфликте выбора; при них — ваш счёт против них
        if (snap.Enemies.Count > 0)
        {
            _panel.Children.Add(Glass.Text(allPick ? "Враги · ваши победы против них" : "Засвечены у врага", 11, Glass.Muted, top: 6));
            var prof = data.Profile is { Loaded: true } p ? p : null;
            _panel.Children.Add(Glass.Chips(snap.Enemies.Select(e =>
            {
                if (prof != null && prof.AgainstHero.TryGetValue(e, out var r) && r.Wins + r.Losses >= 5)
                {
                    double pct = 100.0 * r.Wins / (r.Wins + r.Losses);
                    return (UIElement)Glass.Chip($"{H(e)} {pct:0}%", pct >= 50 ? Glass.Good : Glass.Bad, 11);
                }
                return Glass.Chip(H(e), Glass.Ink, 11);
            }), top: 3));
        }
        if (snap.Allies.Count > 0)
        {
            _panel.Children.Add(Glass.Text("Союзники", 11, Glass.Muted, top: 4));
            _panel.Children.Add(Glass.Chips(snap.Allies.Select(a => (UIElement)Glass.Chip(H(a), Glass.Ink, 11)), top: 3));
        }
        if (snap.Planned.Count > 0)
        {
            _panel.Children.Add(Glass.Text("Собираются взять", 11, Glass.Muted, top: 4));
            _panel.Children.Add(Glass.Chips(snap.Planned.Select(a => (UIElement)Glass.Chip(H(a), Glass.Good, 11)), top: 3));
        }
        var lacking = data.Lacking(ownHero > 0 ? team.Append(ownHero).ToList() : team);
        if (lacking.Count > 0)
        {
            var note = Glass.Tile(Glass.Text("В команде: " + string.Join(", ", lacking), 13, Glass.GoldLight, serif: true, italic: true),
                top: 2, padding: new Thickness(8, 3, 8, 4));
            note.Background = Glass.B(0x29, 0xD2, 0xA1, 0x5A);
            _panel.Children.Add(note);
        }

        if (ownHero > 0)
        {
            // вы уже выбрали — подсказывать, кого брать, поздно
            var mineBox = new StackPanel();
            mineBox.Children.Add(Caps("Ваш герой"));
            mineBox.Children.Add(Glass.Text(H(ownHero), 20, Glass.Ink, serif: true, bold: true));
            double own = data.PersonalBonus(ownHero, snap.Mode);
            if (data.Pool.TryGetValue(ownHero, out var pg) && pg.Games > 0)
                mineBox.Children.Add(Glass.Text($"у вас {pg.Games} {Words.Plural(pg.Games, "игра", "игры", "игр")}, {100.0 * pg.Wins / pg.Games:0}% · ваша игра {own:+0.0;-0.0}", 11, Glass.Muted));
            _panel.Children.Add(Gilded(Glass.Tile(mineBox, top: 8)));
            _panel.Children.Add(Glass.Text(recognition, 9.5, Glass.Muted, top: 8));
            return;
        }

        var blocked = new HashSet<int>(snap.Unavailable.Concat(snap.Planned));
        var picks = data.TurboPicks(snap.Mode, snap.Position, blocked, snap.Enemies, team);
        bool Playable(TurboPick p) => data.Pool.TryGetValue(p.HeroId, out var g) && g.Games >= MinPoolGames;
        double Score(TurboPick p) => data.PickScore(p, snap.Mode);

        // Главное: кого брать. Из ваших героев (не «редко на роли») — с лучшим итогом, включая вашу игру на нём.
        var best = picks.Where(p => !p.Rare && Playable(p)).OrderByDescending(Score).FirstOrDefault()
                   ?? picks.FirstOrDefault(p => !p.Rare);
        var bestAny = picks.FirstOrDefault(p => !p.Rare);
        string scope = snap.Position > 0 ? "на роли" : allPick ? "в All Pick" : "в Турбо";
        if (best != null)
        {
            Recommended = best.HeroId;
            var take = new StackPanel();
            var head = new DockPanel();
            var tier = Glass.TierBadge(best.Winrate, 34);
            DockPanel.SetDock(tier, Dock.Left);
            head.Children.Add(tier);
            var name = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            name.Children.Add(Caps("Бери"));
            name.Children.Add(Glass.Text(H(best.HeroId), 20, Glass.Ink, serif: true, bold: true));
            head.Children.Add(name);
            take.Children.Add(head);
            var why = new List<UIElement> { Glass.Chip($"{best.Winrate:0.0}% {scope}", Glass.Ink, 10.5) };
            why.AddRange(Corrections(best).Select(c => (UIElement)Glass.Chip(c.Text, c.Value >= 0 ? Glass.Good : Glass.Bad, 10.5)));
            double personal = data.PersonalBonus(best.HeroId, snap.Mode);
            if (Math.Abs(personal) >= 0.05) why.Add(Glass.Chip($"ваша игра {personal:+0.0;-0.0}", personal > 0 ? Glass.Good : Glass.Bad, 10.5));
            take.Children.Add(Glass.Chips(why, top: 6));
            if (bestAny != null && bestAny.HeroId != best.HeroId && bestAny.Total - best.Total >= 1.5)
                take.Children.Add(Glass.Text($"по статистике сильнее {H(bestAny.HeroId)} ({bestAny.Winrate:0.0}% {bestAny.Bonus:+0.0;-0.0}), но вы на нём почти не играете", 10, Glass.Muted));
            _panel.Children.Add(Gilded(Glass.Tile(take, top: 8)));

            // Под этот драфт: из ваших героев — лучший только по поправкам за врагов, союзников, состав и риск контрпика,
            // без общего винрейта героя и вашей игры на нём. Когда это не «Бери» и под драфт он заметно лучше.
            var fit = team.Count + snap.Enemies.Count == 0 ? null
                : picks.Where(p => !p.Rare && Playable(p)).OrderByDescending(p => p.Bonus).ThenByDescending(Score).FirstOrDefault();
            if (fit != null && fit.HeroId != best.HeroId && fit.Bonus > 0 && fit.Bonus - best.Bonus >= DraftFitGap)
            {
                var row = new DockPanel { Margin = new Thickness(2, 6, 0, 0) };
                var t = Glass.TierBadge(fit.Winrate, 26);
                DockPanel.SetDock(t, Dock.Left);
                row.Children.Add(t);
                var text = new TextBlock { FontFamily = Glass.Sans, FontSize = 11, Foreground = Glass.B(Glass.Muted), TextWrapping = TextWrapping.Wrap };
                text.Inlines.Add("Под этот драфт: ");
                text.Inlines.Add(new System.Windows.Documents.Run(H(fit.HeroId)) { FontFamily = Glass.Serif, FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Glass.B(Glass.Ink) });
                text.Inlines.Add(new System.Windows.Documents.Run($" {fit.Bonus:+0.0;-0.0}") { FontWeight = FontWeights.Bold, Foreground = Glass.B(Glass.Good) });
                row.Children.Add(text);
                _panel.Children.Add(row);
                _panel.Children.Add(Glass.Text(string.Join(" · ", Corrections(fit).Select(c => c.Text).Append($"{fit.Winrate:0.0}% {scope}")), 10, Glass.Muted));
            }
        }

        _panel.Children.Add(Glass.Divider(8, 4));
        var mine = picks.Where(Playable).OrderBy(p => p.Rare).ThenByDescending(Score).Take(PoolRows).ToList();
        var pool = new StackPanel();
        pool.Children.Add(Glass.Text("Ваши герои", 11, Glass.Muted));
        if (mine.Count == 0) pool.Children.Add(Glass.Text("нет доступных", 12, Glass.Muted));
        foreach (var p in mine)
        {
            var g = data.Pool[p.HeroId];
            pool.Children.Add(Row(H(p.HeroId), p, $"у вас {g.Games} {Words.Plural(g.Games, "игра", "игры", "игр")}, {100.0 * g.Wins / g.Games:0}%" + (p.Rare ? " · редко на этой роли" : "")));
        }
        _panel.Children.Add(Glass.Tile(pool, padding: new Thickness(8, 6, 6, 6)));

        _panel.Children.Add(Glass.Text(snap.Position > 0 ? $"Сильные на роли «{RoleNames[snap.Position]}»" : (allPick ? "Сильные в All Pick" : "Сильные в Турбо"), 11, Glass.Muted, top: 6));
        _panel.Children.Add(Glass.Chips(picks.Where(p => !p.Rare && !mine.Contains(p)).Take(MetaRows).Select(p => (UIElement)Meta(H(p.HeroId), p)), top: 3));

        _panel.Children.Add(Glass.Text("S от 53% · A 51–53 · B 49–51 · C ниже 49", 9.5, Glass.Muted, top: 4));
        _panel.Children.Add(Glass.Text(recognition, 9.5, Glass.Muted, top: 4));
        bool allModes = snap.Enemies.Count + snap.Allies.Count > 0 && (allPick || !data.TurboMatchups);
        var notes = data.TurboLabel + (allModes ? "; поправка за союзников и врагов — по всем режимам" : "");
        _panel.Children.Add(Glass.Text(notes, 9.5, Glass.Muted, top: 2));
    }

    // Поправки к винрейту героя за драфт, п.п.: что заметно — подписью.
    static List<(string Text, double Value)> Corrections(TurboPick p)
    {
        var parts = new List<(string, double)>();
        if (Math.Abs(p.VsRevealed) >= 0.05) parts.Add(($"против врагов {p.VsRevealed:+0.0;-0.0}", p.VsRevealed));
        if (Math.Abs(p.WithAllies) >= 0.05) parts.Add(($"с союзниками {p.WithAllies:+0.0;-0.0}", p.WithAllies));
        if (Math.Abs(p.Composition) >= 0.05) parts.Add(($"состав {p.Composition:+0.0;-0.0}", p.Composition));
        if (Math.Abs(p.CounterRisk) >= 0.05) parts.Add(($"риск контрпика {p.CounterRisk:+0.0;-0.0}", p.CounterRisk));
        return parts;
    }

    // Строка «ваши герои»: тир, имя антиквой и подпись, справа винрейт и поправка за драфт.
    static UIElement Row(string name, TurboPick p, string note)
    {
        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var tier = Glass.TierBadge(p.Winrate, 22);
        DockPanel.SetDock(tier, Dock.Left);
        row.Children.Add(tier);
        var right = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 0, 0) };
        var wr = Glass.Text($"{p.Winrate:0.0}%", 11.5, p.Winrate >= 50 ? Glass.Ink : Glass.Bad, wrap: false);
        wr.FontWeight = FontWeights.SemiBold;
        wr.HorizontalAlignment = HorizontalAlignment.Right;
        right.Children.Add(wr);
        // поправка за союзников, засвеченных врагов, состав и риск контрпика — в процентных пунктах
        if (Math.Abs(p.Bonus) >= 0.05)
        {
            var b = Glass.Text($"{p.Bonus:+0.0;-0.0}", 10.5, p.Bonus > 0 ? Glass.Good : Glass.Bad, wrap: false);
            b.HorizontalAlignment = HorizontalAlignment.Right;
            right.Children.Add(b);
        }
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        var text = new StackPanel();
        text.Children.Add(Glass.Text(name, 14.5, Glass.Ink, serif: true, bold: true, wrap: false));
        text.Children.Add(Glass.Text(note, 9.5, Glass.Muted));
        row.Children.Add(text);
        return row;
    }

    // Чип сильного героя: кружок тира, имя, винрейт и поправка.
    static Border Meta(string name, TurboPick p)
    {
        var sp = new DockPanel(); // не StackPanel: в нём текст не знает ширины и многоточие не ставится
        var tier = Glass.TierBadge(p.Winrate, 16, round: true);
        tier.Margin = new Thickness(0, 0, 4, 0);
        DockPanel.SetDock(tier, Dock.Left);
        sp.Children.Add(tier);
        var text = new TextBlock
        {
            FontFamily = Glass.Sans, FontSize = 10.5, Foreground = Glass.B(Glass.Ink),
            TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
        };
        text.Inlines.Add($"{name} {p.Winrate:0.0}%");
        if (Math.Abs(p.Bonus) >= 0.05)
            text.Inlines.Add(new System.Windows.Documents.Run($" {p.Bonus:+0.0;-0.0}") { Foreground = Glass.B(p.Bonus > 0 ? Glass.Good : Glass.Bad) });
        sp.Children.Add(text);
        return new Border
        {
            Background = Glass.B(0x12, 0xFF, 0xF0, 0xDC), CornerRadius = new CornerRadius(999),
            Padding = new Thickness(2, 2, 8, 2), Margin = new Thickness(0, 0, 4, 4), Child = sp,
            MaxWidth = 170, // уже карточки — длинное имя («Legion Commander») не вылезает за край
        };
    }

    // Полоса шанса: золото — ваши, терракота — их.
    static UIElement Bar(double p)
    {
        var grid = new Grid { Height = 8, VerticalAlignment = VerticalAlignment.Center };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(p, 0.01), GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - p, 0.01), GridUnitType.Star) });
        var mine = new Rectangle { Fill = Glass.B(Glass.Gold), RadiusX = 4, RadiusY = 4, Margin = new Thickness(0, 0, 1.5, 0) };
        var theirs = new Rectangle { Fill = Glass.B(0x8C, 0xE9, 0x8A, 0x5F), RadiusX = 4, RadiusY = 4, Margin = new Thickness(1.5, 0, 0, 0) };
        Grid.SetColumn(theirs, 1);
        grid.Children.Add(mine);
        grid.Children.Add(theirs);
        return grid;
    }

    // Подпись капителью: «БЕРИ», «ВАШ ГЕРОЙ».
    static TextBlock Caps(string text)
    {
        var t = Glass.Text(text.ToUpperInvariant(), 10, Glass.GoldLight, bold: true, wrap: false);
        return t;
    }

    // Главная плитка — с золотой рамкой, как «Бери» в макете.
    static Border Gilded(Border tile)
    {
        tile.BorderBrush = Glass.B(0x73, 0xD2, 0xA1, 0x5A);
        tile.BorderThickness = new Thickness(1);
        return tile;
    }
}
