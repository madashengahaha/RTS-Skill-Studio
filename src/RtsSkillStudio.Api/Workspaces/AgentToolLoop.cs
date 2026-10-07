using RtsSkillStudio.Agent;
using RtsSkillStudio.Agent.Llm;
using RtsSkillStudio.Agent.Workspaces;
using System.Text.RegularExpressions;

namespace RtsSkillStudio.Api.Workspaces;

public sealed class AgentToolLoop(
    LlmProviderFactory providers,
    IAgentReadOnlyToolService tools,
    SkillConfigPlanValidator planValidator,
    ILogger<AgentToolLoop> logger
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
                finalResult = result;
                break;
            }

            IReadOnlyList<AgentToolExecution> roundExecutions =
                await tools.ExecuteAsync(calls, cancellationToken);
            executions.AddRange(roundExecutions);
            if (!userRecorded)
            {
                baseHistory.Add(new LlmChatMessage("user", message));
                userRecorded = true;
            }
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
    }

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
