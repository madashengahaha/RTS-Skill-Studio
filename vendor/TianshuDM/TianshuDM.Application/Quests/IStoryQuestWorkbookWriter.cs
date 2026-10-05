using TianshuDM.Domain.Quests;

namespace TianshuDM.Application.Quests;

public interface IStoryQuestWorkbookWriter
{
    void Write(
        string questWorkbookPath,
        string stepWorkbookPath,
        string turnWorkbookPath,
        StoryQuestDataset dataset);
}
