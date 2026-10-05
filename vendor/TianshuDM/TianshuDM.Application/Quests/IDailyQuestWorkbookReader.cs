using TianshuDM.Domain.Quests;

namespace TianshuDM.Application.Quests;

public interface IDailyQuestWorkbookReader
{
    IReadOnlyList<DailyQuestDefinition> Read(string workbookPath);
}
