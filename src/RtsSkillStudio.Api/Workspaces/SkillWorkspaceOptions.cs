namespace RtsSkillStudio.Api.Workspaces;

public sealed class SkillWorkspaceOptions
{
    public string WorkspaceId { get; set; } = "studio-workspace";

    public string ExcelDataRoot { get; set; } = "";

    public string HeroAuthoringSchemaPath { get; set; } = "";

    public string WriteTestRoot { get; set; } = ".studio-work";

    public string TransactionRoot { get; set; } = ".studio-work/transactions";

    public int WriteTestRetentionCount { get; set; } = 5;

    public string ConversationDatabasePath { get; set; } =
        ".studio-work/studio-conversations.db";
}
