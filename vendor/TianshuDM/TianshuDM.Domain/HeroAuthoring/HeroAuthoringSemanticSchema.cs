namespace TianshuDM.Domain.HeroAuthoring;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification = "Integer is part of the versioned semantic-schema JSON contract.")]
public enum HeroAuthoringParameterKind
{
    Integer,
    ScaledInteger,
    Enum,
    Reference,
}

public enum HeroAuthoringIssueSeverity
{
    Warning,
    Error,
}

public sealed record HeroAuthoringParameterDefinition(
    int Index,
    string Key,
    string Label,
    HeroAuthoringParameterKind Kind,
    bool Required = true,
    string? ReferenceTarget = null,
    string? EnumName = null,
    int? Scale = null,
    bool Repeating = false,
    int RepeatStep = 1,
    string? Description = null,
    bool AllowsMultipleEnumValues = false);

public sealed record HeroAuthoringEnumOptionDefinition(string Code, string Label, int Value);

public sealed record HeroAuthoringEnumDefinition(
    string Name,
    bool Flags,
    IReadOnlyList<HeroAuthoringEnumOptionDefinition> Options);

public sealed record HeroAuthoringActionDefinition(
    string Key,
    int LegacyValue,
    string Label,
    int MinParameterCount,
    int? MaxParameterCount,
    IReadOnlyList<HeroAuthoringParameterDefinition> Parameters,
    string? Description = null,
    string? Warning = null);

public sealed record HeroAuthoringCodeTypeDefinition(
    string Key,
    int LegacyValue,
    string Label);

public sealed record HeroAuthoringSemanticSchema(
    int Version,
    IReadOnlyList<HeroAuthoringActionDefinition> Effects,
    IReadOnlyList<HeroAuthoringActionDefinition> Conditions,
    IReadOnlyList<HeroAuthoringCodeTypeDefinition> DamageStages,
    HeroAuthoringProjectRules? Rules = null,
    IReadOnlyList<HeroAuthoringCreationTemplateDefinition>? CreationTemplates = null);

public enum HeroAuthoringCreationMode
{
    Node,
    Group,
    GroupMember,
    ParameterNode,
    ParameterGroup,
}

public sealed record HeroAuthoringCreationTemplateDefinition(
    string Key,
    string Label,
    string ParentNamespace,
    string ParentField,
    string TargetNamespace,
    HeroAuthoringCreationMode Mode,
    IReadOnlyDictionary<string, IReadOnlyList<string>> InitialFields,
    string? ActionKey = null,
    int? ParameterIndex = null,
    string? ActionValue = null);

public sealed record HeroAuthoringProjectRules(int PlayerHeroId);

public sealed record HeroAuthoringEnumValue(string Key, int LegacyValue);

public sealed record HeroAuthoringCodeBinding(string Key, string SourcePath);

public sealed record HeroAuthoringCodeBindings(
    IReadOnlyList<HeroAuthoringEnumValue> EffectTypes,
    IReadOnlyList<HeroAuthoringCodeBinding> EffectHandlers,
    IReadOnlyList<HeroAuthoringEnumValue> ConditionTypes,
    IReadOnlyList<HeroAuthoringCodeBinding> ConditionHandlers,
    IReadOnlyList<HeroAuthoringEnumValue> DamageStageTypes,
    IReadOnlyList<HeroAuthoringCodeBinding> DamageStageHandlers);

public sealed record HeroAuthoringSchemaIssue(
    string Code,
    string Message,
    HeroAuthoringIssueSeverity Severity = HeroAuthoringIssueSeverity.Error,
    string? Subject = null);

public sealed record HeroAuthoringGraphNode(
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
    int SourceRow = 0);

public sealed record HeroAuthoringGraphEdge(
    string Id,
    string Source,
    string Target,
    string Role,
    string Label,
    string? Detail = null,
    bool Derived = false,
    string? SourceField = null,
    int? ParameterIndex = null);

public sealed record HeroAuthoringGraph(
    string FocusKey,
    IReadOnlyList<HeroAuthoringGraphNode> Nodes,
    IReadOnlyList<HeroAuthoringGraphEdge> Edges);
