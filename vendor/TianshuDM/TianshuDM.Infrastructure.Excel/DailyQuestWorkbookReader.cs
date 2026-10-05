using TianshuDM.Application.Quests;
using TianshuDM.Domain.Quests;

namespace TianshuDM.Infrastructure.Excel;

public sealed class DailyQuestWorkbookReader : IDailyQuestWorkbookReader
{
    public IReadOnlyList<DailyQuestDefinition> Read(string workbookPath)
    {
        return RecurringQuestWorkbookParser.Read(
            workbookPath,
            "DailyQuest",
            (id, type, targetValue, rewards, sortOrder, description) =>
                new DailyQuestDefinition(
                    id,
                    type,
                    targetValue,
                    rewards,
                    sortOrder,
                    description));
    }
}
