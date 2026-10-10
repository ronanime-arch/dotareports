using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DotaReports;

/// <summary>
/// Все сводки поведения с момента установки (GCPD показывает лишь последние) и матчи окна каждой.
/// История матчей GCPD — 100 последних, старые уходят, поэтому окно запоминается, как только его удалось собрать.
/// </summary>
sealed class ConductHistory
{
    /// <summary>Новые первыми, по номеру матча.</summary>
    public List<ConductSummary> Summaries { get; set; } = new();

    /// <summary>Матчи окна по MatchId сводки, новые первыми.</summary>
    public Dictionary<string, List<MatchRow>> Windows { get; set; } = new();

    /// <summary>Добавить сводки со страницы GCPD. false — ничего не поменялось: файл не переписываем каждую минуту.</summary>
    public bool Merge(IEnumerable<ConductSummary> summaries)
    {
        var before = Summaries.ToList();
        var byId = new Dictionary<string, ConductSummary>();
        foreach (var s in Summaries) byId[s.MatchId] = s;
        foreach (var s in summaries) byId[s.MatchId] = s with { ScoreDelta = null };
        Summaries = byId.Values.OrderByDescending(s => Id(s.MatchId)).ToList(); // номер не разобрался (0) — в конец
        // изменение балла — разница с предыдущей сводкой всей истории, а не только страницы GCPD
        for (int i = 0; i < Summaries.Count; i++)
        {
            int? delta = i + 1 < Summaries.Count && Summaries[i].Score is int now && Summaries[i + 1].Score is int prev ? now - prev : null;
            Summaries[i] = Summaries[i] with { ScoreDelta = delta };
        }
        return !before.SequenceEqual(Summaries);
    }

    /// <summary>
    /// Окна из истории матчей: матчи после предыдущей (более старой) сводки и до этой включительно, не больше Matches;
    /// у самой старой — последние Matches штук. Окно заменяется, только если стало длиннее. false — ни одно не выросло.
    /// </summary>
    public bool AttachMatches(IReadOnlyList<MatchRow> matches)
    {
        var known = matches.Where(m => m.MatchId > 0).OrderByDescending(m => m.MatchId).ToList();
        if (known.Count == 0) return false;
        bool changed = false;
        for (int i = 0; i < Summaries.Count; i++)
        {
            var s = Summaries[i];
            ulong id = Id(s.MatchId);
            if (id == 0 || s.Matches <= 0) continue;
            ulong after = i + 1 < Summaries.Count ? Id(Summaries[i + 1].MatchId) : 0;
            var window = known.Where(m => m.MatchId <= id && m.MatchId > after).Take(s.Matches).ToList();
            if (window.Count > Window(s).Count)
            {
                Windows[s.MatchId] = window;
                changed = true;
            }
        }
        return changed;
    }

    public List<MatchRow> Window(ConductSummary s) => Windows.TryGetValue(s.MatchId, out var w) ? w : new();

    /// <summary>
    /// Окно собрано точно: ровно Matches матчей, и последний из них — матч сводки. Valve не пишет, что MatchID сводки —
    /// последний матч её окна; при сдвиге на один матч число бы совпало, а этот якорь — нет.
    /// </summary>
    public bool Exact(ConductSummary s) => Window(s) is { Count: > 0 } w && w.Count == s.Matches && w[0].MatchId == Id(s.MatchId);

    static ulong Id(string matchId) => ulong.TryParse(matchId, out var id) ? id : 0;

    public static ConductHistory Load()
    {
        try
        {
            if (File.Exists(Paths.ConductHistory))
                return JsonSerializer.Deserialize<ConductHistory>(File.ReadAllText(Paths.ConductHistory)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or NotSupportedException)
        {
            Log.Write("История поведения не прочитана, начинаю заново: " + ex.Message);
        }
        return new();
    }

    public void Save() => File.WriteAllText(Paths.ConductHistory, JsonSerializer.Serialize(this));
}
