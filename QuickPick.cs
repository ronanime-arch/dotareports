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
/// Быстрый пик героя. Ctrl+Alt+A открывает поиск героя («снайп», «медуза», «Faceless Void»); Enter — герой записан
/// в cfg\quickpick.cfg, и каждые 10 мс в Dota жмётся Numpad *, на который в autoexec.cfg висит <c>exec quickpick</c>
/// (dota_select_hero). Выключается само: герой выбран (GSI) или повторный Ctrl+Alt+A. Не в Dota — не жмёт.
/// </summary>
sealed class QuickPick
{
    const byte VK_MULTIPLY = 0x6A; // Numpad *: в Dota по умолчанию ничего не делает
    const string KeyName = "KP_MULTIPLY";
    const uint KEYUP = 0x2;
    const string BindLine = "bind \"" + KeyName + "\" \"exec quickpick\"";

    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    readonly DraftData _data;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(10) };
    readonly byte _scan = (byte)MapVirtualKey(VK_MULTIPLY, 0);
    QuickPickWindow? _window;
    int _dotaPid;
    IntPtr _dotaWindow;

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

    public void Start()
    {
        if (On) return;
        _timer.Start();
        Log.Write("Быстрый пик: включён");
    }

    public void Stop()
    {
        if (!On) return;
        _timer.Stop();
        Log.Write("Быстрый пик: выключен");
    }

    /// <summary>Герой выбран (GSI: HeroId > 0) — спамить больше не надо.</summary>
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
        keybd_event(VK_MULTIPLY, _scan, 0, UIntPtr.Zero);
        keybd_event(VK_MULTIPLY, _scan, KEYUP, UIntPtr.Zero);
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
