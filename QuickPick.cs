using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace DotaReports;

/// <summary>
/// Быстрый пик: пока включён (Ctrl+Alt+P), каждые 10 мс жмёт F6 в окне Dota. На F6 в autoexec.cfg висит
/// <c>bind F6 "dota_select_hero npc_dota_hero_sniper"</c>. Выключается сам: герой выбран (GSI) или Dota не на переднем плане.
/// </summary>
sealed class QuickPick
{
    const byte VK_F6 = 0x75;
    const uint KEYUP = 0x2;

    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(10) };
    int _dotaPid;

    public bool On => _timer.IsEnabled;
    public event Action<bool>? Changed;

    public QuickPick() => _timer.Tick += (_, _) => Tick();

    public void Toggle()
    {
        if (On) Stop(); else Start();
    }

    public void Start()
    {
        if (On) return;
        _timer.Start();
        Log.Write("Быстрый пик: включён");
        Changed?.Invoke(true);
    }

    public void Stop()
    {
        if (!On) return;
        _timer.Stop();
        Log.Write("Быстрый пик: выключен");
        Changed?.Invoke(false);
    }

    /// <summary>Герой выбран (GSI: HeroId > 0) — спамить больше не надо.</summary>
    public void OnHeroPicked(int heroId)
    {
        if (heroId > 0) Stop();
    }

    void Tick()
    {
        // не в Dota — не жмём (иначе F6 уйдёт в чужое окно); сам не выключаемся: можно переключиться обратно
        if (!DotaForeground()) return;
        keybd_event(VK_F6, 0, 0, UIntPtr.Zero);
        keybd_event(VK_F6, 0, KEYUP, UIntPtr.Zero);
    }

    bool DotaForeground()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        if (pid == 0) return false;
        if (pid == _dotaPid) return true;
        try
        {
            if (Process.GetProcessById((int)pid).ProcessName.Equals("dota2", StringComparison.OrdinalIgnoreCase))
            {
                _dotaPid = (int)pid;
                return true;
            }
        }
        catch { }
        return false;
    }
}
