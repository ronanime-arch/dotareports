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

/// <summary>
/// Кандидат на пик: винрейт в режиме на вашей роли плюс поправки (п.п. винрейта) за врагов, союзников, состав команды
/// и риск контрпика (в All Pick враги видят ваш пик и могут ответить). Rare — на этой роли его почти не берут.
/// </summary>
sealed record TurboPick(int HeroId, double Winrate, double VsRevealed, double WithAllies, double Total, bool Rare,
    double Composition = 0, double CounterRisk = 0)
{
    /// <summary>Все поправки к винрейту вместе.</summary>
    public double Bonus => VsRevealed + WithAllies + Composition + CounterRisk;
}

/// <summary>
/// Прогноз по драфту: вероятность победы, сколько героев известно с каждой стороны и из чего сложилось (п.п., уже с доверием).
/// Logit — сумма драфта до доверия (для журнала прогнозов), Trust — доверие к драфту и по скольким вашим матчам оно уточнено.
/// </summary>
sealed record WinChance(double P, int KnownAllies, int KnownEnemies, IReadOnlyList<string> Parts, double Logit, double Trust, int TrustMatches);

/// <summary>
/// Данные для пика: герои (OpenDota), матчапы герой-против-героя по вашему брекету (Stratz, кэш на сутки),
/// ваш пул (OpenDota: статистика за год плюс последние 20 игр — годовая отстаёт).
/// </summary>
sealed class DraftData
{
    const int MinMatches = 100;
    // матчап сглаживается к нулю по числу матчей: 300 матчей — вдвое, 3000 — почти как есть; меньше 30 — не учитываем
    const int ShrinkMatches = 300, MinShrunkMatches = 30;
    const double PpToLogit = 0.04; // 1 п.п. винрейта около 50% ≈ 0,04 логита
    const double OwnPrior = 30;    // ваши игры на герое и против него: общий винрейт весит как 30 игр
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
    // те же матчапы только по Турбо (если Stratz их отдаёт) — в Турбо другая игра, чем в обычных режимах
    Dictionary<int, Dictionary<int, (double Syn, int N)>> _vsTurbo = new(), _withTurbo = new();
    // роли героев OpenDota: Carry, Support, Disabler, Initiator, Nuker, Durable, Escape, Pusher
    Dictionary<int, HashSet<string>> _roles = new();

    public IReadOnlyDictionary<int, string> Heroes => _heroes;
    public IReadOnlyDictionary<int, (int Games, int Wins)> Pool => _pool;
    public string Status { get; private set; } = "данные загружаются…";
    public bool Ready => _heroes.Count > 0 && _vs.Count > 0;
    public bool TurboReady => _heroes.Count > 0 && _modeStats.Count > 0;
    /// <summary>Есть матчапы, посчитанные только по Турбо.</summary>
    public bool TurboMatchups => _vsTurbo.Count > 0;
    /// <summary>Подпись к данным Турбо: брекет и до какой даты у Stratz есть статистика.</summary>
    public string TurboLabel { get; private set; } = "";
    /// <summary>Медаль по OpenDota (rank_tier / 10): 1 — Рекрут … 8 — Титан; 0 — неизвестна.</summary>
    public int Medal { get; private set; }
    public int RankTier { get; private set; }
    /// <summary>Ваши данные Valve (GCPD): точные победы на героях и счёт против героев.</summary>
    public ProfileData? Profile { get; private set; }
    (double K, int N) _trust;
    static readonly List<MatchRow> NoMatches = new();
    (DateTime File, List<MatchRow>? Matches) _trustKey = (DateTime.MinValue, null);

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
            int medal = Medal is >= 1 and <= 8 ? Medal : 0; // ранг уже прочитан шагом выше — второй запрос не нужен
            await Step("данные Stratz", errors, async () =>
            {
                var pairs = PairBrackets(medal);
                await File.WriteAllBytesAsync(Paths.Matchups, await StratzAsync(
                    $"{{ heroStats {{ matchUp(bracketBasicIds: [{string.Join(", ", pairs)}], take: 200) {{ heroId vs {{ heroId2 synergy matchCount }} with {{ heroId2 synergy matchCount }} }} }} }}"));
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
            // отдельным шагом: не отдаст Stratz матчапы по режиму — остаются матчапы по всем режимам, остальное не страдает
            await Step("матчапы Турбо", errors, async () =>
                await File.WriteAllBytesAsync(Paths.MatchupsTurbo, await StratzAsync(
                    $"{{ heroStats {{ matchUp(bracketBasicIds: [{string.Join(", ", PairBrackets(medal))}], gameModeIds: [TURBO], take: 200) {{ heroId vs {{ heroId2 synergy matchCount }} with {{ heroId2 synergy matchCount }} }} }} }}")));
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

        bool turbo = modeKey == "TURBO";
        // В All Pick враги видят ваш пик и могут ответить контрпиком: риск — средние 3 худших матчапа среди того,
        // что врагу ещё доступно и реально берётся (≥ 1% пиков), с весом по числу ещё не выбранных врагов.
        long total = overall.Values.Sum(v => v.N);
        var likely = turbo || enemies.Count >= 5 ? new List<int>()
            : overall.Where(kv => kv.Value.N >= total * 0.01 && !unavailable.Contains(kv.Key) && !enemies.Contains(kv.Key) && !allies.Contains(kv.Key))
                .Select(kv => kv.Key).ToList();
        double riskWeight = (5 - enemies.Count) / 5.0 * 0.5;
        var lacking = Lacking(allies);

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
            double vs = enemies.Sum(e => Advantage(hero, e, turbo) ?? 0);
            double with = allies.Sum(a => Synergy(a, hero, turbo) ?? 0);
            double comp = CompositionBonus(hero, allies, lacking);
            double risk = likely.Count == 0 ? 0
                : riskWeight * likely.Where(e => e != hero).Select(e => Math.Min(0, Advantage(hero, e, turbo) ?? 0)).OrderBy(x => x).Take(3).DefaultIfEmpty(0).Average();
            result.Add(new TurboPick(hero, wr, vs, with, wr - 50 + vs + with + comp + risk, rare, comp, risk));
        }
        return result.OrderBy(r => r.Rare).ThenByDescending(r => r.Total).ToList();
    }

    /// <summary>Чего не хватает команде (от двух известных союзников): контроля, инициации, уже много керри.</summary>
    public List<string> Lacking(IReadOnlyCollection<int> allies)
    {
        var notes = new List<string>();
        if (allies.Count < 2 || _roles.Count == 0) return notes;
        bool Has(int h, string role) => _roles.TryGetValue(h, out var r) && r.Contains(role);
        if (!allies.Any(a => Has(a, "Disabler"))) notes.Add("нет контроля");
        if (!allies.Any(a => Has(a, "Initiator"))) notes.Add("нет инициатора");
        if (allies.Count(a => Has(a, "Carry") && !Has(a, "Support")) >= 2) notes.Add("уже два керри");
        return notes;
    }

    // Поправка за состав (п.п.): закрывает дыру в команде — плюс, третий керри — минус. Грубо: роли OpenDota.
    double CompositionBonus(int hero, IReadOnlyCollection<int> allies, List<string> lacking)
    {
        if (lacking.Count == 0) return 0;
        bool Has(string role) => _roles.TryGetValue(hero, out var r) && r.Contains(role);
        double bonus = 0;
        if (lacking.Contains("нет контроля") && Has("Disabler")) bonus += 1.0;
        if (lacking.Contains("нет инициатора") && Has("Initiator")) bonus += 0.5;
        if (lacking.Contains("уже два керри") && Has("Carry") && !Has("Support")) bonus -= 0.7;
        return bonus;
    }

    /// <summary>
    /// Шанс победы по драфту: логистическая модель по винрейтам героев в режиме на вашем ранге, матчапам между командами,
    /// сочетаниям внутри команд и вашей игре на своём герое и против их героев. Неизвестные герои — как средние (0).
    /// Ваш герой — по вашим играм на нём: общий винрейт лишь отправная точка, как будто ещё 30 игр (OwnPrior).
    /// Только драфт: исход решает игра, поэтому прогноз держится в пределах 15–85%.
    /// </summary>
    public WinChance? WinProbability(string? mode, IReadOnlyCollection<int> allies, IReadOnlyCollection<int> enemies, int ownHero)
    {
        var modeKey = mode == "ALL_PICK" ? "ALL_PICK_RANKED" : "TURBO";
        if (!_modeStats.TryGetValue($"{modeKey}_0", out var overall)) return null;
        bool turbo = modeKey == "TURBO";
        var team = allies.Concat(ownHero > 0 ? new[] { ownHero } : Array.Empty<int>()).Distinct().ToList();
        if (team.Count + enemies.Count == 0) return null;

        // винрейт героя, сглаженный к 50% ещё 100 матчами
        double Rate(int h) => overall.TryGetValue(h, out var s) ? (s.W + 50.0) / (s.N + 100.0) : 0.5;
        double Base(int h) => Logit(Rate(h));
        double PairSum(List<int> side)
        {
            double sum = 0;
            for (int i = 0; i < side.Count; i++)
                for (int j = i + 1; j < side.Count; j++)
                {
                    var ab = Synergy(side[i], side[j], turbo);
                    var ba = Synergy(side[j], side[i], turbo);
                    sum += ab is double x && ba is double y ? (x + y) / 2 : ab ?? ba ?? 0;
                }
            return sum;
        }
        var enemyList = enemies.ToList();
        double heroes = team.Sum(Base) - enemyList.Sum(Base);
        double matchups = team.Sum(a => enemyList.Sum(e => Advantage(a, e, turbo) ?? (Advantage(e, a, turbo) is double r ? -r : 0))) * PpToLogit;
        double synergy = (PairSum(team) - PairSum(enemyList)) * PpToLogit;
        // Ваша игра — поправка к общему винрейту: на своём герое ваши игры заменяют его (было: прибавлялись сверху —
        // Wraith King 57,5% давал +7,5 п.п. при ваших 49% за 75 игр), против их героев — сверх того,
        // чего ждать по их общему винрейту (Dota Plus, GCPD), с половинным весом.
        double personal = 0;
        if (ownHero > 0 && _pool.TryGetValue(ownHero, out var own) && own.Games > 0)
            personal += Logit((own.Wins + OwnPrior * Rate(ownHero)) / (own.Games + OwnPrior)) - Base(ownHero);
        if (Profile is { Loaded: true } prof)
            foreach (var e in enemyList)
                if (prof.AgainstHero.TryGetValue(e, out var r) && r.Wins + r.Losses > 0)
                {
                    double expect = 1 - Rate(e);
                    personal += 0.5 * (Logit((r.Wins + OwnPrior * expect) / (r.Wins + r.Losses + OwnPrior)) - Logit(expect));
                }
        double logit = heroes + matchups + synergy + personal;
        var (trust, trustN) = Trust();
        double p = Math.Clamp(1 / (1 + Math.Exp(-trust * logit)), 0.15, 0.85);
        // доли в п.п. (около 50% один логит ≈ 25 п.п.) — с доверием, чтобы складывались в показанный шанс
        var parts = new List<string>();
        void Part(string name, double l) { if (Math.Abs(l * trust * 25) >= 0.1) parts.Add($"{name} {l * trust * 25:+0.0;-0.0}"); }
        Part("герои", heroes);
        Part("матчапы", matchups);
        Part("сочетания", synergy);
        Part("ваша игра", personal);
        return new WinChance(p, team.Count, enemyList.Count, parts, logit, trust, trustN);
    }

    // Доверие к драфту — по журналу прогнозов и истории матчей; пересчёт, только когда что-то из них обновилось.
    (double K, int N) Trust()
    {
        var matches = Profile is { Loaded: true } prof ? prof.Matches : NoMatches;
        var file = File.Exists(Paths.Predictions) ? File.GetLastWriteTimeUtc(Paths.Predictions) : DateTime.MinValue;
        if (_trustKey.Matches == null || _trustKey.File != file || !ReferenceEquals(_trustKey.Matches, matches))
        {
            _trust = PredictionLog.Trust(matches);
            _trustKey = (file, matches);
        }
        return _trust;
    }

    static double Logit(double p) => Math.Log(p / (1 - p));

    /// <summary>Винрейт героя в режиме на вашем ранге за 4 недели (Stratz) и число матчей; null — нет данных.</summary>
    public (double Winrate, long Matches)? ModeWinrate(int hero, string? mode)
    {
        var key = (mode == "ALL_PICK" ? "ALL_PICK_RANKED" : "TURBO") + "_0";
        return _modeStats.TryGetValue(key, out var overall) && overall.TryGetValue(hero, out var s) && s.N > 0
            ? (100.0 * s.W / s.N, s.N) : null;
    }

    /// <summary>Преимущество героя над врагом, п.п. винрейта (сглаженное); null — мало матчей.</summary>
    public double? MatchupAdvantage(int hero, int enemy, string? mode) =>
        Advantage(hero, enemy, mode != "ALL_PICK") ?? (Advantage(enemy, hero, mode != "ALL_PICK") is double r ? -r : (double?)null);

    /// <summary>Сочетание двух героев в одной команде, п.п. винрейта (сглаженное); null — мало матчей.</summary>
    public double? PairSynergy(int a, int b, string? mode) =>
        Synergy(a, b, mode != "ALL_PICK") ?? Synergy(b, a, mode != "ALL_PICK");

    /// <summary>
    /// Тренды меты по неделям Stratz (ваш ранг): последняя неделя против трёх предыдущих, герои с заметным числом матчей.
    /// Растут — первыми, падают — последними; (герой, винрейт сейчас, до того, матчей за неделю).
    /// </summary>
    public List<(int Hero, double Now, double Before, long N)> MetaTrends(string mode)
    {
        var result = new List<(int, double, double, long)>();
        try
        {
            if (!File.Exists(Paths.ModeStats)) return result;
            using var doc = JsonDocument.Parse(File.ReadAllText(Paths.ModeStats));
            var key = (mode == "ALL_PICK" ? "ALL_PICK_RANKED" : "TURBO") + "_0";
            if (!doc.RootElement.TryGetProperty(key, out var rows)) return result;
            var weeks = rows.EnumerateArray().Select(r => (Hero: r.GetProperty("heroId").GetInt32(), Week: r.GetProperty("week").GetInt64(),
                N: r.GetProperty("matchCount").GetInt64(), W: r.GetProperty("winCount").GetInt64())).ToList();
            if (weeks.Count == 0) return result;
            long last = weeks.Max(r => r.Week);
            long minN = (long)(weeks.Where(r => r.Week == last).Sum(r => r.N) * 0.004);
            foreach (var g in weeks.GroupBy(r => r.Hero))
            {
                var now = g.Where(r => r.Week == last).ToList();
                var before = g.Where(r => r.Week < last).ToList();
                long nNow = now.Sum(r => r.N), nBefore = before.Sum(r => r.N);
                if (nNow < Math.Max(minN, 200) || nBefore < 200 || !_heroes.ContainsKey(g.Key)) continue;
                result.Add((g.Key, 100.0 * now.Sum(r => r.W) / nNow, 100.0 * before.Sum(r => r.W) / nBefore, nNow));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Write("Тренды меты не прочитаны: " + ex.Message);
        }
        return result.OrderByDescending(r => r.Item2 - r.Item3).ToList();
    }

    // В Турбо общий винрейт героя перевешивал драфт: сильный на ранге герой — сразу +5 п.п., поправки за врагов и союзников — ±1–2.
    // В выборе «кого брать» он там идёт с половинным весом; в All Pick — как есть.
    const double TurboBaseWeight = 0.5;

    /// <summary>
    /// Кого брать — чем больше, тем лучше: винрейт героя в режиме (в Турбо и пока режим не ясен — с половинным весом),
    /// поправки за драфт и ваша игра на нём.
    /// </summary>
    public double PickScore(TurboPick p, string? mode) =>
        (mode == "ALL_PICK" ? 1 : TurboBaseWeight) * (p.Winrate - 50) + p.Bonus + PersonalBonus(p.HeroId, mode);

    const long MinModeMatches = 2000, MinRoleMatches = 300;
    const double MinRoleShare = 0.008; // Skywrath на миде ≈ 1% пиков — должен считаться мидером

    // Матчапы — со сглаживанием к нулю по числу матчей; в Турбо — из матчапов Турбо, если они есть для этой пары.
    double? Synergy(int ally, int candidate, bool turbo = false) =>
        Shrunk(turbo ? _withTurbo : null, _with, ally, candidate);

    double? Advantage(int candidate, int enemy, bool turbo = false) =>
        Shrunk(turbo ? _vsTurbo : null, _vs, enemy, candidate) is double syn ? -syn : null;

    static double? Shrunk(Dictionary<int, Dictionary<int, (double Syn, int N)>>? first, Dictionary<int, Dictionary<int, (double Syn, int N)>> fallback, int a, int b)
    {
        foreach (var table in new[] { first, fallback })
            if (table != null && table.TryGetValue(a, out var row) && row.TryGetValue(b, out var m) && m.N >= MinShrunkMatches)
                return m.Syn * m.N / (m.N + ShrinkMatches);
        return null;
    }

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
                _roles = doc.RootElement.EnumerateArray().ToDictionary(h => h.GetProperty("id").GetInt32(),
                    h => h.TryGetProperty("roles", out var r) && r.ValueKind == JsonValueKind.Array
                        ? r.EnumerateArray().Select(x => x.GetString() ?? "").ToHashSet() : new HashSet<string>());
            }
            if (File.Exists(Paths.Matchups))
            {
                var bytes = File.ReadAllBytes(Paths.Matchups);
                _vs = ParseMatchups(bytes, "vs");
                _with = ParseMatchups(bytes, "with");
            }
            if (File.Exists(Paths.MatchupsTurbo))
            {
                var bytes = File.ReadAllBytes(Paths.MatchupsTurbo);
                _vsTurbo = ParseMatchups(bytes, "vs");
                _withTurbo = ParseMatchups(bytes, "with");
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

    /// <summary>
    /// Герои всех десяти игроков матча по порядку слотов верхней полосы (0–4 Силы Света, 5–9 Силы Тьмы).
    /// Сначала Stratz, нет там — OpenDota; null — матча ещё нет нигде (появляется через несколько минут после конца).
    /// </summary>
    public static async Task<int[]?> MatchHeroesAsync(long matchId)
    {
        var players = new List<(bool Radiant, int Slot, int Hero)>();
        try
        {
            using var doc = JsonDocument.Parse(await StratzAsync($"{{ match(id: {matchId}) {{ players {{ heroId isRadiant playerSlot }} }} }}"));
            foreach (var p in doc.RootElement.GetProperty("data").GetProperty("match").GetProperty("players").EnumerateArray())
                players.Add((p.GetProperty("isRadiant").GetBoolean(), p.GetProperty("playerSlot").GetInt32(), p.GetProperty("heroId").GetInt32()));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            Log.Write($"Матч {matchId}: Stratz не отдал героев: {ex.Message}");
        }
        if (!Complete(players))
        {
            players.Clear();
            try
            {
                using var resp = await Http.GetAsync($"https://api.opendota.com/api/matches/{matchId}");
                if (resp.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                    foreach (var p in doc.RootElement.GetProperty("players").EnumerateArray())
                    {
                        int slot = p.GetProperty("player_slot").GetInt32(); // 0–4 и 128–132
                        players.Add((slot < 128, slot, p.GetProperty("hero_id").GetInt32()));
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
            {
                Log.Write($"Матч {matchId}: OpenDota не отдал героев: {ex.Message}");
            }
        }
        return Complete(players) ? players.OrderBy(p => !p.Radiant).ThenBy(p => p.Slot).Select(p => p.Hero).ToArray() : null;

        static bool Complete(List<(bool Radiant, int Slot, int Hero)> list) =>
            list.Count == 10 && list.Count(p => p.Radiant) == 5 && list.All(p => p.Hero > 0);
    }

    // Stratz ограничивает частоту: на 429 и сбои сервера — ещё две попытки с паузой (или сколько попросит Retry-After)
    static async Task<byte[]> StratzAsync(string query)
    {
        if (!File.Exists(Paths.StratzToken))
            throw new InvalidOperationException("нет файла stratz_token.txt");
        var token = File.ReadAllText(Paths.StratzToken).Trim();
        for (int attempt = 1; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.stratz.com/graphql")
            {
                Content = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new("Bearer", token);
            req.Headers.UserAgent.ParseAdd("STRATZ_API");
            using var resp = await Http.SendAsync(req);
            int code = (int)resp.StatusCode;
            if ((code == 429 || code >= 500) && attempt < 3)
            {
                var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(attempt * 5);
                await Task.Delay(wait < TimeSpan.FromSeconds(60) ? wait : TimeSpan.FromSeconds(60));
                continue;
            }
            if (!resp.IsSuccessStatusCode)
            {
                // в ответе Stratz пишет причину (например, неверный запрос) — без неё в log.txt виден только код
                var text = (await resp.Content.ReadAsStringAsync()).Replace('\n', ' ').Trim();
                throw new HttpRequestException($"Stratz: {code} {resp.ReasonPhrase}" + (text.Length > 0 ? ": " + (text.Length > 300 ? text[..300] + "…" : text) : ""));
            }
            return CheckErrors(await resp.Content.ReadAsByteArrayAsync());
        }
    }

    static byte[] CheckErrors(byte[] bytes)
    {
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
    /// <param name="mode">Режим матча: плюс свежая форма на герое в этом режиме по истории GCPD (100 последних матчей).</param>
    public double PersonalBonus(int hero, string? mode = null)
    {
        double bonus = _pool.TryGetValue(hero, out var p) ? 0.3 * (100.0 * (p.Wins + 10) / (p.Games + 20) - 50) : 0;
        if (mode != null && Profile is { Loaded: true } prof)
        {
            int gameMode = mode == "ALL_PICK" ? ProfileData.RankedAllPick : ProfileData.Turbo;
            var recent = prof.Matches.Where(m => m.HeroId == hero && m.GameMode == gameMode && m.Win != null).ToList();
            if (recent.Count > 0)
                bonus += 0.15 * (100.0 * (recent.Count(m => m.Win == true) + 3) / (recent.Count + 6) - 50);
        }
        return bonus;
    }

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
