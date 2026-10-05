using TianshuDM.Domain.Workspaces;

namespace TianshuDM.Application.Quests;

public interface IRecurringQuestReleaseStore
{
    void ReplaceRecurringQuestRelease(WorkspaceImportResult import);
}
