using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DotaReports;

/// <summary>
/// Игрок матча: слот 0–9 (0–4 Силы Света, 5–9 Силы Тьмы), аккаунт, ник, медаль (rank_tier OpenDota, 0 — нет)
/// и сколько игр вы сыграли с ним вместе и против (OpenDota, все режимы).
/// </summary>
sealed record LivePlayer(int Slot, uint Account, string Name, int RankTier, int With = 0, int Against = 0);

/// <summary>
/// Кто в матче — ещё на пике. Подключаясь к серверу Valve, Dota пишет в console.log
/// «Host activate: Remote Connect (=[A:1:&lt;id&gt;:&lt;instance&gt;])» — это Steam ID сервера; по нему Steam Web API
/// IDOTA2MatchStats_570/GetRealtimeStats (ключ — apikey.txt в папке данных) отдаёт игроков с аккаунтами, медали — OpenDota.
/// Кастомки NEC подключаются по IP — у них этого нет. Прошлый способ (страница Steam «Недавно играли вместе»)
/// не годился: Steam пишет её только после матча.
/// </summary>
static class LiveMatch
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    static readonly Regex Connect = new(@"Host activate: Remote Connect \(=\[A:1:(\d+):(\d+)\]\)");
    static readonly Dictionary<uint, int> Ranks = new();
    const int TailBytes = 8 << 20; // console.log растёт до сотен МБ — читаем только хвост

    static string KeyFile => Path.Combine(Paths.Root, "apikey.txt");
    public static bool HasKey => File.Exists(KeyFile) && new FileInfo(KeyFile).Length > 0;

    /// <summary>Steam ID сервера Valve, к которому Dota подключилась последней, и когда (по console.log); null — не найден.</summary>
    public static (ulong Server, DateTime At)? LastServer()
    {
        if (Setup.FindDota() is not { } dota) return null;
        var path = Path.Combine(dota, "game", "dota", "console.log");
        if (!File.Exists(path)) return null;
        string text;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            long take = Math.Min(fs.Length, TailBytes);
            fs.Seek(-take, SeekOrigin.End);
            var buf = new byte[take];
            fs.ReadExactly(buf);
            text = Encoding.UTF8.GetString(buf);
        }
        var m = Connect.Matches(text).LastOrDefault();
        if (m == null) return null;
        ulong account = ulong.Parse(m.Groups[1].Value), instance = ulong.Parse(m.Groups[2].Value);
        // строка начинается со времени «MM/dd HH:mm:ss»
        int line = text.LastIndexOf('\n', m.Index) + 1;
        var at = line + 14 <= text.Length && DateTime.TryParseExact(text.Substring(line, 14), "MM/dd HH:mm:ss",
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : DateTime.MinValue;
        // SteamID64 анонимного игрового сервера: вселенная 1, тип 4 (A), экземпляр, номер
        return ((1UL << 56) | (4UL << 52) | (instance << 32) | account, at);
    }

    /// <summary>
    /// Игроки матча на этом сервере (слоты по порядку в команде); null — Steam пока не отдаёт.
    /// Последний ответ Steam — в realtime_stats.json (без ключа), чтобы разобрать, если что-то не так.
    /// </summary>
    public static async Task<List<(int Slot, uint Account, string Name)>?> PlayersAsync(ulong server)
    {
        if (!HasKey) return null;
        var key = (await File.ReadAllTextAsync(KeyFile)).Trim();
        using var resp = await Http.GetAsync($"https://api.steampowered.com/IDOTA2MatchStats_570/GetRealtimeStats/v1/?key={key}&server_steam_id={server}");
        var body = await resp.Content.ReadAsStringAsync();
        await File.WriteAllTextAsync(Path.Combine(Paths.Root, "realtime_stats.json"), body);
        if (!resp.IsSuccessStatusCode) throw new HttpRequestException($"Steam GetRealtimeStats: {(int)resp.StatusCode}");
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("teams", out var teams) || teams.ValueKind != JsonValueKind.Array) return null;
        var result = new List<(int, uint, string)>();
        foreach (var team in teams.EnumerateArray())
        {
            int number = team.TryGetProperty("team_number", out var n) && n.TryGetInt32(out var tn) ? tn : 0;
            if (number is not (2 or 3) || !team.TryGetProperty("players", out var players) || players.ValueKind != JsonValueKind.Array) continue;
            // в команде — по номеру игрока, это порядок слотов в верхней полосе
            var ordered = players.EnumerateArray()
                .OrderBy(p => p.TryGetProperty("playerid", out var id) && id.TryGetInt32(out var v) ? v : 99).ToList();
            for (int i = 0; i < ordered.Count && i < 5; i++)
            {
                var p = ordered[i];
                uint account = p.TryGetProperty("accountid", out var a) && a.TryGetUInt32(out var av) ? av : 0;
                string name = p.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "";
                result.Add(((number == 2 ? 0 : 5) + i, account, name));
            }
        }
        return result.Count > 0 ? result : null;
    }

    /// <summary>
    /// С кем вы уже играли: аккаунт → игр вместе и против (OpenDota players/&lt;вы&gt;/peers, significant=0 — с Турбо).
    /// Пусто — нет account.txt или OpenDota не ответил.
    /// </summary>
    public static async Task<Dictionary<uint, (int With, int Against)>> PeersAsync()
    {
        var peers = new Dictionary<uint, (int, int)>();
        if (!File.Exists(Paths.Account) || !uint.TryParse((await File.ReadAllTextAsync(Paths.Account)).Trim(), out var me)) return peers;
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.opendota.com/api/players/{me}/peers?significant=0"));
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var p in doc.RootElement.EnumerateArray())
                    if (p.TryGetProperty("account_id", out var id) && id.TryGetUInt32(out var account))
                        peers[account] = (Int(p, "with_games"), Int(p, "against_games"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log.Write("Звания на пике: знакомые с OpenDota не получены: " + ex.Message);
        }
        return peers;
    }

    static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;

    /// <summary>Медаль игрока с OpenDota (rank_tier); 0 — нет, скрыт или запрос не прошёл. Один раз на аккаунт.</summary>
    public static async Task<int> RankAsync(uint account)
    {
        if (account is 0 or uint.MaxValue) return 0; // бот или скрытый аккаунт
        lock (Ranks)
            if (Ranks.TryGetValue(account, out var cached)) return cached;
        int tier = 0;
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.opendota.com/api/players/{account}"));
            if (doc.RootElement.TryGetProperty("rank_tier", out var rt) && rt.ValueKind == JsonValueKind.Number) tier = rt.GetInt32();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log.Write($"Звания на пике: OpenDota для {account} не ответил: {ex.Message}");
            return 0; // не запоминаем — спросим снова
        }
        lock (Ranks) Ranks[account] = tier;
        return tier;
    }
}

/// <summary>
/// Ники под портретами верхней полосы на экране пика (1920×1080, по кадрам frames\ и скриншоту 17.10): полоса
/// y 82–102, ник по центру под слотом (центр — x слота + 50, слоты как в GridReader), длинный — с многоточием.
/// Левый край ника — первый столбец со светлыми буквами; медаль ставится сразу перед ним.
/// </summary>
static class TopBarNames
{
    static readonly int[] SlotX = { 219, 343, 467, 591, 715, 1098, 1222, 1346, 1470, 1594 };
    public static readonly Rectangle Band = new(140, 82, 1680, 20);
    const int Half = 60;   // ник шире слота не бывает (124 px), а дальше начинается ник соседа
    public const int Icon = 22;

    /// <summary>Левый край ника в каждом из 10 слотов, px экрана; -1 — ника не видно.</summary>
    public static int[] TextLeft(Bitmap band)
    {
        var left = new int[10];
        for (int s = 0; s < 10; s++)
        {
            left[s] = -1;
            int center = SlotX[s] + 50 - Band.X;
            for (int x = Math.Max(center - Half, 0); x <= center && x < band.Width; x++)
            {
                int bright = 0;
                for (int y = 1; y < band.Height - 1; y++)
                {
                    var c = band.GetPixel(x, y);
                    if (Math.Min(c.R, Math.Min(c.G, c.B)) >= 165) bright++;
                }
                if (bright >= 2)
                {
                    left[s] = x + Band.X;
                    break;
                }
            }
        }
        return left;
    }

    /// <summary>
    /// Медаль перед ником (у кого она есть и ник виден) и под ником — «за N · против M», если вы уже играли
    /// с этим игроком (вместе / друг против друга).
    /// </summary>
    /// <param name="medals">false — без медалей (All Pick: Dota показывает звания сама), только «за · против».</param>
    public static List<BadgeSpot> Spots(IReadOnlyList<LivePlayer> players, int[] left, bool medals = true)
    {
        var spots = new List<BadgeSpot>();
        foreach (var p in players.Where(p => p.Slot is >= 0 and < 10 && left[p.Slot] >= 0))
        {
            if (medals && p.RankTier / 10 is >= 1 and <= 8)
                spots.Add(new BadgeSpot(left[p.Slot] - 2 - Icon, Band.Y - 1, false, p.RankTier, null, Glass.Ink, Icon));
            // нули не пишем: «за 3», «против 2» или «за 3 · против 2»
            var parts = new[] { p.With > 0 ? $"за {p.With}" : null, p.Against > 0 ? $"против {p.Against}" : null }.OfType<string>().ToList();
            if (parts.Count > 0)
                spots.Add(new BadgeSpot(SlotX[p.Slot] + 50, Band.Bottom + 1, false, 0, string.Join(" · ", parts),
                    Glass.GoldLight, 0, Center: true, FontSize: 11.5));
        }
        return spots;
    }
}
