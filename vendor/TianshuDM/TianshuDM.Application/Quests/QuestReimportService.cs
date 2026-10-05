using TianshuDM.Application.Workspaces;
using TianshuDM.Domain.Workspaces;

namespace TianshuDM.Application.Quests;

public enum QuestReimportScope
{
    Story,
    Daily,
    Weekly,
    Chest,
}

public sealed record QuestReimportResult(string Status, string Scope);

public sealed class QuestReimportService(
    IWorkspaceStore workspaceStore,
    IStoryQuestDraftStore storyQuestStore,
    IRecurringQuestImportStore recurringQuestStore,
    IWorkspaceImporter importer)
{
    public QuestReimportResult Execute(QuestReimportScope scope)
    {
        WorkspaceImportResult workspace = workspaceStore.ReadWorkspace()
            ?? throw new InvalidOperationException("当前没有已打开的游戏工程。");
        WorkspaceImportResult imported = importer.Import(workspace.UnityProjectRoot);

        switch (scope)
        {
            case QuestReimportScope.Story:
                storyQuestStore.ReplaceStoryQuestImport(
                    imported.StoryQuest
                    ?? throw new InvalidDataException("任务表格中没有剧情任务数据。"));
                break;
            case QuestReimportScope.Daily:
                recurringQuestStore.ReplaceDailyQuestImport(imported);
                break;
            case QuestReimportScope.Weekly:
                recurringQuestStore.ReplaceWeeklyQuestImport(imported);
                break;
            case QuestReimportScope.Chest:
                recurringQuestStore.ReplaceQuestChestRewardImport(imported);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(scope), scope, null);
        }

        string scopeName = scope switch
        {
            QuestReimportScope.Story => "story",
            QuestReimportScope.Daily => "daily",
            QuestReimportScope.Weekly => "weekly",
            QuestReimportScope.Chest => "chest",
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
        };
        return new QuestReimportResult("completed", scopeName);
    }
}
