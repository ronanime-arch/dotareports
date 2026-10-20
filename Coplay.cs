using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DotaReports;

/// <summary>
/// РљС‚Рѕ РІ РјР°С‚С‡Рµ Valve вЂ” РёР· С„Р°Р№Р»РѕРІ Steam, Р±РµР· -condebug Рё Steam API. РџРѕРґРєР»СЋС‡Р°СЏСЃСЊ Рє РјР°С‚С‡Сѓ, Dota СЃРѕРѕР±С‰Р°РµС‚ Steam РІСЃРµС… РёРіСЂРѕРєРѕРІ
/// (В«РќРµРґР°РІРЅРёРµ РёРіСЂРѕРєРёВ»), Рё РїСЂРёРјРµСЂРЅРѕ С‡РµСЂРµР· РјРёРЅСѓС‚Сѓ Steam РїРёС€РµС‚ РёС… РІ config\coplay_&lt;РІР°С€ SteamID64&gt;.vdf: SteamID Рё РІСЂРµРјСЏ
/// (20.10: РїРѕРґРєР»СЋС‡РµРЅРёРµ 13:24:51, РґРµРІСЏС‚СЊ Р·Р°РїРёСЃРµР№ 13:24:51вЂ“13:25:03, С„Р°Р№Р» вЂ” 13:25:51, РµС‰С‘ РЅР° РїРёРєРµ). Р’ РєРѕРЅС†Рµ РјР°С‚С‡Р° РІСЂРµРјСЏ
/// РѕР±РЅРѕРІР»СЏРµС‚СЃСЏ РµС‰С‘ СЂР°Р·. РљРѕРіРґР° Р±С‹Р»Рѕ РїРѕРґРєР»СЋС‡РµРЅРёРµ, Dota РїРёС€РµС‚ РІ userdata\&lt;Р°РєРєР°СѓРЅС‚&gt;\570\remote\scripts\dota_local_disk_lobby_data.txt
/// (В«timestampВ» Рё В«connectВ» вЂ” Steam ID СЃРµСЂРІРµСЂР°). РљРѕРјР°РЅРґ Рё СЃР»РѕС‚РѕРІ РІ СЌС‚РёС… С„Р°Р№Р»Р°С… РЅРµС‚.
/// РџСЂРѕС€Р»С‹Р№ РёСЃС‚РѕС‡РЅРёРє вЂ” Steam GetRealtimeStats РїРѕ СЃРµСЂРІРµСЂСѓ вЂ” СЃРѕСЃС‚Р°РІ РѕР±С‹С‡РЅС‹С… РјР°С‚С‡РµР№ РЅРµ РѕС‚РґР°С‘С‚: РґР»СЏ РўСѓСЂР±Рѕ 20.10 РЅР° РїРёРєРµ
/// РїСѓСЃС‚РѕР№ РєР°РґСЂ, РІ РёРіСЂРµ В«{}В»; РІ СЃРїРёСЃРєР°С… Р¶РёРІС‹С… РёРіСЂ Steam Рё OpenDota РўСѓСЂР±Рѕ РЅРµС‚ РІРѕРІСЃРµ.
/// </summary>
static class Coplay
{
    const ulong SteamBase = 76561197960265728;
    const ulong DotaApp = 570;

    static string? SteamDir =>
        (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string)?.Replace('/', '\\');

    static uint? Me() =>
        File.Exists(Paths.Account) && uint.TryParse(File.ReadAllText(Paths.Account).Trim(), out var me) ? me : null;

    /// <summary>РџРѕРґРєР»СЋС‡РµРЅРёРµ Рє РїРѕСЃР»РµРґРЅРµРјСѓ РјР°С‚С‡Сѓ: РєРѕРіРґР° Рё Steam ID СЃРµСЂРІРµСЂР°; null вЂ” С„Р°Р№Р»Р° РЅРµС‚.</summary>
    public static (DateTime At, ulong Server)? LastConnect()
    {
        if (SteamDir is not { } steam || Me() is not { } me) return null;
        var path = Path.Combine(steam, "userdata", me.ToString(), "570", "remote", "scripts", "dota_local_disk_lobby_data.txt");
        if (!File.Exists(path)) return null;
        var text = File.ReadAllText(path);
        var stamp = Regex.Match(text, "\"timestamp\"\\s+\"(\\d+)\"");
        var connect = Regex.Match(text, @"=\[A:1:(\d+):(\d+)\]");
        if (!stamp.Success || !connect.Success) return null;
        var at = DateTimeOffset.FromUnixTimeSeconds(long.Parse(stamp.Groups[1].Value)).LocalDateTime;
        ulong account = ulong.Parse(connect.Groups[1].Value), instance = ulong.Parse(connect.Groups[2].Value);
        // SteamID64 Р°РЅРѕРЅРёРјРЅРѕРіРѕ РёРіСЂРѕРІРѕРіРѕ СЃРµСЂРІРµСЂР°: РІСЃРµР»РµРЅРЅР°СЏ 1, С‚РёРї 4 (A), СЌРєР·РµРјРїР»СЏСЂ, РЅРѕРјРµСЂ
        return (at, (1UL << 56) | (4UL << 52) | (instance << 32) | account);
    }

    /// <summary>РђРєРєР°СѓРЅС‚С‹, СЃ РєРµРј Steam Р·Р°РїРёСЃР°Р» РёРіСЂСѓ Dota РЅРµ СЂР°РЅСЊС€Рµ since (Р±РµР· РІР°СЃ); РїСѓСЃС‚Рѕ вЂ” С„Р°Р№Р»Р° РµС‰С‘ РЅРµС‚.</summary>
    public static List<uint> PlayersSince(DateTime since)
    {
        if (SteamDir is not { } steam || Me() is not { } me) return new();
        var path = Path.Combine(steam, "config", $"coplay_{SteamBase + me}.vdf");
        if (!File.Exists(path)) return new();
        byte[] data;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            data = new byte[fs.Length];
            fs.ReadExactly(data);
        }
        long from = new DateTimeOffset(since).ToUnixTimeSeconds();
        return Entries(data)
            .Where(e => e.Game == DotaApp && e.Playtime >= from && e.SteamId > SteamBase && e.SteamId - SteamBase != me)
            .OrderBy(e => e.Playtime)
            .Select(e => (uint)(e.SteamId - SteamBase))
            .Distinct()
            .ToList();
    }

    /// <summary>Р—Р°РїРёСЃРё coplay: РґРІРѕРёС‡РЅС‹Р№ KeyValues В«coplayВ» { "0" { SteamID uint64, gameid uint64, Playtime int32 } вЂ¦ }.</summary>
    static List<(ulong SteamId, ulong Game, long Playtime)> Entries(byte[] data)
    {
        var result = new List<(ulong, ulong, long)>();
        int pos = 0;
        try
        {
            Read(data, ref pos, 0, null, result);
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
        {
            // С„Р°Р№Р» РѕР±РѕСЂРІР°РЅ (Steam РєР°Рє СЂР°Р· РїРёС€РµС‚) вЂ” С‡С‚Рѕ СѓСЃРїРµР»Рё РїСЂРѕС‡РёС‚Р°С‚СЊ, С‚Рѕ Рё РµСЃС‚СЊ; СЃР»РµРґСѓСЋС‰РёР№ С‚Р°РєС‚ РїСЂРѕС‡РёС‚Р°РµС‚ С†РµР»РёРєРѕРј
        }
        return result;
    }

    // РўРёРї СѓР·Р»Р°: 0 вЂ” РІР»РѕР¶РµРЅРЅС‹Р№ Р±Р»РѕРє, 1 вЂ” СЃС‚СЂРѕРєР°, 2 вЂ” int32, 3 вЂ” float, 4 вЂ” СѓРєР°Р·Р°С‚РµР»СЊ, 6 вЂ” С†РІРµС‚, 7 вЂ” uint64, 10 вЂ” int64, 8 вЂ” РєРѕРЅРµС† Р±Р»РѕРєР°.
    static void Read(byte[] d, ref int pos, int depth, Dictionary<string, long>? entry, List<(ulong, ulong, long)> result)
    {
        while (pos < d.Length)
        {
            byte type = d[pos++];
            if (type == 8) return;
            string name = CString(d, ref pos);
            switch (type)
            {
                case 0:
                    // СѓСЂРѕРІРµРЅСЊ 1 вЂ” В«coplayВ», СѓСЂРѕРІРµРЅСЊ 2 вЂ” РѕРґРЅР° Р·Р°РїРёСЃСЊ
                    var child = depth == 1 ? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) : null;
                    Read(d, ref pos, depth + 1, child, result);
                    if (child != null && child.TryGetValue("SteamID", out var sid) && child.TryGetValue("Playtime", out var t))
                        result.Add(((ulong)sid, child.TryGetValue("gameid", out var g) ? (ulong)g : 0, t));
                    break;
                case 1:
                    CString(d, ref pos);
                    break;
                case 2 or 3 or 4 or 6:
                    if (entry != null) entry[name] = type == 2 ? BitConverter.ToInt32(d, pos) : 0;
                    pos += 4;
                    break;
                case 7 or 10:
                    if (entry != null) entry[name] = (long)BitConverter.ToUInt64(d, pos);
                    pos += 8;
                    break;
                default:
                    throw new ArgumentException($"coplay: С‚РёРї {type}");
            }
        }
    }

    static string CString(byte[] d, ref int pos)
    {
        int end = Array.IndexOf(d, (byte)0, pos);
        if (end < 0) throw new ArgumentException("coplay: СЃС‚СЂРѕРєР° Р±РµР· РєРѕРЅС†Р°");
        var s = Encoding.UTF8.GetString(d, pos, end - pos);
        pos = end + 1;
        return s;
    }
}
