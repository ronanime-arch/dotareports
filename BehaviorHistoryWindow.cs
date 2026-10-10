using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace DotaReports;

/// <summary>
/// «История поведения…» из меню значка: сводки Valve (новые сверху) и матчи окна каждой.
/// Valve пишет только, в скольких матчах были жалобы, а не в каких и не от кого, — ни один матч не подсвечиваем.
/// </summary>
sealed class BehaviorHistoryWindow : Window
{
    readonly ConductHistory _history;
    readonly DraftData _data;
    readonly StackPanel _list = new() { Margin = new Thickness(12) };
    readonly ScrollViewer _scroll;

    /// <summary>Строка таблицы матчей.</summary>
    sealed record Row(ulong MatchId, string Date, string Hero, string Mode, string Result, string Kda, string Lp);

    public BehaviorHistoryWindow(ConductHistory history, DraftData data)
    {
        _history = history;
        _data = data;
        Title = "Dota Reports — история поведения";
        Width = 760;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list };
        Content = _scroll;
        Render();
    }

    /// <summary>Перерисовать: история пополнилась.</summary>
    public void Render()
    {
        _list.Children.Clear();
        if (_history.Summaries.Count == 0)
        {
            _list.Children.Add(new TextBlock { Text = "Сводки появятся после следующей проверки.", Foreground = Brushes.Gray });
            return;
        }
        _list.Children.Add(new TextBlock
        {
            Text = "Сводка Valve выходит раз в 15 матчей. Valve пишет, в скольких матчах были жалобы, но не в каких и не от кого. " +
                   "Двойной клик по матчу — открыть его на OpenDota. «≈» — окно матчей собрано примерно.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brushes.Gray,
            Margin = new Thickness(0, 0, 0, 8),
        });
        bool first = true;
        foreach (var s in _history.Summaries)
        {
            _list.Children.Add(new Expander
            {
                Header = new TextBlock { Text = Header(s), TextWrapping = TextWrapping.Wrap }, // строка со сводкой с жалобами длиннее окна
                IsExpanded = first,
                Content = Matches(s),
                Margin = new Thickness(0, 0, 0, 6),
            });
            first = false;
        }
    }

    string Header(ConductSummary s)
    {
        var window = _history.Window(s);
        var parts = new List<string> { s.When is DateTime t ? t.ToString("dd.MM.yyyy HH:mm") : "дата неизвестна" };
        if (s.Score is int score) parts.Add($"поведение {score}" + (s.ScoreDelta is int d ? $" ({d:+#;-#;0})" : ""));
        parts.Add(s.Reports > 0
            ? $"жалоб: {s.Reports} (игроков: {s.ReportingParties}, за общение: {s.CommsReports}, матчей с жалобами: {s.ReportedMatches})"
            : "жалоб нет");
        parts.Add($"похвал: {s.Commends}");
        parts.Add(window.Count > 0
            ? $"{window.Count(m => m.Win == true)}–{window.Count(m => m.Win == false)} в матчах окна: {window.Count} из {s.Matches}" + (_history.Exact(s) ? "" : " ≈")
            : $"матчи окна не найдены (их {s.Matches})");
        return string.Join(" · ", parts);
    }

    UIElement Matches(ConductSummary s)
    {
        var window = _history.Window(s);
        if (window.Count == 0)
            return new TextBlock { Text = "В истории матчей GCPD (100 последних) этих матчей уже нет.", Foreground = Brushes.Gray, Margin = new Thickness(4) };

        var rows = window.Select(m => new Row(
            m.MatchId,
            m.StartLocal == DateTime.MinValue ? "" : m.StartLocal.ToString("dd.MM HH:mm"),
            _data.Heroes.GetValueOrDefault(m.HeroId, m.Hero), // имя по текущему списку героев: сохранённое могло устареть
            m.GameMode switch { ProfileData.Turbo => "Турбо", ProfileData.RankedAllPick => "All Pick", var g => $"режим {g}" },
            m.Win switch { true => "победа", false => "поражение", null => "—" },
            $"{m.Kills}/{m.Deaths}/{m.Assists}",
            m.LowPriority ? "low priority" : "")).ToList();
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            CanUserAddRows = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            ItemsSource = rows,
            Margin = new Thickness(0, 4, 0, 0),
        };
        grid.Columns.Add(Column("Дата", nameof(Row.Date), new DataGridLength(90)));
        grid.Columns.Add(Column("Герой", nameof(Row.Hero), new DataGridLength(1, DataGridLengthUnitType.Star)));
        grid.Columns.Add(Column("Режим", nameof(Row.Mode), new DataGridLength(80)));
        grid.Columns.Add(Column("Итог", nameof(Row.Result), new DataGridLength(90)));
        grid.Columns.Add(Column("У/С/П", nameof(Row.Kda), new DataGridLength(70)));
        grid.Columns.Add(Column("", nameof(Row.Lp), new DataGridLength(90)));
        // двойной клик именно по строке: у самой таблицы он срабатывает и на заголовке, с прежней выделенной строкой
        var rowStyle = new Style(typeof(DataGridRow));
        rowStyle.Setters.Add(new EventSetter(Control.MouseDoubleClickEvent, new MouseButtonEventHandler(OpenMatch)));
        grid.RowStyle = rowStyle;
        // таблица забирает колесо мыши себе — отдаём его общему списку, иначе он не листается
        grid.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            _scroll.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent });
        };
        return grid;
    }

    static DataGridTextColumn Column(string header, string path, DataGridLength width) =>
        new() { Header = header, Binding = new Binding(path), Width = width };

    static void OpenMatch(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGridRow { Item: Row { MatchId: > 0 } row })
            Process.Start(new ProcessStartInfo($"https://www.opendota.com/matches/{row.MatchId}") { UseShellExecute = true });
    }
}
