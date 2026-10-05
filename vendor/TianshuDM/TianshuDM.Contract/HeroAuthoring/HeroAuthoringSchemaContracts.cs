namespace TianshuDM.Contract.HeroAuthoring;

public sealed record HeroAuthoringCreationTemplateResponse(
    string Key,
    string Label,
    string ParentNamespace,
    string ParentField,
    string TargetNamespace,
    string Mode,
    string? ActionKey,
    int? ParameterIndex,
    string? ActionValue);

public sealed record HeroAuthoringCreationTemplatesResponse(
    int Version,
    IReadOnlyList<HeroAuthoringCreationTemplateResponse> Items);

public sealed record CreateHeroAuthoringFromTemplateRequest(
    string TemplateKey,
    string ParentNamespace,
    int ParentId,
    string? ExpectedRevision = null);

public sealed record CreateHeroAuthoringSkillRequest(
    int HeroId,
    string Category,
    int? UnlockLevel = null,
    string? ExpectedRevision = null);

public sealed record HeroAuthoringUpgradeLevelOptionResponse(
    int UpgradeId,
    int Level,
    bool Available,
    int? UnlockItemId);

public sealed record HeroAuthoringSkillCreationOptionsResponse(
    IReadOnlyList<HeroAuthoringUpgradeLevelOptionResponse> UpgradeLevels);

public sealed record CreateHeroAuthoringAssetRequest(
    string NodeNamespace,
    IReadOnlyDictionary<string, string[]> InitialFields,
    string? ExpectedRevision = null);

public sealed record CreateHeroAuthoringAssetFromTemplateRequest(
    string TemplateKey,
    IReadOnlyDictionary<string, string[]> InitialFields,
    string? ExpectedRevision = null);

public sealed record CreateHeroAuthoringStandaloneGroupFromTemplateRequest(
    string TemplateKey,
    IReadOnlyDictionary<string, string[]> InitialFields,
    string? ExpectedRevision = null);

public sealed record HeroAuthoringNodeSummaryResponse(
    string Key,
    string Namespace,
    int LegacyId,
    string Label,
    string Summary,
    string Kind,
    string TableKey);

public sealed record HeroAuthoringAssetCategoryResponse(
    string Namespace,
    string Label,
    int Count);

public sealed record HeroAuthoringAssetResponse(
    string Key,
    string Namespace,
    int LegacyId,
    string Label,
    string Summary,
    string Kind,
    string? TableKey,
    string WorkbookPath,
    string WorksheetName,
    int SourceRow,
    int IncomingReferenceCount,
    int OutgoingReferenceCount,
    int SourceUsageCount,
    int EffectiveReferenceCount,
    int OwnershipScopeCount,
    bool IsVirtual,
    bool IsMissing,
    bool IsOrphan,
    bool IsShared);

public sealed record HeroAuthoringAssetCatalogResponse(
    string Revision,
    int Total,
    IReadOnlyList<HeroAuthoringAssetCategoryResponse> Categories,
    IReadOnlyList<HeroAuthoringAssetResponse> Items);

public sealed record HeroAuthoringPortResponse(
    string Key,
    string Label,
    string Direction,
    string BindingKind,
    string Cardinality,
    bool Required,
    string? SourceField,
    int? ParameterIndex,
    IReadOnlyList<string> AcceptsNamespaces,
    IReadOnlyList<string> ConnectedAssetKeys);

public sealed record HeroAuthoringWorkspaceNodeResponse(
    HeroAuthoringAssetResponse Asset,
    IReadOnlyList<HeroAuthoringPortResponse> Ports);

public sealed record HeroAuthoringSkillWorkspaceResponse(
    string Revision,
    string FocusKey,
    IReadOnlyList<HeroAuthoringWorkspaceNodeResponse> Nodes,
    IReadOnlyList<HeroAuthoringGraphEdgeResponse> Edges);

public sealed record HeroAuthoringNodeLayoutResponse(string InstanceKey, double X, double Y);

public sealed record HeroAuthoringLayoutResponse(
    string Mode,
    IReadOnlyList<HeroAuthoringNodeLayoutResponse> Positions);

public sealed record SaveHeroAuthoringLayoutRequest(
    IReadOnlyList<HeroAuthoringNodeLayoutResponse> Positions);

public sealed record HeroAuthoringActionSchemasResponse(
    int Version,
    IReadOnlyList<HeroAuthoringActionSchemaResponse> Effects,
    IReadOnlyList<HeroAuthoringActionSchemaResponse> Conditions,
    IReadOnlyDictionary<string, HeroAuthoringEnumDefinitionResponse> EnumOptions);

public sealed record HeroAuthoringEnumDefinitionResponse(
    bool Flags,
    IReadOnlyList<HeroAuthoringEnumOptionResponse> Options);

public sealed record HeroAuthoringEnumOptionResponse(string Code, string Label, int Value);

public sealed record HeroAuthoringActionSchemaResponse(
    string Key,
    int LegacyValue,
    string Label,
    int MinParameterCount,
    int? MaxParameterCount,
    IReadOnlyList<HeroAuthoringParameterSchemaResponse> Parameters,
    string? Description,
    string? Warning);

public sealed record HeroAuthoringParameterSchemaResponse(
    int Index,
    string Key,
    string Label,
    string Kind,
    bool Required,
    string? ReferenceTarget,
    string? EnumName,
    int? Scale,
    bool Repeating,
    int RepeatStep,
    string? Description,
    bool AllowsMultipleEnumValues);

public sealed record HeroAuthoringSchemaStatusResponse(
    string Status,
    int SchemaVersion,
    int EffectTypeCount,
    int ConditionTypeCount,
    int DamageStageTypeCount,
    IReadOnlyList<HeroAuthoringSchemaIssueResponse> Issues);

public sealed record HeroAuthoringSchemaIssueResponse(
    string Code,
    string Message,
    string Severity,
    string? Subject);

public sealed record HeroAuthoringHeroSummaryResponse(
    int Id,
    string Name,
    int BaseSkillCount,
    bool HasRuntimeUnusedActiveSkills);

public sealed record HeroAuthoringSourceTypeResponse(
    string Key,
    string Label,
    int ObjectCount);

public sealed record HeroAuthoringSourceTypesResponse(
    string Revision,
    IReadOnlyList<HeroAuthoringSourceTypeResponse> Items);

public sealed record HeroAuthoringSourceObjectResponse(
    string Key,
    int LegacyId,
    string Label,
    string Summary);

public sealed record HeroAuthoringSourceObjectsResponse(
    string SourceType,
    IReadOnlyList<HeroAuthoringSourceObjectResponse> Items);

public sealed record HeroAuthoringRootProfileResponse(
    string Namespace,
    string Key,
    string DisplayName,
    string Kind,
    bool CanBeRoot,
    bool CanCreate,
    bool ShowInSourceBrowser,
    string HistoryScopePrefix,
    string? LayoutScopePrefix);

public sealed record HeroAuthoringRootProfilesResponse(
    IReadOnlyList<HeroAuthoringRootProfileResponse> Items);

public sealed record HeroAuthoringBehaviorRootResponse(
    string Key,
    string Namespace,
    int LegacyId,
    string Label,
    string Summary,
    string Category,
    string CategoryLabel,
    int IncomingReferenceCount,
    bool IsUnreferenced);

public sealed record HeroAuthoringSourceRootsResponse(
    string Revision,
    string SourceType,
    string SourceTypeLabel,
    string SourceObjectKey,
    string SourceObjectLabel,
    IReadOnlyList<HeroAuthoringBehaviorRootResponse> Items,
    IReadOnlyList<string> Warnings);

public sealed record HeroAuthoringGraphResponse(
    string Revision,
    string FocusKey,
    IReadOnlyList<HeroAuthoringGraphNodeResponse> Nodes,
    IReadOnlyList<HeroAuthoringGraphEdgeResponse> Edges);

public sealed record HeroAuthoringGraphNodeResponse(
    string Key,
    string Namespace,
    int LegacyId,
    string Label,
    string Kind,
    string? TableKey,
    bool IsVirtual,
    bool IsCode,
    bool IsMissing,
    bool IsFocus,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Fields,
    int SourceRow);

public sealed record HeroAuthoringGraphEdgeResponse(
    string Id,
    string Source,
    string Target,
    string Role,
    string Label,
    string? Detail,
    bool Derived,
    string? SourceField,
    int? ParameterIndex);

public sealed record UpdateHeroAuthoringNodeRequest(
    IReadOnlyDictionary<string, string[]> Fields,
    string? ExpectedRevision = null);

public sealed record CreateAndLinkHeroAuthoringNodeRequest(
    string ParentNamespace,
    int ParentId,
    string ParentField,
    string TargetNamespace,
    IReadOnlyDictionary<string, string[]> InitialFields,
    string? ExpectedRevision = null);

public sealed record CreateHeroAuthoringGroupRequest(
    string ParentNamespace,
    int ParentId,
    string ParentField,
    string GroupNamespace,
    IReadOnlyDictionary<string, string[]> InitialMemberFields,
    string? ExpectedRevision = null);

public sealed record ChangeHeroAuthoringLinkRequest(
    string ParentNamespace,
    int ParentId,
    string ParentField,
    string TargetNamespace,
    int TargetId,
    string? ExpectedRevision = null,
    int? ParameterIndex = null);

public sealed record DeleteHeroAuthoringGroupMemberRequest(
    string GroupNamespace,
    int GroupId,
    string MemberNamespace,
    int MemberId,
    string? ExpectedRevision = null);

public sealed record ReorderHeroAuthoringGroupRequest(
    string GroupNamespace,
    int GroupId,
    IReadOnlyList<int> OrderedMemberIds,
    string? ExpectedRevision = null);

public sealed record DeleteHeroAuthoringNodeRequest(
    string Namespace,
    int Id,
    string? ExpectedRevision = null);

public sealed record DeleteHeroAuthoringBranchRequest(
    string ParentNamespace,
    int ParentId,
    string ParentField,
    string TargetNamespace,
    int TargetId,
    string? ExpectedRevision = null,
    int? ParameterIndex = null);

public sealed record DeleteHeroAuthoringRootBranchRequest(
    string RootNamespace,
    int RootId,
    string? ExpectedRevision = null);

public sealed record HeroAuthoringCommandResponse(
    string Revision,
    IReadOnlyList<string> ChangedNodeKeys,
    int? CreatedId,
    int? CreatedGroupId,
    IReadOnlyList<string> PreservedNodeKeys);

public sealed record HeroAuthoringHistoryStatusResponse(
    string Revision,
    bool CanUndo,
    bool CanRedo,
    int UndoDepth,
    int RedoDepth);

public sealed record HeroAuthoringDraftIssueResponse(
    string Code,
    string NodeKey,
    string Field,
    string Message);

public sealed record HeroAuthoringDraftValidationResponse(
    bool Valid,
    IReadOnlyList<HeroAuthoringDraftIssueResponse> Issues,
    int KnownIssueCount);
