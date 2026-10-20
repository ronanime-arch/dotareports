using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DotaReports;

/// <summary>Игрок из комнаты NEC и то, что NEC сам подгрузил о нём с OpenDota; Avatar — его аватарка Steam из кэша NEC (файл).</summary>
sealed record NecPlayer(string SteamId, string Name, DateTime SeenAt, bool HasProfile, int RankTier, bool HistoryClosed,
    int? Wins, int? Losses, string? Country, NecMatch? LastMatch, string? Avatar = null);

sealed record NecMatch(DateTime Start, int HeroId, int Kills, int Deaths, int Assists, int GameMode);

/// <summary>
/// Состав комнаты NEC (платформа подбора для кастомок, %APPDATA%\nec-platform) — из его кэша на диске, без своих запросов.
/// Пока комната набирается, NEC пишет ники игроков (Cache\steam-nicknames.json, source=platform) и их профили OpenDota
/// (Cache\dota-records.json; ключ записи — sha256 полного адреса запроса). Деления на команды там нет, ушедшие из комнаты
/// остаются, а тех, чей ник NEC уже знал (друзья, недавние соседи), может не оказаться.
/// </summary>
static class NecLobby
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    const ulong SteamBase = 76561197960265728;
    const string OpenDota = "https://api.opendota.com/api/players/";
    const string DefaultAvatar = "fef49e7fa7e1997310d705b2a6158ff8dc1cdfeb"; // стандартная аватарка Steam «?»
    // NEC перечисляет поля через project= (remote-platform, matchFields): для ключа кэша адрес нужен буква в букву
    static readonly string LastMatchQuery = "/matches?significant=0&limit=1&offset=0&" + string.Join("&", new[]
    {
        "hero_id", "start_time", "duration", "kills", "deaths", "assists", "game_mode", "lobby_type", "lane_role", "gold_per_min",
        "xp_per_min", "hero_damage", "tower_damage", "hero_healing", "last_hits", "average_rank", "party_size", "version",
    }.Select(f => "project=" + f));

    static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "nec-platform");
    public static bool Installed => Directory.Exists(Path.Combine(Root, "Cache"));

    /// <summary>Когда NEC последний раз переписал кэш ников или профилей — чтобы не перечитывать без нужды.</summary>
    public static DateTime Stamp()
    {
        var cache = Path.Combine(Root, "Cache");
        var a = File.GetLastWriteTimeUtc(Path.Combine(cache, "steam-nicknames.json"));
        var b = File.GetLastWriteTimeUtc(Path.Combine(cache, "dota-records.json"));
        return a > b ? a : b;
    }

    /// <summary>Игроки, которых NEC показывал за <paramref name="window"/> (по умолчанию <see cref="Window"/>) до <paramref name="until"/>,
    /// по порядку появления; свои аккаунты не входят.</summary>
    /// <param name="everyone">
    /// Все, кого NEC держит в кэше, без окна по времени, и ещё профили Steam (там и ваши друзья). Знакомых игроков NEC
    /// заново не записывает — по времени из 10 в комнате ловятся 5–6; остальных узнаём по аватарке на экране среди всех.
    /// </param>
    /// <param name="sinceMatch">
    /// Только записанных после того, как NEC нашёл последнюю игру: игроки прошлой (кто-то не принял, вышли до начала) записаны
    /// раньше. Для узнавания по аватарке — false: подбор сводит одних и тех же, а знакомых NEC заново не пишет
    /// (18.10: Felepe Rivera и ещё трое из прошлых двух игр без этого не узнавались).
    /// </param>
    public static List<NecPlayer> Load(DateTime until, TimeSpan? window = null, bool everyone = false, bool sinceMatch = true)
    {
        var cache = Path.Combine(Root, "Cache");
        long to = new DateTimeOffset(until).ToUnixTimeMilliseconds(), from = to - (long)(window ?? Window).TotalMilliseconds;
        if (sinceMatch && !everyone && MatchFound(until) is { } found)
            from = Math.Max(from, new DateTimeOffset(found).ToUnixTimeMilliseconds() - 5000);
        if (everyone) (from, to) = (long.MinValue, long.MaxValue);
        var own = OwnSteamIds();
        var seen = new Dictionary<string, (long At, string Name)>();
        void Add(string? sid, long at, string? name)
        {
            if (sid is null || own.Contains(sid)) return;
            name = name?.Trim();
            if (seen.TryGetValue(sid, out var old))
                seen[sid] = (Math.Min(old.At, at), old.Name.Length > 0 ? old.Name : name ?? "");
            else
                seen[sid] = (at, name ?? "");
        }

        foreach (var (_, at, data) in Entries(Path.Combine(cache, "steam-nicknames.json")))
            if (at >= from && at <= to && Str(data, "source") == "platform") Add(Str(data, "steamId"), at, Str(data, "persona"));
        var records = new Dictionary<string, JsonElement>();
        var avatarUrls = new Dictionary<string, string>();
        foreach (var (key, at, data) in Entries(Path.Combine(cache, "dota-records.json")))
        {
            records[key] = data;
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("profile", out var p) || p.ValueKind != JsonValueKind.Object) continue;
            if (Str(p, "steamid") is { } psid && Str(p, "avatarfull") is { } purl) avatarUrls[psid] = purl;
            if (at >= from && at <= to) Add(Str(p, "steamid"), at, Str(p, "personaname"));
        }
        // аватарка из профилей Steam, которые NEC подгружал для карточек, свежее, чем из OpenDota
        foreach (var (_, at, data) in Entries(Path.Combine(cache, "steam-profiles.json")))
        {
            if (Str(data, "steamId") is not { } psid) continue;
            if (Str(data, "avatar") is { Length: > 0 } purl) avatarUrls[psid] = purl;
            if (everyone) Add(psid, at, Str(data, "persona"));
        }
        // стандартная «?» Steam — у тысяч игроков, на экране никого не выдаёт (одинаковые у кандидатов отсеивает AvatarMatcher)
        var files = AvatarFiles();
        string? Avatar(string sid) => avatarUrls.TryGetValue(sid, out var u) && !u.Contains(DefaultAvatar) ? files(u) : null;

        var players = new List<NecPlayer>();
        foreach (var (sid, (at, nick)) in seen.OrderBy(kv => kv.Value.At))
        {
            if (!ulong.TryParse(sid, out var s64) || s64 <= SteamBase || s64 - SteamBase > uint.MaxValue) continue;
            string acc = (s64 - SteamBase).ToString(), url = OpenDota + acc;
            string name = nick;
            int rank = 0;
            bool closed = false, hasProfile = false;
            string? country = null;
            if (records.TryGetValue(Sha(url), out var rec) && rec.ValueKind == JsonValueKind.Object &&
                rec.TryGetProperty("profile", out var prof) && prof.ValueKind == JsonValueKind.Object)
            {
                hasProfile = true;
                if (name.Length == 0) name = Str(prof, "personaname")?.Trim() ?? "";
                rank = Int(rec, "rank_tier") ?? 0;
                closed = prof.TryGetProperty("fh_unavailable", out var fh) && fh.ValueKind == JsonValueKind.True;
                country = Str(prof, "loccountrycode");
            }
            int? wins = null, losses = null;
            if (records.TryGetValue(Sha(url + "/wl?significant=0"), out var wl))
                (wins, losses) = (Int(wl, "win"), Int(wl, "lose"));
            NecMatch? last = null;
            if (records.TryGetValue(Sha(url + LastMatchQuery), out var ms) && ms.ValueKind == JsonValueKind.Array && ms.GetArrayLength() > 0)
            {
                var m = ms[0];
                last = new NecMatch(DateTimeOffset.FromUnixTimeSeconds(Int(m, "start_time") ?? 0).LocalDateTime, Int(m, "hero_id") ?? 0,
                    Int(m, "kills") ?? 0, Int(m, "deaths") ?? 0, Int(m, "assists") ?? 0, Int(m, "game_mode") ?? 0);
            }
            players.Add(new NecPlayer(sid, name.Length > 0 ? name : acc, DateTimeOffset.FromUnixTimeMilliseconds(at).LocalDateTime,
                hasProfile, rank, closed, wins, losses, country, last, Avatar(sid)));
        }
        return players;
    }

    const int JournalTail = 768 * 1024;
    static readonly object MatchesGate = new();
    static (string Path, DateTime Stamp, long Length, List<(string Id, DateTime At)> Found) _matches;

    /// <summary>
    /// Когда NEC нашёл последнюю игру до <paramref name="until"/>: её matchId впервые в состоянии MATCHED в телеметрии NEC
    /// (operations\client-*.jsonl, событие match.settlement). Профили игроков NEC пишет в кэш сразу, как игра найдена
    /// (18.10: найдена 11:22:39 UTC — записаны 11:22:40). null — найденных игр в журнале нет (комната без подбора).
    /// </summary>
    public static DateTime? MatchFound(DateTime until)
    {
        var dir = Path.Combine(Root, "operations");
        if (!Directory.Exists(dir)) return null;
        // NEC пишет в client-0 и, дойдя до 2 МБ, сдвигает файлы: последние игры — в двух самых свежих
        var files = new DirectoryInfo(dir).GetFiles("client-*.jsonl").OrderByDescending(f => f.LastWriteTimeUtc).Take(2).ToList();
        if (files.Count == 0) return null;
        List<(string Id, DateTime At)> found;
        lock (MatchesGate) // зовут и из потока окна, и из фонового (чтение экрана)
        {
            if (_matches.Path != files[0].FullName || _matches.Stamp != files[0].LastWriteTimeUtc || _matches.Length != files[0].Length)
                _matches = (files[0].FullName, files[0].LastWriteTimeUtc, files[0].Length, ReadMatches(files));
            found = _matches.Found;
        }
        // у каждой игры — первый раз, когда она найдена; из них последний до until
        var starts = found.GroupBy(m => m.Id).Select(g => g.Min(m => m.At)).Where(at => at <= until).ToList();
        return starts.Count > 0 ? starts.Max() : null;
    }

    // найденные игры (matchId, когда) из хвостов журналов: журнал пишется постоянно, последние игры — в конце
    static List<(string Id, DateTime At)> ReadMatches(List<FileInfo> files)
    {
        var found = new List<(string, DateTime)>();
        foreach (var f in files)
            try
            {
                using var stream = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length > JournalTail) stream.Seek(-JournalTail, SeekOrigin.End);
                using var reader = new StreamReader(stream);
                if (stream.Position > 0) reader.ReadLine(); // строка, разрезанная началом хвоста
                while (reader.ReadLine() is { } line)
                {
                    if (!line.Contains("\"match.settlement\"") || !line.Contains("\"matchState\":\"MATCHED\"")) continue;
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (Str(root, "time") is { } time && root.TryGetProperty("detail", out var d) && Str(d, "matchId") is { Length: > 0 } id
                        && DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var at))
                        found.Add((id, at.ToLocalTime()));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Log.Write($"NEC: журнал {f.Name} не прочитан: {ex.Message}");
            }
        return found;
    }

    /// <summary>Файл стандартной аватарки Steam «?» из кэша NEC — с ней сравниваем, чтобы такие аватарки никого не выдавали.</summary>
    public static string? DefaultAvatarFile() => AvatarFiles()($"https://avatars.steamstatic.com/{DefaultAvatar}_full.jpg");

    /// <summary>Номер аккаунта Dota по SteamID64; null — не похоже на аккаунт.</summary>
    public static uint? AccountOf(string steamId) =>
        ulong.TryParse(steamId, out var s64) && s64 > SteamBase && s64 - SteamBase <= uint.MaxValue ? (uint)(s64 - SteamBase) : null;

    /// <summary>Аватарки своих аккаунтов (из data\platform.json NEC): на экране матча их надо узнавать, чтобы не приписать чужому.</summary>
    public static List<string> OwnAvatars()
    {
        var files = AvatarFiles();
        var result = new List<string>();
        if (Read(Path.Combine(Root, "data", "platform.json")) is { } json && json.TryGetProperty("users", out var users) && users.ValueKind == JsonValueKind.Array)
            foreach (var u in users.EnumerateArray())
                if (Str(u, "avatar") is { } url && files(url) is { } file) result.Add(file);
        return result;
    }

    // Аватарки NEC: Cache\avatar-index.json — ключ sha256 полного адреса (…_full.jpg), в записи — имя файла в Cache\avatars.
    static Func<string, string?> AvatarFiles()
    {
        var dir = Path.Combine(Root, "Cache", "avatars");
        var index = new Dictionary<string, string>();
        foreach (var (key, _, data) in Entries(Path.Combine(Root, "Cache", "avatar-index.json")))
            if (Str(data, "file") is { } file) index[key] = file;
        return url => index.TryGetValue(Sha(url), out var f) && File.Exists(Path.Combine(dir, f)) ? Path.Combine(dir, f) : null;
    }

    /// <summary>Коротко для плашки под ником: медаль и процент побед или «история закрыта».</summary>
    public static (string Text, Color Color) Badge(NecPlayer p)
    {
        string rank = Ranks.Name(p.RankTier) ?? "без медали";
        if (p.Wins is int w && p.Losses is int l && w + l > 0) return ($"{rank} · {100.0 * w / (w + l):0}%", OverlayWindow.White);
        return (p.HistoryClosed ? rank + " · история закрыта" : rank, p.HistoryClosed ? OverlayWindow.Red : OverlayWindow.White);
    }

    /// <summary>По две строки на игрока: ник и медаль, затем победы/поражения и последний матч (или «история закрыта»).</summary>
    public static List<(string Text, Color Color)> Lines(IEnumerable<NecPlayer> players, IReadOnlyDictionary<int, string> heroes)
    {
        var lines = new List<(string, Color)>();
        foreach (var p in players)
        {
            lines.Add(($"{p.Name} — {Ranks.Name(p.RankTier) ?? "без медали"}" + (p.Country is { Length: > 0 } c ? $" · {c}" : ""), OverlayWindow.White));
            lines.Add((Details(p, heroes), p.HistoryClosed ? OverlayWindow.Red : OverlayWindow.Gray));
        }
        return lines;
    }

    /// <summary>Плитка на игрока для окна лобби: ник антиквой и чип со званием, ниже — победы/поражения и последний матч.</summary>
    public static IEnumerable<UIElement> Tiles(IEnumerable<NecPlayer> players, IReadOnlyDictionary<int, string> heroes)
    {
        foreach (var p in players)
        {
            var head = new DockPanel();
            // у ника — звание и страна; без медали — только страна (надпись «без медали» лишь шумит)
            var label = string.Join(" · ", new[] { Ranks.Name(p.RankTier), p.Country }.Where(s => !string.IsNullOrEmpty(s)));
            if (label.Length > 0)
            {
                var chip = Glass.Chip(label, p.RankTier > 0 ? Glass.GoldLight : Glass.Muted, 11.5);
                chip.Margin = new Thickness(6, 2, 0, 0);
                chip.VerticalAlignment = VerticalAlignment.Top;
                DockPanel.SetDock(chip, Dock.Right);
                head.Children.Add(chip);
            }
            // ник — кнопка: профиль Steam (иначе клик проходил сквозь карточку в ссылки самой кастомки)
            string sid = p.SteamId;
            head.Children.Add(new Border
            {
                Child = Glass.Text(p.Name, 18, Glass.Ink, serif: true, bold: true, wrap: false),
                Background = Brushes.Transparent, CornerRadius = new CornerRadius(6), Padding = new Thickness(3, 0, 3, 0),
                Margin = new Thickness(-3, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Профиль Steam", Tag = (Action)(() => OpenProfile(sid)),
            });
            var box = new StackPanel();
            box.Children.Add(head);
            box.Children.Add(Glass.Text(Details(p, heroes), 12, p.HistoryClosed ? Glass.Bad : Glass.Muted, top: 1));
            yield return Glass.Tile(box, top: 5, padding: new Thickness(10, 6, 8, 7));
        }
    }

    public static void OpenProfile(string steamId)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo($"https://steamcommunity.com/profiles/{steamId}") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Log.Write("Профиль Steam не открыт: " + ex.Message);
        }
    }

    static string Details(NecPlayer p, IReadOnlyDictionary<int, string> heroes)
    {
        var parts = new List<string>();
        if (p.Wins is int w && p.Losses is int l && w + l > 0) parts.Add($"{w}–{l} ({100.0 * w / (w + l):0}%)");
        if (p.LastMatch is { } m)
            parts.Add($"последний: {(heroes.TryGetValue(m.HeroId, out var h) ? h : "герой " + m.HeroId)} {m.Kills}/{m.Deaths}/{m.Assists} " +
                      $"{ModeName(m.GameMode)}, {m.Start:dd.MM.yy}");
        if (p.HistoryClosed) parts.Add("история матчей закрыта");
        if (!p.HasProfile) parts.Add("NEC не загружал профиль OpenDota");
        return parts.Count > 0 ? string.Join(" · ", parts) : "матчей нет";
    }

    static string ModeName(int mode) => mode switch
    {
        1 => "All Pick", 2 => "Captains Mode", 3 => "Random Draft", 4 => "Single Draft", 5 => "All Random", 16 => "Captains Draft",
        18 => "Ability Draft", 19 => "кастомка", 21 => "1 на 1", 22 => "рейтинг", 23 => "Турбо", _ => "режим " + mode,
    };

    static HashSet<string> OwnSteamIds()
    {
        var ids = new HashSet<string>();
        var json = Read(Path.Combine(Root, "data", "platform.json"));
        if (json?.TryGetProperty("users", out var users) == true && users.ValueKind == JsonValueKind.Array)
            foreach (var u in users.EnumerateArray())
                if (Str(u, "steamId") is { } sid) ids.Add(sid);
        return ids;
    }

    /// <summary>Записи кэша NEC: {"entries":[[ключ,{"at":мс,"data":…}],…]}.</summary>
    static IEnumerable<(string Key, long At, JsonElement Data)> Entries(string path)
    {
        if (Read(path) is not { } root || !root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) yield break;
        foreach (var e in entries.EnumerateArray())
            if (e.ValueKind == JsonValueKind.Array && e.GetArrayLength() == 2 && e[0].GetString() is { } key &&
                e[1].ValueKind == JsonValueKind.Object && Int64(e[1], "at") is long at && e[1].TryGetProperty("data", out var data))
                yield return (key, at, data);
    }

    // NEC переписывает файлы кэша на ходу: читаем без блокировки, недописанный файл — как пустой (через минуту прочитается)
    static JsonElement? Read(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(fs);
            return doc.RootElement.Clone();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            if (ex is not FileNotFoundException and not DirectoryNotFoundException) Log.Write($"NEC: не прочитан {Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
    static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static int? Int(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
    static long? Int64(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : null;
}
