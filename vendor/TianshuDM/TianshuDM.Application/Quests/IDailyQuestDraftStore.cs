using TianshuDM.Domain.Quests;

namespace TianshuDM.Application.Quests;

public interface IDailyQuestDraftStore
{
    bool TrySaveDailyQuestDraft(DailyQuestDefinition quest);
}
