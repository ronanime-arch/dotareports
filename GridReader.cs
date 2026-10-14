using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DotaReports;

/// <summary>Кадр экрана 1920×720 (верх экрана), байты BGR.</summary>
sealed class ScreenFrame
{
    public const int Width = 1920, Height = 720;
    readonly byte[] _data;
    readonly int _stride;

    public ScreenFrame(Bitmap bmp)
    {
        var bd = bmp.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        _stride = bd.Stride;
        _data = new byte[_stride * Height];
        Marshal.Copy(bd.Scan0, _data, 0, _data.Length);
        bmp.UnlockBits(bd);
    }

    public (int R, int G, int B) At(int x, int y)
    {
        int i = y * _stride + x * 3;
        return (_data[i + 2], _data[i + 1], _data[i]);
    }

    public bool Bright(int x, int y, int threshold)
    {
        var (r, g, b) = At(x, y);
        return r + g + b > threshold * 3;
    }
}

/// <summary>
/// Распознаёт по экрану пика (1920×1080): недоступных героев в сетке (бан — красная черта или недоступен с самого начала;
/// пик — серая заливка), чей это пик (по скачку слота верхней полосы в тот же момент), режим (TURBO / ALL PICK)
/// и вашу роль (подпись под ником в All Pick). Пороги подобраны на записанных пиках в D:\DotaReports\testdata.
/// </summary>
sealed class GridReader
{
    // Геометрия сетки: группы по атрибутам, шаг клеток 51×83, клетка 43×75; внутренняя область без рамки и полосы уровня.
    static readonly (int Attr, int X0, int Cols)[] Groups = { (0, 134, 6), (1, 455, 6), (2, 776, 6), (3, 1097, 4) };
    const int Y0 = 210, StepX = 51, StepY = 83, MaxRows = 6;
    const int InX0 = 4, InY0 = 4, PW = 35, PH = 54;
    // Индикаторы голосового чата слева («ник ⊘ FISH», второй ник ниже) заходят на первые столбцы силы — эти пиксели не сравниваем.
    static readonly Rectangle VoiceArea = new(0, 388, 200, 72);

    // Клетка относительно эталона (analyze_*.py):
    //  • заливка серым (пик, в All Pick и бан): контраст 0,12–0,5 при совпадении узора ≥ 0,62; у свободных контраст ≥ 0,91 в 99,9% кадров;
    //  • красная черта (бан в Турбо): «покраснение» на антидиагонали 12–25 при совпадении 0,42–0,8; у свободных ≤ 1,6 в 99%;
    //  • совпадение < 0,35 — клетку закрывает увеличенный портрет или окно.
    const double HiddenCorr = 0.35;
    const double WashCorrMin = 0.6, WashContrast = 0.6;
    const double SlashMin = 12, SlashCorrMin = 0.42, SlashCorrMax = 0.80, WashedSlashMin = 8;
    const double DimFrameLum = 0.85, DimFrameContrast = 0.6;
    const double BanWindowSec = 6;           // недоступные в первые секунды видимой сетки — баны (пики начинаются позже: 11–12 с)
    // Бан или пик. Турбо: бан — черта без заливки, пик — заливка без черты (по виду, в любой момент).
    // All Pick: все баны видны в первые 0–4 с, а позже залитым и даже перечёркнутым становится взятый героем (Marci на 12 с) —
    // там решает время: недоступен в первые 6 с — бан; с чертой в первые 10 с — тоже бан (Tinker на 7 с, а Zeus на 7 с — пик
    // без черты); позже — пик.
    const double ApSlashBanSec = 10;

    // Слоты верхней полосы: пик — скачок слота между соседними кадрами (Турбо 66–81, All Pick 50–70; предвыбор 16–58).
    // Серая заливка в сетке появляется на 0,5–2 с позже скачка, поэтому ищем скачок в окне [−3; +0,5] с
    // от момента недоступности и берём самый сильный (пик сильнее предвыбора).
    static readonly int[] RadiantSlotX = { 219, 343, 467, 591, 715 };
    static readonly int[] DireSlotX = { 1098, 1222, 1346, 1470, 1594 };
    const double SlotJump = 30, MatchBeforeSec = 3.0, MatchAfterSec = 0.5;
    const int GlobalJumpSlots = 6;           // скачут почти все слоты — переход экрана, не пик
    const double EmptySlotDiff = 25, SureSlotScore = 0.6;
    // Бледный предвыбор узнаётся хуже (ложные Io, AA) — «собираются взять» только при уверенном узнавании (Razor: 0,70 / 0,22).
    const double SurePlanScore = 0.6, SurePlanGap = 0.12;
    // Слот, где портрет не узнан, — среди взятых героев без слота: совпадение и запас до второго кандидата и до другого слота.
    // Мягче общих порогов (0,40 / 0,08), потому что кандидатов два-три, а не 127. Пороги не выверены на записанных пиках.
    const double TakenScore = 0.25, TakenGap = 0.08;

    readonly List<(int HeroId, int X, int Y)> _cells = new();
    readonly Dictionary<int, bool[]> _masks = new();     // пиксели клетки, которые не сравниваем (перекрыты интерфейсом)
    readonly Dictionary<int, byte[]> _refs = new();      // эталон клетки: PW×PH×3 (RGB)
    readonly HashSet<int> _provisional = new();
    readonly Dictionary<int, int> _provMismatch = new();
    readonly Dictionary<int, Queue<char>> _history = new(); // b — черта, w — заливка, a — свободен
    readonly Dictionary<int, double> _firstRaw = new();      // когда герой впервые выглядел недоступным
    readonly HashSet<int> _early = new();                    // недоступны с первых кадров — баны
    readonly double[]?[] _slotPrev = new double[10][];
    readonly List<(double T, int Slot, double Mag)> _slotEvents = new();
    readonly Dictionary<int, (int Pos, int Offset, bool[,] Mask)> _roleTemplates = new();
    readonly SlotReader _slotReader = new();
    readonly EnemyIconReader _iconReader = new();
    readonly HashSet<int> _iconEnemies = new();   // из блока «ПОЗИЦИИ ВРАГА» на экране героя
    readonly bool[] _slotSure = new bool[10];     // узнан уверенно — годится для «собираются взять»
    readonly int[] _slotRaw = new int[10];      // последний узнанный герой в слоте
    readonly int[] _slotHero = new int[10];     // подтверждённый двумя кадрами подряд
    readonly HashSet<int> _takenLogged = new();   // узнанные среди взятых — в журнал по разу за пик
    readonly double[]?[] _slotEmpty = new double[10][]; // как слот выглядел в начале пика (пустой: плашка ранга)
    readonly List<int> _assocAllies = new(), _assocEnemies = new();
    double? _firstValidAt;
    bool _refsDirty;

    public string Status { get; private set; } = "";
    public bool Enabled => _cells.Count > 0;
    public HashSet<int> Banned { get; } = new();
    public HashSet<int> Taken { get; } = new();
    /// <summary>Герои союзников: стали недоступны одновременно со скачком слота вашей команды (без вас).</summary>
    public List<int> Allies { get; } = new();
    /// <summary>Герои врагов: в All Pick — их пики, в Турбо — засвеченные при конфликте выбора.</summary>
    public List<int> Enemies { get; } = new();
    /// <summary>Предвыбор союзников: полупрозрачный портрет в их слоте, герой ещё свободен.</summary>
    public List<int> Planned { get; } = new();
    /// <summary>"TURBO", "ALL_PICK" или null, пока не распознан (или кадры противоречат друг другу).</summary>
    public string? Mode { get; private set; }
    readonly Dictionary<string, int> _modeVotes = new();
    /// <summary>Ваша роль 1..5 по подписи под ником (All Pick), 0 — не видна.</summary>
    public int Position { get; private set; }

    /// <summary>Для --grid-test: скачки слотов и моменты, когда герои стали недоступны.</summary>
    public string Debug => "скачки: " + string.Join(" ", _slotEvents.Select(e => $"{(e.Slot < 5 ? "R" : "D")}{e.Slot % 5 + 1}@{e.T:0.#}:{e.Mag:0}"))
        + " | недоступны с: " + string.Join(" ", _firstRaw.OrderBy(kv => kv.Value).Select(kv => $"{kv.Key}@{kv.Value:0.#}"))
        + " | в слотах: " + string.Join(" ", Enumerable.Range(0, 10).Where(s => _slotHero[s] != 0).Select(s => $"{(s < 5 ? "R" : "D")}{s % 5 + 1}={_slotHero[s]}"));

    public GridReader()
    {
        LoadCells();
        LoadRefs();
        LoadRoleTemplates();
    }

    /// <summary>Новый пик: сбросить состояние матча (эталоны остаются).</summary>
    public void Reset()
    {
        _history.Clear();
        _firstRaw.Clear();
        _early.Clear();
        Array.Clear(_slotPrev);
        _slotEvents.Clear();
        _firstValidAt = null;
        Banned.Clear();
        Taken.Clear();
        Allies.Clear();
        Enemies.Clear();
        Planned.Clear();
        _assocAllies.Clear();
        _assocEnemies.Clear();
        Array.Clear(_slotRaw);
        Array.Clear(_slotEmpty);
        Array.Clear(_slotSure);
        _iconEnemies.Clear();
        Array.Clear(_slotHero);
        _takenLogged.Clear();
        _modeVotes.Clear();
        Mode = null;
        Position = 0;
    }

    /// <summary>Обработать кадр. true — изменилось что-то, что показывает карточка.</summary>
    public bool Process(ScreenFrame f, double t, string? team, int ownHero, int ownSlot)
    {
        bool radiant = !string.Equals(team, "dire", StringComparison.OrdinalIgnoreCase);
        int ownIndex = ownSlot is >= 0 and < 5 ? (radiant ? ownSlot : ownSlot + 5) : -1;
        bool changed = DetectModeAndRole(f, ownIndex);

        if (!Enabled) return changed;
        if (!GridVisible(f))
        {
            // После вашего пика сетки нет — врагов берём из блока «ПОЗИЦИИ ВРАГА» на экране героя.
            int before = _iconEnemies.Count;
            foreach (var hero in _iconReader.Read(f)) _iconEnemies.Add(hero);
            if (_iconEnemies.Count != before) changed |= Combine(radiant, ownHero, ownIndex);
            return changed;
        }

        var lumRatios = new List<double>();
        var stdRatios = new List<double>();
        var learn = new List<(int Hero, byte[] Patch)>();
        var measured = new List<(int Hero, int X, int Y, byte[] Patch, double Corr, double Std, double Slash)>();
        var hidden = new List<(int X, int Y)>();
        foreach (var (hero, x, y) in _cells)
        {
            var patch = Patch(f, x, y);
            if (!_refs.TryGetValue(hero, out var rf))
            {
                learn.Add((hero, patch));
                continue;
            }
            var (corr, lumRatio, stdRatio, slash) = Compare(patch, rf, _masks.GetValueOrDefault(hero));
            lumRatios.Add(lumRatio);
            stdRatios.Add(stdRatio);
            if (corr < HiddenCorr)
            {
                hidden.Add((x, y));
                // временный эталон мог попасть под увеличенный портрет — если клетка трижды подряд «другая», берём её заново
                if (_provisional.Contains(hero) && (_provMismatch[hero] = _provMismatch.GetValueOrDefault(hero) + 1) >= 3)
                    learn.Add((hero, patch));
                continue;
            }
            _provMismatch.Remove(hero);
            measured.Add((hero, x, y, patch, corr, stdRatio, slash));
        }
        if (lumRatios.Count == 0) return changed;
        lumRatios.Sort();
        stdRatios.Sort();
        // вся сетка затемнена или залита (переходы, окна поверх) — кадр пропускаем
        if (lumRatios[lumRatios.Count / 2] < DimFrameLum || stdRatios[stdRatios.Count / 2] < DimFrameContrast) return changed;
        _firstValidAt ??= t;

        var cls = new Dictionary<int, char>();
        foreach (var (hero, x, y, patch, corr, stdRatio, slash) in measured)
        {
            bool washed = corr >= WashCorrMin && stdRatio < WashContrast;
            bool slashed = (slash >= SlashMin && corr >= SlashCorrMin && corr <= SlashCorrMax) || (washed && slash >= WashedSlashMin);
            if (washed || slashed)
            {
                // рядом с увеличенным портретом под курсором не решаем: его край даёт и черту, и «заливку»
                if (hidden.Any(hp => Math.Abs(hp.X - x) <= StepX && Math.Abs(hp.Y - y) <= StepY)) continue;
                cls[hero] = slashed ? 'b' : 'w';
                continue;
            }
            cls[hero] = 'a';
            // Уточняем только временные эталоны: тот же портрет с заметно большим контрастом или та же клетка без черты.
            // Готовые эталоны не трогаем — иначе туда попала бы подсветка наведённого героя.
            if (_provisional.Contains(hero) && ((corr >= 0.85 && stdRatio > 1.5 && slash < 5) || (corr >= 0.4 && slash <= -SlashMin)))
                learn.Add((hero, patch));
        }

        foreach (var (hero, patch) in learn)
        {
            if (_refs.ContainsKey(hero)) _provisional.Remove(hero); else _provisional.Add(hero);
            _provMismatch.Remove(hero);
            _refs[hero] = patch;
            _refsDirty = true;
        }

        foreach (var (hero, c) in cls)
        {
            var q = _history.TryGetValue(hero, out var h) ? h : _history[hero] = new Queue<char>();
            q.Enqueue(c);
            if (q.Count > 3) q.Dequeue();
            if (c != 'a')
            {
                _firstRaw.TryAdd(hero, t);
                if (t - _firstValidAt <= BanWindowSec) _early.Add(hero);
            }
            // недоступен — два последних кадра подряд (кадры раз в 0,5 с: успевает до вашего выбора); свободен — 2 из 3
            var last = q.ToArray();
            bool unavailable = last.Length >= 2 && last[^1] != 'a' && last[^2] != 'a';
            bool available = last.Count(v => v == 'a') >= 2;
            if (!unavailable && !available) continue;
            bool slashed = last[^1] == 'b' || last[^2] == 'b';
            bool earlySlash = slashed && _firstRaw.TryGetValue(hero, out var since) && since - _firstValidAt <= ApSlashBanSec;
            bool ban = unavailable && (_early.Contains(hero) || (Mode == "ALL_PICK" ? earlySlash : slashed));
            changed |= Set(Banned, hero, ban);
            changed |= Set(Taken, hero, unavailable && !ban);
            if (available)
            {
                _firstRaw.Remove(hero);
                _early.Remove(hero);
            }
        }

        ReadSlotHeroes(f, ownIndex, ownHero, radiant);   // сначала портреты: узнанный слот закрепляет свой скачок за своим героем
        UpdateSlots(f, t, radiant, ownHero, ownIndex);
        changed |= Combine(radiant, ownHero, ownIndex);
        return changed;
    }

    // Портрет в слоте: подтверждаем двумя кадрами подряд, чтобы не цеплять кадры перехода.
    void ReadSlotHeroes(ScreenFrame f, int ownIndex, int ownHero, bool radiant)
    {
        if (!_slotReader.Enabled) return;
        var heroes = new int[10];
        var sure = new bool[10];
        var open = new List<int>(); // слот изменился с начала пика, а герой в нём не узнан
        for (int s = 0; s < 10; s++)
        {
            if (s == ownIndex) continue;
            int x = SlotX(s);
            var (hero, score, gap) = _slotReader.Read(f, x);
            // Пустой слот с плашкой ранга похож на Shadow Demon (0,44–0,45): героя признаём, только если слот
            // изменился с начала пика (средняя разница ≥ 25) или совпадение очень уверенное.
            var look = SlotVector(f, x);
            _slotEmpty[s] ??= look;
            double diff = 0;
            for (int i = 0; i < look.Length; i++) diff += Math.Abs(look[i] - _slotEmpty[s]![i]);
            bool changed = diff / look.Length >= EmptySlotDiff;
            if (!changed && score < SureSlotScore) hero = 0;
            heroes[s] = hero;
            sure[s] = score >= SurePlanScore && gap >= SurePlanGap;
            // в Турбо вражеские пики скрыты — там ищем только в слотах своей команды
            if (hero == 0 && changed && (Mode == "ALL_PICK" || (s < 5) == radiant)) open.Add(s);
        }
        MatchTaken(f, heroes, open, ownHero);
        for (int s = 0; s < 10; s++)
        {
            if (s == ownIndex) continue;
            int hero = heroes[s];
            if (hero != 0 && hero == _slotRaw[s]) { _slotHero[s] = hero; _slotSure[s] = sure[s]; }
            else if (hero == 0 && _slotRaw[s] == 0) _slotHero[s] = 0;
            _slotRaw[s] = hero;
        }
    }

    // Портрет не узнан среди всех героев (облик из набора, персона), а в сетке есть взятые герои без слота:
    // такие слоты сравниваем только с ними. Пара — когда слот и герой лучшие друг для друга с запасом:
    // так в слот не попадёт чужой герой, если его настоящий слот похож на портрет ещё хуже.
    // Не по времени пика: связь «скачок слота — серая клетка» теряется, если кадр пика пропущен.
    void MatchTaken(ScreenFrame f, int[] heroes, List<int> open, int ownHero)
    {
        if (open.Count == 0) return;
        var placed = Enumerable.Range(0, 10).Select(s => heroes[s] != 0 || open.Contains(s) ? heroes[s] : _slotHero[s]).ToHashSet();
        var free = Taken.Where(h => h != ownHero && !placed.Contains(h)).ToList();
        if (free.Count == 0) return;
        var table = open.Select(s => (Slot: s, Scores: _slotReader.Scores(f, SlotX(s), free).ToDictionary(p => p.Hero, p => p.Score))).ToList();
        foreach (var (slot, scores) in table)
        {
            var ranked = scores.OrderByDescending(kv => kv.Value).ToList();
            if (ranked.Count == 0) continue;
            var (hero, score) = (ranked[0].Key, ranked[0].Value);
            if (score < TakenScore || (ranked.Count > 1 && score - ranked[1].Value < TakenGap)) continue;
            if (table.Any(o => o.Slot != slot && o.Scores.TryGetValue(hero, out var other) && score - other < TakenGap)) continue;
            heroes[slot] = hero;
            if (_takenLogged.Add(hero))
                Log.Write($"Пик: слот {(slot < 5 ? "R" : "D")}{slot % 5 + 1} — герой {hero} среди взятых без слота ({score:0.00}; кандидатов {free.Count}, слотов {open.Count})");
        }
    }

    static int SlotX(int s) => s < 5 ? RadiantSlotX[s] : DireSlotX[s - 5];

    // Итог: союзники и враги — по скачкам слотов и по узнанным портретам; предвыбор — портрет союзника, чей герой ещё свободен.
    bool Combine(bool radiant, int ownHero, int ownIndex)
    {
        // Узнанный в слоте портрет точнее сопоставления по времени (одновременные пики обеих команд) — он и решает сторону.
        var slotSide = new Dictionary<int, bool>();   // герой → враг?
        var planned = new List<int>();
        for (int s = 0; s < 10; s++)
        {
            int hero = _slotHero[s];
            if (hero == 0 || s == ownIndex || hero == ownHero || Banned.Contains(hero)) continue;
            bool enemy = (s < 5) != radiant;
            if (enemy || Taken.Contains(hero)) slotSide[hero] = enemy;
            else if (_slotSure[s] && !planned.Contains(hero)) planned.Add(hero);
        }
        foreach (var hero in _iconEnemies)
            if (hero != ownHero && !Banned.Contains(hero)) slotSide[hero] = true;
        var allies = _assocAllies.Where(h => !slotSide.TryGetValue(h, out var e) || !e).ToList();
        var enemies = _assocEnemies.Where(h => !slotSide.TryGetValue(h, out var e) || e).ToList();
        foreach (var (hero, enemy) in slotSide)
        {
            var list = enemy ? enemies : allies;
            if (!list.Contains(hero)) list.Add(hero);
        }
        if (allies.SequenceEqual(Allies) && enemies.SequenceEqual(Enemies) && planned.SequenceEqual(Planned)) return false;
        Allies.Clear(); Allies.AddRange(allies);
        Enemies.Clear(); Enemies.AddRange(enemies);
        Planned.Clear(); Planned.AddRange(planned);
        return true;
    }

    public void SaveRefs()
    {
        if (!_refsDirty) return;
        var dir = Path.Combine(Paths.Root, "refs");
        Directory.CreateDirectory(dir);
        foreach (var (hero, patch) in _refs)
            SavePatch(Path.Combine(dir, $"{hero}.png"), patch);
        File.WriteAllLines(Path.Combine(dir, "provisional.txt"), _provisional.Select(h => h.ToString()));
        _refsDirty = false;
    }

    void UpdateSlots(ScreenFrame f, double t, bool radiant, int ownHero, int ownIndex)
    {
        var jumps = new List<(int Slot, double Mag)>();
        for (int s = 0; s < 10; s++)
        {
            var v = SlotVector(f, s < 5 ? RadiantSlotX[s] : DireSlotX[s - 5]);
            if (_slotPrev[s] is { } prev)
            {
                double diff = 0;
                for (int i = 0; i < v.Length; i++) diff += Math.Abs(v[i] - prev[i]);
                if (diff / v.Length >= SlotJump) jumps.Add((s, diff / v.Length));
            }
            _slotPrev[s] = v;
        }
        if (jumps.Count < GlobalJumpSlots)
            foreach (var (s, mag) in jumps.Where(j => j.Slot != ownIndex))
                _slotEvents.Add((t, s, mag));

        // Каждому новому пику — ближайший по времени скачок слота (не вашего); ваш собственный герой не считается.
        var used = new HashSet<int>();
        var enemies = new List<int>();
        var allies = new List<int>();
        foreach (var hero in Taken.Where(h => h != ownHero && _firstRaw.ContainsKey(h)).OrderBy(h => _firstRaw[h]))
        {
            double at = _firstRaw[hero];
            int best = -1;
            for (int i = 0; i < _slotEvents.Count; i++)
            {
                double dt = _slotEvents[i].T - at;
                if (used.Contains(i) || dt < -MatchBeforeSec || dt > MatchAfterSec) continue;
                // В слоте узнан другой герой — скачок принадлежит ему. Иначе в All Pick одновременный пик врага
                // (пустой слот → портрет, скачок 68–78) перебивал пик союзника (предвыбор → пик, скачок 37–44).
                int slotHero = _slotHero[_slotEvents[i].Slot];
                if (slotHero != 0 && slotHero != hero) continue;
                if (best < 0 || _slotEvents[i].Mag > _slotEvents[best].Mag) best = i;
            }
            if (best < 0) continue;
            used.Add(best);
            bool enemy = (_slotEvents[best].Slot < 5) != radiant;
            (enemy ? enemies : allies).Add(hero);
        }
        _assocEnemies.Clear();
        _assocEnemies.AddRange(enemies);
        _assocAllies.Clear();
        _assocAllies.AddRange(allies);
    }

    // Режим — по ширине надписи под таймером: «TURBO» x 930–990, «ALL PICK» 926–998.
    // Роль — подпись под вашим ником сравниваем с образцами (roles\*.png, сдвиг ±15 px).
    bool DetectModeAndRole(ScreenFrame f, int ownIndex)
    {
        bool changed = false;
        int left = -1, right = -1;
        for (int x = 880; x < 1040; x++)
            for (int y = 68; y < 86; y++)
                if (f.Bright(x, y, 170)) { if (left < 0) left = x; right = x; break; }
        string? mode = left < 0 ? null : (right - left) switch { >= 66 and <= 80 => "ALL_PICK", >= 52 and <= 64 => "TURBO", _ => null };
        // режим — когда надпись прочитана одинаково хотя бы в 3 кадрах и в 80% прочтений: блик или подсказка поверх
        // надписи не переключают режим, а противоречивые кадры оставляют его неизвестным
        if (mode != null) _modeVotes[mode] = _modeVotes.GetValueOrDefault(mode) + 1;
        var top = _modeVotes.OrderByDescending(kv => kv.Value).FirstOrDefault();
        string? voted = top.Value >= 3 && top.Value >= 0.8 * _modeVotes.Values.Sum() ? top.Key : null;
        if (voted != Mode) { Mode = voted; changed = true; }

        if (ownIndex >= 0 && _roleTemplates.Count > 0 && Mode == "ALL_PICK")
        {
            int cx = (ownIndex < 5 ? RadiantSlotX[ownIndex] : DireSlotX[ownIndex - 5]) + 53;
            var scores = _roleTemplates.Values.Select(r => (r.Pos, Score: RoleScore(f, cx, r.Offset, r.Mask))).OrderByDescending(s => s.Score).ToList();
            if (scores[0].Score >= 0.6 && scores[0].Score - scores[1].Score >= 0.15 && scores[0].Pos != Position)
            {
                Position = scores[0].Pos;
                changed = true;
            }
        }
        return changed;
    }

    static double RoleScore(ScreenFrame f, int cx, int offset, bool[,] mask)
    {
        int h = mask.GetLength(0), w = mask.GetLength(1);
        double best = 0;
        for (int dx = -15; dx <= 15; dx++)
        {
            int inter = 0, union = 0;
            for (int yy = 0; yy < h; yy++)
                for (int xx = 0; xx < w; xx++)
                {
                    int x = cx + offset + dx + xx;
                    bool c = x >= 0 && x < ScreenFrame.Width && White(f.At(x, RoleY0 + yy));
                    if (mask[yy, xx] && c) inter++;
                    if (mask[yy, xx] || c) union++;
                }
            if (union > 0) best = Math.Max(best, inter / (double)union);
        }
        return best;
    }

    // Подпись роли — белый текст: все три канала ярче 140. Цветная полоса игрока над ней (оранжевая, голубая…)
    // по одному из каналов тёмная и в образец не попадает — иначе на стороне Dire «Центр» узнавался на 0,46.
    const int RoleY0 = 104;
    static bool White((int R, int G, int B) p) => Math.Min(p.R, Math.Min(p.G, p.B)) > 140;

    static bool Set(HashSet<int> set, int hero, bool on) => on ? set.Add(hero) : set.Remove(hero);

    // Значки атрибутов над сеткой: сила — красно-оранжевый, ловкость — зелёный, интеллект — голубой.
    static bool GridVisible(ScreenFrame f)
    {
        var s = Avg(f, 138, 190);
        var a = Avg(f, 460, 190);
        var i = Avg(f, 781, 190);
        return s.R > s.B + 60 && a.G > a.R + 40 && a.G > a.B + 20 && i.B > i.R + 40;
    }

    static (double R, double G, double B) Avg(ScreenFrame f, int cx, int cy)
    {
        double r = 0, g = 0, b = 0;
        for (int dy = -3; dy <= 3; dy++)
            for (int dx = -3; dx <= 3; dx++)
            {
                var p = f.At(cx + dx, cy + dy);
                r += p.R; g += p.G; b += p.B;
            }
        return (r / 49, g / 49, b / 49);
    }

    static byte[] Patch(ScreenFrame f, int x, int y)
    {
        var p = new byte[PW * PH * 3];
        int k = 0;
        for (int yy = 0; yy < PH; yy++)
            for (int xx = 0; xx < PW; xx++)
            {
                var (r, g, b) = f.At(x + InX0 + xx, y + InY0 + yy);
                p[k++] = (byte)r; p[k++] = (byte)g; p[k++] = (byte)b;
            }
        return p;
    }

    static double[] SlotVector(ScreenFrame f, int x)
    {
        var v = new List<double>();
        for (int yy = 6; yy < 58; yy += 2)
            for (int xx = x + 6; xx < x + 100; xx += 2)
            {
                var (r, g, b) = f.At(xx, yy);
                v.Add(r); v.Add(g); v.Add(b);
            }
        return v.ToArray();
    }

    /// <summary>Совпадение узора яркости, отношения яркости и контраста, «покраснение» на антидиагонали относительно эталона.</summary>
    static (double Corr, double LumRatio, double StdRatio, double Slash) Compare(byte[] cur, byte[] rf, bool[]? skip)
    {
        int n = 0;
        double sa = 0, sb = 0, saa = 0, sbb = 0, sab = 0, on = 0, off = 0;
        int nOn = 0, nOff = 0;
        for (int k = 0; k < PW * PH; k++)
        {
            if (skip != null && skip[k]) continue;
            n++;
            int i = k * 3;
            double a = (cur[i] + cur[i + 1] + cur[i + 2]) / 3.0;
            double b = (rf[i] + rf[i + 1] + rf[i + 2]) / 3.0;
            sa += a; sb += b; saa += a * a; sbb += b * b; sab += a * b;
            int yy = k / PW, xx = k % PW;
            double ex = (PW - 1) - yy * (PW - 1) / (double)(PH - 1);
            double red = (cur[i] - (cur[i + 1] + cur[i + 2]) / 2.0) - (rf[i] - (rf[i + 1] + rf[i + 2]) / 2.0);
            if (Math.Abs(xx - ex) <= 2.5) { on += red; nOn++; } else { off += red; nOff++; }
        }
        double va = saa - sa * sa / n, vb = sbb - sb * sb / n;
        double corr = va < 1e-6 || vb < 1e-6 ? 0 : (sab - sa * sb / n) / Math.Sqrt(va * vb);
        return (corr, sa / Math.Max(sb, 1), Math.Sqrt(Math.Max(va, 0) / Math.Max(vb, 1e-6)), on / Math.Max(nOn, 1) - off / Math.Max(nOff, 1));
    }

    void LoadCells()
    {
        if (!File.Exists(Paths.HeroList))
        {
            Status = "нет списка героев Valve — распознавание выключено";
            return;
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(Paths.HeroList));
        var heroes = doc.RootElement.GetProperty("result").GetProperty("data").GetProperty("heroes").EnumerateArray()
            .Select(h => (Id: h.GetProperty("id").GetInt32(), Name: h.GetProperty("name_loc").GetString() ?? "", Attr: h.GetProperty("primary_attr").GetInt32()))
            .ToList();
        var cells = new List<(int, int, int)>();
        foreach (var (attr, x0, cols) in Groups)
        {
            // Порядок в игре — по имени, без учёта регистра, посимвольно (культурное сравнение путает Earth Spirit и Earthshaker)
            var group = heroes.Where(h => h.Attr == attr).OrderBy(h => h.Name.ToLowerInvariant(), StringComparer.Ordinal).ToList();
            if (group.Count > cols * MaxRows)
            {
                Status = $"в группе атрибута {attr} {group.Count} героев — сетка изменилась, распознавание выключено";
                return;
            }
            for (int i = 0; i < group.Count; i++)
                cells.Add((group[i].Id, x0 + StepX * (i % cols), Y0 + StepY * (i / cols)));
        }
        foreach (var (hero, x, y) in cells)
        {
            var skip = new bool[PW * PH];
            bool any = false;
            for (int yy = 0; yy < PH; yy++)
                for (int xx = 0; xx < PW; xx++)
                    if (VoiceArea.Contains(x + InX0 + xx, y + InY0 + yy)) skip[yy * PW + xx] = any = true;
            if (any) _masks[hero] = skip;
        }
        _cells.AddRange(cells);
        Status = $"распознавание сетки: {_cells.Count} героев";
    }

    void LoadRefs()
    {
        var dir = Path.Combine(Paths.Root, "refs");
        if (!Directory.Exists(dir)) return;
        foreach (var file in Directory.GetFiles(dir, "*.png"))
            if (int.TryParse(Path.GetFileNameWithoutExtension(file), out var hero))
            {
                using var bmp = new Bitmap(file);
                if (bmp.Width != PW || bmp.Height != PH) continue;
                var p = new byte[PW * PH * 3];
                int k = 0;
                for (int y = 0; y < PH; y++)
                    for (int x = 0; x < PW; x++)
                    {
                        var c = bmp.GetPixel(x, y);
                        p[k++] = c.R; p[k++] = c.G; p[k++] = c.B;
                    }
                _refs[hero] = p;
            }
        var prov = Path.Combine(dir, "provisional.txt");
        if (File.Exists(prov))
            foreach (var line in File.ReadAllLines(prov))
                if (int.TryParse(line, out var h)) _provisional.Add(h);
    }

    // Образцы подписей ролей сняты со скриншота пика All Pick (12.webp): roles\{позиция}.png + offsets.json (смещение от центра слота).
    void LoadRoleTemplates()
    {
        var dir = Path.Combine(Paths.Root, "roles");
        var offsetsFile = Path.Combine(dir, "offsets.json");
        if (!File.Exists(offsetsFile)) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(offsetsFile));
        foreach (var p in doc.RootElement.EnumerateObject())
        {
            var file = Path.Combine(dir, p.Name + ".png");
            if (!int.TryParse(p.Name, out var pos) || !File.Exists(file)) continue;
            using var bmp = new Bitmap(file);
            var mask = new bool[bmp.Height, bmp.Width];
            for (int y = 0; y < bmp.Height; y++)
                for (int x = 0; x < bmp.Width; x++)
                    mask[y, x] = bmp.GetPixel(x, y).R > 127;
            _roleTemplates[pos] = (pos, p.Value.GetInt32(), mask);
        }
    }

    static void SavePatch(string path, byte[] p)
    {
        using var bmp = new Bitmap(PW, PH, PixelFormat.Format24bppRgb);
        int k = 0;
        for (int y = 0; y < PH; y++)
            for (int x = 0; x < PW; x++, k += 3)
                bmp.SetPixel(x, y, Color.FromArgb(p[k], p[k + 1], p[k + 2]));
        bmp.Save(path, ImageFormat.Png);
    }
}
