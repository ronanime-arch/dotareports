using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace DotaReports;

/// <summary>
/// Значки рангов Dota — те же медали, что в клиенте (rank_icon_1…8 — Рекрут…Титан), без звёзд.
/// Из клиента их не достать без разбора текстур Valve (vtex_c в VPK), поэтому берём PNG у OpenDota и держим
/// в папке данных (ranks\); не скачалось — <see cref="RankIcon"/> рисует свой кружок.
/// </summary>
static class RankArt
{
    const string Source = "https://www.opendota.com/assets/images/dota2/rank_icons/";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    static readonly Dictionary<string, BitmapImage?> Cache = new();

    static string Dir => Path.Combine(Paths.Root, "ranks");

    static IEnumerable<string> Files()
    {
        for (int i = 1; i <= 8; i++) yield return $"rank_icon_{i}.png";
    }

    /// <summary>Скачать недостающие значки (раз, при запуске); ошибки — в журнал, программа работает и без них.</summary>
    public static async Task EnsureAsync()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            foreach (var name in Files())
            {
                var path = Path.Combine(Dir, name);
                if (File.Exists(path) && new FileInfo(path).Length > 0) continue;
                var bytes = await Http.GetByteArrayAsync(Source + name);
                // PNG начинается с 89 50 4E 47 — иначе (страница ошибки) не сохраняем
                if (bytes.Length < 8 || bytes[0] != 0x89 || bytes[1] != 0x50) throw new InvalidDataException($"{name}: не PNG");
                await File.WriteAllBytesAsync(path, bytes);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Log.Write("Значки рангов не скачаны: " + ex.Message);
        }
    }

    /// <summary>Медаль 1…8 — картинка из ranks\; нет файла — null.</summary>
    public static BitmapImage? Medal(int medal) => Load($"rank_icon_{medal}.png");

    static BitmapImage? Load(string name)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var cached) && cached != null) return cached;
            var path = Path.Combine(Dir, name);
            if (!File.Exists(path)) return null; // ещё не скачан — спросим снова в следующий раз
            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad; // файл не держим открытым
                image.UriSource = new Uri(path);
                image.EndInit();
                image.Freeze(); // значки строятся и в фоновом потоке
                return Cache[name] = image;
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
            {
                Log.Write($"Значок ранга {name} не прочитан: {ex.Message}");
                return Cache[name] = null;
            }
        }
    }
}
