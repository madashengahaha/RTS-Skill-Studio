using TianshuDM.Domain.Workspaces;

namespace TianshuDM.Application.Quests;

public interface IRecurringQuestWorkbookWriter
{
    void Write(
        string dailyQuestWorkbookPath,
        string weeklyQuestWorkbookPath,
        string questChestRewardWorkbookPath,
        WorkspaceImportResult workspace);
}

public interface IRecurringQuestReleasePipeline
{
    RecurringQuestReleaseResult Execute(
        WorkspaceImportResult workspace,
        bool publish,
        bool confirmLubanFailure = false);
}

public sealed record RecurringQuestReleaseResult(
    string Status,
    string BackupPath,
    string JournalPath,
    string ValidationOutput)
{
    public int KnownValidationErrorCount { get; init; }

    public bool ValidationOverridden { get; init; }

    public string ValidationStatus { get; init; } = "passed";
}
