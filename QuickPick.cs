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
/// Быстрый пик героя мышью. F7 — включить для последнего героя (повторно — выключить), Ctrl+F7 — выбрать другого
/// («снайп», «медуза», «Faceless Void»). Пока идёт выбор героя (GSI) и Dota на переднем плане, кликает клетку героя
/// в сетке, а затем быстро кнопку «Выбрать». Через консоль и бинд выбрать героя не получилось.
/// Включить можно заранее: до начала пика мышь не трогается. Выключается само: герой взят или пик кончился.
/// Разметка 1920×1080, сетка героев «Атрибуты». По красной кнопке (запрет героя) не кликает.
/// </summary>
sealed class QuickPick
{
    const string Selection = "DOTA_GAMERULES_STATE_HERO_SELECTION";
    const uint MOUSE_LEFTDOWN = 0x2, MOUSE_LEFTUP = 0x4;
    const int RepeatHeroStep = 100;
    static readonly System.Drawing.Point PickButton = new(1479, 828);
    static readonly System.Drawing.Rectangle PickButtonArea = new(1350, 800, 260, 55);

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int X, Y; public uint Data, Flags, Time; public UIntPtr Extra; }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct INPUT
    {
        [FieldOffset(0)] public uint Type; // 0 — мышь
        [FieldOffset(8)] public MOUSEINPUT Mi;
    }

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    readonly DraftData _data;
    readonly Func<int, System.Drawing.Rectangle?> _cellOf;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(10) };
    QuickPickWindow? _window;
    int _dotaPid;
    IntPtr _dotaWindow;
    int _heroId;
    int _step;
    bool _down;
    string _gameState = "";
    bool _sawSelection;

    public bool On => _timer.IsEnabled;
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
        if (On) { Stop($"Автопик {Name(_heroId)} выключен", ""); return; }
        if (LastHero() is > 0 and var last && _data.Heroes.ContainsKey(last)) { _dotaWindow = DotaWindowOrZero(); Pick(last); }
        else ChooseHero();
    }

    /// <summary>Ctrl+F7: открыть поиск героя.</summary>
    public void ChooseHero()
    {
        if (On) Stop($"Автопик {Name(_heroId)} выключен", "");
        if (_window is { IsVisible: true }) { _window.Hide(); return; }
        _dotaWindow = DotaWindowOrZero();
        _window ??= new QuickPickWindow(_data, Pick);
        _window.Open(LastHero());
    }

    void Pick(int heroId)
    {
        try { File.WriteAllText(Paths.QuickPickLast, heroId.ToString()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Write("Быстрый пик: герой не запомнен: " + ex.Message); }
        var problem = !Setup.ScreenSupported ? "нужен экран 1920×1080" : _cellOf(heroId) == null ? "героя нет в сетке пика" : null;
        if (problem != null)
        {
            Log.Write($"Быстрый пик: не включён, герой {heroId}: {problem}");
            Notify?.Invoke($"Автопик {Name(heroId)} не включён", problem, false);
            return;
        }
        _heroId = heroId;
        _step = 0;
        _sawSelection = _gameState == Selection;
        if (_dotaWindow != IntPtr.Zero) SetForegroundWindow(_dotaWindow);
        _timer.Start();
        Log.Write($"Быстрый пик: включён, герой {heroId}, состояние {_gameState}");
        Notify?.Invoke($"Автопик {Name(heroId)} включён", (_sawSelection ? "кликаю по герою" : "жду выбора героя") + " · F7 — выключить", true);
    }

    static int LastHero() =>
        File.Exists(Paths.QuickPickLast) && int.TryParse(File.ReadAllText(Paths.QuickPickLast).Trim(), out var id) ? id : 0;

    void Stop(string title, string line)
    {
        if (!On) return;
        _timer.Stop();
        Restart();
        Log.Write($"Быстрый пик: {title} {line}");
        Notify?.Invoke(title, line, false);
    }

    /// <summary>Каждое сообщение GSI: состояние игры и ваш герой (0 — ещё не взят).</summary>
    public void OnGsi(string gameState, int heroId)
    {
        _gameState = gameState;
        if (!On) return;
        if (gameState == Selection) _sawSelection = true;
        if (!_sawSelection) return;
        if (heroId == _heroId) Stop($"{Name(_heroId)} взят", "автопик выключен");
        else if (gameState != Selection)
            Stop($"Автопик {Name(_heroId)} выключен", heroId > 0 ? $"пик кончился, взят {Name(heroId)}" : "пик кончился");
    }

    // Один клик по герою, дальше — клики по «Выбрать» так быстро, как позволяет таймер (нажатие и отпускание по тикам,
    // чтобы Dota видела каждое). Раз в ~1,5 с герой кликается снова: первый клик мог попасть до появления сетки.
    void Tick()
    {
        if (_gameState != Selection || !DotaForeground()) { Restart(); return; } // вне пика и в чужом окне не кликаем
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

    /// <summary>Начать круг заново; кнопку мыши не оставляем зажатой.</summary>
    void Restart()
    {
        if (_down) Mouse(MOUSE_LEFTUP);
        _step = 0;
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
