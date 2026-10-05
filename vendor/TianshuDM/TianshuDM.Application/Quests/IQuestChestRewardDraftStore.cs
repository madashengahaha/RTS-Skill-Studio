using TianshuDM.Domain.Quests;

namespace TianshuDM.Application.Quests;

public interface IQuestChestRewardDraftStore
{
    bool TrySaveQuestChestRewardDraft(QuestChestRewardDefinition chest);
}
