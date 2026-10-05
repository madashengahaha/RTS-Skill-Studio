namespace TianshuDM.Domain.Quests;

public static class DailyQuestTypeCatalog
{
    private static readonly Dictionary<string, int> LegacyValues =
        new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["None"] = 0,
            ["KillEnemy"] = 1,
            ["PlayBattle"] = 2,
            ["WinBattle"] = 3,
            ["PVPBattle"] = 4,
            ["PVEBattle"] = 5,
            ["TransmigratorLevel"] = 6,
        };

    public static bool TryGetLegacyValue(string code, out int legacyValue)
    {
        return LegacyValues.TryGetValue(code, out legacyValue);
    }
}
