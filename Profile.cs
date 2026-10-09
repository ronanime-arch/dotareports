using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DotaReports;

/// <summary>Один матч из истории GCPD (Matches → PlayerMatchHistory).</summary>
sealed record MatchRow(DateTime StartLocal, int GameMode, string Hero, bool? Win, bool Uncalibrated);

/// <summary>
/// Ваши данные Valve из GCPD для окна рейтинга и карточки пика: история матчей (калибровка, сегодня, серия),
/// Game Account Client (всего побед/поражений, сколько жалоб и похвал ещё можно отправить),
/// Hero Standings (победы/поражения на каждом герое за всё время) и Hero Standings Plus (счёт с героем и против него).
/// Сырые таблицы кэшируются в profile_tables.json.
/// </summary>
sealed class ProfileData
{
    public const int RankedAllPick = 22, Turbo = 23;

    public static readonly (string Category, string Tab, int ExtraPages)[] Tabs =
    {
        ("Account", "GameAccountClient", 0),
        ("Matches", "PlayerMatchHistory", 4),   // 5 страниц по 20 матчей: калибровка тянется на несколько дней
        ("Stats", "GameHeroStandings", 0),
        ("Stats", "GameHeroStandingsPlus", 0),
    };

    public List<MatchRow> Matches { get; private set; } = new();
    public Dictionary<int, (int Wins, int Losses)> OnHero { get; private set; } = new();
    public Dictionary<int, (int Wins, int Losses)> AgainstHero { get; private set; } = new();
    public Dictionary<string, string> Account { get; private set; } = new();
    public DateTime? UpdatedAt { get; private set; }

    public bool Loaded => UpdatedAt != null;

    public void LoadCache(IReadOnlyDictionary<int, string> heroes)
    {
        try
        {
            if (!File.Exists(Paths.ProfileTables)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(Paths.ProfileTables));
            var tables = doc.RootElement.GetProperty("tables").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            Apply(tables, heroes, doc.RootElement.GetProperty("updated").GetDateTime());
        }
        catch (Exception ex) when (ex is JsonException or IOException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Write("Кэш профиля не прочитан: " + ex.Message);
        }
    }

    public void Save(Dictionary<string, JsonElement> tables)
    {
        File.WriteAllText(Paths.ProfileTables, JsonSerializer.Serialize(new { updated = DateTime.Now, tables }));
    }

    public void Apply(Dictionary<string, JsonElement> tables, IReadOnlyDictionary<int, string> heroes, DateTime updated)
    {
        var byName = heroes.ToDictionary(h => h.Value, h => h.Key, StringComparer.OrdinalIgnoreCase);
        // имя героя в GCPD английское; у новых героев вместо имени номер байтом со знаком (−118 → 138)
        int HeroId(string s) => byName.TryGetValue(s, out var id) ? id : int.TryParse(s, out var n) && n < 0 ? 256 + n : 0;

        if (tables.TryGetValue("Account:GameAccountClient", out var acc) && Rows(acc).FirstOrDefault() is { } accRow)
            Account = accRow;

        if (tables.TryGetValue("Matches:PlayerMatchHistory", out var mh))
            Matches = Rows(mh).Select(r =>
            {
                int slot = int.TryParse(r.GetValueOrDefault("PlayerSlot"), out var sl) ? sl : 0;
                int outcome = int.TryParse(r.GetValueOrDefault("MatchOutcome"), out var o) ? o : 0;
                bool radiant = slot >= 0 && slot < 128;   // у Dire слот 128+ (в таблице — байт со знаком: −126)
                bool? win = outcome switch { 2 => radiant, 3 => !radiant, _ => null };
                return new MatchRow(ParseGmt(r.GetValueOrDefault("StartTime")), int.TryParse(r.GetValueOrDefault("GameMode"), out var gm) ? gm : 0,
                    r.GetValueOrDefault("Hero") ?? "", win, r.GetValueOrDefault("Uncalibrated") == "Yes");
            }).OrderByDescending(m => m.StartLocal).ToList();

        if (tables.TryGetValue("Stats:GameHeroStandings", out var st))
            OnHero = Rows(st).Select(r => (Id: HeroId(r.GetValueOrDefault("Hero") ?? ""), W: Int(r, "Wins"), L: Int(r, "Losses")))
                .Where(x => x.Id > 0).GroupBy(x => x.Id).ToDictionary(g => g.Key, g => (g.Sum(x => x.W), g.Sum(x => x.L)));

        if (tables.TryGetValue("Stats:GameHeroStandingsPlus", out var sp))
            AgainstHero = Rows(sp).Select(r => (Id: HeroId(r.GetValueOrDefault("Hero") ?? ""), W: Int(r, "WinsAgainst"), L: Int(r, "LossesAgainst")))
                .Where(x => x.Id > 0).GroupBy(x => x.Id).ToDictionary(g => g.Key, g => (g.Sum(x => x.W), g.Sum(x => x.L)));

        UpdatedAt = updated;
    }

    /// <summary>
    /// Калибровка: подряд идущие с конца рейтинговые матчи с пометкой Uncalibrated (null — не калибруетесь).
    /// AtLeast — все прочитанные рейтинговые матчи калибровочные, начало калибровки ещё раньше.
    /// </summary>
    public (int Games, int Wins, bool AtLeast)? Calibration()
    {
        var ranked = Matches.Where(m => m.GameMode == RankedAllPick).ToList();
        if (ranked.Count == 0 || !ranked[0].Uncalibrated) return null;
        var cal = ranked.TakeWhile(m => m.Uncalibrated).ToList();
        return (cal.Count, cal.Count(m => m.Win == true), cal.Count == ranked.Count);
    }

    public (int Games, int Wins, int RankedGames, int RankedWins) Today()
    {
        var today = Matches.Where(m => m.StartLocal.Date == DateTime.Today && m.Win != null).ToList();
        var ranked = today.Where(m => m.GameMode == RankedAllPick).ToList();
        return (today.Count, today.Count(m => m.Win == true), ranked.Count, ranked.Count(m => m.Win == true));
    }

    /// <summary>Серия последних одинаковых результатов: (победы?, сколько подряд).</summary>
    public (bool Wins, int Count)? Streak()
    {
        var done = Matches.Where(m => m.Win != null).ToList();
        if (done.Count == 0) return null;
        bool w = done[0].Win == true;
        return (w, done.TakeWhile(m => (m.Win == true) == w).Count());
    }

    public int AccountInt(string key) => int.TryParse(Account.GetValueOrDefault(key), out var v) ? v : 0;

    static int Int(Dictionary<string, string> r, string key) => int.TryParse(r.GetValueOrDefault(key), out var v) ? v : 0;

    static DateTime ParseGmt(string? s) =>
        DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss 'GMT'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc) ? utc.ToLocalTime() : DateTime.MinValue;

    static IEnumerable<Dictionary<string, string>> Rows(JsonElement table)
    {
        var headers = table.GetProperty("headers").EnumerateArray().Select(h => h.GetString() ?? "").ToList();
        foreach (var r in table.GetProperty("rows").EnumerateArray())
        {
            var cells = r.EnumerateArray().Select(c => c.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "").ToList();
            var row = new Dictionary<string, string>();
            for (int i = 0; i < headers.Count && i < cells.Count; i++) row[headers[i]] = cells[i];
            yield return row;
        }
    }
}
