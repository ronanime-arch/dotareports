using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DotaReports;

/// <summary>
/// --ui-test [скриншот 1920×1080]: окна в стиле «Древний + стекло» на примерных данных — в ui_test\*.png,
/// поверх куска скриншота игры там, где окно стоит на экране; каждое в масштабе 100% и 125%.
/// Чтобы сверить с макетом docs/design/ancient-glass.html, не запуская Dota.
/// </summary>
static class UiTest
{
    public static void Run(string dir, string? backdrop)
    {
        Directory.CreateDirectory(dir);
        BitmapSource? shot = backdrop != null && File.Exists(backdrop) ? new BitmapImage(new Uri(Path.GetFullPath(backdrop))) : null;
        var heroes = LoadHeroes();
        RankArt.EnsureAsync().GetAwaiter().GetResult();
        string H(int id) => heroes.GetValueOrDefault(id, $"#{id}");

        var rating = new RatingWindow();
        rating.RenderRating(new RatingView(0, "Легенда II", (7, 5, false), (64, DateTime.Now), (4, 3, 3, 2), (2, true),
            (9840, -120, 6, 3), new[] { 9410, 9480, 9550, 9590, 9670, 9700, 9770, 9820, 9870, 9900, 9930, 9840 },
            (2318, 2204), (2, 6, false), (38, 61), "данные Valve от 17.10 21:40"), () => { });
        Save(rating.Root, 300, shot, new Point(20, 130), Path.Combine(dir, "rating"));

        var lobby = new RatingWindow(380);
        var players = new List<NecPlayer>
        {
            new("1", "♠foX♠", DateTime.Now, true, 51, true, null, null, "KZ", null),
            new("2", "弗鲁尼肯", DateTime.Now, true, 35, false, 1309, 1307, null, new NecMatch(DateTime.Now.AddDays(-1), 26, 21, 12, 17, 23)),
            new("3", "Mollonn", DateTime.Now, true, 44, false, 2659, 2456, "RU", new NecMatch(DateTime.Now.AddDays(-3), 14, 9, 8, 27, 22)),
            new("4", "Будэика", DateTime.Now, false, 0, false, null, null, null, null),
        };
        lobby.Render($"Лобби NEC · {players.Count}", "Ctrl+Alt+N", NecLobby.Tiles(players, heroes),
            "комната NEC, 13:06 — обновится с новыми игроками · без деления на команды, могут быть ушедшие", () => { });
        Save(lobby.Root, 380, shot, new Point(20, 130), Path.Combine(dir, "lobby"));

        var match = new RatingWindow(380);
        var roster = new List<LivePlayer>
        {
            new(-1, 1, "Mollonn", 44, 3, 1), new(-1, 2, "♠foX♠", 51), new(-1, 3, "Будэика", 0), new(-1, 4, "a very long nickname here", 35),
        };
        roster.AddRange(new LivePlayer[] { new(-1, 5, "ветеран", 0), new(-1, 6, "R4Y", 0) }); // о них ничего — в список не идут
        match.Render($"Игроки матча · {roster.Count}", "Ctrl+Alt+N", LiveMatch.RosterBody(roster, peersKnown: true),
            "Steam «Недавние игроки» · медали и «за · против» — OpenDota · без деления на команды", () => { });
        Save(match.Root, 380, shot, new Point(20, 130), Path.Combine(dir, "match_players"));

        // пустые все — строка вместо списка
        var empty = new RatingWindow(380);
        var nobody = roster.Select(p => p with { RankTier = 0, With = 0, Against = 0 }).ToList();
        empty.Render($"Игроки матча · {nobody.Count}", "Ctrl+Alt+N", LiveMatch.RosterBody(nobody, peersKnown: true),
            "Steam «Недавние игроки» · медали и «за · против» — OpenDota · без деления на команды", () => { });
        Save(empty.Root, 380, shot, new Point(20, 130), Path.Combine(dir, "match_players_empty"));

        var popup = new OverlayWindow();
        popup.Build("На вас пожаловались", OverlayWindow.Red, new[]
        {
            ("3 жалобы от 2 игроков — в 2 из 15 матчей", OverlayWindow.White),
            ("за общение (чат/голос): 1", OverlayWindow.Red),
            ("поведение 9840 (−120) · похвал: 6", OverlayWindow.Gray),
            ("сводка за 15 матчей от 17.10 21:40", OverlayWindow.Gray),
        }, OverlayWindow.Red, () => { });
        Save(popup.Root, 380, shot, new Point(1516, 120), Path.Combine(dir, "popup"));

        var pill = new OverlayWindow(330, 0xCC, pill: true);
        pill.Build("Баунти-руны", ReminderColors.Of("yellow"), new[]
        {
            ("через 0:15 · в 6:00", OverlayWindow.White),
            ("профиль «Турбо»", OverlayWindow.Gray),
        }, ReminderColors.Of("yellow"));
        Save(pill.Root, 380, shot, new Point(1516, 330), Path.Combine(dir, "reminder"));

        var data = new DraftData();
        data.LoadAsync().GetAwaiter().GetResult();
        var prof = new ProfileData();
        prof.LoadCache(data.Heroes);
        data.SetProfile(prof);
        var card = new TurboCard();
        var snap = new PickSnapshot(new HashSet<int> { 1, 14 }, new List<int> { 35, 34 }, new List<int> { 28 }, new List<int> { 49 }, 1, 3, "ALL_PICK", 3);
        card.Render(data, snap, "с экрана: банов 2, занято 4");
        Save(card.Root, 190, shot, new Point(1724, 120), Path.Combine(dir, "pick"));
        card.Render(data, snap, "с экрана: банов 2, занято 4", ownHero: 74);
        Save(card.Root, 190, shot, new Point(1724, 120), Path.Combine(dir, "pick_own"));
        card.RenderChoices(data, new[] { 129, 3, 77 }, "3320645051", id => id == 3
            ? new[] { new List<string> { "Enfeeble: эффект удвоен" }, new List<string> { "Brain Sap: выпускает 3 раза подряд" }, new List<string>() }
            : null);
        Save(card.Root, 1165, shot, new Point(63, 856), Path.Combine(dir, "choices"));
        // первым его сделал тир-лист B站: своих игр нет, без рядов первым был бы Dark Seer (D) — у него Турбо выше
        var byTier = new List<CustomChoice>
        {
            new(2, 0.530, 0, 0, 0, 0, 0, 0, 0, 0.500, null, 0.530, 0.530, 'S', 0.500),
            new(55, 0.505, 0, 0, 0, 0, 0, 0, 0, 0.535, null, 0.505, 0.470, 'D', 0.535),
            new(12, 0.490, 0, 0, 0, 0, 0, 0, 0, 0.520, null, 0.490, 0.470, 'D', 0.520),
        };
        card.RenderChoices(data, CommunityPicks.Game, byTier, false, _ => null);
        Save(card.Root, 1165, shot, new Point(63, 856), Path.Combine(dir, "choices_tier"));
        // худший случай: два героя с самыми длинными описаниями из .vpk самой кастомки (если скачана) и Пророк — с отзывами
        // и рядом S в тир-листе B站; у всех троих по десятку игр здесь с исходами, золотом и KDA и сотня в Доте, а Пророк первый
        // лишь по Доте — самые длинные строки «здесь…» и «Почему» (оценки заданы руками: в BeatenByDota — второй, отзывы и ряды не решили)
        var names = data.HeroInternalNames();
        if (Setup.FindDota() is { } dota
            && AwakeningInfo.Load(Path.GetFullPath(Path.Combine(dota, @"..\..\workshop\content\570\3320645051\3320645051.vpk")), AwakeningInfo.AbilityNames()) is { } awakening)
        {
            int Length(int id) => awakening.Of(names.GetValueOrDefault(id))?.Sum(s => s.Sum(e => e.Length)) ?? 0;
            var longest = names.Keys.Where(id => id != 53).OrderByDescending(Length).Take(2).ToArray();
            double Turbo(int id) => data.ModeWinrate(id, "TURBO") is { } wr ? wr.Winrate / 100 : 0.5;
            char? Tier(int id) => TierList.Of(CommunityPicks.Game, names.GetValueOrDefault(id));
            var worst = new List<CustomChoice>
            {
                new(53, 0.62, 12, 1250, 6, 12, 3.4, 125, 73, Turbo(53), CommunityPicks.Of(CommunityPicks.Game, names.GetValueOrDefault(53)), 0.60, 0.50, Tier(53), 0.61),
                new(longest[0], 0.58, 11, 1180, 7, 11, 2.1, 104, 45, Turbo(longest[0]), null, 0.58, 0.56, Tier(longest[0]), 0.58),
                new(longest[1], 0.45, 10, 1050, 4, 10, 1.9, 33, 15, Turbo(longest[1]), null, 0.45, 0.44, Tier(longest[1]), 0.45),
            };
            card.RenderChoices(data, CommunityPicks.Game, worst, true, id => awakening.Of(names.GetValueOrDefault(id)));
            Save(card.Root, 1165, shot, new Point(63, 856), Path.Combine(dir, "choices_long"));
            double scale = card.Root.LayoutTransform is ScaleTransform st ? st.ScaleX : 1;
            File.WriteAllText(Path.Combine(dir, "choices_long.txt"),
                $"высота {card.Root.DesiredSize.Height:0} из {TurboCard.ChoicesRoom:0} под кнопкой, масштаб {scale:0.###}\n"
                + "почему: " + CustomStats.Why(worst, true, id => data.Heroes.GetValueOrDefault(id, $"#{id}")));
            // как на снимке 22.10: игр здесь хватает, но на этих троих не играли — решила обычная Дота; у Storm Spirit
            // в русском файле кастомки съехали строки (Realign)
            var dotaOnly = new List<CustomChoice>
            {
                new(63, 0.515, 0, 0, 0, 0, 0, 51, 26, 0.496, null, 0.515, 0.53, Tier(63), 0.51),
                new(78, 0.48, 0, 0, 0, 0, 0, 21, 10, 0.471, null, 0.48, 0.47, Tier(78), 0.48),
                new(17, 0.42, 0, 0, 0, 0, 0, 63, 25, 0.476, null, 0.42, 0.51, Tier(17), 0.41),
            };
            card.RenderChoices(data, CommunityPicks.Game, dotaOnly, true, id => awakening.Of(names.GetValueOrDefault(id)));
            Save(card.Root, 1165, shot, new Point(63, 856), Path.Combine(dir, "choices_dota"));
        }

        // иконки ранга: все медали со звёздами, Титан и «нет медали» — в размерах экрана подключения и выбора команды
        var sheet = new System.Windows.Controls.StackPanel();
        foreach (var size in new[] { 34.0, 28.0 })
        {
            var row = new System.Windows.Controls.WrapPanel { Margin = new Thickness(6) };
            foreach (var tier in new[] { 0, 11, 25, 33, 44, 52, 65, 71, 80 })
            {
                var icon = RankIcon.Build(tier, size);
                icon.Margin = new Thickness(4);
                icon.ToolTip = Ranks.Name(tier);
                row.Children.Add(icon);
            }
            sheet.Children.Add(row);
        }
        Save(sheet, 9 * 42 + 12, null, new Point(0, 0), Path.Combine(dir, "rank_icons"));
        Console.WriteLine($"ui_test: {string.Join(", ", new[] { H(1), H(129) })} — готово");
    }

    // Окно поверх куска скриншота там, где оно стоит на экране; масштаб 100% и 125%.
    static void Save(FrameworkElement root, double width, BitmapSource? shot, Point at, string file)
    {
        root.Measure(new Size(width, double.PositiveInfinity));
        root.Arrange(new Rect(new Size(width, root.DesiredSize.Height)));
        root.UpdateLayout();
        double h = Math.Ceiling(root.DesiredSize.Height), w = Math.Ceiling(width); // не ActualHeight: в нём нет масштаба (LayoutTransform)
        foreach (var scale in new[] { 1.0, 1.25 })
        {
            // окно — отдельным снимком как есть (VisualBrush растянул бы по границам вылезшего содержимого)
            var window = new RenderTargetBitmap((int)(w * scale), (int)(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            window.Render(root);
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                if (shot != null)
                {
                    int x = (int)Math.Min(at.X, shot.PixelWidth - w), y = (int)Math.Min(at.Y, shot.PixelHeight - h);
                    var crop = new CroppedBitmap(shot, new Int32Rect(Math.Max(x, 0), Math.Max(y, 0),
                        (int)Math.Min(w, shot.PixelWidth), (int)Math.Min(h, shot.PixelHeight - Math.Max(y, 0))));
                    dc.DrawImage(crop, new Rect(0, 0, crop.PixelWidth, crop.PixelHeight));
                }
                else
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x24, 0x1E, 0x17)), null, new Rect(0, 0, w, h));
                dc.DrawImage(window, new Rect(0, 0, w, h));
            }
            var bmp = new RenderTargetBitmap((int)(w * scale), (int)(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            bmp.Render(visual);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bmp));
            using var fs = File.Create(file + (scale == 1.0 ? "" : "_125") + ".png");
            png.Save(fs);
        }
    }

    /// <summary>--badge-test: кто узнан на снимке экрана кастомки (в .txt) и снимок со значками ранга (в .png) — тем же кодом, что в игре.</summary>
    public static void Badges(string shotFile, NecLayout layout, DateTime at, string file)
    {
        RankArt.EnsureAsync().GetAwaiter().GetResult(); // значки медалей — как в игре
        var matcher = new AvatarMatcher();
        // как в игре: кандидаты — все, кого NEC держит в кэше (знакомых он заново не пишет); at — для старых снимков не важен
        matcher.SetCandidates(NecLobby.Load(at, TimeSpan.FromMinutes(20), sinceMatch: false), NecLobby.Load(at, everyone: true), NecLobby.OwnAvatars(), NecLobby.DefaultAvatarFile());
        List<ScreenHit> found;
        List<string> debug;
        using (var shot = new System.Drawing.Bitmap(shotFile))
        {
            var area = NecScreen.Strip(layout);
            area.Intersect(new System.Drawing.Rectangle(0, 0, shot.Width, shot.Height));
            using var strip = new System.Drawing.Bitmap(NecScreen.Strip(layout).Width, NecScreen.Strip(layout).Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = System.Drawing.Graphics.FromImage(strip))
                g.DrawImage(shot, new System.Drawing.Rectangle(area.X - NecScreen.Strip(layout).X, area.Y - NecScreen.Strip(layout).Y, area.Width, area.Height), area, System.Drawing.GraphicsUnit.Pixel);
            found = NecScreen.Read(strip, layout, matcher);
            debug = NecScreen.Debug(strip, layout, matcher).ToList();
        }
        found = NecScreen.WithRanksAsync(found).GetAwaiter().GetResult();
        var lines = new List<string> { $"кандидатов с аватаркой: {matcher.Count}; узнано: {found.Count}" };
        lines.AddRange(debug);
        lines.AddRange(found.Select(f => $"{f.Index + 1}: {f.Player.Name} — {Ranks.Name(f.Player.RankTier) ?? "без медали"}" +
            $"{(f.Team is int t ? (t == 0 ? " · Свет" : " · Тьма") : "")} ({f.Debug})"));
        File.WriteAllLines(file + ".txt", lines);
        RenderSpots(shotFile, found.SelectMany(NecScreen.Spots), file + ".png");
    }

    /// <summary>
    /// --names-test: ники в полосе под портретами на кадре пика (frames\…_top.png или скриншот) и медали перед ними —
    /// на условных званиях (все медали по слотам, у двоих — «за · против»); левые края ников — в .txt.
    /// </summary>
    public static void Names(string shotFile, string file, string? rosterFile = null)
    {
        RankArt.EnsureAsync().GetAwaiter().GetResult();
        if (rosterFile != null)
        {
            NamesOcr(shotFile, file, rosterFile);
            return;
        }
        int[] left;
        using (var shot = new System.Drawing.Bitmap(shotFile))
        using (var band = new System.Drawing.Bitmap(TopBarNames.Band.Width, TopBarNames.Band.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
        {
            using (var g = System.Drawing.Graphics.FromImage(band))
                g.DrawImage(shot, new System.Drawing.Rectangle(0, 0, band.Width, band.Height), TopBarNames.Band, System.Drawing.GraphicsUnit.Pixel);
            left = TopBarNames.TextLeft(band);
        }
        int[] tiers = { 11, 25, 33, 44, 52, 65, 71, 80, 0, 54 };
        var players = Enumerable.Range(0, 10).Select(s => new LivePlayer(s, 0, $"слот {s}", tiers[s], s == 1 ? 3 : 0, s == 6 ? 2 : 0)).ToList();
        File.WriteAllLines(file + ".txt", left.Select((x, s) => $"слот {s}: ник с x = {x}"));
        RenderSpots(shotFile, TopBarNames.Spots(players, left), file + ".png");
    }

    // Пик матча Valve: состав coplay — строки «ник=звание», как в журнале «Звания на пике»; слоты — по нику в полосе (OCR)
    static void NamesOcr(string shotFile, string file, string rosterFile)
    {
        var roster = File.ReadAllLines(rosterFile).Where(l => l.Contains('=')).Select(l =>
        {
            int eq = l.LastIndexOf('=');
            return new LivePlayer(-1, 0, l[..eq].Trim(), int.Parse(l[(eq + 1)..]));
        }).ToList();
        using var shot = new System.Drawing.Bitmap(shotFile);
        using var area = shot.Clone(TopBarOcr.Area, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        var read = System.Threading.Tasks.Task.Run(() => TopBarOcr.ReadAsync(area)).GetAwaiter().GetResult();
        var slots = TopBarOcr.Match(roster.Select(p => p.Name).ToList(), read.Texts);
        var players = roster.Select((p, i) => p with { Slot = slots[i] }).ToList();
        var lines = Enumerable.Range(0, 10).Select(s => $"слот {s}: ник с x = {read.Left[s]}; прочитано: " +
            string.Join(" | ", read.Texts[s].Select(t => $"«{t}»"))).ToList();
        lines.Add(TopBarOcr.Describe(players, read.Texts));
        lines.AddRange(players.Select(p => $"{p.Name}: слот {p.Slot}, сходство со слотами " +
            string.Join(" ", read.Texts.Select((t, s) => $"{s}:{t.Max(x => TopBarOcr.Score(p.Name, x)):0.00}"))));
        File.WriteAllLines(file + ".txt", lines);
        RenderSpots(shotFile, TopBarNames.Spots(players, read.Left), file + ".png");
    }

    // Значки и плашки тем же кодом, что в игре (RankBadges.Fill), поверх картинки — в png.
    static void RenderSpots(string shotFile, IEnumerable<BadgeSpot> spots, string pngFile)
    {
        var image = new BitmapImage(new Uri(Path.GetFullPath(shotFile)));
        var canvas = new System.Windows.Controls.Canvas { Width = image.PixelWidth, Height = image.PixelHeight };
        RankBadges.Fill(canvas, spots, image.PixelWidth, Matrix.Identity);
        canvas.Measure(new Size(image.PixelWidth, image.PixelHeight));
        canvas.Arrange(new Rect(0, 0, image.PixelWidth, image.PixelHeight));
        canvas.UpdateLayout();
        var overlay = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        overlay.Render(canvas);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(image, new Rect(0, 0, image.PixelWidth, image.PixelHeight));
            dc.DrawImage(overlay, new Rect(0, 0, image.PixelWidth, image.PixelHeight));
        }
        var result = new RenderTargetBitmap(image.PixelWidth, image.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        result.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(result));
        using var fs = File.Create(pngFile);
        png.Save(fs);
    }

    /// <summary>
    /// --live-test: последний матч Valve — подключение из файла Dota, игроки из Steam «Недавние игроки» (coplay),
    /// ники Steam, медали и знакомые с OpenDota; для сравнения — что отдаёт Steam GetRealtimeStats по серверу (в .txt).
    /// </summary>
    public static void Live(string file)
    {
        var lines = new List<string>();
        var connect = Coplay.LastConnect();
        lines.Add(connect is { } c ? $"подключение {c.At:dd.MM HH:mm:ss}, сервер {c.Server}" : "нет dota_local_disk_lobby_data.txt — подключения к матчу не было");
        var since = connect is { } at ? at.At.AddSeconds(-5) : DateTime.Now.AddMinutes(-30);
        var accounts = Coplay.PlayersSince(since);
        lines.Add($"coplay с {since:dd.MM HH:mm:ss}: игроков {accounts.Count}");
        try
        {
            var peers = LiveMatch.PeersAsync().GetAwaiter().GetResult();
            if (peers == null) lines.Add("знакомые: OpenDota не ответил или нет account.txt");
            var ranks = accounts.Select(a => LiveMatch.RankAsync(a).GetAwaiter().GetResult()).ToList();
            var names = LiveMatch.NamesAsync(accounts).GetAwaiter().GetResult();
            for (int i = 0; i < accounts.Count; i++)
            {
                var (with, against) = peers?.GetValueOrDefault(accounts[i]) ?? default;
                lines.Add($"{names.GetValueOrDefault(accounts[i]) ?? "?"} ({accounts[i]}) — {Ranks.Name(ranks[i]) ?? "без медали"}, за {with} · против {against}");
            }
            if (connect is { } srv)
                lines.Add(LiveMatch.PlayersAsync(srv.Server).GetAwaiter().GetResult() is { } list
                    ? $"Steam GetRealtimeStats: игроков {list.Count}" : "Steam GetRealtimeStats: состава нет (ответ — realtime_stats.json)");
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.Threading.Tasks.TaskCanceledException or System.Text.Json.JsonException)
        {
            lines.Add("ошибка: " + ex.Message);
        }
        File.WriteAllLines(file, lines);
    }

    static Dictionary<int, string> LoadHeroes()
    {
        var heroes = new Dictionary<int, string>();
        if (File.Exists(Paths.Heroes))
            foreach (var h in System.Text.Json.JsonDocument.Parse(File.ReadAllText(Paths.Heroes)).RootElement.EnumerateArray())
                heroes[h.GetProperty("id").GetInt32()] = h.GetProperty("localized_name").GetString() ?? "";
        return heroes;
    }
}
