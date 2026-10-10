using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace RtsSkillStudio.Agent.Llm;

public sealed class OpenAiResponsesProvider : ILlmProvider
{
    private readonly LlmProviderOptions _options;
    private readonly HttpClient _httpClient;

    public OpenAiResponsesProvider(
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
            "OpenAiResponses",
            options.BaseUrl,
            options.Model,
            !string.IsNullOrWhiteSpace(options.ResolveApiKey()),
            isDefault,
            DisplayName: options.DisplayName
        );
    }

    public LlmProviderDescriptor Descriptor { get; }

    public async Task<LlmCompletionResult> CompleteAsync(
        LlmCompletionRequest request,
        CancellationToken cancellationToken
    )
    {
        _options.ValidateCredentials();
        var model = string.IsNullOrWhiteSpace(request.Model)
            ? _options.Model
            : request.Model;

        if (string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(
                $"LLM provider '{Descriptor.Name}' does not configure Model."
            );
        }

        var apiKey = _options.ResolveApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                $"LLM provider '{Descriptor.Name}' does not configure an API key."
            );
        }

        var payload = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["store"] = false
        };

        if (request.History is { Count: > 0 })
        {
            var input = new List<Dictionary<string, string>>();
            input.AddRange(
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
            input.Add(
                new Dictionary<string, string>
                {
                    ["role"] = "user",
                    ["content"] = request.Message
                }
            );
            payload["input"] = input;
        }
        else
        {
            payload["input"] = request.Message;
        }

        if (!string.IsNullOrWhiteSpace(request.Instructions))
        {
            payload["instructions"] = request.Instructions;
        }

        string? reasoningEffort = string.IsNullOrWhiteSpace(request.ReasoningEffort)
            ? _options.ReasoningEffort
            : request.ReasoningEffort;
        if (!string.IsNullOrWhiteSpace(reasoningEffort))
        {
            payload["reasoning"] = new Dictionary<string, object?>
            {
                ["effort"] = reasoningEffort
            };
        }

        using var cancellation = CreateTimeoutToken(cancellationToken);
        var stopwatch = Stopwatch.StartNew();

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            BuildEndpoint(_options.BaseUrl, "responses")
        )
        {
            Content = JsonContent.Create(payload)
        };

        AddAuthorization(httpRequest, apiKey);

        using var response = await _httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            cancellation.Token
        );
        var responseBody = await response.Content.ReadAsStringAsync(cancellation.Token);

        if (!response.IsSuccessStatusCode)
        {
            throw new LlmProviderException(
                $"OpenAI Responses request failed with {(int)response.StatusCode}: "
                    + Truncate(responseBody, 600)
            );
        }

        string text = ExtractOutputText(
            responseBody,
            out string? finishReason
        );
        stopwatch.Stop();

        return new LlmCompletionResult(
            Descriptor.Name,
            model,
            text,
            stopwatch.ElapsedMilliseconds,
            finishReason
        );
    }

    private static string ExtractOutputText(
        string responseBody,
        out string? finishReason
    )
    {
        finishReason = null;
        using var document = JsonDocument.Parse(responseBody);
        var root = document.RootElement;
        if (
            root.TryGetProperty("status", out var status)
            && status.ValueKind == JsonValueKind.String
            && string.Equals(
                status.GetString(),
                "incomplete",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            finishReason =
                root.TryGetProperty("incomplete_details", out var details)
                && details.TryGetProperty("reason", out var reason)
                && reason.ValueKind == JsonValueKind.String
                    ? reason.GetString()
                    : "incomplete";
        }

        if (
            root.TryGetProperty("output_text", out var outputText)
            && outputText.ValueKind == JsonValueKind.String
        )
        {
            return outputText.GetString() ?? "";
        }

        if (!root.TryGetProperty("output", out var output))
        {
            throw new LlmProviderException(
                "OpenAI Responses response did not contain an output array."
            );
        }

        var textParts = new List<string>();
        foreach (var item in output.EnumerateArray())
        {
            if (
                !item.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array
            )
            {
                continue;
            }

            foreach (var contentItem in content.EnumerateArray())
            {
                if (
                    contentItem.TryGetProperty("type", out var type)
                    && type.GetString() == "output_text"
                    && contentItem.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String
                )
                {
                    textParts.Add(text.GetString() ?? "");
                }
            }
        }

        if (textParts.Count == 0)
        {
            throw new LlmProviderException(
                "OpenAI Responses response did not contain output_text."
            );
        }

        return string.Join(Environment.NewLine, textParts);
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
