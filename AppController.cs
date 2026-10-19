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
    const int NecHotkeyId = 0xD07D;
    const uint VK_N = 0x4E;
    const uint VK_S = 0x53;
    const uint VK_R = 0x52;
    const uint VK_D = 0x44;
    // сколько держать окна — из настроек напоминаний (0 — пока не нажмут ✕), читаем в момент показа
    TimeSpan PopupTime => WindowDurations.Of(_reminders.Windows.Summary);
    static readonly TimeSpan GsiSilentMax = TimeSpan.FromMinutes(10);

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
    readonly SlotLearner _slotLearner;   // после матча — эталоны слотов, где союзники не узнались
    // Кастомка: три героя на выбор — с экрана раз в секунду; ваша игра там — в custom_stats.json по GSI.
    readonly DispatcherTimer _choiceTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    ChoiceReader? _choiceReader;
    bool _choiceBusy;
    int _choiceCard = -1;
    int[]? _choicesShown;
    string _customGame = "";
    GsiState? _customLast;   // последнее сообщение идущей кастомки, где есть ваш герой
    string? _customWinner;
    // Кастомка IM «Пробуждение»: что даёт герою на 0, 10 и 15 минуте — из её .vpk (Awakening.cs)
    Task<AwakeningInfo?> _awakening = Task.FromResult<AwakeningInfo?>(null);
    string? _awakeningFrom;                       // .vpk, из которого читали
    Dictionary<int, string> _heroNames = new();   // номер героя → npc_dota_hero_…
    int _awakeningShown = -1;                     // показано в этой игре: -1 — ничего, 0 — сводка, 1 и 2 — ступени 10 и 15 мин
    string? _customRemindersNote;                 // что последним записано в лог: будут ли напоминания в этой кастомке и почему
    readonly DispatcherTimer _frameTimer = new() { Interval = TimeSpan.FromSeconds(0.5) };
    TurboCard _turboCard = null!;
    PickFrame _pickFrame = null!;   // рамка вокруг «Бери» на самом экране пика
    RatingWindow _rating = null!;
    // лобби NEC: пока комната набирается — сразу и с каждым новым игроком (кэш NEC смотрим раз в 3 с),
    // при старте кастомки — снимок комнаты на весь матч (за 10 минут до старта); само прячется
    RatingWindow _necLobby = null!;
    readonly DispatcherTimer _necHide = new();
    readonly DispatcherTimer _necPoll = new() { Interval = TimeSpan.FromSeconds(3) };
    DateTime _necStamp;                     // когда NEC последний раз переписал кэш, который мы прочитали
    DateTime _necNewest = DateTime.Now;     // самый поздний игрок, по которому уже показывали (до запуска программы — не новость)
    List<NecPlayer>? _necSnapshot;
    DateTime _necSnapshotAt;
    bool _inCustom;
    enum NecShow { Hotkey, Fresh, Room, GameStart }
    DateTime _necDismissedAt;
    const int NecRoomMin = 3;   // меньше — не комната: поиск игрока, друзья, окно итогов матча
    const int RoomSize = 10;    // комната кастомки IM — 5 на 5, вы в том числе
    bool _necDismissed;          // лобби закрыли ✕ — само не показываем до следующей кастомки
    readonly DispatcherTimer _ratingHide = new();
    // Состав кастомки NEC иногда приходит уже после её загрузки (17.10: вторая пачка ников — на экране подключения),
    // поэтому до начала самой игры кэш NEC смотрим и в кастомке. Посреди игры NEC подгружает посторонних — тогда нет.
    static readonly HashSet<string> CustomBeforeGame = new()
    {
        "DOTA_GAMERULES_STATE_WAIT_FOR_PLAYERS_TO_LOAD", "DOTA_GAMERULES_STATE_CUSTOM_GAME_SETUP",
        "DOTA_GAMERULES_STATE_HERO_SELECTION", "DOTA_GAMERULES_STATE_STRATEGY_TIME",
    };
    // Звания под никами: на экране подключения и выбора героя узнаём игроков по аватарке (NecBadges.cs) раз в 1,5 с,
    // на выборе героя — раз в 0,5 с: карточки появляются — значки сразу
    RankBadges _badges = null!;
    readonly DispatcherTimer _badgeTimer = new() { Interval = BadgeTick };
    static readonly TimeSpan BadgeTick = TimeSpan.FromSeconds(1.5), BadgeTickCards = TimeSpan.FromSeconds(0.5);
    readonly AvatarMatcher _avatars = new();
    DateTime _badgeStamp;
    bool _badgeBusy;
    readonly Dictionary<string, NecPlayer> _onScreen = new();   // кто из комнаты NEC узнан на экране этой кастомки
    readonly Dictionary<string, int> _onScreenTeam = new();     // его команда (0 — Свет, 1 — Тьма), если видна на экране выбора команды
    readonly Dictionary<string, int> _onScreenSlot = new();     // его слот 0–9 (Свет 0–4, Тьма 5–9): строка «выбора команды» или своя карточка
    readonly DispatcherTimer _topBarTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    int[] _topLeft = Array.Empty<int>();                         // где ники в верхней полосе на стратегии кастомки (−1 — ещё не видно)
    readonly Dictionary<int, ScreenHit> _cardHits = new();      // выбор героя: кто узнан в карточке i — держим до конца экрана
    string? _gsiTeam;                                            // ваша сторона из GSI: radiant / dire
    // На пике в матче Valve (LiveMatch): медаль перед ником каждого игрока и «за · против» под ником — состав по
    // Steam ID сервера из console.log и Steam GetRealtimeStats; ники на экране смотрим раз в 2 с
    RankBadges _pickRanks = null!;
    readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    ulong? _liveServer;
    List<LivePlayer>? _livePlayers;
    DateTime _liveAsked;
    bool _liveBusy;
    string? _liveNote;   // что последним записано в журнал — пишем только перемены
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
    // Dota присылает данные хоть раз с запуска программы; нет — через 10 минут подсказать, почему
    DateTime? _lastGsi;
    bool _gsiHintShown;

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
    Forms.ToolStripMenuItem _imbaItem = null!;  // галочка «Оставаться на итогах IMBA»
    bool _imbaSyncing;
    ImbaStay.State? _imbaLogged; // в журнал — только смена состояния карты
    string? _imbaError;          // и только новая ошибка, а не каждую минуту одна и та же
    const string LoginTip = "Нужно войти в Steam: нажмите на это уведомление или значок в трее → «Войти в Steam…».";

    public AppController(System.Windows.Application app)
    {
        _app = app;
        _slotLearner = new SlotLearner(_watcher);
        // «DotaReports.exe --exit» из другой копии: обычный выход с закрытием WebView2 (куки входа в Steam сохраняются)
        var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ExitEventName);
        ThreadPool.RegisterWaitForSingleObject(exitEvent, (_, _) => _app.Dispatcher.BeginInvoke(Exit), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public async Task StartAsync()
    {
        Directory.CreateDirectory(Paths.Root);
        ReminderSound.Use(_reminders.Sounds); // звуки напоминаний — из настроек (микшер)
        _ = RankArt.EnsureAsync(); // значки медалей Dota для лобби NEC — один раз в папку данных
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
        _reminderOverlay = new OverlayWindow(top: 330, backgroundAlpha: 0xCC, pill: true); // полупрозрачная «таблетка»: игра под ней видна
        _draft = new DraftWindow(_draftData) { OpenUrl = url => OpenAnalytics(url) };
        _turboCard = new TurboCard();
        _pickFrame = new PickFrame();
        _turboCard.CloseButtonPressed += () => _pickFrame.ShowAround(null); // закрыли карточку — и рамку «Бери» тоже
        _rating = new RatingWindow();
        _necLobby = new RatingWindow(width: 380);
        _necLobby.CloseButtonPressed = () =>
        {
            _necHide.Stop();
            (_necDismissed, _necDismissedAt) = (true, DateTime.Now);
        };
        _rating.CloseButtonPressed = () => _ratingHide.Stop();
        _ratingHide.Tick += (_, _) =>
        {
            _ratingHide.Stop();
            if (_rating.IsVisible) _rating.Toggle();
        };
        _necHide.Tick += (_, _) =>
        {
            _necHide.Stop();
            if (_necLobby.IsVisible) _necLobby.Toggle();
        };
        _necPoll.Tick += (_, _) => CheckNecRoom();
        _badges = new RankBadges();
        _badgeTimer.Tick += async (_, _) => await CheckBadgesAsync();
        _pickRanks = new RankBadges();
        _liveTimer.Tick += async (_, _) => await CheckLiveRanksAsync();
        _topBarTimer.Tick += async (_, _) => await ShowTopBarAsync();
        _necPoll.Start();
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
        _choiceTimer.Tick += async (_, _) => await CheckChoicesAsync();
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
    }

    // Пик начался — карточка и запись кадров; закончился — убрать.
    void OnGsi(GsiState s)
    {
        _lastGsi = DateTime.Now;
        _watcher.Update(s);
        _mode.Update(s);
        if (!string.IsNullOrEmpty(s.Team)) _gsiTeam = s.Team;
        if (s.GameState.Length == 0 && _gameState is "DOTA_GAMERULES_STATE_POST_GAME" or "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS")
            _profileTimer.Start();
        // В кастомке (Аркада) свой экран выбора и свои герои — обычная карточка советовала бы мимо (Spectre при выборе
        // из трёх своих); там своя — кого из трёх брать по вашим играм (TrackCustom).
        bool custom = !string.IsNullOrEmpty(s.CustomGame);
        if (custom && !_inCustom)
        {
            _onScreen.Clear();
            _onScreenTeam.Clear();
            _onScreenSlot.Clear();
            ShowNecLobby(NecShow.GameStart); // кастомка загрузилась — кто был в комнате NEC перед ней
        }
        if (!custom && _inCustom) _necDismissed = false; // кастомка кончилась — следующую комнату снова показываем
        TrackBadges(custom, s.GameState, s.ClockTime);
        TrackTopBar(custom, s.GameState, s.ClockTime);
        _inCustom = custom;
        if (custom && s.GameState == "DOTA_GAMERULES_STATE_HERO_SELECTION" && _gameState != s.GameState)
        {
            Log.Write("Кастомная игра " + Path.GetFileNameWithoutExtension(s.CustomGame) + $" (карта {s.MapName}): выбор из трёх — по вашим играм; " +
                      (s.MapName != "dota" ? "напоминаний нет — карта не обычная"
                          : _reminders.Pick(ReminderSettings.CustomMode) is { } chosen ? $"напоминания — профиль «{chosen.Name}»" : "напоминания — общие тайминги")
                      + "; прогноза нет");
            _awakeningShown = -1;
            _customRemindersNote = null;
            _awakeningFrom = null; // кастомку могли обновить — перечитать (неизменённый файл берётся из памяти)
            LoadAwakening(s.CustomGame);
        }
        TrackCustom(s, custom);
        _gameState = s.GameState;
        CheckReminders(s);
        // карточка — и в пике, и во время стратегии: тогда видны все герои, и прогноз по драфту полный
        bool picking = !custom && (s.GameState is "DOTA_GAMERULES_STATE_HERO_SELECTION" or "DOTA_GAMERULES_STATE_STRATEGY_TIME");
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
            _turboCard.ResetDismissed(); // новый пик — карточка снова видна, даже если в прошлом её закрыли ✕
            _turboCard.Refresh = RenderCard;
            if (s.OwnGame) StartLiveRanks();
            if (_draftData.TurboReady)
            {
                _turboCard.Render(_draftData, _lastSnap, _watcher.Status, _ownHero);
                _turboCard.ShowCard();
                UpdatePickFrame();
            }
        }
        else
        {
            _frameTimer.Stop();
            _watcher.End();
            _turboCard.Hide();
            _pickFrame.ShowAround(null);
            StopLiveRanks();
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
        UpdatePickFrame();
    }

    void StartLiveRanks()
    {
        (_liveServer, _livePlayers, _liveAsked, _liveNote) = (null, null, DateTime.MinValue, null);
        if (!Setup.ScreenSupported) return;
        if (!LiveMatch.HasKey)
        {
            LiveNote("нет ключа Steam API (apikey.txt в папке данных) — звания на пике не показываем");
            return;
        }
        _liveTimer.Start();
    }

    void StopLiveRanks()
    {
        _liveTimer.Stop();
        _pickRanks.ShowBadges(Array.Empty<BadgeSpot>());
    }

    void LiveNote(string note)
    {
        if (note == _liveNote) return;
        _liveNote = note;
        Log.Write("Звания на пике: " + note);
    }

    // Пик в матче Valve: состав — раз (Steam отдаёт его не сразу — спрашиваем раз в 6 с), ники на экране — каждые 2 с.
    // В All Pick звания Dota показывает сама — там только «за · против».
    async Task CheckLiveRanksAsync()
    {
        if (_liveBusy) return;
        _liveBusy = true;
        try
        {
            if (_livePlayers == null)
            {
                if (DateTime.Now - _liveAsked < TimeSpan.FromSeconds(6)) return;
                _liveAsked = DateTime.Now;
                if (_liveServer == null)
                {
                    // подключение к серверу — до пика; давнее — это прошлый матч (или кастомка по IP)
                    var last = await Task.Run(LiveMatch.LastServer);
                    if (last is not { } srv || DateTime.Now - srv.At > TimeSpan.FromMinutes(15))
                    {
                        LiveNote("в console.log нет свежего подключения к серверу Valve");
                        _liveTimer.Stop();
                        return;
                    }
                    _liveServer = srv.Server;
                }
                var list = await LiveMatch.PlayersAsync(_liveServer.Value);
                if (list == null)
                {
                    LiveNote($"Steam пока не отдаёт состав сервера {_liveServer} (ответ — realtime_stats.json)");
                    return;
                }
                var peersTask = LiveMatch.PeersAsync();
                var ranks = await Task.WhenAll(list.Select(p => LiveMatch.RankAsync(p.Account)));
                var peers = await peersTask;
                _livePlayers = list.Select((p, i) =>
                {
                    var (with, against) = peers.GetValueOrDefault(p.Account);
                    return new LivePlayer(p.Slot, p.Account, p.Name, ranks[i], with, against);
                }).ToList();
                LiveNote($"игроков {_livePlayers.Count}, с медалью {_livePlayers.Count(p => p.RankTier > 0)}, " +
                         $"знакомых {_livePlayers.Count(p => p.With + p.Against > 0)}: " +
                         string.Join(", ", _livePlayers.Select(p => $"{p.Slot}:{p.Name}={p.RankTier}")));
            }
            var players = _livePlayers;
            var left = await Task.Run(() =>
            {
                var band = TopBarNames.Band;
                using var bmp = new System.Drawing.Bitmap(band.Width, band.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                    g.CopyFromScreen(band.Location, System.Drawing.Point.Empty, band.Size);
                return TopBarNames.TextLeft(bmp);
            });
            if (!_liveTimer.IsEnabled) return; // пик уже кончился
            _pickRanks.ShowBadges(TopBarNames.Spots(players, left, medals: _mode.Mode != "ALL_PICK"));
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or IOException or System.Text.Json.JsonException
                                       or System.Runtime.InteropServices.ExternalException or System.ComponentModel.Win32Exception)
        {
            LiveNote("ошибка: " + ex.Message);
        }
        finally
        {
            _liveBusy = false;
        }
    }

    // «Бери» — ещё и рамкой вокруг клетки героя в сетке, пока вы сами не выбрали (и карточку не закрыли ✕)
    void UpdatePickFrame() =>
        _pickFrame.ShowAround(_picking && _ownHero == 0 && _turboCard.Recommended > 0 && !_turboCard.Dismissed && Setup.ScreenSupported
            ? _watcher.CellOf(_turboCard.Recommended) : null);

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
        _overlay.ShowPopup((chance.KnownEnemies == 0 ? "Ваш драфт" : "Шанс победы") + $" ≈ {chance.P:0%}", color, lines,
            WindowDurations.Of(_reminders.Windows.Prediction), color);
        // в журнал — только когда враг известен: иначе это не прогноз матча
        if (chance.KnownEnemies >= 3 && s.MatchId is { Length: > 0 } id && id != "0")
            PredictionLog.Add(new Prediction(id, mode, chance.P, chance.KnownAllies, chance.KnownEnemies, DateTime.Now, chance.Logit));
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

    // Кастомка: на экране выбора — карточка «кого из трёх брать»; конец игры (POST_GAME или сразу меню — тогда без
    // map, уже не «кастомка») — запись в custom_stats.json: герой, исход, K/D/A, GPM, XPM, минуты.
    void TrackCustom(GsiState s, bool custom)
    {
        bool choosing = custom && (s.GameState is "DOTA_GAMERULES_STATE_HERO_SELECTION" or "DOTA_GAMERULES_STATE_STRATEGY_TIME");
        if (choosing && !_choiceTimer.IsEnabled && s.TeamSlot is >= 0 and < 5 && Setup.ScreenSupported)
        {
            _customGame = Path.GetFileNameWithoutExtension(s.CustomGame) ?? "";
            _choiceCard = s.TeamSlot;
            _choicesShown = null;
            _turboCard.ResetDismissed();
            _turboCard.Refresh = () => _choicesShown = null; // ↻ — перечитать три героя с экрана на следующем такте
            _choiceTimer.Start();
            _roshan.Reset(); // номер матча в кастомке всегда 0 — по нему новую игру не отличить
        }
        else if (!choosing && _choiceTimer.IsEnabled)
        {
            _choiceTimer.Stop();
            if (_choicesShown == null && _choiceReader is { LastScores.Length: > 0 } reader)
                Log.Write("Кастомка: три героя на выбор не узнаны (" + reader.LastScores + ")");
            _turboCard.Hide();
        }

        if (custom && (s.WinTeam is "radiant" or "dire")) _customWinner = s.WinTeam;
        if (custom && s.GameState == "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS")
        {
            _customGame = Path.GetFileNameWithoutExtension(s.CustomGame) ?? "";
            if (s.HeroId > 0) _customLast = s;
            return;
        }
        if (_customLast is not { } last) return;
        _customLast = null;
        int minutes = (last.ClockTime ?? 0) / 60;
        bool? win = _customWinner == null ? null : string.Equals(_customWinner, last.Team, StringComparison.OrdinalIgnoreCase);
        _customWinner = null;
        if (minutes < 5) return; // вышли в начале — не игра
        CustomStats.Add(new CustomGameRecord(_customGame, DateTime.Now, last.HeroId, win, last.Kills, last.Deaths, last.Assists, last.Gpm, last.Xpm, minutes));
        Log.Write($"Кастомка {_customGame}: герой {last.HeroId}, {(win == true ? "победа" : win == false ? "поражение" : "исход неизвестен")}, " +
                  $"{last.Kills}/{last.Deaths}/{last.Assists}, {last.Gpm} зол/мин, {minutes} мин");
    }

    async Task CheckChoicesAsync()
    {
        if (_choiceBusy || _choiceCard < 0) return;
        _choiceBusy = true;
        try
        {
            _choiceReader ??= await Task.Run(() => new ChoiceReader()); // портреты читаются один раз, не в потоке окна
            var reader = _choiceReader;
            if (!reader.Enabled) return;
            int card = _choiceCard;
            var heroes = await Task.Run(() => reader.ReadScreen(card));
            if (heroes == null || !_choiceTimer.IsEnabled || (_choicesShown != null && heroes.SequenceEqual(_choicesShown))) return;
            _choicesShown = heroes;
            Log.Write($"Кастомка: на выбор {string.Join(", ", heroes)} ({reader.LastScores})");
            var awakening = await _awakening;
            if (!_choiceTimer.IsEnabled) return;
            _turboCard.RenderChoices(_draftData, heroes, _customGame, id => _heroNames.TryGetValue(id, out var n) ? awakening?.Of(n) : null);
            _turboCard.ShowChoices(); // «Бери» обведён в самой полосе — рядом с тем, что даёт Пробуждение, а не на картинке
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            Log.Write("Выбор из трёх не прочитан: " + ex.Message);
        }
        finally
        {
            _choiceBusy = false;
        }
    }

    // Часы матча идут в GSI (map.clock_time) — те же, что на экране: до горна меньше нуля, на паузе стоят.
    void CheckReminders(GsiState s)
    {
        bool inGame = s.GameState is "DOTA_GAMERULES_STATE_PRE_GAME" or "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS";
        bool custom = !string.IsNullOrEmpty(s.CustomGame);
        // в кастомке — в лог, будут ли напоминания (и снова, только если это изменилось): если молчат, там видно почему
        if (custom && inGame)
        {
            string note = !_reminders.Enabled ? "выключены в настройках"
                : !s.OnDotaMap ? $"не показываются — карта {s.MapName}, герой {s.HeroId}, сторона {s.Team}{(s.Spectating ? ", просмотр" : "")}"
                : s.ClockTime == null ? "не показываются — Dota не присылает часы матча"
                : _reminders.Pick(ReminderSettings.CustomMode) is { } chosen ? $"по профилю «{chosen.Name}»" : "по общим таймингам";
            if (note != _customRemindersNote) Log.Write("Кастомка: напоминания " + note);
            _customRemindersNote = note;
        }
        // только своя игра на обычной карте (и кастомка на карте dota): не просмотр чужой (DotaTV, повтор), герой выбран
        if (!inGame || !s.OnDotaMap || s.ClockTime is not int clock || !_reminders.Enabled)
        {
            _reminderEngine.Reset();
            return;
        }
        // в кастомке режим не определяем (доход и экран там свои): профиль с режимом «Кастомка», нет его — общие тайминги;
        // Рошан — как в обычной игре
        string? mode = custom ? null : _mode.Mode;
        if (_roshan.Update(s, clock, mode) is { } roshan && _reminders.Roshan)
        {
            Log.Write($"Рошан: показано «{roshan.Title}» на {ReminderTime.Format(clock)}");
            _reminderOverlay.ShowPopup(roshan.Title, OverlayWindow.White, new[] { (roshan.Detail, OverlayWindow.Gray) }, WindowDurations.Of(_reminders.Windows.Reminder));
            if (_reminders.Sound) ReminderSound.Play(_reminders.RoshanSound);
        }
        if (custom) CheckAwakening(s, clock);
        // режим не определён — только общие для всех профилей тайминги, а не первый профиль
        var profile = _reminders.Pick(custom ? ReminderSettings.CustomMode : mode);
        // общие тайминги — только пока режим не ясен; ясен, но профиля под него нет — молчим
        var rules = profile ?? (mode == null ? _reminders.CommonRules() : null);
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
        lines.Add((profile != null ? $"профиль «{profile.Name}»" : custom ? "кастомка — общие тайминги (профиля «Кастомка» нет)" : "режим ещё не определён — только общие тайминги",
            OverlayWindow.Gray));
        var color = due.Count == 1 ? due[0].Rule.DisplayColor() : OverlayWindow.White;
        _reminderOverlay.ShowPopup(due.Count == 1 ? due[0].Rule.Text : "Напоминания", color, lines, WindowDurations.Of(_reminders.Windows.Reminder),
            due.Count == 1 ? color : due[0].Rule.DisplayColor());
        if (_reminders.Sound) ReminderSound.Play(due[0].Rule.SoundKey()); // несколько сразу — звук первого
    }

    // Описания Пробуждения читаются в фоне: .vpk кастомки — сотни мегабайт, но нужен из него один файл
    void LoadAwakening(string? vpk)
    {
        if (string.IsNullOrEmpty(vpk) || vpk == _awakeningFrom) return;
        _awakeningFrom = vpk;
        if (_heroNames.Count == 0) _heroNames = _draftData.HeroInternalNames();
        _awakening = Task.Run(() => AwakeningInfo.Load(vpk, AwakeningInfo.AbilityNames()));
    }

    // Пробуждение (кастомка IM): в начале игры — сводка, что даст ваш герой на 0, 10 и 15 минуте; за 30 с до 10:00
    // и 15:00 — что именно сейчас получите. Окно 8 с — до напоминаний в 9:40 и 14:50 оно уже скроется.
    void CheckAwakening(GsiState s, int clock)
    {
        const int Lead = 30;
        if (!_reminders.Awakening) return;
        LoadAwakening(s.CustomGame);
        if (!_awakening.IsCompletedSuccessfully || _awakening.Result?.Of(s.HeroName) is not { } stages) return;
        string hero = _draftData.Heroes.GetValueOrDefault(s.HeroId, s.HeroName ?? "");
        var color = ReminderColors.Of("blue");
        string Label(int k) => k == 0 ? "сразу" : $"в {AwakeningInfo.StageMinutes[k]}:00";
        if (_awakeningShown < 0 && clock < 60)
        {
            _awakeningShown = 0;
            var lines = Enumerable.Range(0, stages.Length).Where(k => stages[k].Count > 0)
                .Select(k => ($"{Label(k)}: {string.Join("; ", stages[k])}", OverlayWindow.White)).ToList();
            if (lines.Count == 0) return;
            lines.Add(("кастомка IM — перед 10:00 и 15:00 напомню", OverlayWindow.Gray));
            _reminderOverlay.ShowPopup($"Пробуждение: {hero}", color, lines, WindowDurations.Of(_reminders.Windows.AwakeningSummary), color);
            return;
        }
        for (int k = 1; k < stages.Length; k++)
        {
            int at = AwakeningInfo.StageMinutes[k] * 60;
            if (_awakeningShown >= k || clock < at - Lead || clock >= at) continue;
            _awakeningShown = k;
            if (stages[k].Count == 0) return;
            var lines = stages[k].Select(e => (e, OverlayWindow.White)).ToList();
            lines.Add(($"{hero} · через {ReminderTime.Format(at - clock)} · {Label(k)}", OverlayWindow.Gray));
            Log.Write($"Пробуждение: показана ступень {k} на {ReminderTime.Format(clock)}");
            _reminderOverlay.ShowPopup("Пробуждение — новые усиления", color, lines, WindowDurations.Of(_reminders.Windows.AwakeningStage), color);
            if (_reminders.Sound) ReminderSound.Play(_reminders.AwakeningSound);
            return;
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
        if (!_picking) await _slotLearner.TickAsync();
        if (!running) await SyncImbaStayAsync(); // карта обновилась в Steam — правка слетела, поставить заново
    }

    // Галочка в трее: карта IMBA AWAKEN не выкидывает в главное меню после матча. Файл карты меняется только при закрытой Dota.
    async Task ToggleImbaStayAsync()
    {
        try
        {
            ImbaStay.Wanted = !ImbaStay.Wanted;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("imba_stay.txt не сохранён: " + ex.Message);
        }
        _imbaItem.Checked = ImbaStay.Wanted;
        if (Setup.DotaStartedAt() != null)
        {
            _tray.ShowBalloonTip(6000, "Dota Reports", "Применится, когда закроете Dota: пока она открыта, файл карты IMBA не меняю.", Forms.ToolTipIcon.Info);
            return;
        }
        var (text, icon) = await SyncImbaStayAsync() switch
        {
            ImbaStay.State.On => ("Готово: после матча IMBA остаётесь на экране итогов. NEC всё равно свернёт Dota — вернитесь в неё через Alt+Tab.", Forms.ToolTipIcon.Info),
            ImbaStay.State.Off => ("Готово: карта IMBA снова как в оригинале.", Forms.ToolTipIcon.Info),
            ImbaStay.State.NoMap => ("Карта IMBA AWAKEN не установлена — правка встанет, когда она скачается.", Forms.ToolTipIcon.Info),
            ImbaStay.State.Unknown => ("Эта версия карты IMBA не подходит — правку не ставлю.", Forms.ToolTipIcon.Warning),
            _ => ("Файл карты IMBA сейчас занят — попробую через минуту. Подробности в журнале.", Forms.ToolTipIcon.Warning),
        };
        _tray.ShowBalloonTip(8000, "Dota Reports", text, icon);
    }

    // Привести карту к галочке: включить правку или вернуть оригинал. null — файл занят или не прочитался.
    async Task<ImbaStay.State?> SyncImbaStayAsync()
    {
        if (_imbaSyncing) return null;
        _imbaSyncing = true;
        try
        {
            bool on = ImbaStay.Wanted;
            var state = await Task.Run(() => ImbaStay.FindVpk() is { } vpk ? ImbaStay.Sync(vpk, on) : ImbaStay.State.NoMap);
            if (state == ImbaStay.State.Unknown && _imbaLogged != state)
                Log.Write("Итоги IMBA: версия карты незнакомая, правку не ставлю");
            _imbaLogged = state;
            _imbaError = null;
            return state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (_imbaError != ex.Message) Log.Write("Итоги IMBA: файл карты не изменён: " + ex.Message);
            _imbaError = ex.Message;
            return null;
        }
        finally
        {
            _imbaSyncing = false;
        }
    }

    // Dota открыта десять минут, а данных GSI нет ни разу — один раз объяснить причину.
    // Раньше нельзя: пока Dota грузится и обновляется, GSI молчит (бывает и 6–7 минут).
    void CheckGsiAlive()
    {
        if (_lastGsi != null || _gsiHintShown || Setup.DotaStartedAt() is not { } started || DateTime.Now - started < GsiSilentMax) return;
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
        string medal = Ranks.Name(_draftData.RankTier) ?? "неизвестен";
        var cal = _profile.Calibration();
        (int, int, int, int)? today = null;
        (int, bool)? streak = null;
        (int, int)? total = null;
        (int, int, bool)? remaining = null;
        if (_profile.Loaded)
        {
            var t = _profile.Today();
            today = (t.Games, t.Wins, t.RankedGames, t.RankedWins);
            if (_profile.Streak() is { } st) streak = (st.Count, st.Wins);
            total = (_profile.AccountInt("Wins"), _profile.AccountInt("Losses"));
            int negative = _profile.AccountInt("NegativeReportsRemaining");
            remaining = (negative, _profile.AccountInt("PositiveReportsRemaining"), negative == 0 && _profile.Account.ContainsKey("NegativeReportsRemaining"));
        }
        var latest = _latest ?? _history.Summaries.FirstOrDefault();
        // тренд по накопленным сводкам: старые слева
        var trend = _history.Summaries.Where(s => s.Score != null).Take(20).Select(s => s.Score!.Value).Reverse().ToList();
        // насколько прогноз по драфту совпадал с исходом — по матчам, исход которых уже есть в истории GCPD
        var (hits, predicted) = PredictionLog.Accuracy(_profile.Matches);
        var view = new RatingView(
            _profile.LowPriorityGames, medal,
            cal is { } c ? (c.Games, c.Wins, c.AtLeast) : null,
            cal != null ? _confidence : null,
            today, streak,
            latest != null ? (latest.Score, latest.ScoreDelta, latest.Commends, latest.Reports) : null,
            trend, total, remaining, (hits, predicted),
            _profile.UpdatedAt is { } u ? $"данные Valve от {u:dd.MM HH:mm}" : "данные Valve ещё не загружены");
        _rating.RenderRating(view, () => _ = RefreshRatingAsync());
    }

    // ↻ в окне рейтинга: перечитать профиль Valve (и процент калибровки, если профиль Dota открыт).
    async Task RefreshRatingAsync()
    {
        await CheckConfidenceAsync();
        await RefreshProfileAsync();
        if (_rating.IsVisible) RenderRating();
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

    // Комната NEC набирается (до запуска кастомки): NEC пишет на диск ники и профили приходящих — показываем лобби сразу
    // и обновляем с каждым новым игроком. Перечитываем, только когда NEC переписал кэш.
    void CheckNecRoom()
    {
        if ((_inCustom && !CustomBeforeGame.Contains(_gameState)) || !NecLobby.Installed) return;
        var stamp = NecLobby.Stamp();
        if (stamp == _necStamp) return;
        _necStamp = stamp;
        var players = NecLobby.Load(DateTime.Now);
        var newest = players.Count > 0 ? players.Max(p => p.SeenAt) : DateTime.MinValue;
        if (newest <= _necNewest) return;
        _necNewest = newest;
        // закрыли ✕ раньше, а теперь в комнате только пришедшие после — это уже другая комната
        if (_necDismissed && players.Count > 0 && players.All(p => p.SeenAt > _necDismissedAt)) _necDismissed = false;
        // кастомка: кого-то уже узнали на экране — карточка по экрану (ShowScreenLobby), кэш NEC её не перебивает,
        // иначе число игроков скачет между двумя составами
        if (_inCustom && _onScreen.Count > 0) return;
        if (players.Count >= NecRoomMin) ShowNecLobby(_inCustom ? NecShow.GameStart : NecShow.Room, players);
    }

    // Стратегия в кастомке: медаль перед ником в верхней полосе — у узнанных на экране, по их слоту; как и у карточек,
    // за 2 с до конца стадии убираем.
    void TrackTopBar(bool custom, string state, int? clock)
    {
        bool show = custom && state == "DOTA_GAMERULES_STATE_STRATEGY_TIME" && clock is not >= -2 && Setup.ScreenSupported && _onScreenSlot.Count > 0;
        if (show && !_topBarTimer.IsEnabled)
        {
            _topLeft = Enumerable.Repeat(-1, 10).ToArray();
            _topBarTimer.Start();
            _ = ShowTopBarAsync(); // сразу, не дожидаясь такта
        }
        else if (!show && _topBarTimer.IsEnabled)
        {
            _topBarTimer.Stop();
            _pickRanks.ShowBadges(Array.Empty<BadgeSpot>());
        }
    }

    // Где ники — читаем, пока не найдены: потом на их месте наша же медаль (снимок экрана её видит) и сбила бы край ника.
    async Task ShowTopBarAsync()
    {
        if (_topBarBusy) return;
        _topBarBusy = true;
        try
        {
            if (_topLeft.Any(x => x < 0))
            {
                var left = await Task.Run(() =>
                {
                    var band = TopBarNames.Band;
                    using var bmp = new System.Drawing.Bitmap(band.Width, band.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                        g.CopyFromScreen(band.Location, System.Drawing.Point.Empty, band.Size);
                    return TopBarNames.TextLeft(bmp);
                });
                for (int s = 0; s < 10; s++)
                    if (_topLeft[s] < 0) _topLeft[s] = left[s];
            }
            if (!_topBarTimer.IsEnabled) return; // стадия уже кончилась
            var players = _onScreenSlot.Where(kv => _onScreen.ContainsKey(kv.Key)).Select(kv =>
            {
                var p = _onScreen[kv.Key];
                return new LivePlayer(kv.Value, NecLobby.AccountOf(p.SteamId) ?? 0, p.Name, p.RankTier);
            }).ToList();
            _pickRanks.ShowBadges(TopBarNames.Spots(players, _topLeft));
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            Log.Write("Кастомка: ники в верхней полосе не прочитаны: " + ex.Message);
        }
        finally
        {
            _topBarBusy = false;
        }
    }

    bool _topBarBusy;

    // Экран подключения и выбор героя в кастомке — плашки со званием у узнанных по аватарке; потом — убрать,
    // а узнанных на экране (если их хотя бы трое) оставить составом матча для Ctrl+Alt+N.
    // Карточки выбора героя видны, пока идут его часы (от −60 до 0), но за 2 с до конца уже уезжают — значки убираем
    // тогда же; на стратегии карточек нет (плашки висели над её экраном).
    void TrackBadges(bool custom, string state, int? clock)
    {
        bool screen = custom && (state == "DOTA_GAMERULES_STATE_CUSTOM_GAME_SETUP"
                                 || state == "DOTA_GAMERULES_STATE_HERO_SELECTION" && clock is not >= -2);
        var tick = state == "DOTA_GAMERULES_STATE_HERO_SELECTION" ? BadgeTickCards : BadgeTick;
        if (_badgeTimer.Interval != tick) _badgeTimer.Interval = tick; // присвоение перезапускает таймер — только при смене
        // начался выбор героя — карточки прочитать сразу, не дожидаясь такта
        if (screen && state == "DOTA_GAMERULES_STATE_HERO_SELECTION" && _gameState != state && _badgeTimer.IsEnabled)
            _ = CheckBadgesAsync(state); // _gameState ещё прежний
        // выбор героя: карточка лобби слева закрыла бы карточку первого союзника — звания уже у аватарок
        if (custom && state == "DOTA_GAMERULES_STATE_HERO_SELECTION" && _necHide.IsEnabled)
        {
            _necHide.Stop();
            if (_necLobby.IsVisible) _necLobby.Toggle();
        }
        if (screen && !_badgeTimer.IsEnabled && Setup.ScreenSupported && NecLobby.Installed)
            _badgeTimer.Start();
        else if (!screen && _badgeTimer.IsEnabled)
        {
            _badgeTimer.Stop();
            _cardHits.Clear();
            _badges.ShowBadges(Array.Empty<BadgeSpot>());
            if (_onScreen.Count >= NecRoomMin)
            {
                (_necSnapshot, _necSnapshotAt) = (_onScreen.Values.ToList(), DateTime.Now);
                Log.Write($"NEC: на экране кастомки узнаны {_onScreen.Count}: {string.Join(", ", _onScreen.Values.Select(p => p.Name))}");
            }
        }
    }

    async Task CheckBadgesAsync(string? state = null)
    {
        if (_badgeBusy) return;
        _badgeBusy = true;
        try
        {
            bool setup = (state ?? _gameState) == "DOTA_GAMERULES_STATE_CUSTOM_GAME_SETUP";
            var found = await Task.Run(() =>
            {
                // кандидаты — все, кого NEC держит в кэше (и друзья из профилей Steam): знакомых игроков NEC заново
                // не записывает, и по свежим записям из 10 в комнате ловятся 5–6
                var stamp = NecLobby.Stamp();
                if (stamp != _badgeStamp || _avatars.Count == 0)
                {
                    _badgeStamp = stamp;
                    _avatars.SetCandidates(NecLobby.Load(DateTime.Now, TimeSpan.FromMinutes(20), sinceMatch: false), NecLobby.Load(DateTime.Now, everyone: true),
                        NecLobby.OwnAvatars(), NecLobby.DefaultAvatarFile());
                }
                // до выбора героя экран бывает двух видов: список подключения из 10 и выбор команды по колонкам —
                // берём тот, где узнано больше игроков
                var layouts = setup ? new[] { NecLayout.Teams, NecLayout.Connect } : new[] { NecLayout.Cards };
                List<ScreenHit> best = new();
                foreach (var layout in layouts)
                {
                    var area = NecScreen.Strip(layout);
                    using var strip = new System.Drawing.Bitmap(area.Width, area.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
                    using (var g = System.Drawing.Graphics.FromImage(strip))
                        g.CopyFromScreen(area.Location, System.Drawing.Point.Empty, area.Size);
                    var read = NecScreen.Read(strip, layout, _avatars);
                    if (read.Count > best.Count) best = read;
                }
                return best;
            });
            if (!_badgeTimer.IsEnabled) return; // экран уже сменился
            // выбор героя: наша же медаль на углу аватарки попадает в снимок экрана и портит следующее чтение
            // (счёт 0,91–0,94 → 0,55–0,89, значок гаснет и снова загорается) — узнанного в карточке держим,
            // пока там уверенно не узнан другой
            if (!setup)
            {
                foreach (var f in found) _cardHits[f.Index] = f;
                found = _cardHits.Values.OrderBy(h => h.Index).ToList();
            }
            found = await NecScreen.WithRanksAsync(found); // у кого NEC не загружал профиль — звание с OpenDota
            if (!_badgeTimer.IsEnabled) return; // экран уже сменился
            bool grew = false;
            foreach (var f in found)
            {
                grew |= !_onScreen.ContainsKey(f.Player.SteamId);
                _onScreen[f.Player.SteamId] = f.Player;
                if (f.Team is int team) _onScreenTeam[f.Player.SteamId] = team;
                // слот — для медали у ника в верхней полосе на стратегии: строки «выбора команды» и карточки своей команды
                // идут по порядку слотов
                if (f.Layout == NecLayout.Teams) _onScreenSlot[f.Player.SteamId] = f.Index;
                else if (f.Layout == NecLayout.Cards && _gsiTeam is "radiant" or "dire")
                    _onScreenSlot[f.Player.SteamId] = (_gsiTeam == "dire" ? 5 : 0) + f.Index;
            }
            _badges.ShowBadges(found.SelectMany(NecScreen.Spots).ToList());
            if (grew && setup) ShowScreenLobby(); // до выбора героя: узнанные — сразу в карточку лобби
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException or System.ComponentModel.Win32Exception or ArgumentException)
        {
            Log.Write("NEC: экран кастомки не прочитан: " + ex.Message);
        }
        finally
        {
            _badgeBusy = false;
        }
    }

    // Узнанные по аватарке на экране кастомки — в карточку лобби, по командам (их видно на экране выбора команды);
    // к ним — те, кого NEC записал за 10 минут, но на экране не узнали (стандартная «?»-аватарка, ушедшие из комнаты).
    // Этот состав и остаётся для Ctrl+Alt+N на весь матч.
    void ShowScreenLobby()
    {
        var recognized = _onScreen.Values.ToList();
        // не узнанных добавляем, только пока в комнате есть места: в кэше за 10 минут и игроки прошлой комнаты
        var extra = NecLobby.Load(DateTime.Now).Where(p => !_onScreen.ContainsKey(p.SteamId))
            .OrderByDescending(p => p.SeenAt).Take(Math.Max(0, RoomSize - 1 - recognized.Count)).ToList();
        (_necSnapshot, _necSnapshotAt) = (recognized.Concat(extra).ToList(), DateTime.Now);
        if (_necDismissed) return; // закрыли ✕ — состав запомнили, но сами не лезем
        var body = new List<System.Windows.UIElement>();
        void Group(string title, IEnumerable<NecPlayer> players)
        {
            var list = players.ToList();
            if (list.Count == 0) return;
            body.Add(Glass.Text(title, 12, Glass.GoldLight, bold: true, top: body.Count > 0 ? 8 : 0));
            body.AddRange(NecLobby.Tiles(list, _draftData.Heroes));
        }
        int? mine = _gsiTeam switch { "radiant" => 0, "dire" => 1, _ => null };
        string Side(int t) => (t == 0 ? "Силы Света" : "Силы Тьмы") + (mine == t ? " — ваша команда" : "");
        int TeamOf(NecPlayer p) => _onScreenTeam.TryGetValue(p.SteamId, out var t) ? t : -1;
        Group(Side(0), recognized.Where(p => TeamOf(p) == 0));
        Group(Side(1), recognized.Where(p => TeamOf(p) == 1));
        Group(_onScreenTeam.Count > 0 ? "Команда не видна" : "Узнаны на экране", recognized.Where(p => TeamOf(p) < 0));
        Group("В комнате NEC, на экране не узнаны", extra);
        _necLobby.Render($"Лобби NEC · {recognized.Count + extra.Count}", "Ctrl+Alt+N", body,
            "узнаны по аватарке на экране кастомки · медали — OpenDota", RefreshNecLobby);
        if (_rating.IsVisible) _rating.Toggle(); // оба окна в одном углу
        if (!_necLobby.IsVisible) _necLobby.Toggle();
        _necHide.Stop();
        if (_reminders.Windows.NecGame <= 0) return;
        _necHide.Interval = WindowDurations.Of(_reminders.Windows.NecGame);
        _necHide.Start();
    }

    // Комната набирается (Room) — кто в ней сейчас; кастомка началась (GameStart) — кого NEC показывал за 10 минут до неё.
    // Ctrl+Alt+N в кастомке — тот же снимок (NEC и посреди матча подгружает посторонних), вне её — свежее за 10 минут,
    // а если пусто — снимок прошлого матча.
    void ShowNecLobby(NecShow mode, List<NecPlayer>? loaded = null)
    {
        if (!NecLobby.Installed)
        {
            if (mode == NecShow.Hotkey) _overlay.ShowPopup("Лобби NEC", OverlayWindow.White, new[] { ("NEC не установлен — нет папки %APPDATA%\\nec-platform.", OverlayWindow.Gray) }, PopupTime);
            return;
        }
        bool auto = mode is NecShow.Room or NecShow.GameStart;
        var at = DateTime.Now;
        List<NecPlayer> players;
        if (mode == NecShow.Hotkey && _inCustom && _necSnapshot != null)
            (players, at) = (_necSnapshot, _necSnapshotAt);
        else
        {
            players = loaded ?? NecLobby.Load(at);
            if (mode == NecShow.GameStart)
            {
                (_necSnapshot, _necSnapshotAt) = (players, at);
                Log.Write($"NEC: состав кастомки — в кэше NEC за 10 мин {players.Count} игроков");
                if (players.Count == 0) return;
            }
            else if (mode == NecShow.Hotkey && players.Count == 0 && _necSnapshot is { Count: > 0 })
                (players, at) = (_necSnapshot, _necSnapshotAt);
        }
        if (auto && _necDismissed) return; // закрыли ✕ — сами не лезем (Ctrl+Alt+N и ↻ — пожалуйста)

        var body = players.Count > 0
            ? NecLobby.Tiles(players, _draftData.Heroes).ToList()
            : new List<System.Windows.UIElement> { Glass.Text("За 10 минут NEC никого не показывал — откройте комнату или подбор в NEC.", 13, Glass.Muted) };
        string source = mode == NecShow.Room ? $"комната NEC, {at:HH:mm} — обновится с новыми игроками" : $"кэш NEC за 10 мин до {at:HH:mm}";
        _necLobby.Render($"Лобби NEC · {players.Count}", "Ctrl+Alt+N", body,
            $"{source} · без деления на команды, могут быть ушедшие", RefreshNecLobby);
        if (_rating.IsVisible) _rating.Toggle(); // оба окна в одном углу
        if (!_necLobby.IsVisible) _necLobby.Toggle();
        _necHide.Stop();
        if (!auto) return;
        int seconds = mode == NecShow.Room ? _reminders.Windows.NecRoom : _reminders.Windows.NecGame;
        if (seconds <= 0) return; // 0 — пока не закроют ✕
        _necHide.Interval = WindowDurations.Of(seconds);
        _necHide.Start();
    }

    // ↻ в лобби: до начала кастомки — узнанные на экране (и аватарки NEC — заново на следующем такте), а пока никого —
    // кэш NEC; посреди идущей кастомки — снимок с её начала (NEC тогда подгружает посторонних).
    void RefreshNecLobby()
    {
        if (_inCustom && CustomBeforeGame.Contains(_gameState) && _onScreen.Count > 0)
        {
            _badgeStamp = default;
            ShowScreenLobby();
            return;
        }
        ShowNecLobby(_inCustom && !CustomBeforeGame.Contains(_gameState) ? NecShow.Hotkey : NecShow.Fresh);
    }

    void ToggleNecLobby()
    {
        if (!_necLobby.IsVisible)
        {
            ShowNecLobby(NecShow.Hotkey);
            return;
        }
        _necHide.Stop();
        _necLobby.Toggle();
    }

    async Task ToggleRatingAsync()
    {
        if (_rating.IsVisible)
        {
            _ratingHide.Stop();
            _rating.Toggle();
            return;
        }
        if (_necLobby.IsVisible)
        {
            _necHide.Stop();
            _necLobby.Toggle();
        }
        RenderRating();
        _rating.Toggle();
        _ratingHide.Stop();
        if (_reminders.Windows.Rating > 0)
        {
            _ratingHide.Interval = WindowDurations.Of(_reminders.Windows.Rating);
            _ratingHide.Start();
        }
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
            lines, PopupTime, s.Reports > 0 ? OverlayWindow.Red : Glass.Gold,
            refresh: () => _ = RefreshAsync(interactive: false, manual: true)); // ↻ — проверить GCPD сейчас
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
        if (!Native.RegisterHotKey(_browser.Handle, NecHotkeyId, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, VK_N))
            Log.Write("Ctrl+Alt+N занята другой программой");
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
        else if (msg == Native.WM_HOTKEY && wParam.ToInt32() == NecHotkeyId)
        {
            ToggleNecLobby();
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
        menu.Items.Add("Лобби NEC (Ctrl+Alt+N)", null, (_, _) => ToggleNecLobby());
        _imbaItem = new Forms.ToolStripMenuItem("Оставаться на итогах IMBA", null, async (_, _) => await ToggleImbaStayAsync())
        {
            Checked = ImbaStay.Wanted,
            ToolTipText = "После матча IMBA AWAKEN карта не выкидывает в главное меню — итоги в самой игре.",
        };
        menu.Items.Add(_imbaItem);
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

    // Выход бывает и до конца запуска (обновление закрывает копию, которая ещё ждёт Dota) — окон и браузера тогда нет.
    void Exit()
    {
        _poll.Stop();
        _confTimer.Stop();
        _gsi.Stop();
        if (_browser != null)
        {
            Native.UnregisterHotKey(_browser.Handle, HotkeyId);
            Native.UnregisterHotKey(_browser.Handle, DraftHotkeyId);
            Native.UnregisterHotKey(_browser.Handle, RatingHotkeyId);
            Native.UnregisterHotKey(_browser.Handle, NecHotkeyId);
        }
        _necHide.Stop();
        _necPoll.Stop();
        _badgeTimer.Stop();
        _liveTimer.Stop();
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        _overlay?.Close();
        _pickFrame?.Close();
        _reminderOverlay?.Close();
        _reminderWindow?.Close();
        _historyWindow?.Close();
        _analytics?.Close();
        _draft?.Close();
        _turboCard?.Close();
        _rating?.Close();
        _necLobby?.Close();
        _badges?.Close();
        _pickRanks?.Close();
        _browser?.Close();
        _app.Shutdown();
    }
}
