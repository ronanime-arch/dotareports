using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DotaReports;

/// <summary>Прогноз по драфту на начало матча — чтобы потом сверить с исходом из истории матчей GCPD.</summary>
sealed record Prediction(string MatchId, string? Mode, double P, int KnownAllies, int KnownEnemies, DateTime At);

/// <summary>Журнал прогнозов в predictions.json (последние 300) и сколько из них угадано.</summary>
static class PredictionLog
{
    const int Keep = 300;

    public static List<Prediction> Load()
    {
        try
        {
            if (File.Exists(Paths.Predictions))
                return JsonSerializer.Deserialize<List<Prediction>>(File.ReadAllText(Paths.Predictions)) ?? new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Log.Write("predictions.json не прочитан: " + ex.Message);
        }
        return new();
    }

    public static void Add(Prediction p)
    {
        var all = Load();
        all.RemoveAll(x => x.MatchId == p.MatchId);
        all.Add(p);
        try
        {
            File.WriteAllText(Paths.Predictions, JsonSerializer.Serialize(all.TakeLast(Keep).ToList()));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("predictions.json не записан: " + ex.Message);
        }
    }

    /// <summary>Сколько прогнозов угадали исход (≥ 50% — победа) среди матчей, исход которых уже есть в истории GCPD.</summary>
    public static (int Hits, int Total) Accuracy(IEnumerable<MatchRow> matches)
    {
        var results = matches.Where(m => m.MatchId > 0 && m.Win != null).GroupBy(m => m.MatchId).ToDictionary(g => g.Key, g => g.First().Win == true);
        int hits = 0, total = 0;
        foreach (var p in Load())
            if (ulong.TryParse(p.MatchId, out var id) && results.TryGetValue(id, out var win))
            {
                total++;
                if (win == (p.P >= 0.5)) hits++;
            }
        return (hits, total);
    }
}
