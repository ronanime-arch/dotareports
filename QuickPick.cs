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
/// Быстрый пик героя. F7 — сразу автопик последнего выбранного героя (повторно — стоп); Ctrl+F7 — поиск другого героя
/// («снайп», «медуза», «Faceless Void»), после Enter автопик идёт уже с ним. Герой пишется в cfg\quickpick.cfg, на клавишу
/// Insert в autoexec.cfg висит <c>exec quickpick</c> (dota_select_hero), и в Dota каждые 10 мс жмётся Insert — это не печатный
/// символ, поэтому срабатывает и на экране пика с активным текстовым полем. Консоль не нужна.
/// Выключается само, когда GSI показал выбранного героя. Не в Dota — ничего не жмёт.
/// </summary>
sealed class QuickPick
{
    const ushort SC_INSERT = 0x52; // расширенная клавиша; в Dota по умолчанию не занята
    const string BindLine = "bind \"INS\" \"exec quickpick\"";
    const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_UNICODE = 0x4, KEYEVENTF_SCANCODE = 0x8, KEYEVENTF_KEYUP = 0x2;

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
    int _heroId;

    public bool On => _timer.IsEnabled;
    /// <summary>Сообщение для значка в трее.</summary>
    public Action<string>? Notify { get; set; }

    public QuickPick(DraftData data)
    {
        _data = data;
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>F7: идёт автопик — остановить; иначе пикать последнего героя, а если его ещё нет — открыть выбор.</summary>
    public void Toggle()
    {
        if (On) { Stop(); Notify?.Invoke("Быстрый пик: выключен"); return; }
        if (LastHero() is > 0 and var last && _data.Heroes.ContainsKey(last)) { _dotaWindow = DotaWindowOrZero(); Pick(last); }
        else ChooseHero();
    }

    /// <summary>Ctrl+F7: открыть поиск героя.</summary>
    public void ChooseHero()
    {
        if (On) Stop();
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
        try
        {
            WriteCfg(internalName);
            File.WriteAllText(Paths.QuickPickLast, heroId.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Write("Быстрый пик: cfg не записан: " + ex.Message);
            Notify?.Invoke("Быстрый пик: не записан cfg — " + ex.Message);
            return;
        }
        _heroId = heroId;
        if (_dotaWindow != IntPtr.Zero) SetForegroundWindow(_dotaWindow);
        Start();
        Notify?.Invoke("Быстрый пик: " + _data.Heroes.GetValueOrDefault(heroId, internalName));
    }

    /// <summary>cfg\quickpick.cfg с командой выбора и строка bind в autoexec.cfg (один раз; нужен перезапуск Dota).</summary>
    void WriteCfg(string internalName)
    {
        var dota = Setup.FindDota() ?? throw new InvalidOperationException("папка Dota 2 не найдена");
        var cfg = Path.Combine(dota, "game", "dota", "cfg");
        File.WriteAllText(Path.Combine(cfg, "quickpick.cfg"), $"dota_select_hero {internalName}\n");
        var autoexec = Path.Combine(cfg, "autoexec.cfg");
        var text = File.Exists(autoexec) ? File.ReadAllText(autoexec) : "";
        if (!text.Contains("exec quickpick", StringComparison.OrdinalIgnoreCase))
        {
            File.AppendAllText(autoexec, (text.Length == 0 || text.EndsWith('\n') ? "" : "\n") + "// Dota Reports: быстрый пик\n" + BindLine + "\n");
            Notify?.Invoke("Быстрый пик: в autoexec.cfg добавлен бинд — перезапустите Dota один раз");
        }
    }

    static int LastHero() =>
        File.Exists(Paths.QuickPickLast) && int.TryParse(File.ReadAllText(Paths.QuickPickLast).Trim(), out var id) ? id : 0;

    void Start()
    {
        if (On) return;
        _timer.Start();
        Log.Write("Быстрый пик: включён, hero " + _heroId);
    }

    public void Stop()
    {
        if (!On) return;
        _timer.Stop();
        Log.Write("Быстрый пик: выключен");
    }

    /// <summary>Выбран именно наш герой (GSI) — повторять команду больше не надо.</summary>
    public void OnHeroPicked(int heroId)
    {
        if (heroId > 0 && On) Log.Write($"Быстрый пик: GSI показал героя {heroId}, ждём {_heroId}");
        if (heroId > 0 && heroId == _heroId && On) // чужой или предварительный номер героя не повод остановиться
        {
            Stop();
            Notify?.Invoke("Быстрый пик: герой выбран");
        }
    }

    void Tick()
    {
        if (!DotaForeground()) return; // в чужое окно не жмём, но и не выключаемся: можно вернуться в Dota
        Tap(SC_INSERT, true);
    }

    static void Tap(ushort scan, bool extended)
    {
        var flags = KEYEVENTF_SCANCODE | (extended ? KEYEVENTF_EXTENDEDKEY : 0);
        Send(new[] { Key(0, scan, flags), Key(0, scan, flags | KEYEVENTF_KEYUP) });
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
