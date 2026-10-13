using System;
using System.IO;
using System.Text.Json;

namespace DotaReports;

/// <summary>
/// Режим текущего матча для напоминаний и пика: с экрана пика (GridReader, голосование по кадрам) и по пассивному доходу
/// из GSI (в Турбо он примерно вдвое выше). Не совпали или ничего не известно — null: лучше промолчать, чем ошибиться.
/// Привязан к номеру матча и лежит в match_mode.json — перезапуск программы посреди игры его не теряет.
/// </summary>
sealed class MatchMode
{
    // пассивный доход в минуту: в обычной игре около 90, в Турбо примерно вдвое больше; между порогами — не решаем
    const double NormalMax = 120, TurboMin = 150;
    const int MeasureSeconds = 60, MeasureUntil = 600; // позже доход могут добавлять предметы

    public string? MatchId { get; set; }
    public string? Screen { get; set; }
    public string? Income { get; set; }

    (int Clock, int Gold)? _start;

    /// <summary>Режим матча: TURBO, ALL_PICK или null.</summary>
    public string? Mode => Screen == null ? Income : Income == null || Income == Screen ? Screen : null;

    /// <summary>Начался пик: всё прошлое забыть. Номер матча в пике бывает пустым — тогда он придёт позже.</summary>
    public void Begin(string? matchId)
    {
        MatchId = matchId;
        Screen = Income = null;
        _start = null;
        Save();
    }

    public void FromScreen(string? mode)
    {
        if (mode == Screen) return;
        Screen = mode;
        if (Income != null && mode != null && Income != mode)
            Log.Write($"Режим: на экране {mode}, по доходу {Income} — не совпали, напоминания только общие");
        Save();
    }

    /// <summary>Каждое сообщение GSI: следить за номером матча и мерить пассивный доход.</summary>
    public void Update(GsiState s)
    {
        if (s.MatchId is not { Length: > 0 } id || id == "0") return;
        if (id != MatchId)
        {
            // другой матч (перезапуск программы между играми) — прежний режим не годится; номер, пришедший после пика, — тот же матч
            if (MatchId is { Length: > 0 } && MatchId != "0") Screen = Income = null;
            MatchId = id;
            _start = null;
            Save();
        }
        if (Income != null || !s.OwnGame || s.GameState != "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS"
            || s.ClockTime is not int clock || s.GoldFromIncome is not int gold || clock < 0 || clock > MeasureUntil) return;
        if (_start is not { } start || gold < start.Gold || clock < start.Clock)
        {
            _start = (clock, gold);
            return;
        }
        if (clock - start.Clock < MeasureSeconds) return;
        double perMinute = (gold - start.Gold) * 60.0 / (clock - start.Clock);
        string? mode = perMinute <= NormalMax ? "ALL_PICK" : perMinute >= TurboMin ? "TURBO" : null;
        Log.Write($"Режим по доходу: {perMinute:0}/мин с {ReminderTime.Format(start.Clock)} по {ReminderTime.Format(clock)} → {mode ?? "не ясно"}");
        if (mode == null)
        {
            _start = (clock, gold); // померить ещё раз на следующей минуте
            return;
        }
        Income = mode;
        if (Screen != null && Screen != mode)
            Log.Write($"Режим: на экране {Screen}, по доходу {mode} — не совпали, напоминания только общие");
        Save();
    }

    public static MatchMode Load()
    {
        try
        {
            // без номера матча (пик без номера, потом выход) — режим ни к чему не привязан: в следующей игре он был бы чужим
            if (File.Exists(Paths.MatchMode) && JsonSerializer.Deserialize<MatchMode>(File.ReadAllText(Paths.MatchMode)) is { MatchId: { Length: > 0 } id } saved && id != "0")
                return saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Write("match_mode.json не прочитан: " + ex.Message);
        }
        return new();
    }

    void Save()
    {
        try
        {
            File.WriteAllText(Paths.MatchMode, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("match_mode.json не записан: " + ex.Message);
        }
    }
}
