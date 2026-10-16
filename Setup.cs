using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace DotaReports;

/// <summary>Первый запуск на чужом компьютере: стартовые данные, конфиг GSI в папке Dota, токен Stratz.</summary>
static class Setup
{
    const string GsiFile = "gamestate_integration_dotareports.cfg";

    static readonly string GsiConfig = $$"""
        "DotaReports"
        {
        	"uri"        "http://127.0.0.1:{{GsiListener.Port}}/"
        	"timeout" 	"5.0"
        	"buffer"  	"0.1"
        	"throttle" 	"0.5"
        	"heartbeat" 	"30.0"
        	"data"
        	{
        		"provider"     "1"
        		"map"          "1"
        		"player"       "1"
        		"hero"         "1"
        		"abilities"    "1"
        		"items"        "1"
        		"draft"        "1"
        		"events"       "1"
        	}
        }
        """;

    /// <summary>Эталоны клеток сетки и образцы подписей ролей лежат рядом с программой (seed\) — копируем недостающие.</summary>
    public static void EnsureSeed()
    {
        var seed = Path.Combine(AppContext.BaseDirectory, "seed");
        foreach (var sub in new[] { "refs", "roles", "slots" })
        {
            var from = Path.Combine(seed, sub);
            if (!Directory.Exists(from)) continue;
            var to = Path.Combine(Paths.Root, sub);
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from))
            {
                var target = Path.Combine(to, Path.GetFileName(file));
                if (!File.Exists(target)) File.Copy(file, target);
            }
        }
    }

    /// <summary>
    /// Кладёт конфиг GSI в папку Dota. null — всё в порядке; иначе — что сказать пользователю.
    /// Dota читает конфиги GSI только при своём запуске: перезапуск нужен один раз — если она уже была открыта, когда конфиг появился.
    /// Дальше программу можно закрывать и открывать когда угодно — Dota сама продолжит слать данные.
    /// </summary>
    public static string? EnsureGsiConfig()
    {
        var file = GsiConfigPath();
        if (file == null) return "Не нашёл Dota 2 в библиотеках Steam — карточка пика не появится.";
        try
        {
            // переводы строк зависят от того, как собран exe (LF/CRLF в исходниках) — Dota всё равно, перезаписывать из-за них не надо
            if (File.Exists(file) && File.ReadAllText(file).ReplaceLineEndings() == GsiConfig.ReplaceLineEndings()) return null;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, GsiConfig);
            Log.Write("Конфиг GSI записан: " + file);
            return DotaStartedAt() is not null
                ? "Конфиг Dota записан. Dota уже открыта — перезапустите её один раз, дальше это не понадобится."
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Конфиг GSI не записан: " + ex.Message);
            return $"Не удалось записать конфиг в папку Dota ({ex.Message}). Запустите программу один раз от имени администратора.";
        }
    }

    public static string? GsiConfigPath() =>
        FindDota() is { } dota ? Path.Combine(dota, "game", "dota", "cfg", "gamestate_integration", GsiFile) : null;

    /// <summary>Когда запущена Dota (самый ранний процесс dota2); null — не запущена.</summary>
    public static DateTime? DotaStartedAt()
    {
        var procs = Process.GetProcessesByName("dota2");
        DateTime? first = null;
        foreach (var p in procs)
        {
            try
            {
                if (first == null || p.StartTime < first) first = p.StartTime;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                first ??= DateTime.Now; // процесс есть, но время старта не отдаёт
            }
            p.Dispose();
        }
        return first;
    }

    // Steam: путь из реестра, библиотеки из steamapps\libraryfolders.vdf, Dota 2 — приложение 570.
    static string? FindDota()
    {
        var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string;
        if (string.IsNullOrEmpty(steam)) return null;
        var libraries = new[] { steam.Replace('/', '\\') }.ToList();
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
            libraries.AddRange(Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\"")
                .Select(m => m.Groups[1].Value.Replace(@"\\", @"\")));
        return libraries.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(lib => Path.Combine(lib, "steamapps", "common", "dota 2 beta"))
            .FirstOrDefault(Directory.Exists);
    }

    /// <summary>Распознавание экрана пика размечено под 1920×1080.</summary>
    public static bool ScreenSupported =>
        System.Windows.Forms.Screen.PrimaryScreen is { } s && s.Bounds.Width == 1920 && s.Bounds.Height == 1080;

    /// <summary>Окно для токена Stratz: человек создаёт его сам на stratz.com/api и вставляет сюда.</summary>
    public static void AskStratzToken()
    {
        var box = new TextBox { Margin = new Thickness(0, 8, 0, 8), Height = 60, TextWrapping = TextWrapping.Wrap };
        if (File.Exists(Paths.StratzToken)) box.Text = File.ReadAllText(Paths.StratzToken).Trim();
        var open = new Button { Content = "Открыть stratz.com/api", Padding = new Thickness(8, 2, 8, 2), HorizontalAlignment = HorizontalAlignment.Left };
        open.Click += (_, _) => Process.Start(new ProcessStartInfo("https://stratz.com/api") { UseShellExecute = true });
        var ok = new Button { Content = "Сохранить", Padding = new Thickness(16, 2, 16, 2), HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true };
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "Для подсказок в пике нужен бесплатный токен Stratz. Откройте stratz.com/api, войдите через Steam, " +
                   "создайте токен и вставьте его сюда. Без токена работают только оповещения о жалобах.",
        });
        panel.Children.Add(box);
        var buttons = new DockPanel();
        DockPanel.SetDock(open, Dock.Left);
        buttons.Children.Add(open);
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);
        var win = new Window { Title = "Dota Reports — токен Stratz", Width = 460, SizeToContent = SizeToContent.Height, Content = panel, WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.NoResize };
        ok.Click += (_, _) =>
        {
            var token = box.Text.Trim();
            if (token.Length > 0)
            {
                Directory.CreateDirectory(Paths.Root);
                File.WriteAllText(Paths.StratzToken, token);
            }
            win.Close();
        };
        win.ShowDialog();
    }
}
