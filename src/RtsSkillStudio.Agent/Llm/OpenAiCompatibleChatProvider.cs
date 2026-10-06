using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace RtsSkillStudio.Agent.Llm;

public sealed class OpenAiCompatibleChatProvider : ILlmProvider
{
    private readonly LlmProviderOptions _options;
    private readonly HttpClient _httpClient;

    public OpenAiCompatibleChatProvider(
        string name,
        LlmProviderOptions options,
        HttpClient httpClient,
        bool isDefault
    )
    {
        _options = options;
        _httpClient = httpClient;
        Descriptor = new LlmProviderDescriptor(
            name,
            "OpenAiCompatibleChat",
            options.BaseUrl,
            options.Model,
            !string.IsNullOrWhiteSpace(options.ResolveApiKey()),
            isDefault
        );
    }

    public LlmProviderDescriptor Descriptor { get; }

    public async Task<LlmCompletionResult> CompleteAsync(
        LlmCompletionRequest request,
        CancellationToken cancellationToken
    )
    {
        var model = string.IsNullOrWhiteSpace(request.Model)
            ? _options.Model
            : request.Model;

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(
                $"LLM provider '{Descriptor.Name}' does not configure Model."
            );
        }

        var messages = new List<Dictionary<string, string>>();
        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            messages.Add(
                new Dictionary<string, string>
                {
                    ["role"] = "system",
                    ["content"] = request.Instructions
                }
            );
        }

        messages.Add(
            new Dictionary<string, string>
            {
                ["role"] = "user",
                ["content"] = request.Message
            }
        );

        if (request.History is { Count: > 0 })
        {
            messages.InsertRange(
                Math.Max(0, messages.Count - 1),
                request
                    .History.TakeLast(16)
                    .Where(message => !string.IsNullOrWhiteSpace(message.Content))
                    .Select(
                        message =>
                            new Dictionary<string, string>
                            {
                                ["role"] = message.Role.Equals(
                                    "assistant",
                                    StringComparison.OrdinalIgnoreCase
                                )
                                    ? "assistant"
                                    : "user",
                                ["content"] = message.Content
                            }
                    )
            );
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = messages,
            ["stream"] = false
        };

        string? reasoningEffort = string.IsNullOrWhiteSpace(request.ReasoningEffort)
            ? _options.ReasoningEffort
            : request.ReasoningEffort;
        if (!string.IsNullOrWhiteSpace(reasoningEffort))
        {
            payload["reasoning_effort"] = reasoningEffort;
        }

        using var cancellation = CreateTimeoutToken(cancellationToken);
        var stopwatch = Stopwatch.StartNew();

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            BuildEndpoint(_options.BaseUrl, "chat/completions")
        )
        {
            Content = JsonContent.Create(payload)
        };

        AddAuthorization(httpRequest, _options.ResolveApiKey());

        using var response = await _httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellation.Token
        );
        var responseBody = await response.Content.ReadAsStringAsync(cancellation.Token);

        if (!response.IsSuccessStatusCode)
        {
            throw new LlmProviderException(
                $"OpenAI-compatible chat request failed with {(int)response.StatusCode}: "
                    + Truncate(responseBody, 600)
            );
        }

        var text = ExtractMessageText(responseBody);
        stopwatch.Stop();

        return new LlmCompletionResult(
            Descriptor.Name,
            model,
            text,
            stopwatch.ElapsedMilliseconds
        );
    }

    private static string ExtractMessageText(string responseBody)
    {
        using var document = JsonDocument.Parse(responseBody);
        if (
            !document.RootElement.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0
        )
        {
            throw new LlmProviderException(
                "OpenAI-compatible chat response did not contain choices."
            );
        }

        var firstChoice = choices[0];
        if (
            !firstChoice.TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.String
        )
        {
            throw new LlmProviderException(
                "OpenAI-compatible chat response did not contain message.content."
            );
        }

        return content.GetString() ?? "";
    }

    private CancellationTokenSource CreateTimeoutToken(
        CancellationToken cancellationToken
    )
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));
        return source;
    }

    private static void AddAuthorization(HttpRequestMessage request, string apiKey)
    {
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Bearer",
                apiKey
            );
        }
    }

    private static Uri BuildEndpoint(string baseUrl, string path)
    {
        return new Uri($"{baseUrl.TrimEnd('/')}/{path}");
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
