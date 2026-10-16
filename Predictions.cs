using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DotaReports;

/// <summary>
/// Прогноз по драфту на начало матча — чтобы потом сверить с исходом из истории матчей GCPD.
/// Logit — сумма драфта до поправки на доверие (в старых записях её нет: тогда доверие было 1, сумма — логит P).
/// </summary>
sealed record Prediction(string MatchId, string? Mode, double P, int KnownAllies, int KnownEnemies, DateTime At, double? Logit = null);

/// <summary>Журнал прогнозов в predictions.json (последние 300) и сколько из них угадано.</summary>
static class PredictionLog
{
    const int Keep = 300;
    // Доверие к драфту: p = σ(k · сумма). Сумма винрейтов, матчапов и сочетаний перехваливает драфт
    // (три поражения подряд при 64–71%), поэтому начинаем с половины. По вашим матчам k уточняется,
    // но медленно: исход решает игра, и сотня матчей сдвигает k примерно на 0,1.
    const double TrustPrior = 0.5, TrustPriorSd = 0.25;

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

    /// <summary>Доверие к драфту k по вашим прогнозам и исходам (максимум правдоподобия с априорным 0,5) и по скольким матчам.</summary>
    public static (double K, int N) Trust(IEnumerable<MatchRow> matches)
    {
        var results = Results(matches);
        var data = new List<(double L, bool Win)>();
        foreach (var p in Load())
            if (ulong.TryParse(p.MatchId, out var id) && results.TryGetValue(id, out var win))
                data.Add((p.Logit ?? Math.Log(p.P / (1 - p.P)), win));
        double best = TrustPrior, bestScore = double.MinValue;
        for (double k = 0; k <= 1.2001; k += 0.01)
        {
            double score = -(k - TrustPrior) * (k - TrustPrior) / (2 * TrustPriorSd * TrustPriorSd);
            foreach (var (l, win) in data)
                score -= Math.Log(1 + Math.Exp(win ? -k * l : k * l)); // log σ(±k·l)
            if (score > bestScore) (best, bestScore) = (k, score);
        }
        return (Math.Round(best, 2), data.Count);
    }

    static Dictionary<ulong, bool> Results(IEnumerable<MatchRow> matches) =>
        matches.Where(m => m.MatchId > 0 && m.Win != null).GroupBy(m => m.MatchId).ToDictionary(g => g.Key, g => g.First().Win == true);

    /// <summary>Сколько прогнозов угадали исход (≥ 50% — победа) среди матчей, исход которых уже есть в истории GCPD.</summary>
    public static (int Hits, int Total) Accuracy(IEnumerable<MatchRow> matches)
    {
        var results = Results(matches);
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
