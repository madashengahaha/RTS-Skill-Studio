namespace RtsSkillStudio.Agent.Llm;

public sealed class LlmOptions
{
    public string DefaultProvider { get; set; } = "ollama";

    public Dictionary<string, LlmProviderOptions> Providers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

public sealed class LlmProviderOptions
{
    public string Kind { get; set; } = "OpenAiCompatibleChat";

    public string BaseUrl { get; set; } = "";

    public string Model { get; set; } = "";

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
