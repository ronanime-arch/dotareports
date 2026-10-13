using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DotaReports;

/// <summary>Состав матча для вкладки «Матч»: ваш герой, союзники, враги, режим и прогноз по драфту.</summary>
sealed record MatchInfo(int OwnHero, IReadOnlyList<int> Allies, IReadOnlyList<int> Enemies, string? Mode, WinChance? Chance);

/// <summary>
/// «Аналитика и Dotabuff» из меню значка: разбор текущего матча (в начале матча обновляется сам), тренды меты по неделям
/// Stratz на вашем ранге и Dotabuff во встроенном браузере — ваш профиль, герои, матчи, мета, новости, страницы героя.
/// У Dotabuff нет API, а разбирать его страницы программой запрещают его правила, поэтому страницы просто показываются,
/// как в обычном браузере, и не читаются.
/// </summary>
sealed class AnalyticsWindow : Window
{
    static readonly Brush GoodBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x4F));
    static readonly Brush BadBrush = new SolidColorBrush(Color.FromRgb(0xD0, 0x45, 0x30));

    readonly DraftData _data;
    readonly CoreWebView2Environment? _env;
    readonly WebView2 _web = new() { Visibility = Visibility.Collapsed };
    readonly ScrollViewer _trends = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly ScrollViewer _match = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
    readonly TextBlock _address = new() { Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    bool _webReady, _webFailed;
    string? _pending;
    string _current = "";
    MatchInfo? _lastMatch;

    public AnalyticsWindow(DraftData data, CoreWebView2Environment? env)
    {
        _data = data;
        _env = env;
        Title = "Dota Reports — аналитика и Dotabuff";
        Width = 1100;
        Height = 820;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var bar = new WrapPanel { Margin = new Thickness(8) };
        bar.Children.Add(Button("Матч", () => ShowMatch(_lastMatch)));
        bar.Children.Add(Button("Тренды меты", ShowTrends));
        bar.Children.Add(Button("Мой профиль", () => Player("")));
        bar.Children.Add(Button("Мои герои", () => Player("heroes")));
        bar.Children.Add(Button("Мои матчи", () => Player("matches")));
        bar.Children.Add(Button("Мета героев", () => Navigate(Dotabuff.Meta)));
        bar.Children.Add(Button("Герои", () => Navigate(Dotabuff.Heroes)));
        bar.Children.Add(Button("Новости", () => Navigate(Dotabuff.News)));
        bar.Children.Add(Button("Открыть в браузере", () => { if (_current.Length > 0) External(_current); }));
        bar.Children.Add(_address);

        var body = new Grid();
        body.Children.Add(_trends);
        body.Children.Add(_match);
        body.Children.Add(_web);
        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(body);
        Content = root;

        Loaded += async (_, _) =>
        {
            if (_env == null) return;
            try
            {
                await _web.EnsureCoreWebView2Async(_env);
                _web.CoreWebView2.SourceChanged += (_, _) => _address.Text = _current = _web.CoreWebView2.Source;
                _webReady = true;
                if (_pending != null) Navigate(_pending);
            }
            catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
            {
                // браузер не поднялся — страницы открываются в обычном браузере
                Log.Write("Встроенный браузер аналитики не запустился, ссылки — во внешнем: " + ex.Message);
                _webFailed = true;
                if (_pending != null)
                {
                    External(_pending);
                    _pending = null;
                    ShowTrends();
                }
            }
        };
        Closed += (_, _) => _web.Dispose();
        ShowTrends();
    }

    /// <summary>Открыть страницу во встроенном браузере (нет его — в обычном).</summary>
    public void Navigate(string url)
    {
        _current = url;
        _address.Text = url;
        if (_env == null || _webFailed)
        {
            External(url);
            return;
        }
        Display(_web);
        if (_webReady)
            _web.CoreWebView2.Navigate(url);
        else
            _pending = url;
    }

    void Display(UIElement panel)
    {
        foreach (var p in new UIElement[] { _trends, _match, _web })
            p.Visibility = p == panel ? Visibility.Visible : Visibility.Collapsed;
    }

    void Player(string tab)
    {
        var account = File.Exists(Paths.Account) ? File.ReadAllText(Paths.Account).Trim() : "";
        if (account.Length == 0)
        {
            MessageBox.Show(this, "Номер аккаунта ещё не известен — он появится после первой проверки жалоб (вход в Steam).", Title);
            return;
        }
        Navigate(Dotabuff.Player(account, tab));
    }

    /// <summary>
    /// Разбор матча: шанс победы по драфту, ваш герой (винрейт на ранге, ваша игра на нём), его матчапы против каждого
    /// врага и сочетания с союзниками, чего не хватает команде, и страницы героя на Dotabuff — сборки, предметы, контрпики.
    /// </summary>
    public void ShowMatch(MatchInfo? info)
    {
        _lastMatch = info;
        _current = "";
        _address.Text = "разбор матча: Stratz на вашем ранге и ваши данные Valve";
        Display(_match);
        var panel = new StackPanel { Margin = new Thickness(16) };
        _match.Content = panel;
        if (info == null || info.OwnHero <= 0)
        {
            panel.Children.Add(Note("Разбор появится в начале матча (после пика), здесь же — и Dotabuff вашего героя."));
            return;
        }
        var heroName = HeroName(info.OwnHero);
        var modeName = info.Mode switch { "TURBO" => "Турбо", "ALL_PICK" => "All Pick", _ => "режим не определён — данные Турбо" };
        panel.Children.Add(new TextBlock { Text = $"{heroName} · {modeName}", FontSize = 22, FontWeight = FontWeights.SemiBold });
        if (info.Chance is { } c)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"Шанс победы по драфту ≈ {c.P:0%}" + (c.KnownEnemies == 0 ? " (враги неизвестны — только по вашим героям)" : ""),
                FontSize = 16,
                Foreground = c.P >= 0.53 ? GoodBrush : c.P <= 0.47 ? BadBrush : Brushes.Black,
                Margin = new Thickness(0, 4, 0, 0),
            });
            if (c.Parts.Count > 0) panel.Children.Add(Note(string.Join(" · ", c.Parts)));
        }

        var links = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        links.Children.Add(Button($"Dotabuff: сборки {heroName}", () => Navigate(Dotabuff.HeroBuilds(heroName))));
        links.Children.Add(Button("предметы", () => Navigate(Dotabuff.HeroItems(heroName))));
        links.Children.Add(Button("контрпики", () => Navigate(Dotabuff.HeroCounters(heroName))));
        panel.Children.Add(links);

        Heading(panel, "Ваш герой");
        if (_data.ModeWinrate(info.OwnHero, info.Mode) is { } wr)
            panel.Children.Add(Line($"винрейт на вашем ранге: {wr.Winrate:0.0}% ({wr.Matches} {Words.Plural(wr.Matches, "матч", "матча", "матчей")} за 4 недели)", null));
        if (_data.Pool.TryGetValue(info.OwnHero, out var pool) && pool.Games > 0)
            panel.Children.Add(Line($"у вас: {pool.Games} {Words.Plural(pool.Games, "игра", "игры", "игр")}, {100.0 * pool.Wins / pool.Games:0}% побед · ваша игра {_data.PersonalBonus(info.OwnHero, info.Mode):+0.0;-0.0}", null));
        else
            panel.Children.Add(Line("вы на нём ещё не играли (по данным Valve и OpenDota)", null));

        Heading(panel, "Против их героев");
        if (info.Enemies.Count == 0) panel.Children.Add(Note("враги ещё не известны (в Турбо пики врага скрыты до стратегии)"));
        foreach (var e in info.Enemies)
        {
            var adv = _data.MatchupAdvantage(info.OwnHero, e, info.Mode);
            var mine = _data.Profile is { Loaded: true } prof && prof.AgainstHero.TryGetValue(e, out var r) && r.Wins + r.Losses > 0
                ? $" · ваши игры против него: {r.Wins}–{r.Losses}" : "";
            panel.Children.Add(Line($"{HeroName(e)}: " + (adv is double a ? $"{a:+0.0;-0.0} п.п." : "мало матчей") + mine, adv));
        }

        Heading(panel, "С союзниками");
        if (info.Allies.Count == 0) panel.Children.Add(Note("союзники не распознаны"));
        foreach (var a in info.Allies)
        {
            var syn = _data.PairSynergy(info.OwnHero, a, info.Mode);
            panel.Children.Add(Line($"{HeroName(a)}: " + (syn is double s ? $"{s:+0.0;-0.0} п.п." : "мало матчей"), syn));
        }
        var lacking = _data.Lacking(info.Allies.Append(info.OwnHero).ToList());
        if (lacking.Count > 0) panel.Children.Add(Line("в команде: " + string.Join(", ", lacking), -1));
        panel.Children.Add(Note("Матчапы и сочетания — разница в винрейте (процентные пункты) на вашем ранге; ничего о других играх игроков не используется."));
    }

    void ShowTrends()
    {
        Display(_trends);
        _current = "";
        _address.Text = "тренды меты: Stratz, ваш ранг — последняя неделя против трёх предыдущих";
        var columns = new Grid { Margin = new Thickness(12) };
        foreach (var (mode, label) in new[] { ("TURBO", "Турбо"), ("ALL_PICK", "All Pick (рейтинг)") })
        {
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var panel = new StackPanel { Margin = new Thickness(0, 0, 24, 0) };
            Grid.SetColumn(panel, columns.ColumnDefinitions.Count - 1);
            panel.Children.Add(new TextBlock { Text = label, FontSize = 18, FontWeight = FontWeights.SemiBold });
            var trends = _data.MetaTrends(mode);
            if (trends.Count == 0)
                panel.Children.Add(Note("Нет данных Stratz (нужен токен: значок в трее → «Токен Stratz…»)."));
            else
            {
                Section(panel, "Растут", trends.Where(t => t.Now > t.Before).Take(12));
                Section(panel, "Падают", trends.Where(t => t.Now < t.Before).Reverse().Take(12));
            }
            columns.Children.Add(panel);
        }
        _trends.Content = columns;
    }

    void Section(StackPanel panel, string title, IEnumerable<(int Hero, double Now, double Before, long N)> rows)
    {
        Heading(panel, title);
        foreach (var (hero, now, before, n) in rows)
        {
            var name = HeroName(hero);
            double delta = now - before;
            var line = new TextBlock
            {
                FontSize = 14,
                Cursor = Cursors.Hand,
                ToolTip = $"{n} {Words.Plural(n, "матч", "матча", "матчей")} за неделю, до того {before:0.0}%. Двойной клик — контрпики на Dotabuff",
            };
            line.Inlines.Add(name + "  ");
            line.Inlines.Add(new System.Windows.Documents.Run($"{now:0.0}%") { FontWeight = FontWeights.SemiBold });
            line.Inlines.Add(new System.Windows.Documents.Run($"  {delta:+0.0;-0.0}") { Foreground = delta >= 0 ? GoodBrush : BadBrush });
            line.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) Navigate(Dotabuff.HeroCounters(name)); };
            panel.Children.Add(line);
        }
    }

    string HeroName(int hero) => _data.Heroes.GetValueOrDefault(hero, $"#{hero}");

    static void Heading(StackPanel panel, string text) =>
        panel.Children.Add(new TextBlock { Text = text, FontSize = 14, Foreground = Brushes.Gray, Margin = new Thickness(0, 12, 0, 4) });

    // строка разбора; value — знак поправки (зелёный/красный), null — без цвета
    static TextBlock Line(string text, double? value) => new()
    {
        Text = text,
        FontSize = 14,
        TextWrapping = TextWrapping.Wrap,
        Foreground = value is double v ? (v >= 0 ? GoodBrush : BadBrush) : Brushes.Black,
        Margin = new Thickness(0, 2, 0, 0),
    };

    static TextBlock Note(string text) => new() { Text = text, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };

    static void External(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Write("Ссылка не открылась: " + ex.Message);
        }
    }

    static Button Button(string text, Action onClick)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 4) };
        b.Click += (_, _) => onClick();
        return b;
    }
}
