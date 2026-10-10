namespace RtsSkillStudio.Api.Workspaces;

public sealed record WorkbookPatchCompileRequest(string PlanJson, string? ReviewedPatchJson = null, bool Rebase = false);

public sealed record WorkbookPatchApplyTemporaryRequest(string PatchJson);

public sealed record WorkbookPatchApplyFinalRequest(string PatchJson);
