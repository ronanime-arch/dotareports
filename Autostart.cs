using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace DotaReports;

/// <summary>
/// Запуск вместе с Dota. Сама Dota запустить программу не может, поэтому программа стартует с Windows
/// (HKCU\…\Run с ключом --background) и до запуска Dota спит: окон нет, в Steam не ходит, в трее — «ждёт Dota».
/// Запустили Dota (процесс dota2) — просыпается и работает как обычно.
/// </summary>
static class Autostart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunName = "DotaReports";
    public const string BackgroundArg = "--background";
    /// <summary>Второй запуск программы будит ту, что ждёт Dota.</summary>
    public const string WakeEventName = @"Local\DotaReports.Wake";
    static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Прописать автозагрузку (папку с программой могли перенести — тогда обновить путь). Выключенную в Диспетчере задач
    /// Windows помнит отдельно (StartupApproved) — её не трогаем; удалённую вручную после нас (есть метка) не возвращаем.
    /// </summary>
    public static void Ensure()
    {
        if (Environment.ProcessPath is not { } exe) return;
        try
        {
            var command = $"\"{exe}\" {BackgroundArg}";
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            var current = key.GetValue(RunName) as string;
            if (current == null && File.Exists(Paths.AutostartMark)) return;
            if (current != command)
            {
                key.SetValue(RunName, command);
                Log.Write("Автозагрузка: " + command);
            }
            Directory.CreateDirectory(Paths.Root);
            File.WriteAllText(Paths.AutostartMark, command);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Write("Автозагрузка не прописана: " + ex.Message);
        }
    }

    /// <summary>
    /// Ждать запуска Dota или просьбы проснуться (второй запуск программы, «Запустить сейчас» в трее).
    /// false — из трея выбрали «Выход» или программу закрыли, пока она ждала.
    /// </summary>
    public static async Task<bool> WaitForDotaAsync(System.Windows.Application app)
    {
        if (DotaRunning()) return true;
        Log.Write("Ждём запуска Dota");
        var decided = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var wake = new EventWaitHandle(false, EventResetMode.AutoReset, WakeEventName);
        var waitHandle = ThreadPool.RegisterWaitForSingleObject(wake, (_, _) => decided.TrySetResult(true), null, Timeout.Infinite, executeOnlyOnce: true);

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Запустить сейчас", null, (_, _) => decided.TrySetResult(true));
        menu.Items.Add("Выход", null, (_, _) => decided.TrySetResult(false));
        using var tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Shield,
            Text = "Dota Reports — ждёт запуска Dota",
            ContextMenuStrip = menu,
            Visible = true,
        };
        tray.DoubleClick += (_, _) => decided.TrySetResult(true);
        app.Exit += (_, _) => decided.TrySetResult(false);

        while (!decided.Task.IsCompleted && !DotaRunning())
            await Task.WhenAny(decided.Task, Task.Delay(CheckEvery));
        waitHandle.Unregister(null);
        tray.Visible = false;
        bool start = !decided.Task.IsCompleted || decided.Task.Result;
        Log.Write(!start ? "Выход, пока ждали Dota" : DotaRunning() ? "Dota запущена — просыпаемся" : "Просыпаемся по просьбе");
        return start;
    }

    static bool DotaRunning()
    {
        var procs = Process.GetProcessesByName("dota2");
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }
}
