using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace DotaReports;

/// <summary>
/// Что важно из сообщения GSI: фаза игры, ваша сторона, ваш герой (0 — ещё не выбран), матч, часы матча в секундах
/// (до горна — меньше нуля); зритель (повтор, DotaTV — у player вместо полей игрока команды team2/team3),
/// кастомная игра (map.customgamename), пассивный доход с начала матча (player.gold_from_income),
/// время игры (map.game_time) и когда убивали Рошана (events: roshan_killed, по времени игры).
/// </summary>
sealed record GsiState(string GameState, string? Team, int HeroId, string? MatchId, int TeamSlot, int? ClockTime = null,
    bool Spectating = false, string? CustomGame = null, int? GoldFromIncome = null, int? GameTime = null, IReadOnlyList<int>? RoshanKills = null)
{
    /// <summary>Вы играете сами: есть герой и сторона, не смотрите чужую игру и не в кастомке.</summary>
    public bool OwnGame => HeroId > 0 && !Spectating && string.IsNullOrEmpty(CustomGame) &&
        (string.Equals(Team, "radiant", StringComparison.OrdinalIgnoreCase) || string.Equals(Team, "dire", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Приём Game State Integration: Dota сама шлёт POST с JSON на 127.0.0.1:38461
/// (конфиг gamestate_integration_dotareports.cfg). Пока только записывает образцы,
/// чтобы увидеть, что игра отдаёт во время пика в Турбо.
/// </summary>
sealed class GsiListener
{
    public const int Port = 38461;
    const int MaxSamples = 400;
    const int KeepSamples = 300; // за все запуски: старые образцы удаляем, иначе папка растёт без конца
    const int MaxPerLateState = 5;
    const string InProgress = "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS";
    static readonly HashSet<string> SkipStates = new() { "DOTA_GAMERULES_STATE_POST_GAME" };
    static readonly HashSet<string> LateStates = new() { "DOTA_GAMERULES_STATE_PRE_GAME", "" };
    // из идущего матча — по образцу на отметках часов (сверить поля дохода и режима), не каждое сообщение
    static readonly int[] InGameMarks = { 0, 30, 60, 120, 300, 600 };

    readonly TcpListener _listener = new(IPAddress.Loopback, Port);
    readonly object _lock = new();
    readonly Dictionary<string, int> _perState = new();
    readonly HashSet<string> _inGameSaved = new();
    string? _lastState;
    string? _lastBody;
    int _saved;

    /// <summary>Каждое сообщение от Dota; вызывается из фонового потока.</summary>
    public event Action<GsiState>? Updated;

    public void Start()
    {
        PruneSamples();
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    // Имена образцов начинаются с даты — по имени и сортируем, новые в конце.
    static void PruneSamples()
    {
        var dir = Path.Combine(Paths.Root, "gsi_samples");
        if (!Directory.Exists(dir)) return;
        try
        {
            var files = Directory.GetFiles(dir, "*.json").OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).ToList();
            int extra = files.Count - KeepSamples;
            if (extra <= 0) return;
            foreach (var f in files.Take(extra)) File.Delete(f);
            Log.Write($"GSI: удалено старых образцов: {extra}, осталось {KeepSamples}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("GSI: старые образцы не удалены: " + ex.Message);
        }
    }

    public void Stop() => _listener.Stop();

    async Task AcceptLoopAsync()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync();
            }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
            {
                return; // приёмник остановлен
            }
            _ = HandleAsync(client);
        }
    }

    async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var data = new MemoryStream();
                var chunk = new byte[16384];
                int headerEnd = -1;
                while (headerEnd < 0)
                {
                    int n = await stream.ReadAsync(chunk);
                    if (n == 0 || data.Length > 4 << 20) return;
                    data.Write(chunk, 0, n);
                    headerEnd = IndexOfHeaderEnd(data.GetBuffer(), (int)data.Length);
                }
                var headers = Encoding.ASCII.GetString(data.GetBuffer(), 0, headerEnd);
                int length = ContentLength(headers);
                int bodyStart = headerEnd + 4;
                while (data.Length - bodyStart < length)
                {
                    int n = await stream.ReadAsync(chunk);
                    if (n == 0) break;
                    data.Write(chunk, 0, n);
                }
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                Record(Encoding.UTF8.GetString(data.GetBuffer(), bodyStart, (int)Math.Min(length, data.Length - bodyStart)));
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
            {
                Log.Write("GSI: соединение оборвалось: " + ex.Message);
            }
        }
    }

    void Record(string body)
    {
        string state;
        GsiState parsed;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            bool hasMap = root.TryGetProperty("map", out var map);
            state = hasMap && map.TryGetProperty("game_state", out var gs) ? gs.GetString() ?? "" : "";
            string? matchId = hasMap && map.TryGetProperty("matchid", out var mid) ? mid.GetString() : null;
            bool hasPlayer = root.TryGetProperty("player", out var pl);
            string? team = hasPlayer && pl.TryGetProperty("team_name", out var tn) ? tn.GetString() : null;
            int teamSlot = hasPlayer && pl.TryGetProperty("team_slot", out var ts) && ts.ValueKind == JsonValueKind.Number ? ts.GetInt32() : -1;
            int hero = root.TryGetProperty("hero", out var h) && h.TryGetProperty("id", out var hid) && hid.ValueKind == JsonValueKind.Number ? hid.GetInt32() : 0;
            int? clock = hasMap && map.TryGetProperty("clock_time", out var ct) && ct.ValueKind == JsonValueKind.Number ? ct.GetInt32() : null;
            bool spectating = hasPlayer && (pl.TryGetProperty("team2", out _) || pl.TryGetProperty("team3", out _));
            string? custom = hasMap && map.TryGetProperty("customgamename", out var cg) && cg.ValueKind == JsonValueKind.String ? cg.GetString() : null;
            int? income = hasPlayer && pl.TryGetProperty("gold_from_income", out var gi) && gi.ValueKind == JsonValueKind.Number ? gi.GetInt32() : null;
            int? gameTime = hasMap && map.TryGetProperty("game_time", out var gt) && gt.ValueKind == JsonValueKind.Number ? (int)gt.GetDouble() : null;
            var roshanKills = new List<int>();
            if (root.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
                foreach (var e in events.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("event_type", out var et) && et.ValueKind == JsonValueKind.String && et.GetString() == "roshan_killed"
                        && e.TryGetProperty("game_time", out var egt) && egt.ValueKind == JsonValueKind.Number)
                        roshanKills.Add((int)egt.GetDouble());
            parsed = new GsiState(state, team, hero, matchId, teamSlot, clock, spectating, custom, income, gameTime, roshanKills);
        }
        catch (JsonException)
        {
            Log.Write("GSI: пришёл не JSON");
            return;
        }

        Updated?.Invoke(parsed);
        lock (_lock)
        {
            if (state != _lastState)
            {
                Log.Write($"GSI: {(_lastState is null ? "первое сообщение" : _lastState)} → {(state.Length > 0 ? state : "без map (меню)")}");
                _lastState = state;
            }
            if (SkipStates.Contains(state) || body == _lastBody || _saved >= MaxSamples) return;
            if (state == InProgress)
            {
                // отметка часов, которую матч уже прошёл и которой ещё нет в образцах
                int? mark = parsed.ClockTime is int c ? InGameMarks.Where(m => m <= c).Select(m => (int?)m).LastOrDefault() : null;
                if (mark == null || !_inGameSaved.Add($"{parsed.MatchId}:{mark}")) return;
            }
            _perState.TryGetValue(state, out var count);
            if (LateStates.Contains(state) && count >= MaxPerLateState) return;
            _perState[state] = count + 1;
            _lastBody = body;
            _saved++;

            var dir = Path.Combine(Paths.Root, "gsi_samples");
            Directory.CreateDirectory(dir);
            var name = $"{DateTime.Now:yyyyMMdd_HHmmss_fff}_{(state.Length > 0 ? state.Replace("DOTA_GAMERULES_STATE_", "") : "MENU")}.json";
            File.WriteAllText(Path.Combine(dir, name), body);
        }
    }

    static int IndexOfHeaderEnd(byte[] buf, int len)
    {
        for (int i = 0; i + 3 < len; i++)
            if (buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n') return i;
        return -1;
    }

    static int ContentLength(string headers)
    {
        foreach (var line in headers.Split("\r\n"))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(line.AsSpan("Content-Length:".Length).Trim(), out var n))
                return n;
        return 0;
    }
}
