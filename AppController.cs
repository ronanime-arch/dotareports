using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace DotaReports;

/// <summary>Значок в трее, проверка GCPD раз в минуту, пока запущена Dota, всплывающее окно на 6 секунд.</summary>
sealed class AppController
{
    const int HotkeyId = 0xD07A;
    const int DraftHotkeyId = 0xD07B;
    const int RatingHotkeyId = 0xD07C;
    const uint VK_S = 0x53;
    const uint VK_R = 0x52;
    const uint VK_D = 0x44;
    static readonly TimeSpan PopupTime = TimeSpan.FromSeconds(6);

    readonly System.Windows.Application _app;
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMinutes(1) }; // пока открыта Dota — сводка жалоб и ваши отправленные жалобы
    readonly SeenStore _seen = SeenStore.Load();
    SteamBrowser _browser = null!;
    GcpdReader _reader = null!;
    OverlayWindow _overlay = null!;
    Forms.NotifyIcon _tray = null!;
    readonly DraftData _draftData = new();
    readonly GsiListener _gsi = new();
    readonly PickWatcher _watcher = new();
    readonly DispatcherTimer _frameTimer = new() { Interval = TimeSpan.FromSeconds(0.5) };
    TurboCard _turboCard = null!;
    RatingWindow _rating = null!;
    readonly ProfileData _profile = new();
    readonly DispatcherTimer _profileTimer = new() { Interval = TimeSpan.FromSeconds(60) };
    // «Уверенность в ранге» видна только на экране профиля Dota — смотрим туда раз в 3 с, пока вы в меню
    readonly DispatcherTimer _confTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    (int Percent, DateTime At)? _confidence = ConfidenceReader.LoadLast();
    bool _confBusy;
    string _gameState = "";
    bool _picking;
    DraftWindow _draft = null!;
    // напоминания по часам матча: режим берём из пика (Турбо / All Pick)
    ReminderSettings _reminders = ReminderSettings.Load();
    readonly ReminderEngine _reminderEngine = new();
    OverlayWindow _reminderOverlay = null!;
    ReminderWindow? _reminderWindow;
    string? _matchMode;
    // Dota присылает данные хоть раз с запуска программы; нет — после пары минут подсказать, почему
    DateTime? _lastGsi;
    bool _gsiHintShown;

    ConductSummary? _latest;
    bool _busy, _dotaWasRunning;
    bool _incomingEmpty; // Steam отдаёт вкладку жалоб без таблицы — в журнал только смену состояния
    int _failStreak;     // проверок подряд с ошибкой: в фоне окно ошибки — один раз, со второй

    public AppController(System.Windows.Application app)
    {
        _app = app;
        // «DotaReports.exe --exit» из другой копии: обычный выход с закрытием WebView2 (куки входа в Steam сохраняются)
        var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ExitEventName);
        ThreadPool.RegisterWaitForSingleObject(exitEvent, (_, _) => _app.Dispatcher.BeginInvoke(Exit), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public async Task StartAsync()
    {
        Directory.CreateDirectory(Paths.Root);
        // Приёмник GSI — первым делом: окно токена и вход в Steam могут ждать сколько угодно, а Dota уже шлёт данные.
        string? gsiError = null;
        try
        {
            _gsi.Start();
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            gsiError = ex.Message;
            Log.Write($"GSI: порт {GsiListener.Port} занят: {ex.Message}");
        }
        Setup.EnsureSeed();
        if (!File.Exists(Paths.StratzToken)) Setup.AskStratzToken();
        _overlay = new OverlayWindow();
        _reminderOverlay = new OverlayWindow(top: 330);
        _draft = new DraftWindow(_draftData);
        _turboCard = new TurboCard();
        _rating = new RatingWindow();
        // после матча история в GCPD обновляется не сразу — читаем профиль через минуту после выхода в меню
        _profileTimer.Tick += async (_, _) =>
        {
            _profileTimer.Stop();
            await RefreshAsync(interactive: false, manual: false); // сводка жалоб и ваши отправленные жалобы за этот матч
            await RefreshProfileAsync();
        };
        _confTimer.Tick += async (_, _) => await CheckConfidenceAsync();
        _confTimer.Start();
        _frameTimer.Tick += (_, _) => _watcher.Tick();
        _watcher.Changed += snap => _app.Dispatcher.BeginInvoke(() =>
        {
            if (snap.Mode != null) _matchMode = snap.Mode;
            if (_picking && _draftData.TurboReady)
                _turboCard.Render(_draftData, snap, $"с экрана: банов {snap.Bans}, занято {snap.Taken}");
        });
        _gsi.Updated += s => _app.Dispatcher.BeginInvoke(() => OnGsi(s));
        CreateTray();
        if (gsiError != null)
            _tray.ShowBalloonTip(10000, "Dota Reports",
                $"Порт {GsiListener.Port} занят другой программой — карточка пика и напоминания не сработают.", Forms.ToolTipIcon.Warning);
        else if (Setup.EnsureGsiConfig() is { } gsiNote)
            _tray.ShowBalloonTip(10000, "Dota Reports", gsiNote, Forms.ToolTipIcon.Info);
        if (!Setup.ScreenSupported)
            _tray.ShowBalloonTip(10000, "Dota Reports", "Баны и союзники с экрана распознаются только при 1920×1080 — у вас другое разрешение, карточка будет без них.", Forms.ToolTipIcon.Warning);
        _browser = new SteamBrowser();
        await _browser.InitAsync();
        _reader = new GcpdReader(_browser);
        RegisterHotkey();

        _poll.Tick += async (_, _) => await OnPollAsync();
        _poll.Start();
        _ = LoadDraftAsync(); // не ждёт GCPD: вход в Steam может затянуться, а кэш пика уже на диске
        await RefreshAsync(interactive: true, manual: false);
        await RefreshProfileAsync();
    }

    // Пик начался — карточка и запись кадров; закончился — убрать.
    void OnGsi(GsiState s)
    {
        _lastGsi = DateTime.Now;
        _watcher.Update(s);
        if (s.GameState.Length == 0 && _gameState is "DOTA_GAMERULES_STATE_POST_GAME" or "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS")
            _profileTimer.Start();
        _gameState = s.GameState;
        CheckReminders(s);
        bool picking = s.GameState == "DOTA_GAMERULES_STATE_HERO_SELECTION";
        if (picking == _picking) return;
        _picking = picking;
        if (picking)
        {
            _matchMode = null; // новый матч — режим распознается заново
            _watcher.Begin(s.MatchId is { Length: > 0 } m && m != "0" ? m : DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            _frameTimer.Start();
            if (_draftData.TurboReady)
            {
                _turboCard.Render(_draftData, new PickSnapshot(new HashSet<int>(), new List<int>(), new List<int>(), new List<int>(), 0, 0, null, 0), _watcher.Status);
                _turboCard.ShowCard();
            }
        }
        else
        {
            _frameTimer.Stop();
            _watcher.End();
            _turboCard.Hide();
        }
    }

    // Часы матча идут в GSI (map.clock_time) — те же, что на экране: до горна меньше нуля, на паузе стоят.
    void CheckReminders(GsiState s)
    {
        bool inGame = s.GameState is "DOTA_GAMERULES_STATE_PRE_GAME" or "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS";
        if (!inGame || s.ClockTime is not int clock || !_reminders.Enabled || _reminders.Pick(_matchMode) is not { } profile)
        {
            _reminderEngine.Reset();
            return;
        }
        var due = _reminderEngine.Update(s.MatchId, clock, profile).ToList();
        if (due.Count == 0) return;
        string When(int at) => (at > clock ? $"через {ReminderTime.Format(at - clock)}" : "сейчас") + $" · в {ReminderTime.Format(at)}";
        var lines = due.Count == 1
            ? new List<(string, Color)> { (When(due[0].At), OverlayWindow.White) }
            : due.Select(d => ($"{d.Rule.Text} — {When(d.At)}", OverlayWindow.White)).ToList();
        lines.Add(($"профиль «{profile.Name}»", OverlayWindow.Gray));
        _reminderOverlay.ShowPopup(due.Count == 1 ? due[0].Rule.Text : "Напоминания", OverlayWindow.Green, lines, TimeSpan.FromSeconds(8));
        if (_reminders.Sound) System.Media.SystemSounds.Exclamation.Play();
    }

    void OpenReminders()
    {
        if (_reminderWindow != null)
        {
            _reminderWindow.Activate();
            return;
        }
        _reminderWindow = new ReminderWindow(_reminders);
        _reminderWindow.Saved += s =>
        {
            _reminders = s;
            _reminderEngine.Reset();
        };
        _reminderWindow.Closed += (_, _) => _reminderWindow = null;
        _reminderWindow.Show();
        _reminderWindow.Activate();
    }

    async Task LoadDraftAsync()
    {
        try
        {
            await _draftData.LoadAsync();
            if (!_profile.Loaded) _profile.LoadCache(_draftData.Heroes);
            _draftData.SetProfile(_profile);
        }
        catch (Exception ex)
        {
            Log.Write("Данные пика не загрузились: " + ex);
        }
        _draft.DataChanged();
    }

    async Task OnPollAsync()
    {
        var procs = Process.GetProcessesByName("dota2");
        bool running = procs.Length > 0;
        foreach (var p in procs) p.Dispose();

        // Сводка появляется после матча: проверяем, пока игра открыта, и ещё раз сразу после выхода.
        bool justClosed = _dotaWasRunning && !running;
        _dotaWasRunning = running;
        if (running) CheckGsiAlive();
        if (running || justClosed)
            await RefreshAsync(interactive: false, manual: false);
        if (justClosed)
            await LoadDraftAsync(); // обновить пул: появились новые игры
    }

    // Dota открыта больше двух минут, а данных GSI нет ни разу — один раз объяснить причину.
    void CheckGsiAlive()
    {
        if (_lastGsi != null || _gsiHintShown || Setup.DotaStartedAt() is not { } started || DateTime.Now - started < TimeSpan.FromMinutes(2)) return;
        var config = Setup.GsiConfigPath();
        if (config == null || !File.Exists(config)) return; // об этом уже сказали при запуске
        _gsiHintShown = true;
        var written = File.GetLastWriteTime(config);
        bool beforeConfig = started < written;
        Log.Write($"GSI: Dota открыта с {started:HH:mm:ss}, конфиг от {written:dd.MM HH:mm:ss}, данных нет");
        _tray.ShowBalloonTip(10000, "Dota Reports", beforeConfig
            ? "Dota открыта раньше, чем появился конфиг, — перезапустите её один раз, дальше не понадобится."
            : "Dota не присылает данные игры — карточка пика и напоминания не сработают. Подробности в log.txt.",
            Forms.ToolTipIcon.Warning);
    }

    async Task RefreshAsync(bool interactive, bool manual)
    {
        if (_busy) return;
        _busy = true;
        SetTrayText("проверяю…");
        try
        {
            var res = await _reader.ReadIncomingAsync(interactive);
            if (res.NeedLogin)
            {
                SetTrayText("нужно войти в Steam");
                _tray.ShowBalloonTip(8000, "Dota Reports",
                    "Нужно войти в Steam: правый клик по значку → «Проверить сейчас».", Forms.ToolTipIcon.Warning);
                return;
            }

            if (res.Me.AccountId is { } account)
                File.WriteAllText(Paths.Account, account);
            if (res.NoTable)
            {
                // Не «жалоб нет» и не «Valve сменила колонки»: Steam временно не отдаёт данные.
                // Сводки, отметку первого запуска и _latest не трогаем — иначе потом старые сводки всплывут как новые.
                if (!_incomingEmpty) Log.Write("GCPD: Steam не отдал сводку жалоб: " + GcpdReader.PageMessage(res.Table));
                _incomingEmpty = true;
                SetTrayText($"Steam не отдал сводку жалоб, проверено {DateTime.Now:HH:mm}");
                if (manual)
                    _tray.ShowBalloonTip(6000, "Dota Reports", "Steam сейчас не отдаёт сводку жалоб — проверю позже.", Forms.ToolTipIcon.Info);
                await CheckOutgoingAsync(); // отдельная вкладка — может и работать
                _failStreak = 0;
                return;
            }
            var summaries = ReportParser.Parse(res.Table);
            if (_incomingEmpty) Log.Write("GCPD: сводка жалоб снова доступна");
            _incomingEmpty = false;
            var fresh = summaries.Where(s => !_seen.Contains(s.MatchId)).ToList();
            bool firstRun = !_seen.Initialized;
            _seen.Add(fresh.Select(s => s.MatchId));
            _seen.Initialized = true;
            _seen.Save();
            _latest = summaries.FirstOrDefault();
            SetTrayText($"проверено {DateTime.Now:HH:mm}");

            // Первый запуск: старые сводки просто запоминаем, окно не показываем.
            var reported = firstRun ? null : fresh.FirstOrDefault(s => s.Reports > 0);
            if (reported != null)
            {
                ShowSummary(reported);
                // окно на 6 с легко пропустить — уведомление Windows остаётся в центре уведомлений
                _tray.ShowBalloonTip(10000, "На вас пожаловались",
                    $"{reported.Reports} {Plural(reported.Reports, "жалоба", "жалобы", "жалоб")} от {reported.ReportingParties} " +
                    $"{Plural(reported.ReportingParties, "игрока", "игроков", "игроков")} за {reported.Matches} матчей, за общение: {reported.CommsReports}",
                    Forms.ToolTipIcon.Warning);
            }
            else if (manual)
                _tray.ShowBalloonTip(4000, "Dota Reports", "Новых жалоб нет.", Forms.ToolTipIcon.Info);

            await CheckOutgoingAsync();
            _failStreak = 0; // сброс только после всей проверки: устойчивая ошибка на любом шаге должна дойти до окна
        }
        catch (Exception ex)
        {
            _failStreak++;
            Log.Write("Ошибка проверки: " + ex);
            SetTrayText("ошибка, см. log.txt");
            // разовый сбой Steam — не повод для окна: в фоне сообщаем со второй ошибки подряд и один раз, по кнопке — сразу
            if (manual || _failStreak == 2)
                _tray.ShowBalloonTip(8000, "Dota Reports — ошибка", ex.Message, Forms.ToolTipIcon.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    static readonly Dictionary<string, string> ReportReasonsRu = new()
    {
        ["Communication Abuse"] = "оскорбления", ["Ability Abuse"] = "абуз способностей", ["Feeding"] = "фид",
        ["Did Not Play Role Postgame"] = "не своя роль", ["Did Not Play Role"] = "не своя роль", ["Avoid Player"] = "избегать",
        ["Abusive Coach"] = "токсичный тренер", ["Cheating"] = "читы", ["Disruptive Gameplay"] = "мешал игре", ["Enemy Cheating"] = "читы у врага",
    };

    /// <summary>
    /// Ваши отправленные жалобы (GCPD «Игроки, на которых пожаловались» — Target = тот, на кого пожаловались вы):
    /// новая строка → окно и уведомление «Ваша жалоба отправлена». Похвалы из той же таблицы не показываем.
    /// </summary>
    async Task CheckOutgoingAsync()
    {
        try
        {
            var tables = await _reader.ReadTablesAsync(new[] { ("Account", "MatchPlayerReport", 0) });
            if (tables == null || !tables.TryGetValue("Account:MatchPlayerReport", out var table)) return;
            var headers = table.GetProperty("headers").EnumerateArray().Select(h => h.GetString() ?? "").ToList();
            var rows = table.GetProperty("rows").EnumerateArray()
                .Select(r => r.EnumerateArray().Select(c => c.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "").ToList())
                .Where(r => r.Count == headers.Count)
                .ToList();
            string Cell(List<string> r, string name) => headers.IndexOf(name) is var i and >= 0 ? r[i] : "";

            bool firstRun = !File.Exists(Paths.SeenOutgoing);
            var seen = firstRun ? new HashSet<string>() : System.Text.Json.JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(Paths.SeenOutgoing)) ?? new();
            var fresh = new List<(string Target, string Match, List<string> Reasons)>();
            foreach (var r in rows)
            {
                var key = $"{Cell(r, "MatchID")}|{Cell(r, "Target")}|{Cell(r, "Date")}";
                if (!seen.Add(key)) continue;
                var reasons = headers.Where((h, i) => r[i] == "Yes" && h != "Commended").Select(h => ReportReasonsRu.GetValueOrDefault(h, h)).ToList();
                if (reasons.Count > 0) fresh.Add((Cell(r, "Target"), Cell(r, "MatchID"), reasons));
            }
            File.WriteAllText(Paths.SeenOutgoing, System.Text.Json.JsonSerializer.Serialize(seen));
            if (firstRun || fresh.Count == 0) return; // при первом запуске старые строки только запоминаем

            var lines = fresh.Take(4).Select(f => ($"{f.Target} — {string.Join(", ", f.Reasons)}", OverlayWindow.White)).ToList();
            lines.Add(($"матч {fresh[0].Match} · это жалоба от вас, не на вас", OverlayWindow.Gray));
            _overlay.ShowPopup(fresh.Count == 1 ? "Ваша жалоба отправлена" : $"Ваши жалобы отправлены: {fresh.Count}", OverlayWindow.White, lines, PopupTime);
            _tray.ShowBalloonTip(10000, "Ваша жалоба отправлена",
                string.Join("; ", fresh.Take(3).Select(f => $"{f.Target} — {string.Join(", ", f.Reasons)}")) + $" (матч {fresh[0].Match})",
                Forms.ToolTipIcon.Info);
        }
        catch (Exception ex)
        {
            // дополнительная вкладка: её сбой не должен превращать проверку жалоб на вас в ошибку
            Log.Write("Отправленные жалобы не прочитаны: " + ex.Message);
        }
    }

    // Вкладки GCPD для окна рейтинга и пула героев; та же страница Steam, что и для жалоб, поэтому тот же замок _busy.
    async Task RefreshProfileAsync()
    {
        if (_busy || _draftData.Heroes.Count == 0) return;
        _busy = true;
        try
        {
            var tables = await _reader.ReadTablesAsync(ProfileData.Tabs);
            if (tables == null) return; // нужен вход — напомнит проверка жалоб
            // пустые вкладки Steam («повторите попытку позже») в tables не попадают — по ним остаются прежние данные
            if (tables.Count < ProfileData.Tabs.Length)
                Log.Write("Профиль GCPD: Steam отдал не все вкладки: " + (tables.Count > 0 ? string.Join(", ", tables.Keys) : "ни одной"));
            if (tables.Count == 0) return;
            _profile.Apply(tables, _draftData.Heroes, DateTime.Now);
            _profile.Save(tables); // недостающие вкладки Save возьмёт из прошлого кэша
            _draftData.SetProfile(_profile);
            if (_rating.IsVisible) RenderRating();
        }
        catch (Exception ex)
        {
            Log.Write("Профиль GCPD не прочитан: " + ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    static readonly string[] MedalNames = { "", "Рекрут", "Страж", "Рыцарь", "Герой", "Легенда", "Властелин", "Божество", "Титан" };

    void RenderRating()
    {
        var lines = new List<(string, Color)>();
        string medal = _draftData.Medal is >= 1 and <= 8
            ? MedalNames[_draftData.Medal] + (_draftData.RankTier % 10 is >= 1 and <= 5 ? " " + new[] { "", "I", "II", "III", "IV", "V" }[_draftData.RankTier % 10] : "")
            : "неизвестен";
        if (_profile.Calibration() is { } cal)
        {
            lines.Add(($"На калибровке: {cal.Games}{(cal.AtLeast ? "+" : "")} {Plural(cal.Games, "игра", "игры", "игр")} · {cal.Wins}–{cal.Games - cal.Wins} ({100.0 * cal.Wins / cal.Games:0}%)", OverlayWindow.Green));
            if (_confidence is { } conf)
                lines.Add(($"Уверенность в ранге: {conf.Percent}% (профиль Dota, {conf.At:dd.MM HH:mm})", OverlayWindow.Green));
            else
                lines.Add(("Уверенность в ранге: откройте профиль в Dota — прочитаю", OverlayWindow.Gray));
            lines.Add(($"ранг до калибровки: {medal}", OverlayWindow.Gray));
        }
        else
            lines.Add(($"Ранг: {medal}", OverlayWindow.White));

        if (_profile.Loaded)
        {
            var today = _profile.Today();
            lines.Add(($"Сегодня: {today.Games} {Plural(today.Games, "игра", "игры", "игр")} · {today.Wins}–{today.Games - today.Wins}" +
                       (today.RankedGames > 0 ? $" · в рейтинге {today.RankedWins}–{today.RankedGames - today.RankedWins}" : ""), OverlayWindow.White));
            if (_profile.Streak() is { } st)
                lines.Add(($"Серия: {st.Count} {(st.Wins ? Plural(st.Count, "победа", "победы", "побед") : Plural(st.Count, "поражение", "поражения", "поражений"))} подряд",
                    st.Wins ? OverlayWindow.Green : OverlayWindow.Red));
        }
        if (_latest != null)
            lines.Add(($"Поведение: {_latest.Score}" + (_latest.ScoreDelta is int d ? $" ({d:+#;-#;0})" : "") +
                       $" · похвал {_latest.Commends} · жалоб {_latest.Reports}", OverlayWindow.White));
        if (_profile.Loaded)
        {
            int w = _profile.AccountInt("Wins"), l = _profile.AccountInt("Losses");
            if (w + l > 0) lines.Add(($"Всего: {w}–{l} ({100.0 * w / (w + l):0.0}%)", OverlayWindow.Gray));
            lines.Add(($"Можно отправить: жалоб {_profile.AccountInt("NegativeReportsRemaining")} · похвал {_profile.AccountInt("PositiveReportsRemaining")}", OverlayWindow.Gray));
        }
        var updated = _profile.UpdatedAt is { } u ? $"данные Valve от {u:dd.MM HH:mm}" : "данные Valve ещё не загружены";
        _rating.Render("Рейтинг", lines, updated + " · Ctrl+Alt+S — скрыть");
    }

    async Task CheckConfidenceAsync()
    {
        if (_confBusy || _picking || _gameState is "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS" or "DOTA_GAMERULES_STATE_PRE_GAME") return;
        var procs = Process.GetProcessesByName("dota2");
        bool running = procs.Length > 0;
        foreach (var p in procs) p.Dispose();
        if (!running || !Setup.ScreenSupported) return;
        _confBusy = true;
        try
        {
            var value = await Task.Run(() =>
            {
                using var bmp = ConfidenceReader.CaptureScreen();
                return ConfidenceReader.Read(bmp);
            });
            if (value is { } v && (_confidence?.Percent != v || DateTime.Now - _confidence.Value.At > TimeSpan.FromMinutes(30)))
            {
                ConfidenceReader.Save(v);
                _confidence = (v, DateTime.Now);
                if (_rating.IsVisible) RenderRating();
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.ExternalException)
        {
            Log.Write("Полоса уверенности не прочитана: " + ex.Message);
        }
        finally
        {
            _confBusy = false;
        }
    }

    async Task ToggleRatingAsync()
    {
        if (_rating.IsVisible)
        {
            _rating.Toggle();
            return;
        }
        RenderRating();
        _rating.Toggle();
        await CheckConfidenceAsync();   // если профиль Dota открыт прямо сейчас — сразу подхватим процент
        if (_profile.UpdatedAt is not { } u || DateTime.Now - u > TimeSpan.FromMinutes(15))
            await RefreshProfileAsync();
    }

    void ShowSummary(ConductSummary s)
    {
        var lines = new List<(string, Color)>();
        if (s.Reports > 0)
        {
            lines.Add(($"{s.Reports} {Plural(s.Reports, "жалоба", "жалобы", "жалоб")} от {s.ReportingParties} {Plural(s.ReportingParties, "игрока", "игроков", "игроков")}" +
                       $" — в {s.ReportedMatches} из {s.Matches} {Plural(s.Matches, "матча", "матчей", "матчей")}", OverlayWindow.White));
            lines.Add(($"за общение (чат/голос): {s.CommsReports}", s.CommsReports > 0 ? OverlayWindow.Red : OverlayWindow.Gray));
        }
        var score = s.Score is int sc ? $"поведение {sc}" + (s.ScoreDelta is int d ? $" ({d:+#;-#;0})" : "") + " · " : "";
        lines.Add(($"{score}похвал: {s.Commends}", OverlayWindow.Gray));
        if (s.When is DateTime w) lines.Add(($"сводка за {s.Matches} {Plural(s.Matches, "матч", "матча", "матчей")} от {w:dd.MM HH:mm}", OverlayWindow.Gray));

        _overlay.ShowPopup(
            s.Reports > 0 ? "На вас пожаловались" : "Жалоб на вас нет",
            s.Reports > 0 ? OverlayWindow.Red : OverlayWindow.Green,
            lines, PopupTime);
    }

    static string Plural(int n, string one, string few, string many)
    {
        int m10 = n % 10, m100 = n % 100;
        if (m10 == 1 && m100 != 11) return one;
        if (m10 is >= 2 and <= 4 && m100 is < 12 or > 14) return few;
        return many;
    }

    void ShowLatest()
    {
        if (_latest != null)
            ShowSummary(_latest);
        else
            _overlay.ShowPopup("Dota Reports", OverlayWindow.White, new[] { ("Сводки ещё не загружены.", OverlayWindow.Gray) }, PopupTime);
    }

    void RegisterHotkey()
    {
        HwndSource.FromHwnd(_browser.Handle).AddHook(WndProc);
        if (!Native.RegisterHotKey(_browser.Handle, HotkeyId, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, VK_R))
        {
            Log.Write("Ctrl+Alt+R занята другой программой");
            _tray.ShowBalloonTip(8000, "Dota Reports",
                "Ctrl+Alt+R занята другой программой — последняя сводка по двойному клику на значок.", Forms.ToolTipIcon.Warning);
        }
        if (!Native.RegisterHotKey(_browser.Handle, RatingHotkeyId, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, VK_S))
            Log.Write("Ctrl+Alt+S занята другой программой");
        if (!Native.RegisterHotKey(_browser.Handle, DraftHotkeyId, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, VK_D))
        {
            Log.Write("Ctrl+Alt+D занята другой программой");
            _tray.ShowBalloonTip(8000, "Dota Reports",
                "Ctrl+Alt+D занята другой программой — помощник в пике открывается из меню значка.", Forms.ToolTipIcon.Warning);
        }
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            ShowLatest();
            handled = true;
        }
        else if (msg == Native.WM_HOTKEY && wParam.ToInt32() == RatingHotkeyId)
        {
            _ = ToggleRatingAsync();
            handled = true;
        }
        else if (msg == Native.WM_HOTKEY && wParam.ToInt32() == DraftHotkeyId)
        {
            _draft.Toggle();
            handled = true;
        }
        return IntPtr.Zero;
    }

    void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Помощник в пике (Ctrl+Alt+D)", null, (_, _) => _draft.Toggle());
        menu.Items.Add("Рейтинг (Ctrl+Alt+S)", null, async (_, _) => await ToggleRatingAsync());
        menu.Items.Add("Последняя сводка (Ctrl+Alt+R)", null, (_, _) => ShowLatest());
        menu.Items.Add("Напоминания…", null, (_, _) => OpenReminders());
        menu.Items.Add("Проверить сейчас", null, async (_, _) => await RefreshAsync(interactive: true, manual: true));
        menu.Items.Add("Папка с данными", null, (_, _) => Process.Start("explorer.exe", Paths.Root));
        menu.Items.Add("Токен Stratz…", null, async (_, _) =>
        {
            Setup.AskStratzToken();
            await LoadDraftAsync();
        });
        var autostart = new Forms.ToolStripMenuItem("Запускать с Windows") { Checked = Setup.AutostartEnabled, CheckOnClick = true };
        autostart.CheckedChanged += (_, _) => Setup.AutostartEnabled = autostart.Checked;
        menu.Items.Add(autostart);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Exit());

        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Shield,
            Text = "Dota Reports",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => ShowLatest();
    }

    void SetTrayText(string status)
    {
        var text = "Dota Reports: " + status;
        _tray.Text = text.Length > 120 ? text[..120] : text;
    }

    void Exit()
    {
        _poll.Stop();
        _confTimer.Stop();
        _gsi.Stop();
        Native.UnregisterHotKey(_browser.Handle, HotkeyId);
        Native.UnregisterHotKey(_browser.Handle, DraftHotkeyId);
        Native.UnregisterHotKey(_browser.Handle, RatingHotkeyId);
        _tray.Visible = false;
        _tray.Dispose();
        _overlay.Close();
        _reminderOverlay.Close();
        _reminderWindow?.Close();
        _draft.Close();
        _turboCard.Close();
        _rating.Close();
        _browser.Close();
        _app.Shutdown();
    }
}
