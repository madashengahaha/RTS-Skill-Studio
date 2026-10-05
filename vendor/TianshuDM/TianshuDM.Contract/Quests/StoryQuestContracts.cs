namespace TianshuDM.Contract.Quests;

public sealed record StoryQuestEditorStateResponse(
    IReadOnlyList<StoryQuestResponse> Quests,
    IReadOnlyList<StoryQuestStepResponse> Steps,
    IReadOnlyList<StoryConversationTurnResponse> Turns);

public sealed record StoryQuestResponse(
    int Id,
    string Name,
    string Description,
    bool IsRepeatable,
    bool StepAutoAccept,
    IReadOnlyList<StoryQuestPrerequisiteResponse> Prerequisites);

public sealed record StoryQuestPrerequisiteResponse(string Type, int Value);

public sealed record StoryQuestStepResponse(
    int Id,
    int QuestId,
    int StepOrder,
    int NpcId,
    int AcceptDialogueId,
    int SubmitDialogueId,
    int ProcessingDialogueId,
    string ConditionType,
    int ConditionValue,
    IReadOnlyList<QuestRewardResponse> Rewards,
    string Description,
    IReadOnlyList<int> ConditionParams);

public sealed record StoryConversationTurnResponse(
    int Id,
    int DialogueId,
    int OrderIndex,
    int SpeakerType,
    string SpeakerName,
    string SpeakerImage,
    string Text);

public sealed record UpdateStoryQuestRequest(
    string Name,
    string Description,
    bool IsRepeatable,
    bool StepAutoAccept,
    IReadOnlyList<StoryQuestPrerequisiteRequest> Prerequisites);

public sealed record StoryQuestPrerequisiteRequest(string Type, int Value);

public sealed record UpdateStoryQuestStepRequest(
    int NpcId,
    string ConditionType,
    int ConditionValue,
    IReadOnlyList<int> ConditionParams,
    string Description,
    IReadOnlyList<QuestRewardRequest> Rewards);

public sealed record UpdateStoryConversationTurnRequest(
    int OrderIndex,
    int SpeakerType,
    string SpeakerName,
    string SpeakerImage,
    string Text);

public sealed record StoryQuestCreationResponse(
    StoryQuestResponse Quest,
    StoryQuestStepResponse Step,
    IReadOnlyList<StoryConversationTurnResponse> Turns);

public sealed record StoryQuestStepCreationResponse(
    StoryQuestStepResponse Step,
    IReadOnlyList<StoryConversationTurnResponse> Turns);

public sealed record ReorderStoryQuestStepsRequest(IReadOnlyList<int> StepIds);

public sealed record StoryQuestEditorMetadataResponse(
    IReadOnlyList<StoryQuestPrerequisiteTypeOptionResponse> PrerequisiteTypes,
    IReadOnlyList<StoryQuestConditionTypeOptionResponse> ConditionTypes,
    IReadOnlyList<StoryQuestRewardTypeOptionResponse> RewardTypes,
    IReadOnlyList<LobbyCharacterReferenceResponse> Characters,
    IReadOnlyList<WorldMapReferenceResponse> Maps,
    IReadOnlyList<StoryQuestReferenceOptionResponse> CombatUnits,
    IReadOnlyList<StoryQuestReferenceTableResponse> ReferenceTables);

public sealed record StoryQuestPrerequisiteTypeOptionResponse(
    string Value,
    string Label,
    string ValueKind);

public sealed record StoryQuestConditionTypeOptionResponse(
    string Value,
    string Label,
    string ValueKind,
    string? ParamsKind);

public sealed record StoryQuestRewardTypeOptionResponse(
    string Value,
    string Label,
    string? ConfigSource,
    bool ConfigRequired);

public sealed record LobbyCharacterReferenceResponse(
    int Id,
    string Name,
    int ModelId,
    IReadOnlyList<string> ScenePaths);

public sealed record WorldMapReferenceResponse(int Id, string Name);

public sealed record StoryQuestReferenceOptionResponse(
    int Id,
    string Name,
    string TableKey,
    string Category);

public sealed record StoryQuestReferenceTableResponse(
    string TableKey,
    string DisplayName,
    IReadOnlyList<StoryQuestReferenceOptionResponse> Options);
