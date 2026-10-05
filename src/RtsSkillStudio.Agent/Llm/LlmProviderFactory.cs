namespace RtsSkillStudio.Agent.Llm;

public sealed class LlmProviderFactory
{
    private readonly HttpClient _httpClient;
    private readonly LlmOptions _options;

    public LlmProviderFactory(HttpClient httpClient, LlmOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public IReadOnlyList<LlmProviderDescriptor> ListProviders()
    {
        return _options
            .Providers.Select(pair => Create(pair.Key, pair.Value).Descriptor)
            .OrderByDescending(provider => provider.IsDefault)
            .ThenBy(provider => provider.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public ILlmProvider GetProvider(string? name = null)
    {
        var providerName = string.IsNullOrWhiteSpace(name)
            ? _options.DefaultProvider
            : name;

        if (
            string.IsNullOrWhiteSpace(providerName)
            || !_options.Providers.TryGetValue(providerName, out var providerOptions)
        )
        {
            throw new ArgumentException(
                $"Unknown LLM provider '{providerName}'.",
                nameof(name)
            );
        }

        return Create(providerName, providerOptions);
    }

    private ILlmProvider Create(string name, LlmProviderOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.BaseUrl))
        {
            throw new InvalidOperationException(
                $"LLM provider '{name}' does not configure BaseUrl."
            );
        }

        var isDefault = string.Equals(
            name,
            _options.DefaultProvider,
            StringComparison.OrdinalIgnoreCase
        );

        return options.Kind.ToUpperInvariant() switch
        {
            "OPENAIRESPONSES" => new OpenAiResponsesProvider(
                name,
                options,
                _httpClient,
                isDefault
            ),
            "OPENAICOMPATIBLECHAT" => new OpenAiCompatibleChatProvider(
                name,
                options,
                _httpClient,
                isDefault
            ),
            _ => throw new InvalidOperationException(
                $"LLM provider '{name}' has unsupported kind '{options.Kind}'."
            )
        };
    }
}
