using Microsoft.Extensions.Configuration;
using RtsSkillStudio.Agent;
using RtsSkillStudio.Agent.Llm;
using RtsSkillStudio.Agent.Workspaces;
using RtsSkillStudio.Api.Workspaces;

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
        LlmProviderFactory providerFactory,
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
            AgentWorkspaceContext workspaceContext = await contextBuilder.BuildAsync(
                selectedAsset,
                request.Message,
                cancellationToken
            );
            IReadOnlyList<StudioAssetRef> mentionedAssets =
                workspaceContext.Asset is not null
                    ? [workspaceContext.Asset]
                    : workspaceContext.ValidCandidates;
            await conversations.UpdateMentionedAssetsAsync(
                conversationId,
                mentionedAssets,
                cancellationToken
            );
            if (
                workspaceContext.RequiresClarification
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
                workspaceContext.Asset is not null
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
            LlmCompletionResult result = await providerFactory
                .GetProvider(request.Provider)
                .CompleteAsync(
                    new LlmCompletionRequest(
                        request.Message,
                        SkillAgentInstructions.Build(
                            null,
                            workspaceContext.Text
                        ),
                        request.Model,
                        history,
                        request.ReasoningEffort
                    ),
                    cancellationToken
                );
            string displayText = SkillConfigPlanParser.RemovePlanBlock(
                result.Text
            );

            await conversations.AppendMessageAsync(
                conversationId,
                "assistant",
                displayText,
                result.Provider,
                result.Model,
                result.LatencyMs,
                cancellationToken
            );

            AgentIntentKind intent = AgentIntentDetector.Classify(
                request.Message
            );
            bool expectsPlan = intent == AgentIntentKind.Configuration;
            SkillConfigPlanExtraction plan = SkillConfigPlanParser.Extract(
                result.Text
            );
            string planDisposition = "None";
            if (expectsPlan && string.IsNullOrWhiteSpace(plan.PlanJson))
            {
                plan = new SkillConfigPlanExtraction(
                    null,
                    ["配置请求未生成可提取的 SkillConfigPlan。"]
                );
                planDisposition = "Missing";
            }
            else if (expectsPlan)
            {
                planDisposition = "Expected";
            }
            else if (!string.IsNullOrWhiteSpace(plan.PlanJson))
            {
                planDisposition = "UnexpectedProposal";
                plan = plan with
                {
                    Errors =
                    [
                        .. plan.Errors,
                        "本轮未识别为配置请求，Plan 未作为当前配置计划保存，仅保留供人工检查。"
                    ]
                };
            }
            else if (intent == AgentIntentKind.Ambiguous)
            {
                planDisposition = "NeedsClarification";
                plan = new SkillConfigPlanExtraction(
                    null,
                    ["请求缺少明确的技能目标和字段，需要用户补充后再生成 Plan。"]
                );
            }
            if (!string.IsNullOrWhiteSpace(plan.PlanJson))
            {
                await conversations.SavePlanAsync(
                    conversationId,
                    plan.PlanJson,
                    plan.Errors,
                    expectsPlan,
                    planDisposition,
                    cancellationToken
                );
            }

            return Results.Ok(
                new ConversationChatResponse(
                    conversationId,
                    workspaceContext.Asset?.Namespace == "TbSkill"
                        ? workspaceContext.Asset.Id
                        : null,
                    workspaceContext.Asset,
                    result.Provider,
                    result.Model,
                    displayText,
                    result.LatencyMs,
                    plan.PlanJson,
                    plan.Errors,
                    expectsPlan,
                    planDisposition,
                    mentionedAssets
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

        try
        {
            return Results.Ok(
                await workspace.GetAssetChainAsync(
                    new StudioAssetRef(@namespace, id.Value),
                    depth ?? 6,
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

app.Run();
