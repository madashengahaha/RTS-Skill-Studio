namespace RtsSkillStudio.Api.Workspaces;

public sealed record WorkbookPatchCompileRequest(string PlanJson);

public sealed record WorkbookPatchApplyTemporaryRequest(string PatchJson);

public sealed record WorkbookPatchApplyFinalRequest(string PatchJson);
