using TianshuDM.Domain.Quests;
using TianshuDM.Domain.Workspaces;

namespace TianshuDM.Application.Quests;

public interface IStoryQuestDraftStore
{
    StoryQuestDataset? ReadStoryQuestDataset();

    void ReplaceStoryQuestDataset(StoryQuestDataset dataset);

    void ReplaceStoryQuestImport(StoryQuestImportData import);
}
