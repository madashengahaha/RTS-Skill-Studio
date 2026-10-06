namespace RtsSkillStudio.Api.Workspaces;

public sealed class SkillWorkspaceOptions
{
    public string ExcelDataRoot { get; set; } = "";

    public string HeroAuthoringSchemaPath { get; set; } = "";

    public string WriteTestRoot { get; set; } = ".studio-work";

    public string ConversationDatabasePath { get; set; } =
        ".studio-work/studio-conversations.db";
}
