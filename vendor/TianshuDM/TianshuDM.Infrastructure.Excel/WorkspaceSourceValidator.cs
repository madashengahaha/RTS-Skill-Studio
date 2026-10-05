using TianshuDM.Application.Workspaces;
using TianshuDM.Domain.Workspaces;

namespace TianshuDM.Infrastructure.Excel;

public sealed class WorkspaceSourceValidator : IWorkspaceSourceValidator
{
    public void Validate(WorkspaceImportResult workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        RequireDirectory(workspace.UnityProjectRoot);
        RequireFile(workspace.DailyQuestWorkbookPath);
        RequireOptionalFile(workspace.WeeklyQuestWorkbookPath);
        RequireOptionalFile(workspace.QuestChestRewardWorkbookPath);
        if (workspace.StoryQuest is not null)
        {
            RequireFile(workspace.StoryQuest.QuestWorkbookPath);
            RequireFile(workspace.StoryQuest.StepWorkbookPath);
            RequireFile(workspace.StoryQuest.TurnWorkbookPath);
        }

        foreach (var table in workspace.GameData.Tables)
        {
            RequireFile(table.WorkbookPath);
        }
    }

    private static void RequireDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException(
                $"The selected project no longer exists: {path}");
        }
    }

    private static void RequireFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "A source workbook in the selected project no longer exists.",
                path);
        }
    }

    private static void RequireOptionalFile(string path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            RequireFile(path);
        }
    }
}
