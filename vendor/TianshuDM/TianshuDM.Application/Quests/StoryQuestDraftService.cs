using TianshuDM.Domain.Quests;

namespace TianshuDM.Application.Quests;

public sealed class StoryQuestDraftService(IStoryQuestDraftStore store)
{
    public StoryQuestDataset GetDataset()
    {
        return store.ReadStoryQuestDataset()
               ?? throw new InvalidOperationException("No StoryQuest workspace is open.");
    }

    public StoryQuestCreationResult CreateQuest()
    {
        StoryQuestDataset dataset = GetDataset();
        IEnumerable<int> reservedQuestIds = dataset.Quests.Select(quest => quest.Id)
            .Concat(dataset.Steps.Select(step => step.QuestId));
        int id = reservedQuestIds.Any() ? checked(reservedQuestIds.Max() + 1) : 1;
        StoryQuestPrerequisite[] prerequisites = dataset.Quests.Count == 0
            ? []
            :
            [
                new StoryQuestPrerequisite(
                    StoryQuestPrerequisiteType.CompleteQuest,
                    dataset.Quests.Max(quest => quest.Id)),
            ];
        var quest = new StoryQuestDefinition(
            id,
            "新任务",
            "请填写任务描述",
            false,
            false,
            prerequisites);
        StoryQuestStepCreationResult creation = CreateStepDefinition(dataset, id, 0);
        Save(
            dataset.Quests.Append(quest),
            dataset.Steps.Append(creation.Step),
            dataset.Turns.Concat(creation.Turns));
        return new StoryQuestCreationResult(quest, creation.Step, creation.Turns);
    }

    public StoryQuestDefinition UpdateQuest(StoryQuestDefinition quest)
    {
        ArgumentNullException.ThrowIfNull(quest);
        StoryQuestDataset dataset = GetDataset();
        var quests = dataset.Quests.ToList();
        int index = quests.FindIndex(candidate => candidate.Id == quest.Id);
        if (index < 0)
        {
            throw Missing("StoryQuest", quest.Id);
        }

        ValidateQuestPrerequisites(quest, quests);
        quests[index] = quest;
        Save(quests, dataset.Steps, dataset.Turns);
        return quest;
    }

    public void DeleteQuest(int questId)
    {
        StoryQuestDataset dataset = GetDataset();
        if (!dataset.Quests.Any(quest => quest.Id == questId))
        {
            throw Missing("StoryQuest", questId);
        }

        StoryQuestStepDefinition[] removedSteps = dataset.Steps
            .Where(step => step.QuestId == questId)
            .ToArray();
        HashSet<int> dialogueIds = DialogueIds(removedSteps);
        Save(
            dataset.Quests.Where(quest => quest.Id != questId),
            dataset.Steps.Where(step => step.QuestId != questId),
            dataset.Turns.Where(turn => !dialogueIds.Contains(turn.DialogueId)));
    }

    public StoryQuestStepCreationResult CreateStep(int questId)
    {
        StoryQuestDataset dataset = GetDataset();
        if (!dataset.Quests.Any(quest => quest.Id == questId))
        {
            throw Missing("StoryQuest", questId);
        }

        StoryQuestStepDefinition[] existingSteps = dataset.Steps
            .Where(step => step.QuestId == questId)
            .ToArray();
        int order = existingSteps.Length == 0
            ? 0
            : checked(existingSteps.Max(step => step.StepOrder) + 1);
        StoryQuestStepCreationResult creation = CreateStepDefinition(dataset, questId, order);
        Save(
            dataset.Quests,
            dataset.Steps.Append(creation.Step),
            dataset.Turns.Concat(creation.Turns));
        return creation;
    }

    private StoryQuestStepDefinition ReplaceStep(StoryQuestStepDefinition step)
    {
        ArgumentNullException.ThrowIfNull(step);
        StoryQuestDataset dataset = GetDataset();
        var steps = dataset.Steps.ToList();
        int index = steps.FindIndex(candidate => candidate.Id == step.Id);
        if (index < 0)
        {
            throw Missing("StoryQuestStep", step.Id);
        }

        if (!dataset.Quests.Any(quest => quest.Id == step.QuestId))
        {
            throw Missing("StoryQuest", step.QuestId);
        }

        steps[index] = step;
        Save(dataset.Quests, steps, dataset.Turns);
        return step;
    }

    public StoryQuestStepDefinition UpdateStep(
        int stepId,
        int npcId,
        StoryQuestConditionType conditionType,
        int conditionValue,
        IReadOnlyList<int> conditionParams,
        string description,
        IReadOnlyList<QuestReward> rewards)
    {
        StoryQuestDataset dataset = GetDataset();
        StoryQuestStepDefinition current = dataset.Steps.FirstOrDefault(step => step.Id == stepId)
            ?? throw Missing("StoryQuestStep", stepId);
        return ReplaceStep(
            current with
            {
                NpcId = npcId,
                ConditionType = conditionType,
                ConditionValue = conditionValue,
                ConditionParams = conditionParams,
                Description = description,
                Rewards = rewards,
            });
    }

    public void DeleteStep(int stepId)
    {
        StoryQuestDataset dataset = GetDataset();
        StoryQuestStepDefinition step = dataset.Steps.FirstOrDefault(candidate => candidate.Id == stepId)
            ?? throw Missing("StoryQuestStep", stepId);
        HashSet<int> dialogueIds = DialogueIds([step]);
        Save(
            dataset.Quests,
            dataset.Steps.Where(candidate => candidate.Id != stepId),
            dataset.Turns.Where(turn => !dialogueIds.Contains(turn.DialogueId)));
    }

    public IReadOnlyList<StoryQuestStepDefinition> ReorderSteps(
        int questId,
        IReadOnlyList<int> orderedStepIds)
    {
        ArgumentNullException.ThrowIfNull(orderedStepIds);
        StoryQuestDataset dataset = GetDataset();
        StoryQuestStepDefinition[] questSteps = dataset.Steps
            .Where(step => step.QuestId == questId)
            .ToArray();
        if (questSteps.Length == 0 && !dataset.Quests.Any(quest => quest.Id == questId))
        {
            throw Missing("StoryQuest", questId);
        }

        HashSet<int> expected = questSteps.Select(step => step.Id).ToHashSet();
        if (orderedStepIds.Count != expected.Count
            || orderedStepIds.Distinct().Count() != orderedStepIds.Count
            || orderedStepIds.Any(id => !expected.Contains(id)))
        {
            throw new ArgumentException(
                "The order must contain every step in the quest exactly once.",
                nameof(orderedStepIds));
        }

        Dictionary<int, int> orderById = orderedStepIds
            .Select((id, index) => (id, index))
            .ToDictionary(pair => pair.id, pair => pair.index);
        StoryQuestStepDefinition[] steps = dataset.Steps
            .Select(
                step => step.QuestId == questId
                    ? step with { StepOrder = orderById[step.Id] }
                    : step)
            .ToArray();
        StoryQuestDataset saved = Save(dataset.Quests, steps, dataset.Turns);
        return saved.Steps.Where(step => step.QuestId == questId).ToArray();
    }

    public StoryConversationTurnDefinition CreateTurn(int dialogueId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(dialogueId);

        StoryQuestDataset dataset = GetDataset();
        StoryConversationTurnDefinition[] existing = dataset.Turns
            .Where(turn => turn.DialogueId == dialogueId)
            .ToArray();
        int id = existing.Length == 0
            ? checked((dialogueId * 1000) + 1)
            : existing.Max(turn => turn.Id) + 1;
        HashSet<int> usedIds = dataset.Turns.Select(turn => turn.Id).ToHashSet();
        while (usedIds.Contains(id))
        {
            id = checked(id + 1);
        }

        var turn = new StoryConversationTurnDefinition(
            id,
            dialogueId,
            existing.Length == 0 ? 0 : checked(existing.Max(turn => turn.OrderIndex) + 1),
            0,
            "非玩家角色",
            string.Empty,
            "请填写对话内容");
        Save(dataset.Quests, dataset.Steps, dataset.Turns.Append(turn));
        return turn;
    }

    private StoryConversationTurnDefinition ReplaceTurn(StoryConversationTurnDefinition turn)
    {
        ArgumentNullException.ThrowIfNull(turn);
        StoryQuestDataset dataset = GetDataset();
        var turns = dataset.Turns.ToList();
        int index = turns.FindIndex(candidate => candidate.Id == turn.Id);
        if (index < 0)
        {
            throw Missing("StoryConversationTurn", turn.Id);
        }

        turns[index] = turn;
        Save(dataset.Quests, dataset.Steps, turns);
        return turn;
    }

    public StoryConversationTurnDefinition UpdateTurn(
        int turnId,
        int orderIndex,
        int speakerType,
        string speakerName,
        string speakerImage,
        string text)
    {
        StoryQuestDataset dataset = GetDataset();
        StoryConversationTurnDefinition current = dataset.Turns.FirstOrDefault(turn => turn.Id == turnId)
            ?? throw Missing("StoryConversationTurn", turnId);
        return ReplaceTurn(
            current with
            {
                OrderIndex = orderIndex,
                SpeakerType = speakerType,
                SpeakerName = speakerName,
                SpeakerImage = speakerImage,
                Text = text,
            });
    }

    public void DeleteTurn(int turnId)
    {
        StoryQuestDataset dataset = GetDataset();
        if (!dataset.Turns.Any(turn => turn.Id == turnId))
        {
            throw Missing("StoryConversationTurn", turnId);
        }

        Save(
            dataset.Quests,
            dataset.Steps,
            dataset.Turns.Where(turn => turn.Id != turnId));
    }

    public StoryQuestDataset MigrateIds()
    {
        StoryQuestDataset dataset = GetDataset();
        StoryQuestStepDefinition[] steps = dataset.Steps
            .GroupBy(step => step.QuestId)
            .OrderBy(group => group.Key)
            .SelectMany(
                group => group
                    .OrderBy(step => step.StepOrder)
                    .ThenBy(step => step.Id)
                    .Select(
                        (step, index) => step with
                        {
                            Id = checked((group.Key * 1000) + index + 1),
                        }))
            .ToArray();
        StoryConversationTurnDefinition[] turns = dataset.Turns
            .GroupBy(turn => turn.DialogueId)
            .OrderBy(group => group.Key)
            .SelectMany(
                group => group
                    .OrderBy(turn => turn.OrderIndex)
                    .ThenBy(turn => turn.Id)
                    .Select(
                        (turn, index) => turn with
                        {
                            Id = checked((group.Key * 1000) + index + 1),
                        }))
            .ToArray();
        return Save(dataset.Quests, steps, turns);
    }

    private StoryQuestDataset Save(
        IEnumerable<StoryQuestDefinition> quests,
        IEnumerable<StoryQuestStepDefinition> steps,
        IEnumerable<StoryConversationTurnDefinition> turns)
    {
        var replacement = new StoryQuestDataset(
            quests.OrderBy(quest => quest.Id).ToArray(),
            steps.OrderBy(step => step.QuestId).ThenBy(step => step.StepOrder).ThenBy(step => step.Id).ToArray(),
            turns.OrderBy(turn => turn.DialogueId).ThenBy(turn => turn.OrderIndex).ThenBy(turn => turn.Id).ToArray());
        store.ReplaceStoryQuestDataset(replacement);
        return replacement;
    }

    private static HashSet<int> DialogueIds(IEnumerable<StoryQuestStepDefinition> steps)
    {
        var result = new HashSet<int>();
        foreach (StoryQuestStepDefinition step in steps)
        {
            if (step.AcceptDialogueId > 0) result.Add(step.AcceptDialogueId);
            if (step.SubmitDialogueId > 0) result.Add(step.SubmitDialogueId);
            if (step.ProcessingDialogueId > 0) result.Add(step.ProcessingDialogueId);
        }

        return result;
    }

    private static StoryQuestStepCreationResult CreateStepDefinition(
        StoryQuestDataset dataset,
        int questId,
        int stepOrder)
    {
        HashSet<int> stepIds = dataset.Steps.Select(step => step.Id).ToHashSet();
        int stepId = checked((questId * 1000) + stepOrder + 1);
        while (stepIds.Contains(stepId))
        {
            stepId = checked(stepId + 1);
        }

        int dialogueId = NextDialogueId(dataset);
        int acceptDialogueId = dialogueId;
        int processingDialogueId = checked(dialogueId + 1);
        int submitDialogueId = checked(dialogueId + 2);
        var step = new StoryQuestStepDefinition(
            stepId,
            questId,
            stepOrder,
            0,
            acceptDialogueId,
            submitDialogueId,
            processingDialogueId,
            StoryQuestConditionType.StartBattle,
            1,
            [],
            "请填写步骤描述",
            []);

        HashSet<int> usedTurnIds = dataset.Turns.Select(turn => turn.Id).ToHashSet();
        StoryConversationTurnDefinition[] turns =
        [
            CreateDefaultTurn(acceptDialogueId, "请填写接任务对话", usedTurnIds),
            CreateDefaultTurn(processingDialogueId, "请填写进行中对话", usedTurnIds),
            CreateDefaultTurn(submitDialogueId, "请填写提交任务对话", usedTurnIds),
        ];
        return new StoryQuestStepCreationResult(step, turns);
    }

    private static int NextDialogueId(StoryQuestDataset dataset)
    {
        IEnumerable<int> ids = dataset.Turns.Select(turn => turn.DialogueId)
            .Concat(
                dataset.Steps.SelectMany(
                    step => new[]
                    {
                        step.AcceptDialogueId,
                        step.ProcessingDialogueId,
                        step.SubmitDialogueId,
                    }))
            .Where(id => id > 0);
        return ids.Any() ? checked(ids.Max() + 1) : 1;
    }

    private static StoryConversationTurnDefinition CreateDefaultTurn(
        int dialogueId,
        string text,
        HashSet<int> usedIds)
    {
        int id = checked((dialogueId * 1000) + 1);
        while (!usedIds.Add(id))
        {
            id = checked(id + 1);
        }

        return new StoryConversationTurnDefinition(
            id,
            dialogueId,
            0,
            0,
            "非玩家角色",
            string.Empty,
            text);
    }

    private static void ValidateQuestPrerequisites(
        StoryQuestDefinition replacement,
        IReadOnlyList<StoryQuestDefinition> quests)
    {
        if (replacement.Prerequisites.Any(prerequisite => prerequisite.Value <= 0))
        {
            throw new ArgumentException("前置依赖的值必须大于 0。", nameof(replacement));
        }

        int[] directQuestDependencies = replacement.Prerequisites
            .Where(prerequisite => prerequisite.Type == StoryQuestPrerequisiteType.CompleteQuest)
            .Select(prerequisite => prerequisite.Value)
            .ToArray();
        if (directQuestDependencies.Contains(replacement.Id))
        {
            throw new ArgumentException("任务不能依赖自身完成。", nameof(replacement));
        }

        Dictionary<int, int[]> dependencies = quests
            .Select(quest => quest.Id == replacement.Id ? replacement : quest)
            .ToDictionary(
                quest => quest.Id,
                quest => quest.Prerequisites
                    .Where(prerequisite => prerequisite.Type == StoryQuestPrerequisiteType.CompleteQuest)
                    .Select(prerequisite => prerequisite.Value)
                    .ToArray());
        foreach (int dependency in directQuestDependencies)
        {
            if (ReachesQuest(dependency, replacement.Id, dependencies, []))
            {
                throw new ArgumentException("任务前置依赖形成了循环，无法保存。", nameof(replacement));
            }
        }
    }

    private static bool ReachesQuest(
        int current,
        int target,
        IReadOnlyDictionary<int, int[]> dependencies,
        HashSet<int> visited)
    {
        if (current == target)
        {
            return true;
        }

        return visited.Add(current)
               && dependencies.TryGetValue(current, out int[]? next)
               && next.Any(dependency => ReachesQuest(dependency, target, dependencies, visited));
    }

    private static KeyNotFoundException Missing(string type, int id)
    {
        return new KeyNotFoundException($"{type} {id} does not exist in the working copy.");
    }
}
