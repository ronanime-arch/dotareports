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

    /// <summary>Звук: ключ из <see cref="ReminderSettings.Sounds"/> или «none»; пусто — по тексту.</summary>
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
    /// <summary>Звуки — встроенные и свои из микшера; на их ключи ссылаются напоминания.</summary>
    public List<ReminderSoundDef> Sounds { get; set; } = new();
    /// <summary>Рошан: «может возродиться» и «жив» после убийства (по событию GSI), своим звуком.</summary>
    public bool Roshan { get; set; } = true;
    public string RoshanSound { get; set; } = "roshan";

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

    public ReminderSettings Copy() => new()
    {
        Enabled = Enabled, Sound = Sound, Active = Active, Profiles = Profiles.Select(p => p.Copy()).ToList(),
        Sounds = Sounds.Select(x => x.Copy()).ToList(), Roshan = Roshan, RoshanSound = RoshanSound,
    };

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
                // встроенные звуки, которых в файле ещё нет (файл старше микшера или звук добавлен в новой версии)
                s.Sounds ??= new();
                foreach (var d in ReminderSound.Defaults().Where(d => s.Sounds.All(x => x.Key != d.Key)))
                    s.Sounds.Add(d);
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
        Sounds = ReminderSound.Defaults(),
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
    // звуки правятся на вкладке «Звуки»; в списках выбора — они же и «без звука»
    readonly ObservableCollection<ReminderSoundDef> _sounds;
    readonly ObservableCollection<ReminderSoundDef> _soundChoices = new();
    static readonly ReminderSoundDef Silent = new() { Key = ReminderSound.None, Label = "без звука" };
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
        _sounds = new ObservableCollection<ReminderSoundDef>(_edit.Sounds);
        FillSoundChoices();
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
            ItemsSource = _soundChoices,
            DisplayMemberPath = nameof(ReminderSoundDef.Label),
            SelectedValuePath = nameof(ReminderSoundDef.Key),
            SelectedValueBinding = Bind(nameof(ReminderRule.Sound)),
            Width = 100,
        });
        // ▶ — послушать звук строки (как он сейчас в микшере, даже не сохранённый)
        var play = new FrameworkElementFactory(typeof(Button));
        play.SetValue(ContentControl.ContentProperty, "▶");
        play.SetValue(Control.PaddingProperty, new Thickness(4, 0, 4, 0));
        play.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is FrameworkElement { DataContext: ReminderRule rule }) PlaySound(rule.SoundKey());
        }));
        _grid.Columns.Add(new DataGridTemplateColumn { CellTemplate = new DataTemplate { VisualTree = play }, Width = 34 });

        // Рошан: по событию GSI «Рошан убит», а не по часам — отдельной строкой
        var roshan = new CheckBox { Content = "Рошан: «может возродиться» и «жив» после убийства", IsChecked = _edit.Roshan, Margin = new Thickness(0, 0, 12, 0) };
        roshan.Click += (_, _) => _edit.Roshan = roshan.IsChecked == true;
        var roshanSound = new ComboBox
        {
            MinWidth = 120, ItemsSource = _soundChoices,
            DisplayMemberPath = nameof(ReminderSoundDef.Label), SelectedValuePath = nameof(ReminderSoundDef.Key), SelectedValue = _edit.RoshanSound,
        };
        roshanSound.SelectionChanged += (_, _) =>
        {
            if (roshanSound.SelectedValue is string key) _edit.RoshanSound = key;
        };

        var panel = new StackPanel();
        panel.Children.Add(Row(enabled, sound));
        panel.Children.Add(Row(roshan, new TextBlock { Text = "звук:", Margin = new Thickness(0, 0, 6, 0) }, roshanSound,
            Button("▶", () => PlaySound(_edit.RoshanSound))));
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
                   "Пока режим не определён — только тайминги, одинаковые во всех профилях. Чужие игры, повторы и кастомки — без напоминаний. " +
                   "Сами звуки — на вкладке «Звуки».",
        });
        panel.Children.Add(Row(Button("Добавить строку", AddRule), Button("Удалить строку", RemoveRule), Button("Вернуть стандартные", ResetDefaults)));

        // Списки выбора меняем точечно: если их очистить, выпадающие списки в таблице запишут в напоминания пустой звук.
        var mixer = new SoundMixer(_sounds);
        _sounds.CollectionChanged += (_, e) =>
        {
            foreach (ReminderSoundDef d in e.OldItems ?? Array.Empty<ReminderSoundDef>()) _soundChoices.Remove(d);
            foreach (ReminderSoundDef d in e.NewItems ?? Array.Empty<ReminderSoundDef>()) _soundChoices.Insert(_soundChoices.Count - 1, d);
        };
        mixer.Renamed += () =>
        {
            _grid.Items.Refresh();
            roshanSound.Items.Refresh();
        };
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Напоминания", Content = new Border { Padding = new Thickness(10), Child = panel } });
        tabs.Items.Add(new TabItem { Header = "Звуки", Content = new Border { Padding = new Thickness(10), Child = mixer } });
        var save = new Button { Content = "Сохранить", Padding = new Thickness(16, 2, 16, 2), IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        save.Click += (_, _) => Save();
        var cancel = new Button { Content = "Отмена", Padding = new Thickness(16, 2, 16, 2), IsCancel = true };
        var bottom = Row(save, cancel);
        bottom.HorizontalAlignment = HorizontalAlignment.Right;
        bottom.Margin = new Thickness(0, 10, 0, 0);
        var root = new StackPanel { Margin = new Thickness(14) };
        root.Children.Add(tabs);
        root.Children.Add(bottom);
        Content = root;

        FillActive();
        _profileBox.SelectedIndex = 0;
    }

    static Binding Bind(string path) => new(path) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged };

    // списки выбора звука: звуки из микшера и «без звука» (последним)
    void FillSoundChoices()
    {
        _soundChoices.Clear();
        foreach (var d in _sounds) _soundChoices.Add(d);
        _soundChoices.Add(Silent);
    }

    void PlaySound(string key)
    {
        if (key == ReminderSound.None) return;
        var def = _sounds.FirstOrDefault(d => d.Key == key) ?? _sounds.FirstOrDefault(d => d.Key == "soft");
        if (def != null) ReminderSound.Play(def);
    }

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
        _edit.Sounds = _sounds.ToList();
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

/// <summary>Нота звука напоминания — то, что крутится в микшере (вкладка «Звуки»).</summary>
sealed class SoundNote
{
    public double Freq { get; set; } = 392;      // основной тон, Гц
    public double Start { get; set; }            // с от начала звука
    public double Pan { get; set; }              // −1 слева … +1 справа
    public double Attack { get; set; } = 0.02;   // нарастание, с
    public double Decay { get; set; } = 0.15;    // затухание, с
    public double H2 { get; set; } = 0.1;        // доля 2-й гармоники
    public double H3 { get; set; }               // доля 3-й гармоники
    public double Glide { get; set; }            // > 0 — за Glide с тон съезжает к Freq·GlideTo
    public double GlideTo { get; set; } = 1;
    public double Vibrato { get; set; }          // доля частоты, 5,5 Гц
    public double? PanTo { get; set; }           // звук уплывает туда к концу; null — стоит на месте

    public SoundNote Copy() => (SoundNote)MemberwiseClone();
}

/// <summary>Звук напоминания: на Key ссылаются напоминания, Label — название в настройках.</summary>
sealed class ReminderSoundDef
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    public double Seconds { get; set; } = 0.7;
    public List<SoundNote> Notes { get; set; } = new();

    public ReminderSoundDef Copy() => new() { Key = Key, Label = Label, Seconds = Seconds, Notes = Notes.Select(n => n.Copy()).ToList() };
}

/// <summary>
/// Звуки напоминаний — у каждого свой мотив, чтобы узнавать на слух, не глядя на экран. Встроенные — низкие и мягкие
/// (основные тона 90–590 Гц, без звона), стерео: ноты разнесены влево-вправо. Хранятся в reminders.json и правятся
/// в микшере; синтезируются в памяти, тихо (пик около −10 дБ при любых нотах).
/// </summary>
static class ReminderSound
{
    const int Rate = 44100;
    const double Peak = 0.32;
    public const string None = "none";

    static List<ReminderSoundDef> _sounds = Defaults();
    // собранный звук по ключу; подпись — ноты целиком: поправили в микшере — пересобрать
    static readonly Dictionary<string, (string Sig, System.Media.SoundPlayer Player)> Players = new();

    static SoundNote N(double freq, double start, double pan, double attack, double decay, double h2 = 0.1, double h3 = 0,
        double glide = 0, double glideTo = 1, double vibrato = 0, double? panTo = null) => new()
    {
        Freq = freq, Start = start, Pan = pan, Attack = attack, Decay = decay, H2 = h2, H3 = h3,
        Glide = glide, GlideTo = glideTo, Vibrato = vibrato, PanTo = panTo,
    };

    static ReminderSoundDef S(string key, string label, double seconds, params SoundNote[] notes) =>
        new() { Key = key, Label = label, Seconds = seconds, Notes = notes.ToList() };

    public static List<ReminderSoundDef> Defaults() => new()
    {
        // капли: две ноты, быстро съезжающие вниз (640 → 300 Гц), слева и справа
        S("water", "капли", 0.5,
            N(640, 0, -0.5, 0.003, 0.07, h2: 0, glide: 0.03, glideTo: 0.47),
            N(560, 0.17, 0.5, 0.003, 0.07, h2: 0, glide: 0.03, glideTo: 0.47)),
        // подъём: до-ми-соль снизу вверх, слева направо
        S("power", "подъём", 0.8,
            N(261.6, 0, -0.4, 0.012, 0.16, h2: 0.15),
            N(329.6, 0.11, 0, 0.012, 0.16, h2: 0.15),
            N(392.0, 0.22, 0.4, 0.012, 0.16, h2: 0.15)),
        // монетки: три коротких щипка, справа-слева-справа
        S("bounty", "монетки", 0.5,
            N(523.3, 0, 0.5, 0.003, 0.07, h2: 0.25),
            N(392.0, 0.08, -0.5, 0.003, 0.07, h2: 0.25),
            N(523.3, 0.16, 0.5, 0.003, 0.07, h2: 0.25)),
        // аккорд: до мажор медленно нарастает и проплывает слева направо
        S("wisdom", "аккорд", 1.2,
            N(261.6, 0, -0.8, 0.15, 0.45, h2: 0.08, panTo: 0.8),
            N(329.6, 0, -0.8, 0.15, 0.45, h2: 0.08, panTo: 0.8),
            N(392.0, 0, -0.8, 0.15, 0.45, h2: 0.08, panTo: 0.8)),
        // барабан: два низких удара с падением тона (180 → 90 Гц)
        S("siege", "барабан", 0.6,
            N(180, 0, -0.3, 0.004, 0.12, h2: 0.4, glide: 0.05, glideTo: 0.5),
            N(180, 0.22, 0.3, 0.004, 0.12, h2: 0.4, glide: 0.05, glideTo: 0.5)),
        // гонг: низкие ми и до с обертонами и лёгким дрожанием, по центру
        S("tormentor", "гонг", 1.4,
            N(164.8, 0, 0, 0.02, 0.5, h2: 0.5, h3: 0.3, vibrato: 0.006),
            N(130.8, 0.35, 0, 0.02, 0.5, h2: 0.5, h3: 0.3, vibrato: 0.006)),
        // волна: две плавные ноты вверх на большую терцию с вибрато, справа налево
        S("lotus", "волна", 0.9,
            N(329.6, 0, 0.5, 0.06, 0.25, vibrato: 0.01),
            N(415.3, 0.2, -0.5, 0.06, 0.25, vibrato: 0.01)),
        // рошан: собран владельцем в микшере — две плывущие вверх ноты с вибрато и обертонами
        S("roshan", "рошан", 0.7,
            N(409.7, 0.15, 0.3, 0.118, 0.83, h2: 0.66, h3: 0.41, glide: 0.05, glideTo: 1.1, vibrato: 0.041),
            N(310.7, 0.16, -0.5, 0.221, 0.24, h2: 0.51, h3: 0.29, glide: 0.03, glideTo: 1.45, vibrato: 0.037)),
        // мягкий: ля и ми вниз — для своих напоминаний
        S("soft", "мягкий", 0.7,
            N(440.0, 0, -0.3, 0.025, 0.12),
            N(329.6, 0.16, 0.3, 0.025, 0.12)),
    };

    /// <summary>Звуки из настроек — их играют напоминания.</summary>
    public static void Use(IEnumerable<ReminderSoundDef> sounds) => _sounds = sounds.Select(s => s.Copy()).ToList();

    /// <summary>Звук по тексту — для правил без выбранного звука.</summary>
    public static string Guess(string? text)
    {
        var t = (text ?? "").ToLowerInvariant();
        if (t.Contains("лотос")) return "lotus";
        if (t.Contains("баунти") || t.Contains("золот")) return "bounty";
        if (t.Contains("мудрост") || t.Contains("опыт")) return "wisdom";
        if (t.Contains("катапульт")) return "siege";
        if (t.Contains("торментор")) return "tormentor";
        if (t.Contains("рошан")) return "roshan";
        if (t.Contains("вод")) return "water";
        if (t.Contains("активн") || t.Contains("силы")) return "power";
        return "soft";
    }

    /// <summary>Проиграть звук по ключу, не дожидаясь конца; «без звука» — ничего, неизвестный ключ — «мягкий».</summary>
    public static void Play(string? key)
    {
        if (key == None) return;
        var def = _sounds.FirstOrDefault(s => s.Key == key) ?? _sounds.FirstOrDefault(s => s.Key == "soft") ?? Defaults().First(s => s.Key == "soft");
        Play(def);
    }

    /// <summary>Проиграть звук как есть — и ещё не сохранённый из микшера.</summary>
    public static void Play(ReminderSoundDef def)
    {
        if (def.Key == None || def.Notes.Count == 0) return;
        try
        {
            var sig = JsonSerializer.Serialize(def);
            if (!Players.TryGetValue(def.Key, out var cached) || cached.Sig != sig)
            {
                var player = new System.Media.SoundPlayer(new MemoryStream(Wav(def)));
                player.Load();
                cached = (sig, player);
                Players[def.Key] = cached;
            }
            cached.Player.Play(); // не ждёт конца звука; новый звук обрывает прежний
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            Log.Write("Звук напоминания не проигран: " + ex.Message);
        }
    }

    /// <summary>Самая высокая частота в звуке (с гармониками, скольжением и вибрато), Гц — выше ~1500 звук звенит.</summary>
    public static double Highest(ReminderSoundDef def) => def.Notes.Count == 0 ? 0 : def.Notes.Max(n =>
        Math.Max(n.Freq, n.Glide > 0 ? n.Freq * n.GlideTo : 0) * (1 + n.Vibrato) * (n.H3 > 0.01 ? 3 : n.H2 > 0.01 ? 2 : 1));

    // Ноты складываются в левый и правый канал (панорама с постоянной громкостью); в конце 50 мс — в ноль.
    static byte[] Wav(ReminderSoundDef def)
    {
        int n = Math.Max(1, (int)(Rate * def.Seconds));
        var left = new double[n];
        var right = new double[n];
        foreach (var note in def.Notes)
        {
            double phase = 0, length = Math.Max(def.Seconds - note.Start, 1e-3), attack = Math.Max(note.Attack, 1e-4), decay = Math.Max(note.Decay, 1e-3);
            for (int i = Math.Max(0, (int)(note.Start * Rate)); i < n; i++)
            {
                double tt = (double)i / Rate - note.Start;
                double f = note.Freq * (note.Glide > 0 ? note.GlideTo + (1 - note.GlideTo) * Math.Exp(-tt / note.Glide) : 1)
                           * (1 + note.Vibrato * Math.Sin(2 * Math.PI * 5.5 * tt));
                phase += 2 * Math.PI * f / Rate;
                double env = Math.Min(tt / attack, 1) * Math.Exp(-tt / decay);
                double v = env * (Math.Sin(phase) + note.H2 * Math.Sin(2 * phase) + note.H3 * Math.Sin(3 * phase));
                double pan = note.PanTo is double to ? note.Pan + (to - note.Pan) * Math.Min(tt / length, 1) : note.Pan;
                double a = (Math.Clamp(pan, -1, 1) + 1) * Math.PI / 4;
                left[i] += v * Math.Cos(a);
                right[i] += v * Math.Sin(a);
            }
        }
        double peak = 1e-9;
        for (int i = 0; i < n; i++) peak = Math.Max(peak, Math.Max(Math.Abs(left[i]), Math.Abs(right[i])));
        int fade = Math.Min(n, Rate / 20);
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

/// <summary>
/// Рошан: GSI присылает игроку событие roshan_killed (раздел events) со временем игры — состояние Рошана видят только
/// зрители. Возрождается через 8–11 мин после убийства (в Турбо 4–5:30). Напоминаем дважды: когда может возродиться
/// и когда точно жив. Часы — те же, что у напоминаний: на паузе стоят.
/// </summary>
sealed class RoshanTimer
{
    string? _match;
    readonly HashSet<int> _seen = new();   // время игры уже учтённых убийств
    int? _may, _sure;                      // по часам матча

    public (string Title, string Detail)? Update(GsiState s, int clock, string? mode)
    {
        if (s.MatchId != _match)
        {
            _match = s.MatchId;
            _seen.Clear();
            _may = _sure = null;
        }
        bool turbo = mode == "TURBO";
        foreach (var t in s.RoshanKills ?? Array.Empty<int>())
        {
            if (!_seen.Add(t)) continue;
            // момент убийства по часам: сейчас минус сколько прошло времени игры с события
            int killed = s.GameTime is int now ? clock - Math.Max(0, now - t) : clock;
            _may = killed + (turbo ? 240 : 480);
            _sure = killed + (turbo ? 330 : 660);
            Log.Write($"Рошан убит в {ReminderTime.Format(Math.Max(0, killed))}: может возродиться в {ReminderTime.Format(_may.Value)}, точно жив в {ReminderTime.Format(_sure.Value)}");
            if (clock >= _sure) _may = _sure = null; // старое убийство (программу запустили позже) — давно жив
        }
        if (_may is int may && clock >= may)
        {
            _may = null;
            return ("Рошан может возродиться", (_sure is int sure ? $"точно жив в {ReminderTime.Format(sure)} · " : "") +
                (turbo ? "в Турбо через 4–5:30 после убийства" : "через 8–11 мин после убийства"));
        }
        if (_sure is int alive && clock >= alive)
        {
            _sure = null;
            return ("Рошан жив", "окно возрождения закончилось — он точно на месте");
        }
        return null;
    }
}
