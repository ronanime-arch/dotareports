using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace DotaReports;

/// <summary>Кто вы в Steam — чтобы не выдавать вас самого за жалующегося.</summary>
sealed record Me(string? SteamId, string? AccountId, string? Profile);

sealed record GcpdResult(bool NeedLogin, JsonElement Table, Me Me)
{
    public static readonly GcpdResult Login = new(true, default, new Me(null, null, null));
}

/// <summary>Открывает steamcommunity.com/my/gcpd/570 и достаёт таблицу «Incoming Match Player Report».</summary>
sealed class GcpdReader
{
    public const string StartUrl = "https://steamcommunity.com/my/gcpd/570/?category=Account";
    const ulong SteamId64Base = 76561197960265728;
    static readonly TimeSpan NavTimeout = TimeSpan.FromSeconds(30);
    static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(20);

    readonly SteamBrowser _b;

    public GcpdReader(SteamBrowser browser) => _b = browser;

    public async Task<GcpdResult> ReadIncomingAsync(bool interactive)
    {
        await _b.NavigateAsync(StartUrl, NavTimeout);
        var state = await WaitStateAsync();
        if (state == "login")
        {
            if (!interactive || !await LoginAsync()) return GcpdResult.Login;
            await _b.NavigateAsync(StartUrl, NavTimeout);
            state = await WaitStateAsync();
        }
        if (state != "gcpd")
            throw new InvalidOperationException($"Страница GCPD не открылась (открыто: {_b.Source}).");

        var page = await _b.EvalAsync(PageInfoJs);
        if (!TryFindIncoming(page, out int selectIndex, out string value, out bool selected))
        {
            Dump(page, null);
            throw new InvalidOperationException(
                "На странице нет пункта «Incoming Match Player Report». Список пунктов записан в " + Paths.Dump);
        }

        if (!selected)
        {
            // Как сделал бы человек: выбрать пункт в списке. Если страница не перешла — открыть по ссылке.
            bool navigated = await _b.RunAndWaitNavigationAsync(
                () => _b.EvalAsync($"((i, v) => {{ const s = document.querySelectorAll('select')[i]; s.value = v; s.dispatchEvent(new Event('change', {{ bubbles: true }})); }})({selectIndex}, {Js(value)})"),
                TimeSpan.FromSeconds(15));
            if (!navigated || !await IsSelectedAsync(selectIndex, value))
                await _b.NavigateAsync(TabUrl(_b.Source, value), NavTimeout);
            await WaitStateAsync();
            if (!await IsSelectedAsync(selectIndex, value))
            {
                Dump(page, null);
                throw new InvalidOperationException("Не удалось переключиться на «Incoming Match Player Report». Подробности в " + Paths.Dump);
            }
            page = await _b.EvalAsync(PageInfoJs);
        }

        var table = await _b.EvalAsync(TableJs);
        Dump(page, table);
        return new GcpdResult(false, table, ReadMe(page));
    }

    /// <summary>Таблицы нескольких вкладок GCPD (раздел, вкладка, сколько раз нажать «Загрузить ещё»). null — нужен вход в Steam.</summary>
    public async Task<Dictionary<string, JsonElement>?> ReadTablesAsync(IEnumerable<(string Category, string Tab, int ExtraPages)> tabs)
    {
        var result = new Dictionary<string, JsonElement>();
        foreach (var (category, tab, pages) in tabs)
        {
            await _b.NavigateAsync($"https://steamcommunity.com/my/gcpd/570/?category={category}&tab={tab}", NavTimeout);
            var state = await WaitStateAsync();
            if (state == "login") return null;
            if (state != "gcpd") throw new InvalidOperationException($"Вкладка {category}/{tab} не открылась (открыто: {_b.Source}).");
            for (int i = 0; i < pages; i++)
            {
                var clicked = await _b.EvalAsync("(() => { const b = document.getElementById('load_more_button'); if (!b || b.offsetParent === null) return false; b.click(); return true; })()");
                if (clicked.ValueKind != JsonValueKind.True) break;
                await Task.Delay(2500);
            }
            result[$"{category}:{tab}"] = await _b.EvalAsync(TableJs);
        }
        return result;
    }

    /// <summary>Отладка: выгружает таблицы указанных вкладок (с «Загрузить ещё») в один файл.</summary>
    public async Task DumpTabsAsync(IEnumerable<string> tabs, string outPath, int extraPages)
    {
        var result = new Dictionary<string, JsonElement>();
        foreach (var tab in tabs)
        {
            // «Раздел:Вкладка» — другой раздел GCPD; «Раздел:» — только список его вкладок
            var parts = tab.Split(':');
            var url = parts.Length == 2
                ? $"https://steamcommunity.com/my/gcpd/570/?category={Uri.EscapeDataString(parts[0])}" + (parts[1].Length > 0 ? "&tab=" + Uri.EscapeDataString(parts[1]) : "")
                : StartUrl + "&tab=" + Uri.EscapeDataString(tab);
            await _b.NavigateAsync(url, NavTimeout);
            if (await WaitStateAsync() != "gcpd")
                throw new InvalidOperationException("Страница не открылась или нет входа: " + _b.Source);
            if (parts.Length == 2 && parts[1].Length == 0)
            {
                result[tab] = await _b.EvalAsync(PageInfoJs);
                continue;
            }
            for (int i = 0; i < extraPages; i++)
            {
                var clicked = await _b.EvalAsync("(() => { const b = document.getElementById('load_more_button'); if (!b || b.offsetParent === null) return false; b.click(); return true; })()");
                if (clicked.ValueKind != JsonValueKind.True) break;
                await Task.Delay(2500);
            }
            result[tab] = await _b.EvalAsync(TableJs);
        }
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(outPath, JsonSerializer.Serialize(result, options));
    }

    async Task<bool> LoginAsync()
    {
        _b.ShowWindow();
        try
        {
            while (!_b.UserClosed)
            {
                await Task.Delay(1000);
                var s = await StateAsync();
                if (s is "gcpd" or "loggedin") return true;
            }
            return false;
        }
        finally
        {
            _b.HideWindow();
        }
    }

    async Task<string> WaitStateAsync()
    {
        var until = DateTime.UtcNow + StateTimeout;
        string s;
        do
        {
            s = await StateAsync();
            if (s is "login" or "gcpd") return s;
            await Task.Delay(500);
        } while (DateTime.UtcNow < until);
        return s;
    }

    async Task<string> StateAsync()
    {
        try
        {
            var r = await _b.EvalAsync(StateJs);
            return r.ValueKind == JsonValueKind.String ? r.GetString()! : "wait";
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or System.Runtime.InteropServices.COMException)
        {
            return "wait"; // страница в процессе перехода
        }
    }

    async Task<bool> IsSelectedAsync(int index, string value)
    {
        var r = await _b.EvalAsync($"((i, v) => {{ const s = document.querySelectorAll('select')[i]; return !!s && s.value === v; }})({index}, {Js(value)})");
        return r.ValueKind == JsonValueKind.True;
    }

    static bool TryFindIncoming(JsonElement page, out int selectIndex, out string value, out bool selected)
    {
        foreach (var sel in page.GetProperty("selects").EnumerateArray())
        {
            foreach (var opt in sel.GetProperty("options").EnumerateArray())
            {
                var text = opt.GetProperty("text").GetString() ?? "";
                if (!Regex.IsMatch(text, "incoming", RegexOptions.IgnoreCase) || !Regex.IsMatch(text, "report", RegexOptions.IgnoreCase))
                    continue;
                selectIndex = sel.GetProperty("index").GetInt32();
                value = opt.GetProperty("value").GetString() ?? "";
                selected = sel.GetProperty("value").GetString() == value;
                return true;
            }
        }
        selectIndex = -1;
        value = "";
        selected = false;
        return false;
    }

    static string TabUrl(string current, string value)
    {
        if (value.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return value;
        if (value.StartsWith('/')) return "https://steamcommunity.com" + value;
        var uri = new Uri(current);
        var path = uri.GetLeftPart(UriPartial.Path);
        if (value.StartsWith('?')) return path + value;
        var query = HttpUtility.ParseQueryString(uri.Query);
        query["tab"] = value;
        return path + "?" + query;
    }

    static Me ReadMe(JsonElement page)
    {
        string? steamId = page.GetProperty("steamId").ValueKind == JsonValueKind.String ? page.GetProperty("steamId").GetString() : null;
        string? accountId = ulong.TryParse(steamId, out var id64) && id64 > SteamId64Base ? (id64 - SteamId64Base).ToString() : null;
        var m = Regex.Match(page.GetProperty("href").GetString() ?? "", @"^https://steamcommunity\.com/(?:id|profiles)/[^/?#]+", RegexOptions.IgnoreCase);
        return new Me(steamId, accountId, m.Success ? m.Value : null);
    }

    static void Dump(JsonElement page, JsonElement? table)
    {
        var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        File.WriteAllText(Paths.Dump, JsonSerializer.Serialize(new { fetchedAt = DateTime.Now.ToString("s"), page, table }, options));
    }

    static string Js(string s) => JsonSerializer.Serialize(s);

    const string StateJs = """
        (() => {
          if (document.readyState !== 'complete') return 'wait';
          if (/^login\./.test(location.host) || /\/login(\/|$)/.test(location.pathname)) return 'login';
          if (/\/gcpd\/570/.test(location.pathname) && document.querySelector('select')) return 'gcpd';
          if (typeof g_steamID !== 'undefined' && g_steamID) return 'loggedin';
          return 'other';
        })()
        """;

    const string PageInfoJs = """
        (() => ({
          href: location.href,
          steamId: (typeof g_steamID !== 'undefined' && g_steamID) ? String(g_steamID) : null,
          selects: [...document.querySelectorAll('select')].map((s, i) => ({
            index: i,
            name: s.name || s.id || '',
            onchange: s.getAttribute('onchange') || '',
            value: s.value,
            options: [...s.options].map(o => ({ value: o.value, text: (o.text || '').trim() }))
          }))
        }))()
        """;

    const string TableJs = """
        (() => {
          const clean = s => (s || '').replace(/\s+/g, ' ').trim();
          const tables = [...document.querySelectorAll('table')];
          const score = t => (/match/i.test(clean(t.rows[0] && t.rows[0].innerText)) ? 100000 : 0) + t.rows.length;
          const t = tables.sort((a, b) => score(b) - score(a))[0];
          const out = { href: location.href, tableCount: tables.length, headers: [], rows: [], loadMore: null, pageText: null };
          if (t) {
            const rows = [...t.rows];
            const head = rows.find(r => r.querySelector('th'));
            if (head) out.headers = [...head.cells].map(c => clean(c.innerText));
            for (const r of rows) {
              if (r === head || !r.cells.length) continue;
              out.rows.push([...r.cells].map(c => ({
                text: clean(c.innerText),
                links: [...c.querySelectorAll('a[href]')].map(a => a.href),
                mini: [...c.querySelectorAll('[data-miniprofile]')].map(e => e.getAttribute('data-miniprofile')),
                imgs: [...c.querySelectorAll('img[src]')].map(i => i.src),
                title: c.getAttribute('title') || '',
                html: c.innerHTML.slice(0, 400)
              })));
            }
          }
          const more = [...document.querySelectorAll('a, button, input[type=button], div, span')]
            .find(e => e.children.length === 0 && /load more|загрузить ещ|показать ещ/i.test(e.textContent || e.value || ''));
          if (more) out.loadMore = more.outerHTML.slice(0, 600);
          out.pageText = clean(document.body.innerText).slice(0, 2000);
          return out;
        })()
        """;
}
