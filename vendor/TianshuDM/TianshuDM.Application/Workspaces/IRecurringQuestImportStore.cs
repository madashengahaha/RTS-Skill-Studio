using TianshuDM.Domain.Workspaces;

namespace TianshuDM.Application.Workspaces;

public interface IRecurringQuestImportStore
{
    void ReplaceRecurringQuestImport(WorkspaceImportResult import);

    void ReplaceDailyQuestImport(WorkspaceImportResult import);

    void ReplaceWeeklyQuestImport(WorkspaceImportResult import);

    void ReplaceQuestChestRewardImport(WorkspaceImportResult import);
}
