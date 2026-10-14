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
    // звания в Турбо: «Недавно играли вместе» в матче проверяем раз в 40 с, пока не появятся его игроки (не дольше 8 раз)
    // В Турбо 5 на 5: новых в списке не больше 9 и не меньше 5 (даже с полной группой — пятеро врагов).
    // 1–4 новых — не этот матч: Steam дописал кого-то из прошлых игр (было: 4 чужих ника вместо 9 игроков).
    const int RanksMaxTries = 8, MaxOthers = 9, MinFresh = 5;
    static readonly TimeSpan RanksRetry = TimeSpan.FromSeconds(40);
    static readonly TimeSpan RanksShowTime = TimeSpan.FromSeconds(20);
    // Steam мог добавить игроков ещё при принятии матча — прочитанное за последнюю минуту перед матчем не годится
    static readonly TimeSpan CoplayMargin = TimeSpan.FromSeconds(60);

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
    bool _picking;   // пик и время стратегии: карточка видна, кадры снимаются
    int _ownHero;    // ваш герой в этом пике (GSI), 0 — ещё не выбран
    PickSnapshot? _lastSnap;
    DraftWindow _draft = null!;
    // напоминания по часам матча: режим берём из пика (Турбо / All Pick)
    ReminderSettings _reminders = ReminderSettings.Load();
    readonly ReminderEngine _reminderEngine = new();
    readonly RoshanTimer _roshan = new();   // «может возродиться» и «жив» после убийства — по событию GSI
    OverlayWindow _reminderOverlay = null!;
    ReminderWindow? _reminderWindow;
    readonly MatchMode _mode = MatchMode.Load(); // режим матча: экран пика + доход из GSI
    // Dota присылает данные хоть раз с запуска программы; нет — после пары минут подсказать, почему
    DateTime? _lastGsi;
    bool _gsiHintShown;
    // Звания игроков в Турбо: кто был в «Недавно играли вместе» в меню перед матчем; новые там во время матча — его игроки
    CoplayReader _coplay = null!;
    RankStrip _rankStrip = null!;
    readonly List<(DateTime At, HashSet<string> Ids)> _coplayMenu = new();
    HashSet<string>? _coplayBefore;
    bool _coplayFailing;
    string? _ranksMatch;
    int _ranksTries;
    DateTime _ranksNext;

    ConductSummary? _latest;
    bool _busy, _dotaWasRunning;
    bool _incomingEmpty; // Steam отдаёт вкладку жалоб без таблицы — в журнал только смену состояния
    int _failStreak;     // проверок подряд с ошибкой: в фоне окно ошибки — один раз, со второй
    bool _profilePending; // профиль после матча не прочитан из-за занятости — дочитает поминутный опрос
    readonly ConductHistory _history = ConductHistory.Load();
    BehaviorHistoryWindow? _historyWindow;
    AnalyticsWindow? _analytics;
    Forms.ToolStripMenuItem _updateItem = null!;
    Forms.ToolStripMenuItem _loginItem = null!; // «Войти в Steam…» — виден, только когда вход слетел
    const string LoginTip = "Нужно войти в Steam: нажмите на это уведомление или значок в трее → «Войти в Steam…».";

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
        ReminderSound.Use(_reminders.Sounds); // звуки напоминаний — из настроек (микшер)
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
        _reminderOverlay = new OverlayWindow(top: 330, backgroundAlpha: 0x99); // полупрозрачное: игра под ним видна
        _draft = new DraftWindow(_draftData) { OpenUrl = url => OpenAnalytics(url) };
        _turboCard = new TurboCard();
        _rankStrip = new RankStrip();
        _rating = new RatingWindow();
        // после матча история в GCPD обновляется не сразу — читаем профиль через минуту после выхода в меню
        _profileTimer.Tick += async (_, _) =>
        {
            _profileTimer.Stop();
            // идёт поминутная проверка: повтор через 60 с попал бы в неё же (у опроса тот же период) — дочитает сам опрос
            if (_busy)
            {
                _profilePending = true;
                return;
            }
            await RefreshAsync(interactive: false, manual: false); // сводка жалоб и ваши отправленные жалобы за этот матч
            await RefreshProfileAsync();
        };
        _confTimer.Tick += async (_, _) => await CheckConfidenceAsync();
        _confTimer.Start();
        _frameTimer.Tick += (_, _) => _watcher.Tick();
        _watcher.Changed += snap => _app.Dispatcher.BeginInvoke(() =>
        {
            _mode.FromScreen(snap.Mode);
            _lastSnap = snap;
            RenderCard();
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
        _coplay = new CoplayReader(_browser);
        RegisterHotkey();

        _poll.Tick += async (_, _) => await OnPollAsync();
        _poll.Start();
        _ = LoadDraftAsync(); // не ждёт GCPD: вход в Steam может затянуться, а кэш пика уже на диске
        _ = UpdateAsync(manual: false); // вышла новая версия — подсказать про кнопку в трее
        // герои из кэша уже прочитаны (до первого await в LoadAsync): профиль из кэша — сразу, не дожидаясь сети,
        // иначе сравнение low priority и окна истории зависели бы от того, что успело раньше
        if (!_profile.Loaded && _draftData.Heroes.Count > 0)
        {
            _profile.LoadCache(_draftData.Heroes);
            UpdateHistory(null);
        }
        await RefreshAsync(interactive: true, manual: false);
        await RefreshProfileAsync();
        await RefreshCoplayAsync(); // кто был в «Недавно играли вместе» до первого матча
    }

    // Пик начался — карточка и запись кадров; закончился — убрать.
    void OnGsi(GsiState s)
    {
        _lastGsi = DateTime.Now;
        _watcher.Update(s);
        _mode.Update(s);
        if (s.GameState.Length == 0 && _gameState is "DOTA_GAMERULES_STATE_POST_GAME" or "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS")
            _profileTimer.Start();
        if (s.GameState.Length > 0 && _gameState.Length == 0)
            _coplayBefore = _coplayMenu.LastOrDefault(r => r.At <= DateTime.Now - CoplayMargin).Ids; // из меню в матч
        _gameState = s.GameState;
        CheckReminders(s);
        CheckRanks(s);
        // карточка — и в пике, и во время стратегии: тогда видны все герои, и прогноз по драфту полный
        bool picking = s.GameState is "DOTA_GAMERULES_STATE_HERO_SELECTION" or "DOTA_GAMERULES_STATE_STRATEGY_TIME";
        if (picking && s.HeroId != _ownHero)
        {
            _ownHero = s.HeroId;
            if (_picking) RenderCard();
        }
        if (picking == _picking) return;
        _picking = picking;
        if (picking)
        {
            _mode.Begin(s.MatchId); // новый матч — режим распознается заново
            _watcher.Begin(s.MatchId is { Length: > 0 } m && m != "0" ? m : DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            _frameTimer.Start();
            _lastSnap = new PickSnapshot(new HashSet<int>(), new List<int>(), new List<int>(), new List<int>(), 0, 0, null, 0);
            if (_draftData.TurboReady)
            {
                _turboCard.Render(_draftData, _lastSnap, _watcher.Status, _ownHero);
                _turboCard.ShowCard();
            }
        }
        else
        {
            _frameTimer.Stop();
            _watcher.End();
            _turboCard.Hide();
            if (s.GameState.Length > 0 && s.OwnGame) ShowPrediction(s); // матч пошёл дальше (не выход в меню из пика)
            _ownHero = 0;
            _lastSnap = null;
        }
    }

    void RenderCard()
    {
        if (!_picking || _lastSnap == null || !_draftData.TurboReady) return;
        _turboCard.Render(_draftData, _lastSnap, $"с экрана: банов {_lastSnap.Bans}, занято {_lastSnap.Taken}", _ownHero);
        _turboCard.ShowCard(); // данные могли догрузиться уже посреди пика — тогда карточки ещё не было
    }

    // Начало матча: прогноз по итоговому драфту — окно поверх игры и журнал (потом сверить с исходом),
    // разбор матча и Dotabuff вашего героя — в «Аналитике» (окно свёрнуто или уже открыто — игре не мешает).
    void ShowPrediction(GsiState s)
    {
        if (_lastSnap is not { } snap) return;
        var mode = _mode.Mode ?? snap.Mode;
        var team = snap.Allies.Concat(snap.Planned).Where(h => h != s.HeroId).Distinct().ToList();
        var chance = mode == null ? null : _draftData.WinProbability(mode, team, snap.Enemies, s.HeroId);
        ShowMatchAnalytics(new MatchInfo(s.HeroId, team, snap.Enemies.ToList(), mode, chance));
        // режим неизвестен — винрейты Турбо и обычной игры разные, гадать не будем
        if (chance == null || chance.KnownAllies + chance.KnownEnemies < 4) return;
        var color = chance.P >= 0.53 ? OverlayWindow.Green : chance.P <= 0.47 ? OverlayWindow.Red : OverlayWindow.White;
        var verdict = chance.KnownEnemies == 0 ? "враги неизвестны — оценка только по вашим героям"
            : chance.P >= 0.53 ? "драфт в вашу пользу" : chance.P <= 0.47 ? "драфт за врагом — играйте аккуратнее" : "драфт примерно равный";
        var lines = new List<(string, Color)>
        {
            (verdict, OverlayWindow.White),
            ($"известно: ваших {chance.KnownAllies}, их {chance.KnownEnemies}" + (chance.Parts.Count > 0 ? " · " + string.Join(" · ", chance.Parts) : ""), OverlayWindow.Gray),
        };
        // ваш герой против их героев: самые заметные матчапы
        var matchups = snap.Enemies.Select(e => (Enemy: e, Adv: _draftData.MatchupAdvantage(s.HeroId, e, mode)))
            .Where(m => m.Adv is double a && Math.Abs(a) >= 0.5).OrderByDescending(m => Math.Abs(m.Adv!.Value)).Take(3)
            .Select(m => $"{_draftData.Heroes.GetValueOrDefault(m.Enemy, $"#{m.Enemy}")} {m.Adv:+0.0;-0.0}").ToList();
        if (matchups.Count > 0)
            lines.Add(($"{_draftData.Heroes.GetValueOrDefault(s.HeroId, "ваш герой")} против: {string.Join(", ", matchups)}", OverlayWindow.Gray));
        lines.Add(("разбор и Dotabuff героя — «Аналитика» (трей)", OverlayWindow.Gray));
        _overlay.ShowPopup((chance.KnownEnemies == 0 ? "Ваш драфт" : "Шанс победы") + $" ≈ {chance.P:0%}", color, lines, PopupTime, color);
        // в журнал — только когда враг известен: иначе это не прогноз матча
        if (chance.KnownEnemies >= 3 && s.MatchId is { Length: > 0 } id && id != "0")
            PredictionLog.Add(new Prediction(id, mode, chance.P, chance.KnownAllies, chance.KnownEnemies, DateTime.Now));
    }

    // Разбор матча в «Аналитике»: открытое окно обновляется без перехвата фокуса, закрытое открывается свёрнутым.
    void ShowMatchAnalytics(MatchInfo info)
    {
        if (_analytics == null)
        {
            _analytics = new AnalyticsWindow(_draftData, _browser?.WebEnvironment) { ShowActivated = false, WindowState = System.Windows.WindowState.Minimized };
            _analytics.Closed += (_, _) => _analytics = null;
            _analytics.Show();
        }
        _analytics.ShowMatch(info);
    }

    // Часы матча идут в GSI (map.clock_time) — те же, что на экране: до горна меньше нуля, на паузе стоят.
    void CheckReminders(GsiState s)
    {
        bool inGame = s.GameState is "DOTA_GAMERULES_STATE_PRE_GAME" or "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS";
        // только своя игра: не просмотр чужой (DotaTV, повтор), не кастомка, герой выбран
        if (!inGame || !s.OwnGame || s.ClockTime is not int clock || !_reminders.Enabled)
        {
            _reminderEngine.Reset();
            return;
        }
        if (_roshan.Update(s, clock, _mode.Mode) is { } roshan && _reminders.Roshan)
        {
            _reminderOverlay.ShowPopup(roshan.Title, OverlayWindow.White, new[] { (roshan.Detail, OverlayWindow.Gray) }, TimeSpan.FromSeconds(8));
            if (_reminders.Sound) ReminderSound.Play(_reminders.RoshanSound);
        }
        // режим не определён — только общие для всех профилей тайминги, а не первый профиль
        var profile = _reminders.Pick(_mode.Mode);
        // общие тайминги — только пока режим не ясен; ясен, но профиля под него нет — молчим
        var rules = profile ?? (_mode.Mode == null ? _reminders.CommonRules() : null);
        if (rules == null)
        {
            _reminderEngine.Reset();
            return;
        }
        var due = _reminderEngine.Update(s.MatchId, clock, rules).ToList();
        if (due.Count == 0) return;
        string When(int at) => (at > clock ? $"через {ReminderTime.Format(at - clock)}" : "сейчас") + $" · в {ReminderTime.Format(at)}";
        // у каждого напоминания свой цвет: одно — заголовок и полоса его цвета, несколько — каждая строка своим
        var lines = due.Count == 1
            ? new List<(string, Color)> { (When(due[0].At), OverlayWindow.White) }
            : due.Select(d => ($"{d.Rule.Text} — {When(d.At)}", d.Rule.DisplayColor())).ToList();
        lines.Add((profile != null ? $"профиль «{profile.Name}»" : "режим ещё не определён — только общие тайминги", OverlayWindow.Gray));
        var color = due.Count == 1 ? due[0].Rule.DisplayColor() : OverlayWindow.White;
        _reminderOverlay.ShowPopup(due.Count == 1 ? due[0].Rule.Text : "Напоминания", color, lines, TimeSpan.FromSeconds(8),
            due.Count == 1 ? color : due[0].Rule.DisplayColor());
        if (_reminders.Sound) ReminderSound.Play(due[0].Rule.SoundKey()); // несколько сразу — звук первого
    }

    // Турбо: звания игроков матча. Valve их там не показывает; кто в матче — новые в «Недавно играли вместе»
    // с последнего чтения в меню, звания — OpenDota. Команды и героев Steam не сообщает — полоска со всеми.
    void CheckRanks(GsiState s)
    {
        if (s.GameState is not ("DOTA_GAMERULES_STATE_PRE_GAME" or "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS") || !s.OwnGame
            || s.MatchId is not { Length: > 0 } id || id == "0" || _mode.Mode != "TURBO") return;
        if (id != _ranksMatch)
        {
            _ranksMatch = id;
            _ranksTries = 0;
            _ranksNext = DateTime.MinValue;
            if (_coplayBefore == null) Log.Write("Звания: нет списка «Недавно играли вместе» из меню перед матчем — в этом матче не ищу");
        }
        if (_coplayBefore == null || _ranksTries >= RanksMaxTries || _busy || DateTime.Now < _ranksNext) return;
        _ = ReadRanksAsync(id, _coplayBefore);
    }

    async Task ReadRanksAsync(string matchId, HashSet<string> before)
    {
        _busy = true;
        _ranksTries++;
        _ranksNext = DateTime.Now + RanksRetry;
        List<CoplayPlayer>? players = null;
        try
        {
            players = await _coplay.ReadAsync(Paths.CoplayMatchDump);
        }
        catch (Exception ex)
        {
            Log.Write("Звания: «Недавно играли вместе» не прочитан: " + ex.Message);
        }
        finally
        {
            _busy = false;
        }
        if (matchId != _ranksMatch) return; // пока читали, начался другой матч
        var fresh = players?.Where(p => !before.Contains(p.AccountId)).ToList() ?? new();
        if (fresh.Count < MinFresh)
        {
            // мало новых — не игроки этого матча: запоминаем их как уже бывших и ждём дальше
            if (fresh.Count > 0)
            {
                Log.Write($"Звания: новых в «Недавно играли вместе» {fresh.Count} — меньше, чем в матче, не показываю: " +
                          string.Join(", ", fresh.Select(p => $"{p.AccountId} «{p.Name}»")));
                before.UnionWith(fresh.Select(p => p.AccountId));
            }
            if (_ranksTries >= RanksMaxTries)
                Log.Write($"Звания: Steam не добавил игроков матча в «Недавно играли вместе» за {RanksMaxTries} проверок");
            return;
        }
        _ranksTries = RanksMaxTries; // в этом матче больше не ищем
        // больше, чем игроков в матче, — в списке и игроки прошлых матчей: показывать их как этих нельзя
        if (fresh.Count > MaxOthers)
        {
            Log.Write($"Звания: новых в «Недавно играли вместе» {fresh.Count} — больше, чем игроков в матче, не показываю");
            return;
        }
        var ranks = await Ranks.FetchAsync(fresh);
        Log.Write($"Звания в матче {matchId}: " + string.Join(", ", ranks.Select(r => $"{r.AccountId} «{r.Name}» {r.RankTier?.ToString() ?? "?"}")));
        if (matchId == _ranksMatch && _gameState is "DOTA_GAMERULES_STATE_PRE_GAME" or "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS")
            _rankStrip.ShowRanks(ranks, RanksShowTime);
    }

    // В меню, пока открыта Dota: кто уже есть в «Недавно играли вместе» — с этим сравнивается список во время матча.
    async Task RefreshCoplayAsync()
    {
        if (_busy || _gameState.Length > 0) return;
        _busy = true;
        try
        {
            if (await _coplay.ReadAsync() is { } players)
            {
                _coplayMenu.Add((DateTime.Now, players.Select(p => p.AccountId).ToHashSet()));
                if (_coplayMenu.Count > 3) _coplayMenu.RemoveAt(0);
            }
            if (_coplayFailing) Log.Write("«Недавно играли вместе» снова читается");
            _coplayFailing = false;
        }
        catch (Exception ex)
        {
            // раз в минуту — в журнал только первую ошибку подряд
            if (!_coplayFailing) Log.Write("«Недавно играли вместе» не прочитан: " + ex.Message);
            _coplayFailing = true;
        }
        finally
        {
            _busy = false;
        }
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
            ReminderSound.Use(s.Sounds);
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
            if (!_profile.Loaded)
            {
                _profile.LoadCache(_draftData.Heroes);
                UpdateHistory(null);
            }
            _draftData.SetProfile(_profile);
        }
        catch (Exception ex)
        {
            Log.Write("Данные пика не загрузились: " + ex);
        }
        _draft.DataChanged();
        RenderCard(); // данные догрузились посреди пика — карточка с ними
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
        if (_profilePending && !_busy)
        {
            _profilePending = false;
            await RefreshProfileAsync();
        }
        if (running) await RefreshCoplayAsync(); // только в меню: для званий в следующем Турбо
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
            _loginItem.Visible = res.NeedLogin;
            if (res.NeedLogin)
            {
                SetTrayText("нужно войти в Steam");
                _tray.ShowBalloonTip(8000, "Dota Reports", LoginTip, Forms.ToolTipIcon.Warning);
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

            UpdateHistory(summaries); // и при первом запуске: это история, а не оповещение
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
            // пустой Account — первый запуск без кэша: уже действующий low priority покажет окно рейтинга, не уведомление
            int? lpBefore = _profile.Account.Count > 0 ? _profile.LowPriorityGames : null;
            _profile.Apply(tables, _draftData.Heroes, DateTime.Now);
            _profile.Save(tables); // недостающие вкладки Save возьмёт из прошлого кэша
            _draftData.SetProfile(_profile);
            if (lpBefore is int before) NotifyLowPriority(before, _profile.LowPriorityGames);
            UpdateHistory(null);
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

    // Low priority назначили или сняли. Счётчик убывает после каждой игры в нём — это не событие.
    void NotifyLowPriority(int before, int now)
    {
        if (before == 0 && now > 0)
        {
            var games = $"{now} {Plural(now, "игра", "игры", "игр")}";
            _overlay.ShowPopup("Вы в low priority", OverlayWindow.Red, new[]
            {
                ($"осталось {games}", OverlayWindow.White),
                ("данные Valve (GCPD), обновлены после матча", OverlayWindow.Gray),
            }, PopupTime);
            _tray.ShowBalloonTip(10000, "Вы в low priority", $"Осталось {games}.", Forms.ToolTipIcon.Warning);
        }
        else if (before > 0 && now == 0)
            _tray.ShowBalloonTip(8000, "Dota Reports", "Low priority снят.", Forms.ToolTipIcon.Info);
    }

    // Сводки и окна матчей — в conduct_history.json, файл — только при изменениях (опрос идёт раз в минуту).
    // Побочная функция: её сбой не должен ломать оповещения о жалобах.
    void UpdateHistory(IEnumerable<ConductSummary>? summaries)
    {
        try
        {
            bool changed = summaries != null && _history.Merge(summaries);
            changed |= _history.AttachMatches(_profile.Matches);
            if (!changed) return;
            _history.Save();
            _historyWindow?.Render();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            Log.Write("История поведения не сохранена: " + ex.Message);
        }
    }

    // Тренды меты и Dotabuff во встроенном браузере (тот же профиль WebView2, что и для GCPD)
    void OpenAnalytics(string? url = null)
    {
        if (_analytics == null)
        {
            _analytics = new AnalyticsWindow(_draftData, _browser?.WebEnvironment);
            _analytics.Closed += (_, _) => _analytics = null;
            _analytics.Show();
        }
        if (_analytics.WindowState == System.Windows.WindowState.Minimized) _analytics.WindowState = System.Windows.WindowState.Normal;
        _analytics.Activate();
        if (url != null) _analytics.Navigate(url);
    }

    void OpenHistory()
    {
        if (_historyWindow != null)
        {
            _historyWindow.Activate();
            return;
        }
        _historyWindow = new BehaviorHistoryWindow(_history, _draftData, url => OpenAnalytics(url));
        _historyWindow.Closed += (_, _) => _historyWindow = null;
        _historyWindow.Show();
        _historyWindow.Activate();
    }

    void RenderRating()
    {
        var lines = new List<(string, Color)>();
        int lp = _profile.LowPriorityGames;
        if (lp > 0) lines.Add(($"Low priority: осталось {lp} {Plural(lp, "игра", "игры", "игр")}", OverlayWindow.Red));
        string medal = Ranks.Name(_draftData.RankTier) ?? "неизвестен";
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
        if ((_latest ?? _history.Summaries.FirstOrDefault()) is { } latest)
            lines.Add(($"Поведение: {latest.Score}" + (latest.ScoreDelta is int d ? $" ({d:+#;-#;0})" : "") +
                       $" · похвал {latest.Commends} · жалоб {latest.Reports}", OverlayWindow.White));
        // тренд по накопленным сводкам: старые слева
        var trend = _history.Summaries.Where(s => s.Score != null).Take(20).Select(s => s.Score!.Value).Reverse().ToList();
        if (trend.Count >= 2)
            lines.Add(($"поведение за {trend.Count} {Plural(trend.Count, "сводку", "сводки", "сводок")}: {trend[0]} → {trend[^1]}", OverlayWindow.Gray));
        if (_profile.Loaded)
        {
            int w = _profile.AccountInt("Wins"), l = _profile.AccountInt("Losses");
            if (w + l > 0) lines.Add(($"Всего: {w}–{l} ({100.0 * w / (w + l):0.0}%)", OverlayWindow.Gray));
            int negative = _profile.AccountInt("NegativeReportsRemaining");
            bool noReports = negative == 0 && _profile.Account.ContainsKey("NegativeReportsRemaining");
            lines.Add(($"Можно отправить: жалоб {negative} · похвал {_profile.AccountInt("PositiveReportsRemaining")}", noReports ? OverlayWindow.Red : OverlayWindow.Gray));
        }
        // насколько прогноз по драфту совпадал с исходом — по матчам, исход которых уже есть в истории GCPD
        var (hits, predicted) = PredictionLog.Accuracy(_profile.Matches);
        if (predicted >= 5)
            lines.Add(($"Прогноз по драфту угадал {hits} из {predicted} ({100.0 * hits / predicted:0}%)", OverlayWindow.Gray));
        var updated = _profile.UpdatedAt is { } u ? $"данные Valve от {u:dd.MM HH:mm}" : "данные Valve ещё не загружены";
        _rating.Render("Рейтинг", lines, updated + " · Ctrl+Alt+S — скрыть", trend.Count >= 2 ? trend : null);
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

    static string Plural(int n, string one, string few, string many) => Words.Plural(n, one, few, many);

    void ShowLatest()
    {
        if ((_latest ?? _history.Summaries.FirstOrDefault()) is { } latest)
            ShowSummary(latest);
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

    // Вход в Steam слетел: окно входа, затем сводка жалоб и профиль.
    async Task LoginAsync()
    {
        await RefreshAsync(interactive: true, manual: true);
        await RefreshProfileAsync();
    }

    void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        _loginItem = new Forms.ToolStripMenuItem("Войти в Steam…", null, async (_, _) => await LoginAsync()) { Visible = false };
        menu.Items.Add(_loginItem);
        menu.Items.Add("Помощник в пике (Ctrl+Alt+D)", null, (_, _) => _draft.Toggle());
        menu.Items.Add("Рейтинг (Ctrl+Alt+S)", null, async (_, _) => await ToggleRatingAsync());
        menu.Items.Add("Последняя сводка (Ctrl+Alt+R)", null, (_, _) => ShowLatest());
        menu.Items.Add("Напоминания…", null, (_, _) => OpenReminders());
        menu.Items.Add("История поведения…", null, (_, _) => OpenHistory());
        menu.Items.Add("Аналитика и Dotabuff…", null, (_, _) => OpenAnalytics());
        menu.Items.Add("Папка с данными", null, (_, _) => Process.Start("explorer.exe", Paths.Root));
        menu.Items.Add("Токен Stratz…", null, async (_, _) =>
        {
            Setup.AskStratzToken();
            await LoadDraftAsync();
        });
        _updateItem = new Forms.ToolStripMenuItem($"Обновить DotaReports (у вас {Updater.Current})", null, async (_, _) => await UpdateAsync(manual: true));
        menu.Items.Add(_updateItem);
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
        _tray.BalloonTipClicked += async (_, _) =>
        {
            if (_tray.BalloonTipText == LoginTip) await LoginAsync();
        };
    }

    // Кнопка «Обновить» в трее: последний релиз с GitHub, файлы заменяет скрипт после выхода, программа запускается снова.
    // При запуске (manual: false) — только подсказка, если вышла новая версия.
    async Task UpdateAsync(bool manual)
    {
        Version? latest = null;
        try
        {
            latest = await Updater.LatestAsync();
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Log.Write("Проверка обновления: " + ex.Message);
        }
        var current = Updater.Current;
        if (latest != null && latest > current) _updateItem.Text = $"Обновить до {latest} (у вас {current})";
        if (!manual)
        {
            if (latest != null && latest > current)
                _tray.ShowBalloonTip(10000, "Dota Reports", $"Вышла версия {latest} — значок в трее → «Обновить до {latest}».", Forms.ToolTipIcon.Info);
            return;
        }
        if (latest == null)
        {
            _tray.ShowBalloonTip(6000, "Dota Reports", "GitHub не ответил — попробуйте обновиться позже.", Forms.ToolTipIcon.Warning);
            return;
        }
        if (latest <= current)
        {
            _tray.ShowBalloonTip(5000, "Dota Reports", $"У вас последняя версия {current}.", Forms.ToolTipIcon.Info);
            return;
        }
        if (System.Windows.MessageBox.Show($"Установить версию {latest}? Программа закроется и запустится снова; вход в Steam, история и настройки сохранятся.",
                "Dota Reports — обновление", System.Windows.MessageBoxButton.YesNo) != System.Windows.MessageBoxResult.Yes)
            return;
        var text = _updateItem.Text;
        _updateItem.Enabled = false;
        _updateItem.Text = "Скачиваю обновление…";
        try
        {
            await Updater.PrepareAsync();
            Exit(); // скрипт ждёт выхода, копирует файлы и запускает новую версию
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException
                                       or InvalidDataException or System.ComponentModel.Win32Exception)
        {
            Log.Write("Обновление не удалось: " + ex);
            _tray.ShowBalloonTip(8000, "Dota Reports — обновление", "Не удалось: " + ex.Message, Forms.ToolTipIcon.Error);
            _updateItem.Enabled = true;
            _updateItem.Text = text;
        }
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
        _historyWindow?.Close();
        _analytics?.Close();
        _draft.Close();
        _turboCard.Close();
        _rankStrip.Close();
        _rating.Close();
        _browser.Close();
        _app.Shutdown();
    }
}
