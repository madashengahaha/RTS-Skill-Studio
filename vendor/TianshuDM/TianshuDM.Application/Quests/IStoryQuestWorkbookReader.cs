using TianshuDM.Domain.Quests;

namespace TianshuDM.Application.Quests;

public interface IStoryQuestWorkbookReader
{
    StoryQuestDataset Read(
        string questWorkbookPath,
        string stepWorkbookPath,
        string turnWorkbookPath);
}
