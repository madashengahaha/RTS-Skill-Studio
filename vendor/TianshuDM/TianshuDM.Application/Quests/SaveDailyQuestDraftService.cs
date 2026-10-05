using TianshuDM.Domain.Quests;

namespace TianshuDM.Application.Quests;

public sealed class SaveDailyQuestDraftService(IDailyQuestDraftStore store)
{
    public DailyQuestDefinition Save(DailyQuestDefinition quest)
    {
        ArgumentNullException.ThrowIfNull(quest);

        if (!DailyQuestTypeCatalog.TryGetLegacyValue(
                quest.Type.Code,
                out int legacyValue)
            || legacyValue != quest.Type.LegacyValue)
        {
            throw new ArgumentException(
                $"Quest type '{quest.Type.Code}' does not match legacy value "
                + $"{quest.Type.LegacyValue}.",
                nameof(quest));
        }

        if (!store.TrySaveDailyQuestDraft(quest))
        {
            throw new KeyNotFoundException(
                $"DailyQuest {quest.Id} does not exist in the working copy.");
        }

        return quest;
    }
}
