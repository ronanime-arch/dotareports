using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DotaReports;

/// <summary>
/// Кастомка «IM Пробуждение» (NEC, мастерская 3320645051): на 0, 10 и 15 минуте каждый герой получает свои усиления.
/// Их описания лежат в самой кастомке — resource/addon_russian.txt внутри её .vpk (путь присылает GSI в customgamename):
/// ключи npc_dota_hero_{герой}_Awakening{0|1|2}_{способность}. Названия способностей — из списка Valve (abilitylist).
/// </summary>
sealed class AwakeningInfo
{
    public static readonly int[] StageMinutes = { 0, 10, 15 };
    const int MaxText = 90; // одно усиление в строке карточки и окна — длиннее обрезаем

    static readonly Regex KeyValue = new("^\\s*\"([^\"]+)\"\\s+\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.Compiled);
    static readonly Regex Key = new("^npc_dota_hero_(.+?)_Awakening([012])_(.+)$", RegexOptions.Compiled);
    static readonly Regex Tag = new("<[^>]+>", RegexOptions.Compiled);
    static (string Path, DateTime Stamp, AwakeningInfo? Info) _cache;

    // герой (npc_dota_hero_…) → три ступени, в каждой — усиления «Способность: что даёт»
    readonly Dictionary<string, List<string>[]> _heroes = new();

    public int Count => _heroes.Count;

    /// <summary>Все герои — для --awakening-test.</summary>
    public IReadOnlyDictionary<string, List<string>[]> All => _heroes;

    /// <summary>Усиления героя по ступеням (0, 10, 15 мин); null — для героя описаний нет.</summary>
    public List<string>[]? Of(string? heroName) => heroName != null && _heroes.TryGetValue(heroName, out var s) ? s : null;

    /// <summary>
    /// Описания из .vpk кастомки; null — не та кастомка (нет таких ключей) или файл не прочитать.
    /// Разобранное помнится, пока файл не обновится.
    /// </summary>
    public static AwakeningInfo? Load(string? vpkPath, IReadOnlyDictionary<string, string> abilityNames)
    {
        if (string.IsNullOrEmpty(vpkPath) || !File.Exists(vpkPath)) return null;
        var stamp = File.GetLastWriteTimeUtc(vpkPath);
        if (_cache.Path == vpkPath && _cache.Stamp == stamp) return _cache.Info;
        AwakeningInfo? info = null;
        try
        {
            if (ReadText(vpkPath) is { } text) info = Parse(text, abilityNames);
            Log.Write(info is { Count: > 0 }
                ? $"Пробуждение: описания {info.Count} героев из {Path.GetFileName(vpkPath)}"
                : $"Пробуждение: в {Path.GetFileName(vpkPath)} описаний нет");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or NotSupportedException)
        {
            Log.Write("Пробуждение: файл кастомки не прочитан: " + ex.Message);
        }
        if (info is { Count: 0 }) info = null;
        _cache = (vpkPath, stamp, info);
        return info;
    }

    /// <summary>Файл локализации кастомки из её .vpk (русский, а нет — английский); null — такого нет.</summary>
    public static string? ReadText(string vpkPath) =>
        (Vpk.Read(vpkPath, "resource/addon_russian.txt") ?? Vpk.Read(vpkPath, "resource/addon_english.txt")) is { } bytes ? Decode(bytes) : null;

    // Файлы локализации Valve бывают и в UTF-16 (с BOM FF FE), и в UTF-8
    static string Decode(byte[] bytes) =>
        bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE ? Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2)
        : bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
        : Encoding.UTF8.GetString(bytes);

    /// <param name="simplify">Коротко (<see cref="AwakeningText"/>); false — как в кастомке, для сравнения в --awakening-test.</param>
    public static AwakeningInfo Parse(string text, IReadOnlyDictionary<string, string> abilityNames, bool simplify = true)
    {
        var info = new AwakeningInfo();
        foreach (var line in text.Split('\n'))
        {
            var kv = KeyValue.Match(line);
            if (!kv.Success) continue;
            var key = Key.Match(kv.Groups[1].Value);
            if (!key.Success) continue;
            string hero = "npc_dota_hero_" + key.Groups[1].Value;
            int stage = key.Groups[2].Value[0] - '0';
            string value = Regex.Replace(Tag.Replace(kv.Groups[2].Value.Replace("\\n", " ").Replace("\\\"", "\""), ""), "\\s+", " ").Trim();
            if (simplify) value = AwakeningText.Simplify(value);
            if (value.Length == 0) continue;
            if (value.Length > MaxText) value = value[..(MaxText - 1)].TrimEnd() + "…";
            // своё «способность» у кастомки (pudge_awaken, antimage_base) в списке Valve не найдётся — тогда без названия
            string entry = abilityNames.TryGetValue(key.Groups[3].Value, out var name) ? $"{name}: {value}" : value;
            if (!info._heroes.TryGetValue(hero, out var stages))
                info._heroes[hero] = stages = new[] { new List<string>(), new List<string>(), new List<string>() };
            stages[stage].Add(entry);
        }
        // в ступени — по названию способности; без названия (усиления героя целиком) — первыми
        foreach (var stages in info._heroes.Values)
            foreach (var list in stages)
                list.Sort((a, b) => (Named(a), Named(b)) switch
                {
                    (true, false) => 1,
                    (false, true) => -1,
                    _ => string.Compare(a, b, StringComparison.OrdinalIgnoreCase),
                });
        return info;
    }

    // «Blink: +500 дальность» — с названием способности из списка Valve
    static bool Named(string entry) => Regex.IsMatch(entry, "^[A-Z][^:]{0,40}: ");

    /// <summary>Названия способностей по внутренним именам из списка Valve (abilitylist): ищем все пары name / name_loc.</summary>
    public static Dictionary<string, string> AbilityNames()
    {
        var result = new Dictionary<string, string>();
        try
        {
            if (!File.Exists(Paths.AbilityList)) return result;
            using var doc = JsonDocument.Parse(File.ReadAllText(Paths.AbilityList));
            void Walk(JsonElement e)
            {
                if (e.ValueKind == JsonValueKind.Array)
                    foreach (var x in e.EnumerateArray()) Walk(x);
                else if (e.ValueKind == JsonValueKind.Object)
                {
                    if (e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                        && e.TryGetProperty("name_loc", out var l) && l.ValueKind == JsonValueKind.String && l.GetString() is { Length: > 0 } loc)
                        result[n.GetString()!] = loc;
                    foreach (var p in e.EnumerateObject()) Walk(p.Value);
                }
            }
            Walk(doc.RootElement);
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            Log.Write("Список способностей не прочитан: " + ex.Message);
        }
        return result;
    }
}

/// <summary>
/// Описание усиления Пробуждения — коротко и одинаково: «+50–200 урон», «−50% cd», «нет cd», «×2 эффект», «×3 подряд».
/// Без «к» и косвенных падежей («+400 к радиусу действия» → «+400 радиус»), уровни — диапазоном. В .vpk кастомки есть
/// строки по-белорусски и с иероглифами — известные куски переведены, незнакомое остаётся как есть.
/// </summary>
static class AwakeningText
{
    const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    const string Num = @"[+−]?\d+(?:\.\d+)?(?:/\d+(?:\.\d+)?)*%?";

    // белорусские и машинно переведённые куски (по .vpk от 15.10.2026) — до разбора по запятым
    static readonly (Regex From, string To)[] Phrases =
    {
        (new("−?50% да скарачэння часу аднаўлення", I), "−50% cd"),
        (new(@"Скарачэнне часу аднаўлення −50%\s*", I), "−50% cd, "),
        (new("падвойнае максімальнае аднаўленне здароўя", I), "×2 макс. лечение"),
        (new("да (?:дальнасці|далёкасці) чаравання|дальнасці (?:чаравання|дзеяння)", I), "дальность"),
        (new("да шкоды ад выбуху", I), "урон взрыва"),
        (new("да ўрону ў секунду", I), "урон/с"),
        (new("да плошчы дзеяння", I), "радиус"),
        (new("позірк з плошчай дзеяння", I), "взгляд по площади"),
        (new("зарадаў", I), "зарядов"),
        (new("адлегласць цягі", I), "дальность притяжения"),
        (new("Час зарадкі складае 1 хвіліну", I), "заряд за 1 мин"),
        (new("Поўная хуткасць атакі для вас і вашых юнітаў", I), "макс. скорость атаки вам и юнитам"),
        (new("Зямны воін атрымлівае ў спадчыну эфекты рыштунку", I), "земной дух получает эффекты предметов"),
        (new("каэфіцыент сілы пашкоджанняў", I), "множитель урона от силы"),
        (new(@"Падчас дуэлі вам будзе наносіць шкоду толькі іншы дуэлянт\.?", I), "в дуэли вас бьёт только соперник"),
        (new("да максімальнай рэгенерацыі/шкоды ад здароўя", I), "реген/урон от макс. здоровья"),
        (new("падвойны бонус здароўя за забойствы", I), "×2 бонус здоровья за убийства"),
        (new("Працягласць эфекту накладання ў секундах", I), "с длительность"),
        (new("Дух мядзведзя атрымлівае ў спадчыну атаку друіда", I), "медведь получает атаку друида"),
        (new(@",?\s*У цяперашні час.*$", I), ""),
        (new(@"\.?\s*Час аглушэння.*$", I), ""),
        (new("Эфектыўны супраць абарончых вежаў", I), "действует на башни"),
        (new(@"(\d+)-кратны эфект", I), "×$1 эффект"),
        (new("пастаянная трансфармацыя", I), "постоянная трансформация"),
        (new("Неабмежаваны час атакі", I), "без лимита атак"),
        (new("да зоны ўздзеяння мэты", I), "радиус цели"),
        (new("да зоны захопу дрэў", I), "радиус захвата деревьев"),
        (new("бонус да сілы", I), "бонус силы"),
        (new(@"Пашкоджанне/далекасць падвоеныя,?\s*кінута двойчы запар\.?", I), "×2 урон и дальность, ×2 подряд"),
        (new(@"Сіла атакі павялічана на (\d+%)", I), "+$1 урон атаки"),
        (new(@"С каждым уровнем дальность применения '[^']+' \S+ увеличивается на (\d+)\.?", I), "+$1 дальность за уровень,"),
        (new(@"Дополнительно крюк наносит урон,?\s*равный ([\d/%]+) от дистанции,?\s*на которую оттянута цель\.?", I), "урон $1 от дистанции"),
        (new("методическое расстояние", I), "дальность"),
        (new(@"При применении одновременно активирует текущий уровень умений '([^']+)' и '([^']+)'?", I), "заодно срабатывают «$1» и «$2»"),
        (new(@"(\d+)% шанс (\d+)-кратного мультикаста", I), "×$2 мультикаст $1%"),
    };

    // целая часть между запятыми — привычные обороты
    static readonly (Regex From, string To)[] Clauses =
    {
        (new(@"^(?:без cd|без перезарядки|cd:?\s*0(?:\s*с)?|0 cd|0)$", I), "нет cd"),
        (new(@"^(?:фиксированный\s+)?cd:?\s*(\d+(?:\.\d+)?)\s*с$", I), "cd $1 с"),
        (new(@"^только\s*(\d+(?:\.\d+)?)\s*с\s*cd$", I), "cd $1 с"),
        (new(@"^только\s*(\d+(?:\.\d+)?)\s*cd$", I), "cd $1"),
        (new(@"^(\d+(?:\.\d+)?)\s*с\s*cd$", I), "cd $1 с"),
        (new(@"^(?:применяется|выпускает|выполнить|выполнено|срабатывает)\s*(\d+)\s*раза?(?:\s*подряд)?$", I), "×$1 подряд"),
        (new(@"^(?:эффект удваива\w*|эффект удвоен|удваивает эффект|удвоенный эффект)$", I), "×2 эффект"),
        (new(@"^удвоен[аоы]?\s+(.+)$", I), "×2 $1"),
        (new(@"^удваивает длительност[иь]$", I), "×2 длительность"),
        (new(@"^удваивает\s+(.+)$", I), "×2 $1"),
        (new(@"^(.+?)\s+удвоен[аоы]?$", I), "×2 $1"),
        (new(@"^(.+?)\s+удваива(?:ется|ются)$", I), "×2 $1"),
        (new(@"^удвоение дополнительного здоровья$", I), "×2 доп. здоровье"),
        (new(@"^двойная денежная награда$", I), "×2 награда"),
        (new(@"^множитель урона:\s*×(\d+)$", I), "×$1 урон"),
        (new(@"^способность становится пассивной$", I), "пассивная"),
        (new(@"^(?:стать навыком конусной формы|становится способностью в форме конуса)$", I), "конус"),
        (new(@"^(?:не расходует ману|без затрат маны|0 затрат маны)$", I), "без маны"),
        (new(@"^без времени произнесения$", I), "без каста"),
        (new(@"^увеличение скорости передвижения на \+?(\d+%)$", I), "+$1 скорость передвижения"),
        (new(@"^бонус к дальности атаки:\s*\+?(\d+)$", I), "+$1 дальность атаки"),
        (new(@"^повторно срабатывает через (\d+(?:\.\d+)?)\s*с$", I), "повтор через $1 с"),
    };

    // в конце — длинные сочетания короче
    static readonly (Regex From, string To)[] Short =
    {
        (new("дальность применения", I), "дальность"),
        (new("радиус действия", I), "радиус"),
        (new("дистанция применения", I), "дальность"),
        (new("урона? в секунду", I), "урон/с"),
        (new(@"\bдополнительн(?:ый|ое|ая|ого|ой)\b", I), "доп."),
        (new(@"\bмаксимальн(?:ый|ое|ая|ого|ой)\b", I), "макс."),
        (new(@"\bперезарядк[аи]\b", I), "cd"),
        (new(@"\bдлительности\b", I), "длительность"),
    };

    public static string Simplify(string value)
    {
        string s = Regex.Replace(value, @"[㐀-鿿]+", " "); // «+150/200/250伤害 к урону» — иероглифы лишние
        s = s.Replace('，', ',').Replace('、', ',').Replace('；', ',');
        s = Regex.Replace(s, @"\+\s+(?=\d)", "+");
        s = Regex.Replace(s, @"(?<=\d)\s*/\s*(?=\d)", "/");
        s = Regex.Replace(s, @"(?<=\d)%/(?=\d)", "/");                 // «20%/30%/40%» → «20/30/40%»
        s = Regex.Replace(s, @"(?<![\w.])-(?=\d)", "−");
        s = Regex.Replace(s, @"\bсек\b\.?", "с");
        s = Regex.Replace(s, @"(?<=\d)с\b", " с");
        foreach (var (from, to) in Phrases) s = from.Replace(s, to);
        var parts = new List<string>();
        // части — через запятую, а бывает и через точку («+50 урона. Мгновенно создаёт…»)
        foreach (var raw in Regex.Split(s, @",(?!\d)|\.\s+(?=[А-ЯA-Z])"))
        {
            string c = raw.Trim().TrimEnd('.').Trim();
            if (c.Length == 0) continue;
            c = Dative(c);
            c = Genitive(c);
            foreach (var (from, to) in Clauses) c = from.Replace(c, to);
            foreach (var (from, to) in Short) c = from.Replace(c, to);
            c = Range(Regex.Replace(c, @"\s+", " ").Trim());
            c = Regex.Replace(c, @"^(×\d+ )?[А-ЯЁ](?=[а-яё])", m => m.Value.ToLowerInvariant()); // «Скорость прыжка» → «скорость…»
            if (c.Length > 0 && !parts.Contains(c)) parts.Add(c);
        }
        return string.Join(", ", parts);
    }

    // «+400 к радиусу действия» → «+400 радиус действия»: после «к» прилагательные и слово — в именительный
    static string Dative(string c)
    {
        var m = Regex.Match(c, $@"^(?<n>{Num}(?:\s*с)?)\s*(?:бонус\s+)?к\s+(?<rest>.+)$", I);
        return m.Success && Nominative(m.Groups["rest"].Value, dative: true) is { } rest ? $"{m.Groups["n"].Value} {rest}" : c;
    }

    // «+80 урона», «+50 дополнительного урона», «+120 с длительности», «+500 дальности атаки» — тоже в именительный
    static string Genitive(string c)
    {
        var m = Regex.Match(c, $@"^(?<n>{Num}(?:\s*с)?)\s+(?<rest>(?:\w+ого\s+)?(?:урона|длительности|дальности|скорости)\b.*)$", I);
        return m.Success && Nominative(m.Groups["rest"].Value, dative: false) is { } rest ? $"{m.Groups["n"].Value} {rest}" : c;
    }

    /// <summary>Первые прилагательные и главное слово — в именительный; дальше (род. падеж: «урон от атаки») — как есть.</summary>
    static string? Nominative(string phrase, bool dative)
    {
        var words = phrase.Split(' ');
        int i = 0;
        // прилагательные и сокращения («макс./мин.», «доп.») перед главным словом
        while (i < words.Length - 1 && Regex.IsMatch(words[i], dative ? @"(?:ому|ему|ой|ей|\.)$" : @"(?:ого|\.)$")) i++;
        var (noun, gender) = Noun(words[i], dative);
        if (noun == null) return null;
        for (int k = 0; k < i; k++) words[k] = Adjective(words[k], gender);
        words[i] = noun;
        return string.Join(' ', words);
    }

    // главное слово в именительном и его род: m, n, f; null — незнакомое (тогда фразу не трогаем)
    static (string? Word, char Gender) Noun(string w, bool dative)
    {
        if (!dative)
            return w.ToLowerInvariant() switch
            {
                "урона" => ("урон", 'm'),
                "длительности" or "дальности" or "скорости" => (w[..^1] + "ь", 'f'),
                _ => (null, 'm'),
            };
        if (w.EndsWith("ству")) return (w[..^1] + "о", 'n');
        if (w.EndsWith("ию")) return (w[..^1] + "е", 'n');
        if (w.EndsWith("ости")) return (w[..^1] + "ь", 'f');
        if (w is "броне" or "брони") return ("броня", 'f');
        if (w == "времени") return ("время", 'n');
        if (w.EndsWith("е") && !w.EndsWith("ие")) return (w[..^1] + "а", 'f'); // силе, атаке, длине
        if (w.EndsWith("ю")) return (w[..^1] + "ь", 'm');
        if (w.EndsWith("у")) return (w[..^1], 'm');
        return (null, 'm');
    }

    static string Adjective(string w, char gender) => gender switch
    {
        'f' => Regex.Replace(Regex.Replace(w, "(?:ой|ого)$", "ая"), "ей$", "яя"),
        'n' => Regex.Replace(Regex.Replace(w, "(?:ому|ого)$", "ое"), "ему$", "ее"),
        _ => Regex.Replace(Regex.Replace(w, "(?:ому|ого)$", "ый"), "ему$", "ий"),
    };

    // уровни диапазоном: «+50/100/150/200» → «+50–200»; две цифры («50/100») — как есть
    static string Range(string c) =>
        Regex.Replace(c, @"\d+(?:\.\d+)?(?:/\d+(?:\.\d+)?){2,}", m =>
        {
            var v = m.Value.Split('/');
            return $"{v[0]}–{v[^1]}";
        });
}

/// <summary>
/// Чтение одного файла из .vpk Valve (версии 1 и 2): дерево «расширение → папка → имя», данные — в самом файле
/// (архив 0x7FFF, у кастомок из мастерской так) или в preload-байтах записи. Внешние архивы _NNN.vpk не читаем.
/// </summary>
static class Vpk
{
    const uint Signature = 0x55AA1234;

    public static byte[]? Read(string vpkPath, string innerPath)
    {
        innerPath = innerPath.Replace('\\', '/').ToLowerInvariant();
        using var fs = File.OpenRead(vpkPath);
        using var r = new BinaryReader(fs);
        if (r.ReadUInt32() != Signature) throw new InvalidDataException("не VPK");
        uint version = r.ReadUInt32();
        uint treeSize = r.ReadUInt32();
        int headerSize = 12;
        if (version == 2)
        {
            r.ReadBytes(16); // размеры разделов данных и подписей
            headerSize = 28;
        }
        else if (version != 1) throw new InvalidDataException("VPK версии " + version);
        long dataStart = headerSize + treeSize;
        while (true)
        {
            string ext = ReadString(r);
            if (ext.Length == 0) return null;
            while (true)
            {
                string dir = ReadString(r);
                if (dir.Length == 0) break;
                while (true)
                {
                    string name = ReadString(r);
                    if (name.Length == 0) break;
                    r.ReadUInt32(); // CRC
                    ushort preload = r.ReadUInt16();
                    ushort archive = r.ReadUInt16();
                    uint offset = r.ReadUInt32();
                    uint length = r.ReadUInt32();
                    r.ReadUInt16(); // 0xFFFF
                    var preloadBytes = r.ReadBytes(preload);
                    string path = (dir == " " ? "" : dir + "/") + name + (ext == " " ? "" : "." + ext);
                    if (!string.Equals(path, innerPath, StringComparison.OrdinalIgnoreCase)) continue;
                    if (length == 0) return preloadBytes;
                    if (archive != 0x7FFF) return null; // данные во внешнем архиве
                    if (dataStart + offset + length > fs.Length) throw new InvalidDataException("запись за концом файла");
                    long back = fs.Position;
                    fs.Position = dataStart + offset;
                    var data = r.ReadBytes((int)length);
                    fs.Position = back;
                    return preloadBytes.Concat(data).ToArray();
                }
            }
        }
    }

    static string ReadString(BinaryReader r)
    {
        var bytes = new List<byte>();
        byte b;
        while ((b = r.ReadByte()) != 0) bytes.Add(b);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
