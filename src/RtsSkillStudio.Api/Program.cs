using RtsSkillStudio.Agent;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet(
    "/",
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

app.Run();
