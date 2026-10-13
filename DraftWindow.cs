using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DotaReports;

/// <summary>
/// Окно пика: вводите героев противника и союзников, режим и роль — справа шанс победы по драфту, кого брать и ваши герои.
/// Работает и без распознавания экрана (другое разрешение). Обычное окно поверх игры (в него печатают), Esc — спрятать.
/// Двойной клик по герою — его контрпики на Dotabuff.
/// </summary>
sealed class DraftWindow : Window
{
    const int TopCount = 8;
    const int MinPoolGames = 2;

    static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x14, 0x18, 0x21));
    static readonly Brush PanelBg = new SolidColorBrush(Color.FromRgb(0x1E, 0x24, 0x30));
    static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2));
    static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB0));
    static readonly Brush Good = new SolidColorBrush(Color.FromRgb(0x7C, 0xD9, 0x92));
    static readonly Brush Bad = new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x57));
    static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0xFF, 0xB3, 0x47));
    static readonly string[] RoleNames = { "любая роль", "лёгкая", "центр", "сложная", "поддержка", "полная поддержка" };

    readonly DraftData _data;
    readonly HeroPicker _enemies;
    readonly HeroPicker _allies;
    readonly ComboBox _mode = new() { Margin = new Thickness(0, 0, 8, 0) };
    readonly ComboBox _role = new();
    readonly TextBlock _chance = new() { FontSize = 18, FontWeight = FontWeights.SemiBold };
    readonly TextBlock _chanceWhy = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap };
    readonly TextBlock _lacking = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    readonly StackPanel _mine = new();
    readonly StackPanel _best = new();
    readonly TextBlock _status = new();

    /// <summary>Открыть страницу (контрпики героя на Dotabuff) — окно «Аналитика».</summary>
    public Action<string>? OpenUrl { get; set; }

    public DraftWindow(DraftData data)
    {
        _data = data;
        Title = "Пик — помощник";
        Width = 460;
        Height = 720;
        Topmost = true;
        ShowInTaskbar = false;
        WindowStyle = WindowStyle.ToolWindow;
        Background = Bg;
        FontFamily = new FontFamily("Segoe UI");
        Foreground = Fg;

        _enemies = new HeroPicker(data, "Герои противника: «медуза», «па», «войд»… Enter — добавить, можно через запятую. Backspace в пустом поле — убрать последнего.",
            new SolidColorBrush(Color.FromRgb(0x5A, 0x24, 0x20)), Recalculate);
        _allies = new HeroPicker(data, "Ваши союзники (без вас). Enter — добавить, Backspace в пустом поле — убрать последнего.",
            new SolidColorBrush(Color.FromRgb(0x1F, 0x4A, 0x2C)), Recalculate);

        foreach (var (label, tag) in new[] { ("Турбо", "TURBO"), ("All Pick", "ALL_PICK") })
            _mode.Items.Add(new ComboBoxItem { Content = label, Tag = tag });
        _mode.SelectedIndex = 0;
        _mode.SelectionChanged += (_, _) => Recalculate();
        for (int i = 0; i < RoleNames.Length; i++) _role.Items.Add(new ComboBoxItem { Content = RoleNames[i], Tag = i });
        _role.SelectedIndex = 0;
        _role.SelectionChanged += (_, _) => Recalculate();
        _role.ToolTip = "Роль учитывается в All Pick: винрейт героя именно на ней";

        var clear = new Button { Content = "Очистить", Padding = new Thickness(10, 2, 10, 2) };
        clear.Click += (_, _) => { _enemies.Clear(); _allies.Clear(); Recalculate(); _enemies.Focus(); };
        var settings = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(clear, Dock.Right);
        settings.Children.Add(clear);
        settings.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { _mode, _role } });

        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(settings);
        root.Children.Add(Label("Герои противника", 12, Dim));
        root.Children.Add(_enemies.View);
        root.Children.Add(Label("Ваши союзники", 12, Dim, top: 4));
        root.Children.Add(_allies.View);
        root.Children.Add(_chance);
        _chanceWhy.Foreground = Dim;
        root.Children.Add(_chanceWhy);
        _lacking.Foreground = Accent;
        _lacking.Margin = new Thickness(0, 2, 0, 0);
        root.Children.Add(_lacking);
        root.Children.Add(Label("Из ваших героев", 13, Dim, top: 8));
        root.Children.Add(_mine);
        root.Children.Add(Label("Лучшие на пик", 13, Dim, top: 10));
        root.Children.Add(_best);
        _status.FontSize = 11;
        _status.Foreground = Dim;
        _status.TextWrapping = TextWrapping.Wrap;
        _status.Margin = new Thickness(0, 10, 0, 0);
        root.Children.Add(_status);

        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Hide(); e.Handled = true; } };
        Closing += (_, e) => { e.Cancel = true; Hide(); };
    }

    public void Toggle()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 24;
        Top = wa.Top + 80;
        Show();
        Activate();
        _enemies.Focus();
        Recalculate();
    }

    /// <summary>Данные догрузились в фоне — перерисовать, если окно открыто.</summary>
    public void DataChanged()
    {
        if (IsVisible) Recalculate();
    }

    string Mode => _mode.SelectedItem is ComboBoxItem { Tag: string tag } ? tag : "TURBO";
    int Position => Mode == "ALL_PICK" && _role.SelectedItem is ComboBoxItem { Tag: int pos } ? pos : 0;

    void Recalculate()
    {
        _enemies.Render();
        _allies.Render();
        _mine.Children.Clear();
        _best.Children.Clear();
        _chance.Text = _chanceWhy.Text = _lacking.Text = "";
        _status.Text = _data.Status;
        if (!_data.Ready && !_data.TurboReady)
        {
            _best.Children.Add(Label("Данные ещё не загружены.", 13, Dim));
            return;
        }

        if (_data.WinProbability(Mode, _allies.Ids, _enemies.Ids, 0) is { } chance && chance.KnownAllies + chance.KnownEnemies >= 2)
        {
            _chance.Text = $"Шанс победы ≈ {chance.P:0%}";
            _chance.Foreground = chance.P >= 0.52 ? Good : chance.P <= 0.48 ? Bad : Fg;
            _chanceWhy.Text = $"по драфту: ваших {chance.KnownAllies}, их {chance.KnownEnemies}" + (chance.Parts.Count > 0 ? " · " + string.Join(" · ", chance.Parts) : "");
        }
        var lacking = _data.Lacking(_allies.Ids);
        if (lacking.Count > 0) _lacking.Text = "В команде: " + string.Join(", ", lacking);

        if (!_data.TurboReady)
        {
            // винрейтов по режиму ещё нет (нет токена Stratz) — только контрпики
            ShowCounters();
            return;
        }
        if (_enemies.Ids.Count + _allies.Ids.Count == 0)
            _best.Children.Add(Label("Без героев — просто сильные в режиме. Добавьте врагов и союзников.", 12, Dim));
        var picks = _data.TurboPicks(Mode, Position, new HashSet<int>(_allies.Ids), _enemies.Ids, _allies.Ids);
        double Score(TurboPick p) => p.Total + _data.PersonalBonus(p.HeroId, Mode);
        var mine = picks.Where(p => _data.Pool.TryGetValue(p.HeroId, out var g) && g.Games >= MinPoolGames)
            .OrderBy(p => p.Rare).ThenByDescending(Score).Take(TopCount).ToList();
        if (mine.Count == 0) _mine.Children.Add(Label("Нет героев с 2+ играми.", 13, Dim));
        foreach (var p in mine)
        {
            var g = _data.Pool[p.HeroId];
            _mine.Children.Add(Row(p, $"у вас {g.Games} игр, {100.0 * g.Wins / g.Games:0}%" + (p.Rare ? " · редко на этой роли" : "")));
        }
        foreach (var p in picks.Where(p => !p.Rare).Take(TopCount))
            _best.Children.Add(Row(p, _data.Pool.TryGetValue(p.HeroId, out var g) ? $"у вас {g.Games} игр" : ""));
    }

    void ShowCounters()
    {
        if (_enemies.Ids.Count == 0)
        {
            _best.Children.Add(Label("Введите героев противника.", 13, Dim));
            return;
        }
        var ranked = _data.Counters(_enemies.Ids);
        foreach (var r in ranked.Where(r => _data.Pool.TryGetValue(r.HeroId, out var p) && p.Games >= MinPoolGames))
        {
            var p = _data.Pool[r.HeroId];
            _mine.Children.Add(CounterRow(r, $"у вас {p.Games} игр, {100.0 * p.Wins / p.Games:0}% побед"));
        }
        if (_mine.Children.Count == 0) _mine.Children.Add(Label("Нет героев с 2+ играми.", 13, Dim));
        foreach (var r in ranked.Take(TopCount))
            _best.Children.Add(CounterRow(r, _data.Pool.TryGetValue(r.HeroId, out var p) ? $"у вас {p.Games} игр" : ""));
    }

    // Строка героя: винрейт в режиме (на роли) и поправки за врагов, союзников, состав и риск контрпика, п.п.
    UIElement Row(TurboPick p, string note)
    {
        int hero = p.HeroId;
        var parts = new List<string> { $"{p.Winrate:0.0}% в режиме" };
        if (Math.Abs(p.VsRevealed) >= 0.05) parts.Add($"против врагов {p.VsRevealed:+0.0;-0.0}");
        if (Math.Abs(p.WithAllies) >= 0.05) parts.Add($"с союзниками {p.WithAllies:+0.0;-0.0}");
        if (Math.Abs(p.Composition) >= 0.05) parts.Add($"состав {p.Composition:+0.0;-0.0}");
        if (Math.Abs(p.CounterRisk) >= 0.05) parts.Add($"риск контрпика {p.CounterRisk:+0.0;-0.0}");
        double personal = _data.PersonalBonus(hero, Mode);
        if (Math.Abs(personal) >= 0.05) parts.Add($"ваша игра {personal:+0.0;-0.0}");
        return HeroRow(hero, note, $"{p.Winrate:0.0}%", p.Bonus, string.Join("\n", parts) + "\nДвойной клик — контрпики на Dotabuff");
    }

    UIElement CounterRow(CounterResult r, string note) =>
        HeroRow(r.HeroId, note + (r.LowData ? "  мало данных" : ""), "", r.Score, string.Join("\n", r.Parts) + "\nДвойной клик — контрпики на Dotabuff");

    UIElement HeroRow(int hero, string note, string winrate, double bonus, string tooltip)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2), ToolTip = tooltip, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heroName = _data.Heroes.GetValueOrDefault(hero, $"#{hero}");
        var name = new TextBlock { FontSize = 14, Foreground = Fg };
        name.Inlines.Add(heroName);
        if (note.Length > 0) name.Inlines.Add(new System.Windows.Documents.Run("  " + note) { Foreground = Dim, FontSize = 12 });
        var score = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold };
        if (winrate.Length > 0) score.Inlines.Add(new System.Windows.Documents.Run(winrate + " ") { Foreground = Fg });
        score.Inlines.Add(new System.Windows.Documents.Run(bonus.ToString("+0.0;-0.0;0.0")) { Foreground = bonus >= 0 ? Good : Bad });
        Grid.SetColumn(score, 1);
        grid.Children.Add(name);
        grid.Children.Add(score);
        grid.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2 && OpenUrl != null) OpenUrl(Dotabuff.HeroCounters(heroName));
        };
        return grid;
    }

    static TextBlock Label(string text, double size, Brush color, double top = 0) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = color,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, top, 0, 4),
    };

    /// <summary>Поле ввода героев с подсказками и плашками выбранных (до пяти).</summary>
    sealed class HeroPicker
    {
        readonly DraftData _data;
        readonly Brush _chipBg;
        readonly Action _changed;
        readonly TextBox _input = new();
        readonly WrapPanel _suggest = new() { Margin = new Thickness(0, 4, 0, 0) };
        readonly WrapPanel _chips = new() { Margin = new Thickness(0, 4, 0, 4) };
        readonly List<int> _ids = new();

        public StackPanel View { get; } = new();
        public IReadOnlyCollection<int> Ids => _ids;

        public HeroPicker(DraftData data, string tooltip, Brush chipBg, Action changed)
        {
            _data = data;
            _chipBg = chipBg;
            _changed = changed;
            _input.FontSize = 15;
            _input.Padding = new Thickness(6, 4, 6, 4);
            _input.Background = PanelBg;
            _input.Foreground = Fg;
            _input.CaretBrush = Fg;
            _input.BorderBrush = Dim;
            _input.ToolTip = tooltip;
            _input.TextChanged += (_, _) => UpdateSuggestions();
            _input.PreviewKeyDown += OnKey;
            View.Children.Add(_input);
            View.Children.Add(_suggest);
            View.Children.Add(_chips);
        }

        public void Focus() => _input.Focus();

        public void Clear()
        {
            _ids.Clear();
            _input.Clear();
        }

        public void Render()
        {
            _chips.Children.Clear();
            foreach (var id in _ids.ToList())
            {
                var b = Chip(_data.Heroes.GetValueOrDefault(id, $"#{id}") + "  ✕", _chipBg);
                b.ToolTip = "Убрать";
                b.Click += (_, _) => { _ids.Remove(id); _changed(); _input.Focus(); };
                _chips.Children.Add(b);
            }
        }

        void OnKey(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                foreach (var part in _input.Text.Split(',', ';'))
                    if (HeroSearch.Find(part, _data.Heroes, 1).FirstOrDefault() is int id and > 0)
                        Add(id);
                _input.Clear();
                e.Handled = true;
            }
            else if (e.Key == Key.Back && _input.Text.Length == 0 && _ids.Count > 0)
            {
                _ids.RemoveAt(_ids.Count - 1);
                _changed();
                e.Handled = true;
            }
        }

        void Add(int id)
        {
            if (_ids.Contains(id) || _ids.Count >= 5) return;
            _ids.Add(id);
            _changed();
        }

        void UpdateSuggestions()
        {
            _suggest.Children.Clear();
            var last = _input.Text.Split(',', ';').Last();
            foreach (var id in HeroSearch.Find(last, _data.Heroes))
            {
                var b = Chip(_data.Heroes[id], PanelBg);
                b.Click += (_, _) =>
                {
                    Add(id);
                    _input.Clear();
                    _input.Focus();
                };
                _suggest.Children.Add(b);
            }
        }

        static Button Chip(string text, Brush bg) => new()
        {
            Content = text,
            Margin = new Thickness(0, 0, 6, 6),
            Padding = new Thickness(8, 2, 8, 2),
            Background = bg,
            Foreground = Fg,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
        };
    }
}
