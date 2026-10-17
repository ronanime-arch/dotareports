using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;

namespace DotaReports;

static class Program
{
    public const string ExitEventName = @"Local\DotaReports.Exit";

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--dump")
        {
            DumpTabs(args[1].Split(','), Path.Combine(Paths.Root, args.Length >= 3 ? args[2] : "dump_tabs.json"), args.Length >= 4 ? int.Parse(args[3]) : 3);
            return;
        }
        if (args.Length >= 3 && args[0] == "--grid-test")
        {
            GridTest(args[1], args[2], Path.Combine(Paths.Root, "grid_test.txt"));
            return;
        }
        if (args.Length >= 3 && args[0] == "--frame-test")
        {
            // рамка «Бери» на скриншоте 1920×1080, в frame_test.png: --frame-test <png> <герой> — клетка в сетке пика;
            // --frame-test <png> choice <слот 0–4> <картинка 0–2> — одна из трёх в кастомке
            var cell = args[2] == "choice" && args.Length >= 5
                ? ChoiceReader.ThumbArea(int.Parse(args[3]), int.Parse(args[4]))
                : new GridReader().CellOf(int.Parse(args[2]));
            if (cell is not { } c) return;
            using var shot = new System.Drawing.Bitmap(args[1]);
            using (var g = System.Drawing.Graphics.FromImage(shot))
            using (var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(0x7C, 0xD9, 0x92)))
            {
                var o = PickFrame.Outer(c);
                int t = PickFrame.Thickness;
                g.FillRectangle(brush, o.Left, o.Top, o.Width, t);
                g.FillRectangle(brush, o.Left, o.Bottom - t, o.Width, t);
                g.FillRectangle(brush, o.Left, o.Top, t, o.Height);
                g.FillRectangle(brush, o.Right - t, o.Top, t, o.Height);
            }
            shot.Save(Path.Combine(Paths.Root, "frame_test.png"));
            return;
        }
        if (args.Length >= 2 && args[0] == "--conf-test")
        {
            // уверенность в ранге с картинки (скриншот профиля целиком или вырезка с полосой)
            using var shot = new System.Drawing.Bitmap(args[1]);
            File.WriteAllText(Path.Combine(Paths.Root, "conf_test.txt"), ConfidenceReader.Read(shot)?.ToString() ?? "не найдено");
            return;
        }
        if (args.Length >= 2 && args[0] == "--rating-test")
        {
            // данные профиля из выгрузки GCPD (--dump) → что покажет окно рейтинга и пул героев
            var data = new DraftData();
            data.LoadAsync().GetAwaiter().GetResult();
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(args[1]));
            var tables = doc.RootElement.EnumerateObject().Where(p => p.Value.ValueKind == System.Text.Json.JsonValueKind.Object && p.Value.TryGetProperty("headers", out _))
                .ToDictionary(p => p.Name, p => p.Value.Clone());
            var profile = new ProfileData();
            profile.Apply(tables, data.Heroes, DateTime.Now);
            data.SetProfile(profile);
            var cal = profile.Calibration();
            var today = profile.Today();
            var streak = profile.Streak();
            var lines = new List<string>
            {
                $"матчей {profile.Matches.Count}, героев (свои) {profile.OnHero.Count}, (против) {profile.AgainstHero.Count}",
                $"калибровка: {(cal is { } c ? $"{c.Games} игр, {c.Wins} побед" : "нет")}; ранг OpenDota {data.RankTier}",
                $"сегодня: {today.Games} игр, {today.Wins} побед; рейтинг {today.RankedGames}/{today.RankedWins}",
                $"серия: {(streak is { } st ? $"{st.Count} {(st.Wins ? "побед" : "поражений")}" : "—")}",
                $"всего: {profile.AccountInt("Wins")}–{profile.AccountInt("Losses")}; можно жалоб {profile.AccountInt("NegativeReportsRemaining")}",
                $"low priority: {profile.LowPriorityGames}",
                "пул: " + string.Join(", ", data.Pool.OrderByDescending(kv => kv.Value.Games).Take(8).Select(kv => $"{data.Heroes.GetValueOrDefault(kv.Key)} {kv.Value.Games}/{kv.Value.Wins}")),
                "против: " + string.Join(", ", new[] { 14, 35, 26, 44 }.Select(h => profile.AgainstHero.TryGetValue(h, out var r) ? $"{data.Heroes.GetValueOrDefault(h)} {r.Wins}–{r.Losses}" : $"{h}: нет")),
            };
            File.WriteAllLines(Path.Combine(Paths.Root, "rating_test.txt"), lines);
            return;
        }
        if (args.Length >= 2 && args[0] == "--parse-test")
        {
            // last_table.json (или выгрузка одной таблицы) → что увидит проверка жалоб, в parse_test.txt
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(args[1]));
            var table = doc.RootElement.TryGetProperty("table", out var t) ? t : doc.RootElement;
            string result;
            if (GcpdReader.EmptyTable(table))
                result = "нет таблицы: " + GcpdReader.PageMessage(table);
            else
                try
                {
                    var summaries = ReportParser.Parse(table);
                    result = $"сводок {summaries.Count}" + Environment.NewLine + string.Join(Environment.NewLine, summaries);
                }
                catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
                {
                    result = "ошибка: " + ex.Message; // не тот файл или таблица без нужных колонок
                }
            File.WriteAllText(Path.Combine(Paths.Root, "parse_test.txt"), result);
            return;
        }
        if (args.Length >= 1 && args[0] == "--history-test")
        {
            // conduct_history.json (+ сводки из last_table.json, если передан) и кэш профиля → окна сводок, в history_test.txt.
            // Файл истории не меняется. «матч сводки в истории» проверяет правило окна: MatchID сводки — последний матч окна.
            var history = ConductHistory.Load();
            var lines = new List<string>();
            if (args.Length >= 2)
            {
                using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(args[1]));
                var table = doc.RootElement.TryGetProperty("table", out var t) ? t : doc.RootElement;
                if (table.ValueKind == System.Text.Json.JsonValueKind.Object && !GcpdReader.EmptyTable(table))
                    history.Merge(ReportParser.Parse(table));
                else
                    lines.Add("в дампе нет таблицы сводок: " + GcpdReader.PageMessage(table));
            }
            var heroes = new Dictionary<int, string>();
            if (File.Exists(Paths.Heroes))
            {
                using var heroDoc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Paths.Heroes));
                foreach (var h in heroDoc.RootElement.EnumerateArray())
                    heroes[h.GetProperty("id").GetInt32()] = h.GetProperty("localized_name").GetString() ?? "";
            }
            var profile = new ProfileData();
            profile.LoadCache(heroes);
            history.AttachMatches(profile.Matches);
            lines.Add($"сводок {history.Summaries.Count}, матчей в истории GCPD {profile.Matches.Count}");
            foreach (var s in history.Summaries)
            {
                var w = history.Window(s);
                ulong.TryParse(s.MatchId, out var id);
                var own = profile.Matches.FirstOrDefault(m => m.MatchId == id && id > 0);
                var next = profile.Matches.Where(m => m.MatchId > id).OrderBy(m => m.MatchId).FirstOrDefault();
                string anchor = own == null
                    ? "матч сводки в истории: нет"
                    : $"матч сводки в истории: да ({own.StartLocal:dd.MM HH:mm}); сводка после его начала: {(s.When > own.StartLocal ? "да" : "нет")}" +
                      (next == null ? "" : $", до начала следующего ({next.StartLocal:dd.MM HH:mm}): {(s.When < next.StartLocal ? "да" : "нет")}");
                lines.Add($"{s.MatchId} от {s.When:dd.MM HH:mm} · поведение {s.Score} ({s.ScoreDelta:+#;-#;0}) · жалоб {s.Reports} · " +
                          $"окно {w.Count}/{s.Matches} {(history.Exact(s) ? "точно" : "≈")} · {anchor}");
                foreach (var m in w)
                    lines.Add($"    {m.MatchId} {m.StartLocal:dd.MM HH:mm} {heroes.GetValueOrDefault(m.HeroId, m.Hero)} режим {m.GameMode} " +
                              $"{(m.Win switch { true => "W", false => "L", null => "?" })} {m.Kills}/{m.Deaths}/{m.Assists}{(m.LowPriority ? " LP" : "")}");
            }
            File.WriteAllLines(Path.Combine(Paths.Root, "history_test.txt"), lines);
            return;
        }
        if (args.Length >= 1 && args[0] == "--card-test")
        {
            // отрисовка карточки на вымышленном пике All Pick, роль «сложная»: враги Sniper, Tinker; союзник Slardar; собирается DK
            var data = new DraftData();
            data.LoadAsync().GetAwaiter().GetResult();
            var prof = new ProfileData();
            prof.LoadCache(data.Heroes);
            data.SetProfile(prof);
            var card = new TurboCard();
            card.Render(data, new PickSnapshot(new HashSet<int> { 1, 14 }, new List<int> { 35, 34 }, new List<int> { 28 }, new List<int> { 49 }, 1, 3, "ALL_PICK", 3), "тест");
            var text = card.DumpText();
            // время стратегии: все 10 героев, вы на Juggernaut — прогноз и состав вместо подсказок
            card.Render(data, new PickSnapshot(new HashSet<int> { 1, 14 }, new List<int> { 35, 34, 26, 44, 5 }, new List<int> { 28, 49, 86, 74 }, new List<int>(), 1, 9, "ALL_PICK", 1), "тест 5×5", ownHero: 8);
            File.WriteAllText(Path.Combine(Paths.Root, "card_test.txt"), text + Environment.NewLine + "=====" + Environment.NewLine + card.DumpText());
            return;
        }
        if (args.Length >= 2 && args[0] == "--slot-test")
        {
            // кадр верхней полосы (0..110) или скриншот: кто в каждом слоте и с каким совпадением
            var reader = new SlotReader();
            using var src = new System.Drawing.Bitmap(args[1]);
            using var bmp = new System.Drawing.Bitmap(ScreenFrame.Width, ScreenFrame.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp)) g.DrawImageUnscaled(src, 0, 0);
            var frame = new ScreenFrame(bmp);
            int[] xs = { 219, 343, 467, 591, 715, 1098, 1222, 1346, 1470, 1594 };
            File.WriteAllLines(Path.Combine(Paths.Root, "slot_test.txt"), xs.Select((x, i) =>
            {
                var (hero, score, _) = reader.Read(frame, x);
                return $"{(i < 5 ? "R" : "D")}{i % 5 + 1}: {hero} {score:0.00}";
            }));
            return;
        }
        if (args.Length >= 1 && args[0] == "--reminder-test")
        {
            // прогон часов матча с -1:30 до 40:00 по профилям из reminders.json и по общим правилам (режим не определён):
            // когда что покажется и каким цветом, в reminder_test.txt
            var settings = ReminderSettings.Load();
            var lines = new List<string>();
            foreach (var profile in settings.Profiles.Append(settings.CommonRules()))
            {
                lines.Add($"== {profile.Name} ({(!string.IsNullOrEmpty(profile.Mode) ? profile.Mode : "вручную / режим не определён")})");
                var engine = new ReminderEngine();
                for (int clock = -90; clock <= 40 * 60; clock++)
                    foreach (var (rule, at) in engine.Update("test", clock, profile))
                        lines.Add($"{ReminderTime.Format(Math.Max(clock, 0))}  {rule.Text} (в {ReminderTime.Format(at)}) — {rule.DisplayColor()}");
            }
            File.WriteAllLines(Path.Combine(Paths.Root, "reminder_test.txt"), lines);
            return;
        }
        if (args.Length >= 2 && args[0] == "--winprob-test")
        {
            // DotaReports.exe --winprob-test "джагер,крип,лион|снайпер,пудж" [ALL_PICK] — прогноз по драфту и кого брать, в winprob_test.txt
            var data = new DraftData();
            data.LoadAsync().GetAwaiter().GetResult();
            var prof = new ProfileData();
            prof.LoadCache(data.Heroes);
            data.SetProfile(prof);
            var sides = args[1].Split('|');
            List<int> Ids(string list) => list.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => HeroSearch.Find(x, data.Heroes, 1).FirstOrDefault()).Where(id => id > 0).Distinct().ToList();
            var allies = Ids(sides[0]);
            var enemies = sides.Length > 1 ? Ids(sides[1]) : new List<int>();
            var mode = args.Length >= 3 ? args[2].ToUpperInvariant() : "TURBO";
            string Names(IEnumerable<int> ids) => string.Join(", ", ids.Select(id => data.Heroes.GetValueOrDefault(id, $"#{id}")));
            var lines = new List<string> { $"режим {mode}; ваши: {Names(allies)}; их: {Names(enemies)}", "status: " + data.Status };
            if (data.WinProbability(mode, allies, enemies, 0) is { } chance)
                lines.Add($"шанс победы {chance.P:0.0%} (ваших {chance.KnownAllies}, их {chance.KnownEnemies}): {string.Join(" · ", chance.Parts)}");
            else
                lines.Add("шанс победы: нет данных Stratz");
            lines.Add("в команде: " + string.Join(", ", data.Lacking(allies)));
            foreach (var p in data.TurboPicks(mode, 0, new HashSet<int>(allies), enemies, allies).Where(p => !p.Rare).Take(10))
                lines.Add($"{data.Heroes.GetValueOrDefault(p.HeroId)} {p.Winrate:0.0}% · враги {p.VsRevealed:+0.0;-0.0} · союзники {p.WithAllies:+0.0;-0.0} · " +
                          $"состав {p.Composition:+0.0;-0.0} · риск {p.CounterRisk:+0.0;-0.0} · итог {p.Total:0.0} · ваша игра {data.PersonalBonus(p.HeroId, mode):+0.0;-0.0}");
            File.WriteAllLines(Path.Combine(Paths.Root, "winprob_test.txt"), lines);
            return;
        }
        if (args.Length >= 2 && args[0] == "--draft-test")
        {
            DraftTest(args[1].Split(','), Path.Combine(Paths.Root, "draft_test.txt"));
            return;
        }


        if (args.Length >= 1 && args[0] == "--exit")
        {
            // Аккуратно закрыть запущенную копию (для обновлений): она успеет сохранить вход в Steam.
            if (EventWaitHandle.TryOpenExisting(ExitEventName, out var ev)) using (ev) ev.Set();
            return;
        }

        using var mutex = new Mutex(true, @"Local\DotaReports.SingleInstance", out bool first);
        if (!first)
        {
            // копия из автозагрузки, которая ждёт Dota, — разбудить
            if (EventWaitHandle.TryOpenExisting(Autostart.WakeEventName, out var wake)) using (wake) wake.Set();
            MessageBox.Show("Dota Reports уже запущен — значок в трее.", "Dota Reports");
            return;
        }
        Autostart.Ensure();
        bool background = args.Contains(Autostart.BackgroundArg); // из автозагрузки Windows: до запуска Dota спать

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var controller = new AppController(app);
        app.DispatcherUnhandledException += (_, e) =>
        {
            Log.Write("Необработанная ошибка: " + e.Exception);
            e.Handled = true;
        };
        app.Startup += async (_, _) =>
        {
            try
            {
                if (background && !await Autostart.WaitForDotaAsync(app))
                {
                    app.Shutdown();
                    return;
                }
                await controller.StartAsync();
            }
            catch (Exception ex)
            {
                Log.Write("Запуск не удался: " + ex);
                MessageBox.Show($"Не удалось запуститься:\n{ex.Message}\n\nПодробности: {Paths.Log}", "Dota Reports");
                app.Shutdown();
            }
        };
        app.Run();
    }

    // DotaReports.exe --draft-test медуза,па,войд — проверка поиска героев и контрпиков по кэшу, в draft_test.txt.
    static void DraftTest(string[] inputs, string outPath)
    {
        var data = new DraftData();
        data.LoadAsync().GetAwaiter().GetResult();
        var lines = new System.Collections.Generic.List<string> { "status: " + data.Status };
        var enemies = new System.Collections.Generic.List<int>();
        foreach (var input in inputs)
        {
            var found = HeroSearch.Find(input, data.Heroes);
            lines.Add($"{input} -> " + string.Join(", ", System.Linq.Enumerable.Select(found, id => data.Heroes[id])));
            if (found.Count > 0) enemies.Add(found[0]);
        }
        lines.Add("pool: " + string.Join(", ", System.Linq.Enumerable.Select(data.Pool, p => $"{data.Heroes.GetValueOrDefault(p.Key)} {p.Value.Games}/{p.Value.Wins}")));
        foreach (var r in System.Linq.Enumerable.Take(data.Counters(enemies), 8))
            lines.Add($"{data.Heroes[r.HeroId]} {r.Score:+0.0;-0.0} | " + string.Join("; ", r.Parts));
        lines.Add("turbo: " + data.TurboLabel);
        var picks = data.TurboPicks("TURBO", 0, new HashSet<int>(), enemies, Array.Empty<int>());
        var mid = data.TurboPicks("ALL_PICK", 2, new HashSet<int>(), Array.Empty<int>(), Array.Empty<int>());
        lines.Add("all pick центр: " + string.Join(", ", mid.Take(8).Select(p => $"{data.Heroes[p.HeroId]} {p.Winrate:0.0}%{(p.Rare ? " (редко)" : "")}")));
        lines.Add("all pick центр, ваши: " + string.Join(", ", mid.Where(p => data.Pool.TryGetValue(p.HeroId, out var g) && g.Games >= 2)
            .Select(p => $"{data.Heroes[p.HeroId]} {p.Winrate:0.0}%{(p.Rare ? " (редко)" : "")}")));
        lines.Add("turbo pool: " + string.Join(", ", System.Linq.Enumerable.Take(System.Linq.Enumerable.Where(picks, p => data.Pool.TryGetValue(p.HeroId, out var g) && g.Games >= 2), 6)
            .Select(p => $"{data.Heroes[p.HeroId]} {p.Winrate:0.0}% {p.VsRevealed:+0.0;-0.0}")));
        lines.Add("turbo top: " + string.Join(", ", System.Linq.Enumerable.Take(picks, 6).Select(p => $"{data.Heroes[p.HeroId]} {p.Winrate:0.0}% {p.VsRevealed:+0.0;-0.0}")));
        File.WriteAllLines(outPath, lines);
    }

    // DotaReports.exe --grid-test <папка кадров> radiant|dire — прогон распознавания по сохранённым кадрам, в grid_test.txt.
    static void GridTest(string folder, string team, string outPath)
    {
        var names = new Dictionary<int, string>();
        using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Paths.HeroList)))
            foreach (var h in doc.RootElement.GetProperty("result").GetProperty("data").GetProperty("heroes").EnumerateArray())
                names[h.GetProperty("id").GetInt32()] = h.GetProperty("name_loc").GetString() ?? "";
        string N(IEnumerable<int> ids) => string.Join(", ", ids.Select(id => names.GetValueOrDefault(id, $"#{id}")));

        var grid = new GridReader();
        var lines = new List<string> { grid.Status };
        if (File.Exists(folder))
        {
            // полный скриншот 1920×1080: режим, роль (слот из аргумента team: radiant:2) и сетка за один кадр
            var parts = team.Split(':');
            int slot = parts.Length > 1 ? int.Parse(parts[1]) : -1;
            using var shot = new System.Drawing.Bitmap(folder);
            using var top = new System.Drawing.Bitmap(ScreenFrame.Width, ScreenFrame.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = System.Drawing.Graphics.FromImage(top)) g.DrawImageUnscaled(shot, 0, 0); // скриншоты бывают 1919×1079
            var frame = new ScreenFrame(top);
            for (int i = 0; i < 3; i++) grid.Process(frame, i * 0.5, parts[0], 0, slot);
            lines.Add($"{Path.GetFileName(folder)}: режим {grid.Mode} роль {grid.Position} | баны [{N(grid.Banned)}] заняты [{N(grid.Taken)}] враги [{N(grid.Enemies)}] союзники [{N(grid.Allies)}] собираются [{N(grid.Planned)}]");
            File.WriteAllLines(outPath, lines);
            return;
        }
        foreach (var gridPng in Directory.GetFiles(folder, "*_grid.png").OrderBy(f => f))
        {
            var name = Path.GetFileName(gridPng);
            double t = int.Parse(name.Split('_')[1].TrimEnd('s'));
            using var bmp = PickWatcher.Compose(gridPng.Replace("_grid.png", "_top.png"), gridPng);
            var tp = team.Split(':');
            bool changed = grid.Process(new ScreenFrame(bmp), t, tp[0], 0, tp.Length > 1 ? int.Parse(tp[1]) : -1);
            if (changed)
                lines.Add($"{name}: режим {grid.Mode} роль {grid.Position} | баны [{N(grid.Banned)}] заняты [{N(grid.Taken)}] враги [{N(grid.Enemies)}] союзники [{N(grid.Allies)}] собираются [{N(grid.Planned)}]");
        }
        lines.Add(grid.Debug);
        File.WriteAllLines(outPath, lines);
    }

    // DotaReports.exe --dump TabA,TabB — без трея и оверлея, только выгрузка в dump_tabs.json.
    static void DumpTabs(string[] tabs, string outPath, int extraPages)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                var browser = new SteamBrowser();
                await browser.InitAsync();
                await new GcpdReader(browser).DumpTabsAsync(tabs, outPath, extraPages);
                browser.Close();
            }
            catch (Exception ex)
            {
                Log.Write("Выгрузка не удалась: " + ex);
            }
            app.Shutdown();
        };
        app.Run();
    }
}
