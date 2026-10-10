using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using RtsSkillStudio.Agent;
using RtsSkillStudio.Agent.Llm;
using RtsSkillStudio.Agent.Workspaces;

namespace RtsSkillStudio.Api.Workspaces;

public sealed class SkillReadOnlyToolService(
    SkillWorkspaceService workspace,
    IHostEnvironment environment
) : IAgentReadOnlyToolService
{
    private static readonly Regex QueryIdentifierRegex = new(
        @"[A-Za-z_][A-Za-z0-9_]*",
        RegexOptions.Compiled
    );

    private readonly Lazy<Task<JsonObject>> _registry = new(
        () => LoadRegistryAsync(environment)
    );
    private readonly Lazy<Task<JsonObject>> _creation = new(async () =>
    {
        string json = await File.ReadAllTextAsync(ContractPath(environment, "config", "default-value-contract.v0.json"));
        JsonObject defaults = JsonNode.Parse(json)!.AsObject();
        JsonObject creation = defaults["creation"]?.DeepClone().AsObject() ?? new JsonObject();
        creation["editing"] = defaults["editing"]?.DeepClone();
        return creation;
    });
    private readonly Lazy<Task<JsonObject>> _mechanisms = new(async () =>
    {
        JsonNode? document = JsonNode.Parse(await File.ReadAllTextAsync(
            ContractPath(environment, "config", "default-mechanism-contract.v0.json")));
        return document!.AsObject();
    });

    private static readonly IReadOnlyDictionary<string, string>
        ToolDescriptions = new Dictionary<string, string>(
            StringComparer.Ordinal
        )
        {
            ["get_capability_context"] =
                "查询动作、条件、意图、实体字段、配表字段和枚举的只读能力切片。",
            ["resolve_asset"] =
                "按 namespace + id 校验资产，或按名称返回全部候选，不自动选择同名项。",
            ["get_graph"] =
                "从行为根返回有界上下游图、字段和边证据。",
            ["search_similar_skills"] =
                "按动作、关系类型和角色字段确定性地检索相似技能。",
            ["explain_execution_chain"] =
                "返回带边级证据的执行链，不补造未观察到的步骤。"
        };

    public IReadOnlyList<AgentToolDefinition> Definitions { get; } =
        LoadDefinitions(environment);

    public async Task<IReadOnlyList<AgentToolExecution>> ExecuteAsync(
        IReadOnlyList<AgentToolCall> calls,
        CancellationToken cancellationToken
    )
    {
        var results = new List<AgentToolExecution>();
        foreach (AgentToolCall call in calls.Take(3))
        {
            try
            {
                JsonNode result = call.Name switch
                {
                    "get_capability_context" => await GetCapabilityContextAsync(
                        call.Arguments,
                        cancellationToken
                    ),
                    "resolve_asset" => await ResolveAssetAsync(
                        call.Arguments,
                        cancellationToken
                    ),
                    "get_graph" => await GetGraphAsync(
                        call.Arguments,
                        cancellationToken
                    ),
                    "search_similar_skills" => await SearchSimilarSkillsAsync(
                        call.Arguments,
                        cancellationToken
                    ),
                    "explain_execution_chain" =>
                        await ExplainExecutionChainAsync(
                            call.Arguments,
                            cancellationToken
                        ),
                    _ => new JsonObject
                    {
                        ["status"] = "InvalidRequest",
                        ["error"] = $"Unknown read-only tool '{call.Name}'."
                    }
                };
                results.Add(
                    new AgentToolExecution(
                        call.Name,
                        call.Arguments?.ToJsonString() ?? "{}",
                        result.ToJsonString(),
                        false
                    )
                );
            }
            catch (Exception exception) when (
                exception is ArgumentException
                    or KeyNotFoundException
                    or InvalidOperationException
                    or FileNotFoundException
            )
            {
                results.Add(
                    new AgentToolExecution(
                        call.Name,
                        call.Arguments?.ToJsonString() ?? "{}",
                        JsonSerializer.Serialize(
                            new
                            {
                                status = "Error",
                                error = exception.Message
                            }
                        ),
                        true
                    )
                );
            }
        }

        return results;
    }

    private async Task<JsonNode> ResolveAssetAsync(
        JsonNode? arguments,
        CancellationToken cancellationToken
    )
    {
        JsonObject args = RequireObject(arguments);
        string namespaceName = RequireString(args, "namespace");
        int? id = OptionalInt(args, "id");
        string? name = OptionalString(args, "name");
        int limit = Clamp(OptionalInt(args, "limit") ?? 50, 1, 500);
        if (id is null && string.IsNullOrWhiteSpace(name))
        {
            return new JsonObject
            {
                ["status"] = "InvalidRequest",
                ["error"] = "id or name is required."
            };
        }
        if (id is not null && !string.IsNullOrWhiteSpace(name))
        {
            return new JsonObject
            {
                ["status"] = "InvalidRequest",
                ["error"] = "id and name cannot be used together."
            };
        }

        string normalized = SkillWorkspaceService.NormalizeAssetNamespace(
            namespaceName
        );
        if (id is not null)
        {
            SkillChainSnapshot chain = await workspace.GetAssetChainAsync(
                new StudioAssetRef(normalized, id.Value),
                depth: 1,
                cancellationToken
            );
            return new JsonObject
            {
                ["status"] = "Ready",
                ["asset"] = JsonSerializer.SerializeToNode(
                    new { @namespace = normalized, id = id.Value }
                )
            };
        }

        IReadOnlyList<AssetSearchResult> candidates =
            await workspace.SearchAssetsAsync(name, limit, cancellationToken);
        var filtered = candidates
            .Where(
                candidate => string.Equals(
                    candidate.Ref.Namespace,
                    normalized,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ToArray();
        return new JsonObject
        {
            ["status"] = filtered.Length == 0 ? "NotFound" : "Candidates",
            ["total"] = filtered.Length,
            ["returned"] = filtered.Length,
            ["truncated"] = candidates.Count > filtered.Length,
            ["candidates"] = JsonSerializer.SerializeToNode(
                filtered.Select(
                    candidate => new
                    {
                        key = $"{candidate.Ref.Namespace}:{candidate.Ref.Id}",
                        candidate.Label,
                        candidate.Summary,
                        candidate.Kind,
                        candidate.SourceRow
                    }
                )
            )
        };
    }

    private static SkillChainNode FilterAgentVisibleFields(
        SkillChainNode node
    )
    {
        if (
            !node.Fields.Keys.Any(
                SkillAgentInstructions.IsHiddenLegacyField
            )
        )
        {
            return node;
        }

        return node with
        {
            Fields = node.Fields
                .Where(
                    pair =>
                        !SkillAgentInstructions.IsHiddenLegacyField(
                            pair.Key
                        )
                )
                .ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal
                )
        };
    }

    private async Task<JsonNode> GetGraphAsync(
        JsonNode? arguments,
        CancellationToken cancellationToken
    )
    {
        JsonObject args = RequireObject(arguments);
        StudioAssetRef root = ParseAssetRef(RequireString(args, "root"));
        int depth = Clamp(OptionalInt(args, "depth") ?? 32, 0, 32);
        string direction = (
            OptionalString(args, "direction") ?? "out"
        ).Trim().ToLowerInvariant();
        if (direction is not ("out" or "in" or "both"))
        {
            throw new ArgumentException(
                "direction 只能是 out、in 或 both。"
            );
        }
        int fallbackLimit = Clamp(
            OptionalInt(args, "limit") ?? 200,
            1,
            500
        );
        int nodeLimit = Clamp(
            OptionalInt(args, "nodeLimit") ?? fallbackLimit,
            1,
            500
        );
        int edgeLimit = Clamp(
            OptionalInt(args, "edgeLimit") ?? fallbackLimit,
            1,
            500
        );
        SkillChainSnapshot chain = await workspace.GetAssetChainAsync(
            root,
            depth,
            direction,
            cancellationToken
        );
        SkillChainNode[] nodes = chain
            .Nodes.Take(nodeLimit)
            .Select(FilterAgentVisibleFields)
            .ToArray();
        JsonObject registry = await _registry.Value;
        IReadOnlyDictionary<string, SkillChainNode> nodesByKey = chain
            .Nodes.ToDictionary(node => node.Key, StringComparer.Ordinal);
        var nodePayloads = new JsonArray();
        foreach (SkillChainNode node in nodes)
        {
            JsonObject payload = JsonSerializer
                .SerializeToNode(node)!
                .AsObject();
            JsonObject? parameterDetails =
                EffectActionParameterProjector.Project(
                    registry,
                    node,
                    chain.Edges,
                    nodesByKey
                );
            if (parameterDetails is not null)
            {
                payload["actionParameterDetails"] = parameterDetails;
            }
            nodePayloads.Add(payload);
        }
        var nodeKeys = nodes
            .Select(node => node.Key)
            .ToHashSet(StringComparer.Ordinal);
        SkillChainEdge[] edges = chain
            .Edges.Where(
                edge =>
                    nodeKeys.Contains(edge.Source)
                    && nodeKeys.Contains(edge.Target)
            )
            .Take(edgeLimit)
            .ToArray();
        bool truncated =
            nodes.Length < chain.Nodes.Count
            || edges.Length < chain.Edges.Count;
        return JsonSerializer.SerializeToNode(
            new
            {
                status = truncated ? "Truncated" : "Ready",
                snapshot = new
                {
                    revision = chain.Revision,
                    rootKey = chain.RootKey
                },
                depth,
                direction,
                limits = new
                {
                    maxNodes = nodeLimit,
                    maxEdges = edgeLimit
                },
                totals = new
                {
                    nodes = chain.Nodes.Count,
                    edges = chain.Edges.Count
                },
                truncated,
                nodes = nodePayloads,
                edges
            }
        )!;
    }

    private async Task<JsonNode> ExplainExecutionChainAsync(
        JsonNode? arguments,
        CancellationToken cancellationToken
    )
    {
        JsonObject args = RequireObject(arguments);
        StudioAssetRef root = ParseAssetRef(RequireString(args, "skill"));
        int limit = Clamp(OptionalInt(args, "limit") ?? 200, 1, 500);
        SkillChainSnapshot chain = await workspace.GetExecutionChainAsync(
            root,
            depth: 32,
            cancellationToken
        );
        Dictionary<string, SkillChainNode> nodesByKey = chain
            .Nodes.ToDictionary(
                node => node.Key,
                StringComparer.Ordinal
            );
        if (!nodesByKey.TryGetValue(chain.RootKey, out SkillChainNode? focus))
        {
            throw new KeyNotFoundException(
                $"执行链中不存在根节点 {chain.RootKey}。"
            );
        }
        Dictionary<string, SkillChainEdge[]> outgoing = chain
            .Edges.GroupBy(edge => edge.Source, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.Ordinal
            );
        var includedNodes = new HashSet<string>(
            StringComparer.Ordinal
        )
        {
            chain.RootKey
        };
        var includedEdges = new List<SkillChainEdge>();
        var emittedEdges = new HashSet<string>(StringComparer.Ordinal);
        var depths = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [chain.RootKey] = 0
        };
        var queue = new Queue<(string Key, int Depth)>();
        queue.Enqueue((chain.RootKey, 0));
        bool truncated = false;

        while (queue.Count > 0)
        {
            (string key, int depth) = queue.Dequeue();
            foreach (
                SkillChainEdge edge in outgoing.GetValueOrDefault(key, [])
            )
            {
                if (!emittedEdges.Add(edge.Id))
                {
                    continue;
                }
                if (includedEdges.Count >= limit)
                {
                    truncated = true;
                    break;
                }
                if (!includedNodes.Contains(edge.Target))
                {
                    if (includedNodes.Count >= limit)
                    {
                        truncated = true;
                        continue;
                    }
                    includedNodes.Add(edge.Target);
                    depths[edge.Target] = depth + 1;
                    queue.Enqueue((edge.Target, depth + 1));
                }
                includedEdges.Add(edge);
            }
        }

        SkillChainNode[] nodes = chain
            .Nodes.Where(node => includedNodes.Contains(node.Key))
            .ToArray();
        object[] steps = includedEdges
            .Select(
                (edge, index) =>
                {
                    nodesByKey.TryGetValue(edge.Target, out var target);
                    return (object)new
                    {
                        sequence = index,
                        edgeSequence = index,
                        depth = depths.GetValueOrDefault(edge.Source, 0),
                        from = edge.Source,
                        field = edge.SourceField ?? edge.Role,
                        parameterIndex = edge.ParameterIndex,
                        to = edge.Target,
                        toKind = target?.Kind ?? "",
                        toLabel = target?.Label ?? edge.Target,
                        reusedSubtree = false,
                        evidence = new
                        {
                            kind = edge.Derived
                                ? "RuntimeBinding"
                                : "ExistingConfig",
                            @ref = edge.Id,
                            note = edge.Detail
                        }
                    };
                }
            )
            .ToArray();
        int maxDepth = steps.Length == 0
            ? 0
            : includedEdges.Max(
                edge => depths.GetValueOrDefault(edge.Source, 0)
            );
        return new JsonObject
        {
            ["status"] = truncated ? "Partial" : "Ready",
            ["snapshot"] = new JsonObject
            {
                ["revision"] = chain.Revision,
                ["rootKey"] = chain.RootKey
            },
            ["skill"] = JsonSerializer.SerializeToNode(focus),
            ["steps"] = JsonSerializer.SerializeToNode(steps),
            ["nodes"] = JsonSerializer.SerializeToNode(nodes),
            ["summary"] = new JsonObject
            {
                ["stepCount"] = steps.Length,
                ["maxDepth"] = maxDepth,
                ["reusedSubtreeCount"] = 0,
                ["unresolvedTargetCount"] = 0,
                ["cycleDetected"] = false
            },
            ["unresolvedTargets"] = new JsonArray()
        };
    }

    private async Task<JsonNode> SearchSimilarSkillsAsync(
        JsonNode? arguments,
        CancellationToken cancellationToken
    )
    {
        JsonObject args = RequireObject(arguments);
        StudioAssetRef focus = ParseAssetRef(RequireString(args, "focus"));
        int limit = Clamp(OptionalInt(args, "limit") ?? 5, 1, 500);
        SkillChainSnapshot focusChain = await workspace.GetAssetChainAsync(
            focus,
            depth: 12,
            cancellationToken
        );
        HashSet<string> focusFeatures = Features(focusChain);
        IReadOnlyList<SkillSummary> skills = await workspace.ListSkillsAsync(
            null,
            500,
            cancellationToken
        );

        var candidates = new List<object>();
        foreach (SkillSummary skill in skills)
        {
            if (
                string.Equals(
                    focus.Namespace,
                    "TbSkill",
                    StringComparison.Ordinal
                )
                && skill.Id == focus.Id
            )
            {
                continue;
            }

            SkillChainSnapshot chain = await workspace.GetAssetChainAsync(
                new StudioAssetRef("TbSkill", skill.Id),
                depth: 12,
                cancellationToken
            );
            HashSet<string> candidateFeatures = Features(chain);
            int overlap = focusFeatures.Intersect(candidateFeatures).Count();
            if (overlap == 0)
            {
                continue;
            }

            candidates.Add(
                new
                {
                    key = $"TbSkill:{skill.Id}",
                    skill.Label,
                    skill.Summary,
                    score = Math.Round(
                        overlap /
                        (double)Math.Max(
                            1,
                            focusFeatures.Union(candidateFeatures).Count()
                        ),
                        4
                    ),
                    matchedFeatures = focusFeatures
                        .Intersect(candidateFeatures)
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .Take(12)
                        .ToArray()
                }
            );
        }

        object[] ranked = candidates
            .OrderByDescending(
                candidate =>
                    (double)candidate.GetType().GetProperty("score")!.GetValue(
                        candidate
                    )!
            )
            .Take(limit)
            .ToArray();
        return JsonSerializer.SerializeToNode(
            new
            {
                status = ranked.Length == 0 ? "NotFound" : "Ready",
                focus = $"{focus.Namespace}:{focus.Id}",
                total = candidates.Count,
                returned = ranked.Length,
                candidates = ranked
            }
        )!;
    }

    private async Task<JsonNode> GetCapabilityContextAsync(
        JsonNode? arguments,
        CancellationToken cancellationToken
    )
    {
        JsonObject args = arguments as JsonObject ?? new JsonObject();
        string? query = OptionalString(args, "query");
        string? intent = OptionalString(args, "intent");
        string? actionKey = OptionalString(args, "actionKey");
        string? enumName = OptionalString(args, "enum");
        int limit = Clamp(OptionalInt(args, "limit") ?? 20, 1, 500);
        int enumValueLimit = Clamp(
            OptionalInt(args, "enumValueLimit") ?? 50,
            1,
            500
        );
        JsonObject registry = await _registry.Value;
        JsonArray effects = registry["effects"]?.AsArray() ?? [];
        JsonArray conditions = registry["conditions"]?.AsArray() ?? [];
        JsonArray intents = registry["intents"]?.AsArray() ?? [];
        JsonArray entities = registry["entities"]?.AsArray() ?? [];
        JsonArray entityFields = registry["entityFields"]?.AsArray() ?? [];
        JsonArray enums = registry["enums"]?.AsArray() ?? [];
        if (!string.IsNullOrWhiteSpace(intent) && !intents.OfType<JsonObject>().Any(item => item["key"]?.GetValue<string>() == intent))
            throw new ArgumentException("intent 必须使用已发布的 key：" + string.Join(", ", intents.OfType<JsonObject>().Select(item => item["key"]?.GetValue<string>())) + "。查询动作应使用 actionKey，查询实体应使用单个实体 key。不要把创建请求正文放入 intent。");

        bool enumScoped = !string.IsNullOrWhiteSpace(enumName);
        JsonArray selectedEffects = enumScoped ? [] : FilterActions(
            effects,
            query,
            actionKey,
            intent
        );
        JsonArray selectedConditions = enumScoped ? [] : FilterActions(
            conditions,
            query,
            actionKey,
            intent
        );
        bool actionScoped =
            !string.IsNullOrWhiteSpace(actionKey)
            || !string.IsNullOrWhiteSpace(intent);
        bool contextScoped = actionScoped || enumScoped;
        JsonArray selectedIntents = contextScoped
            ? []
            : FilterByText(intents, query, ["key", "label"]);
        JsonArray selectedEntities = contextScoped
            ? []
            : FilterByText(
                entities,
                query,
                ["key", "namespace", "role"]
            );
        IReadOnlyList<AssetTableFieldSummary> allTableFields =
            contextScoped
                ? []
                : (
                    await workspace.GetTableFieldsAsync(
                        EntityNamespaceMap(entities),
                        cancellationToken
                    )
                )
                    .Where(
                        field =>
                            !SkillAgentInstructions.IsHiddenLegacyField(
                                field.Key
                            )
                    )
                    .ToArray();
        selectedEntities = MergeEntities(
            selectedEntities,
            entities,
            allTableFields
                .Where(field => TableFieldMatches(field, query))
                .Select(field => field.EntityKey)
        );
        JsonArray selectedFields = contextScoped
            ? []
            : MergeEntityFields(
                FilterByText(
                    entityFields,
                    query,
                    ["path", "enumName"]
                ),
                entityFields,
                selectedEntities
            );
        IReadOnlyList<AssetTableFieldSummary> tableFieldDefinitions =
            contextScoped
                ? []
                : allTableFields
                    .Where(
                        field => selectedEntities
                            .OfType<JsonObject>()
                            .Any(
                                entity => string.Equals(
                                    entity["key"]?.GetValue<string>(),
                                    field.EntityKey,
                                    StringComparison.OrdinalIgnoreCase
                                )
                            )
                    )
                    .ToArray();
        JsonArray nestedTypes = SelectNestedTypes(
            registry["nestedTypes"]?.AsArray() ?? [],
            tableFieldDefinitions
        );
        JsonArray referencedEnumNames = ReferencedEnumNames(
            selectedEffects,
            selectedConditions,
            selectedFields,
            nestedTypes
        );
        JsonArray selectedEnums;
        if (!string.IsNullOrWhiteSpace(enumName))
        {
            selectedEnums = FilterByText(
                enums,
                enumName,
                ["name", "comment"]
            );
        }
        else if (referencedEnumNames.Count > 0)
        {
            selectedEnums = FilterEnumsByName(
                enums,
                referencedEnumNames
            );
        }
        else
        {
            selectedEnums = FilterByText(
                enums,
                query,
                ["name", "comment"]
            );
        }
        selectedEnums = LimitEnumValues(selectedEnums, enumValueLimit);
        Dictionary<string, string> enumNamesByPath = entityFields
            .OfType<JsonObject>()
            .Select(
                field => new
                {
                    Path = field["path"]?.GetValue<string>() ?? "",
                    EnumName = field["enumName"]?.GetValue<string>()
                }
            )
            .Where(
                field =>
                    field.Path.Length > 0
                    && !string.IsNullOrWhiteSpace(field.EnumName)
            )
            .GroupBy(field => field.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().EnumName!,
                StringComparer.OrdinalIgnoreCase
            );
        Dictionary<string, JsonObject> fieldSemanticsByPath =
            (registry["fieldSemantics"]?.AsArray() ?? [])
            .OfType<JsonObject>()
            .Select(
                semantics => new
                {
                    Path = semantics["path"]?.GetValue<string>() ?? "",
                    Semantics = semantics
                }
            )
            .Where(item => item.Path.Length > 0)
            .ToDictionary(
                item => item.Path,
                item => item.Semantics,
                StringComparer.OrdinalIgnoreCase
            );
        JsonArray tableFields = new(
            tableFieldDefinitions
                .Select(
                    field =>
                    {
                        fieldSemanticsByPath.TryGetValue(
                            field.Path,
                            out JsonObject? semantics
                        );
                        return (JsonNode?)new JsonObject
                        {
                            ["entityKey"] = field.EntityKey,
                            ["namespace"] = field.Namespace,
                            ["tableKey"] = field.TableKey,
                            ["key"] = field.Key,
                            ["label"] = field.Label,
                            ["path"] = field.Path,
                            ["kind"] = field.Kind,
                            ["rawType"] = field.RawType,
                            ["required"] = field.Required,
                            ["referenceTarget"] = field.ReferenceTarget,
                            ["enumName"] =
                                enumNamesByPath.GetValueOrDefault(
                                    field.Path
                                ),
                            ["elementType"] =
                                semantics?["elementType"]?.GetValue<string>(),
                            ["description"] =
                                semantics?["description"]?.GetValue<string>(),
                            ["indexRoles"] =
                                semantics?["indexRoles"]?.DeepClone()
                                ?? new JsonArray(),
                            ["options"] = JsonSerializer.SerializeToNode(
                                field.Options.Take(enumValueLimit).ToArray()
                            )
                        };
                    }
                )
                .ToArray()
        );

        bool directoryRequest = string.IsNullOrWhiteSpace(query) && !actionScoped && string.IsNullOrWhiteSpace(enumName);
        JsonObject? creationSlice = null;
        if (!actionScoped && string.IsNullOrWhiteSpace(enumName))
        {
            creationSlice = (await _creation.Value).DeepClone().AsObject();
            if (!directoryRequest)
                creationSlice["entities"] = new JsonArray(creationSlice["entities"]!.AsArray().OfType<JsonObject>()
                    .Where(entity => string.Equals(query, entity["entityKey"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase)
                        || string.Equals(query, entity["namespace"]?.GetValue<string>(), StringComparison.OrdinalIgnoreCase))
                    .Select(entity => (JsonNode?)entity.DeepClone()).ToArray());
            creationSlice["nestedTypes"] = registry["nestedTypes"]?.DeepClone();
        }

        return new JsonObject
        {
            ["status"] =
                selectedEffects.Count
                + selectedConditions.Count
                + selectedIntents.Count
                + selectedEnums.Count
                + selectedEntities.Count
                + selectedFields.Count
                == 0
                    ? "NotFound"
                    : "Ready",
            ["counts"] = new JsonObject
            {
                ["effects"] = Counts(effects.Count, selectedEffects.Count, limit),
                ["conditions"] = Counts(
                    conditions.Count,
                    selectedConditions.Count,
                    limit
                ),
                ["intents"] = Counts(intents.Count, selectedIntents.Count, limit),
                ["entities"] = Counts(entities.Count, selectedEntities.Count, limit),
                ["entityFields"] = Counts(
                    entityFields.Count,
                    selectedFields.Count,
                    limit
                ),
                ["tableFields"] = Counts(
                    tableFieldDefinitions.Count,
                    tableFields.Count,
                    limit
                ),
                ["enums"] = Counts(enums.Count, selectedEnums.Count, limit)
            },
            ["effects"] = Take(selectedEffects, limit),
            ["conditions"] = Take(selectedConditions, limit),
            ["intents"] = Take(selectedIntents, limit),
            ["entities"] = Take(selectedEntities, limit),
            ["entityFields"] = Take(selectedFields, limit),
            ["creationContract"] = creationSlice,
            ["editingContract"] = (await _creation.Value)["editing"]?.DeepClone(),
            ["planOperations"] = registry["planOperations"]?.DeepClone(),
            ["mechanismContract"] = directoryRequest ? (await _mechanisms.Value).DeepClone() : null,
            ["actionDirectory"] = directoryRequest ? new JsonArray(effects.OfType<JsonObject>().Concat(conditions.OfType<JsonObject>()).Take(128)
                .Select(action => (JsonNode?)new JsonObject { ["key"] = action["key"]?.DeepClone(), ["label"] = action["label"]?.DeepClone(), ["semantic"] = action["semantic"]?.DeepClone() }).ToArray()) : null,
            ["tableFields"] = Take(tableFields, limit),
            ["nestedTypes"] = Take(nestedTypes, limit),
            ["enums"] = Take(selectedEnums, limit)
        };
    }

    private static JsonArray FilterActions(
        JsonArray actions,
        string? query,
        string? actionKey,
        string? intent
    )
    {
        return new JsonArray(
            actions
                .Where(node => node is JsonObject action)
                .Cast<JsonObject>()
                .Where(
                    action =>
                        (
                            string.IsNullOrWhiteSpace(actionKey)
                            || string.Equals(
                                action["key"]?.GetValue<string>(),
                                actionKey,
                                StringComparison.OrdinalIgnoreCase
                            )
                        )
                        && (
                            string.IsNullOrWhiteSpace(intent)
                            || action["semantic"]?["intents"] is JsonArray intents
                            && intents.Any(
                                value => string.Equals(
                                    value?.GetValue<string>(),
                                    intent,
                                    StringComparison.OrdinalIgnoreCase
                                )
                            )
                        )
                        && (
                            string.IsNullOrWhiteSpace(query)
                            || new[]
                            {
                                action["key"]?.GetValue<string>(),
                                action["label"]?.GetValue<string>(),
                                action["description"]?.GetValue<string>()
                            }
                                .Where(value => !string.IsNullOrWhiteSpace(value))
                                .Any(
                                    value => value!.Contains(
                                        query,
                                        StringComparison.OrdinalIgnoreCase
                                    )
                                )
                            || (
                                action["semantic"]?["intents"] is JsonArray actionIntents
                                && actionIntents.Any(
                                    value => value?.GetValue<string>()?.Contains(
                                        query,
                                        StringComparison.OrdinalIgnoreCase
                                    ) == true
                                )
                            )
                        )
                )
                .Select(action => action.DeepClone())
                .ToArray()
        );
    }

    private static JsonArray FilterByText(
        JsonArray values,
        string? query,
        IReadOnlyList<string> properties
    )
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return new JsonArray(
                values.Select(value => value?.DeepClone()).ToArray()
            );
        }

        return new JsonArray(
            values
                .Where(value => value is JsonObject item)
                .Cast<JsonObject>()
                .Where(
                    item => properties.Any(
                        property => item[property]
                            ?.GetValue<string>() is { } value
                            && TextMatches(value, query)
                    )
                )
                .Select(item => item.DeepClone())
                .ToArray()
        );
    }

    private static JsonArray MergeEntityFields(
        JsonArray matchedFields,
        JsonArray allFields,
        JsonArray selectedEntities
    )
    {
        string[] entityKeys = selectedEntities
            .OfType<JsonObject>()
            .Select(entity => entity["key"]?.GetValue<string>() ?? "")
            .Where(key => key.Length > 0)
            .ToArray();
        var matchedPaths = matchedFields
            .OfType<JsonObject>()
            .Select(field => field["path"]?.GetValue<string>() ?? "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new JsonArray(
            allFields
                .OfType<JsonObject>()
                .Where(
                    field =>
                    {
                        string path = field["path"]?.GetValue<string>() ?? "";
                        return matchedPaths.Contains(path)
                            || entityKeys.Any(
                                key => path.StartsWith(
                                    $"{key}.",
                                    StringComparison.OrdinalIgnoreCase
                                )
                            );
                    }
                )
                .GroupBy(
                    field => field["path"]?.GetValue<string>() ?? "",
                    StringComparer.OrdinalIgnoreCase
                )
                .Select(group => group.First().DeepClone())
                .ToArray()
        );
    }

    private static IReadOnlyDictionary<string, string> EntityNamespaceMap(
        JsonArray entities
    )
    {
        return entities
            .OfType<JsonObject>()
            .Select(
                entity => new
                {
                    Key = entity["key"]?.GetValue<string>() ?? "",
                    Namespace =
                        entity["namespace"]?.GetValue<string>() ?? ""
                }
            )
            .Where(
                entity =>
                    entity.Key.Length > 0 && entity.Namespace.Length > 0
            )
            .GroupBy(entity => entity.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.First().Namespace,
                StringComparer.OrdinalIgnoreCase
            );
    }

    private static JsonArray MergeEntities(
        JsonArray matchedEntities,
        JsonArray allEntities,
        IEnumerable<string> additionalKeys
    )
    {
        var keys = additionalKeys.ToHashSet(
            StringComparer.OrdinalIgnoreCase
        );
        return new JsonArray(
            allEntities
                .OfType<JsonObject>()
                .Where(
                    entity =>
                        keys.Contains(
                            entity["key"]?.GetValue<string>() ?? ""
                        )
                        || matchedEntities
                            .OfType<JsonObject>()
                            .Any(
                                matched => string.Equals(
                                    matched["key"]?.GetValue<string>(),
                                    entity["key"]?.GetValue<string>(),
                                    StringComparison.OrdinalIgnoreCase
                                )
                            )
                )
                .Select(entity => entity.DeepClone())
                .ToArray()
        );
    }

    private static bool TableFieldMatches(
        AssetTableFieldSummary field,
        string? query
    )
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return false;
        }

        return new[] { field.Key, field.Label, field.Path }.Any(
            value => TextMatches(value, query)
        );
    }

    private static bool TextMatches(string value, string query)
    {
        if (
            value.Contains(query, StringComparison.OrdinalIgnoreCase)
        )
        {
            return true;
        }

        return QueryIdentifierRegex
            .Matches(query)
            .Select(match => match.Value)
            .Any(
                token => value.Contains(
                    token,
                    StringComparison.OrdinalIgnoreCase
                )
            );
    }

    private static JsonArray SelectNestedTypes(
        JsonArray definitions,
        IReadOnlyList<AssetTableFieldSummary> tableFields
    )
    {
        string[] rawTypes = tableFields
            .Select(field => field.RawType)
            .Where(rawType => !string.IsNullOrWhiteSpace(rawType))
            .ToArray();
        return new JsonArray(
            definitions
                .OfType<JsonObject>()
                .Where(
                    definition =>
                    {
                        string key =
                            definition["key"]?.GetValue<string>() ?? "";
                        return key.Length > 0
                            && rawTypes.Any(
                                rawType => rawType.Contains(
                                    key,
                                    StringComparison.OrdinalIgnoreCase
                                )
                            );
                    }
                )
                .Select(definition => definition.DeepClone())
                .ToArray()
        );
    }

    private static JsonArray ReferencedEnumNames(
        params JsonArray[] actions
    )
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (
            JsonObject action in actions
                .SelectMany(action => action.OfType<JsonObject>())
        )
        {
            string? directEnumName = action["enumName"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(directEnumName))
            {
                names.Add(directEnumName);
            }

            if (action["parameters"] is not JsonArray parameters)
            {
                parameters = [];
            }

            foreach (JsonObject? parameter in parameters.OfType<JsonObject>())
            {
                string? name = parameter["enumName"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }

            if (action["fields"] is not JsonArray fields)
            {
                continue;
            }

            foreach (JsonObject? field in fields.OfType<JsonObject>())
            {
                string? name = field["enumName"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    names.Add(name);
                }
            }
        }

        return new JsonArray(
            names.OrderBy(name => name, StringComparer.Ordinal)
                .Select(name => JsonValue.Create(name))
                .ToArray()
        );
    }

    private static JsonArray FilterEnumsByName(
        JsonArray enums,
        JsonArray names
    )
    {
        var selected = names
            .Select(value => value?.GetValue<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new JsonArray(
            enums
                .Where(value => value is JsonObject item)
                .Cast<JsonObject>()
                .Where(
                    item => selected.Contains(
                        item["name"]?.GetValue<string>() ?? ""
                    )
                )
                .Select(item => item.DeepClone())
                .ToArray()
        );
    }

    private static JsonArray LimitEnumValues(
        JsonArray enums,
        int enumValueLimit
    )
    {
        var result = new JsonArray();
        foreach (JsonObject? item in enums.OfType<JsonObject>())
        {
            JsonObject clone = (JsonObject)item.DeepClone();
            if (clone["values"] is JsonArray values)
            {
                clone["totalValueCount"] = values.Count;
                clone["values"] = new JsonArray(
                    values.Take(enumValueLimit)
                        .Select(value => value?.DeepClone())
                        .ToArray()
                );
                clone["returnedValueCount"] = Math.Min(
                    enumValueLimit,
                    values.Count
                );
                clone["truncated"] = values.Count > enumValueLimit;
            }
            result.Add(clone);
        }
        return result;
    }

    private static JsonObject Counts(int total, int count, int limit)
    {
        return new JsonObject
        {
            ["total"] = total,
            ["returned"] = Math.Min(count, limit),
            ["truncated"] = count > limit
        };
    }

    private static JsonArray Take(JsonArray values, int limit)
    {
        return new JsonArray(
            values.Take(limit).Select(value => value?.DeepClone()).ToArray()
        );
    }

    private static HashSet<string> Features(SkillChainSnapshot chain)
    {
        var features = new HashSet<string>(StringComparer.Ordinal);
        foreach (SkillChainNode node in chain.Nodes)
        {
            features.Add(node.Namespace);
            features.Add(node.Kind);
            if (node.Fields.TryGetValue("__action", out var action))
            {
                foreach (string value in action)
                {
                    features.Add($"action:{value}");
                }
            }
        }
        foreach (SkillChainEdge edge in chain.Edges)
        {
            features.Add($"role:{edge.Role}");
            if (!string.IsNullOrWhiteSpace(edge.SourceField))
            {
                features.Add($"field:{edge.SourceField}");
            }
        }
        return features;
    }

    private static JsonObject RequireObject(JsonNode? value)
    {
        return value as JsonObject
            ?? throw new ArgumentException("Tool arguments must be an object.");
    }

    private static string RequireString(JsonObject value, string property)
    {
        string? result = OptionalString(value, property);
        return string.IsNullOrWhiteSpace(result)
            ? throw new ArgumentException($"{property} is required.")
            : result;
    }

    private static string? OptionalString(JsonObject value, string property)
    {
        return value[property]?.GetValue<string>();
    }

    private static int? OptionalInt(JsonObject value, string property)
    {
        return value[property]?.GetValue<int>();
    }

    private static StudioAssetRef ParseAssetRef(string value)
    {
        int separator = value.LastIndexOf(':');
        if (
            separator <= 0
            || !int.TryParse(value[(separator + 1)..], out int id)
        )
        {
            throw new ArgumentException(
                $"Asset reference '{value}' must use Namespace:id."
            );
        }

        return new StudioAssetRef(
            SkillWorkspaceService.NormalizeAssetNamespace(value[..separator]),
            id
        );
    }

    private static int Clamp(int value, int minimum, int maximum) =>
        Math.Min(maximum, Math.Max(minimum, value));

    private static IReadOnlyList<AgentToolDefinition> LoadDefinitions(
        IHostEnvironment environment
    )
    {
        string path = ContractPath(
            environment,
            "config",
            "read-only-tools.v0.json"
        );
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllText(path)
        );
        var definitions = new List<AgentToolDefinition>();
        foreach (
            JsonElement tool in document.RootElement
                .GetProperty("tools")
                .EnumerateArray()
        )
        {
            string name = tool.GetProperty("name").GetString() ?? "";
            string description = ToolDescriptions.GetValueOrDefault(
                name,
                tool.GetProperty("purpose").GetString() ?? name
            );
            definitions.Add(
                new AgentToolDefinition(
                    name,
                    description,
                    tool.GetProperty("inputSchema").GetRawText()
                )
            );
        }
        return definitions;
    }

    private static async Task<JsonObject> LoadRegistryAsync(
        IHostEnvironment environment
    )
    {
        string path = ContractPath(
            environment,
            "config",
            "capability-registry.v0.json"
        );
        string json = await File.ReadAllTextAsync(path);
        return JsonNode.Parse(json)!.AsObject();
    }

    private static string ContractPath(
        IHostEnvironment environment,
        params string[] relativeParts
    )
    {
        string repoRoot = Path.GetFullPath(
            Path.Combine(environment.ContentRootPath, "..", "..")
        );
        var parts = new List<string>
        {
            repoRoot,
            StudioInfo.ContractRoot
        };
        parts.AddRange(relativeParts);
        return Path.Combine(parts.ToArray());
    }
}
