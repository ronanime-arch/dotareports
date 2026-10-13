using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DotaReports;

static class Paths
{
    public static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DotaReports");
    public static string WebView => Path.Combine(Root, "WebView2");
    public static string Dump => Path.Combine(Root, "last_table.json");
    public static string Seen => Path.Combine(Root, "seen_summaries.json");
    public static string Log => Path.Combine(Root, "log.txt");
    public static string Account => Path.Combine(Root, "account.txt");
    public static string StratzToken => Path.Combine(Root, "stratz_token.txt");
    public static string Heroes => Path.Combine(Root, "heroes.json");
    public static string Matchups => Path.Combine(Root, "matchups.json");
    public static string MatchupsBrackets => Path.Combine(Root, "matchups_brackets.txt");
    public static string MatchupsTurbo => Path.Combine(Root, "matchups_turbo.json");
    public static string Pool => Path.Combine(Root, "pool.json");
    public static string Recent => Path.Combine(Root, "recent.json");
    public static string ModeStats => Path.Combine(Root, "mode_stats.json");
    public static string TurboBrackets => Path.Combine(Root, "turbo_brackets.txt");
    public static string Frames => Path.Combine(Root, "frames");
    public static string HeroList => Path.Combine(Root, "herolist_ru.json");
    public static string Portraits => Path.Combine(Root, "portraits");
    public static string Icons => Path.Combine(Root, "icons");
    public static string ProfileTables => Path.Combine(Root, "profile_tables.json");
    public static string RankConfidence => Path.Combine(Root, "rank_confidence.txt");
    public static string SeenOutgoing => Path.Combine(Root, "seen_outgoing.json");
    public static string Reminders => Path.Combine(Root, "reminders.json");
    public static string ConductHistory => Path.Combine(Root, "conduct_history.json");
    public static string MatchMode => Path.Combine(Root, "match_mode.json");
    public static string Predictions => Path.Combine(Root, "predictions.json");
}

static class Words
{
    /// <summary>Склонение по числу: 1 игра, 2 игры, 5 игр.</summary>
    public static string Plural(long n, string one, string few, string many)
    {
        long m10 = n % 10, m100 = n % 100;
        if (m10 == 1 && m100 != 11) return one;
        if (m10 is >= 2 and <= 4 && m100 is < 12 or > 14) return few;
        return many;
    }
}

static class Log
{
    public static void Write(string message)
    {
        try
        {
            Directory.CreateDirectory(Paths.Root);
            File.AppendAllText(Paths.Log, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (IOException) { } // писать ошибку записи лога некуда
    }
}

/// <summary>Какие строки таблицы уже показывались, чтобы выделять только новые.</summary>
sealed class SeenStore
{
    const int MaxKeys = 3000;

    public bool Initialized { get; set; }
    public List<string> Keys { get; set; } = new();

    HashSet<string>? _set;
    HashSet<string> Set => _set ??= new HashSet<string>(Keys);

    public bool Contains(string key) => Set.Contains(key);

    public void Add(IEnumerable<string> keys)
    {
        foreach (var k in keys)
            if (Set.Add(k)) Keys.Add(k);
        if (Keys.Count > MaxKeys)
        {
            Keys.RemoveRange(0, Keys.Count - MaxKeys);
            _set = null;
        }
    }

    public static SeenStore Load()
    {
        try
        {
            if (File.Exists(Paths.Seen))
                return JsonSerializer.Deserialize<SeenStore>(File.ReadAllText(Paths.Seen)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Write("seen.json не прочитан, начинаю заново: " + ex.Message);
        }
        return new();
    }

    public void Save() => File.WriteAllText(Paths.Seen, JsonSerializer.Serialize(this));
}

static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x20;
    public const int WS_EX_TOOLWINDOW = 0x80;
    public const int WS_EX_LAYERED = 0x80000;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public static readonly IntPtr HWND_TOPMOST = new(-1);
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;

    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    public const int WM_HOTKEY = 0x312;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hWnd, int index, int value);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
