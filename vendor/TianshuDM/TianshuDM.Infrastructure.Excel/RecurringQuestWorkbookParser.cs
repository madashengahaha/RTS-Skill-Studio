using TianshuDM.Domain.Quests;

namespace TianshuDM.Infrastructure.Excel;

internal static class RecurringQuestWorkbookParser
{
    private static readonly string[] RequiredColumns =
        ["Id", "Type", "TargetValue", "Reward", "SortOrder", "Desc"];

    public static IReadOnlyList<TQuest> Read<TQuest>(
        string workbookPath,
        string logicalName,
        Func<int, QuestTypeValue, int, IReadOnlyList<QuestReward>, int, string, TQuest> factory)
    {
        return LubanWorkbookTable.Read(workbookPath, logicalName, RequiredColumns)
            .Select(
                row =>
                {
                    string typeCode = row.Values["Type"];
                    if (!DailyQuestTypeCatalog.TryGetLegacyValue(typeCode, out int legacyValue))
                    {
                        throw new InvalidDataException(
                            $"{logicalName} row {row.RowIndex} has unknown QuestType '{typeCode}'.");
                    }

                    return factory(
                        LubanValueParser.ParseInt(row, "Id", logicalName),
                        new QuestTypeValue(typeCode, legacyValue),
                        LubanValueParser.ParseInt(row, "TargetValue", logicalName),
                        LubanValueParser.ParseRewards(row, "Reward", logicalName),
                        LubanValueParser.ParseInt(row, "SortOrder", logicalName),
                        row.Values["Desc"]);
                })
            .ToArray();
    }
}
