using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DotaReports;

/// <summary>
/// Окно пика: вводите героев противника — справа лучшие контрпики и ваши герои.
/// Обычное окно поверх игры (в него печатают), Esc или горячая клавиша — спрятать.
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

    readonly DraftData _data;
    readonly TextBox _input = new();
    readonly WrapPanel _suggest = new();
    readonly WrapPanel _enemies = new();
    readonly StackPanel _mine = new();
    readonly StackPanel _best = new();
    readonly TextBlock _status = new();
    readonly List<int> _enemyIds = new();

    public DraftWindow(DraftData data)
    {
        _data = data;
        Title = "Пик — контрпики";
        Width = 440;
        Height = 640;
        Topmost = true;
        ShowInTaskbar = false;
        WindowStyle = WindowStyle.ToolWindow;
        Background = Bg;
        FontFamily = new FontFamily("Segoe UI");
        Foreground = Fg;

        _input.FontSize = 15;
        _input.Padding = new Thickness(6, 4, 6, 4);
        _input.Background = PanelBg;
        _input.Foreground = Fg;
        _input.CaretBrush = Fg;
        _input.BorderBrush = Dim;
        _input.ToolTip = "Герои противника: «медуза», «па», «войд»… Enter — добавить, можно через запятую. Backspace в пустом поле — убрать последнего.";
        _input.TextChanged += (_, _) => UpdateSuggestions();
        _input.PreviewKeyDown += OnInputKey;

        var clear = new Button { Content = "Очистить", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2) };
        clear.Click += (_, _) => { _enemyIds.Clear(); _input.Clear(); Recalculate(); _input.Focus(); };

        var inputRow = new DockPanel();
        DockPanel.SetDock(clear, Dock.Right);
        inputRow.Children.Add(clear);
        inputRow.Children.Add(_input);

        var root = new StackPanel { Margin = new Thickness(12) };
        root.Children.Add(Label("Герои противника", 12, Dim));
        root.Children.Add(inputRow);
        _suggest.Margin = new Thickness(0, 4, 0, 0);
        root.Children.Add(_suggest);
        _enemies.Margin = new Thickness(0, 6, 0, 6);
        root.Children.Add(_enemies);
        root.Children.Add(Label("Из ваших героев", 13, Dim, top: 6));
        root.Children.Add(_mine);
        root.Children.Add(Label("Лучшие контрпики", 13, Dim, top: 10));
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
        _input.Focus();
        Recalculate();
    }

    /// <summary>Данные догрузились в фоне — перерисовать, если окно открыто.</summary>
    public void DataChanged()
    {
        if (IsVisible) Recalculate();
    }

    void OnInputKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            foreach (var part in _input.Text.Split(',', ';'))
                if (HeroSearch.Find(part, _data.Heroes, 1).FirstOrDefault() is int id and > 0)
                    AddEnemy(id);
            _input.Clear();
            e.Handled = true;
        }
        else if (e.Key == Key.Back && _input.Text.Length == 0 && _enemyIds.Count > 0)
        {
            _enemyIds.RemoveAt(_enemyIds.Count - 1);
            Recalculate();
            e.Handled = true;
        }
    }

    void AddEnemy(int id)
    {
        if (_enemyIds.Contains(id) || _enemyIds.Count >= 5) return;
        _enemyIds.Add(id);
        Recalculate();
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
                AddEnemy(id);
                _input.Clear();
                _input.Focus();
            };
            _suggest.Children.Add(b);
        }
    }

    void Recalculate()
    {
        _enemies.Children.Clear();
        foreach (var id in _enemyIds)
        {
            var b = Chip(_data.Heroes.GetValueOrDefault(id, $"#{id}") + "  ✕", new SolidColorBrush(Color.FromRgb(0x5A, 0x24, 0x20)));
            b.ToolTip = "Убрать";
            b.Click += (_, _) => { _enemyIds.Remove(id); Recalculate(); _input.Focus(); };
            _enemies.Children.Add(b);
        }

        _mine.Children.Clear();
        _best.Children.Clear();
        _status.Text = _data.Status;
        if (!_data.Ready)
        {
            _best.Children.Add(Label("Данные ещё не загружены.", 13, Dim));
            return;
        }
        if (_enemyIds.Count == 0)
        {
            _best.Children.Add(Label("Введите героев противника.", 13, Dim));
            return;
        }

        var ranked = _data.Counters(_enemyIds);
        var mine = ranked.Where(r => _data.Pool.TryGetValue(r.HeroId, out var p) && p.Games >= MinPoolGames).ToList();
        if (mine.Count == 0) _mine.Children.Add(Label("Нет героев с 2+ играми.", 13, Dim));
        foreach (var r in mine)
        {
            var p = _data.Pool[r.HeroId];
            _mine.Children.Add(Row(r, $"у вас {p.Games} игр, {100.0 * p.Wins / p.Games:0}% побед"));
        }
        foreach (var r in ranked.Take(TopCount))
        {
            var note = _data.Pool.TryGetValue(r.HeroId, out var p) ? $"у вас {p.Games} игр" : "";
            _best.Children.Add(Row(r, note));
        }
    }

    // Счёт — сумма преимуществ против каждого врага, в процентных пунктах винрейта.
    UIElement Row(CounterResult r, string note)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2), ToolTip = string.Join("\n", r.Parts) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var name = new TextBlock { FontSize = 14, Foreground = Fg };
        name.Inlines.Add(_data.Heroes.GetValueOrDefault(r.HeroId, $"#{r.HeroId}"));
        if (note.Length > 0) name.Inlines.Add(new System.Windows.Documents.Run("  " + note) { Foreground = Dim, FontSize = 12 });
        if (r.LowData) name.Inlines.Add(new System.Windows.Documents.Run("  мало данных") { Foreground = Dim, FontSize = 11 });
        var score = new TextBlock { Text = r.Score.ToString("+0.0;-0.0;0.0"), FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = r.Score >= 0 ? Good : Bad };
        Grid.SetColumn(score, 1);
        grid.Children.Add(name);
        grid.Children.Add(score);
        return grid;
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

    static TextBlock Label(string text, double size, Brush color, double top = 0) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = color,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, top, 0, 4),
    };
}
