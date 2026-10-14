using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;

namespace DotaReports;

/// <summary>Игрок из списка Steam «Недавно играли вместе»: номер аккаунта Dota (32 бита), ник, игра (570 — Dota 2; пусто — не понять).</summary>
sealed record CoplayPlayer(string AccountId, string Name, string App);

/// <summary>Звание по OpenDota: rank_tier (54 — Легенда IV, 0 — звания нет, null — OpenDota не ответил) и место Титана в таблице.</summary>
sealed record PlayerRank(string AccountId, string Name, int? RankTier, int? Leaderboard);

/// <summary>
/// steamcommunity.com/my/friends/coplay — «Недавно играли вместе», в том же браузере, что и GCPD (вход в Steam уже есть).
/// Steam не говорит, кто в какой команде и на каком герое, — только с кем вы играли.
/// </summary>
sealed class CoplayReader
{
    const string Url = "https://steamcommunity.com/my/friends/coplay";
    const ulong SteamId64Base = 76561197960265728;
    static readonly TimeSpan NavTimeout = TimeSpan.FromSeconds(30);
    static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(20);

    readonly SteamBrowser _b;

    public CoplayReader(SteamBrowser browser) => _b = browser;

    /// <summary>Игроки Dota 2 со страницы (без вас); null — нужен вход в Steam. Разобранная страница — в dumpPath.</summary>
    public async Task<List<CoplayPlayer>?> ReadAsync(string? dumpPath = null)
    {
        await _b.NavigateAsync(Url, NavTimeout);
        JsonElement page = default;
        string state = "wait";
        var until = DateTime.UtcNow + StateTimeout;
        while (true)
        {
            try
            {
                page = await _b.EvalAsync(PageJs);
                state = page.ValueKind == JsonValueKind.Object && page.TryGetProperty("state", out var st) ? st.GetString() ?? "wait" : "wait";
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException or System.Runtime.InteropServices.COMException)
            {
                state = "wait"; // страница в процессе перехода
            }
            if (state is "login" or "ok" || DateTime.UtcNow >= until) break;
            await Task.Delay(500);
        }
        if (state == "login") return null;
        if (state != "ok") throw new InvalidOperationException($"Страница «Недавно играли вместе» не открылась (открыто: {_b.Source}).");
        Dump(page, dumpPath ?? Paths.CoplayDump);

        var me = File.Exists(Paths.Account) ? File.ReadAllText(Paths.Account).Trim() : null;
        var players = new List<CoplayPlayer>();
        foreach (var p in page.GetProperty("players").EnumerateArray())
        {
            // data-steamid — 64-битный SteamID, data-miniprofile — уже номер аккаунта
            string? id = ulong.TryParse(Str(p, "steamId"), out var id64) && id64 > SteamId64Base ? (id64 - SteamId64Base).ToString()
                : uint.TryParse(Str(p, "mini"), out var mini) && mini > 0 ? mini.ToString() : null;
            if (id == null || id == me || players.Any(x => x.AccountId == id)) continue;
            players.Add(new CoplayPlayer(id, Str(p, "name"), Str(p, "app")));
        }
        // на странице и другие игры: если игру удалось понять — только Dota 2
        return players.Any(x => x.App == "570") ? players.Where(x => x.App == "570").ToList() : players;
    }

    static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static void Dump(JsonElement page, string path)
    {
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(path, JsonSerializer.Serialize(new { fetchedAt = DateTime.Now.ToString("s"), page }, options));
    }

    // Блок игрока — внешний элемент с data-steamid / data-miniprofile (внутри бывает аватар с тем же номером).
    // Игра блока — ссылка на /app/N в его группе (.coplayGroup), а без неё — ближайшая перед ним вне блоков игроков.
    const string PageJs = """
        (() => {
          if (document.readyState !== 'complete') return { state: 'wait' };
          if (/^login\./.test(location.host) || /\/login(\/|$)/.test(location.pathname)) return { state: 'login' };
          if (!/\/friends\/coplay/.test(location.pathname)) return { state: 'other', href: location.href };
          const clean = s => (s || '').replace(/\s+/g, ' ').trim();
          const sel = '[data-steamid], [data-miniprofile]';
          const blocks = [...document.querySelectorAll(sel)].filter(e => !(e.parentElement && e.parentElement.closest(sel)));
          const apps = [...document.querySelectorAll('a[href*="/app/"]')].filter(a => !a.closest(sel));
          const appOf = b => {
            let found = null;
            for (const a of apps) if (a.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING) found = a;
            return found;
          };
          const players = blocks.map(b => {
            const grp = b.closest('.coplayGroup');
            const a = (grp && [...grp.querySelectorAll('a[href*="/app/"]')].find(x => !x.closest(sel))) || appOf(b);
            const m = a ? /\/app\/(\d+)/.exec(a.getAttribute('href') || '') : null;
            const content = b.querySelector('.friend_block_content') || b;
            return {
              steamId: b.getAttribute('data-steamid') || '',
              mini: b.getAttribute('data-miniprofile') || '',
              name: clean((content.innerText || '').split('\n')[0]),
              app: m ? m[1] : ''
            };
          });
          return {
            state: 'ok', href: location.href, players,
            sample: blocks.length ? blocks[0].outerHTML.slice(0, 800) : null,
            pageText: clean(document.body ? document.body.innerText : '').slice(0, 1500)
          };
        })()
        """;
}

/// <summary>Звания: названия медалей и публичные данные профилей OpenDota.</summary>
static class Ranks
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    static readonly string[] Medals = { "", "Рекрут", "Страж", "Рыцарь", "Герой", "Легенда", "Властелин", "Божество", "Титан" };
    static readonly string[] Stars = { "", "I", "II", "III", "IV", "V" };

    /// <summary>Звание по rank_tier: 54 — «Легенда IV»; null — звания нет.</summary>
    public static string? Name(int rankTier)
    {
        int medal = rankTier / 10, star = rankTier % 10;
        if (medal is < 1 or > 8) return null;
        return Medals[medal] + (star is >= 1 and <= 5 ? " " + Stars[star] : "");
    }

    /// <summary>Звания игроков по OpenDota, все запросы сразу; кого OpenDota не отдал — с RankTier = null.</summary>
    public static async Task<List<PlayerRank>> FetchAsync(IEnumerable<CoplayPlayer> players) =>
        (await Task.WhenAll(players.Select(p => FetchOneAsync(p)))).ToList();

    static async Task<PlayerRank> FetchOneAsync(CoplayPlayer p)
    {
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.opendota.com/api/players/{p.AccountId}"));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new PlayerRank(p.AccountId, p.Name, null, null);
            int? Int(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
            // ник со страницы Steam — нынешний; нет его — из OpenDota
            var nick = p.Name.Length > 0 ? p.Name
                : root.TryGetProperty("profile", out var prof) && prof.ValueKind == JsonValueKind.Object
                  && prof.TryGetProperty("personaname", out var pn) && pn.ValueKind == JsonValueKind.String ? pn.GetString() ?? "" : "";
            return new PlayerRank(p.AccountId, nick, Int("rank_tier") ?? 0, Int("leaderboard_rank"));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or FormatException)
        {
            Log.Write($"OpenDota: игрок {p.AccountId} не прочитан: {ex.Message}");
            return new PlayerRank(p.AccountId, p.Name, null, null);
        }
    }
}
