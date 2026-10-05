using TianshuDM.Application.Quests;
using TianshuDM.Domain.Quests;

namespace TianshuDM.Infrastructure.Excel;

public sealed class QuestChestRewardWorkbookReader : IQuestChestRewardWorkbookReader
{
    private static readonly string[] RequiredColumns =
        ["Id", "RequiredCount", "Reward", "ChestLevel"];

    public IReadOnlyList<QuestChestRewardDefinition> Read(string workbookPath)
    {
        return LubanWorkbookTable.Read(workbookPath, "ChestReward", RequiredColumns)
            .Select(
                row => new QuestChestRewardDefinition(
                    LubanValueParser.ParseInt(row, "Id", "ChestReward"),
                    LubanValueParser.ParseInt(row, "RequiredCount", "ChestReward"),
                    LubanValueParser.ParseRewards(row, "Reward", "ChestReward"),
                    LubanValueParser.ParseInt(row, "ChestLevel", "ChestReward")))
            .ToArray();
    }
}
