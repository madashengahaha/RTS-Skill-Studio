using RtsSkillStudio.Agent.Workspaces;
using Xunit;

namespace RtsSkillStudio.Tests;

public sealed class ExecutionChainProjectionPolicyTests
{
    [Fact]
    public void ProjectsExecutionSubtreesButKeepsConfiguredReferencesOut()
    {
        SkillChainSnapshot chain = new(
            Revision: "test",
            RootKey: "TbSkill:1",
            RootNamespace: "TbSkill",
            RootId: 1,
            FocusKey: "TbSkill:1",
            Nodes:
            [
                Node("TbSkill:1", "TbSkill", "技能"),
                Node("EffectGroup:10", "EffectGroup", "效果组", isVirtual: true),
                Node(
                    "TbEffect:170",
                    "TbEffect",
                    "重新索敌",
                    fields: new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["__executor"] =
                        [
                            "重新索敌并执行 · Research · 枚举值 14"
                        ]
                    }
                ),
                Node(
                    "TbEffect:180",
                    "TbEffect",
                    "造成伤害",
                    fields: new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["__executor"] =
                        [
                            "造成伤害 · Damage · 枚举值 4"
                        ]
                    }
                ),
                Node("TbSearch:30", "TbSearch", "索敌"),
                Node(
                    "TbDamagePipeline:40",
                    "TbDamagePipeline",
                    "伤害管线"
                ),
                Node("TbSkillResource:50", "TbSkillResource", "特效"),
                Node("TbResource:60", "TbResource", "资源"),
                Node("TbEntity:70", "TbEntity", "搜索限定单位"),
                Node("EffectGroup:20", "EffectGroup", "后续效果组", isVirtual: true),
                Node("TbEffect:210", "TbEffect", "后续伤害")
            ],
            Edges:
            [
                Edge(
                    "TbSkill:1",
                    "EffectGroup:10",
                    "mainEffect",
                    "主要效果",
                    "effect_group_id"
                ),
                Edge(
                    "EffectGroup:10",
                    "TbEffect:170",
                    "orderedEffect",
                    "有序效果"
                ),
                Edge(
                    "EffectGroup:10",
                    "TbEffect:180",
                    "orderedEffect",
                    "有序效果"
                ),
                Edge(
                    "TbEffect:170",
                    "TbSearch:30",
                    "searchId",
                    "搜索配置",
                    "action_param",
                    0
                ),
                Edge(
                    "TbEffect:170",
                    "EffectGroup:20",
                    "effectGroupId",
                    "效果组",
                    "action_param",
                    1
                ),
                Edge(
                    "TbEffect:180",
                    "TbDamagePipeline:40",
                    "pipeline",
                    "伤害管线",
                    "action_param",
                    0
                ),
                Edge(
                    "TbEffect:180",
                    "TbSkillResource:50",
                    "fx",
                    "播放特效",
                    "fx"
                ),
                Edge(
                    "TbSearch:30",
                    "TbEntity:70",
                    "unitType",
                    "限定单位",
                    "table_id"
                ),
                Edge(
                    "EffectGroup:20",
                    "TbEffect:210",
                    "orderedEffect",
                    "有序效果"
                )
            ],
            IncomingReferences: [],
            IncomingReferencesTruncated: false
        );
        var policy = new ExecutionChainProjectionPolicy(
            Path.Combine(
                FindContractRoot(),
                "config",
                "capability-registry.v0.json"
            )
        );

        SkillChainSnapshot projected = policy.Project(chain);

        Assert.Equal(
            [
                "TbSkill:1",
                "EffectGroup:10",
                "TbEffect:170",
                "TbEffect:180",
                "TbSearch:30",
                "EffectGroup:20",
                "TbEffect:210"
            ],
            projected.Nodes.Select(node => node.Key)
        );
        Assert.DoesNotContain(
            projected.Edges,
            edge => edge.Target is "TbDamagePipeline:40" or "TbSkillResource:50"
        );
        Assert.DoesNotContain(
            projected.Nodes,
            node => node.Key == "TbEntity:70"
        );
        Assert.Contains(
            projected.Edges,
            edge =>
                edge.Source == "TbEffect:170"
                && edge.Target == "EffectGroup:20"
        );
        Assert.Contains(
            projected.Edges,
            edge =>
                edge.Source == "TbEffect:170"
                && edge.Target == "TbSearch:30"
        );
    }

    [Fact]
    public void UsesParameterIndexWhenTheProjectedRoleNameChanges()
    {
        SkillChainSnapshot chain = new(
            Revision: "test",
            RootKey: "TbSkill:1",
            RootNamespace: "TbSkill",
            RootId: 1,
            FocusKey: "TbSkill:1",
            Nodes:
            [
                Node("TbSkill:1", "TbSkill", "技能"),
                Node("EffectGroup:10", "EffectGroup", "效果组", isVirtual: true),
                Node(
                    "TbEffect:170",
                    "TbEffect",
                    "条件分支",
                    fields: new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["__executor"] =
                        [
                            "条件分支 · ConditionBranch · 枚举值 15"
                        ]
                    }
                ),
                Node(
                    "EffectGroup:20",
                    "EffectGroup",
                    "成立效果",
                    isVirtual: true
                )
            ],
            Edges:
            [
                Edge(
                    "TbSkill:1",
                    "EffectGroup:10",
                    "mainEffect",
                    "主要效果",
                    "effect_group_id"
                ),
                Edge(
                    "EffectGroup:10",
                    "TbEffect:170",
                    "orderedEffect",
                    "有序效果"
                ),
                Edge(
                    "TbEffect:170",
                    "EffectGroup:20",
                    "branchSuccess",
                    "成立",
                    "action_param",
                    1
                )
            ],
            IncomingReferences: [],
            IncomingReferencesTruncated: false
        );
        var policy = new ExecutionChainProjectionPolicy(
            Path.Combine(
                FindContractRoot(),
                "config",
                "capability-registry.v0.json"
            )
        );

        SkillChainSnapshot projected = policy.Project(chain);

        Assert.Contains(
            projected.Nodes,
            node => node.Key == "EffectGroup:20"
        );
        Assert.Contains(
            projected.Edges,
            edge =>
                edge.Source == "TbEffect:170"
                && edge.Target == "EffectGroup:20"
                && edge.Role == "branchSuccess"
        );
    }

    private static SkillChainNode Node(
        string key,
        string @namespace,
        string label,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? fields = null,
        bool isVirtual = false
    )
    {
        int separator = key.IndexOf(':');
        int id = int.Parse(key[(separator + 1)..]);
        return new SkillChainNode(
            key,
            @namespace,
            id,
            label,
            label,
            null,
            isVirtual,
            false,
            false,
            key == "TbSkill:1",
            0,
            fields ?? new Dictionary<string, IReadOnlyList<string>>()
        );
    }

    private static SkillChainEdge Edge(
        string source,
        string target,
        string role,
        string label,
        string? sourceField = null,
        int? parameterIndex = null
    )
    {
        return new SkillChainEdge(
            $"{source}|{role}|{target}|{parameterIndex}",
            source,
            target,
            role,
            label,
            null,
            false,
            sourceField,
            parameterIndex
        );
    }

    private static string FindContractRoot()
    {
        DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "RtsSkillStudio.sln")))
            {
                return Path.Combine(
                    current.FullName,
                    "contracts",
                    "rts-skill-agent"
                );
            }
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
