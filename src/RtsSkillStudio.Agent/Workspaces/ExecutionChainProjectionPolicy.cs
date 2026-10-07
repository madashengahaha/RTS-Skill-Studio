using System.Text.Json;

namespace RtsSkillStudio.Agent.Workspaces;

public sealed class ExecutionChainProjectionPolicy
{
    private readonly Lazy<ProjectionContract> _contract;

    public ExecutionChainProjectionPolicy(string capabilityRegistryPath)
    {
        string fullPath = Path.GetFullPath(capabilityRegistryPath);
        _contract = new Lazy<ProjectionContract>(
            () => Load(fullPath),
            LazyThreadSafetyMode.ExecutionAndPublication
        );
    }

    public SkillChainSnapshot Project(SkillChainSnapshot chain)
    {
        ProjectionContract contract = _contract.Value;
        if (!contract.Configured)
        {
            return chain;
        }

        Dictionary<string, SkillChainNode> nodesByKey = chain.Nodes
            .ToDictionary(node => node.Key, StringComparer.Ordinal);
        if (!nodesByKey.ContainsKey(chain.RootKey))
        {
            return chain;
        }

        Dictionary<string, SkillChainEdge[]> outgoing = chain.Edges
            .GroupBy(edge => edge.Source, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.Ordinal
            );
        var included = new HashSet<string>(StringComparer.Ordinal)
        {
            chain.RootKey
        };
        var expanded = new HashSet<string>(StringComparer.Ordinal)
        {
            chain.RootKey
        };
        var emitted = new HashSet<string>(StringComparer.Ordinal);
        var projectedEdges = new List<SkillChainEdge>();
        var queue = new Queue<string>();
        queue.Enqueue(chain.RootKey);

        while (queue.Count > 0)
        {
            string sourceKey = queue.Dequeue();
            foreach (
                SkillChainEdge edge in outgoing.GetValueOrDefault(sourceKey, [])
            )
            {
                if (!emitted.Add(edge.Id))
                {
                    continue;
                }

                nodesByKey.TryGetValue(edge.Source, out SkillChainNode? source);
                nodesByKey.TryGetValue(edge.Target, out SkillChainNode? target);
                ExecutionProjection projection = ResolveProjection(
                    contract,
                    edge,
                    source,
                    target
                );
                if (projection == ExecutionProjection.Hidden)
                {
                    continue;
                }

                included.Add(edge.Target);
                projectedEdges.Add(edge);
                if (
                    projection == ExecutionProjection.Subtree
                    && expanded.Add(edge.Target)
                )
                {
                    queue.Enqueue(edge.Target);
                }
            }
        }

        return chain with
        {
            Nodes = chain.Nodes
                .Where(node => included.Contains(node.Key))
                .ToArray(),
            Edges = projectedEdges.ToArray()
        };
    }

    private static ExecutionProjection ResolveProjection(
        ProjectionContract contract,
        SkillChainEdge edge,
        SkillChainNode? source,
        SkillChainNode? target
    )
    {
        if (
            string.Equals(
                edge.SourceField,
                "action_param",
                StringComparison.Ordinal
            )
        )
        {
            string category = Category(source);
            string? actionKey = ActionKey(source);
            ParameterProjection? projectedParameter = contract
                .FindAction(category, actionKey)
                ?.FindParameter(edge);
            if (projectedParameter is not null)
            {
                return projectedParameter.Projection;
            }

            string? referenceTarget =
                target?.Namespace
                ?? NamespaceFromKey(edge.Target);
            ParameterRule? rule = contract.ParameterRules.FirstOrDefault(
                candidate => candidate.Matches(
                    category,
                    actionKey,
                    edge.Role,
                    referenceTarget
                )
            );
            return rule?.Projection
                ?? contract.DefaultParameterProjection;
        }

        EdgeRule? edgeRule = contract.EdgeRules.FirstOrDefault(
            candidate => candidate.Matches(edge)
        );
        return edgeRule?.Projection ?? contract.DefaultEdgeProjection;
    }

    private static string Category(SkillChainNode? node)
    {
        return node?.Namespace switch
        {
            "TbEffect" => "effect",
            "TbCondition" => "condition",
            _ => ""
        };
    }

    private static string? ActionKey(SkillChainNode? node)
    {
        if (
            node is null
            || !node.Fields.TryGetValue("__executor", out var values)
            || values.Count == 0
        )
        {
            return null;
        }

        string[] parts = values[0].Split(
            " · ",
            StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries
        );
        return parts.Length >= 2 ? parts[1] : null;
    }

    private static string NamespaceFromKey(string key)
    {
        int separator = key.IndexOf(':');
        return separator > 0 ? key[..separator] : key;
    }

    private static ProjectionContract Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "找不到执行链投影契约。",
                path
            );
        }

        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(path)
        );
        if (
            !document.RootElement.TryGetProperty(
                "executionProjection",
                out JsonElement projection
            )
        )
        {
            return ProjectionContract.Disabled;
        }

        return new ProjectionContract(
            Configured: true,
            DefaultParameterProjection: ParseProjection(
                projection.GetProperty("defaultParameterProjection").GetString()
            ),
            DefaultEdgeProjection: ParseProjection(
                projection.GetProperty("defaultEdgeProjection").GetString()
            ),
            ParameterRules: projection
                .GetProperty("parameterRules")
                .EnumerateArray()
                .Select(ParameterRule.FromJson)
                .ToArray(),
            EdgeRules: projection
                .GetProperty("edgeRules")
                .EnumerateArray()
                .Select(EdgeRule.FromJson)
                .ToArray(),
            Actions: LoadActions(document.RootElement)
        );
    }

    private static ExecutionProjection ParseProjection(string? value)
    {
        return value switch
        {
            "Subtree" => ExecutionProjection.Subtree,
            "Node" => ExecutionProjection.Node,
            "Hidden" => ExecutionProjection.Hidden,
            _ => throw new InvalidDataException(
                $"未知的执行链投影类型：{value ?? "<null>"}。"
            )
        };
    }

    private enum ExecutionProjection
    {
        Hidden,
        Node,
        Subtree
    }

    private sealed record ProjectionContract(
        bool Configured,
        ExecutionProjection DefaultParameterProjection,
        ExecutionProjection DefaultEdgeProjection,
        IReadOnlyList<ParameterRule> ParameterRules,
        IReadOnlyList<EdgeRule> EdgeRules,
        IReadOnlyDictionary<string, ActionProjection> Actions
    )
    {
        public static ProjectionContract Disabled { get; } = new(
            Configured: false,
            DefaultParameterProjection: ExecutionProjection.Node,
            DefaultEdgeProjection: ExecutionProjection.Node,
            ParameterRules: [],
            EdgeRules: [],
            Actions: new Dictionary<string, ActionProjection>(
                StringComparer.Ordinal
            )
        );

        public ActionProjection? FindAction(
            string category,
            string? actionKey
        )
        {
            if (
                string.IsNullOrWhiteSpace(category)
                || string.IsNullOrWhiteSpace(actionKey)
            )
            {
                return null;
            }

            return Actions.GetValueOrDefault($"{category}:{actionKey}");
        }
    }

    private sealed record ParameterProjection(
        int Index,
        string Key,
        ExecutionProjection Projection
    );

    private sealed record ActionProjection(
        string Category,
        string Key,
        IReadOnlyList<ParameterProjection> Parameters
    )
    {
        public ParameterProjection? FindParameter(SkillChainEdge edge)
        {
            return edge.ParameterIndex is int index
                ? Parameters.FirstOrDefault(
                    parameter => parameter.Index == index
                )
                : Parameters.FirstOrDefault(
                    parameter => string.Equals(
                        parameter.Key,
                        edge.Role,
                        StringComparison.Ordinal
                    )
                );
        }
    }

    private sealed record ParameterRule(
        string? Category,
        string? ActionKey,
        string? ParameterKey,
        string? ReferenceTarget,
        ExecutionProjection Projection
    )
    {
        public bool Matches(
            string category,
            string? actionKey,
            string? parameterKey,
            string? referenceTarget
        )
        {
            return (
                    Category is null
                    || string.Equals(
                        Category,
                        category,
                        StringComparison.Ordinal
                    )
                )
                && (
                    ActionKey is null
                    || string.Equals(
                        ActionKey,
                        actionKey,
                        StringComparison.Ordinal
                    )
                )
                && (
                    ParameterKey is null
                    || string.Equals(
                        ParameterKey,
                        parameterKey,
                        StringComparison.Ordinal
                    )
                )
                && (
                    ReferenceTarget is null
                    || string.Equals(
                        ReferenceTarget,
                        referenceTarget,
                        StringComparison.Ordinal
                    )
                );
        }

        public static ParameterRule FromJson(JsonElement element)
        {
            return new ParameterRule(
                OptionalString(element, "category"),
                OptionalString(element, "actionKey"),
                OptionalString(element, "parameterKey"),
                OptionalString(element, "referenceTarget"),
                ParseProjection(
                    element.GetProperty("projection").GetString()
                )
            );
        }
    }

    private sealed record EdgeRule(
        string? Role,
        string? SourceField,
        ExecutionProjection Projection
    )
    {
        public bool Matches(SkillChainEdge edge)
        {
            return (
                    Role is null
                    || string.Equals(
                        Role,
                        edge.Role,
                        StringComparison.Ordinal
                    )
                )
                && (
                    SourceField is null
                    || string.Equals(
                        SourceField,
                        edge.SourceField,
                        StringComparison.Ordinal
                    )
                );
        }

        public static EdgeRule FromJson(JsonElement element)
        {
            return new EdgeRule(
                OptionalString(element, "role"),
                OptionalString(element, "sourceField"),
                ParseProjection(
                    element.GetProperty("projection").GetString()
                )
            );
        }
    }

    private static string? OptionalString(
        JsonElement element,
        string propertyName
    )
    {
        return element.TryGetProperty(propertyName, out JsonElement value)
            ? value.GetString()
            : null;
    }

    private static IReadOnlyDictionary<string, ActionProjection> LoadActions(
        JsonElement registry
    )
    {
        var actions = new Dictionary<string, ActionProjection>(
            StringComparer.Ordinal
        );
        foreach (
            (string category, string propertyName) in new[]
            {
                ("effect", "effects"),
                ("condition", "conditions")
            }
        )
        {
            if (
                !registry.TryGetProperty(
                    propertyName,
                    out JsonElement actionElements
                )
            )
            {
                continue;
            }

            foreach (
                JsonElement actionElement in actionElements.EnumerateArray()
            )
            {
                string key = actionElement.GetProperty("key").GetString()
                    ?? "";
                IReadOnlyList<ParameterProjection> parameters =
                    actionElement
                        .GetProperty("parameters")
                        .EnumerateArray()
                        .Where(
                            parameter =>
                                parameter.TryGetProperty(
                                    "executionProjection",
                                    out _
                                )
                        )
                        .Select(
                            parameter => new ParameterProjection(
                                parameter.GetProperty("index").GetInt32(),
                                parameter.GetProperty("key").GetString()
                                    ?? "",
                                ParseProjection(
                                    parameter
                                        .GetProperty("executionProjection")
                                        .GetString()
                                )
                            )
                        )
                        .ToArray();
                actions[$"{category}:{key}"] = new ActionProjection(
                    category,
                    key,
                    parameters
                );
            }
        }

        return actions;
    }
}
