using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DotaReports;

/// <summary>
/// Микшер звуков напоминаний — вкладка «Звуки» окна напоминаний: слева звуки, справа их ноты с ползунками и «Слушать».
/// Правит звуки на месте; в reminders.json они попадают по «Сохранить» окна.
/// </summary>
sealed class SoundMixer : Grid
{
    static readonly HashSet<string> BuiltIn = ReminderSound.Defaults().Select(d => d.Key).ToHashSet();
    static readonly string[] NoteNames = { "до", "до♯", "ре", "ре♯", "ми", "фа", "фа♯", "соль", "соль♯", "ля", "ля♯", "си" };

    readonly ObservableCollection<ReminderSoundDef> _sounds;
    readonly ListBox _list = new() { DisplayMemberPath = nameof(ReminderSoundDef.Label), Height = 300 };
    readonly TextBox _name = new() { MinWidth = 220 };
    readonly Slider _seconds = new() { Minimum = 0.2, Maximum = 2, SmallChange = 0.05, LargeChange = 0.25, TickFrequency = 0.05, IsSnapToTickEnabled = true, Width = 300 };
    readonly TextBlock _secondsText = new() { Margin = new Thickness(8, 0, 0, 0) };
    readonly TextBlock _info = new() { Margin = new Thickness(12, 0, 0, 0) };
    readonly StackPanel _notes = new();
    readonly Button _remove, _reset;
    bool _loading;

    /// <summary>Звук переименован или возвращён к исходному — обновить списки выбора звука.</summary>
    public event Action? Renamed;

    ReminderSoundDef? Current => _list.SelectedItem as ReminderSoundDef;

    public SoundMixer(ObservableCollection<ReminderSoundDef> sounds)
    {
        _sounds = sounds;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(190) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // слева: звуки и что с ними сделать
        var left = new StackPanel { Margin = new Thickness(0, 0, 14, 0) };
        _list.ItemsSource = _sounds;
        _list.SelectionChanged += (_, _) => ShowSound();
        left.Children.Add(_list);
        left.Children.Add(Btn("Новый звук", AddSound));
        _remove = Btn("Удалить звук", RemoveSound);
        _reset = Btn("Как было", ResetSound);
        left.Children.Add(_remove);
        left.Children.Add(_reset);
        left.Children.Add(new TextBlock
        {
            Text = "Встроенные звуки не удаляются — «Как было» вернёт исходный. Новый звук начинается с копии выбранного.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray, Margin = new Thickness(0, 8, 0, 0),
        });
        Children.Add(left);

        // справа: название, длина, «Слушать», ноты
        _name.LostFocus += (_, _) =>
        {
            if (Current is not { } d) return;
            var label = _name.Text.Trim();
            if (label.Length == 0 || label == d.Label) return;
            d.Label = label;
            _list.Items.Refresh();
            Renamed?.Invoke();
        };
        _seconds.ValueChanged += (_, e) =>
        {
            if (_loading || Current is not { } d) return;
            d.Seconds = Math.Round(e.NewValue, 2);
            _secondsText.Text = $"{d.Seconds:0.00} с";
        };
        var play = new Button { Content = "▶  Слушать", Padding = new Thickness(14, 4, 14, 4), FontWeight = FontWeights.SemiBold };
        play.Click += (_, _) =>
        {
            if (Current is { } d) ReminderSound.Play(d);
        };

        var right = new StackPanel();
        right.Children.Add(Line(Caption("Название:"), _name));
        right.Children.Add(Line(Caption("Длина звука:"), _seconds, _secondsText));
        right.Children.Add(Line(play, _info));
        right.Children.Add(new ScrollViewer
        {
            Content = _notes, Height = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 4, 0, 4),
        });
        right.Children.Add(Line(Btn("+ Добавить ноту", AddNote), new TextBlock
        {
            Text = "Чем выше гармоники и короче атака, тем звонче. Панорама: −1 — слева, +1 — справа. Громкость подгоняется сама.",
            Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, Margin = new Thickness(12, 6, 0, 0),
        }));
        SetColumn(right, 1);
        Children.Add(right);

        _list.SelectedIndex = 0;
    }

    void ShowSound()
    {
        _notes.Children.Clear();
        if (Current is not { } d) return;
        _loading = true;
        _name.Text = d.Label;
        _seconds.Value = d.Seconds;
        _loading = false;
        _secondsText.Text = $"{d.Seconds:0.00} с";
        _remove.IsEnabled = !BuiltIn.Contains(d.Key);
        _reset.IsEnabled = BuiltIn.Contains(d.Key);
        for (int i = 0; i < d.Notes.Count; i++) _notes.Children.Add(NoteEditor(d, d.Notes[i], i));
        UpdateInfo();
    }

    UIElement NoteEditor(ReminderSoundDef d, SoundNote n, int index)
    {
        var box = new StackPanel();
        var title = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 12, 0) };
        void Title() => title.Text = $"Нота {index + 1} · {Pitch(n.Freq)}";
        Title();
        var remove = Btn("Убрать ноту", () =>
        {
            d.Notes.Remove(n);
            ShowSound();
        });
        remove.IsEnabled = d.Notes.Count > 1;
        box.Children.Add(Line(title, remove));
        box.Children.Add(SliderRow("Частота", 40, 1000, 0.1, n.Freq, v => $"{v:0.#} Гц", v => { n.Freq = v; Title(); }));
        box.Children.Add(SliderRow("Начало", 0, 1.5, 0.01, n.Start, v => $"{v:0.00} с", v => n.Start = v));
        box.Children.Add(SliderRow("Панорама", -1, 1, 0.1, n.Pan, PanText, v => n.Pan = v));
        box.Children.Add(SliderRow("Атака", 0.001, 0.3, 0.001, n.Attack, v => $"{v * 1000:0} мс", v => n.Attack = v));
        box.Children.Add(SliderRow("Затухание", 0.02, 1, 0.01, n.Decay, v => $"{v:0.00} с", v => n.Decay = v));
        box.Children.Add(SliderRow("2-я гармоника", 0, 1, 0.01, n.H2, v => $"{v * 100:0}%", v => n.H2 = v));
        box.Children.Add(SliderRow("3-я гармоника", 0, 1, 0.01, n.H3, v => $"{v * 100:0}%", v => n.H3 = v));
        box.Children.Add(SliderRow("Вибрато", 0, 0.05, 0.001, n.Vibrato, v => $"{v * 100:0.#}%", v => n.Vibrato = v));

        // скольжение тона: включается галочкой, скорость помним и когда выключено
        double glide = n.Glide > 0 ? n.Glide : 0.03;
        var glideRows = new StackPanel { Visibility = n.Glide > 0 ? Visibility.Visible : Visibility.Collapsed };
        glideRows.Children.Add(SliderRow("Скорость скольжения", 0.005, 0.3, 0.005, glide, v => $"{v:0.000} с", v =>
        {
            glide = v;
            if (n.Glide > 0) n.Glide = v;
        }));
        glideRows.Children.Add(SliderRow("Куда скользит", 0.3, 2, 0.01, n.GlideTo, v => $"×{v:0.00}", v => n.GlideTo = v));
        var glideOn = new CheckBox { Content = "Тон скользит (как капля или удар)", IsChecked = n.Glide > 0, Margin = new Thickness(0, 4, 0, 2) };
        glideOn.Click += (_, _) =>
        {
            n.Glide = glideOn.IsChecked == true ? glide : 0;
            glideRows.Visibility = glideOn.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
            UpdateInfo();
        };

        // звук уплывает в сторону к концу
        double panTo = n.PanTo ?? 0.8;
        var panRow = SliderRow("Уплывает к", -1, 1, 0.1, panTo, PanText, v =>
        {
            panTo = v;
            if (n.PanTo != null) n.PanTo = v;
        });
        panRow.Visibility = n.PanTo != null ? Visibility.Visible : Visibility.Collapsed;
        var panOn = new CheckBox { Content = "Звук уплывает в сторону", IsChecked = n.PanTo != null, Margin = new Thickness(0, 2, 0, 2) };
        panOn.Click += (_, _) =>
        {
            n.PanTo = panOn.IsChecked == true ? panTo : null;
            panRow.Visibility = panOn.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        };

        box.Children.Add(glideOn);
        box.Children.Add(glideRows);
        box.Children.Add(panOn);
        box.Children.Add(panRow);
        return new Border
        {
            BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(10, 6, 10, 8), Margin = new Thickness(0, 0, 8, 8), Child = box,
        };
    }

    FrameworkElement SliderRow(string caption, double min, double max, double step, double value, Func<double, string> show, Action<double> set)
    {
        var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        var text = new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider
        {
            Minimum = min, Maximum = max, SmallChange = step, LargeChange = step * 10, TickFrequency = step, IsSnapToTickEnabled = true,
            Value = value, VerticalAlignment = VerticalAlignment.Center,
        };
        var output = new TextBlock { Text = show(value), TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        slider.ValueChanged += (_, e) =>
        {
            double v = Math.Round(e.NewValue, 4);
            set(v);
            output.Text = show(v);
            UpdateInfo();
        };
        SetColumn(slider, 1);
        SetColumn(output, 2);
        row.Children.Add(text);
        row.Children.Add(slider);
        row.Children.Add(output);
        return row;
    }

    void UpdateInfo()
    {
        if (Current is not { } d)
        {
            _info.Text = "";
            return;
        }
        double hi = ReminderSound.Highest(d);
        _info.Text = $"нот: {d.Notes.Count} · самая высокая частота ≈ {hi:0} Гц" + (hi > 1500 ? " — может звенеть" : "");
        _info.Foreground = hi > 1500 ? Brushes.OrangeRed : Brushes.DimGray;
    }

    void AddSound()
    {
        int k = 1;
        while (_sounds.Any(s => s.Key == $"custom{k}")) k++;
        var d = Current?.Copy() ?? new ReminderSoundDef { Notes = { new SoundNote() } };
        d.Key = $"custom{k}";
        d.Label = $"свой звук {k}";
        _sounds.Add(d);
        _list.SelectedItem = d;
        _list.ScrollIntoView(d);
    }

    void RemoveSound()
    {
        if (Current is not { } d || BuiltIn.Contains(d.Key)) return;
        int i = _list.SelectedIndex;
        _sounds.Remove(d);
        _list.SelectedIndex = Math.Min(i, _sounds.Count - 1);
    }

    void ResetSound()
    {
        if (Current is not { } d || ReminderSound.Defaults().FirstOrDefault(x => x.Key == d.Key) is not { } original) return;
        d.Label = original.Label;
        d.Seconds = original.Seconds;
        d.Notes = original.Notes;
        _list.Items.Refresh();
        ShowSound();
        Renamed?.Invoke();
    }

    void AddNote()
    {
        if (Current is not { } d) return;
        var last = d.Notes.LastOrDefault();
        d.Notes.Add(new SoundNote { Freq = last?.Freq ?? 392, Start = Math.Min(1.5, Math.Round((last?.Start ?? -0.15) + 0.15, 2)) });
        ShowSound();
    }

    static string Pitch(double f)
    {
        int m = (int)Math.Round(69 + 12 * Math.Log2(Math.Max(f, 8) / 440));
        return $"{NoteNames[(m % 12 + 12) % 12]} {m / 12 - 1}";
    }

    static string PanText(double v) => Math.Abs(v) < 0.05 ? "центр" : (v < 0 ? "слева " : "справа ") + $"{Math.Abs(v):0.0}";

    static TextBlock Caption(string text) => new() { Text = text, Width = 100 };

    static Button Btn(string text, Action click)
    {
        var b = new Button { Content = text, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 6, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        b.Click += (_, _) => click();
        return b;
    }

    static StackPanel Line(params UIElement[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        foreach (var item in items)
        {
            if (item is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(item);
        }
        return row;
    }
}
