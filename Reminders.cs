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
                // правила из файла до появления цветов — цвет по тексту
                foreach (var r in s.Profiles.SelectMany(p => p.Rules).Where(r => string.IsNullOrEmpty(r.Color)))
                    r.Color = ReminderColors.Guess(r.Text);
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
                    new() { Text = "Руна воды — река", Color = "green", At = "2:00", Every = "2:00", Until = "4:00", Lead = 20 },
                    new() { Text = "Баунти-руны", Color = "yellow", At = "4:00", Every = "4:00", Lead = 20 },
                    new() { Text = "Активная руна — река у мида", Color = "green", At = "6:00", Every = "2:00", Lead = 20 },
                    new() { Text = "Святилище мудрости — драться за опыт", Color = "purple", At = "7:00", Every = "7:00", Lead = 30 },
                    new() { Text = "Катапульта выходит на линию", Color = "red", At = "5:00", Every = "5:00", Lead = 10 },
                    new() { Text = "Торментор — собирайтесь", Color = "green", At = "15:00", Lead = 60 },
                    new() { Enabled = false, Text = "Лотосы в пруду", Color = "blue", At = "3:00", Every = "3:00", Lead = 15 },
                },
            },
            new ReminderProfile
            {
                Name = "Турбо", Mode = "TURBO",
                Rules = new()
                {
                    new() { Text = "Руна воды — река", Color = "green", At = "2:00", Every = "2:00", Until = "4:00", Lead = 20 },
                    new() { Text = "Баунти-руны", Color = "yellow", At = "4:00", Every = "4:00", Lead = 20 },
                    new() { Text = "Активная руна — река у мида", Color = "green", At = "6:00", Every = "2:00", Lead = 20 },
                    new() { Text = "Святилище мудрости — драться за опыт", Color = "purple", At = "7:00", Every = "7:00", Lead = 30 },
                    new() { Text = "Катапульта выходит на линию", Color = "red", At = "5:00", Every = "5:00", Lead = 10 },
                    new() { Text = "Торментор — собирайтесь", Color = "green", At = "7:30", Lead = 45 },
                    new() { Enabled = false, Text = "Лотосы в пруду", Color = "blue", At = "1:30", Every = "1:30", Lead = 15 },
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
        Width = 860;
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
                   "«За сколько» — во сколько секунд до события показать напоминание. " +
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
        var rule = new ReminderRule { Text = "Новое напоминание", At = "10:00", Color = "green" };
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

/// <summary>
/// Звук напоминания: две короткие низкие ноты (A4 и E4, через 0,16 с), почти чистый синус с плавной атакой —
/// без звона и высоких частот, тихо (пик около −10 дБ), 0,7 с. Синтезируется в памяти; системный звук Windows посреди игры резкий.
/// </summary>
static class ReminderSound
{
    const int Rate = 44100;
    const double Seconds = 0.7, Peak = 0.32, Attack = 0.025, Decay = 0.12;
    static readonly (double Freq, double Start)[] Notes = { (440.0, 0), (329.6, 0.16) };
    static readonly Lazy<System.Media.SoundPlayer> Player = new(() =>
    {
        var p = new System.Media.SoundPlayer(new MemoryStream(Wav()));
        p.Load();
        return p;
    });

    public static void Play()
    {
        try
        {
            Player.Value.Play(); // не ждёт конца звука
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException)
        {
            Log.Write("Звук напоминания не проигран: " + ex.Message);
        }
    }

    // Нота — основной тон и 10% второй гармоники (для теплоты), атака 25 мс, затухание 0,12 с; в конце 50 мс — в ноль.
    static byte[] Wav()
    {
        int n = (int)(Rate * Seconds);
        var s = new double[n];
        double peak = 0;
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Rate;
            foreach (var (f, start) in Notes)
            {
                double tt = t - start;
                if (tt < 0) continue;
                double env = Math.Min(tt / Attack, 1) * Math.Exp(-tt / Decay);
                s[i] += env * (Math.Sin(2 * Math.PI * f * tt) + 0.1 * Math.Sin(4 * Math.PI * f * tt));
            }
            peak = Math.Max(peak, Math.Abs(s[i]));
        }
        int fade = Rate / 20;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        // WAV: PCM, 16 бит, моно
        w.Write("RIFF"u8); w.Write(36 + n * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(n * 2);
        for (int i = 0; i < n; i++)
        {
            double v = s[i] * Peak / peak * (i >= n - fade ? (n - i) / (double)fade : 1);
            w.Write((short)Math.Round(v * 32767));
        }
        w.Flush();
        return ms.ToArray();
    }
}
