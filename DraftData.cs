using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DotaReports;

sealed record CounterResult(int HeroId, double Score, IReadOnlyList<string> Parts, bool LowData);

/// <summary>Кандидат на пик в Турбо: винрейт в Турбо на вашем ранге плюс преимущество против засвеченных врагов.</summary>
/// <summary>Кандидат на пик: винрейт в режиме на вашей роли плюс поправки за врагов и союзников. Rare — на этой роли его почти не берут.</summary>
sealed record TurboPick(int HeroId, double Winrate, double VsRevealed, double WithAllies, double Total, bool Rare);

/// <summary>
/// Данные для пика: герои (OpenDota), матчапы герой-против-героя по вашему брекету (Stratz, кэш на сутки),
/// ваш пул (OpenDota: статистика за год плюс последние 20 игр — годовая отстаёт).
/// </summary>
sealed class DraftData
{
    const int MinMatches = 100;
    static readonly TimeSpan MatchupsMaxAge = TimeSpan.FromHours(24);
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    Dictionary<int, string> _heroes = new();
    // _vs[враг][кандидат] = (synergy врага против кандидата, матчей). Отрицательная — кандидат контрит врага.
    Dictionary<int, Dictionary<int, (double Syn, int N)>> _vs = new();
    Dictionary<int, (int Games, int Wins)> _pool = new();
    // _modeStats["TURBO_2"] — Турбо, роль 2 (центр); "_0" — все роли. Режимы: TURBO, ALL_PICK_RANKED.
    Dictionary<string, Dictionary<int, (long N, long W)>> _modeStats = new();
    // _with[союзник][кандидат] = (synergy в одной команде, матчей)
    Dictionary<int, Dictionary<int, (double Syn, int N)>> _with = new();

    public IReadOnlyDictionary<int, string> Heroes => _heroes;
    public IReadOnlyDictionary<int, (int Games, int Wins)> Pool => _pool;
    public string Status { get; private set; } = "данные загружаются…";
    public bool Ready => _heroes.Count > 0 && _vs.Count > 0;
    public bool TurboReady => _heroes.Count > 0 && _modeStats.Count > 0;
    /// <summary>Подпись к данным Турбо: брекет и до какой даты у Stratz есть статистика.</summary>
    public string TurboLabel { get; private set; } = "";
    /// <summary>Медаль по OpenDota (rank_tier / 10): 1 — Рекрут … 8 — Титан; 0 — неизвестна.</summary>
    public int Medal { get; private set; }
    public int RankTier { get; private set; }
    /// <summary>Ваши данные Valve (GCPD): точные победы на героях и счёт против героев.</summary>
    public ProfileData? Profile { get; private set; }

    public async Task LoadAsync()
    {
        LoadFromCache();
        var errors = new List<string>();
        string? account = File.Exists(Paths.Account) ? File.ReadAllText(Paths.Account).Trim() : null;

        await Step("герои", errors, async () =>
            await File.WriteAllTextAsync(Paths.Heroes, await Http.GetStringAsync("https://api.opendota.com/api/heroes")));
        // список Valve с атрибутами — по нему строится раскладка сетки пика
        await Step("список героев Valve", errors, async () =>
            await File.WriteAllTextAsync(Paths.HeroList, await Http.GetStringAsync("https://www.dota2.com/datafeed/herolist?language=russian")));
        // портреты Valve — по ним узнаются герои в слотах верхней полосы (качаем только недостающие)
        await Step("портреты героев", errors, () => DownloadHeroImagesAsync(Paths.Portraits, ""));
        await Step("иконки героев", errors, () => DownloadHeroImagesAsync(Paths.Icons, "icons/"));
        if (account != null)
            await Step("ранг", errors, async () =>
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.opendota.com/api/players/{account}"));
                RankTier = doc.RootElement.TryGetProperty("rank_tier", out var rt) && rt.ValueKind == JsonValueKind.Number ? rt.GetInt32() : 0;
                Medal = RankTier / 10;
            });

        if (account != null)
        {
            await Step("ваш пул", errors, async () =>
            {
                await File.WriteAllTextAsync(Paths.Pool, await Http.GetStringAsync($"https://api.opendota.com/api/players/{account}/heroes?date=365"));
                await File.WriteAllTextAsync(Paths.Recent, await Http.GetStringAsync($"https://api.opendota.com/api/players/{account}/recentMatches"));
            });
        }

        bool stale = !File.Exists(Paths.Matchups) || !File.Exists(Paths.ModeStats) || _with.Count == 0 || DateTime.Now - File.GetLastWriteTime(Paths.Matchups) > MatchupsMaxAge;
        if (stale || !File.Exists(Paths.MatchupsBrackets))
        {
            await Step("данные Stratz", errors, async () =>
            {
                int medal = await MedalAsync(account);
                var pairs = PairBrackets(medal);
                await File.WriteAllBytesAsync(Paths.Matchups, await StratzAsync(
                    $"{{ heroStats {{ matchUp(bracketBasicIds: [{string.Join(", ", pairs)}], take: 130) {{ heroId vs {{ heroId2 synergy matchCount }} with {{ heroId2 synergy matchCount }} }} }} }}"));
                await File.WriteAllTextAsync(Paths.MatchupsBrackets, string.Join(", ", pairs));
                // Винрейты по режиму и роли: последние 4 недели, ваша медаль и соседняя (12 запросов, раз в сутки)
                var medals = MedalBrackets(medal);
                var all = new System.Text.Json.Nodes.JsonObject();
                foreach (var mode in new[] { "TURBO", "ALL_PICK_RANKED" })
                    for (int pos = 0; pos <= 5; pos++)
                    {
                        var posFilter = pos > 0 ? $", positionIds: [POSITION_{pos}]" : "";
                        var bytes = await StratzAsync(
                            $"{{ heroStats {{ winWeek(take: 4, bracketIds: [{string.Join(", ", medals)}], gameModeIds: [{mode}]{posFilter}, groupBy: HERO_ID) {{ heroId week matchCount winCount }} }} }}");
                        all[$"{mode}_{pos}"] = System.Text.Json.Nodes.JsonNode.Parse(bytes)!["data"]!["heroStats"]!["winWeek"]!.DeepClone();
                    }
                await File.WriteAllTextAsync(Paths.ModeStats, all.ToJsonString());
                await File.WriteAllTextAsync(Paths.TurboBrackets, string.Join("–", medals));
            });
        }

        LoadFromCache();
        var brk = File.Exists(Paths.MatchupsBrackets) ? File.ReadAllText(Paths.MatchupsBrackets) : "?";
        var age = File.Exists(Paths.Matchups) ? File.GetLastWriteTime(Paths.Matchups).ToString("dd.MM HH:mm") : "нет";
        Status = (Ready ? $"брекет {brk} · данные от {age}" : "нет данных матчапов")
                 + (errors.Count > 0 ? " · не обновилось: " + string.Join(", ", errors) : "");
    }

    /// <summary>
    /// Пока враги скрыты, лучший прогноз — винрейт героя в Турбо на вашем ранге: он уже учитывает,
    /// против кого обычно играют. Засвеченные враги добавляют преимущество против них (матчапы обычных режимов).
    /// </summary>
    /// <param name="mode">"TURBO" или "ALL_PICK" (null — Турбо).</param>
    /// <param name="position">1..5 — ваша роль; 0 — неизвестна, берём все роли.</param>
    public List<TurboPick> TurboPicks(string? mode, int position, ISet<int> unavailable, IReadOnlyCollection<int> enemies, IReadOnlyCollection<int> allies)
    {
        var modeKey = mode == "ALL_PICK" ? "ALL_PICK_RANKED" : "TURBO";
        if (!_modeStats.TryGetValue($"{modeKey}_0", out var overall)) return new();
        var atRole = position > 0 ? _modeStats.GetValueOrDefault($"{modeKey}_{position}") : null;
        long roleTotal = atRole?.Values.Sum(v => v.N) ?? 0;

        var result = new List<TurboPick>();
        foreach (var (hero, (n, w)) in overall)
        {
            if (n < MinModeMatches || unavailable.Contains(hero) || enemies.Contains(hero) || !_heroes.ContainsKey(hero)) continue;
            // На своей роли — винрейт на ней, если героя там реально берут (≥ 1% пиков роли); иначе «редко на этой роли».
            bool rare = false;
            double wr = 100.0 * w / n;
            if (atRole != null)
            {
                if (atRole.TryGetValue(hero, out var r) && r.N >= MinRoleMatches && r.N >= roleTotal * MinRoleShare)
                    wr = 100.0 * r.W / r.N;
                else
                    rare = true;
            }
            double vs = enemies.Sum(e => Advantage(hero, e) ?? 0);
            double with = allies.Sum(a => Synergy(a, hero) ?? 0);
            result.Add(new TurboPick(hero, wr, vs, with, wr - 50 + vs + with, rare));
        }
        return result.OrderBy(r => r.Rare).ThenByDescending(r => r.Total).ToList();
    }

    const long MinModeMatches = 2000, MinRoleMatches = 300;
    const double MinRoleShare = 0.008; // Skywrath на миде ≈ 1% пиков — должен считаться мидером

    double? Synergy(int ally, int candidate) =>
        _with.TryGetValue(ally, out var row) && row.TryGetValue(candidate, out var m) && m.N >= MinMatches ? m.Syn : null;

    double? Advantage(int candidate, int enemy) =>
        _vs.TryGetValue(enemy, out var row) && row.TryGetValue(candidate, out var m) && m.N >= MinMatches ? -m.Syn : null;

    public List<CounterResult> Counters(IReadOnlyCollection<int> enemies)
    {
        var result = new List<CounterResult>();
        foreach (var (hero, _) in _heroes)
        {
            if (enemies.Contains(hero)) continue;
            double score = 0;
            bool low = false;
            var parts = new List<string>();
            foreach (var e in enemies)
            {
                var enemyName = _heroes.GetValueOrDefault(e, $"#{e}");
                if (_vs.TryGetValue(e, out var row) && row.TryGetValue(hero, out var m) && m.N >= MinMatches)
                {
                    score -= m.Syn;
                    parts.Add($"против {enemyName}: {-m.Syn:+0.0;-0.0} ({m.N} матчей)");
                }
                else
                {
                    low = true;
                    parts.Add($"против {enemyName}: мало данных");
                }
            }
            result.Add(new CounterResult(hero, score, parts, low));
        }
        return result.OrderByDescending(r => r.Score).ToList();
    }

    void LoadFromCache()
    {
        try
        {
            if (File.Exists(Paths.Heroes))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(Paths.Heroes));
                _heroes = doc.RootElement.EnumerateArray()
                    .ToDictionary(h => h.GetProperty("id").GetInt32(), h => h.GetProperty("localized_name").GetString() ?? "");
            }
            if (File.Exists(Paths.Matchups))
            {
                var bytes = File.ReadAllBytes(Paths.Matchups);
                _vs = ParseMatchups(bytes, "vs");
                _with = ParseMatchups(bytes, "with");
            }
            _pool = ParsePool();
            MergeProfile();
            if (File.Exists(Paths.ModeStats))
                (_modeStats, TurboLabel) = ParseModeStats(File.ReadAllText(Paths.ModeStats));
        }
        catch (Exception ex) when (ex is JsonException or IOException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Write("Кэш пика не прочитан: " + ex.Message);
        }
    }

    static (Dictionary<string, Dictionary<int, (long, long)>>, string) ParseModeStats(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<string, Dictionary<int, (long, long)>>();
        long lastWeek = 0;
        foreach (var key in doc.RootElement.EnumerateObject())
        {
            var sum = new Dictionary<int, (long N, long W)>();
            foreach (var r in key.Value.EnumerateArray())
            {
                int hero = r.GetProperty("heroId").GetInt32();
                sum.TryGetValue(hero, out var acc);
                sum[hero] = (acc.N + r.GetProperty("matchCount").GetInt64(), acc.W + r.GetProperty("winCount").GetInt64());
                lastWeek = Math.Max(lastWeek, r.GetProperty("week").GetInt64());
            }
            result[key.Name] = sum.ToDictionary(kv => kv.Key, kv => ((long, long))kv.Value);
        }
        var brackets = File.Exists(Paths.TurboBrackets) ? File.ReadAllText(Paths.TurboBrackets).ToLowerInvariant() : "";
        var until = DateTimeOffset.FromUnixTimeSeconds(lastWeek).AddDays(7).ToString("dd.MM");
        return (result, $"Stratz {brackets}, до {until}");
    }

    static Dictionary<int, Dictionary<int, (double, int)>> ParseMatchups(byte[] json, string list)
    {
        using var doc = JsonDocument.Parse(json);
        var result = new Dictionary<int, Dictionary<int, (double, int)>>();
        foreach (var hero in doc.RootElement.GetProperty("data").GetProperty("heroStats").GetProperty("matchUp").EnumerateArray())
        {
            var row = new Dictionary<int, (double, int)>();
            if (!hero.TryGetProperty(list, out var items) || items.ValueKind != JsonValueKind.Array) continue;
            foreach (var v in items.EnumerateArray())
                row[v.GetProperty("heroId2").GetInt32()] = (v.GetProperty("synergy").GetDouble(), v.GetProperty("matchCount").GetInt32());
            if (row.Count > 0) result[hero.GetProperty("heroId").GetInt32()] = row;
        }
        return result;
    }

    // Годовая статистика OpenDota отстаёт, поэтому по каждому герою берём больший из двух источников.
    static Dictionary<int, (int, int)> ParsePool()
    {
        var pool = new Dictionary<int, (int Games, int Wins)>();
        if (File.Exists(Paths.Pool))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Paths.Pool));
            foreach (var h in doc.RootElement.EnumerateArray())
            {
                int games = h.GetProperty("games").GetInt32();
                if (games > 0) pool[int.Parse(h.GetProperty("hero_id").ToString())] = (games, h.GetProperty("win").GetInt32());
            }
        }
        if (File.Exists(Paths.Recent))
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Paths.Recent));
            var recent = doc.RootElement.EnumerateArray()
                .GroupBy(m => m.GetProperty("hero_id").GetInt32())
                .Select(g => (Hero: g.Key, Games: g.Count(), Wins: g.Count(m =>
                    (m.GetProperty("player_slot").GetInt32() < 128) == m.GetProperty("radiant_win").GetBoolean())));
            foreach (var (hero, games, wins) in recent)
                if (!pool.TryGetValue(hero, out var p) || p.Games < games)
                    pool[hero] = (games, wins);
        }
        return pool.ToDictionary(kv => kv.Key, kv => ((int, int))kv.Value);
    }

    static readonly string[] Medals = { "HERALD", "GUARDIAN", "CRUSADER", "ARCHON", "LEGEND", "ANCIENT", "DIVINE", "IMMORTAL" };
    static readonly string[] Pairs = { "HERALD_GUARDIAN", "CRUSADER_ARCHON", "LEGEND_ANCIENT", "DIVINE_IMMORTAL" };

    /// <summary>Медаль 1..8 из OpenDota rank_tier (24 = Guardian 4), 0 — неизвестно.</summary>
    static async Task<int> MedalAsync(string? account)
    {
        if (account == null) return 0;
        using var doc = JsonDocument.Parse(await Http.GetStringAsync($"https://api.opendota.com/api/players/{account}"));
        return doc.RootElement.TryGetProperty("rank_tier", out var rt) && rt.ValueKind == JsonValueKind.Number && rt.GetInt32() / 10 is var m and >= 1 and <= 8
            ? m
            : 0;
    }

    // Брекеты Stratz парами медалей; добавляем соседнюю пару, чтобы на краю медали хватало матчей.
    static string[] PairBrackets(int medal)
    {
        if (medal == 0) return Pairs;
        int own = (medal - 1) / 2;
        int neighbour = medal % 2 == 1 ? own - 1 : own + 1;
        return neighbour is >= 0 and < 4 ? new[] { Pairs[own], Pairs[neighbour] } : new[] { Pairs[own] };
    }

    // Отдельные медали: своя и следующая (у Immortal — предыдущая).
    static string[] MedalBrackets(int medal) =>
        medal == 0 ? Medals : new[] { Medals[medal - 1], Medals[medal < 8 ? medal : medal - 2] };

    static async Task<byte[]> StratzAsync(string query)
    {
        if (!File.Exists(Paths.StratzToken))
            throw new InvalidOperationException("нет файла stratz_token.txt");
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.stratz.com/graphql")
        {
            Content = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json"),
        };
        req.Headers.Authorization = new("Bearer", File.ReadAllText(Paths.StratzToken).Trim());
        req.Headers.UserAgent.ParseAdd("STRATZ_API");
        using var resp = await Http.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        using var doc = JsonDocument.Parse(bytes);
        if (doc.RootElement.TryGetProperty("errors", out var errors))
            throw new InvalidOperationException("Stratz: " + errors.ToString());
        return bytes;
    }

    // Портреты (heroes/) — для слотов верхней полосы; иконки (heroes/icons/) — для блока «ПОЗИЦИИ ВРАГА». Только недостающие.
    static async Task DownloadHeroImagesAsync(string dir, string kind)
    {
        if (!File.Exists(Paths.HeroList)) return;
        Directory.CreateDirectory(dir);
        using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(Paths.HeroList));
        foreach (var h in doc.RootElement.GetProperty("result").GetProperty("data").GetProperty("heroes").EnumerateArray())
        {
            var file = Path.Combine(dir, $"{h.GetProperty("id").GetInt32()}.png");
            if (File.Exists(file)) continue;
            var shortName = (h.GetProperty("name").GetString() ?? "").Replace("npc_dota_hero_", "");
            await File.WriteAllBytesAsync(file, await Http.GetByteArrayAsync(
                $"https://cdn.cloudflare.steamstatic.com/apps/dota2/images/dota_react/heroes/{kind}{shortName}.png"));
        }
    }

    public void SetProfile(ProfileData profile)
    {
        Profile = profile;
        MergeProfile();
    }

    // Состав пула — герои за год (OpenDota) и из свежей истории GCPD; победы и поражения на них — точные, из данных Valve за всё время.
    void MergeProfile()
    {
        if (Profile is not { Loaded: true } p) return;
        var byName = _heroes.ToDictionary(h => h.Value, h => h.Key, StringComparer.OrdinalIgnoreCase);
        var recent = p.Matches.Select(m => byName.GetValueOrDefault(m.Hero)).Where(id => id > 0).ToHashSet();
        foreach (var hero in _pool.Keys.Concat(recent).Distinct().ToList())
            if (p.OnHero.TryGetValue(hero, out var s) && s.Wins + s.Losses > 0)
                _pool[hero] = (s.Wins + s.Losses, s.Wins);
            else if (!_pool.ContainsKey(hero))
                _pool[hero] = (p.Matches.Count(m => byName.GetValueOrDefault(m.Hero) == hero), p.Matches.Count(m => byName.GetValueOrDefault(m.Hero) == hero && m.Win == true));
    }

    /// <summary>
    /// Ваша игра на герое, сильно сглаженная к 50% (как будто ещё 20 игр с 50%), с весом 0,3:
    /// 3 игры из 3 дают ≈ +2,0; 18 игр с 44% — ≈ −0,8. Мало игр не должно перевешивать миллионы матчей Stratz.
    /// </summary>
    public double PersonalBonus(int hero) =>
        _pool.TryGetValue(hero, out var p) ? 0.3 * (100.0 * (p.Wins + 10) / (p.Games + 20) - 50) : 0;

    static async Task Step(string what, List<string> errors, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or JsonException or InvalidOperationException)
        {
            Log.Write($"Пик: не обновилось ({what}): {ex.Message}");
            errors.Add(what);
        }
    }
}
