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
            var result = await providerFactory
                .GetProvider(request.Provider)
                .CompleteAsync(
                    new LlmCompletionRequest(
                        request.Message,
                        SkillAgentInstructions.Build(request.Instructions),
                        request.Model
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

app.Run();
