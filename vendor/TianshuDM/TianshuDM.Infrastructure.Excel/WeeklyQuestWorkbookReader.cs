using TianshuDM.Application.Quests;
using TianshuDM.Domain.Quests;

namespace TianshuDM.Infrastructure.Excel;

public sealed class WeeklyQuestWorkbookReader : IWeeklyQuestWorkbookReader
{
    public IReadOnlyList<WeeklyQuestDefinition> Read(string workbookPath)
    {
        return RecurringQuestWorkbookParser.Read(
            workbookPath,
            "WeeklyQuest",
            (id, type, targetValue, rewards, sortOrder, description) =>
                new WeeklyQuestDefinition(
                    id,
                    type,
                    targetValue,
                    rewards,
                    sortOrder,
                    description));
    }
}
