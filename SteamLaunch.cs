using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace DotaReports;

/// <summary>
/// Параметры запуска Dota в Steam — localconfig.vdf вашего аккаунта, ключ
/// UserLocalConfigStore → Software → Valve → Steam → apps → 570 → LaunchOptions.
/// Звания на пике в Турбо держатся на console.log (сервер матча → состав от Steam), а Dota пишет его только с -condebug.
/// Другого источника состава во время пика нет (20.10): Steam не сообщает сервер игрока (gameserversteamid пуст),
/// «Недавние игроки» Steam пишет в конце матча, VConsole в обычной Dota закрыт. Поэтому -condebug добавляет сама программа.
/// Steam держит файл в памяти и перезаписывает его — править можно только при закрытом Steam.
/// </summary>
static class SteamLaunch
{
    public const string Flag = "-condebug";
    static readonly string[] AppPath = { "UserLocalConfigStore", "Software", "Valve", "Steam", "apps", "570" };

    static string? SteamDir =>
        (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string)?.Replace('/', '\\');

    static string? ConfigPath()
    {
        if (SteamDir is not { Length: > 0 } steam || !File.Exists(Paths.Account)) return null;
        var account = File.ReadAllText(Paths.Account).Trim();
        return uint.TryParse(account, out _) ? Path.Combine(steam, "userdata", account, "config", "localconfig.vdf") : null;
    }

    /// <summary>Параметры запуска Dota сейчас; "" — не заданы, null — файла настроек Steam не нашёл.</summary>
    public static string? Options()
    {
        if (ConfigPath() is not { } path || !File.Exists(path)) return null;
        var text = File.ReadAllText(path);
        return Find(text) is { } spot ? spot.Value ?? "" : null;
    }

    public static bool HasFlag(string? options) =>
        options != null && options.Split(' ', '\t').Any(a => a.Equals(Flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Текст localconfig.vdf с -condebug в параметрах запуска Dota (остальной текст — байт в байт);
    /// null — нет блока приложения 570 или флаг уже есть.
    /// </summary>
    public static string? WithFlag(string text)
    {
        if (Find(text) is not { } spot || HasFlag(spot.Value)) return null;
        if (spot.Value != null)
        {
            var value = spot.Value.TrimEnd();
            return text[..spot.ValueStart] + (value.Length > 0 ? value + " " : "") + Flag + text[spot.ValueEnd..];
        }
        // ключа нет — новая строка перед закрывающей скобкой блока 570, с отступом как у его ключей
        int lineStart = text.LastIndexOf('\n', spot.BlockEnd - 1) + 1;
        var indent = text[lineStart..spot.BlockEnd];
        var newline = text.Contains("\r\n") ? "\r\n" : "\n";
        return text[..lineStart] + indent + "\t\"LaunchOptions\"\t\t\"" + Flag + "\"" + newline + text[lineStart..];
    }

    /// <summary>
    /// Закрыть Steam, добавить -condebug и открыть Steam снова. Возвращает, что сказать пользователю.
    /// Копия прежних настроек — localconfig.vdf.dotareports.bak рядом.
    /// </summary>
    public static async Task<string> EnableAsync()
    {
        if (Setup.DotaStartedAt() != null) return "Сначала закройте Dota: вместе со Steam закроется и она.";
        if (ConfigPath() is not { } path || !File.Exists(path)) return "Не нашёл настройки Steam вашего аккаунта (localconfig.vdf).";
        if (HasFlag(Options())) return $"{Flag} уже в параметрах запуска Dota.";
        var steamExe = Path.Combine(SteamDir!, "steam.exe");
        bool wasRunning = SteamRunning();
        if (wasRunning)
        {
            if (!File.Exists(steamExe)) return "Не нашёл steam.exe — закройте Steam сами и повторите.";
            Process.Start(new ProcessStartInfo(steamExe, "-shutdown") { UseShellExecute = false })?.Dispose();
            // Steam сохраняет настройки при выходе — ждём, пока закроется совсем
            for (int i = 0; i < 120 && SteamRunning(); i++) await Task.Delay(500);
            if (SteamRunning()) return "Steam не закрылся за минуту — закройте его сами и повторите.";
            await Task.Delay(1000);
        }
        try
        {
            var text = await File.ReadAllTextAsync(path);
            if (WithFlag(text) is not { } patched)
                return HasFlag(Options()) ? $"{Flag} уже в параметрах запуска Dota." : "В настройках Steam нет записи Dota 2 — запустите её из Steam один раз и повторите.";
            var temp = path + ".dotareports.tmp";
            await File.WriteAllTextAsync(temp, patched, new UTF8Encoding(false));
            File.Replace(temp, path, path + ".dotareports.bak");
            Log.Write($"Параметры запуска Dota: «{Find(text)?.Value}» → «{Find(patched)?.Value}» ({path})");
            return $"Готово: {Flag} в параметрах запуска Dota. Звания в Турбо появятся со следующего запуска Dota.";
        }
        finally
        {
            if (wasRunning) StartSteam(steamExe); // Steam закрывали мы — открыть снова, даже если записать не вышло
        }
    }

    static bool SteamRunning()
    {
        var procs = Process.GetProcessesByName("steam");
        foreach (var p in procs) p.Dispose();
        return procs.Length > 0;
    }

    static void StartSteam(string exe)
    {
        if (File.Exists(exe)) Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true })?.Dispose();
    }

    /// <summary>Где в тексте блок 570: значение LaunchOptions (без кавычек; null — ключа нет) и закрывающая скобка блока.</summary>
    sealed record Spot(string? Value, int ValueStart, int ValueEnd, int BlockEnd);

    // Текстовый KeyValues Valve: "ключ" "значение" или "ключ" { … }; ключи без учёта регистра, \" внутри строк.
    static Spot? Find(string text)
    {
        var path = new List<string>();
        string? key = null;
        int depthOfApp = -1;
        string? value = null;
        int valueStart = 0, valueEnd = 0;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
                continue;
            }
            if (c == '{')
            {
                path.Add(key ?? "");
                key = null;
                if (depthOfApp < 0 && path.Count == AppPath.Length && path.Zip(AppPath).All(p => p.First.Equals(p.Second, StringComparison.OrdinalIgnoreCase)))
                    depthOfApp = path.Count;
                i++;
                continue;
            }
            if (c == '}')
            {
                if (depthOfApp > 0 && path.Count == depthOfApp) return new Spot(value, valueStart, valueEnd, i);
                if (path.Count > 0) path.RemoveAt(path.Count - 1);
                key = null;
                i++;
                continue;
            }
            if (c != '"') { i++; continue; } // без кавычек в localconfig.vdf не пишут — пропускаем
            int start = i + 1, end = start;
            while (end < text.Length && text[end] != '"') end += text[end] == '\\' ? 2 : 1;
            end = Math.Min(end, text.Length);
            var token = text[start..end];
            i = end + 1;
            if (key == null)
            {
                key = token;
                continue;
            }
            // пара «ключ — значение»
            if (depthOfApp > 0 && path.Count == depthOfApp && key.Equals("LaunchOptions", StringComparison.OrdinalIgnoreCase))
                (value, valueStart, valueEnd) = (token, start, end);
            key = null;
        }
        return null;
    }
}
