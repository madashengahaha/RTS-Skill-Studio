using Microsoft.AspNetCore.DataProtection;
using RtsSkillStudio.Agent.Llm;
using System.Text.Json;

namespace RtsSkillStudio.Api;

public sealed record ModelSettingsRequest(string BaseUrl, string Model, string? ApiKey = null,
    bool ClearApiKey = false, string ReasoningEffort = "", string Kind = "OpenAiCompatibleChat",
    bool MakeDefault = true, bool SupportsJsonSchema = false);

public sealed class ModelSettingsStore
{
    private readonly LlmOptions _options;
    private readonly IDataProtector _protector;
    private readonly string _path;
    private readonly object _gate = new();
    public ModelSettingsStore(LlmOptions options, IDataProtectionProvider protection, string path)
    {
        _options = options;
        _protector = protection.CreateProtector("RtsSkillStudio.ModelSettings.v1");
        _path = path;
        if (File.Exists(path))
        {
            var saved = JsonSerializer.Deserialize<LlmOptions>(_protector.Unprotect(File.ReadAllText(path)))
                ?? throw new InvalidDataException("Invalid model settings.");
            options.Providers = new(options.Providers, StringComparer.OrdinalIgnoreCase);
            foreach (var pair in saved.Providers)
            {
                if (options.Providers.TryGetValue(pair.Key, out var preset))
                    pair.Value.RequiresApiKey = preset.RequiresApiKey;
                options.Providers[pair.Key] = pair.Value;
            }
            if (options.Providers.ContainsKey(saved.DefaultProvider)) options.DefaultProvider = saved.DefaultProvider;
        }
    }

    public void Save(string name, ModelSettingsRequest request)
    {
        lock (_gate)
        {
            if (!_options.Providers.TryGetValue(name, out var current)) throw new ArgumentException("Unknown provider.");
            if (!Uri.TryCreate(request.BaseUrl.Trim(), UriKind.Absolute, out var uri)
                || (uri.Scheme != "https" && uri.Scheme != "http") || !string.IsNullOrEmpty(uri.UserInfo)
                || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new ArgumentException("API 地址必须是 HTTP/HTTPS 地址，且不含账号、查询参数或片段。");
            if (request.Kind is not ("OpenAiCompatibleChat" or "OpenAiResponses")) throw new ArgumentException("Unsupported API protocol.");
            if (request.ReasoningEffort.Length > 0 && !current.GetReasoningEfforts().Contains(request.ReasoningEffort))
                throw new ArgumentException("Unsupported reasoning effort.");
            if (request.ClearApiKey && !string.IsNullOrWhiteSpace(request.ApiKey)) throw new ArgumentException("Cannot set and clear API key together.");
            var updated = JsonSerializer.Deserialize<LlmProviderOptions>(JsonSerializer.Serialize(current))!;
            updated.BaseUrl = request.BaseUrl.Trim().TrimEnd('/');
            updated.Model = request.Model.Trim();
            updated.Kind = request.Kind;
            updated.ReasoningEffort = request.ReasoningEffort;
            updated.SupportsJsonSchema = request.SupportsJsonSchema;
            if (request.ClearApiKey) { updated.ApiKey = ""; updated.ApiKeyEnvironmentVariable = ""; }
            else if (!string.IsNullOrWhiteSpace(request.ApiKey)) updated.ApiKey = request.ApiKey.Trim();
            var providers = new Dictionary<string, LlmProviderOptions>(_options.Providers, StringComparer.OrdinalIgnoreCase) { [name] = updated };
            var next = new LlmOptions { Providers = providers, DefaultProvider = request.MakeDefault ? name : _options.DefaultProvider };
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string temp = _path + ".tmp";
            File.WriteAllText(temp, _protector.Protect(JsonSerializer.Serialize(next)));
            File.Move(temp, _path, true);
            _options.Providers = providers;
            _options.DefaultProvider = next.DefaultProvider;
        }
    }
}
