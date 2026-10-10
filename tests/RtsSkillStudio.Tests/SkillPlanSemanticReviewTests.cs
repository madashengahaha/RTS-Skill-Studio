using System.Text.Json.Nodes;
using RtsSkillStudio.Agent.Llm;
using Xunit;

namespace RtsSkillStudio.Tests;

public sealed class SkillPlanSemanticReviewTests
{
    private static string Root([System.Runtime.CompilerServices.CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "..", "contracts", "rts-skill-agent"));

    private static JsonObject Report(string status = "Covered", string planPointer = "/operations/0/fields/cooldown", string evidencePointer = "/0/userRequest", string quote = "8秒") => new()
    {
        ["schemaVersion"] = 0,
        ["requirements"] = new JsonArray(new JsonObject
        {
            ["key"] = "r1", ["dimension"] = "Values", ["sourceQuote"] = quote,
            ["requirement"] = "原始需求", ["status"] = status,
            ["planPointers"] = new JsonArray(JsonValue.Create(planPointer)),
            ["evidencePointers"] = new JsonArray(JsonValue.Create(evidencePointer)),
            ["reason"] = "检查依据"
        })
    };

    private static async Task<SkillPlanSemanticReviewResult> Check(JsonObject report) =>
        await SkillPlanSemanticReview.ValidateAsync(report.ToJsonString(),
            await new SkillConfigPlanValidator(Root()).ReadSemanticReviewSchemaAsync(CancellationToken.None),
            """{"operations":[{"fields":{"cooldown":{"value":8}}}]}""",
            """[{"userRequest":"冷却8秒"}]""", ["冷却8秒"]);

    [Fact]
    public async Task CoveredRequiresRealBindingsAndOriginalUserEvidence()
    {
        Assert.Empty((await Check(Report())).Errors);
        Assert.Contains((await Check(Report(planPointer: "/operations/0/fields/invented"))).Errors,
            error => error.Contains("invalid_plan_pointer"));
        Assert.Contains((await Check(Report(evidencePointer: "/1/result"))).Errors,
            error => error.Contains("invalid_evidence_pointer"));
        Assert.Contains((await Check(Report(quote: "我编造的需求"))).Errors,
            error => error.Contains("invalid_quote"));
        JsonObject emptyBinding = Report();
        emptyBinding["requirements"]![0]!["planPointers"] = new JsonArray();
        Assert.Contains((await Check(emptyBinding)).Errors, error => error.Contains("missing_binding"));
    }

    [Theory]
    [InlineData("Missing", "向四周发射多枚飞行道具")]
    [InlineData("Missing", "覆盖敌方所有单位而非仅英雄")]
    [InlineData("Missing", "命中后延迟爆炸而非直接伤害")]
    [InlineData("Uncertain", "未配置数量与优先级的默认规则")]
    [InlineData("Uncertain", "施法动作与表现尚未配置")]
    public async Task AnyUncoveredRequirementBlocksApprovalRegardlessOfMechanism(string status, string requirement)
    {
        JsonObject report = Report(status);
        report["requirements"]![0]!["requirement"] = requirement;
        Assert.Contains((await Check(report)).Errors, error => error.Contains(requirement));
    }

    [Fact]
    public async Task EmptyMalformedAndDuplicateReportsDoNotPass()
    {
        JsonObject report = Report();
        report["requirements"]!.AsArray().Add(report["requirements"]![0]!.DeepClone());
        Assert.Contains((await Check(report)).Errors, error => error.Contains("duplicate_requirement"));
        report["requirements"] = new JsonArray();
        Assert.NotEmpty((await Check(report)).Errors);
        report = Report(); report["schemaVersion"] = 99;
        Assert.NotEmpty((await Check(report)).Errors);
    }
}
