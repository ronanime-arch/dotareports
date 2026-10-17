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
            var bytes = Vpk.Read(vpkPath, "resource/addon_russian.txt") ?? Vpk.Read(vpkPath, "resource/addon_english.txt");
            if (bytes != null) info = Parse(Decode(bytes), abilityNames);
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

    // Файлы локализации Valve бывают и в UTF-16 (с BOM FF FE), и в UTF-8
    static string Decode(byte[] bytes) =>
        bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE ? Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2)
        : bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3)
        : Encoding.UTF8.GetString(bytes);

    static AwakeningInfo Parse(string text, IReadOnlyDictionary<string, string> abilityNames)
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
            if (value.Length == 0) continue;
            if (value.Length > MaxText) value = value[..(MaxText - 1)].TrimEnd() + "…";
            // своё «способность» у кастомки (pudge_awaken, antimage_base) в списке Valve не найдётся — тогда без названия
            string entry = abilityNames.TryGetValue(key.Groups[3].Value, out var name) ? $"{name}: {value}" : value;
            if (!info._heroes.TryGetValue(hero, out var stages))
                info._heroes[hero] = stages = new[] { new List<string>(), new List<string>(), new List<string>() };
            stages[stage].Add(entry);
        }
        return info;
    }

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
