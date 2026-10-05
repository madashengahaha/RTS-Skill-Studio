using System.Globalization;
using TianshuDM.Domain.GameData;
using TianshuDM.Domain.HeroAuthoring;

namespace TianshuDM.Application.HeroAuthoring;

public sealed record HeroAuthoringDraftIssue(
    string Code,
    string NodeKey,
    string Field,
    string Message);

public sealed class HeroAuthoringDraftValidator(IHeroAuthoringSemanticSchemaSource schemaSource)
{
    public IReadOnlyList<HeroAuthoringDraftIssue> Validate(
        GameDataCatalog catalog,
        IReadOnlyCollection<string>? tableKeys = null)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        HeroAuthoringSemanticSchema schema = schemaSource.Read();
        HashSet<string>? selected = tableKeys?.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var issues = new List<HeroAuthoringDraftIssue>();
        foreach (GameDataTable table in catalog.Tables.Where(table => selected is null || selected.Contains(table.Key)))
        {
            if (!HeroAuthoringGraphProjector.TryGetNamespace(table.Key, out string nodeNamespace)) continue;
            foreach (GameDataRecord record in table.Records)
            {
                ValidateExplicitReferences(catalog, table, record, nodeNamespace, issues);
                ValidateSkillReleaseConditions(catalog, table, record, nodeNamespace, issues);
            }
        }
        ValidateActions(catalog, "effect", schema.Effects, selected, issues);
        ValidateActions(catalog, "condition", schema.Conditions, selected, issues);
        return issues;
    }

    public void EnsureValid(GameDataCatalog catalog, IReadOnlyCollection<string>? tableKeys = null)
    {
        IReadOnlyList<HeroAuthoringDraftIssue> issues = Validate(catalog, tableKeys);
        if (issues.Count == 0) return;
        string detail = string.Join(
            Environment.NewLine,
            issues.Take(20).Select(issue => $"- {issue.NodeKey} · {issue.Field}：{issue.Message}"));
        string remainder = issues.Count > 20 ? $"{Environment.NewLine}- 另有 {issues.Count - 20} 项错误未展开。" : string.Empty;
        throw new InvalidDataException($"英雄配置语义校验失败，共 {issues.Count} 项：{Environment.NewLine}{detail}{remainder}");
    }

    public IReadOnlyList<HeroAuthoringDraftIssue> FindNewIssues(
        GameDataCatalog baseline,
        GameDataCatalog candidate,
        IReadOnlyCollection<string>? tableKeys = null)
    {
        HashSet<string> known = Validate(baseline, tableKeys)
            .Select(Fingerprint)
            .ToHashSet(StringComparer.Ordinal);
        return Validate(candidate, tableKeys)
            .Where(issue => !known.Contains(Fingerprint(issue)))
            .ToArray();
    }

    public void EnsureNoNewIssues(
        GameDataCatalog baseline,
        GameDataCatalog candidate,
        IReadOnlyCollection<string>? tableKeys = null)
    {
        IReadOnlyList<HeroAuthoringDraftIssue> issues = FindNewIssues(baseline, candidate, tableKeys);
        if (issues.Count == 0) return;
        string detail = string.Join(
            Environment.NewLine,
            issues.Take(20).Select(issue => $"- {issue.NodeKey} · {issue.Field}：{issue.Message}"));
        string remainder = issues.Count > 20 ? $"{Environment.NewLine}- 另有 {issues.Count - 20} 项错误未展开。" : string.Empty;
        throw new InvalidDataException($"本次修改新增了 {issues.Count} 项英雄配置语义错误：{Environment.NewLine}{detail}{remainder}");
    }

    private static string Fingerprint(HeroAuthoringDraftIssue issue) =>
        $"{issue.Code}\u001f{issue.NodeKey}\u001f{issue.Field}\u001f{issue.Message}";

    private static void ValidateExplicitReferences(
        GameDataCatalog catalog,
        GameDataTable table,
        GameDataRecord record,
        string nodeNamespace,
        List<HeroAuthoringDraftIssue> issues)
    {
        foreach (GameDataFieldDefinition field in table.Fields.Where(field => field.ReferenceTable is not null))
        {
            GameDataTable? target = catalog.Tables.FirstOrDefault(candidate =>
                string.Equals(candidate.Key, field.ReferenceTable, StringComparison.OrdinalIgnoreCase));
            if (target is null) continue;
            HashSet<int> ids = target.Records.Select(candidate => candidate.Id).ToHashSet();
            foreach (string value in Values(record, field.Key))
            {
                if (TryId(value, out int id) && !ids.Contains(id))
                {
                    issues.Add(new HeroAuthoringDraftIssue(
                        "MISSING_REFERENCE", $"{nodeNamespace}:{record.Id}", field.Key,
                        $"引用的 {target.DisplayName} {id} 不存在。"));
                }
            }
        }
    }

    private static void ValidateSkillReleaseConditions(
        GameDataCatalog catalog,
        GameDataTable table,
        GameDataRecord record,
        string nodeNamespace,
        List<HeroAuthoringDraftIssue> issues)
    {
        if (!string.Equals(table.Key, "skill", StringComparison.OrdinalIgnoreCase)) return;
        GameDataTable? conditions = catalog.Tables.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, "condition", StringComparison.OrdinalIgnoreCase));
        if (conditions is null) return;
        HashSet<int> conditionIds = conditions.Records.Select(candidate => candidate.Id).ToHashSet();
        foreach (string value in Values(record, "condition_id_array"))
        {
            if (TryId(value, out int conditionId) && !conditionIds.Contains(conditionId))
            {
                issues.Add(new HeroAuthoringDraftIssue(
                    "MISSING_REFERENCE", $"{nodeNamespace}:{record.Id}", "condition_id_array",
                    $"引用的{conditions.DisplayName} {conditionId} 不存在。"));
            }
        }
    }

    private static void ValidateActions(
        GameDataCatalog catalog,
        string tableKey,
        IReadOnlyList<HeroAuthoringActionDefinition> definitions,
        HashSet<string>? selected,
        List<HeroAuthoringDraftIssue> issues)
    {
        if (selected is not null && !selected.Contains(tableKey)) return;
        GameDataTable? table = catalog.Tables.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, tableKey, StringComparison.OrdinalIgnoreCase));
        if (table is null) return;
        GameDataFieldDefinition? typeField = table.Fields.FirstOrDefault(field => field.Key == "action_type");
        if (typeField is null) return;
        string nodeNamespace = tableKey == "effect" ? "TbEffect" : "TbCondition";
        foreach (GameDataRecord record in table.Records)
        {
            IReadOnlyList<string> typeValues = Values(record, "action_type");
            string typeValue = typeValues.Count > 0 ? typeValues[0] : string.Empty;
            HeroAuthoringActionDefinition? definition = ResolveAction(definitions, typeField, typeValue);
            if (definition is null)
            {
                issues.Add(new HeroAuthoringDraftIssue(
                    "UNKNOWN_ACTION_TYPE", $"{nodeNamespace}:{record.Id}", "action_type",
                    $"未找到“{typeValue}”对应的语义定义。"));
                continue;
            }
            string[] parameters = Values(record, "action_param")
                .TakeWhile((_, index) => index <= LastNonEmptyIndex(Values(record, "action_param")))
                .ToArray();
            if (parameters.Length < definition.MinParameterCount
                || definition.MaxParameterCount is { } max && parameters.Length > max)
            {
                issues.Add(new HeroAuthoringDraftIssue(
                    "ACTION_PARAMETER_COUNT", $"{nodeNamespace}:{record.Id}", "action_param",
                    $"{definition.Label}需要 {definition.MinParameterCount}"
                    + (definition.MaxParameterCount == definition.MinParameterCount ? string.Empty : $"–{definition.MaxParameterCount?.ToString(CultureInfo.InvariantCulture) ?? "不限"}")
                    + $" 个参数，当前为 {parameters.Length} 个。"));
            }
            foreach (HeroAuthoringParameterDefinition parameter in definition.Parameters)
            {
                IEnumerable<int> indexes = parameter.Repeating
                    ? Enumerable.Range(parameter.Index, Math.Max(0, parameters.Length - parameter.Index))
                        .Where(index => (index - parameter.Index) % parameter.RepeatStep == 0)
                    : [parameter.Index];
                foreach (int index in indexes)
                {
                    string value = index < parameters.Length ? parameters[index] : string.Empty;
                    ValidateParameter(catalog, record.Id, nodeNamespace, parameter, index, value, issues);
                }
            }
        }
    }

    private static void ValidateParameter(
        GameDataCatalog catalog,
        int recordId,
        string nodeNamespace,
        HeroAuthoringParameterDefinition parameter,
        int index,
        string value,
        List<HeroAuthoringDraftIssue> issues)
    {
        string field = $"action_param[{index}]";
        if (string.IsNullOrWhiteSpace(value))
        {
            if (parameter.Required)
            {
                issues.Add(new HeroAuthoringDraftIssue(
                    "MISSING_ACTION_PARAMETER", $"{nodeNamespace}:{recordId}", field,
                    $"参数“{parameter.Label}”不能为空。"));
            }
            return;
        }
        if (parameter.Kind is HeroAuthoringParameterKind.Integer or HeroAuthoringParameterKind.ScaledInteger
            && !int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            issues.Add(new HeroAuthoringDraftIssue(
                "INVALID_ACTION_PARAMETER", $"{nodeNamespace}:{recordId}", field,
                $"参数“{parameter.Label}”必须是整数。"));
            return;
        }
        if (parameter.Kind != HeroAuthoringParameterKind.Reference || !TryId(value, out int targetId)) return;
        bool exists = parameter.ReferenceTarget switch
        {
            "EffectGroup" or "ConditionGroup" => true,
            { } target when HeroAuthoringGraphProjector.TryGetTableKey(target, out string targetTable) =>
                catalog.Tables.FirstOrDefault(table => string.Equals(table.Key, targetTable, StringComparison.OrdinalIgnoreCase))
                    ?.Records.Any(record => record.Id == targetId) == true,
            _ => true,
        };
        if (!exists)
        {
            issues.Add(new HeroAuthoringDraftIssue(
                "MISSING_ACTION_REFERENCE", $"{nodeNamespace}:{recordId}", field,
                $"参数“{parameter.Label}”引用的配置 {targetId} 不存在。"));
        }
    }

    private static HeroAuthoringActionDefinition? ResolveAction(
        IReadOnlyList<HeroAuthoringActionDefinition> definitions,
        GameDataFieldDefinition typeField,
        string value)
    {
        GameDataOption? option = typeField.Options.FirstOrDefault(candidate =>
            string.Equals(candidate.Value, value, StringComparison.Ordinal)
            || string.Equals(candidate.Code, value, StringComparison.Ordinal));
        if (option?.LegacyValue is { } legacy)
            return definitions.FirstOrDefault(definition => definition.LegacyValue == legacy);
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
            return definitions.FirstOrDefault(definition => definition.LegacyValue == number);
        return definitions.FirstOrDefault(definition =>
            string.Equals(definition.Key, value, StringComparison.Ordinal)
            || string.Equals(definition.Label, value, StringComparison.Ordinal));
    }

    private static int LastNonEmptyIndex(IReadOnlyList<string> values)
    {
        for (int index = values.Count - 1; index >= 0; index--)
            if (!string.IsNullOrWhiteSpace(values[index])) return index;
        return -1;
    }

    private static IReadOnlyList<string> Values(GameDataRecord record, string field) =>
        record.Fields.TryGetValue(field, out IReadOnlyList<string>? values) ? values : [];

    private static bool TryId(string value, out int id) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out id) && id > 0;

}
