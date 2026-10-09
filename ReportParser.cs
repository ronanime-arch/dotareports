using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace DotaReports;

/// <summary>
/// Одна строка «Incoming Match Player Report» — сводка поведения за 15 матчей.
/// Valve не пишет туда, кто пожаловался: только сколько жалоб, от скольких игроков и сколько из них за общение.
/// </summary>
sealed record ConductSummary(
    string MatchId,
    DateTime? When,
    int Matches,
    int ReportedMatches,
    int Reports,
    int ReportingParties,
    int CommsReports,
    int Commends,
    int? Score,
    int? ScoreDelta);

static class ReportParser
{
    public static List<ConductSummary> Parse(JsonElement table)
    {
        var headers = table.GetProperty("headers").EnumerateArray().Select(h => (h.GetString() ?? "").Trim()).ToList();
        int Col(string name) => headers.FindIndex(h => h.Equals(name, StringComparison.OrdinalIgnoreCase));
        int Required(string name) => Col(name) is var i and >= 0
            ? i
            : throw new InvalidOperationException($"В таблице GCPD нет колонки «{name}» — Valve её поменяла. Таблица записана в {Paths.Dump}");

        int match = Required("Conduct Summary MatchID");
        int reports = Required("Reports");
        int date = Col("Conduct Summary Date");
        int matches = Col("Match Count");
        int reportedMatches = Col("Reported Matches");
        int parties = Col("Reporting Parties");
        int comms = Col("Comms Reports");
        int commends = Col("Commends");
        int score = Col("Behavior Score");

        var rows = table.GetProperty("rows").EnumerateArray()
            .Select(r => r.EnumerateArray().Select(c => c.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "").ToList())
            .Where(r => r.Count > match && r[match].Length > 0)
            .ToList();

        string At(List<string> r, int i) => i >= 0 && i < r.Count ? r[i] : "";
        int Int(List<string> r, int i) => int.TryParse(At(r, i), out var v) ? v : 0;
        int? IntOrNull(List<string> r, int i) => int.TryParse(At(r, i), out var v) ? v : null;

        var result = rows.Select(r => new ConductSummary(
            At(r, match),
            ParseDate(At(r, date)),
            Int(r, matches),
            Int(r, reportedMatches),
            Int(r, reports),
            Int(r, parties),
            Int(r, comms),
            Int(r, commends),
            IntOrNull(r, score),
            null)).ToList();

        // Таблица идёт от новых к старым: изменение рейтинга — разница с предыдущей сводкой.
        for (int i = 0; i + 1 < result.Count; i++)
            if (result[i].Score is int now && result[i + 1].Score is int before)
                result[i] = result[i] with { ScoreDelta = now - before };
        return result;
    }

    // Пример: «2026-10-06 09:44:20 GMT»
    static DateTime? ParseDate(string s) =>
        DateTime.TryParseExact(s, "yyyy-MM-dd HH:mm:ss 'GMT'", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc)
            ? utc.ToLocalTime()
            : null;
}
