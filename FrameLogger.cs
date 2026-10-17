using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace DotaReports;

/// <summary>Что распознано на экране пика.</summary>
sealed record PickSnapshot(HashSet<int> Unavailable, List<int> Enemies, List<int> Allies, List<int> Planned, int Bans, int Taken, string? Mode, int Position);

/// <summary>
/// Снимает верх экрана (1920×720) раз в секунду, пока идёт пик, и отдаёт кадр распознаванию.
/// Часть кадров (верхняя полоса и сетка) сохраняет на диск, чтобы можно было перепроверить пороги.
/// </summary>
sealed class PickWatcher
{
    const int MaxSavedFrames = 110;
    const int SaveEvery = 4;
    const double SaveAllUntilSec = 20; // начало пика (баны и первые выборы) — каждый кадр
    const int KeepMatches = 2;
    static readonly Rectangle TopBar = new(0, 0, 1920, 110);
    static readonly Rectangle GridArea = new(100, 175, 1220, 535);

    readonly GridReader _grid = new();
    string? _dir;
    int _ticks, _saved;
    DateTime _start;
    volatile bool _busy;
    volatile string? _team;
    volatile int _ownHero, _ownSlot = -1;

    /// <summary>Распознавание изменилось. Из фонового потока.</summary>
    public event Action<PickSnapshot>? Changed;

    public string Status => Setup.ScreenSupported ? _grid.Status : "экран не 1920×1080 — баны и союзники не распознаются";

    /// <summary>Где герой в сетке пика на экране (клетки заданы раз при запуске — без блокировки).</summary>
    public Rectangle? CellOf(int hero) => _grid.CellOf(hero);

    public void Begin(string matchId)
    {
        lock (_grid) _grid.Reset();
        _dir = Path.Combine(Paths.Frames, matchId);
        Directory.CreateDirectory(_dir);
        _saved = Directory.GetFiles(_dir, "*_grid.png").Length;
        _ticks = 0;
        _start = DateTime.Now;
        Prune();
    }

    public void End()
    {
        _dir = null;
        lock (_grid) _grid.SaveRefs();
    }

    /// <summary>См. GridReader.SlotsToLearn — под замком: кадры пика читают тот же SlotReader в фоне.</summary>
    public List<(int Slot, int Hero, string Was)>? SlotsToLearn(ScreenFrame f, int[] truth, bool radiant)
    {
        lock (_grid) return _grid.SlotsToLearn(f, truth, radiant);
    }

    public void AddSlotRef(int hero, Bitmap crop)
    {
        lock (_grid) _grid.AddSlotRef(hero, crop);
    }

    public void Update(GsiState s)
    {
        _team = s.Team;
        _ownHero = s.HeroId;
        _ownSlot = s.TeamSlot;
    }

    public void Tick()
    {
        if (_busy || !Setup.ScreenSupported) return;
        _busy = true;
        var dir = _dir;
        double t = (DateTime.Now - _start).TotalSeconds;
        bool save = dir != null && (_ticks++ % SaveEvery == 0 || t < SaveAllUntilSec) && _saved < MaxSavedFrames;
        int n = save ? _saved++ : -1;
        Task.Run(() =>
        {
            try
            {
                using var bmp = new Bitmap(ScreenFrame.Width, ScreenFrame.Height, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(Point.Empty, Point.Empty, bmp.Size);
                if (save && dir != null) SaveCrops(bmp, Path.Combine(dir, $"{n:00}_{(int)t:000}s_hero{_ownHero}"));

                var frame = new ScreenFrame(bmp);
                PickSnapshot? snap = null;
                lock (_grid)
                {
                    if (_grid.Process(frame, t, _team, _ownHero, _ownSlot))
                        snap = new PickSnapshot(new HashSet<int>(_grid.Banned.Concat(_grid.Taken)), new List<int>(_grid.Enemies),
                            new List<int>(_grid.Allies), new List<int>(_grid.Planned), _grid.Banned.Count, _grid.Taken.Count, _grid.Mode, _grid.Position);
                }
                if (snap != null) Changed?.Invoke(snap);
            }
            catch (Exception ex) when (ex is ExternalException or Win32Exception or IOException or ArgumentException)
            {
                Log.Write("Кадр пика не обработан: " + ex.Message);
            }
            finally
            {
                _busy = false;
            }
        });
    }

    static void SaveCrops(Bitmap bmp, string name)
    {
        using (var top = bmp.Clone(TopBar, bmp.PixelFormat)) top.Save(name + "_top.png", ImageFormat.Png);
        using (var grid = bmp.Clone(GridArea, bmp.PixelFormat)) grid.Save(name + "_grid.png", ImageFormat.Png);
    }

    /// <summary>Собрать кадр 1920×720 из сохранённых вырезок (для --grid-test).</summary>
    public static Bitmap Compose(string topPng, string gridPng)
    {
        var bmp = new Bitmap(ScreenFrame.Width, ScreenFrame.Height, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Black);
        using (var top = new Bitmap(topPng)) g.DrawImageUnscaled(top, TopBar.Location);
        using (var grid = new Bitmap(gridPng)) g.DrawImageUnscaled(grid, GridArea.Location);
        return bmp;
    }

    // Храним кадры только последних матчей: каждый матч — до ~80 МБ.
    static void Prune()
    {
        var old = new DirectoryInfo(Paths.Frames).GetDirectories()
            .OrderByDescending(d => d.CreationTime)
            .Skip(KeepMatches);
        foreach (var d in old)
            d.Delete(recursive: true);
    }
}
