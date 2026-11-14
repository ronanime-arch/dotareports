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
using System.Windows.Threading;

namespace DotaReports;

/// <summary>
/// Быстрый пик героя. Ctrl+Alt+A открывает поиск героя («снайп», «медуза», «Faceless Void»); Enter — открывается консоль Dota
/// (нужен параметр запуска -console), вводится <c>dota_select_hero …</c>, и дальше каждые 10 мс жмутся ↑ и Enter (повтор
/// последней команды). Бинды на экране пика не годятся: там активно текстовое поле и клавиша просто печатается.
/// Выключается само: герой выбран (GSI, консоль закрывается) или повторный Ctrl+Alt+A. Не в Dota — ничего не жмёт.
/// </summary>
sealed class QuickPick
{
    const ushort SC_GRAVE = 0x29, SC_ENTER = 0x1C, SC_UP = 0x48;
    const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_UNICODE = 0x4, KEYEVENTF_SCANCODE = 0x8, KEYEVENTF_KEYUP = 0x2;
    const int OpenTicks = 20; // 200 мс на открытие консоли

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort Vk, Scan; public uint Flags, Time; public UIntPtr Extra; }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct INPUT
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public KEYBDINPUT Ki;
    }

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    readonly DraftData _data;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(10) };
    QuickPickWindow? _window;
    int _dotaPid;
    IntPtr _dotaWindow;
    string _command = "";
    int _step; // 0 — открыть консоль, 1…OpenTicks — ждём, дальше — повтор

    public bool On => _timer.IsEnabled;
    /// <summary>Сообщение для значка в трее.</summary>
    public Action<string>? Notify { get; set; }

    public QuickPick(DraftData data)
    {
        _data = data;
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>Ctrl+Alt+A: идёт спам — остановить, иначе открыть выбор героя.</summary>
    public void Toggle()
    {
        if (On) { Stop(); Notify?.Invoke("Быстрый пик: выключен"); return; }
        if (_window is { IsVisible: true }) { _window.Hide(); return; }
        _dotaWindow = DotaWindowOrZero();
        _window ??= new QuickPickWindow(_data, Pick);
        _window.Open(LastHero());
    }

    void Pick(int heroId)
    {
        var internalName = _data.HeroInternalNames().GetValueOrDefault(heroId);
        if (internalName == null)
        {
            Notify?.Invoke("Быстрый пик: нет списка героев Valve — запустите помощника в пике, он его скачает");
            return;
        }
        try { File.WriteAllText(Paths.QuickPickLast, heroId.ToString()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Write("Быстрый пик: герой не запомнен: " + ex.Message); }
        _command = "dota_select_hero " + internalName;
        if (_dotaWindow != IntPtr.Zero) SetForegroundWindow(_dotaWindow);
        Start();
        Notify?.Invoke("Быстрый пик: " + _data.Heroes.GetValueOrDefault(heroId, internalName));
    }

    static int LastHero() =>
        File.Exists(Paths.QuickPickLast) && int.TryParse(File.ReadAllText(Paths.QuickPickLast).Trim(), out var id) ? id : 0;

    void Start()
    {
        if (On) return;
        _step = 0;
        _timer.Start();
        Log.Write("Быстрый пик: включён, " + _command);
    }

    public void Stop()
    {
        if (!On) return;
        _timer.Stop();
        if (_step > 0 && DotaForeground()) Tap(SC_GRAVE, false); // закрыть консоль
        Log.Write("Быстрый пик: выключен");
    }

    /// <summary>Герой выбран (GSI: HeroId > 0) — повторять команду больше не надо.</summary>
    public void OnHeroPicked(int heroId)
    {
        if (heroId > 0 && On)
        {
            Stop();
            Notify?.Invoke("Быстрый пик: герой выбран");
        }
    }

    void Tick()
    {
        if (!DotaForeground()) return; // в чужое окно не жмём, но и не выключаемся: можно вернуться в Dota
        if (_step == 0)
        {
            Tap(SC_GRAVE, false);
            _step = 1;
        }
        else if (_step < OpenTicks) _step++;
        else if (_step == OpenTicks)
        {
            Type(_command);
            Tap(SC_ENTER, false);
            _step++;
        }
        else
        {
            Tap(SC_UP, true);
            Tap(SC_ENTER, false);
        }
    }

    static void Tap(ushort scan, bool extended)
    {
        var flags = KEYEVENTF_SCANCODE | (extended ? KEYEVENTF_EXTENDEDKEY : 0);
        Send(new[] { Key(0, scan, flags), Key(0, scan, flags | KEYEVENTF_KEYUP) });
    }

    static void Type(string text)
    {
        var inputs = new List<INPUT>();
        foreach (var c in text)
        {
            inputs.Add(Key(0, c, KEYEVENTF_UNICODE));
            inputs.Add(Key(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP));
        }
        Send(inputs.ToArray());
    }

    static INPUT Key(ushort vk, ushort scan, uint flags) =>
        new() { Type = 1, Ki = new KEYBDINPUT { Vk = vk, Scan = scan, Flags = flags } };

    static void Send(INPUT[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
            Log.Write("Быстрый пик: SendInput принял не всё, ошибка " + Marshal.GetLastWin32Error());
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

    public QuickPickWindow(DraftData data, Action<int> picked)
    {
        _data = data;
        _picked = picked;
        Title = "Быстрый пик";
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
