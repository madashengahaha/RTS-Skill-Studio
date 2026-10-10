using RtsSkillStudio.Agent.Workspaces;
using RtsSkillStudio.Api.Workspaces;
using Xunit;

namespace RtsSkillStudio.Tests;

public sealed class AgentTargetResolutionTests
{
    [Fact]
    public void MentioningTheSelectedAssetTypeDoesNotIntroduceANewTarget()
    {
        var selected = new StudioAssetRef("TbSkill", 12340019);
        Assert.False(SkillAgentContextBuilder.HasConflictingNamespaceConstraint(selected,
            new HashSet<string> { "TbSkill" }));
        Assert.False(SkillAgentContextBuilder.HasConflictingNamespaceConstraint(selected,
            new HashSet<string>()));
        Assert.True(SkillAgentContextBuilder.HasConflictingNamespaceConstraint(selected,
            new HashSet<string> { "TbEffect" }));
        Assert.True(SkillAgentContextBuilder.HasConflictingNamespaceConstraint(null,
            new HashSet<string> { "TbSkill" }));
    }

    [Theory]
    [InlineData("改成 固定数值为0，3倍的攻击力伤害")]
    [InlineData("把冷却改成 8 秒")]
    [InlineData("把攻击倍率调整为 3")]
    public void ConfigurationFollowupDoesNotTreatPartialFieldMatchesAsTargets(string message)
    {
        var selected = new StudioAssetRef("TbEffect", 100800020);
        Assert.False(SkillAgentContextBuilder.ShouldUseMentionCandidate(selected, message, false));
        Assert.False(SkillAgentContextBuilder.ShouldUseMentionCandidate(selected, message, true, "攻击力"));
        Assert.True(SkillAgentContextBuilder.ShouldUseMentionCandidate(null, message, false));
    }

    [Fact]
    public void CompleteNameMustIdentifyTheTargetRatherThanTheNewValue()
    {
        var selected = new StudioAssetRef("TbEffect", 100800020);
        Assert.True(SkillAgentContextBuilder.ShouldUseMentionCandidate(
            selected, "把另一项资产的倍率改成3", true, "另一项资产"));
        Assert.False(SkillAgentContextBuilder.ShouldUseMentionCandidate(
            selected, "改成3倍攻击力", true, "攻击力"));
    }

    [Theory]
    [InlineData("把“另一项资产”的冷却改成8秒")]
    [InlineData("把骷髅王-冥火暴击-伤害改成3倍攻击力")]
    [InlineData("看下攻击力伤害")]
    public void ExplicitNamesAndQueriesStillAllowAssetDiscovery(string message)
    {
        Assert.True(SkillAgentContextBuilder.ShouldUseMentionCandidate(
            new StudioAssetRef("TbEffect", 100800020), message, false));
    }
}
