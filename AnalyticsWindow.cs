using System;
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

/// <summary>
/// «Аналитика и Dotabuff» из меню значка: тренды меты по неделям Stratz на вашем ранге (свой расчёт) и Dotabuff
/// во встроенном браузере — ваш профиль, герои, матчи, мета, новости. У Dotabuff нет API, а разбирать его страницы
/// программой запрещают его правила, поэтому страницы просто показываются, как в обычном браузере, и не читаются.
/// </summary>
sealed class AnalyticsWindow : Window
{
    readonly DraftData _data;
    readonly CoreWebView2Environment? _env;
    readonly WebView2 _web = new() { Visibility = Visibility.Collapsed };
    readonly ScrollViewer _trends = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    readonly TextBlock _address = new() { Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
    bool _webReady;
    string? _pending;
    string _current = "";

    public AnalyticsWindow(DraftData data, CoreWebView2Environment? env)
    {
        _data = data;
        _env = env;
        Title = "Dota Reports — аналитика и Dotabuff";
        Width = 1100;
        Height = 820;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var bar = new WrapPanel { Margin = new Thickness(8) };
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
                Log.Write("Встроенный браузер аналитики не запустился, ссылки — во внешнем: " + ex.Message);
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
        if (_env == null)
        {
            External(url);
            return;
        }
        _trends.Visibility = Visibility.Collapsed;
        _web.Visibility = Visibility.Visible;
        if (_webReady)
            _web.CoreWebView2.Navigate(url);
        else
            _pending = url;
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

    void ShowTrends()
    {
        _web.Visibility = Visibility.Collapsed;
        _trends.Visibility = Visibility.Visible;
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
                panel.Children.Add(new TextBlock { Text = "Нет данных Stratz (нужен токен: значок в трее → «Токен Stratz…»).", Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap });
            else
            {
                Section(panel, "Растут", trends.Take(12));
                Section(panel, "Падают", trends.AsEnumerable().Reverse().Take(12));
            }
            columns.Children.Add(panel);
        }
        _trends.Content = columns;
    }

    void Section(StackPanel panel, string title, System.Collections.Generic.IEnumerable<(int Hero, double Now, double Before, long N)> rows)
    {
        panel.Children.Add(new TextBlock { Text = title, FontSize = 14, Foreground = Brushes.Gray, Margin = new Thickness(0, 10, 0, 4) });
        foreach (var (hero, now, before, n) in rows)
        {
            var name = _data.Heroes.GetValueOrDefault(hero, $"#{hero}");
            double delta = now - before;
            var line = new TextBlock { FontSize = 14, Cursor = Cursors.Hand, ToolTip = $"{n} матчей за неделю, до того {before:0.0}%. Двойной клик — контрпики на Dotabuff" };
            line.Inlines.Add(name + "  ");
            line.Inlines.Add(new System.Windows.Documents.Run($"{now:0.0}%") { FontWeight = FontWeights.SemiBold });
            line.Inlines.Add(new System.Windows.Documents.Run($"  {delta:+0.0;-0.0}")
            {
                Foreground = new SolidColorBrush(delta >= 0 ? Color.FromRgb(0x2E, 0x9E, 0x4F) : Color.FromRgb(0xD0, 0x45, 0x30)),
            });
            line.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) Navigate(Dotabuff.HeroCounters(name)); };
            panel.Children.Add(line);
        }
    }

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
