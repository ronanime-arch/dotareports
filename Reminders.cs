using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace DotaReports;

/// <summary>
/// Одно напоминание: «Text» к моменту At по часам матча, дальше каждые Every (пусто — один раз) до Until (пусто — до конца игры).
/// Показывается за Lead секунд до события. Время — «м:сс».
/// </summary>
sealed class ReminderRule
{
    public bool Enabled { get; set; } = true;
    public string Text { get; set; } = "";
    public string At { get; set; } = "0:00";
    public string Every { get; set; } = "";
    public string Until { get; set; } = "";
    public int Lead { get; set; } = 20;
    /// <summary>Цвет в окне поверх игры: ключ из <see cref="ReminderColors"/>; пусто — по тексту.</summary>
    public string Color { get; set; } = "";

    public System.Windows.Media.Color DisplayColor() => ReminderColors.Of(string.IsNullOrEmpty(Color) ? ReminderColors.Guess(Text) : Color);

    /// <summary>Звук: ключ из <see cref="ReminderSound.All"/>; пусто — по тексту.</summary>
    public string Sound { get; set; } = "";

    public string SoundKey() => string.IsNullOrEmpty(Sound) ? ReminderSound.Guess(Text) : Sound;

    /// <summary>Моменты событий (секунды часов матча), у которых время показа попадает в (from, to].</summary>
    public IEnumerable<int> DueBetween(int from, int to)
    {
        if (!ReminderTime.TryParse(At, out int at) || !ReminderTime.TryParse(Every, out int every) || !ReminderTime.TryParse(Until, out int until))
            yield break;
        if (every <= 0)
        {
            if (at - Lead > from && at - Lead <= to) yield return at;
            yield break;
        }
        // первый повтор, чей показ позже from
        long k = Math.Max(0, (long)Math.Floor((double)(from + Lead - at) / every) + 1);
        for (long t = at + k * every; t - Lead <= to; t += every)
        {
            if (until > 0 && t > until) yield break;
            yield return (int)t;
        }
    }

    public ReminderRule Copy() => (ReminderRule)MemberwiseClone();
}

/// <summary>Набор напоминаний под режим: Mode = "TURBO" / "ALL_PICK" выбирается сам по режиму из пика; пусто — только вручную.</summary>
sealed class ReminderProfile
{
    public string Name { get; set; } = "";
    public string Mode { get; set; } = "";
    public ObservableCollection<ReminderRule> Rules { get; set; } = new();

    public ReminderProfile Copy() => new() { Name = Name, Mode = Mode, Rules = new(Rules.Select(r => r.Copy())) };
}

/// <summary>Цвет напоминания: ключ в reminders.json, подпись в окне настроек.</summary>
sealed record ReminderColor(string Key, string Label, Color Value);

static class ReminderColors
{
    public static readonly ReminderColor[] All =
    {
        new("green", "зелёный", Color.FromRgb(0x7C, 0xD9, 0x92)),
        new("blue", "синий", Color.FromRgb(0x4F, 0xA3, 0xFF)),
        new("yellow", "жёлтый", Color.FromRgb(0xFF, 0xD3, 0x4D)),
        new("purple", "фиолетовый", Color.FromRgb(0xB5, 0x7B, 0xFF)),
        new("red", "красный", Color.FromRgb(0xFF, 0x6B, 0x57)),
        new("white", "белый", Color.FromRgb(0xF2, 0xF2, 0xF2)),
    };

    public static Color Of(string? key) => (All.FirstOrDefault(c => c.Key == key) ?? All[0]).Value;

    /// <summary>Цвет по тексту — для правил из старого reminders.json: лотосы синие, баунти жёлтые, мудрость фиолетовая, катапульта красная, остальное зелёное.</summary>
    public static string Guess(string? text)
    {
        var t = (text ?? "").ToLowerInvariant();
        if (t.Contains("лотос")) return "blue";
        if (t.Contains("баунти") || t.Contains("золот")) return "yellow";
        if (t.Contains("мудрост") || t.Contains("опыт")) return "purple";
        if (t.Contains("катапульт")) return "red";
        return "green";
    }
}

static class ReminderTime
{
    /// <summary>«7:00», «0:30», «90» (секунды); пусто — 0.</summary>
    public static bool TryParse(string? text, out int seconds)
    {
        seconds = 0;
        text = text?.Trim() ?? "";
        if (text.Length == 0) return true;
        var parts = text.Split(':');
        if (parts.Length == 1) return int.TryParse(parts[0], out seconds) && seconds >= 0;
        if (parts.Length != 2 || !int.TryParse(parts[0], out int m) || !int.TryParse(parts[1], out int s) || m < 0 || s is < 0 or > 59) return false;
        seconds = m * 60 + s;
        return true;
    }

    public static string Format(int seconds) => $"{seconds / 60}:{seconds % 60:00}";
}

/// <summary>Настройки напоминаний в reminders.json рядом с остальными данными программы.</summary>
sealed class ReminderSettings
{
    public const string Auto = "auto";

    public bool Enabled { get; set; } = true;
    public bool Sound { get; set; } = true;
    /// <summary>"auto" — профиль по режиму из пика; иначе имя профиля.</summary>
    public string Active { get; set; } = Auto;
    public List<ReminderProfile> Profiles { get; set; } = new();

    /// <summary>
    /// Профиль для матча: выбранный вручную или по режиму. Режим не определён (или под него нет профиля) — null:
    /// тогда только <see cref="CommonRules"/>, а не первый профиль, — иначе в Турбо всплывали тайминги обычной игры.
    /// </summary>
    public ReminderProfile? Pick(string? mode)
    {
        if (Active != Auto && Profiles.FirstOrDefault(p => p.Name == Active) is { } chosen) return chosen;
        return mode == null ? null : Profiles.FirstOrDefault(p => p.Mode == mode);
    }

    /// <summary>
    /// Правила, одинаковые во всех профилях с режимом: руны, мудрость, катапульта. У Торментора и лотосов в Турбо
    /// свои тайминги — пока режим не ясен, по ним лучше промолчать, чем напомнить не вовремя.
    /// </summary>
    public ReminderProfile CommonRules()
    {
        var auto = Profiles.Where(p => !string.IsNullOrEmpty(p.Mode)).ToList();
        var rules = auto.Count == 0 ? new List<ReminderRule>()
            : auto[0].Rules.Where(r => r.Enabled && auto.All(p => p.Rules.Any(o => o.Enabled && o.Text == r.Text && o.At == r.At && o.Every == r.Every && o.Until == r.Until))).ToList();
        return new ReminderProfile { Name = "общие тайминги", Rules = new(rules) };
    }

    public ReminderSettings Copy() => new() { Enabled = Enabled, Sound = Sound, Active = Active, Profiles = Profiles.Select(p => p.Copy()).ToList() };

    public static ReminderSettings Load()
    {
        try
        {
            if (File.Exists(Paths.Reminders) && JsonSerializer.Deserialize<ReminderSettings>(File.ReadAllText(Paths.Reminders)) is { Profiles.Count: > 0 } s)
            {
                // правила из файла до появления цветов и звуков — цвет и звук по тексту
                foreach (var r in s.Profiles.SelectMany(p => p.Rules))
                {
                    if (string.IsNullOrEmpty(r.Color)) r.Color = ReminderColors.Guess(r.Text);
                    if (string.IsNullOrEmpty(r.Sound)) r.Sound = ReminderSound.Guess(r.Text);
                }
                return s;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Write("reminders.json не прочитан, беру напоминания по умолчанию: " + ex.Message);
        }
        return Defaults();
    }

    public void Save()
    {
        Directory.CreateDirectory(Paths.Root);
        File.WriteAllText(Paths.Reminders, JsonSerializer.Serialize(this, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // кириллица в файле читаемая
        }));
    }

    // Цвета: вода и руна силы — зелёные, баунти — жёлтые, мудрость — фиолетовая, катапульта — красная, лотосы — синие.
    // Звуки — у каждого свой: капли, подъём, монетки, аккорд, барабан, гонг, волна.
    // Тайминги патча 7.41: руны воды 2:00 и 4:00, руна силы с 6:00 каждые 2:00, баунти каждые 4:00,
    // святилища мудрости (бывшие руны опыта) с 7:00 каждые 7:00, катапульта каждые 5:00, Торментор с 15:00 (в Турбо 7:30), лотосы каждые 3:00 (в Турбо 1:30).
    public static ReminderSettings Defaults() => new()
    {
        Profiles = new()
        {
            new ReminderProfile
            {
                Name = "Обычная игра", Mode = "ALL_PICK",
                Rules = new()
                {
                    new() { Text = "Руна воды — река", Color = "green", Sound = "water", At = "2:00", Every = "2:00", Until = "4:00", Lead = 20 },
                    new() { Text = "Баунти-руны", Color = "yellow", Sound = "bounty", At = "4:00", Every = "4:00", Lead = 20 },
                    new() { Text = "Активная руна — река у мида", Color = "green", Sound = "power", At = "6:00", Every = "2:00", Lead = 20 },
                    new() { Text = "Святилище мудрости — драться за опыт", Color = "purple", Sound = "wisdom", At = "7:00", Every = "7:00", Lead = 30 },
                    new() { Text = "Катапульта выходит на линию", Color = "red", Sound = "siege", At = "5:00", Every = "5:00", Lead = 10 },
                    new() { Text = "Торментор — собирайтесь", Color = "green", Sound = "tormentor", At = "15:00", Lead = 60 },
                    new() { Enabled = false, Text = "Лотосы в пруду", Color = "blue", Sound = "lotus", At = "3:00", Every = "3:00", Lead = 15 },
                },
            },
            new ReminderProfile
            {
                Name = "Турбо", Mode = "TURBO",
                Rules = new()
                {
                    new() { Text = "Руна воды — река", Color = "green", Sound = "water", At = "2:00", Every = "2:00", Until = "4:00", Lead = 20 },
                    new() { Text = "Баунти-руны", Color = "yellow", Sound = "bounty", At = "4:00", Every = "4:00", Lead = 20 },
                    new() { Text = "Активная руна — река у мида", Color = "green", Sound = "power", At = "6:00", Every = "2:00", Lead = 20 },
                    new() { Text = "Святилище мудрости — драться за опыт", Color = "purple", Sound = "wisdom", At = "7:00", Every = "7:00", Lead = 30 },
                    new() { Text = "Катапульта выходит на линию", Color = "red", Sound = "siege", At = "5:00", Every = "5:00", Lead = 10 },
                    new() { Text = "Торментор — собирайтесь", Color = "green", Sound = "tormentor", At = "7:30", Lead = 45 },
                    new() { Enabled = false, Text = "Лотосы в пруду", Color = "blue", Sound = "lotus", At = "1:30", Every = "1:30", Lead = 15 },
                },
            },
        },
    };
}

/// <summary>Следит за часами матча из GSI и говорит, какие напоминания пора показать.</summary>
sealed class ReminderEngine
{
    const int MaxStep = 10; // часы прыгнули дальше (переподключение, повтор) — не засыпаем пачкой старых напоминаний
    int? _last;
    string? _match;

    public void Reset() => _last = null;

    public IEnumerable<(ReminderRule Rule, int At)> Update(string? matchId, int clock, ReminderProfile profile)
    {
        if (matchId != _match)
        {
            _match = matchId;
            _last = null;
        }
        var result = new List<(ReminderRule, int)>();
        if (_last is int last && clock > last && clock - last <= MaxStep)
            foreach (var rule in profile.Rules.Where(r => r.Enabled))
                foreach (var t in rule.DueBetween(last, clock))
                    result.Add((rule, t));
        _last = clock;
        return result;
    }
}

/// <summary>Окно настройки напоминаний: профили и таблица таймингов. Правится копия, в файл — по «Сохранить».</summary>
sealed class ReminderWindow : Window
{
    static readonly (string Label, string Mode)[] Modes = { ("Турбо", "TURBO"), ("All Pick", "ALL_PICK"), ("только вручную", "") };

    readonly ReminderSettings _edit;
    readonly ObservableCollection<ReminderProfile> _profiles;
    readonly ComboBox _profileBox = new() { MinWidth = 180, DisplayMemberPath = nameof(ReminderProfile.Name) };
    readonly ComboBox _activeBox = new() { MinWidth = 220 };
    readonly TextBox _nameBox = new() { MinWidth = 180 };
    readonly ComboBox _modeBox = new() { MinWidth = 140 };
    readonly DataGrid _grid = new()
    {
        AutoGenerateColumns = false,
        CanUserAddRows = false,
        CanUserDeleteRows = false,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        Height = 300,
        Margin = new Thickness(0, 8, 0, 8),
    };

    /// <summary>Сохранено — новые настройки.</summary>
    public event Action<ReminderSettings>? Saved;

    public ReminderWindow(ReminderSettings current)
    {
        _edit = current.Copy();
        _profiles = new ObservableCollection<ReminderProfile>(_edit.Profiles);
        Title = "Dota Reports — напоминания";
        Width = 1000;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanMinimize;

        var enabled = new CheckBox { Content = "Напоминания включены", IsChecked = _edit.Enabled, Margin = new Thickness(0, 0, 16, 0) };
        enabled.Click += (_, _) => _edit.Enabled = enabled.IsChecked == true;
        var sound = new CheckBox { Content = "Звук", IsChecked = _edit.Sound };
        sound.Click += (_, _) => _edit.Sound = sound.IsChecked == true;

        _activeBox.SelectionChanged += (_, _) =>
        {
            if (_activeBox.SelectedItem is ComboBoxItem { Tag: string tag }) _edit.Active = tag;
        };

        _profileBox.ItemsSource = _profiles;
        _profileBox.SelectionChanged += (_, _) => ShowProfile();
        _nameBox.LostFocus += (_, _) => RenameProfile();
        foreach (var (label, mode) in Modes) _modeBox.Items.Add(new ComboBoxItem { Content = label, Tag = mode });
        _modeBox.SelectionChanged += (_, _) =>
        {
            if (_profileBox.SelectedItem is ReminderProfile p && _modeBox.SelectedItem is ComboBoxItem { Tag: string mode }) p.Mode = mode;
        };

        _grid.Columns.Add(new DataGridCheckBoxColumn { Header = "Вкл", Binding = Bind(nameof(ReminderRule.Enabled)), Width = 40 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Что напомнить", Binding = Bind(nameof(ReminderRule.Text)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Время", Binding = Bind(nameof(ReminderRule.At)), Width = 70 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Повтор", Binding = Bind(nameof(ReminderRule.Every)), Width = 70 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "До", Binding = Bind(nameof(ReminderRule.Until)), Width = 70 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "За сколько, с", Binding = Bind(nameof(ReminderRule.Lead)), Width = 90 });
        _grid.Columns.Add(new DataGridComboBoxColumn
        {
            Header = "Цвет",
            ItemsSource = ReminderColors.All,
            DisplayMemberPath = nameof(ReminderColor.Label),
            SelectedValuePath = nameof(ReminderColor.Key),
            SelectedValueBinding = Bind(nameof(ReminderRule.Color)),
            Width = 100,
        });
        _grid.Columns.Add(new DataGridComboBoxColumn
        {
            Header = "Звук",
            ItemsSource = ReminderSound.All,
            DisplayMemberPath = nameof(ReminderSoundKind.Label),
            SelectedValuePath = nameof(ReminderSoundKind.Key),
            SelectedValueBinding = Bind(nameof(ReminderRule.Sound)),
            Width = 100,
        });
        // ▶ — послушать звук строки
        var play = new FrameworkElementFactory(typeof(Button));
        play.SetValue(ContentControl.ContentProperty, "▶");
        play.SetValue(Control.PaddingProperty, new Thickness(4, 0, 4, 0));
        play.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is FrameworkElement { DataContext: ReminderRule rule }) ReminderSound.Play(rule.SoundKey());
        }));
        _grid.Columns.Add(new DataGridTemplateColumn { CellTemplate = new DataTemplate { VisualTree = play }, Width = 34 });

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(Row(enabled, sound));
        panel.Children.Add(Row(new TextBlock { Text = "В игре использовать:", Margin = new Thickness(0, 0, 8, 0) }, _activeBox));
        panel.Children.Add(new Separator { Margin = new Thickness(0, 10, 0, 10) });
        panel.Children.Add(Row(
            new TextBlock { Text = "Профиль:", Margin = new Thickness(0, 0, 8, 0) }, _profileBox,
            Button("Новый", AddProfile), Button("Удалить профиль", RemoveProfile)));
        panel.Children.Add(Row(
            new TextBlock { Text = "Название:", Margin = new Thickness(0, 0, 8, 0) }, _nameBox,
            new TextBlock { Text = "Режим:", Margin = new Thickness(12, 0, 8, 0) }, _modeBox));
        panel.Children.Add(_grid);
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = System.Windows.Media.Brushes.DimGray,
            Text = "Время — по часам матча, «м:сс». «Повтор» пусто — один раз; «До» пусто — до конца игры. " +
                   "«За сколько» — во сколько секунд до события показать напоминание. «Звук» — свой у каждого, ▶ — послушать. " +
                   "«Авто» выбирает профиль по режиму: с экрана пика или по доходу в первую минуту (в Турбо он вдвое выше). " +
                   "Пока режим не определён — только тайминги, одинаковые во всех профилях. Чужие игры, повторы и кастомки — без напоминаний.",
        });
        var buttons = Row(Button("Добавить строку", AddRule), Button("Удалить строку", RemoveRule), Button("Вернуть стандартные", ResetDefaults));
        var save = new Button { Content = "Сохранить", Padding = new Thickness(16, 2, 16, 2), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        save.Click += (_, _) => Save();
        var cancel = new Button { Content = "Отмена", Padding = new Thickness(16, 2, 16, 2), IsCancel = true };
        var bottom = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var right = Row(save, cancel);
        DockPanel.SetDock(right, Dock.Right);
        bottom.Children.Add(right);
        bottom.Children.Add(buttons);
        panel.Children.Add(bottom);
        Content = panel;

        FillActive();
        _profileBox.SelectedIndex = 0;
    }

    static Binding Bind(string path) => new(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };

    static StackPanel Row(params UIElement[] items)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        foreach (var item in items)
        {
            if (item is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(item);
        }
        return row;
    }

    static Button Button(string text, Action onClick)
    {
        var b = new Button { Content = text, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(8, 0, 0, 0) };
        b.Click += (_, _) => onClick();
        return b;
    }

    void FillActive()
    {
        var active = _edit.Active;
        _activeBox.Items.Clear();
        _activeBox.Items.Add(new ComboBoxItem { Content = "Авто — по режиму матча", Tag = ReminderSettings.Auto });
        foreach (var p in _profiles) _activeBox.Items.Add(new ComboBoxItem { Content = p.Name, Tag = p.Name });
        _activeBox.SelectedItem = _activeBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == active) ?? _activeBox.Items[0];
    }

    void ShowProfile()
    {
        if (_profileBox.SelectedItem is not ReminderProfile p)
        {
            _grid.ItemsSource = null;
            return;
        }
        _nameBox.Text = p.Name;
        _modeBox.SelectedItem = _modeBox.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == p.Mode) ?? _modeBox.Items[^1];
        _grid.ItemsSource = p.Rules;
    }

    void RenameProfile()
    {
        if (_profileBox.SelectedItem is not ReminderProfile p) return;
        var name = _nameBox.Text.Trim();
        if (name.Length == 0 || name == p.Name) return;
        if (_profiles.Any(o => o != p && o.Name == name))
        {
            MessageBox.Show(this, $"Профиль «{name}» уже есть.", Title);
            _nameBox.Text = p.Name;
            return;
        }
        if (_edit.Active == p.Name) _edit.Active = name;
        p.Name = name;
        _profileBox.Items.Refresh();
        FillActive();
    }

    void AddProfile()
    {
        int n = 1;
        while (_profiles.Any(p => p.Name == $"Профиль {n}")) n++;
        var source = _profileBox.SelectedItem as ReminderProfile;
        var profile = source?.Copy() ?? new ReminderProfile();
        profile.Name = $"Профиль {n}";
        profile.Mode = "";
        _profiles.Add(profile);
        FillActive();
        _profileBox.SelectedItem = profile;
    }

    void RemoveProfile()
    {
        if (_profileBox.SelectedItem is not ReminderProfile p) return;
        if (_profiles.Count == 1)
        {
            MessageBox.Show(this, "Нужен хотя бы один профиль.", Title);
            return;
        }
        if (_edit.Active == p.Name) _edit.Active = ReminderSettings.Auto;
        _profiles.Remove(p);
        FillActive();
        _profileBox.SelectedIndex = 0;
    }

    void AddRule()
    {
        if (_profileBox.SelectedItem is not ReminderProfile p) return;
        var rule = new ReminderRule { Text = "Новое напоминание", At = "10:00", Color = "green", Sound = "soft" };
        p.Rules.Add(rule);
        _grid.SelectedItem = rule;
        _grid.ScrollIntoView(rule);
    }

    void RemoveRule()
    {
        if (_profileBox.SelectedItem is ReminderProfile p && _grid.SelectedItem is ReminderRule rule)
            p.Rules.Remove(rule);
    }

    void ResetDefaults()
    {
        if (MessageBox.Show(this, "Заменить все профили стандартными таймингами?", Title, MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        _profiles.Clear();
        foreach (var p in ReminderSettings.Defaults().Profiles) _profiles.Add(p);
        _edit.Active = ReminderSettings.Auto;
        FillActive();
        _profileBox.SelectedIndex = 0;
    }

    void Save()
    {
        _grid.CommitEdit(DataGridEditingUnit.Row, true);
        RenameProfile();
        foreach (var p in _profiles)
            foreach (var r in p.Rules)
            {
                if (!ReminderTime.TryParse(r.At, out _) || !ReminderTime.TryParse(r.Every, out _) || !ReminderTime.TryParse(r.Until, out _))
                {
                    _profileBox.SelectedItem = p;
                    _grid.SelectedItem = r;
                    MessageBox.Show(this, $"«{r.Text}»: время пишется как 7:00 или 0:30.", Title);
                    return;
                }
                r.Lead = Math.Max(0, r.Lead);
            }
        _edit.Profiles = _profiles.ToList();
        try
        {
            _edit.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, "Не удалось сохранить: " + ex.Message, Title);
            return;
        }
        Saved?.Invoke(_edit);
        Close();
    }
}

/// <summary>Звук напоминания: ключ в reminders.json, подпись в окне настроек.</summary>
sealed record ReminderSoundKind(string Key, string Label);

/// <summary>
/// Звуки напоминаний — у каждого свой мотив, чтобы узнавать на слух, не глядя на экран. Все низкие и мягкие
/// (основные тона 90–590 Гц, без звона), стерео: ноты разнесены влево-вправо. Синтезируются в памяти, тихо (пик около −10 дБ).
/// </summary>
static class ReminderSound
{
    const int Rate = 44100;
    const double Peak = 0.32;

    public static readonly ReminderSoundKind[] All =
    {
        new("water", "капли"),
        new("power", "подъём"),
        new("bounty", "монетки"),
        new("wisdom", "аккорд"),
        new("siege", "барабан"),
        new("tormentor", "гонг"),
        new("lotus", "волна"),
        new("soft", "мягкий"),
        new("none", "без звука"),
    };

    // Нота: частота (Glide > 0 — за Glide с съезжает к Freq·GlideTo), начало, панорама −1…1 (PanTo — уплывает туда до конца звука),
    // атака и затухание (с), доли 2-й и 3-й гармоник, вибрато (доля частоты, 5,5 Гц).
    sealed record Tone(double Freq, double Start, double Pan, double Attack, double Decay,
        double H2 = 0.1, double H3 = 0, double Glide = 0, double GlideTo = 1, double Vibrato = 0, double PanTo = double.NaN);

    static readonly Dictionary<string, (double Seconds, Tone[] Tones)> Motifs = new()
    {
        // капли: две ноты, быстро съезжающие вниз (640 → 300 Гц), слева и справа
        ["water"] = (0.5, new[]
        {
            new Tone(640, 0, -0.5, 0.003, 0.07, H2: 0, Glide: 0.03, GlideTo: 0.47),
            new Tone(560, 0.17, 0.5, 0.003, 0.07, H2: 0, Glide: 0.03, GlideTo: 0.47),
        }),
        // подъём: до-ми-соль снизу вверх, слева направо
        ["power"] = (0.8, new[]
        {
            new Tone(261.6, 0, -0.4, 0.012, 0.16, H2: 0.15),
            new Tone(329.6, 0.11, 0, 0.012, 0.16, H2: 0.15),
            new Tone(392.0, 0.22, 0.4, 0.012, 0.16, H2: 0.15),
        }),
        // монетки: три коротких щипка, справа-слева-справа
        ["bounty"] = (0.5, new[]
        {
            new Tone(523.3, 0, 0.5, 0.003, 0.07, H2: 0.25),
            new Tone(392.0, 0.08, -0.5, 0.003, 0.07, H2: 0.25),
            new Tone(523.3, 0.16, 0.5, 0.003, 0.07, H2: 0.25),
        }),
        // аккорд: до мажор медленно нарастает и проплывает слева направо
        ["wisdom"] = (1.2, new[]
        {
            new Tone(261.6, 0, -0.8, 0.15, 0.45, H2: 0.08, PanTo: 0.8),
            new Tone(329.6, 0, -0.8, 0.15, 0.45, H2: 0.08, PanTo: 0.8),
            new Tone(392.0, 0, -0.8, 0.15, 0.45, H2: 0.08, PanTo: 0.8),
        }),
        // барабан: два низких удара с падением тона (180 → 90 Гц)
        ["siege"] = (0.6, new[]
        {
            new Tone(180, 0, -0.3, 0.004, 0.12, H2: 0.4, Glide: 0.05, GlideTo: 0.5),
            new Tone(180, 0.22, 0.3, 0.004, 0.12, H2: 0.4, Glide: 0.05, GlideTo: 0.5),
        }),
        // гонг: низкие ми и до с обертонами и лёгким дрожанием, по центру
        ["tormentor"] = (1.4, new[]
        {
            new Tone(164.8, 0, 0, 0.02, 0.5, H2: 0.5, H3: 0.3, Vibrato: 0.006),
            new Tone(130.8, 0.35, 0, 0.02, 0.5, H2: 0.5, H3: 0.3, Vibrato: 0.006),
        }),
        // волна: две плавные ноты вверх на большую терцию с вибрато, справа налево
        ["lotus"] = (0.9, new[]
        {
            new Tone(329.6, 0, 0.5, 0.06, 0.25, Vibrato: 0.01),
            new Tone(415.3, 0.2, -0.5, 0.06, 0.25, Vibrato: 0.01),
        }),
        // мягкий: ля и ми вниз — для своих напоминаний
        ["soft"] = (0.7, new[]
        {
            new Tone(440.0, 0, -0.3, 0.025, 0.12),
            new Tone(329.6, 0.16, 0.3, 0.025, 0.12),
        }),
    };

    static readonly Dictionary<string, System.Media.SoundPlayer> Players = new();

    /// <summary>Звук по тексту — для правил без выбранного звука.</summary>
    public static string Guess(string? text)
    {
        var t = (text ?? "").ToLowerInvariant();
        if (t.Contains("лотос")) return "lotus";
        if (t.Contains("баунти") || t.Contains("золот")) return "bounty";
        if (t.Contains("мудрост") || t.Contains("опыт")) return "wisdom";
        if (t.Contains("катапульт")) return "siege";
        if (t.Contains("торментор")) return "tormentor";
        if (t.Contains("вод")) return "water";
        if (t.Contains("активн") || t.Contains("силы")) return "power";
        return "soft";
    }

    /// <summary>Проиграть звук по ключу, не дожидаясь конца; «без звука» — ничего, неизвестный ключ — «мягкий».</summary>
    public static void Play(string? key)
    {
        if (key == "none") return;
        if (key == null || !Motifs.ContainsKey(key)) key = "soft";
        try
        {
            if (!Players.TryGetValue(key, out var player))
            {
                player = new System.Media.SoundPlayer(new MemoryStream(Wav(Motifs[key])));
                player.Load();
                Players[key] = player;
            }
            player.Play();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            Log.Write("Звук напоминания не проигран: " + ex.Message);
        }
    }

    // Ноты складываются в левый и правый канал (панорама с постоянной громкостью); в конце 50 мс — в ноль.
    static byte[] Wav((double Seconds, Tone[] Tones) motif)
    {
        int n = (int)(Rate * motif.Seconds);
        var left = new double[n];
        var right = new double[n];
        foreach (var tone in motif.Tones)
        {
            double phase = 0, length = Math.Max(motif.Seconds - tone.Start, 1e-3);
            for (int i = (int)(tone.Start * Rate); i < n; i++)
            {
                double tt = (double)i / Rate - tone.Start;
                double f = tone.Freq * (tone.Glide > 0 ? tone.GlideTo + (1 - tone.GlideTo) * Math.Exp(-tt / tone.Glide) : 1)
                           * (1 + tone.Vibrato * Math.Sin(2 * Math.PI * 5.5 * tt));
                phase += 2 * Math.PI * f / Rate;
                double env = Math.Min(tt / tone.Attack, 1) * Math.Exp(-tt / tone.Decay);
                double v = env * (Math.Sin(phase) + tone.H2 * Math.Sin(2 * phase) + tone.H3 * Math.Sin(3 * phase));
                double pan = double.IsNaN(tone.PanTo) ? tone.Pan : tone.Pan + (tone.PanTo - tone.Pan) * Math.Min(tt / length, 1);
                double a = (pan + 1) * Math.PI / 4;
                left[i] += v * Math.Cos(a);
                right[i] += v * Math.Sin(a);
            }
        }
        double peak = 1e-9;
        for (int i = 0; i < n; i++) peak = Math.Max(peak, Math.Max(Math.Abs(left[i]), Math.Abs(right[i])));
        int fade = Rate / 20;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        // WAV: PCM, 16 бит, стерео
        w.Write("RIFF"u8); w.Write(36 + n * 4); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(Rate); w.Write(Rate * 4); w.Write((short)4); w.Write((short)16);
        w.Write("data"u8); w.Write(n * 4);
        for (int i = 0; i < n; i++)
        {
            double k = Peak / peak * (i >= n - fade ? (n - i) / (double)fade : 1);
            w.Write((short)Math.Round(left[i] * k * 32767));
            w.Write((short)Math.Round(right[i] * k * 32767));
        }
        w.Flush();
        return ms.ToArray();
    }
}
