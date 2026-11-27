using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DotaReports;

/// <summary>
/// Быстрый пик героя. F7 — включить для последнего героя (повторно — выключить), Ctrl+F7 — выбрать другого
/// («снайп», «медуза», «Faceless Void»). Выключается само: герой взят или пик кончился. Не в Dota — ничего не жмёт.
/// Режим консоли (если в Dota назначена клавиша консоли, нужен параметр запуска -console): с начала выбора героя консоль
/// открывается и в неё вводится <c>dota_select_hero …</c>; как только кончились запреты — Enter и дальше без пауз ↑ + Enter.
/// Режим мыши (клавиша консоли не назначена): клик по герою в сетке и быстрые клики по «Выбрать»; 1920×1080, «Атрибуты».
/// Режим — галочкой в меню значка. Пока кнопка выбора красная (запрет героя), ни Enter, ни кликов пика нет.
/// Автобан: F8 — включить/выключить, Ctrl+F8 — выбрать героя. Пока кнопка красная, клик по герою и по кнопке (в обоих
/// режимах: консольной команды запрета для All Pick и Турбо нет); пик ждёт, пока бан не отправлен.
/// </summary>
sealed class QuickPick
{
    const string Selection = "DOTA_GAMERULES_STATE_HERO_SELECTION";
    const uint MOUSE_LEFTDOWN = 0x2, MOUSE_LEFTUP = 0x4;
    const int RepeatHeroStep = 100;
    const uint KEY_EXTENDED = 0x1, KEY_UP = 0x2, KEY_UNICODE = 0x4, KEY_SCANCODE = 0x8;
    const ushort SC_ENTER = 0x1C, SC_ARROW_UP = 0x48;
    const int ConsoleOpenTicks = 15; // ~0,2 с на открытие консоли
    const int BanRetryStep = 50, BanTries = 2;
    static readonly TimeSpan BanWait = TimeSpan.FromSeconds(5); // нет красной кнопки с начала выбора — запретов нет
    static readonly System.Drawing.Point PickButton = new(1479, 828);
    static readonly System.Drawing.Rectangle PickButtonArea = new(1350, 800, 260, 55);

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort Vk, Scan; public uint Flags, Time; public UIntPtr Extra; }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct INPUT
    {
        [FieldOffset(0)] public uint Type; // 0 — мышь, 1 — клавиатура
        [FieldOffset(8)] public MOUSEINPUT Mi;
        [FieldOffset(8)] public KEYBDINPUT Ki;
    }

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    readonly DraftData _data;
    readonly Func<int, System.Drawing.Rectangle?> _cellOf;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(10) };
    QuickPickWindow? _window, _banWindow;
    int _banHeroId, _banStep, _banSent;
    DateTime? _selectionSince;
    bool _pickOn, _banOn;
    int _dotaPid;
    IntPtr _dotaWindow;
    int _heroId;
    int _step;
    bool _down;
    (ushort Scan, bool Extended)? _consoleKey; // null — режим мыши
    string _command = "";
    bool _consoleOpen;
    bool _entered;
    string _gameState = "";
    bool _sawSelection;

    public bool On => _pickOn;
    public bool BanOn => _banOn;

    /// <summary>Галочка «через консоль» (по умолчанию стоит).</summary>
    public bool UseConsole
    {
        get => !File.Exists(Paths.QuickPickMouse);
        set
        {
            try
            {
                if (value) File.Delete(Paths.QuickPickMouse);
                else File.WriteAllText(Paths.QuickPickMouse, "");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Write("Быстрый пик: режим не сохранён: " + ex.Message); }
            Log.Write("Быстрый пик: режим " + (value ? "консоли" : "мыши"));
            if (_pickOn) { StopPick($"Автопик {Name(_heroId)} выключен", "сменён режим — включите заново (F7)"); }
        }
    }
    /// <summary>Уведомление поверх игры: заголовок, строка, «включён» (чуть дольше).</summary>
    public Action<string, string, bool>? Notify { get; set; }

    public QuickPick(DraftData data, Func<int, System.Drawing.Rectangle?> cellOf)
    {
        _data = data;
        _cellOf = cellOf;
        _timer.Tick += (_, _) => Tick();
    }

    string Name(int heroId) => _data.Heroes.GetValueOrDefault(heroId, $"герой #{heroId}");

    /// <summary>F7: включён — выключить; иначе включить для последнего героя, а если его ещё нет — открыть выбор.</summary>
    public void Toggle()
    {
        if (On) { StopPick($"Автопик {Name(_heroId)} выключен", ""); return; }
        if (LastHero(Paths.QuickPickLast) is > 0 and var last && _data.Heroes.ContainsKey(last)) { _dotaWindow = DotaWindowOrZero(); Pick(last); }
        else ChooseHero();
    }

    /// <summary>Ctrl+F7: открыть поиск героя.</summary>
    public void ChooseHero()
    {
        if (On) StopPick($"Автопик {Name(_heroId)} выключен", "");
        if (_window is { IsVisible: true }) { _window.Hide(); return; }
        _dotaWindow = DotaWindowOrZero();
        _window ??= new QuickPickWindow(_data, Pick);
        _window.Open(LastHero(Paths.QuickPickLast));
    }

    void Pick(int heroId)
    {
        Remember(Paths.QuickPickLast, heroId);
        _consoleKey = UseConsole ? ConsoleKey() : null;
        var internalName = _data.HeroInternalNames().GetValueOrDefault(heroId);
        var problem = _consoleKey != null
            ? internalName == null ? "нет списка героев Valve" : null
            : !Setup.ScreenSupported ? "нужен экран 1920×1080" : _cellOf(heroId) == null ? "героя нет в сетке пика" : null;
        if (problem != null)
        {
            Log.Write($"Быстрый пик: не включён, герой {heroId}: {problem}");
            Notify?.Invoke($"Автопик {Name(heroId)} не включён", problem, false);
            return;
        }
        _heroId = heroId;
        _step = 0;
        _command = "dota_select_hero " + internalName;
        _consoleOpen = _entered = false;
        _sawSelection = _gameState == Selection;
        if (_dotaWindow != IntPtr.Zero) SetForegroundWindow(_dotaWindow);
        _pickOn = true;
        _timer.Start();
        var mode = _consoleKey != null ? "консоль" : "мышь";
        Log.Write($"Быстрый пик: включён ({mode}), герой {heroId}, состояние {_gameState}");
        Notify?.Invoke($"Автопик {Name(heroId)} включён", $"режим: {mode} · " + (_sawSelection ? "выбираю" : "жду выбора героя") + " · F7 — выключить", true);
    }

    static int LastHero(string path) =>
        File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var id) ? id : 0;

    void StopPick(string title, string line)
    {
        if (!_pickOn) return;
        _pickOn = false;
        Restart();
        if (_consoleOpen && DotaForeground()) Tap(_consoleKey!.Value.Scan, _consoleKey.Value.Extended); // закрыть консоль
        _consoleOpen = false;
        Finish(title, line);
    }

    void StopBan(string title, string line)
    {
        if (!_banOn) return;
        _banOn = false;
        if (_down) Mouse(MOUSE_LEFTUP);
        Finish(title, line);
    }

    void Finish(string title, string line)
    {
        if (!_pickOn && !_banOn) _timer.Stop();
        Log.Write($"Быстрый пик: {title} {line}");
        Notify?.Invoke(title, line, false);
    }

    /// <summary>F8: автобан включён — выключить; иначе включить для последнего героя бана, а если его нет — открыть выбор.</summary>
    public void ToggleBan()
    {
        if (_banOn) { StopBan($"Автобан {Name(_banHeroId)} выключен", ""); return; }
        if (LastHero(Paths.QuickBanLast) is > 0 and var last && _data.Heroes.ContainsKey(last)) { _dotaWindow = DotaWindowOrZero(); Ban(last); }
        else ChooseBanHero();
    }

    /// <summary>Ctrl+F8: выбрать героя для бана.</summary>
    public void ChooseBanHero()
    {
        if (_banOn) StopBan($"Автобан {Name(_banHeroId)} выключен", "");
        if (_banWindow is { IsVisible: true }) { _banWindow.Hide(); return; }
        _dotaWindow = DotaWindowOrZero();
        _banWindow ??= new QuickPickWindow(_data, Ban, "Автобан");
        _banWindow.Open(LastHero(Paths.QuickBanLast));
    }

    void Ban(int heroId)
    {
        Remember(Paths.QuickBanLast, heroId);
        var problem = !Setup.ScreenSupported ? "нужен экран 1920×1080" : _cellOf(heroId) == null ? "героя нет в сетке пика" : null;
        if (problem != null)
        {
            Log.Write($"Быстрый пик: автобан не включён, герой {heroId}: {problem}");
            Notify?.Invoke($"Автобан {Name(heroId)} не включён", problem, false);
            return;
        }
        _banHeroId = heroId;
        _banStep = _banSent = 0;
        _selectionSince = null;
        _banOn = true;
        if (_dotaWindow != IntPtr.Zero) SetForegroundWindow(_dotaWindow);
        _timer.Start();
        Log.Write($"Быстрый пик: автобан включён, герой {heroId}, состояние {_gameState}");
        Notify?.Invoke($"Автобан {Name(heroId)} включён", "F8 — выключить", true);
    }

    static void Remember(string path, int heroId)
    {
        try { File.WriteAllText(path, heroId.ToString()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Write("Быстрый пик: герой не запомнен: " + ex.Message); }
    }

    /// <summary>Каждое сообщение GSI: состояние игры и ваш герой (0 — ещё не взят).</summary>
    public void OnGsi(string gameState, int heroId)
    {
        _gameState = gameState;
        if (_banOn)
        {
            if (gameState == Selection) _selectionSince ??= DateTime.Now;
            else if (_selectionSince != null) StopBan($"Автобан {Name(_banHeroId)} выключен", "пик кончился");
        }
        if (!_pickOn) return;
        if (gameState == Selection) _sawSelection = true;
        if (!_sawSelection) return;
        if (heroId == _heroId) StopPick($"{Name(_heroId)} взят", "автопик выключен");
        else if (gameState != Selection)
            StopPick($"Автопик {Name(_heroId)} выключен", heroId > 0 ? $"пик кончился, взят {Name(heroId)}" : "пик кончился");
    }

    // Один клик по герою, дальше — клики по «Выбрать» так быстро, как позволяет таймер (нажатие и отпускание по тикам,
    // чтобы Dota видела каждое). Раз в ~1,5 с герой кликается снова: первый клик мог попасть до появления сетки.
    void Tick()
    {
        if (_gameState != Selection || !DotaForeground()) { Restart(); return; } // вне пика и в чужом окне не жмём
        if (_banOn) { BanTick(); return; } // пик — после бана
        if (!_pickOn) return;
        if (_consoleKey != null) ConsoleTick();
        else MouseTick();
    }

    // Пока кнопка красная: клик по герою, клик по кнопке; через ~0,5 с ещё раз, если она всё ещё красная.
    void BanTick()
    {
        if (_banStep == 0)
        {
            if (!BanButton())
            {
                if (_banSent > 0) StopBan($"{Name(_banHeroId)}: запрет отправлен", "автобан выключен");
                else if (DateTime.Now - _selectionSince > BanWait) StopBan($"Автобан {Name(_banHeroId)} выключен", "этапа запрета не видно");
                return;
            }
            if (_banSent >= BanTries) { StopBan($"{Name(_banHeroId)}: запрет отправлен", "автобан выключен"); return; }
            if (_cellOf(_banHeroId) is { } cell) SetCursorPos(cell.X + cell.Width / 2, cell.Y + cell.Height / 2);
        }
        else if (_banStep is 1 or 10) Mouse(MOUSE_LEFTDOWN);
        else if (_banStep is 2 or 11) Mouse(MOUSE_LEFTUP);
        else if (_banStep == 8)
        {
            if (!BanButton()) { _banStep = 0; return; } // запреты кончились: «Выбрать» с этим героем не жмём
            SetCursorPos(PickButton.X, PickButton.Y);
        }
        if (_banStep == 11) _banSent++;
        _banStep = _banStep >= BanRetryStep ? 0 : _banStep + 1;
    }

    // Команда набирается один раз (в запретах — заранее, без Enter), дальше только ↑ и Enter: если консоль вдруг
    // не открылась, в чат уйдёт не больше одной строки.
    void ConsoleTick()
    {
        if (!_consoleOpen)
        {
            Tap(_consoleKey!.Value.Scan, _consoleKey.Value.Extended);
            _consoleOpen = true;
            _step = 0;
        }
        else if (_step < ConsoleOpenTicks) _step++;
        else if (_step == ConsoleOpenTicks)
        {
            Type(_command);
            _step++;
        }
        else if (!_entered)
        {
            if (BanButton()) return;
            Tap(SC_ENTER, false);
            _entered = true;
        }
        else if (_step++ % 2 == 0) Tap(SC_ARROW_UP, true);
        else Tap(SC_ENTER, false);
    }

    void MouseTick()
    {
        if (_step == 0)
        {
            if (BanButton()) return; // идёт запрет героев: даже клетку не трогаем
            if (_cellOf(_heroId) is { } cell) SetCursorPos(cell.X + cell.Width / 2, cell.Y + cell.Height / 2);
        }
        else if (_step == 1) Mouse(MOUSE_LEFTDOWN);
        else if (_step == 2) Mouse(MOUSE_LEFTUP);
        else if (_step == 5) SetCursorPos(PickButton.X, PickButton.Y); // дали Dota отметить героя
        else if (_step > 5)
        {
            if (!_down && _step % 8 == 0 && BanButton()) { _step = 0; return; }
            Mouse(_down ? MOUSE_LEFTUP : MOUSE_LEFTDOWN);
        }
        _step++;
        if (_step >= RepeatHeroStep && !_down) _step = 0;
    }

    /// <summary>Начать круг мыши заново; кнопку не оставляем зажатой. Открытая консоль остаётся открытой.</summary>
    void Restart()
    {
        if (_down) Mouse(MOUSE_LEFTUP);
        _banStep = 0;
        if (_consoleKey == null) _step = 0;
    }

    /// <summary>Кнопка красная — это запрет героя, а не выбор: по ней кликать нельзя.</summary>
    static bool BanButton()
    {
        using var bmp = new System.Drawing.Bitmap(PickButtonArea.Width, PickButtonArea.Height);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
            g.CopyFromScreen(PickButtonArea.Location, System.Drawing.Point.Empty, PickButtonArea.Size);
        long r = 0, gr = 0, n = 0;
        for (int y = 0; y < bmp.Height; y += 4)
            for (int x = 0; x < bmp.Width; x += 4, n++)
            {
                var c = bmp.GetPixel(x, y);
                r += c.R;
                gr += c.G;
            }
        return r / n > 60 && r > gr * 3 / 2;
    }

    void Mouse(uint flags)
    {
        _down = flags == MOUSE_LEFTDOWN;
        var input = new[] { new INPUT { Type = 0, Mi = new MOUSEINPUT { Flags = flags } } };
        if (SendInput(1, input, Marshal.SizeOf<INPUT>()) != 1)
            Log.Write("Быстрый пик: SendInput не принят, ошибка " + Marshal.GetLastWin32Error());
    }

    void Tap(ushort scan, bool extended)
    {
        var flags = KEY_SCANCODE | (extended ? KEY_EXTENDED : 0);
        Send(Key(scan, flags), Key(scan, flags | KEY_UP));
    }

    void Type(string text) =>
        Send(text.SelectMany(c => new[] { Key(c, KEY_UNICODE), Key(c, KEY_UNICODE | KEY_UP) }).ToArray());

    static INPUT Key(ushort scan, uint flags) => new() { Type = 1, Ki = new KEYBDINPUT { Scan = scan, Flags = flags } };

    static void Send(params INPUT[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
            Log.Write("Быстрый пик: SendInput принял не всё, ошибка " + Marshal.GetLastWin32Error());
    }

    // клавиши, которые не печатают символов, и «~»: имя в dotakeys_personal.lst → скан-код
    static readonly Dictionary<string, (ushort, bool)> ConsoleScans = new(StringComparer.OrdinalIgnoreCase)
    {
        ["INS"] = (0x52, true), ["DEL"] = (0x53, true), ["HOME"] = (0x47, true), ["END"] = (0x4F, true),
        ["PGUP"] = (0x49, true), ["PGDN"] = (0x51, true), ["`"] = (0x29, false), ["BACKQUOTE"] = (0x29, false),
        ["F1"] = (0x3B, false), ["F2"] = (0x3C, false), ["F3"] = (0x3D, false), ["F4"] = (0x3E, false), ["F5"] = (0x3F, false),
        ["F6"] = (0x40, false), ["F9"] = (0x43, false), ["F10"] = (0x44, false),
        ["F11"] = (0x57, false), ["F12"] = (0x58, false),
    };

    /// <summary>Клавиша консоли из настроек Dota (самый свежий dotakeys_personal.lst); null — не назначена или незнакомая.</summary>
    static (ushort, bool)? ConsoleKey()
    {
        try
        {
            if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is not string steam) return null;
            var file = new DirectoryInfo(Path.Combine(steam.Replace('/', '\\'), "userdata")).GetDirectories()
                .Select(d => new FileInfo(Path.Combine(d.FullName, "570", "remote", "cfg", "dotakeys_personal.lst")))
                .Where(f => f.Exists).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (file == null) return null;
            var name = Regex.Match(File.ReadAllText(file.FullName),
                "\"Console\"\\s*\\{[^{}]*?\"Key\"\\s+\"([^\"]+)\"").Groups[1].Value;
            if (ConsoleScans.TryGetValue(name, out var key)) return key;
            Log.Write($"Быстрый пик: клавиша консоли «{name}» не назначена или не поддерживается — режим мыши");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Быстрый пик: настройки клавиш не прочитаны: " + ex.Message);
        }
        return null;
    }

    bool DotaForeground()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        if (pid == 0) return false;
        if (pid == _dotaPid) return true;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            if (!p.ProcessName.Equals("dota2", StringComparison.OrdinalIgnoreCase)) return false;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
        _dotaPid = (int)pid;
        return true;
    }

    /// <summary>Окно Dota, чтобы вернуть ему фокус после выбора героя в нашем окне.</summary>
    static IntPtr DotaWindowOrZero()
    {
        var fg = GetForegroundWindow();
        GetWindowThreadProcessId(fg, out var pid);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            if (p.ProcessName.Equals("dota2", StringComparison.OrdinalIgnoreCase)) return fg;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { }
        return Process.GetProcessesByName("dota2").Select(p => p.MainWindowHandle).FirstOrDefault(h => h != IntPtr.Zero);
    }
}

/// <summary>Маленькое окно: строка поиска и до шести подходящих героев. Enter — выбрать, ↑↓ — перейти, Esc — закрыть.</summary>
sealed class QuickPickWindow : Window
{
    static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x14, 0x18, 0x20));
    static readonly Brush PanelBg = new SolidColorBrush(Color.FromRgb(0x1E, 0x24, 0x30));
    static readonly Brush Fg = new SolidColorBrush(Color.FromRgb(0xF2, 0xF2, 0xF2));
    static readonly Brush Dim = new SolidColorBrush(Color.FromRgb(0x9A, 0xA4, 0xB0));

    readonly DraftData _data;
    readonly Action<int> _picked;
    readonly TextBox _input = new();
    readonly ListBox _list = new();
    readonly TextBlock _hint = new() { FontSize = 11, Foreground = Dim, Margin = new Thickness(0, 6, 0, 0) };
    int _last;
    List<int> _found = new();

    public QuickPickWindow(DraftData data, Action<int> picked, string title = "Быстрый пик")
    {
        _data = data;
        _picked = picked;
        Title = title;
        Width = 320;
        SizeToContent = SizeToContent.Height;
        Topmost = true;
        ShowInTaskbar = false;
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.NoResize;
        Background = Bg;
        FontFamily = new FontFamily("Segoe UI");

        _input.FontSize = 16;
        _input.Padding = new Thickness(6, 4, 6, 4);
        _input.Background = PanelBg;
        _input.Foreground = Fg;
        _input.CaretBrush = Fg;
        _input.BorderBrush = Dim;
        _input.TextChanged += (_, _) => Search();
        _list.Background = PanelBg;
        _list.Foreground = Fg;
        _list.BorderThickness = new Thickness(0);
        _list.FontSize = 14;
        _list.Margin = new Thickness(0, 4, 0, 0);
        _list.MouseUp += (_, _) => Choose();

        var root = new StackPanel { Margin = new Thickness(10) };
        root.Children.Add(_input);
        root.Children.Add(_list);
        root.Children.Add(_hint);
        Content = root;

        PreviewKeyDown += OnKey;
        Closing += (_, e) => { e.Cancel = true; Hide(); };
    }

    public void Open(int lastHero)
    {
        _last = lastHero;
        _input.Clear();
        Search();
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - Width) / 2;
        Top = wa.Top + 120;
        Show();
        Activate();
        _input.Focus();
    }

    void Search()
    {
        _found = HeroSearch.Find(_input.Text, _data.Heroes, 6);
        // пустое поле: Enter берёт прошлого героя
        if (_found.Count == 0 && _input.Text.Trim().Length == 0 && _last > 0 && _data.Heroes.ContainsKey(_last)) _found = new() { _last };
        _list.ItemsSource = _found.Select(id => _data.Heroes.GetValueOrDefault(id, $"#{id}")).ToList();
        if (_found.Count > 0) _list.SelectedIndex = 0;
        _hint.Text = _data.Heroes.Count == 0 ? "Список героев ещё не загружен."
            : _input.Text.Trim().Length == 0 && _last > 0 ? "Enter — прошлый герой, или начните вводить название."
            : _found.Count == 0 ? "Не нашёл такого героя." : "Enter — выбрать, ↑↓ — перейти, Esc — закрыть.";
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Hide(); e.Handled = true; break;
            case Key.Enter: Choose(); e.Handled = true; break;
            case Key.Down when _list.Items.Count > 0: _list.SelectedIndex = Math.Min(_list.SelectedIndex + 1, _list.Items.Count - 1); e.Handled = true; break;
            case Key.Up when _list.Items.Count > 0: _list.SelectedIndex = Math.Max(_list.SelectedIndex - 1, 0); e.Handled = true; break;
        }
    }

    void Choose()
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _found.Count) return;
        var id = _found[_list.SelectedIndex];
        Hide();
        _picked(id);
    }
}
