using System.Net.Http.Headers;
using System.Text.Json;

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

    public async Task<IReadOnlyList<LlmModelDescriptor>> ListModelsAsync(
        string? name,
        CancellationToken cancellationToken
    )
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

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri($"{providerOptions.BaseUrl.TrimEnd('/')}/models")
        );
        string apiKey = providerOptions.ResolveApiKey();
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                apiKey
            );
        }

        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            cancellationToken
        );
        string body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new LlmProviderException(
                $"Model list request failed with {(int)response.StatusCode}: "
                    + Truncate(body, 600)
            );
        }

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        JsonElement models;
        if (
            !root.TryGetProperty("data", out models)
            && !root.TryGetProperty("models", out models)
        )
        {
            throw new LlmProviderException(
                "Model list response did not contain data or models."
            );
        }

        var results = new List<LlmModelDescriptor>();
        foreach (JsonElement model in models.EnumerateArray())
        {
            string? nameValue = GetString(model, "id")
                ?? GetString(model, "slug")
                ?? GetString(model, "name");
            if (string.IsNullOrWhiteSpace(nameValue))
            {
                continue;
            }

            string displayName = GetString(model, "display_name")
                ?? GetString(model, "displayName")
                ?? nameValue;
            results.Add(new LlmModelDescriptor(nameValue, displayName));
        }

        return results
            .GroupBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement property)
            && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
