using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Brushes = System.Windows.Media.Brushes;

namespace DotaReports;

/// <summary>
/// Игрок матча: слот 0–9 (0–4 Силы Света, 5–9 Силы Тьмы; -1 — неизвестен), аккаунт, ник, медаль (rank_tier OpenDota, 0 — нет)
/// и сколько игр вы сыграли с ним вместе и против (OpenDota, все режимы).
/// </summary>
sealed record LivePlayer(int Slot, uint Account, string Name, int RankTier, int With = 0, int Against = 0);

/// <summary>
/// Кто в матче Valve: аккаунты — из Steam «Недавние игроки» (Coplay), ники — Steam GetPlayerSummaries (ключ — apikey.txt
/// в папке данных; без ключа — ник из OpenDota), медали и знакомые — OpenDota.
/// </summary>
static class LiveMatch
{
    const ulong SteamBase = 76561197960265728;
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    static readonly Dictionary<uint, int> Ranks = new();
    static readonly Dictionary<uint, string> OpenDotaNames = new();

    static string KeyFile => Path.Combine(Paths.Root, "apikey.txt");
    public static bool HasKey => File.Exists(KeyFile) && new FileInfo(KeyFile).Length > 0;

    /// <summary>
    /// Игроки матча на этом сервере (слоты по порядку в команде); null — Steam не отдаёт. Только для --live-test:
    /// состав обычных матчей (Турбо) Steam не отдаёт — 20.10 на пике пустой кадр, в игре «{}»; работает лишь у транслируемых топ-игр.
    /// Последний ответ Steam — в realtime_stats.json (без ключа).
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
    /// null — нет account.txt или OpenDota не ответил (тогда «знакомых нет» писать нельзя).
    /// </summary>
    public static async Task<Dictionary<uint, (int With, int Against)>?> PeersAsync()
    {
        if (!File.Exists(Paths.Account) || !uint.TryParse((await File.ReadAllTextAsync(Paths.Account)).Trim(), out var me)) return null;
        var peers = new Dictionary<uint, (int, int)>();
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
            return null;
        }
        return peers;
    }

    static int Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;

    /// <summary>Медаль игрока с OpenDota (rank_tier); 0 — нет, скрыт или запрос не прошёл. Один раз на аккаунт; ник — заодно.</summary>
    public static async Task<int> RankAsync(uint account)
    {
        if (account is 0 or uint.MaxValue) return 0; // бот или скрытый аккаунт
        lock (Ranks)
            if (Ranks.TryGetValue(account, out var cached)) return cached;
        int tier = 0;
        string? name = null;
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.opendota.com/api/players/{account}"));
            if (doc.RootElement.TryGetProperty("rank_tier", out var rt) && rt.ValueKind == JsonValueKind.Number) tier = rt.GetInt32();
            if (doc.RootElement.TryGetProperty("profile", out var pr) && pr.ValueKind == JsonValueKind.Object
                && pr.TryGetProperty("personaname", out var pn) && pn.ValueKind == JsonValueKind.String) name = pn.GetString();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log.Write($"Звания на пике: OpenDota для {account} не ответил: {ex.Message}");
            return 0; // не запоминаем — спросим снова
        }
        lock (Ranks)
        {
            Ranks[account] = tier;
            if (!string.IsNullOrEmpty(name)) OpenDotaNames[account] = name;
        }
        return tier;
    }

    /// <summary>
    /// Ники сейчас — как их показывает Dota (Steam GetPlayerSummaries, одним запросом); без ключа или если Steam не ответил —
    /// ник из профиля OpenDota (после RankAsync), он бывает устаревшим.
    /// </summary>
    public static async Task<Dictionary<uint, string>> NamesAsync(IReadOnlyCollection<uint> accounts)
    {
        var names = new Dictionary<uint, string>();
        lock (Ranks)
            foreach (var a in accounts)
                if (OpenDotaNames.TryGetValue(a, out var n)) names[a] = n;
        if (!HasKey || accounts.Count == 0) return names;
        try
        {
            var key = (await File.ReadAllTextAsync(KeyFile)).Trim();
            var ids = string.Join(",", accounts.Select(a => SteamBase + a));
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.steampowered.com/ISteamUser/GetPlayerSummaries/v2/?key={key}&steamids={ids}"));
            if (doc.RootElement.TryGetProperty("response", out var r) && r.TryGetProperty("players", out var players) && players.ValueKind == JsonValueKind.Array)
                foreach (var p in players.EnumerateArray())
                    if (p.TryGetProperty("steamid", out var sid) && ulong.TryParse(sid.GetString(), out var s64) && s64 > SteamBase
                        && p.TryGetProperty("personaname", out var pn) && pn.GetString() is { Length: > 0 } name)
                        names[(uint)(s64 - SteamBase)] = name;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            Log.Write("Звания на пике: ники из Steam не получены: " + ex.Message);
        }
        return names;
    }

    /// <summary>
    /// Плитки для окна «Игроки матча»: значок медали, ник (клик — профиль Steam) и чип со званием, ниже — сколько вы с ним сыграли вместе
    /// и против. peersKnown — список знакомых с OpenDota получен (иначе «не встречались» было бы неправдой).
    /// </summary>
    public static IEnumerable<UIElement> Tiles(IEnumerable<LivePlayer> players, bool peersKnown)
    {
        foreach (var p in players)
        {
            var head = new DockPanel();
            var chip = Glass.Chip(DotaReports.Ranks.Name(p.RankTier) ?? "без медали", p.RankTier > 0 ? Glass.GoldLight : Glass.Muted, 11.5);
            chip.Margin = new Thickness(6, 2, 0, 0);
            chip.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(chip, Dock.Right);
            head.Children.Add(chip);
            string sid = (SteamBase + p.Account).ToString();
            head.Children.Add(new Border
            {
                Child = Glass.Text(p.Name, 18, Glass.Ink, serif: true, bold: true, wrap: false),
                Background = Brushes.Transparent, CornerRadius = new CornerRadius(6), Padding = new Thickness(3, 0, 3, 0),
                Margin = new Thickness(-3, 0, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Cursor = System.Windows.Input.Cursors.Hand,
                ToolTip = "Профиль Steam", Tag = (Action)(() => NecLobby.OpenProfile(sid)),
            });
            // нули не пишем: «за 3», «против 2» или «за 3 · против 2»
            var parts = new[] { p.With > 0 ? $"за {p.With}" : null, p.Against > 0 ? $"против {p.Against}" : null }.OfType<string>().ToList();
            string detail = parts.Count > 0 ? "играли: " + string.Join(" · ", parts) : peersKnown ? "раньше не встречались" : "знакомые: OpenDota не ответил";
            var box = new StackPanel();
            box.Children.Add(head);
            box.Children.Add(Glass.Text(detail, 12, parts.Count > 0 ? Glass.GoldLight : Glass.Muted, top: 1));
            // слева — значок медали, как в клиенте (без медали — кружок «?»)
            var icon = RankIcon.Build(p.RankTier, 38);
            icon.Margin = new Thickness(0, 0, 9, 0);
            icon.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(icon, Dock.Left);
            var row = new DockPanel();
            row.Children.Add(icon);
            row.Children.Add(box);
            yield return Glass.Tile(row, top: 5, padding: new Thickness(8, 6, 8, 7));
        }
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

    /// <summary>Под каким слотом текст с центром в x (px экрана): ближайший центр ника, не дальше полуширины; null — ничей.</summary>
    public static int? SlotOf(double x)
    {
        int best = Enumerable.Range(0, 10).MinBy(s => Math.Abs(SlotX[s] + 50 - x));
        return Math.Abs(SlotX[best] + 50 - x) <= Half ? best : null;
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
