using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DotaReports;

/// <summary>Ваша игра в IM Пробуждение по истории NEC: номер матча, начало, герой, победа, K/D/A.</summary>
sealed record AwakenMatch(long Id, DateTime Start, int Hero, bool Win, int Kills, int Deaths, int Assists);

/// <summary>Накопленная история: игры и сколько их всего у NEC (Total — с его последней страницы, от TotalAt, мс).</summary>
sealed record AwakenStore(int Total, long TotalAt, List<AwakenMatch> Matches);

/// <summary>
/// История ваших игр в кастомке IM Пробуждение, которую ведёт сам NEC: исход известен у каждой игры (по GSI кастомка его
/// почти не присылает). На диск (Cache\awaken-records.json) NEC кладёт только открытые в «Записях» страницы — по 15 игр,
/// хранит их 7 дней и обновляет, лишь когда их открывают снова. Поэтому всё, что там появилось, копим у себя
/// (nec_awaken.json, по номеру матча). Чужую историю Пробуждения NEC не показывает — все страницы ваши.
/// </summary>
static class AwakenHistory
{
    static DateTime _stamp;

    static string Source => Path.Combine(NecLobby.Root, "Cache", "awaken-records.json");

    public static AwakenStore Load()
    {
        try
        {
            if (File.Exists(Paths.AwakenHistory) && JsonSerializer.Deserialize<AwakenStore>(File.ReadAllText(Paths.AwakenHistory)) is { Matches: not null } store)
                return store;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            Log.Write("nec_awaken.json не прочитан: " + ex.Message);
        }
        return new AwakenStore(0, 0, new());
    }

    /// <summary>NEC переписал свой файл — дописать новые игры к накопленным. Зовётся из опроса NEC раз в несколько секунд.</summary>
    public static void Sync()
    {
        var stamp = File.GetLastWriteTimeUtc(Source);
        if (stamp == _stamp) return;
        _stamp = stamp;
        var store = Load();
        var ids = store.Matches.Select(m => m.Id).ToHashSet();
        var (total, totalAt) = (store.Total, store.TotalAt);
        int added = 0;
        foreach (var (_, at, data) in NecLobby.Entries(Source))
        {
            // в том же файле и рейтинг, и разборы матчей — нужны страницы «Записей»
            if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("matchData", out var page) || page.ValueKind != JsonValueKind.Object
                || !page.TryGetProperty("PlayerData", out var list) || list.ValueKind != JsonValueKind.Array) continue;
            if (at > totalAt && NecLobby.Int(page, "Sums") is int sums) (total, totalAt) = (sums, at);
            foreach (var m in list.EnumerateArray())
                if (Parse(m) is { } match && ids.Add(match.Id))
                {
                    store.Matches.Add(match);
                    added++;
                }
        }
        if (added == 0 && totalAt == store.TotalAt) return;
        try
        {
            File.WriteAllText(Paths.AwakenHistory, JsonSerializer.Serialize(store with { Total = total, TotalAt = totalAt, Matches = store.Matches.OrderBy(m => m.Start).ToList() }));
            Log.Write($"NEC: история Пробуждения +{added}, у нас {store.Matches.Count} из {total}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("nec_awaken.json не записан: " + ex.Message);
        }
    }

    // {match_id, hid, win 0/1, add_times (unix, ≈ начало игры), kills, deaths, assists}
    static AwakenMatch? Parse(JsonElement m) =>
        NecLobby.Int64(m, "match_id") is long id && NecLobby.Int(m, "hid") is int hero and > 0 && NecLobby.Int(m, "win") is int win and (0 or 1)
        && NecLobby.Int64(m, "add_times") is long start and > 0 and < 4102444800 // до 2100 года: мусор не роняет опрос
            ? new AwakenMatch(id, DateTimeOffset.FromUnixTimeSeconds(start).LocalDateTime, hero, win == 1,
                NecLobby.Int(m, "kills") ?? 0, NecLobby.Int(m, "deaths") ?? 0, NecLobby.Int(m, "assists") ?? 0)
            : null;
}
