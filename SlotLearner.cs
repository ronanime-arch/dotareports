using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DotaReports;

/// <summary>
/// Свои эталоны слотов из прошедших матчей. Портрет Valve похож на вид героя в верхней полосе не у всех
/// (Shadow Fiend, Riki, Lich не узнавались). После матча Stratz (или OpenDota) знает героев всех десяти игроков,
/// а в кадрах пика (frames\{матч}) видна полоса: слот вашей команды, где герой не узнан уверенно, запоминается
/// как его эталон — slots\{id}_auto_{матч}.png, и в следующий раз герой узнаётся.
/// </summary>
sealed class SlotLearner
{
    const string DoneFile = "learned.txt";
    const int KeepPerHero = 3;                                     // своих эталонов на героя — последние три
    static readonly TimeSpan After = TimeSpan.FromMinutes(25);     // раньше матч ещё идёт — его героев нигде нет
    static readonly TimeSpan RetryEvery = TimeSpan.FromMinutes(10);
    static readonly TimeSpan GiveUp = TimeSpan.FromHours(6);
    static readonly Regex OwnHeroInName = new(@"_hero(\d+)_top\.png$");

    readonly PickWatcher _watcher;
    readonly Dictionary<string, DateTime> _tried = new();
    bool _busy;

    public SlotLearner(PickWatcher watcher) => _watcher = watcher;

    /// <summary>Разобрать один прошедший матч, если есть что; зовётся раз в минуту вне пика.</summary>
    public async Task TickAsync()
    {
        if (_busy || !Directory.Exists(Paths.Frames)) return;
        var dir = new DirectoryInfo(Paths.Frames).GetDirectories()
            .Where(d => long.TryParse(d.Name, out var id) && id > 0 && !File.Exists(Path.Combine(d.FullName, DoneFile)))
            .Where(d => DateTime.Now - d.LastWriteTime > After && (!_tried.TryGetValue(d.Name, out var at) || DateTime.Now - at > RetryEvery))
            .OrderBy(d => d.LastWriteTime)
            .FirstOrDefault();
        if (dir == null) return;
        _busy = true;
        try
        {
            _tried[dir.Name] = DateTime.Now;
            var truth = await DraftData.MatchHeroesAsync(long.Parse(dir.Name));
            if (truth != null) Finish(dir, Learn(dir, truth));
            else if (DateTime.Now - dir.LastWriteTime > GiveUp) Finish(dir, "героев матча нет ни в Stratz, ни в OpenDota");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or ExternalException)
        {
            Finish(dir, "не вышло: " + ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    // Последний кадр, где взяты все герои вашей команды (ваша команда — по вашему герою из имён кадров).
    string Learn(DirectoryInfo dir, int[] truth)
    {
        var tops = dir.GetFiles("*_top.png").OrderByDescending(f => f.Name, StringComparer.Ordinal).ToList();
        int own = tops.Select(f => OwnHeroInName.Match(f.Name)).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).FirstOrDefault(h => h > 0);
        int ownSlot = own > 0 ? Array.IndexOf(truth, own) : -1;
        if (ownSlot < 0) return "вашего героя нет среди героев матча";
        bool radiant = ownSlot < 5;
        foreach (var file in tops)
        {
            using var top = new Bitmap(file.FullName);
            using var full = new Bitmap(ScreenFrame.Width, ScreenFrame.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(full))
            {
                g.Clear(Color.Black);
                g.DrawImageUnscaled(top, 0, 0);
            }
            var frame = new ScreenFrame(full);
            if (!GridReader.TeamPicked(frame, radiant)) continue;
            var learn = _watcher.SlotsToLearn(frame, truth, radiant);
            if (learn == null) return $"{file.Name}: порядок слотов не сошёлся с героями матча — не запоминаю";
            if (learn.Count == 0) return "все герои вашей команды узнаны";
            Directory.CreateDirectory(Paths.SlotRefs);
            foreach (var (slot, hero, was) in learn)
            {
                using var crop = full.Clone(GridReader.SlotArea(slot), PixelFormat.Format24bppRgb);
                crop.Save(Path.Combine(Paths.SlotRefs, $"{hero}_auto_{dir.Name}.png"), ImageFormat.Png);
                _watcher.AddSlotRef(hero, crop);
                Trim(hero);
                Log.Write($"Эталон слота: герой {hero} из матча {dir.Name} ({was})");
            }
            return "запомнены герои " + string.Join(", ", learn.Select(l => l.Hero));
        }
        return "нет кадра, где взяты все герои вашей команды";
    }

    static void Trim(int hero)
    {
        foreach (var old in new DirectoryInfo(Paths.SlotRefs).GetFiles($"{hero}_auto_*.png")
                     .OrderByDescending(f => f.LastWriteTime).Skip(KeepPerHero))
            old.Delete();
    }

    // Папку кадров могли уже удалить (храним два последних матча) — тогда и отметить негде, и не нужно.
    static void Finish(DirectoryInfo dir, string result)
    {
        Log.Write($"Эталоны слотов, матч {dir.Name}: {result}");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, DoneFile), result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Отметка о разборе кадров не записана: " + ex.Message);
        }
    }
}
