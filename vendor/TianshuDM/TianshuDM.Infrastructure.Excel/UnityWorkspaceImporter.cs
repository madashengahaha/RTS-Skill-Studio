using System.Security.Cryptography;
using TianshuDM.Application.GameData;
using TianshuDM.Application.Quests;
using TianshuDM.Application.Workspaces;
using TianshuDM.Domain.Workspaces;

namespace TianshuDM.Infrastructure.Excel;

public sealed class UnityWorkspaceImporter(
    IDailyQuestWorkbookReader dailyQuestReader,
    IWeeklyQuestWorkbookReader weeklyQuestReader,
    IQuestChestRewardWorkbookReader questChestRewardReader,
    IStoryQuestWorkbookReader storyQuestReader,
    IGameDataCatalogReader gameDataCatalogReader)
    : IWorkspaceImporter
{
    public WorkspaceImportResult Import(string unityProjectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unityProjectRoot);

        string projectRoot = Path.GetFullPath(unityProjectRoot);
        RequireDirectory(Path.Combine(projectRoot, "Unity", "Assets"), "Unity/Assets");
        RequireFile(
            Path.Combine(projectRoot, "Unity", "ProjectSettings", "ProjectVersion.txt"),
            "Unity/ProjectSettings/ProjectVersion.txt");

        string excelRoot = Path.Combine(projectRoot, "Unity", "Assets", "Config", "Excel");
        RequireFile(Path.Combine(excelRoot, "__luban__.conf"), "Excel/__luban__.conf");
        RequireFile(
            Path.Combine(excelRoot, "Datas", "__tables__.xlsx"),
            "Excel/Datas/__tables__.xlsx");

        string lubanDllPath = Path.Combine(
            projectRoot,
            "Tools",
            "Luban",
            "LubanRelease",
            "Luban.dll");
        RequireFile(lubanDllPath, "Tools/Luban/LubanRelease/Luban.dll");

        string dailyQuestWorkbookPath = Path.Combine(
            excelRoot,
            "Datas",
            "Quest",
            "DailyQuest.xlsx");
        RequireFile(dailyQuestWorkbookPath, "Excel/Datas/Quest/DailyQuest.xlsx");

        string weeklyQuestWorkbookPath = Path.Combine(
            excelRoot,
            "Datas",
            "Quest",
            "WeeklyQuest.xlsx");
        RequireFile(weeklyQuestWorkbookPath, "Excel/Datas/Quest/WeeklyQuest.xlsx");

        string questChestRewardWorkbookPath = Path.Combine(
            excelRoot,
            "Datas",
            "Quest",
            "ChestReward.xlsx");
        RequireFile(questChestRewardWorkbookPath, "Excel/Datas/Quest/ChestReward.xlsx");

        string storyQuestWorkbookPath = Path.Combine(
            excelRoot,
            "Datas",
            "Quest",
            "StoryQuest.xlsx");
        string storyStepWorkbookPath = Path.Combine(
            excelRoot,
            "Datas",
            "Quest",
            "StoryQuestStep.xlsx");
        string storyTurnWorkbookPath = Path.Combine(
            excelRoot,
            "Datas",
            "Quest",
            "StoryConversationTurn.xlsx");
        RequireFile(storyQuestWorkbookPath, "Excel/Datas/Quest/StoryQuest.xlsx");
        RequireFile(storyStepWorkbookPath, "Excel/Datas/Quest/StoryQuestStep.xlsx");
        RequireFile(
            storyTurnWorkbookPath,
            "Excel/Datas/Quest/StoryConversationTurn.xlsx");

        string sourceHash = HashFile(dailyQuestWorkbookPath);
        var storyQuest = new StoryQuestImportData(
            storyQuestWorkbookPath,
            storyStepWorkbookPath,
            storyTurnWorkbookPath,
            HashFile(storyQuestWorkbookPath),
            HashFile(storyStepWorkbookPath),
            HashFile(storyTurnWorkbookPath),
            storyQuestReader.Read(
                storyQuestWorkbookPath,
                storyStepWorkbookPath,
                storyTurnWorkbookPath));

        return new WorkspaceImportResult(
            projectRoot,
            dailyQuestWorkbookPath,
            sourceHash,
            dailyQuestReader.Read(dailyQuestWorkbookPath))
        {
            WeeklyQuestWorkbookPath = weeklyQuestWorkbookPath,
            WeeklyQuestSourceHash = HashFile(weeklyQuestWorkbookPath),
            WeeklyQuests = weeklyQuestReader.Read(weeklyQuestWorkbookPath),
            QuestChestRewardWorkbookPath = questChestRewardWorkbookPath,
            QuestChestRewardSourceHash = HashFile(questChestRewardWorkbookPath),
            QuestChestRewards = questChestRewardReader.Read(questChestRewardWorkbookPath),
            StoryQuest = storyQuest,
            GameData = gameDataCatalogReader.Read(Path.Combine(excelRoot, "Datas")),
        };
    }

    private static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static void RequireDirectory(string path, string logicalName)
    {
        if (!Directory.Exists(path))
        {
            throw new DirectoryNotFoundException(
                $"The selected project is missing {logicalName}: {path}");
        }
    }

    private static void RequireFile(string path, string logicalName)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"The selected project is missing {logicalName}.",
                path);
        }
    }
}
