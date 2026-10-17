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
        card.RenderChoices(data, new[] { 129, 3, 119 }, "3320645051", id => id == 3
            ? new[] { new List<string> { "Enfeeble: эффект удвоен" }, new List<string> { "Brain Sap: выпускает 3 раза подряд" }, new List<string>() }
            : null);
        Save(card.Root, 1165, shot, new Point(63, 856), Path.Combine(dir, "choices"));

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
        double h = Math.Ceiling(root.ActualHeight), w = Math.Ceiling(width);
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
        matcher.SetCandidates(NecLobby.Load(at, TimeSpan.FromMinutes(20)), NecLobby.Load(at, everyone: true), NecLobby.OwnAvatars(), NecLobby.DefaultAvatarFile());
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
    public static void Names(string shotFile, string file)
    {
        RankArt.EnsureAsync().GetAwaiter().GetResult();
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

    /// <summary>--live-test: во время матча — сервер из console.log, состав от Steam, медали и знакомые с OpenDota (в .txt).</summary>
    public static void Live(string file)
    {
        var lines = new List<string>();
        var last = LiveMatch.LastServer();
        lines.Add(last is { } s ? $"сервер {s.Server}, подключение {s.At:dd.MM HH:mm:ss}" : "в console.log нет подключения к серверу Valve");
        if (last is { } srv)
        {
            try
            {
                var list = LiveMatch.PlayersAsync(srv.Server).GetAwaiter().GetResult();
                if (list == null) lines.Add("Steam не отдал состав (ответ — realtime_stats.json)");
                else
                {
                    var peers = LiveMatch.PeersAsync().GetAwaiter().GetResult();
                    foreach (var p in list)
                    {
                        var (with, against) = peers.GetValueOrDefault(p.Account);
                        lines.Add($"слот {p.Slot}: {p.Name} ({p.Account}) — {Ranks.Name(LiveMatch.RankAsync(p.Account).GetAwaiter().GetResult()) ?? "без медали"}, за {with} · против {against}");
                    }
                }
            }
            catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.Threading.Tasks.TaskCanceledException or System.Text.Json.JsonException)
            {
                lines.Add("ошибка: " + ex.Message);
            }
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
