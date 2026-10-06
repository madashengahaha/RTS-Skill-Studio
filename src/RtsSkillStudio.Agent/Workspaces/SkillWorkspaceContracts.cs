namespace RtsSkillStudio.Agent.Workspaces;

public sealed record SkillWorkspaceStatus(
    bool Configured,
    string ExcelDataRoot,
    string SchemaPath,
    bool ExcelRootExists,
    bool SchemaExists,
    string? Revision,
    int TableCount,
    int NodeCount,
    int EdgeCount,
    int SkillCount,
    IReadOnlyList<string> Errors
);

public sealed record SkillSummary(
    int Id,
    string Label,
    string Summary,
    int SourceRow,
    int IncomingReferenceCount,
    int OutgoingReferenceCount
);

public sealed record SkillChainSnapshot(
    string Revision,
    int SkillId,
    string FocusKey,
    IReadOnlyList<SkillChainNode> Nodes,
    IReadOnlyList<SkillChainEdge> Edges,
    IReadOnlyList<SkillInboundReference> IncomingReferences,
    bool IncomingReferencesTruncated
);

public sealed record SkillChainNode(
    string Key,
    string Namespace,
    int Id,
    string Label,
    string Kind,
    string? TableKey,
    bool IsVirtual,
    bool IsCode,
    bool IsMissing,
    bool IsFocus,
    int SourceRow,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Fields
);

public sealed record SkillChainEdge(
    string Id,
    string Source,
    string Target,
    string Role,
    string Label,
    string? Detail,
    bool Derived,
    string? SourceField,
    int? ParameterIndex
);

public sealed record SkillInboundReference(
    string Source,
    string SourceKind,
    string SourceLabel,
    string Relationship,
    string? SourceField,
    int? ParameterIndex,
    IReadOnlyDictionary<string, IReadOnlyList<string>> SourceFields
);

public sealed record SkillInboundReferenceSet(
    IReadOnlyList<SkillInboundReference> References,
    int TotalCount,
    bool Truncated
);

public sealed record WriteSmokeTestResult(
    string Status,
    string TableKey,
    string OutputWorkbookPath,
    int RecordCount,
    int ReReadRecordCount,
    bool FieldsMatch,
    string OriginalHash,
    string WrittenHash
);
