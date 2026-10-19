using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace DotaReports;

/// <summary>
/// Кастомка IMBA AWAKEN (воркшоп 3320645051) по сигналу сервера GameEndDisconnect сама выполняет disconnect
/// (scripts/vscripts/addon_game_mode_client.lua) и выкидывает в главное меню, не дав посмотреть свои итоги.
/// Правка меняет в VPK "disconnect" на безвредную "echo stay." той же длины — смещения не сдвигаются —
/// и пересчитывает CRC файла, MD5 каталога и MD5 всего VPK. Дата файла остаётся прежней: NEC кэширует карту по размеру и дате.
/// </summary>
static class ImbaStay
{
    public enum State { NoMap, Off, On, Unknown }

    const string WorkshopId = "3320645051";
    const string Entry = "scripts/vscripts/addon_game_mode_client.lua";
    const string Prefix = "GameEvent(\"awaken_game_end_disconnect\",function()SendToConsole(\"";
    static readonly byte[] Original = Encoding.ASCII.GetBytes(Prefix + "disconnect\")end");
    static readonly byte[] Patched = Encoding.ASCII.GetBytes(Prefix + "echo stay.\")end");

    /// <summary>Галочка в трее: отметка в папке данных.</summary>
    public static bool Wanted
    {
        get => File.Exists(Paths.ImbaStay);
        set
        {
            Directory.CreateDirectory(Paths.Root);
            if (value) File.WriteAllText(Paths.ImbaStay, "on");
            else File.Delete(Paths.ImbaStay);
        }
    }

    // Воркшоп лежит в той же библиотеке Steam, что и Dota: steamapps\workshop\content\570\<id>\<id>.vpk.
    public static string? FindVpk()
    {
        if (Setup.FindDota() is not { } dota || Path.GetDirectoryName(Path.GetDirectoryName(dota)) is not { } steamapps) return null;
        var vpk = Path.Combine(steamapps, "workshop", "content", "570", WorkshopId, WorkshopId + ".vpk");
        return File.Exists(vpk) ? vpk : null;
    }

    /// <summary>Привести карту к нужному состоянию. Только при закрытой Dota; при неизвестной версии карты ничего не пишет.</summary>
    public static State Sync(string vpk, bool on)
    {
        var target = on ? State.On : State.Off;
        // поминутная проверка только читает: запись и замок на файл — лишь когда состояние надо менять
        if (Read(vpk) is var now && (now == State.Unknown || now == target)) return now;
        var mtime = File.GetLastWriteTimeUtc(vpk);
        using (var fs = new FileStream(vpk, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var v = Inspect(fs);
            if (v.State == State.Unknown || v.State == target) return v.State;
            if (!SumsOk(fs, v)) throw new InvalidDataException("контрольные суммы карты не сходятся ещё до правки");
            Write(fs, v, on ? Patched : Original);
            var after = Inspect(fs);
            if (after.State != target || !SumsOk(fs, after))
                throw new InvalidDataException("после записи проверка не прошла — переподпишитесь на карту в мастерской Steam");
        }
        File.SetLastWriteTimeUtc(vpk, mtime);
        Log.Write(on ? "Итоги IMBA: правка карты включена" : "Итоги IMBA: карта возвращена к оригиналу");
        return target;
    }

    public static State Read(string vpk)
    {
        using var fs = new FileStream(vpk, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Inspect(fs).State;
    }

    sealed record Layout(State State, byte[] Tree, int EntryPos, long ContentPos, byte[] Content, int At, long Md5Pos, byte[] Md5Block);

    static readonly Layout Unknown = new(State.Unknown, Array.Empty<byte>(), 0, 0, Array.Empty<byte>(), -1, 0, Array.Empty<byte>());

    // VPK v2: заголовок 28 байт, каталог, данные, таблица MD5 кусков,
    // блок из трёх MD5 (каталог, таблица кусков, весь файл до этого места), подпись.
    // MD5 кусков не сходятся уже в оригинальной карте — Dota их не проверяет, их не трогаем.
    static Layout Inspect(FileStream fs)
    {
        var head = ReadAt(fs, 0, 28);
        if (BitConverter.ToUInt32(head, 0) != 0x55AA1234 || BitConverter.ToUInt32(head, 4) != 2 || BitConverter.ToUInt32(head, 20) != 48)
            return Unknown;
        int treeSize = (int)BitConverter.ToUInt32(head, 8);
        long dataSize = BitConverter.ToUInt32(head, 12), chunkMd5Size = BitConverter.ToUInt32(head, 16);
        var tree = ReadAt(fs, 28, treeSize);
        int p = 0, entry = -1;
        string Next()
        {
            int end = Array.IndexOf(tree, (byte)0, p);
            if (end < 0) throw new InvalidDataException("битый каталог VPK");
            var s = Encoding.Latin1.GetString(tree, p, end - p);
            p = end + 1;
            return s;
        }
        for (string ext; (ext = Next()) != "";)
            for (string dir; (dir = Next()) != "";)
                for (string name; (name = Next()) != "";)
                {
                    if ($"{dir}/{name}.{ext}" == Entry) entry = p;
                    p += 18 + BitConverter.ToUInt16(tree, p + 4); // CRC, preload, архив, смещение, длина, 0xFFFF, preload-данные
                }
        // скрипт должен лежать целиком внутри этого VPK (архив 0x7FFF), без preload
        if (entry < 0 || BitConverter.ToUInt16(tree, entry + 4) != 0 || BitConverter.ToUInt16(tree, entry + 6) != 0x7FFF) return Unknown;
        long dataStart = 28 + treeSize, md5Pos = dataStart + dataSize + chunkMd5Size;
        long contentPos = dataStart + BitConverter.ToUInt32(tree, entry + 8);
        var content = ReadAt(fs, contentPos, (int)BitConverter.ToUInt32(tree, entry + 12));
        int orig = FindOnce(content, Original), patched = FindOnce(content, Patched);
        var state = orig >= 0 && patched == -1 ? State.Off : patched >= 0 && orig == -1 ? State.On : State.Unknown;
        if (state == State.Unknown) return Unknown;
        return new(state, tree, entry, contentPos, content, state == State.Off ? orig : patched, md5Pos, ReadAt(fs, md5Pos, 48));
    }

    static void Write(FileStream fs, Layout v, byte[] replacement)
    {
        WriteAt(fs, v.ContentPos + v.At, replacement);
        replacement.CopyTo(v.Content, v.At);
        var crc = BitConverter.GetBytes(Crc32(v.Content));
        WriteAt(fs, 28 + v.EntryPos, crc);
        crc.CopyTo(v.Tree, v.EntryPos);
        WriteAt(fs, v.Md5Pos, MD5.HashData(v.Tree));
        WriteAt(fs, v.Md5Pos + 32, Md5Prefix(fs, v.Md5Pos + 32));
        fs.Flush(true);
    }

    static bool SumsOk(FileStream fs, Layout v) =>
        Crc32(v.Content) == BitConverter.ToUInt32(v.Tree, v.EntryPos)
        && MD5.HashData(v.Tree).AsSpan().SequenceEqual(v.Md5Block.AsSpan(0, 16))
        && Md5Prefix(fs, v.Md5Pos + 32).AsSpan().SequenceEqual(v.Md5Block.AsSpan(32, 16));

    // -1 — нет, -2 — больше одного раза (незнакомая версия карты).
    static int FindOnce(byte[] data, byte[] needle)
    {
        int i = data.AsSpan().IndexOf(needle);
        if (i < 0) return -1;
        return data.AsSpan(i + 1).IndexOf(needle) < 0 ? i : -2;
    }

    static byte[] Md5Prefix(FileStream fs, long length)
    {
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        var buf = new byte[1 << 20];
        fs.Position = 0;
        for (long left = length; left > 0;)
        {
            int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, left));
            if (n <= 0) throw new EndOfStreamException("файл карты обрезан");
            md5.AppendData(buf, 0, n);
            left -= n;
        }
        return md5.GetHashAndReset();
    }

    static byte[] ReadAt(FileStream fs, long pos, int length)
    {
        var buf = new byte[length];
        fs.Position = pos;
        fs.ReadExactly(buf);
        return buf;
    }

    static void WriteAt(FileStream fs, long pos, byte[] data)
    {
        fs.Position = pos;
        fs.Write(data);
    }

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    static uint Crc32(byte[] data)
    {
        uint c = 0xFFFFFFFF;
        foreach (var b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
        return ~c;
    }
}
