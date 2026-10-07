using Microsoft.Extensions.Configuration;
using RtsSkillStudio.Agent;
using RtsSkillStudio.Agent.Llm;
using RtsSkillStudio.Agent.Workspaces;
using RtsSkillStudio.Api.Workspaces;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);
var llmOptions = builder.Configuration.GetSection("Llm").Get<LlmOptions>()
    ?? new LlmOptions();
var workspaceOptions = builder.Configuration.GetSection("Workspace").Get<SkillWorkspaceOptions>()
    ?? new SkillWorkspaceOptions();

builder.Services.AddSingleton(llmOptions);
builder.Services.AddSingleton(workspaceOptions);
builder.Services.AddSingleton(
    new HttpClient { Timeout = Timeout.InfiniteTimeSpan }
);
builder.Services.AddSingleton<LlmProviderFactory>();
builder.Services.AddSingleton<SkillWorkspaceService>();
builder.Services.AddSingleton<SkillAgentContextBuilder>();
builder.Services.AddSingleton<StudioConversationStore>();
builder.Services.AddSingleton(
    new SkillConfigPlanValidator(
        Path.GetFullPath(
            Path.Combine(
                builder.Environment.ContentRootPath,
                "..",
                "..",
                StudioInfo.ContractRoot
            )
        )
    )
);
builder.Services.AddSingleton<SkillReadOnlyToolService>();
builder.Services.AddSingleton<IAgentReadOnlyToolService>(
    services => services.GetRequiredService<SkillReadOnlyToolService>()
);
builder.Services.AddSingleton<AgentToolLoop>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet(
    "/api/v1/legacy",
    () =>
        Results.Ok(
            new
            {
                name = StudioInfo.Name,
                version = StudioInfo.Version
            }
        )
);

app.MapGet(
    "/api/v1/health",
    () =>
        Results.Ok(
            new
            {
                status = "ok",
                studio = StudioInfo.Name,
                version = StudioInfo.Version
            }
        )
);

app.MapGet(
    "/api/v1/studio/info",
    () =>
        Results.Ok(
            new
            {
                name = StudioInfo.Name,
                version = StudioInfo.Version,
                contractRoot = StudioInfo.ContractRoot
            }
        )
);

app.MapGet(
    "/api/v1/llm/providers",
    (LlmProviderFactory providerFactory) =>
        Results.Ok(providerFactory.ListProviders())
);

app.MapPost(
    "/api/v1/llm/chat",
    async (
        LlmChatApiRequest request,
        LlmProviderFactory providerFactory,
        SkillAgentContextBuilder contextBuilder,
        CancellationToken cancellationToken
    ) =>
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return Results.BadRequest(
                new { error = "Message is required." }
            );
        }

        try
        {
            AgentWorkspaceContext workspaceContext = await contextBuilder.BuildAsync(
                ResolveLlmRequestAsset(request),
                request.Message,
                cancellationToken
            );
            var result = await providerFactory
                .GetProvider(request.Provider)
                .CompleteAsync(
                    new LlmCompletionRequest(
                        request.Message,
                        SkillAgentInstructions.Build(
                            request.Instructions,
                            workspaceContext.Text
                        ),
                        request.Model,
                        request.History,
                        request.ReasoningEffort
                    ),
                    cancellationToken
                );

            return Results.Ok(
                new LlmChatApiResponse(
                    result.Provider,
                    result.Model,
                    result.Text,
                    result.LatencyMs
                )
            );
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(
                new { error = exception.Message }
            );
        }
        catch (InvalidOperationException exception)
        {
            return Results.Problem(
                detail: exception.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable
            );
        }
        catch (LlmProviderException exception)
        {
            return Results.Problem(
                detail: exception.Message,
                statusCode: StatusCodes.Status502BadGateway
            );
        }
    }
);

app.MapGet(
    "/api/v1/llm/providers/{providerName}/models",
    async (
        string providerName,
        LlmProviderFactory providerFactory,
        CancellationToken cancellationToken
    ) =>
    {
        try
        {
            return Results.Ok(
                await providerFactory.ListModelsAsync(
                    providerName,
                    cancellationToken
                )
            );
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
        catch (LlmProviderException exception)
        {
            return Results.Problem(
                detail: exception.Message,
                statusCode: StatusCodes.Status502BadGateway
            );
        }
    }
);

app.MapGet(
    "/api/v1/conversations",
    async (
        StudioConversationStore conversations,
        CancellationToken cancellationToken
    ) => Results.Ok(await conversations.ListAsync(10, cancellationToken))
);

app.MapPost(
    "/api/v1/conversations",
    async (
        CreateConversationRequest? request,
        StudioConversationStore conversations,
        CancellationToken cancellationToken
    ) =>
        Results.Ok(
            await conversations.CreateAsync(
                request?.Title,
                cancellationToken
            )
        )
);

app.MapDelete(
    "/api/v1/conversations/{conversationId}",
    async (
        string conversationId,
        StudioConversationStore conversations,
        CancellationToken cancellationToken
    ) =>
    {
        bool deleted = await conversations.DeleteAsync(
            conversationId,
            cancellationToken
        );
        return deleted
            ? Results.NoContent()
            : Results.NotFound(new { error = "Conversation not found." });
    }
);

app.MapGet(
    "/api/v1/conversations/{conversationId}",
    async (
        string conversationId,
        StudioConversationStore conversations,
        CancellationToken cancellationToken
    ) =>
    {
        ConversationDetail? conversation = await conversations.GetAsync(
            conversationId,
            cancellationToken
        );
        return conversation is null
            ? Results.NotFound(new { error = "Conversation not found." })
            : Results.Ok(conversation);
    }
);

app.MapGet(
    "/api/v1/conversations/{conversationId}/plans",
    async (
        string conversationId,
        StudioConversationStore conversations,
        CancellationToken cancellationToken
    ) =>
    {
        ConversationDetail? conversation = await conversations.GetAsync(
            conversationId,
            cancellationToken
        );
        return conversation is null
            ? Results.NotFound(new { error = "Conversation not found." })
            : Results.Ok(
                await conversations.ListPlanRevisionsAsync(
                    conversationId,
                    cancellationToken
                )
            );
    }
);

app.MapPut(
    "/api/v1/conversations/{conversationId}/skill",
    async (
        string conversationId,
        UpdateConversationSkillRequest request,
        StudioConversationStore conversations,
        CancellationToken cancellationToken
    ) =>
    {
        ConversationDetail? conversation = await conversations.GetAsync(
            conversationId,
            cancellationToken
        );
        if (conversation is null)
        {
            return Results.NotFound(new { error = "Conversation not found." });
        }

        await conversations.UpdateSelectedSkillAsync(
            conversationId,
            request.SkillId,
            cancellationToken
        );
        return Results.NoContent();
    }
);

app.MapPut(
    "/api/v1/conversations/{conversationId}/asset",
    async (
        string conversationId,
        UpdateConversationAssetRequest request,
        StudioConversationStore conversations,
        CancellationToken cancellationToken
    ) =>
    {
        ConversationDetail? conversation = await conversations.GetAsync(
            conversationId,
            cancellationToken
        );
        if (conversation is null)
        {
            return Results.NotFound(new { error = "Conversation not found." });
        }

        StudioAssetRef? asset = ResolveAsset(
            request.AssetNamespace,
            request.AssetId,
            request.SkillId
        );
        await conversations.UpdateSelectedAssetAsync(
            conversationId,
            asset,
            cancellationToken
        );
        return Results.NoContent();
    }
);

app.MapPost(
    "/api/v1/conversations/{conversationId}/chat",
    async (
        string conversationId,
        ConversationChatRequest request,
        StudioConversationStore conversations,
        SkillAgentContextBuilder contextBuilder,
        AgentToolLoop toolLoop,
        CancellationToken cancellationToken
    ) =>
    {
        if (string.IsNullOrWhiteSpace(request.Message))
        {
            return Results.BadRequest(new { error = "Message is required." });
        }

        ConversationDetail? conversation = await conversations.GetAsync(
            conversationId,
            cancellationToken
        );
        if (conversation is null)
        {
            return Results.NotFound(new { error = "Conversation not found." });
        }

        StudioAssetRef? requestAsset =
            ResolveConversationRequestAsset(request);
        string effectiveMessage = request.Message;
        if (
            requestAsset is null
            && conversation.PendingRequest is not null
            && conversation.PendingAssets.Count > 0
        )
        {
            StudioAssetRef? pendingSelection = ResolvePendingAssetSelection(
                request.Message,
                conversation.PendingAssets
            );
            if (pendingSelection is not null)
            {
                requestAsset = pendingSelection;
                effectiveMessage = conversation.PendingRequest;
                await conversations.ClearPendingAssetClarificationAsync(
                    conversationId,
                    cancellationToken
                );
            }
            else if (
                IsGenericConfirmation(request.Message)
                && conversation.PendingAssets.Count > 1
            )
            {
                await conversations.AppendMessageAsync(
                    conversationId,
                    "user",
                    request.Message,
                    null,
                    null,
                    null,
                    cancellationToken
                );
                string clarification = BuildAssetClarificationText(
                    conversation.PendingAssets
                );
                await conversations.AppendMessageAsync(
                    conversationId,
                    "assistant",
                    clarification,
                    "studio",
                    "asset-resolver",
                    0,
                    cancellationToken
                );
                return Results.Ok(
                    new ConversationChatResponse(
                        conversationId,
                        null,
                        null,
                        "studio",
                        "asset-resolver",
                        clarification,
                        0,
                        null,
                        [],
                        false,
                        "NeedsClarification",
                        conversation.PendingAssets,
                        AgentIntentKind.Configuration.ToString(),
                        "NeedsClarification",
                        [
                            new SkillPlanClarification(
                                "asset-selection",
                                clarification,
                                "request.focus"
                            )
                        ],
                        [],
                        []
                    )
                );
            }
            else
            {
                await conversations.ClearPendingAssetClarificationAsync(
                    conversationId,
                    cancellationToken
                );
            }
        }
        else if (
            requestAsset is not null
            && conversation.PendingRequest is not null
        )
        {
            await conversations.ClearPendingAssetClarificationAsync(
                conversationId,
                cancellationToken
            );
        }

        StudioAssetRef? selectedAsset = requestAsset
            ?? conversation.SelectedAsset;
        if (requestAsset is not null)
        {
            await conversations.UpdateSelectedAssetAsync(
                conversationId,
                requestAsset,
                cancellationToken
            );
        }

        try
        {
            AgentRoute route = AgentIntentRouter.Route(effectiveMessage);
            AgentWorkspaceContext workspaceContext =
                await contextBuilder.BuildBootstrapAsync(
                    selectedAsset,
                    effectiveMessage,
                    cancellationToken
                );
            StudioAssetRef? boundAsset =
                requestAsset
                ?? workspaceContext.Asset
                ?? conversation.SelectedAsset;
            IReadOnlyList<StudioAssetRef> mentionedAssets =
                boundAsset is not null
                    ? [boundAsset]
                    : workspaceContext.ValidCandidates;
            if (
                workspaceContext.Asset is null
                && workspaceContext.RequiresClarification
                && workspaceContext.ValidCandidates.Count > 0
                && requestAsset is null
            )
            {
                IReadOnlyList<StudioAssetRef> candidates =
                    workspaceContext.ValidCandidates;
                IReadOnlyList<StudioAssetRef> pendingCandidates =
                    candidates.Take(5).ToArray();
                await conversations.SetPendingAssetClarificationAsync(
                    conversationId,
                    effectiveMessage,
                    pendingCandidates,
                    cancellationToken
                );
                await conversations.UpdateMentionedAssetsAsync(
                    conversationId,
                    pendingCandidates,
                    cancellationToken
                );
                await conversations.AppendMessageAsync(
                    conversationId,
                    "user",
                    request.Message,
                    null,
                    null,
                    null,
                    cancellationToken
                );
                string clarification = BuildAssetClarificationText(
                    pendingCandidates
                );
                await conversations.AppendMessageAsync(
                    conversationId,
                    "assistant",
                    clarification,
                    "studio",
                    "asset-resolver",
                    0,
                    cancellationToken
                );
                return Results.Ok(
                    new ConversationChatResponse(
                        conversationId,
                        null,
                        null,
                        "studio",
                        "asset-resolver",
                        clarification,
                        0,
                        null,
                        [],
                        false,
                        "NeedsClarification",
                        pendingCandidates,
                        route.Kind.ToString(),
                        "NeedsClarification",
                        [
                            new SkillPlanClarification(
                                "asset-selection",
                                clarification,
                                "request.focus"
                            )
                        ],
                        [],
                        []
                    )
                );
            }
            await conversations.UpdateMentionedAssetsAsync(
                conversationId,
                mentionedAssets,
                cancellationToken
            );
            if (
                workspaceContext.RequiresClarification
                && requestAsset is null
                && conversation.SelectedAsset is not null
            )
            {
                await conversations.UpdateSelectedAssetAsync(
                    conversationId,
                    null,
                    cancellationToken
                );
            }
            else if (
                requestAsset is null
                && workspaceContext.Asset is not null
                && workspaceContext.Asset != conversation.SelectedAsset
            )
            {
                await conversations.UpdateSelectedAssetAsync(
                    conversationId,
                    workspaceContext.Asset,
                    cancellationToken
                );
            }
            await conversations.AppendMessageAsync(
                conversationId,
                "user",
                request.Message,
                null,
                null,
                null,
                cancellationToken
            );

            IReadOnlyList<LlmChatMessage> history = conversation
                .Messages.TakeLast(16)
                .Select(
                    message => new LlmChatMessage(
                        message.Role,
                        message.Content
                    )
                )
                .ToArray();
            AgentTurnOutcome turn = await toolLoop.RunAsync(
                request.Provider,
                request.Model,
                request.ReasoningEffort,
                effectiveMessage,
                history,
                workspaceContext.Text,
                route,
                boundAsset,
                mentionedAssets,
                cancellationToken
            );

            await conversations.AppendMessageAsync(
                conversationId,
                "assistant",
                turn.Text,
                turn.Provider,
                turn.Model,
                turn.LatencyMs,
                cancellationToken
            );

            if (!string.IsNullOrWhiteSpace(turn.PlanJson))
            {
                await conversations.SavePlanAsync(
                    conversationId,
                    turn.PlanJson,
                    turn.PlanErrors,
                    turn.ExpectsPlan,
                    turn.PlanDisposition,
                    cancellationToken
                );
            }

            return Results.Ok(
                new ConversationChatResponse(
                    conversationId,
                    boundAsset?.Namespace == "TbSkill"
                        ? boundAsset.Id
                        : null,
                    boundAsset,
                    turn.Provider,
                    turn.Model,
                    turn.Text,
                    turn.LatencyMs,
                    turn.PlanJson,
                    turn.PlanErrors,
                    turn.ExpectsPlan,
                    turn.PlanDisposition,
                    mentionedAssets,
                    turn.Intent.ToString(),
                    turn.Status,
                    turn.Clarifications,
                    turn.Unsupported,
                    turn.ToolExecutions
                )
            );
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
        catch (InvalidOperationException exception)
        {
            return Results.Problem(
                detail: exception.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable
            );
        }
        catch (LlmProviderException exception)
        {
            return Results.Problem(
                detail: exception.Message,
                statusCode: StatusCodes.Status502BadGateway
            );
        }
    }
);

app.MapGet(
    "/api/v1/workspace/status",
    async (
        SkillWorkspaceService workspace,
        CancellationToken cancellationToken
    ) => Results.Ok(await workspace.GetStatusAsync(cancellationToken))
);

app.MapGet(
    "/api/v1/skills",
    async (
        string? query,
        int? limit,
        SkillWorkspaceService workspace,
        CancellationToken cancellationToken
    ) =>
    {
        try
        {
            return Results.Ok(
                await workspace.ListSkillsAsync(
                    query,
                    limit ?? 100,
                    cancellationToken
                )
            );
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or FileNotFoundException
                or ArgumentException
        )
        {
            return Results.Problem(
                detail: exception.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable
            );
        }
    }
);

app.MapGet(
    "/api/v1/skills/{skillId:int}/chain",
    async (
        int skillId,
        int? depth,
        SkillWorkspaceService workspace,
        CancellationToken cancellationToken
    ) =>
    {
        try
        {
            return Results.Ok(
                await workspace.GetSkillChainAsync(
                    skillId,
                    depth ?? 12,
                    cancellationToken
                )
            );
        }
        catch (KeyNotFoundException exception)
        {
            return Results.NotFound(new { error = exception.Message });
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or FileNotFoundException
                or ArgumentException
        )
        {
            return Results.Problem(
                detail: exception.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable
            );
        }
    }
);

app.MapGet(
    "/api/v1/assets/search",
    async (
        string? query,
        int? limit,
        SkillWorkspaceService workspace,
        CancellationToken cancellationToken
    ) =>
    {
        try
        {
            return Results.Ok(
                await workspace.SearchAssetsAsync(
                    query,
                    limit ?? 20,
                    cancellationToken
                )
            );
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or FileNotFoundException
                or ArgumentException
        )
        {
            return Results.Problem(
                detail: exception.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable
            );
        }
    }
);

app.MapGet(
    "/api/v1/assets/chain",
    async (
        string? @namespace,
        int? id,
        int? depth,
        string? direction,
        SkillWorkspaceService workspace,
        CancellationToken cancellationToken
    ) =>
    {
        if (string.IsNullOrWhiteSpace(@namespace) || id is null)
        {
            return Results.BadRequest(
                new { error = "namespace and id are required." }
            );
        }
        if (
            !string.IsNullOrWhiteSpace(direction)
            && direction is not ("out" or "in" or "both")
        )
        {
            return Results.BadRequest(
                new { error = "direction must be out, in, or both." }
            );
        }

        try
        {
            return Results.Ok(
                await workspace.GetAssetChainAsync(
                    new StudioAssetRef(@namespace, id.Value),
                    depth ?? 6,
                    direction ?? "out",
                    cancellationToken
                )
            );
        }
        catch (KeyNotFoundException exception)
        {
            return Results.NotFound(new { error = exception.Message });
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or FileNotFoundException
                or ArgumentException
        )
        {
            return Results.Problem(
                detail: exception.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable
            );
        }
    }
);

app.MapPost(
    "/api/v1/workspace/write-smoke-test",
    async (
        SkillWorkspaceService workspace,
        CancellationToken cancellationToken
    ) =>
    {
        try
        {
            return Results.Ok(
                await workspace.RunWriteSmokeTestAsync(cancellationToken)
            );
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or FileNotFoundException
                or IOException
        )
        {
            return Results.Problem(
                detail: exception.Message,
                statusCode: StatusCodes.Status500InternalServerError
            );
        }
    }
);

static StudioAssetRef? ResolveLlmRequestAsset(LlmChatApiRequest request)
{
    return ResolveAsset(
        request.AssetNamespace,
        request.AssetId,
        request.SkillId
    );
}

static StudioAssetRef? ResolveConversationRequestAsset(
    ConversationChatRequest request
)
{
    return ResolveAsset(
        request.AssetNamespace,
        request.AssetId,
        request.SkillId
    );
}

static StudioAssetRef? ResolveAsset(
    string? assetNamespace,
    int? assetId,
    int? skillId
)
{
    if (!string.IsNullOrWhiteSpace(assetNamespace) && assetId is not null)
    {
        return new StudioAssetRef(
            SkillWorkspaceService.NormalizeAssetNamespace(assetNamespace),
            assetId.Value
        );
    }

    return skillId is null
        ? null
        : new StudioAssetRef("TbSkill", skillId.Value);
}

static StudioAssetRef? ResolvePendingAssetSelection(
    string message,
    IReadOnlyList<StudioAssetRef> candidates
)
{
    string text = message.Trim();
    Match keyMatch = Regex.Match(
        text,
        @"(?i)\b(?<namespace>Tb[A-Za-z]+|EffectGroup|ConditionGroup):(?<id>\d+)\b"
    );
    if (keyMatch.Success && int.TryParse(keyMatch.Groups["id"].Value, out int keyId))
    {
        StudioAssetRef? keyed = candidates.FirstOrDefault(
            candidate =>
                string.Equals(
                    candidate.Namespace,
                    keyMatch.Groups["namespace"].Value,
                    StringComparison.OrdinalIgnoreCase
                )
                && candidate.Id == keyId
        );
        if (keyed is not null)
        {
            return keyed;
        }
    }

    Match indexMatch = Regex.Match(text, @"^\s*(?<index>\d{1,2})\s*$");
    if (
        indexMatch.Success
        && int.TryParse(indexMatch.Groups["index"].Value, out int index)
        && index >= 1
        && index <= candidates.Count
    )
    {
        return candidates[index - 1];
    }

    if (
        candidates.Count == 1
        && Regex.IsMatch(
            text,
            @"^(?:是|是的|对|对的|确认|没错|可以|好|就这个|这个|yes|y)$",
            RegexOptions.IgnoreCase
        )
    )
    {
        return candidates[0];
    }

    foreach (StudioAssetRef candidate in candidates)
    {
        if (
            text.Contains(
                $"{candidate.Namespace}:{candidate.Id}",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return candidate;
        }
    }

    return null;
}

static bool IsGenericConfirmation(string message)
{
    return Regex.IsMatch(
        message.Trim(),
        @"^(?:是|是的|对|对的|确认|没错|可以|好|就这个|这个|yes|y)$",
        RegexOptions.IgnoreCase
    );
}

static string BuildAssetClarificationText(
    IReadOnlyList<StudioAssetRef> candidates
)
{
    var builder = new System.Text.StringBuilder();
    builder.AppendLine(
        candidates.Count == 1
            ? "我找到一个可能的资产，请确认是不是它："
            : "我找到多个可能的资产，请选择："
    );
    builder.AppendLine();
    for (int index = 0; index < candidates.Count; index++)
    {
        builder.AppendLine(
            $"{index + 1}. `{candidates[index].Namespace}:{candidates[index].Id}`"
        );
    }
    builder.AppendLine();
    builder.AppendLine(
        candidates.Count == 1
            ? "如果正确，回复“是”或“1”；如果不是，请给出正确名称或 `Namespace:id`。"
            : "请回复对应编号或 `Namespace:id`。"
    );
    return builder.ToString().Trim();
}

app.Run();
