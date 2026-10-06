namespace RtsSkillStudio.Agent.Llm;

public sealed record LlmChatApiRequest(
    string? Provider,
    string Message,
    string? Instructions = null,
    string? Model = null,
    int? SkillId = null,
    IReadOnlyList<LlmChatMessage>? History = null,
    string? ReasoningEffort = null
);

public sealed record LlmChatMessage(
    string Role,
    string Content
);

public sealed record LlmChatApiResponse(
    string Provider,
    string Model,
    string Text,
    long LatencyMs
);

public sealed record LlmCompletionRequest(
    string Message,
    string? Instructions = null,
    string? Model = null,
    IReadOnlyList<LlmChatMessage>? History = null,
    string? ReasoningEffort = null
);

public sealed record LlmCompletionResult(
    string Provider,
    string Model,
    string Text,
    long LatencyMs
);

public sealed record LlmProviderDescriptor(
    string Name,
    string Kind,
    string BaseUrl,
    string Model,
    bool ApiKeyConfigured,
    bool IsDefault
);

public sealed record LlmModelDescriptor(
    string Name,
    string DisplayName
);
