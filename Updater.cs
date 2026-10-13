using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace DotaReports;

/// <summary>
/// Обновление из трея: номер версии сборки (CI ставит 1.0.N, как и тег релиза) сверяется с последним релизом GitHub;
/// по кнопке скачивается DotaReports.zip, программа закрывается, скрипт копирует новые файлы поверх и запускает её снова.
/// Данные программы (вход в Steam, история, настройки) лежат в %LOCALAPPDATA% и не трогаются.
/// </summary>
static class Updater
{
    const string LatestApi = "https://api.github.com/repos/ronanime-arch/dotareports/releases/latest";
    const string ZipUrl = "https://github.com/ronanime-arch/dotareports/releases/latest/download/DotaReports.zip";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(3) };

    /// <summary>Версия этой сборки: 1.0.N из CI; собранная вручную — 1.0.0.</summary>
    public static Version Current
    {
        get
        {
            var v = typeof(Updater).Assembly.GetName().Version ?? new Version(1, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    /// <summary>Номер последнего релиза (тег v1.0.N); null — GitHub не ответил.</summary>
    public static async Task<Version?> LatestAsync()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, LatestApi);
        req.Headers.UserAgent.ParseAdd("DotaReports");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        return Version.TryParse(tag?.TrimStart('v', 'V'), out var v) ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : null;
    }

    /// <summary>
    /// Скачать и распаковать последний релиз, запустить скрипт замены файлов. После этого программу нужно закрыть:
    /// скрипт ждёт её выхода, копирует файлы в папку программы и запускает её снова.
    /// </summary>
    public static async Task PrepareAsync()
    {
        var work = Path.Combine(Path.GetTempPath(), "DotaReports-update");
        if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
        Directory.CreateDirectory(work);
        var zip = Path.Combine(work, "DotaReports.zip");
        await File.WriteAllBytesAsync(zip, await Http.GetByteArrayAsync(ZipUrl));
        var unpacked = Path.Combine(work, "new");
        ZipFile.ExtractToDirectory(zip, unpacked);
        // в архиве — папка DotaReports с программой
        var source = File.Exists(Path.Combine(unpacked, "DotaReports", "DotaReports.exe")) ? Path.Combine(unpacked, "DotaReports") : unpacked;
        if (!File.Exists(Path.Combine(source, "DotaReports.exe")))
            throw new InvalidDataException("в архиве нет DotaReports.exe");

        var target = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var script = Path.Combine(work, "update.ps1");
        File.WriteAllText(script, Script);
        var psi = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
                     "-ProcessId", Environment.ProcessId.ToString(), "-Source", source, "-Target", target, "-Log", Paths.Log })
            psi.ArgumentList.Add(arg);
        Process.Start(psi);
    }

    // ASCII: Windows PowerShell 5.1 читает файл без BOM в кодировке ANSI
    const string Script = """
        param([int]$ProcessId, [string]$Source, [string]$Target, [string]$Log)
        Wait-Process -Id $ProcessId -Timeout 90 -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 700
        try {
            Copy-Item -Path (Join-Path $Source '*') -Destination $Target -Recurse -Force -ErrorAction Stop
            Add-Content -Path $Log -Value ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + ' update: files copied to ' + $Target)
        } catch {
            Add-Content -Path $Log -Value ((Get-Date -Format 'yyyy-MM-dd HH:mm:ss') + ' update failed: ' + $_)
        }
        Start-Process -FilePath (Join-Path $Target 'DotaReports.exe')
        """;
}
