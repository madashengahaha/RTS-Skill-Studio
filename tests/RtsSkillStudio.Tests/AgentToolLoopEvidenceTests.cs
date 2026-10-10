using System.Net;
using System.Text.Json;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RtsSkillStudio.Agent.Llm;
using RtsSkillStudio.Agent.Patch;
using RtsSkillStudio.Agent.Workspaces;
using RtsSkillStudio.Api.Workspaces;
using Xunit;

namespace RtsSkillStudio.Tests;

public sealed class AgentToolLoopEvidenceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidProposalIsRepairedAndRawJsonIsValidated(bool invalidFirstReview)
    {
        string root = FindRoot();
        string contracts = Path.Combine(root, "contracts", "rts-skill-agent");
        string valid = await File.ReadAllTextAsync(Path.Combine(root, "contract-factory", "examples", "modify-skill-cooldown.plan.json"));
        var handler = new RepairHandler(valid, invalidFirstReview: invalidFirstReview);
        using var client = new HttpClient(handler);
        var factory = new LlmProviderFactory(client, new LlmOptions { DefaultProvider = "test", Providers = new()
        { ["test"] = new() { Kind = "OpenAiCompatibleChat", BaseUrl = "http://repair.test/v1", Model = "fake", SupportsJsonSchema = true } } });
        var environment = new TestEnvironment { ContentRootPath = Path.Combine(root, "src", "RtsSkillStudio.Api") };
        var workspace = new SkillWorkspaceService(new(), environment,
            new ExecutionChainProjectionPolicy(Path.Combine(contracts, "config", "capability-registry.v0.json")), NullLogger<SkillWorkspaceService>.Instance);
        var loop = new AgentToolLoop(factory, new EvidenceTools(), new(contracts), new(WorkbookPatchRegistryLoader.Load(contracts)), workspace, NullLogger<AgentToolLoop>.Instance);
        AgentTurnOutcome result = await loop.RunAsync("test", "fake", "none", "把冷却改成8秒", [], "workspace context",
            new(AgentIntentKind.Configuration, true, [], "test"), null, [], CancellationToken.None);
        Assert.Equal(invalidFirstReview ? 4 : 3, handler.Calls);
        Assert.Empty(result.PlanErrors);
        Assert.NotNull(result.PlanJson);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SemanticFailureBlocksReadyAndValidClarificationIsNotPromoted(bool clarification)
    {
        string root = FindRoot();
        string contracts = Path.Combine(root, "contracts", "rts-skill-agent");
        var plan = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(
            Path.Combine(root, "contract-factory", "examples", "modify-skill-cooldown.plan.json")))!;
        if (clarification)
        {
            plan["status"] = "NeedsClarification";
            plan["operations"] = new System.Text.Json.Nodes.JsonArray();
            plan["clarifications"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
                { ["key"] = "duration", ["question"] = "持续多久？", ["fieldPath"] = "request" });
        }
        var handler = new RepairHandler(plan.ToJsonString(), true, true);
        using var client = new HttpClient(handler);
        var factory = new LlmProviderFactory(client, new LlmOptions { DefaultProvider = "test", Providers = new()
            { ["test"] = new() { Kind = "OpenAiCompatibleChat", BaseUrl = "http://review.test/v1", Model = "fake", SupportsJsonSchema = true } } });
        var environment = new TestEnvironment { ContentRootPath = Path.Combine(root, "src", "RtsSkillStudio.Api") };
        var workspace = new SkillWorkspaceService(new(), environment,
            new ExecutionChainProjectionPolicy(Path.Combine(contracts, "config", "capability-registry.v0.json")), NullLogger<SkillWorkspaceService>.Instance);
        var loop = new AgentToolLoop(factory, new EvidenceTools(), new(contracts), new(WorkbookPatchRegistryLoader.Load(contracts)), workspace, NullLogger<AgentToolLoop>.Instance);
        AgentTurnOutcome result = await loop.RunAsync("test", "fake", "none", "从零创建技能，冷却8秒", [], "context",
            new(AgentIntentKind.Create, true, [], "test"), null, [], CancellationToken.None);
        Assert.Equal("NeedsClarification", result.Status);
        if (clarification)
        {
            Assert.Equal(1, handler.Calls);
            Assert.Empty(result.PlanErrors);
        }
        else
        {
            Assert.Contains(result.PlanErrors, error => error.StartsWith("semantic_review.missing"));
            Assert.NotEqual("Expected", result.PlanDisposition);
            Assert.Equal(6, handler.Calls);
        }
    }

    private sealed class RepairHandler(string valid, bool rejectReview = false, bool initialValid = false, bool invalidFirstReview = false) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        private int _reviewCalls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string payload = await request.Content!.ReadAsStringAsync(token);
            if (Calls > 0)
            {
                Assert.Contains("JSON Schema", payload);
                using JsonDocument json = JsonDocument.Parse(payload);
                Assert.Equal("json_schema", json.RootElement.GetProperty("response_format").GetProperty("type").GetString());
                Assert.Equal(0, json.RootElement.GetProperty("temperature").GetInt32());
            }
            bool review = payload.Contains("SkillPlanSemanticReview", StringComparison.Ordinal);
            string answer = review ? """
                    {"schemaVersion":0,"requirements":[{"key":"cooldown","dimension":"Values","sourceQuote":"8秒","requirement":"冷却8秒","status":"Covered","planPointers":["/operations/0/fields/cooldown"],"evidencePointers":["/0/userRequest"],"reason":"明确的用户数值"}]}
                    """
                : Calls == 0 && !initialValid ? "```json\n{\"schemaVersion\":0,\"operations\":[]}\n```" : valid;
            if (review && rejectReview) answer = answer.Replace("Covered", "Missing");
            if (review && _reviewCalls++ == 0 && invalidFirstReview)
                answer = answer.Replace("/0/userRequest", "/999/nonexistent");
            Calls++;
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = answer }, finish_reason = "stop" } } })) };
        }
    }
    [Fact]
    public async Task EnumQueriesDoNotReturnUnrelatedActionsOrWorkbookFields()
    {
        string root = FindRoot();
        var environment = new TestEnvironment { ContentRootPath = Path.Combine(root, "src", "RtsSkillStudio.Api") };
        var workspace = new SkillWorkspaceService(new(), environment,
            new ExecutionChainProjectionPolicy(Path.Combine(root, "contracts", "rts-skill-agent", "config", "capability-registry.v0.json")), NullLogger<SkillWorkspaceService>.Instance);
        var tools = new SkillReadOnlyToolService(workspace, environment);
        AgentToolExecution result = Assert.Single(await tools.ExecuteAsync(
            [new("get_capability_context", new System.Text.Json.Nodes.JsonObject { ["enum"] = "EUnitType" })], CancellationToken.None));
        Assert.False(result.IsError);
        using JsonDocument json = JsonDocument.Parse(result.ResultJson);
        Assert.Equal("Ready", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("EUnitType", Assert.Single(json.RootElement.GetProperty("enums").EnumerateArray()).GetProperty("name").GetString());
        foreach (string key in new[] { "effects", "conditions", "tableFields", "entities", "entityFields", "intents" })
            Assert.Empty(json.RootElement.GetProperty(key).EnumerateArray());
        Assert.True(result.ResultJson.Length < 10000);
        foreach (string type in new[] { "NumericType", "FlagLabel", "ValueSource", "EffectActionType" })
        {
            AgentToolExecution bounded = Assert.Single(await tools.ExecuteAsync(
                [new("get_capability_context", new System.Text.Json.Nodes.JsonObject { ["enum"] = type, ["enumValueLimit"] = 20 })], CancellationToken.None));
            Assert.True(bounded.ResultJson.Length < 20000, type + ": " + bounded.ResultJson.Length);
        }
    }

    [Fact]
    public async Task LaterToolRoundsRetainEarlierContractEvidence()
    {
        string root = FindRoot();
        string contracts = Path.Combine(root, "contracts", "rts-skill-agent");
        WorkbookPatchRegistry registry = WorkbookPatchRegistryLoader.Load(contracts);
        var handler = new EvidenceHandler();
        using var client = new HttpClient(handler);
        var factory = new LlmProviderFactory(client, new LlmOptions { DefaultProvider = "test", Providers = new()
        {
            ["test"] = new() { Kind = "OpenAiCompatibleChat", BaseUrl = "http://evidence.test/v1", Model = "fake" }
        } });
        var environment = new TestEnvironment { ContentRootPath = Path.Combine(root, "src", "RtsSkillStudio.Api") };
        var workspace = new SkillWorkspaceService(new(), environment,
            new ExecutionChainProjectionPolicy(Path.Combine(contracts, "config", "capability-registry.v0.json")), NullLogger<SkillWorkspaceService>.Instance);
        var loop = new AgentToolLoop(factory, new EvidenceTools(), new(contracts), new(registry), workspace, NullLogger<AgentToolLoop>.Instance);
        AgentTurnOutcome result = await loop.RunAsync("test", "fake", "none", "解释执行链", [], "workspace context",
            new(AgentIntentKind.Query, false, ["get_capability_context", "get_graph"], "test"), null, [], CancellationToken.None);
        Assert.Equal(3, handler.Calls);
        Assert.Equal(2, result.ToolExecutions.Count);
    }
    private sealed class EvidenceHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string payload = await request.Content!.ReadAsStringAsync(cancellationToken);
            if (Calls == 2)
            {
                Assert.Contains("earlier-contract", payload);
                Assert.Contains("later-graph", payload);
            }
            string text = Calls++ switch
            {
                0 => "```json\n{\"type\":\"skill_studio_tool_calls\",\"calls\":[{\"name\":\"get_capability_context\",\"arguments\":{}}]}\n```",
                1 => "```json\n{\"type\":\"skill_studio_tool_calls\",\"calls\":[{\"name\":\"get_graph\",\"arguments\":{}}]}\n```",
                _ => "Evidence retained."
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = text }, finish_reason = "stop" } } })) };
        }
    }
    private sealed class EvidenceTools : IAgentReadOnlyToolService
    {
        public IReadOnlyList<AgentToolDefinition> Definitions => [new("get_capability_context", "test", "{}"), new("get_graph", "test", "{}")];
        public Task<IReadOnlyList<AgentToolExecution>> ExecuteAsync(IReadOnlyList<AgentToolCall> calls, CancellationToken token) =>
            Task.FromResult<IReadOnlyList<AgentToolExecution>>(calls.Select(call => new AgentToolExecution(call.Name, "{}",
                call.Name == "get_capability_context" ? "{\"fact\":\"earlier-contract\"}" : "{\"fact\":\"later-graph\"}", false)).ToArray());
    }
    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "EvidenceTests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    private static string FindRoot([System.Runtime.CompilerServices.CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", ".."));
}
