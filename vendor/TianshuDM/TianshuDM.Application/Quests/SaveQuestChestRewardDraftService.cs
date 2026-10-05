using TianshuDM.Domain.Quests;

namespace TianshuDM.Application.Quests;

public sealed class SaveQuestChestRewardDraftService(IQuestChestRewardDraftStore store)
{
    public QuestChestRewardDefinition Save(QuestChestRewardDefinition chest)
    {
        ArgumentNullException.ThrowIfNull(chest);

        if (chest.RequiredCount < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(chest),
                chest.RequiredCount,
                "Required count cannot be negative.");
        }

        if (chest.ChestLevel <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(chest),
                chest.ChestLevel,
                "Chest level must be greater than zero.");
        }

        if (!store.TrySaveQuestChestRewardDraft(chest))
        {
            throw new KeyNotFoundException(
                $"Quest chest reward {chest.Id} does not exist in the working copy.");
        }

        return chest;
    }
}
