using System.Text.Json.Nodes;
using RtsSkillStudio.Agent.Workspaces;
using Xunit;

namespace RtsSkillStudio.Tests;

public sealed class EffectActionParameterProjectorTests
{
    [Fact]
    public void ProjectsScaledEnumAndReferenceParametersForDamage()
    {
        JsonObject registry = LoadRegistry();
        SkillChainNode effect = Node(
            "TbEffect:100600180",
            "TbEffect",
            "英雄-闪电幽魂-风暴之眼-伤害",
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["__executor"] =
                [
                    "造成伤害 · Damage · 枚举值 4"
                ],
                ["action_param"] =
                [
                    "1006",
                    "1020",
                    "50000",
                    "10000"
                ]
            }
        );
        SkillChainNode pipeline = Node(
            "TbDamagePipeline:1006",
            "TbDamagePipeline",
            "闪电幽魂"
        );
        var nodesByKey = new Dictionary<string, SkillChainNode>(
            StringComparer.Ordinal
        )
        {
            [effect.Key] = effect,
            [pipeline.Key] = pipeline
        };

        JsonObject? details = EffectActionParameterProjector.Project(
            registry,
            effect,
            [
                Edge(
                    effect.Key,
                    pipeline.Key,
                    "pipeline",
                    "伤害管线",
                    "action_param",
                    0
                )
            ],
            nodesByKey
        );

        Assert.NotNull(details);
        JsonArray parameters = details!["parameters"]!.AsArray();
        Assert.Equal("TbDamagePipeline:1006", Value(parameters[0]!, "resolvedKey"));
        Assert.Equal("pipeline", Value(parameters[0]!, "key"));
        Assert.Equal("attackType", Value(parameters[1]!, "key"));
        Assert.Equal(
            "LightningDamage",
            parameters[1]!["enumValue"]!["name"]!.GetValue<string>()
        );
        Assert.Equal(
            "雷电",
            parameters[1]!["enumValue"]!["alias"]!.GetValue<string>()
        );
        Assert.Equal("fixedDamage", Value(parameters[2]!, "key"));
        Assert.Equal("5", Value(parameters[2]!, "logicalValue"));
        Assert.Equal("attackScale", Value(parameters[3]!, "key"));
        Assert.Equal("1", Value(parameters[3]!, "logicalValue"));
    }

    [Fact]
    public void ProjectsEveryRegisteredEffectAndConditionActionByIndex()
    {
        JsonObject registry = LoadRegistry();
        foreach ((string field, string namespaceName) in new[]
        {
            ("effects", "TbEffect"),
            ("conditions", "TbCondition")
        })
        {
            foreach (JsonObject action in registry[field]!.AsArray().OfType<JsonObject>())
            {
                JsonArray contractParameters = action["parameters"]!.AsArray();
                string actionKey = action["key"]!.GetValue<string>();
                SkillChainNode node = Node(
                    $"{namespaceName}:1",
                    namespaceName,
                    actionKey,
                    new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["__executor"] =
                        [
                            $"{actionKey} · {actionKey} · 枚举值 0"
                        ],
                        ["action_param"] = contractParameters
                            .OfType<JsonObject>()
                            .Select(
                                parameter =>
                                    parameter["index"]!.GetValue<int>()
                                        .ToString(
                                            System.Globalization.CultureInfo.InvariantCulture
                                        )
                            )
                            .ToArray()
                    }
                );

                JsonObject? details = EffectActionParameterProjector.Project(
                    registry,
                    node,
                    [],
                    new Dictionary<string, SkillChainNode>(
                        StringComparer.Ordinal
                    )
                    {
                        [node.Key] = node
                    }
                );

                Assert.NotNull(details);
                Assert.Equal(actionKey, Value(details!, "actionKey"));
                Assert.Equal(
                    contractParameters.Count,
                    details!["parameters"]!.AsArray().Count
                );
            }
        }
    }

    private static string? Value(JsonNode node, string propertyName)
    {
        return node[propertyName]?.GetValue<string>();
    }

    private static SkillChainNode Node(
        string key,
        string @namespace,
        string label,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? fields = null
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
            false,
            false,
            false,
            true,
            0,
            fields ?? new Dictionary<string, IReadOnlyList<string>>()
        );
    }

    private static SkillChainEdge Edge(
        string source,
        string target,
        string role,
        string label,
        string? sourceField,
        int? parameterIndex
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

    private static JsonObject LoadRegistry()
    {
        return JsonNode.Parse(
            File.ReadAllText(
                Path.Combine(
                    FindContractRoot(),
                    "config",
                    "capability-registry.v0.json"
                )
            )
        )!.AsObject();
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
