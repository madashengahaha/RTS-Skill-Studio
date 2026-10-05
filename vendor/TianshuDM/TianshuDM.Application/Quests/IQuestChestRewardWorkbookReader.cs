using TianshuDM.Domain.Quests;

namespace TianshuDM.Application.Quests;

public interface IQuestChestRewardWorkbookReader
{
    IReadOnlyList<QuestChestRewardDefinition> Read(string workbookPath);
}
