using RtsSkillStudio.Agent;
using RtsSkillStudio.Agent.Llm;
using RtsSkillStudio.Agent.Patch;
using RtsSkillStudio.Agent.Workspaces;
using System.Text.RegularExpressions;

namespace RtsSkillStudio.Api.Workspaces;

public sealed class AgentToolLoop(
    LlmProviderFactory providers,
    IAgentReadOnlyToolService tools,
    SkillConfigPlanValidator planValidator,
    SkillConfigPlanNormalizer planNormalizer,
    SkillWorkspaceService workspace,
    ILogger<AgentToolLoop> logger,
    WorkbookPatchWorkspaceService? patches = null
)
{
    private const int MaxRounds = 4;
    private static readonly Regex ParameterSemanticsRegex = new(
        @"(?i)(?:action_param|\bparameters?\b|\bparams?\b|参数(?:槽|索引)?).{0,40}(?:代表什么|什么意思|什么含义|含义是什么|定义是什么|清楚吗|是什么意思)|(?:代表什么|什么意思|什么含义|你清楚吗|清楚吗|难道不是)",
        RegexOptions.Compiled
    );

    public async Task<AgentTurnOutcome> RunAsync(
        string? providerName,
        string? model,
        string? reasoningEffort,
        string message,
        IReadOnlyList<LlmChatMessage> history,
        string bootstrapContext,
        AgentRoute route,
        StudioAssetRef? selectedAsset,
        IReadOnlyList<StudioAssetRef> mentionedAssets,
        CancellationToken cancellationToken
    )
    {
        ILlmProvider provider = providers.GetProvider(providerName);
        bool parameterSemanticsQuestion =
            ParameterSemanticsRegex.IsMatch(message)
            && !AgentIntentRouter.IsCapabilityQuestion(message);
        var baseHistory = history.ToList();
        if (
            baseHistory.Count > 0
            && string.Equals(
                baseHistory[^1].Role,
                "user",
                StringComparison.OrdinalIgnoreCase
            )
            && string.Equals(
                baseHistory[^1].Content,
                message,
                StringComparison.Ordinal
            )
        )
        {
            baseHistory.RemoveAt(baseHistory.Count - 1);
        }
        var executions = new List<AgentToolExecution>();
        string currentMessage = message;
        long latencyMs = 0;
        LlmCompletionResult? finalResult = null;
        var continuedAnswerParts = new List<string>();
        int continuationCount = 0;
        bool userRecorded = false;
        AgentAssetIdentity? authoritativeIdentity = null;

        if (
            route.Kind == AgentIntentKind.Query
            && AgentIntentRouter.IsCapabilityQuestion(message)
        )
        {
            IReadOnlyList<AgentToolExecution> capabilityTools =
                await tools.ExecuteAsync(
                    [
                        new AgentToolCall(
                            "get_capability_context",
                            new System.Text.Json.Nodes.JsonObject
                            {
                                ["query"] = message,
                                ["limit"] = 20,
                                ["enumValueLimit"] = 500
                            }
                        )
                    ],
                    cancellationToken
                );
            executions.AddRange(capabilityTools);
            currentMessage =
                message
                + Environment.NewLine
                + AgentToolProtocol.FormatToolResults(capabilityTools);
        }

        if (selectedAsset is not null && route.Kind != AgentIntentKind.Unsupported)
        {
            IReadOnlyList<AgentToolExecution> bootstrapTools =
                await tools.ExecuteAsync(
                    [
                        new AgentToolCall(
                            "get_graph",
                            new System.Text.Json.Nodes.JsonObject
                            {
                                ["root"] =
                                    $"{selectedAsset.Namespace}:{selectedAsset.Id}",
                                ["depth"] = 8,
                                ["nodeLimit"] = 64,
                                ["edgeLimit"] = 128
                            }
                        )
                    ],
                    cancellationToken
                );
            executions.AddRange(bootstrapTools);
            authoritativeIdentity =
                AgentToolProtocol.FindAuthoritativeIdentity(bootstrapTools);
            logger.LogInformation(
                "Agent authoritative identity for {Asset}: {Identity}.",
                $"{selectedAsset.Namespace}:{selectedAsset.Id}",
                authoritativeIdentity is null
                    ? "<not found>"
                    : $"{authoritativeIdentity.Key} / {authoritativeIdentity.CanonicalName}"
            );
            currentMessage =
                AgentToolProtocol.BuildIdentityInstructions(
                    authoritativeIdentity
                )
                + Environment.NewLine
                + Environment.NewLine
                + "【用户请求】"
                + Environment.NewLine
                + currentMessage
                + Environment.NewLine
                + AgentToolProtocol.FormatToolResults(bootstrapTools);

            IReadOnlyList<string> actionKeys =
                AgentToolProtocol.FindExecutorActionKeys(bootstrapTools);
            if (actionKeys.Count > 0)
            {
                IReadOnlyList<AgentToolExecution> parameterContracts =
                    await tools.ExecuteAsync(
                        actionKeys
                            .Select(
                                actionKey => new AgentToolCall(
                                    "get_capability_context",
                                    new System.Text.Json.Nodes.JsonObject
                                    {
                                        ["actionKey"] = actionKey,
                                        ["limit"] = 10,
                                        ["enumValueLimit"] = 200
                                    }
                                )
                            )
                            .ToArray(),
                        cancellationToken
                    );
                executions.AddRange(parameterContracts);
                currentMessage =
                    currentMessage
                    + Environment.NewLine
                    + AgentToolProtocol.FormatToolResults(
                        parameterContracts
                    );
            }

            DamageChangeSummary? damageSummary =
                DamageChangeSummaryBuilder.TryBuild(message, executions);
            if (damageSummary is not null)
            {
                IReadOnlyList<SkillPlanClarification> summaryClarifications =
                    damageSummary.ConfirmationQuestion is null
                        ? []
                        :
                        [
                            new SkillPlanClarification(
                                "fixed-damage-mode",
                                damageSummary.ConfirmationQuestion,
                                "TbEffect.action_param[3]"
                            )
                        ];
                return new AgentTurnOutcome(
                    route.Kind,
                    summaryClarifications.Count > 0
                        ? "NeedsClarification"
                        : "Ready",
                    route.ExpectsPlan,
                    provider.Descriptor.Name,
                    model ?? provider.Descriptor.Model,
                    damageSummary.Text,
                    0,
                    null,
                    [],
                    summaryClarifications.Count > 0
                        ? "NeedsClarification"
                        : "None",
                    summaryClarifications,
                    [],
                    executions,
                    selectedAsset,
                    mentionedAssets
                );
            }
        }

        if (route.Kind == AgentIntentKind.Create)
        {
            IReadOnlyList<AgentToolExecution> creationContext = await tools.ExecuteAsync(
                [new AgentToolCall("get_capability_context", new System.Text.Json.Nodes.JsonObject { ["limit"] = 1, ["enumValueLimit"] = 1 })], cancellationToken);
            executions.AddRange(creationContext);
            var evidenceCalls = new List<AgentToolCall>();
            foreach (AgentToolExecution context in creationContext.Where(item => !item.IsError))
            {
                var data = System.Text.Json.Nodes.JsonNode.Parse(context.ResultJson);
                var primitives = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    { "array", "map", "int", "int32", "int64", "string", "bool", "float", "double", "long", "decimal" };
                var typeNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entity in (data?["creationContract"]?["entities"] as System.Text.Json.Nodes.JsonArray ?? []))
                    foreach (var field in (entity?["fields"] as System.Text.Json.Nodes.JsonArray ?? []))
                        foreach (string token in (field?["rawType"]?.GetValue<string>() ?? "").Split('#')[0].Split(','))
                            if (!primitives.Contains(token.Trim()) && token.Trim().Length > 0)
                                typeNames.Add(token.Trim());
                foreach (string name in typeNames)
                {
                    evidenceCalls.Add(new("get_capability_context", new System.Text.Json.Nodes.JsonObject { ["enum"] = name, ["enumValueLimit"] = 20 }));
                }
                foreach (var entry in (data?["actionDirectory"] as System.Text.Json.Nodes.JsonArray ?? []))
                {
                    string? key = entry?["key"]?.GetValue<string>();
                    string label = entry?["label"]?.GetValue<string>() ?? "";
                    if (key is not null && (message.Contains(key, StringComparison.OrdinalIgnoreCase)
                        || (label.Length >= 2 && message.Contains(label[^2..], StringComparison.Ordinal))))
                        evidenceCalls.Add(new("get_capability_context", new System.Text.Json.Nodes.JsonObject { ["actionKey"] = key, ["enumValueLimit"] = 20 }));
                }
            }
            var creationEvidence = new List<AgentToolExecution>();
            foreach (AgentToolCall[] batch in evidenceCalls.Take(24).Chunk(3))
                creationEvidence.AddRange(await tools.ExecuteAsync(batch, cancellationToken));
            executions.AddRange(creationEvidence);
            currentMessage += Environment.NewLine + AgentToolProtocol.FormatToolResults(creationContext)
                + Environment.NewLine + AgentToolProtocol.FormatToolResults(creationEvidence)
                + Environment.NewLine + "创建契约和动作目录已经提供。请从目录选择动作 key，再用 actionKey 查询完整参数，用 enum 查询所需枚举。Plan 数值必须是用户语义值，包括嵌套对象中的数值；仅编译器负责按 scale 编码，模型不得预乘 scale。不要查询尚未分配的新资产 ID。";
        }

        for (int round = 0; round < MaxRounds; round++)
        {
            string instructions = AgentToolProtocol.BuildIdentityInstructions(
                authoritativeIdentity
            )
                + Environment.NewLine
                + Environment.NewLine
                + SkillAgentInstructions.Build(
                    null,
                    bootstrapContext
                )
                + Environment.NewLine
                + Environment.NewLine
                + AgentToolProtocol.BuildInstructions(tools.Definitions)
                + Environment.NewLine
                + $"[intent={route.Kind}; expectsPlan={route.ExpectsPlan}; reason={route.Reason}]";
            LlmCompletionResult result = await provider.CompleteAsync(
                new LlmCompletionRequest(
                    currentMessage,
                    instructions,
                    model,
                    baseHistory,
                    reasoningEffort
                ),
                cancellationToken
            );
            latencyMs += result.LatencyMs;
            IReadOnlyList<AgentToolCall> calls = AgentToolProtocol.Parse(
                result.Text
            );
            if (calls.Count == 0)
            {
                if (IsLengthTruncated(result) && continuationCount < 2)
                {
                    continuedAnswerParts.Add(result.Text);
                    if (!userRecorded)
                    {
                        baseHistory.Add(new LlmChatMessage("user", currentMessage));
                        userRecorded = true;
                    }
                    baseHistory.Add(
                        new LlmChatMessage("assistant", result.Text)
                    );
                    currentMessage =
                        "继续输出上一条回答，从断点继续，不要重复已经输出的内容，也不要重新开始。";
                    continuationCount += 1;
                    finalResult = result;
                    continue;
                }

                if (continuedAnswerParts.Count > 0)
                {
                    result = result with
                    {
                        Text = string.Concat(continuedAnswerParts)
                            + result.Text
                    };
                }
                if (IsLengthTruncated(result))
                {
                    result = result with
                    {
                        Text = result.Text
                            + Environment.NewLine
                            + Environment.NewLine
                            + "（回答因模型输出长度限制被截断。）"
                    };
                }
                finalResult = result;
                break;
            }

            IReadOnlyList<AgentToolExecution> roundExecutions =
                await tools.ExecuteAsync(calls, cancellationToken);
            foreach (AgentToolExecution execution in roundExecutions)
                logger.LogDebug("Agent tool {Name} arguments={Arguments} resultChars={Length}", execution.Name, execution.ArgumentsJson, execution.ResultJson.Length);
            executions.AddRange(roundExecutions);
            // Preserve every tool-result turn. Dropping earlier user/tool messages
            // made the model lose contracts after querying a different asset.
            baseHistory.Add(new LlmChatMessage("user", currentMessage));
            userRecorded = true;
            baseHistory.Add(
                new LlmChatMessage("assistant", result.Text)
            );
            currentMessage =
                AgentToolProtocol.FormatToolResults(roundExecutions)
                + Environment.NewLine
                + "请根据上述 TOOL_RESULTS 继续。若证据足够，给出最终回答；否则继续请求最多 3 个只读工具。";
            finalResult = result;

            if (round == MaxRounds - 1)
            {
                string finalInstructions =
                    AgentToolProtocol.BuildIdentityInstructions(
                        authoritativeIdentity
                    )
                    + Environment.NewLine
                    + Environment.NewLine
                    + SkillAgentInstructions.Build(
                        null,
                        bootstrapContext
                    )
                    + Environment.NewLine
                    + Environment.NewLine
                    + AgentToolProtocol.BuildFinalInstructions()
                    + Environment.NewLine
                    + $"[intent={route.Kind}; expectsPlan={route.ExpectsPlan}; reason={route.Reason}]";
                LlmCompletionResult final = await provider.CompleteAsync(
                    new LlmCompletionRequest(
                        currentMessage,
                        finalInstructions,
                        model,
                        baseHistory,
                        reasoningEffort
                    ),
                    cancellationToken
                );
                latencyMs += final.LatencyMs;
                finalResult = final;
            }
        }

        if (finalResult is null)
        {
            throw new InvalidOperationException("Agent tool loop produced no result.");
        }

        string displayText = SkillConfigPlanParser.RemovePlanBlock(
            AgentToolProtocol.RemoveToolCallBlock(finalResult.Text)
        );
        if (
            authoritativeIdentity is not null
            && route.Kind == AgentIntentKind.Query
            && !AgentToolProtocol.TextContainsAuthoritativeName(
                displayText,
                authoritativeIdentity
            )
        )
        {
            string repairInstructions =
                AgentToolProtocol.BuildIdentityRepairInstructions(
                    authoritativeIdentity
                )
                + Environment.NewLine
                + Environment.NewLine
                + SkillAgentInstructions.Build(
                    null,
                    bootstrapContext
                )
                + Environment.NewLine
                + Environment.NewLine
                + AgentToolProtocol.BuildFinalInstructions();
            string repairMessage =
                AgentToolProtocol.BuildIdentityRepairInstructions(
                    authoritativeIdentity
                )
                + Environment.NewLine
                + Environment.NewLine
                + currentMessage
                + Environment.NewLine
                + Environment.NewLine
                + "【上一版回答】"
                + Environment.NewLine
                + displayText;
            LlmCompletionResult repair = await provider.CompleteAsync(
                new LlmCompletionRequest(
                    repairMessage,
                    repairInstructions,
                    model,
                    baseHistory,
                    reasoningEffort
                ),
                cancellationToken
            );
            latencyMs += repair.LatencyMs;
            finalResult = finalResult with
            {
                Text = repair.Text,
                LatencyMs = finalResult.LatencyMs + repair.LatencyMs
            };
            displayText = SkillConfigPlanParser.RemovePlanBlock(
                AgentToolProtocol.RemoveToolCallBlock(repair.Text)
            );
        }
        if (
            authoritativeIdentity is not null
            && route.Kind == AgentIntentKind.Query
        )
        {
            displayText = AgentToolProtocol.NormalizeAssetIdentityText(
                displayText,
                authoritativeIdentity
            );
        }
        displayText = AgentToolProtocol.NormalizeReferencedIdentityText(
            displayText,
            AgentToolProtocol.FindReferencedIdentities(executions)
        );
        bool hasParameterContractEvidence =
            AgentToolProtocol.HasParameterContractEvidence(executions);
        if (parameterSemanticsQuestion && !hasParameterContractEvidence)
        {
            displayText = BuildMissingParameterContractText(
                selectedAsset,
                mentionedAssets
            );
        }
        SkillConfigPlanExtraction extraction = SkillConfigPlanParser.Extract(
            finalResult.Text
        );
        if (
            route.ExpectsPlan
            && !string.IsNullOrWhiteSpace(extraction.PlanJson)
        )
        {
            try
            {
                WorkbookPatchWorkspaceSnapshot snapshot =
                    await workspace.GetPatchWorkspaceSnapshotAsync(
                        cancellationToken
                    );
                string normalizedPlan = planNormalizer.Normalize(
                    extraction.PlanJson,
                    message,
                    snapshot
                );
                extraction = new SkillConfigPlanExtraction(
                    normalizedPlan,
                    []
                );
            }
            catch (Exception exception) when (
                exception is InvalidOperationException
                    or FileNotFoundException
            )
            {
                logger.LogWarning(
                    exception,
                    "Failed to normalize SkillConfigPlan."
                );
            }
        }
        var validationErrors = new List<string>(extraction.Errors);
        SkillConfigPlanValidationResult? validation = null;
        if (!string.IsNullOrWhiteSpace(extraction.PlanJson))
        {
            validation = await planValidator.ValidateAsync(
                extraction.PlanJson,
                cancellationToken
            );
            validationErrors.AddRange(validation.Errors);
        }

        if (route.ExpectsPlan && validation?.IsValid == true && validation.Status == "Ready" && patches is not null)
        {
            WorkbookPatchCompileResponse compiled = await patches.CompileAsync(extraction.PlanJson!, cancellationToken);
            validationErrors.AddRange(compiled.Errors.Select(error => error.Code + ": " + error.Message));
        }
        if (route.ExpectsPlan && validation?.IsValid == true && validation.Status == "Ready" && validationErrors.Count == 0)
            validationErrors.AddRange(await ReviewSemanticsAsync(extraction.PlanJson!));
        // Repair invalid model syntax against the actual versioned schema. Never
        // accept a repaired proposal without running the same validator again.
        for (int attempt = 0; route.ExpectsPlan
            && (validationErrors.Count > 0 || string.IsNullOrWhiteSpace(extraction.PlanJson)) && attempt < 2; attempt++)
        {
            string schema = await planValidator.ReadSchemaAsync(cancellationToken,
                route.Kind == AgentIntentKind.Create ? "CreateSkillChainOperation" : null);
            if (route.Kind == AgentIntentKind.Create) schema = BindCreationRepairSchema(schema, executions);
            LlmCompletionResult repair = await provider.CompleteAsync(new LlmCompletionRequest(
                "修正下面的 SkillConfigPlan，使其严格满足 JSON Schema；保留用户意图和值，不添加未经证实的字段或假设。只输出完整 JSON，不要 Markdown。省略 summary、assumptions、evidence、reason 等非必需项，保持输出简短。数值必须是用户语义值，不得提前乘 scale；例如秒值使用 unit=s。root 是 localKey 字符串，fields/parameters 是键值对象。\n"
                + "创建契约已允许新建实体，不需要先存在 Skill、Search 或效果组。先从提供的工具契约与用户请求解决枚举、单位和参数问题；只有用户未说明且没有默认契约的意图才需要澄清。已有明确语义数值时直接保留，不需要运行时战斗结果。所有所需信息齐全时输出 Ready。\n"
                + "用户请求：" + message + "\n校验错误：" + string.Join("\n", validationErrors)
                + "\n语义审查错误必须逐项解决，不能删除原需求或缩窄目标。缺少用户参数或契约证据时输出NeedsClarification，不强行Ready。保留载体、发射方式、命中事件、时序、生命周期和表现要求。\n"
                + "\n待修正计划：\n" + (extraction.PlanJson ?? finalResult.Text)
                + "\n只读契约证据：\n" + AgentToolProtocol.FormatToolResults(RepairEvidence(executions))
                + "\nJSON Schema：\n" + schema,
                "你是受 JSON Schema 约束的配置计划修复器。仅输出合法 JSON，禁止解释。只能使用提供的契约字段和动作参数。\n" + bootstrapContext,
                model, [], reasoningEffort, provider.Descriptor.SupportsJsonSchema ? schema : null), cancellationToken);
            latencyMs += repair.LatencyMs;
            extraction = SkillConfigPlanParser.Extract(repair.Text);
            validationErrors = new(extraction.Errors);
            if (!string.IsNullOrWhiteSpace(extraction.PlanJson))
            {
                try
                {
                    WorkbookPatchWorkspaceSnapshot snapshot = await workspace.GetPatchWorkspaceSnapshotAsync(cancellationToken);
                    extraction = new(planNormalizer.Normalize(extraction.PlanJson, message, snapshot), []);
                }
                catch (Exception exception) when (exception is InvalidOperationException or FileNotFoundException)
                {
                    logger.LogWarning(exception, "Failed to normalize repaired SkillConfigPlan.");
                }
                validation = await planValidator.ValidateAsync(extraction.PlanJson!, cancellationToken);
                validationErrors.AddRange(validation.Errors);
                if (validation.IsValid && validation.Status == "Ready" && patches is not null)
                {
                    WorkbookPatchCompileResponse compiled = await patches.CompileAsync(extraction.PlanJson!, cancellationToken);
                    validationErrors.AddRange(compiled.Errors.Select(error => error.Code + ": " + error.Message));
                }
                if (validation.IsValid && validation.Status == "Ready" && validationErrors.Count == 0)
                    validationErrors.AddRange(await ReviewSemanticsAsync(extraction.PlanJson!));
            }
            finalResult = repair;
            displayText = SkillConfigPlanParser.RemovePlanBlock(repair.Text);
        }

        if (validationErrors.Any(error => error.StartsWith("semantic_review.", StringComparison.Ordinal)))
            displayText += "\n\n需求语义审查未通过，本计划不可写入：\n"
                + string.Join("\n", validationErrors.Where(error => error.StartsWith("semantic_review.", StringComparison.Ordinal)).Select(error => "- " + error));

        string? planJson = route.ExpectsPlan
            ? extraction.PlanJson
            : null;
        if (!route.ExpectsPlan && extraction.PlanJson is not null)
        {
            validationErrors.Clear();
            validationErrors.Add(route.Kind switch
            {
                AgentIntentKind.Ambiguous =>
                    "模型返回了非预期的 SkillConfigPlan；本轮按澄清请求处理，计划未保存。",
                AgentIntentKind.Unsupported =>
                    "模型返回了非预期的 SkillConfigPlan；本轮按不支持请求处理，计划未保存。",
                _ =>
                    "模型返回了非预期的 SkillConfigPlan；本轮按查询处理，计划未保存。"
            });
            validation = null;
        }

        validationErrors = validationErrors
            .Distinct(StringComparer.Ordinal)
            .ToList();
        string disposition = DetermineDisposition(
            route,
            extraction,
            validationErrors
        );
        IReadOnlyList<SkillPlanClarification> clarifications =
            validation?.Clarifications ?? [];
        IReadOnlyList<SkillPlanUnsupported> unsupported =
            validation?.Unsupported ?? [];
        if (
            route.ExpectsPlan
            && string.IsNullOrWhiteSpace(planJson)
            && clarifications.Count == 0
        )
        {
            clarifications =
            [
                new SkillPlanClarification(
                    "missing-plan",
                    "本轮没有生成可提取的 SkillConfigPlan，请补充配置目标、字段和值。",
                    "plan"
                )
            ];
        }
        if (
            route.Kind == AgentIntentKind.Ambiguous
            && clarifications.Count == 0
        )
        {
            clarifications =
            [
                new SkillPlanClarification(
                    "request-target",
                    "请明确要操作的目标技能或资产。",
                    "request.focus"
                )
            ];
        }
        if (
            route.Kind == AgentIntentKind.Unsupported
            && unsupported.Count == 0
        )
        {
            unsupported =
            [
                new SkillPlanUnsupported(
                    "UNSUPPORTED_REQUEST",
                    route.Reason,
                    "使用 Studio 的受控 Plan、校验和 diff 流程。"
                )
            ];
        }
        if (
            parameterSemanticsQuestion
            && !hasParameterContractEvidence
            && clarifications.Count == 0
        )
        {
            clarifications =
            [
                new SkillPlanClarification(
                    "parameter-contract",
                    "当前没有可用的 action_param 能力契约证据，请先绑定包含目标 Effect 的资产后再查询参数定义。",
                    "operations[].fields.action_param"
                )
            ];
        }

        string status = route.Kind switch
        {
            AgentIntentKind.Unsupported => "Unsupported",
            AgentIntentKind.Ambiguous => "NeedsClarification",
            _ when parameterSemanticsQuestion
                && !hasParameterContractEvidence => "NeedsClarification",
            _ when !route.ExpectsPlan => "Ready",
            _ when clarifications.Count > 0 => "NeedsClarification",
            _ when validationErrors.Count > 0 => "NeedsClarification",
            _ => validation?.Status ?? "Ready"
        };
        return new AgentTurnOutcome(
            route.Kind,
            status,
            route.ExpectsPlan,
            finalResult.Provider,
            finalResult.Model,
            displayText,
            latencyMs,
            planJson,
            validationErrors,
            disposition,
            clarifications,
            unsupported,
            executions,
            selectedAsset,
            mentionedAssets
        );

        async Task<IReadOnlyList<string>> ReviewSemanticsAsync(string candidate)
        {
            string schema = await planValidator.ReadSemanticReviewSchemaAsync(cancellationToken);
            var evidence = new System.Text.Json.Nodes.JsonArray
            {
                new System.Text.Json.Nodes.JsonObject
                {
                    ["userRequest"] = message,
                    ["workspaceContext"] = bootstrapContext,
                    ["selectedAsset"] = System.Text.Json.JsonSerializer.SerializeToNode(selectedAsset)
                }
            };
            foreach (AgentToolExecution execution in executions.Where(item => !item.IsError && item.Name != "review_plan_semantics"))
                evidence.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["tool"] = execution.Name,
                    ["arguments"] = System.Text.Json.Nodes.JsonNode.Parse(execution.ArgumentsJson),
                    ["result"] = System.Text.Json.Nodes.JsonNode.Parse(execution.ResultJson)
                });
            string evidenceJson = evidence.ToJsonString();
            try
            {
                string? previousReport = null;
                IReadOnlyList<string> reportErrors = [];
                for (int reviewAttempt = 0; reviewAttempt < 2; reviewAttempt++)
                {
                    LlmCompletionResult review = await provider.CompleteAsync(new LlmCompletionRequest(
                        System.Text.Json.JsonSerializer.Serialize(new
                        {
                            originalRequest = message,
                            conversation = history,
                            candidatePlan = System.Text.Json.Nodes.JsonNode.Parse(candidate),
                            evidence,
                            previousInvalidReport = previousReport,
                            reportValidationErrors = reportErrors
                        }),
                        SkillPlanSemanticReview.Instructions + "\nJSON Schema:\n" + schema,
                        model, [], reasoningEffort, provider.Descriptor.SupportsJsonSchema ? schema : null), cancellationToken);
                    latencyMs += review.LatencyMs;
                    string report = review.Text.Trim();
                    if (report.StartsWith("```", StringComparison.Ordinal))
                        report = Regex.Replace(report, @"^```(?:json)?\s*|\s*```$", "");
                    var userMessages = history.Where(item => item.Role == "user").Select(item => item.Content).Append(message).ToArray();
                    SkillPlanSemanticReviewResult checkedReview = await SkillPlanSemanticReview.ValidateAsync(
                        report, schema, candidate, evidenceJson, userMessages);
                    executions.Add(new AgentToolExecution("review_plan_semantics",
                        System.Text.Json.JsonSerializer.Serialize(new { planHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(candidate))) }),
                        System.Text.Json.JsonSerializer.Serialize(new { report, errors = checkedReview.Errors }),
                        checkedReview.Errors.Count > 0));
                    bool invalidReport = checkedReview.Errors.Any(error =>
                        error.StartsWith("semantic_review.invalid_", StringComparison.Ordinal)
                        || error.StartsWith("semantic_review.duplicate_", StringComparison.Ordinal)
                        || error.StartsWith("semantic_review.missing_binding", StringComparison.Ordinal));
                    if (invalidReport && reviewAttempt == 0)
                    {
                        previousReport = report;
                        reportErrors = checkedReview.Errors;
                        continue;
                    }
                    return checkedReview.Errors;
                }
                return ["semantic_review.invalid_report: 审查报告未通过校验。"];
            }
            catch (Exception exception) when (exception is HttpRequestException or System.Text.Json.JsonException
                || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
            {
                logger.LogWarning(exception, "Semantic review unavailable; candidate blocked.");
                return ["semantic_review.unavailable: 需求语义审查未完成，禁止将该计划视为Ready。"];
            }
        }
    }

    private static bool IsLengthTruncated(LlmCompletionResult result)
    {
        return result.FinishReason?.Trim().ToLowerInvariant() switch
        {
            "length" => true,
            "max_tokens" => true,
            "max_output_tokens" => true,
            "incomplete" => true,
            _ => false
        };
    }

    public static string BindCreationRepairSchema(string schema, IReadOnlyList<AgentToolExecution> executions)
    {
        var contexts = executions.Where(item => !item.IsError)
            .Select(item => System.Text.Json.Nodes.JsonNode.Parse(item.ResultJson)).ToArray();
        var entities = contexts.SelectMany(data => (data?["creationContract"]?["entities"] as System.Text.Json.Nodes.JsonArray ?? [])
            .OfType<System.Text.Json.Nodes.JsonObject>()).GroupBy(entity => entity["namespace"]!.GetValue<string>()).Select(group => group.First()).ToArray();
        if (entities.Length == 0) return schema;
        var root = System.Text.Json.Nodes.JsonNode.Parse(schema)!;
        var operation = root["$defs"]!["CreateSkillChainOperation"]!["properties"]!;
        var nodeTemplate = operation["nodes"]!["items"]!;
        var alternatives = new System.Text.Json.Nodes.JsonArray();
        var valueSchemas = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entity in entities)
        {
            var node = nodeTemplate.DeepClone();
            var properties = node["properties"]!.AsObject();
            properties["namespace"] = new System.Text.Json.Nodes.JsonObject { ["enum"] = new System.Text.Json.Nodes.JsonArray(entity["namespace"]!.DeepClone()) };
            var fields = new System.Text.Json.Nodes.JsonObject();
            var reserved = new[] { "identityField", "actionField", "parameterField", "groupField" }
                .Select(key => entity[key]?.GetValue<string>()).Where(key => key is not null).ToHashSet();
            foreach (var field in entity["fields"]!.AsArray().OfType<System.Text.Json.Nodes.JsonObject>())
                if (!reserved.Contains(field["key"]!.GetValue<string>()))
                    fields[field["semanticName"]!.GetValue<string>()] = ValueSchema(field["referenceTarget"] is not null,
                        (field["rawType"]?.GetValue<string>() ?? "").StartsWith("array", StringComparison.Ordinal), field["rawType"]?.GetValue<string>());
            properties["fields"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = fields };
            string? category = entity["actionCategory"]?.GetValue<string>();
            if (category is null)
            {
                foreach (string key in new[] { "actionKey", "parameters", "group" }) properties.Remove(key);
            }
            else
            {
                node["required"]!.AsArray().Add("actionKey");
                node["required"]!.AsArray().Add("group");
                var actions = contexts.SelectMany(data => (data?[category == "effect" ? "effects" : "conditions"] as System.Text.Json.Nodes.JsonArray ?? [])
                    .OfType<System.Text.Json.Nodes.JsonObject>()).GroupBy(action => action["key"]!.GetValue<string>()).Select(group => group.First()).ToArray();
                if (actions.Length > 0)
                {
                    properties["actionKey"] = new System.Text.Json.Nodes.JsonObject { ["enum"] = new System.Text.Json.Nodes.JsonArray(actions.Select(action => action["key"]!.DeepClone()).ToArray()) };
                    var parameters = new System.Text.Json.Nodes.JsonObject();
                    foreach (var parameter in actions.SelectMany(action => action["parameters"]!.AsArray().OfType<System.Text.Json.Nodes.JsonObject>()))
                        parameters[parameter["key"]!.GetValue<string>()] = ValueSchema(parameter["referenceTarget"] is not null, parameter["repeating"]?.GetValue<bool>() == true || parameter["allowsMultipleEnumValues"]?.GetValue<bool>() == true);
                    properties["parameters"] = new System.Text.Json.Nodes.JsonObject
                        { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = parameters };
                }
            }
            alternatives.Add(node);
        }
        operation["nodes"]!["items"] = new System.Text.Json.Nodes.JsonObject { ["oneOf"] = alternatives };
        operation["groups"]!["items"]!["properties"]!["namespace"] = new System.Text.Json.Nodes.JsonObject
        {
            ["enum"] = new System.Text.Json.Nodes.JsonArray(entities.Select(entity => entity["groupNamespace"]?.GetValue<string>())
                .Where(value => value is not null).Distinct().Select(value => (System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(value)).ToArray())
        };
        return root.ToJsonString();
        System.Text.Json.Nodes.JsonNode ValueSchema(bool reference, bool list, string? rawType = null)
        {
            var nested = contexts.SelectMany(data => (data?["creationContract"]?["nestedTypes"] as System.Text.Json.Nodes.JsonArray ?? [])
                .OfType<System.Text.Json.Nodes.JsonObject>()).FirstOrDefault(type => rawType?.Split(',').Select(token => token.Trim()).Contains(type["key"]!.GetValue<string>()) == true);
            bool map = rawType?.StartsWith("map,", StringComparison.Ordinal) == true;
            string signature = reference + ":" + list + ":" + nested?["key"]?.GetValue<string>() + ":" + map;
            if (valueSchemas.TryGetValue(signature, out string? existing))
                return new System.Text.Json.Nodes.JsonObject { ["$ref"] = "#/$defs/" + existing };
            var value = root["$defs"]!["Value"]!.DeepClone();
            value["properties"]!.AsObject().Remove("evidence");
            System.Text.Json.Nodes.JsonNode item = reference
                ? new System.Text.Json.Nodes.JsonObject { ["$ref"] = "#/$defs/ChainRef" }
                : new System.Text.Json.Nodes.JsonObject { ["type"] = new System.Text.Json.Nodes.JsonArray("string", "number", "boolean") };
            if (nested is not null)
            {
                var properties = new System.Text.Json.Nodes.JsonObject();
                foreach (var field in nested["fields"]!.AsArray())
                    properties[field!["key"]!.GetValue<string>()] = new System.Text.Json.Nodes.JsonObject
                        { ["type"] = new System.Text.Json.Nodes.JsonArray("string", "number") };
                item = new System.Text.Json.Nodes.JsonObject { ["type"] = "object", ["additionalProperties"] = false, ["properties"] = properties,
                    ["required"] = new System.Text.Json.Nodes.JsonArray(properties.Select(pair => (System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(pair.Key)).ToArray()) };
            }
            if (map) item = new System.Text.Json.Nodes.JsonObject { ["type"] = "object", ["additionalProperties"] = new System.Text.Json.Nodes.JsonObject { ["type"] = new System.Text.Json.Nodes.JsonArray("number", "string") } };
            value["properties"]!["value"] = list
                ? new System.Text.Json.Nodes.JsonObject { ["type"] = "array", ["items"] = item, ["maxItems"] = 128 }
                : item;
            string name = "CreationValue" + valueSchemas.Count;
            valueSchemas[signature] = name;
            root["$defs"]![name] = value;
            return new System.Text.Json.Nodes.JsonObject { ["$ref"] = "#/$defs/" + name };
        }
    }

    private static IReadOnlyList<AgentToolExecution> RepairEvidence(IReadOnlyList<AgentToolExecution> executions) =>
        executions.Select(execution =>
        {
            var data = System.Text.Json.Nodes.JsonNode.Parse(execution.ResultJson);
            if (data is System.Text.Json.Nodes.JsonObject obj)
            {
                obj.Remove("actionDirectory");
                obj.Remove("tableFields");
                if (obj["creationContract"]?["entities"] is System.Text.Json.Nodes.JsonArray entities)
                    foreach (var entity in entities.OfType<System.Text.Json.Nodes.JsonObject>()) entity.Remove("fields");
            }
            return execution with { ResultJson = data?.ToJsonString() ?? execution.ResultJson };
        }).ToArray();

    private static string BuildMissingParameterContractText(
        StudioAssetRef? selectedAsset,
        IReadOnlyList<StudioAssetRef> mentionedAssets
    )
    {
        StudioAssetRef? target =
            selectedAsset
            ?? mentionedAssets.FirstOrDefault();
        string targetText = target is null
            ? "当前会话没有绑定可查询的行为资产"
            : $"当前会话绑定的资产是 {target.Namespace}:{target.Id}，但本轮没有取得对应动作的参数契约";
        return string.Join(
            Environment.NewLine,
            $"{targetText}。",
            "",
            "因此我不能判断这些 `action_param` 分别代表什么，也不会据此给出任何目标值或修改建议。",
            "",
            "请绑定包含目标 Effect 的 Skill，或明确目标 Effect ID。系统会先通过 `get_graph` 找到动作类型，再通过 `get_capability_context` 按 `parameters[index]` 读取权威定义。"
        );
    }

    private static string DetermineDisposition(
        AgentRoute route,
        SkillConfigPlanExtraction extraction,
        IReadOnlyList<string> validationErrors
    )
    {
        bool hasPlan = !string.IsNullOrWhiteSpace(extraction.PlanJson);
        if (route.Kind == AgentIntentKind.Unsupported)
        {
            return hasPlan ? "UnexpectedProposal" : "Unsupported";
        }
        if (route.Kind == AgentIntentKind.Ambiguous)
        {
            return hasPlan ? "UnexpectedProposal" : "NeedsClarification";
        }
        if (!route.ExpectsPlan && hasPlan)
        {
            return "UnexpectedProposal";
        }
        if (route.ExpectsPlan && !hasPlan)
        {
            return "Missing";
        }
        if (hasPlan && validationErrors.Count > 0)
        {
            return "ValidationFailed";
        }
        return hasPlan ? "Expected" : "None";
    }
}
