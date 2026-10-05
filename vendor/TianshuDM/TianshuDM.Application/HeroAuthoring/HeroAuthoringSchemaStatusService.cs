using TianshuDM.Domain.HeroAuthoring;

namespace TianshuDM.Application.HeroAuthoring;

public interface IHeroAuthoringSemanticSchemaSource
{
    HeroAuthoringSemanticSchema Read();
}

public interface IHeroAuthoringCodeBindingSource
{
    HeroAuthoringCodeBindings Scan(string unityProjectRoot);
}

public interface IHeroAuthoringEnumOptionSource
{
    IReadOnlyDictionary<string, HeroAuthoringEnumDefinition> Read(
        string unityProjectRoot,
        IReadOnlyCollection<string> enumNames);
}

public sealed record HeroAuthoringSchemaStatus(
    string Status,
    int SchemaVersion,
    int EffectTypeCount,
    int ConditionTypeCount,
    int DamageStageTypeCount,
    IReadOnlyList<HeroAuthoringSchemaIssue> Issues);

public sealed class HeroAuthoringSchemaStatusService(
    IHeroAuthoringSemanticSchemaSource schemaSource,
    IHeroAuthoringCodeBindingSource bindingSource)
{
    public HeroAuthoringSchemaStatus Inspect(string unityProjectRoot)
    {
        HeroAuthoringSemanticSchema schema = schemaSource.Read();
        HeroAuthoringCodeBindings bindings = bindingSource.Scan(unityProjectRoot);
        HeroAuthoringSchemaIssue[] issues = HeroAuthoringSchemaValidator.Validate(schema)
            .Concat(HeroAuthoringBindingAudit.Audit(schema, bindings))
            .ToArray();
        return new HeroAuthoringSchemaStatus(
            issues.Any(issue => issue.Severity == HeroAuthoringIssueSeverity.Error) ? "invalid" : "valid",
            schema.Version,
            bindings.EffectTypes.Count,
            bindings.ConditionTypes.Count,
            bindings.DamageStageTypes.Count,
            issues);
    }
}
