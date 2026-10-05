using TianshuDM.Application.Workspaces;
using TianshuDM.Domain.Quests;
using TianshuDM.Domain.Workspaces;

namespace TianshuDM.Application.Quests;

public sealed class RecurringQuestDraftService(
    IWorkspaceStore workspaceStore,
    IRecurringQuestMutationStore mutationStore)
{
    public DailyQuestDefinition CreateDailyQuest()
    {
        WorkspaceImportResult workspace = RequireWorkspace();
        var quest = new DailyQuestDefinition(
            NextId(workspace.DailyQuests.Select(item => item.Id)),
            new QuestTypeValue("None", 0),
            1,
            [],
            NextId(workspace.DailyQuests.Select(item => item.SortOrder)),
            "请填写任务描述");
        if (!mutationStore.TryCreateDailyQuestDraft(quest))
        {
            throw new InvalidOperationException($"日常任务编号 {quest.Id} 已存在，请重新加载后重试。");
        }

        return quest;
    }

    public void DeleteDailyQuest(int questId)
    {
        RequireWorkspace();
        if (!mutationStore.TryDeleteDailyQuestDraft(questId))
        {
            throw new KeyNotFoundException($"日常任务 {questId} 不存在。");
        }
    }

    public WeeklyQuestDefinition CreateWeeklyQuest()
    {
        WorkspaceImportResult workspace = RequireWorkspace();
        var quest = new WeeklyQuestDefinition(
            NextId(workspace.WeeklyQuests.Select(item => item.Id)),
            new QuestTypeValue("None", 0),
            1,
            [],
            NextId(workspace.WeeklyQuests.Select(item => item.SortOrder)),
            "请填写任务描述");
        if (!mutationStore.TryCreateWeeklyQuestDraft(quest))
        {
            throw new InvalidOperationException($"周常任务编号 {quest.Id} 已存在，请重新加载后重试。");
        }

        return quest;
    }

    public void DeleteWeeklyQuest(int questId)
    {
        RequireWorkspace();
        if (!mutationStore.TryDeleteWeeklyQuestDraft(questId))
        {
            throw new KeyNotFoundException($"周常任务 {questId} 不存在。");
        }
    }

    public QuestChestRewardDefinition CreateQuestChestReward()
    {
        WorkspaceImportResult workspace = RequireWorkspace();
        var chest = new QuestChestRewardDefinition(
            NextId(workspace.QuestChestRewards.Select(item => item.Id)),
            0,
            [],
            NextId(workspace.QuestChestRewards.Select(item => item.ChestLevel)));
        if (!mutationStore.TryCreateQuestChestRewardDraft(chest))
        {
            throw new InvalidOperationException($"任务宝箱编号 {chest.Id} 已存在，请重新加载后重试。");
        }

        return chest;
    }

    public void DeleteQuestChestReward(int chestId)
    {
        RequireWorkspace();
        if (!mutationStore.TryDeleteQuestChestRewardDraft(chestId))
        {
            throw new KeyNotFoundException($"任务宝箱 {chestId} 不存在。");
        }
    }

    private WorkspaceImportResult RequireWorkspace()
    {
        return workspaceStore.ReadWorkspace()
            ?? throw new InvalidOperationException("尚未打开项目工作区。");
    }

    private static int NextId(IEnumerable<int> values)
    {
        return values.DefaultIfEmpty(0).Max() + 1;
    }
}
