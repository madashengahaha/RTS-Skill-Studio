namespace RtsSkillStudio.Agent.Llm;

public sealed class LlmOptions
{
    public string DefaultProvider { get; set; } = "ollama";

    public Dictionary<string, LlmProviderOptions> Providers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class LlmProviderOptions
{
    public string ReasoningMode { get; set; } = "Effort";
    public string[] ReasoningEfforts { get; set; } = [];
    public IReadOnlyList<string> GetReasoningEfforts() => ReasoningEfforts.Length > 0
        ? ReasoningEfforts : ["none", "low", "medium", "high"];

    public string? DisplayName { get; set; }
    public bool SupportsJsonSchema { get; set; }
    public string Kind { get; set; } = "OpenAiCompatibleChat";

    public string BaseUrl { get; set; } = "";

    public string Model { get; set; } = "";

    public bool RequiresApiKey { get; set; }
    public string ApiKeySource => !string.IsNullOrWhiteSpace(ApiKey) ? "Studio"
        : !string.IsNullOrWhiteSpace(ResolveApiKey()) ? "Environment" : "None";

    public void ValidateCredentials()
    {
        if (RequiresApiKey && string.IsNullOrWhiteSpace(ResolveApiKey()))
            throw new LlmProviderException("当前供应商尚未配置 API Key，请在模型设置中填写并保存。");
    }

    public string ApiKey { get; set; } = "";

    public string ApiKeyEnvironmentVariable { get; set; } = "";

    public int TimeoutSeconds { get; set; } = 120;

    public string ReasoningEffort { get; set; } = "";

    public string ResolveApiKey()
    {
        if (!string.IsNullOrWhiteSpace(ApiKey))
        {
            return ApiKey;
        }

        return string.IsNullOrWhiteSpace(ApiKeyEnvironmentVariable)
            ? ""
            : Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable) ?? "";
    }
}
