using TianshuDM.Domain.Quests;

namespace TianshuDM.Application.Quests;

public interface IRecurringQuestMutationStore
{
    bool TryCreateDailyQuestDraft(DailyQuestDefinition quest);

    bool TryDeleteDailyQuestDraft(int questId);

    bool TryCreateWeeklyQuestDraft(WeeklyQuestDefinition quest);

    bool TryDeleteWeeklyQuestDraft(int questId);

    bool TryCreateQuestChestRewardDraft(QuestChestRewardDefinition chest);

    bool TryDeleteQuestChestRewardDraft(int chestId);
}
