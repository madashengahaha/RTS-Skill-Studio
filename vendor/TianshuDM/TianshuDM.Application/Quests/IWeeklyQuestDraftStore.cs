using TianshuDM.Domain.Quests;

namespace TianshuDM.Application.Quests;

public interface IWeeklyQuestDraftStore
{
    bool TrySaveWeeklyQuestDraft(WeeklyQuestDefinition quest);
}
