namespace TianshuDM.Domain.HeroAuthoring;

public static class HeroAuthoringBindingAudit
{
    public static IReadOnlyList<HeroAuthoringSchemaIssue> Audit(
        HeroAuthoringSemanticSchema schema,
        HeroAuthoringCodeBindings bindings)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(bindings);
        var issues = new List<HeroAuthoringSchemaIssue>();
        AuditCategory(
            "EFFECT",
            schema.Effects.Select(definition => definition.Key),
            bindings.EffectTypes,
            bindings.EffectHandlers,
            issues);
        AuditCategory(
            "CONDITION",
            schema.Conditions.Select(definition => definition.Key),
            bindings.ConditionTypes,
            bindings.ConditionHandlers,
            issues);
        AuditCategory(
            "DAMAGE_STAGE",
            schema.DamageStages.Select(definition => definition.Key),
            bindings.DamageStageTypes,
            bindings.DamageStageHandlers,
            issues);
        return issues;
    }

    private static void AuditCategory(
        string category,
        IEnumerable<string> semantics,
        IReadOnlyList<HeroAuthoringEnumValue> enumValues,
        IReadOnlyList<HeroAuthoringCodeBinding> handlers,
        List<HeroAuthoringSchemaIssue> issues)
    {
        HashSet<string> semanticKeys = semantics.ToHashSet(StringComparer.Ordinal);
        HashSet<string> enumKeys = enumValues.Select(value => value.Key).ToHashSet(StringComparer.Ordinal);
        HashSet<string> handlerKeys = handlers.Select(value => value.Key).ToHashSet(StringComparer.Ordinal);

        foreach (string key in enumKeys.Except(semanticKeys).Order(StringComparer.Ordinal))
        {
            issues.Add(Issue($"{category}_SEMANTICS_MISSING", $"{key} 缺少后台语义定义。", key));
        }

        foreach (string key in semanticKeys.Except(enumKeys).Order(StringComparer.Ordinal))
        {
            issues.Add(Issue($"{category}_ENUM_MISSING", $"{key} 在 Unity 枚举中不存在。", key));
        }

        foreach (string key in enumKeys.Except(handlerKeys).Order(StringComparer.Ordinal))
        {
            issues.Add(Issue($"{category}_HANDLER_MISSING", $"{key} 没有对应的 Unity 处理器。", key));
        }

        foreach (string key in handlerKeys.Except(enumKeys).Order(StringComparer.Ordinal))
        {
            issues.Add(Issue($"{category}_HANDLER_UNKNOWN", $"处理器 {key} 没有对应的 Unity 枚举值。", key));
        }
    }

    private static HeroAuthoringSchemaIssue Issue(string code, string message, string subject) =>
        new(code, message, HeroAuthoringIssueSeverity.Error, subject);
}
