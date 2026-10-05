using TianshuDM.Domain.Quests;

namespace TianshuDM.Application.Quests;

public interface IWeeklyQuestWorkbookReader
{
    IReadOnlyList<WeeklyQuestDefinition> Read(string workbookPath);
}
