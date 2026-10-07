using RtsSkillStudio.Agent.Workspaces;

namespace RtsSkillStudio.Agent.Llm;

public sealed record AgentTurnOutcome(
    AgentIntentKind Intent,
    string Status,
    bool ExpectsPlan,
    string Provider,
    string Model,
    string Text,
    long LatencyMs,
    string? PlanJson,
    IReadOnlyList<string> PlanErrors,
    string PlanDisposition,
    IReadOnlyList<SkillPlanClarification> Clarifications,
    IReadOnlyList<SkillPlanUnsupported> Unsupported,
    IReadOnlyList<AgentToolExecution> ToolExecutions,
    StudioAssetRef? SelectedAsset,
    IReadOnlyList<StudioAssetRef> MentionedAssets
);
