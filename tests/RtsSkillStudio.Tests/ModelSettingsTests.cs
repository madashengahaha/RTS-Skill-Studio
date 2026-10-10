using Microsoft.Extensions.Configuration;
using Xunit;
using Microsoft.AspNetCore.DataProtection;
using RtsSkillStudio.Agent.Llm;
using RtsSkillStudio.Api;
using System.Net;
using System.Text.Json;

namespace RtsSkillStudio.Tests;

public sealed class ModelSettingsTests
{
    [Fact]
    public void ConfiguredEffortsReplaceDefaults()
    {
        using var json = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"Providers\":{\"direct\":{\"ReasoningEfforts\":[\"none\",\"high\",\"max\"]}}}"));
        var options = new ConfigurationBuilder().AddJsonStream(json).Build().Get<LlmOptions>()!;
        Assert.Equal(new[] { "none", "high", "max" }, options.Providers["direct"].GetReasoningEfforts());
    }

    [Fact]
    public void SettingsPersistEncryptedKeepKeysAndDoNotExposeSecrets()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var protector = new EphemeralDataProtectionProvider();
            var options = Options();
            var store = new ModelSettingsStore(options, protector, Path.Combine(root, "settings"));
            store.Save("direct", new("https://example.test/v1", "model-a", "test-secret", ReasoningEffort:"high"));
            Assert.DoesNotContain("test-secret", File.ReadAllText(Path.Combine(root, "settings")));
            store.Save("direct", new("https://example.test/v1", "model-b", ReasoningEffort:"low"));
            var restored = Options();
            _ = new ModelSettingsStore(restored, protector, Path.Combine(root, "settings"));
            Assert.Equal("test-secret", restored.Providers["direct"].ResolveApiKey());
            Assert.Equal("model-b", restored.Providers["direct"].Model);
            var descriptor = new LlmProviderFactory(new HttpClient(), restored).ListProviders().Single();
            Assert.True(descriptor.ApiKeyConfigured);
            Assert.Equal("test-secret".Length, descriptor.ApiKeyLength);
            Assert.DoesNotContain("test-secret", JsonSerializer.Serialize(descriptor));
            store.Save("direct", new("https://example.test/v1", "model-b", ClearApiKey:true));
            Assert.Equal("", options.Providers["direct"].ResolveApiKey());
            Assert.Throws<ArgumentException>(() => store.Save("direct", new("https://user:secret@example.test", "m")));
            Assert.Throws<ArgumentException>(() => store.Save("direct", new("https://example.test", "m", ReasoningEffort:"invalid")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("none", "disabled")]
    [InlineData("high", "enabled")]
    public async Task ThinkingProtocolUsesExplicitToggle(string effort, string expected)
    {
        var handler = new CaptureHandler();
        var options = Options();
        options.Providers["direct"].ReasoningMode = "ThinkingAndEffort";
        var provider = new LlmProviderFactory(new HttpClient(handler), options).GetProvider();
        await provider.CompleteAsync(new("hello", ReasoningEffort:effort), CancellationToken.None);
        using var json = JsonDocument.Parse(handler.Body!);
        Assert.Equal(expected, json.RootElement.GetProperty("thinking").GetProperty("type").GetString());
        Assert.Equal(effort != "none", json.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task RequiredKeyBlocksOutboundRequestsWhileLocalRemainsUsable()
    {
        var handler = new CaptureHandler();
        var options = Options();
        options.Providers["cloud"] = new() { BaseUrl = "https://cloud.test/v1", Model = "cloud-model", RequiresApiKey = true };
        var factory = new LlmProviderFactory(new HttpClient(handler), options);
        await Assert.ThrowsAsync<LlmProviderException>(() => factory.GetProvider("cloud").CompleteAsync(new("hello"), CancellationToken.None));
        await Assert.ThrowsAsync<LlmProviderException>(() => factory.ListModelsAsync("cloud", CancellationToken.None));
        Assert.Null(handler.Body);
        var local = await factory.GetProvider("direct").CompleteAsync(new("hello"), CancellationToken.None);
        Assert.Equal("direct", local.Provider);
        Assert.Equal("test", local.Model);
        Assert.Equal("example.test", handler.Endpoint!.Host);
        options.Providers["cloud"].ApiKey = "fixture-key";
        var cloud = await factory.GetProvider("cloud").CompleteAsync(new("hello"), CancellationToken.None);
        Assert.Equal("cloud", cloud.Provider);
        Assert.Equal("cloud-model", cloud.Model);
        Assert.Equal("cloud.test", handler.Endpoint!.Host);
        using var payload = JsonDocument.Parse(handler.Body!);
        Assert.Equal("cloud-model", payload.RootElement.GetProperty("model").GetString());
        Assert.Equal("Studio", factory.ListProviders().Single(p => p.Name == "cloud").ApiKeySource);
    }

    private static LlmOptions Options() => new() { DefaultProvider = "direct", Providers = new() {
        ["direct"] = new() { BaseUrl = "https://example.test", Model = "test" }
    }};
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public Uri? Endpoint { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Endpoint = request.RequestUri;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"ok\"},\"finish_reason\":\"stop\"}]}") };
        }
    }
}
