using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DotaReports;

/// <summary>
/// Поиск героя по тому, как его пишут: «медуза», «антимаг», «па», «войд», «Faceless Void».
/// Кириллица транслитерируется и сравнивается с английским именем; сленг — через словарь.
/// </summary>
static class HeroSearch
{
    static readonly Dictionary<string, string> Aliases = new()
    {
        ["ам"] = "Anti-Mage", ["антимаг"] = "Anti-Mage", ["па"] = "Phantom Assassin", ["фантомка"] = "Phantom Assassin",
        ["мортред"] = "Phantom Assassin", ["пл"] = "Phantom Lancer", ["войд"] = "Faceless Void", ["фв"] = "Faceless Void",
        ["сф"] = "Shadow Fiend", ["невермор"] = "Shadow Fiend", ["вр"] = "Windranger", ["винда"] = "Windranger",
        ["дк"] = "Dragon Knight", ["цм"] = "Crystal Maiden", ["кристалка"] = "Crystal Maiden", ["акс"] = "Axe",
        ["джагер"] = "Juggernaut", ["джаггер"] = "Juggernaut", ["тб"] = "Terrorblade", ["бх"] = "Bounty Hunter",
        ["бб"] = "Bristleback", ["лс"] = "Lifestealer", ["гуль"] = "Lifestealer", ["дровка"] = "Drow Ranger",
        ["дроу"] = "Drow Ranger", ["шторм"] = "Storm Spirit", ["эмбер"] = "Ember Spirit", ["земля"] = "Earth Spirit",
        ["шейкер"] = "Earthshaker", ["эш"] = "Earthshaker", ["ск"] = "Sand King", ["вк"] = "Wraith King",
        ["скелет"] = "Wraith King", ["огр"] = "Ogre Magi", ["огрмаг"] = "Ogre Magi", ["веник"] = "Venomancer",
        ["веном"] = "Venomancer", ["нага"] = "Naga Siren", ["фура"] = "Nature's Prophet", ["профет"] = "Nature's Prophet",
        ["лесник"] = "Nature's Prophet", ["вд"] = "Witch Doctor", ["раста"] = "Shadow Shaman", ["шаман"] = "Shadow Shaman",
        ["тайд"] = "Tidehunter", ["гиро"] = "Gyrocopter", ["трент"] = "Treant Protector", ["висп"] = "Io",
        ["бм"] = "Beastmaster", ["бист"] = "Beastmaster", ["бара"] = "Spirit Breaker", ["сб"] = "Spirit Breaker",
        ["бэт"] = "Batrider", ["бейн"] = "Bane", ["дс"] = "Dark Seer", ["дп"] = "Death Prophet",
        ["кент"] = "Centaur Warrunner", ["кентавр"] = "Centaur Warrunner", ["мк"] = "Monkey King", ["обезьяна"] = "Monkey King",
        ["алх"] = "Alchemist", ["алхимик"] = "Alchemist", ["трол"] = "Troll Warlord", ["тролль"] = "Troll Warlord",
        ["скай"] = "Skywrath Mage", ["снеп"] = "Snapfire", ["бабка"] = "Snapfire", ["аа"] = "Ancient Apparition",
        ["апп"] = "Ancient Apparition", ["снайп"] = "Sniper", ["зевс"] = "Zeus", ["тини"] = "Tiny", ["маг"] = "Ogre Magi",
        ["ван"] = "Dark Willow", ["пангольер"] = "Pangolier", ["марси"] = "Marci", ["мирана"] = "Mirana", ["пота"] = "Mirana",
        ["квопа"] = "Queen of Pain", ["куопа"] = "Queen of Pain", ["qop"] = "Queen of Pain", ["вивер"] = "Weaver",
        ["клинкз"] = "Clinkz", ["клинкс"] = "Clinkz", ["тинкер"] = "Tinker", ["лешрак"] = "Leshrac", ["леш"] = "Leshrac",
        ["хуск"] = "Huskar", ["хускар"] = "Huskar", ["рики"] = "Riki", ["слардар"] = "Slardar", ["спектра"] = "Spectre",
        ["морф"] = "Morphling", ["медуза"] = "Medusa", ["дуза"] = "Medusa", ["пудж"] = "Pudge", ["инвок"] = "Invoker",
    };

    static readonly Dictionary<char, string> Translit = new()
    {
        ['а'] = "a", ['б'] = "b", ['в'] = "v", ['г'] = "g", ['д'] = "d", ['е'] = "e", ['ё'] = "e", ['ж'] = "zh",
        ['з'] = "z", ['и'] = "i", ['й'] = "i", ['к'] = "k", ['л'] = "l", ['м'] = "m", ['н'] = "n", ['о'] = "o",
        ['п'] = "p", ['р'] = "r", ['с'] = "s", ['т'] = "t", ['у'] = "u", ['ф'] = "f", ['х'] = "h", ['ц'] = "ts",
        ['ч'] = "ch", ['ш'] = "sh", ['щ'] = "sch", ['ъ'] = "", ['ы'] = "y", ['ь'] = "", ['э'] = "e", ['ю'] = "yu", ['я'] = "ya",
    };

    /// <summary>До max героев, лучшие совпадения первыми.</summary>
    public static List<int> Find(string input, IReadOnlyDictionary<int, string> heroes, int max = 6)
    {
        var raw = Letters(input.ToLowerInvariant());
        if (raw.Length == 0) return new();
        var q = ToLatin(raw);
        var byName = heroes.ToDictionary(h => h.Value, h => h.Key, StringComparer.OrdinalIgnoreCase);

        var scored = new Dictionary<int, double>();
        void Offer(int id, double score)
        {
            if (!scored.TryGetValue(id, out var s) || score < s) scored[id] = score;
        }

        foreach (var (alias, name) in Aliases)
        {
            if (!byName.TryGetValue(name, out var id)) continue;
            if (alias == raw) Offer(id, 0);
            else if (raw.Length >= 2 && alias.StartsWith(raw)) Offer(id, 1.5);
        }

        foreach (var (id, name) in heroes)
        {
            var lower = name.ToLowerInvariant();
            var n = Letters(lower);
            var words = lower.Split(new[] { ' ', '-', '\'' }, StringSplitOptions.RemoveEmptyEntries);
            var initials = string.Concat(words.Select(w => w[0]));

            if (n == q) Offer(id, 0);
            else if (n.StartsWith(q)) Offer(id, 1);
            else if (words.Any(w => w.StartsWith(q)) || (q.Length >= 2 && initials == q)) Offer(id, 2);
            else if (q.Length >= 3 && n.Contains(q)) Offer(id, 3);
            else if (q.Length >= 4)
            {
                int allowed = q.Length >= 7 ? 2 : 1;
                int best = int.MaxValue;
                for (int len = Math.Max(1, q.Length - 1); len <= Math.Min(n.Length, q.Length + 1); len++)
                    best = Math.Min(best, Levenshtein(q, n[..len]));
                if (best <= allowed) Offer(id, 4 + best);
            }
        }

        return scored.OrderBy(kv => kv.Value).ThenBy(kv => heroes[kv.Key]).Take(max).Select(kv => kv.Key).ToList();
    }

    static string Letters(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());

    static string ToLatin(string s)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            // «дж»: в начале слова — j (джаггер → jagger), иначе dg (пудж → pudg)
            if (s[i] == 'д' && i + 1 < s.Length && s[i + 1] == 'ж')
            {
                sb.Append(i == 0 ? "j" : "dg");
                i++;
            }
            else sb.Append(Translit.TryGetValue(s[i], out var t) ? t : s[i].ToString());
        }
        return sb.ToString();
    }

    static int Levenshtein(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return d[a.Length, b.Length];
    }
}
