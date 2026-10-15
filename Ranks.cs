namespace DotaReports;

/// <summary>Названия медалей Dota.</summary>
static class Ranks
{
    static readonly string[] Medals = { "", "Рекрут", "Страж", "Рыцарь", "Герой", "Легенда", "Властелин", "Божество", "Титан" };
    static readonly string[] Stars = { "", "I", "II", "III", "IV", "V" };

    /// <summary>Звание по rank_tier: 54 — «Легенда IV»; null — звания нет.</summary>
    public static string? Name(int rankTier)
    {
        int medal = rankTier / 10, star = rankTier % 10;
        if (medal is < 1 or > 8) return null;
        return Medals[medal] + (star is >= 1 and <= 5 ? " " + Stars[star] : "");
    }
}
