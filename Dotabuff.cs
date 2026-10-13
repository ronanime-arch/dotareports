using System.Linq;
using System.Text.RegularExpressions;

namespace DotaReports;

/// <summary>
/// Ссылки на Dotabuff. API у него нет, а разбирать страницы программой запрещают его правила, — страницы открываются
/// во встроенном окне «Аналитика» как в обычном браузере, программа их не читает.
/// </summary>
static class Dotabuff
{
    const string Site = "https://www.dotabuff.com";

    /// <param name="tab">"" — обзор; "heroes", "matches", "records", "activity".</param>
    public static string Player(string account, string tab = "") => $"{Site}/players/{account}" + (tab.Length > 0 ? "/" + tab : "");

    public static string Match(ulong matchId) => $"{Site}/matches/{matchId}";

    public static string HeroCounters(string heroName) => $"{Site}/heroes/{Slug(heroName)}/counters";

    public static string Meta => $"{Site}/heroes/meta";

    public static string Heroes => $"{Site}/heroes";

    public static string News => $"{Site}/blog";

    /// <summary>Адрес героя: «Nature's Prophet» → natures-prophet, «Anti-Mage» → anti-mage, «Queen of Pain» → queen-of-pain.</summary>
    public static string Slug(string heroName)
    {
        var s = new string(heroName.ToLowerInvariant().Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-').ToArray());
        return Regex.Replace(s.Trim(), "[ -]+", "-");
    }
}
