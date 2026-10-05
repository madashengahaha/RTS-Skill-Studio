using System.Globalization;
using TianshuDM.Application.GameData;
using TianshuDM.Domain.GameData;
using TianshuDM.Domain.HeroAuthoring;

namespace TianshuDM.Application.HeroAuthoring;

public sealed record HeroAuthoringCreationTemplateResult(
    HeroAuthoringCreationTemplateDefinition Template,
    HeroAuthoringCommandResult Command);

public sealed record HeroAuthoringUpgradeLevelOption(
    int UpgradeId,
    int Level,
    bool Available,
    int? UnlockItemId);

public sealed class HeroAuthoringCreationTemplateService(
    IHeroAuthoringSemanticSchemaSource schemaSource,
    HeroAuthoringCommandService commands,
    IGameDataDraftStore store)
{
    public int Version => schemaSource.Read().Version;

    public IReadOnlyList<HeroAuthoringCreationTemplateDefinition> List(string? parentNamespace = null) =>
        BuildTemplates(schemaSource.Read())
        .Where(template => string.IsNullOrWhiteSpace(parentNamespace)
                           || string.Equals(template.ParentNamespace, parentNamespace, StringComparison.Ordinal))
        .OrderBy(template => template.ParentNamespace, StringComparer.Ordinal)
        .ThenBy(template => template.Key, StringComparer.Ordinal)
        .ToArray();

    public IReadOnlyList<HeroAuthoringUpgradeLevelOption> ListUpgradeLevels(int heroId)
    {
        GameDataCatalog catalog = store.ReadGameDataCatalog()
                                  ?? throw new InvalidOperationException("尚未加载英雄配置数据。");
        catalog.Table("hero").Record(heroId);
        GameDataTable? upgrades = catalog.Tables.FirstOrDefault(table =>
            string.Equals(table.Key, "hero-upgrade", StringComparison.OrdinalIgnoreCase));
        if (upgrades is null) return [];
        return upgrades.Records
            .Where(record => FirstId(record, "group_id") == heroId)
            .Select(record =>
            {
                int unlockItemId = FirstId(record, "UnlockItem");
                return new HeroAuthoringUpgradeLevelOption(
                    record.Id,
                    FirstId(record, "level"),
                    unlockItemId <= 0,
                    unlockItemId > 0 ? unlockItemId : null);
            })
            .Where(option => option.Level > 0)
            .OrderBy(option => option.Level)
            .ToArray();
    }

    public HeroAuthoringCreationTemplateResult CreateHeroSkill(
        int heroId,
        string category,
        int? unlockLevel = null,
        string? expectedRevision = null)
    {
        HeroAuthoringSemanticSchema schema = schemaSource.Read();
        HeroAuthoringCreationTemplateDefinition template = BuildTemplates(schema).FirstOrDefault(candidate =>
            candidate.Key == "hero-skill"
            && candidate.ParentNamespace == "TbHero"
            && candidate.TargetNamespace == "TbSkill")
            ?? throw new InvalidDataException("英雄技能创建模板 hero-skill 不存在。");
        IReadOnlyDictionary<string, IReadOnlyList<string>> initialFields = PrepareInitialFields(template, schema);
        HeroAuthoringCommandResult command = category switch
        {
            "base" => commands.CreateAndLink(
                "TbHero",
                heroId,
                template.ParentField,
                "TbSkill",
                initialFields,
                expectedRevision),
            "upgrade" when unlockLevel is > 0 => commands.CreateUpgradeSkill(
                heroId,
                unlockLevel.Value,
                initialFields,
                expectedRevision),
            "upgrade" => throw new ArgumentException("创建升级解锁技能时必须选择解锁等级。", nameof(unlockLevel)),
            _ => throw new ArgumentException("技能归属类型只能是 base 或 upgrade。", nameof(category)),
        };
        return new HeroAuthoringCreationTemplateResult(template, command);
    }

    public HeroAuthoringCreationTemplateResult Create(
        string templateKey,
        string parentNamespace,
        int parentId,
        string? expectedRevision = null)
    {
        HeroAuthoringSemanticSchema schema = schemaSource.Read();
        HeroAuthoringCreationTemplateDefinition template =
            BuildTemplates(schema).FirstOrDefault(
                candidate => string.Equals(candidate.Key, templateKey, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"不存在创建模板“{templateKey}”。");
        if (!string.Equals(template.ParentNamespace, parentNamespace, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"创建模板“{template.Label}”不适用于 {parentNamespace}。",
                nameof(parentNamespace));
        }

        IReadOnlyDictionary<string, IReadOnlyList<string>> initialFields = PrepareInitialFields(template, schema);
        ValidateParentAction(template, schema, parentId);
        int? parameterIndex = template.Mode is HeroAuthoringCreationMode.ParameterNode or HeroAuthoringCreationMode.ParameterGroup
            ? ResolveParameterIndex(template, schema, parentId)
            : null;
        HeroAuthoringCommandResult result = template.Mode switch
        {
            HeroAuthoringCreationMode.Node => commands.CreateAndLink(
                parentNamespace,
                parentId,
                template.ParentField,
                template.TargetNamespace,
                initialFields,
                expectedRevision),
            HeroAuthoringCreationMode.Group => commands.CreateGroupAndMember(
                parentNamespace,
                parentId,
                template.ParentField,
                template.TargetNamespace,
                initialFields,
                expectedRevision),
            HeroAuthoringCreationMode.GroupMember => commands.CreateGroupMember(
                parentNamespace,
                parentId,
                template.TargetNamespace,
                initialFields,
                expectedRevision),
            HeroAuthoringCreationMode.ParameterNode => commands.CreateParameterNode(
                parentNamespace,
                parentId,
                template.ParentField,
                parameterIndex ?? throw new InvalidDataException($"创建模板“{template.Label}”缺少参数位置。"),
                template.TargetNamespace,
                initialFields,
                expectedRevision),
            HeroAuthoringCreationMode.ParameterGroup => commands.CreateParameterGroup(
                parentNamespace,
                parentId,
                template.ParentField,
                parameterIndex ?? throw new InvalidDataException($"创建模板“{template.Label}”缺少参数位置。"),
                template.TargetNamespace,
                expectedRevision),
            _ => throw new InvalidDataException($"创建模板“{template.Label}”包含未知模式 {template.Mode}。"),
        };
        return new HeroAuthoringCreationTemplateResult(template, result);
    }

    public HeroAuthoringCreationTemplateResult CreateStandaloneFromTemplate(
        string templateKey,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? initialFields = null,
        string? expectedRevision = null)
    {
        HeroAuthoringSemanticSchema schema = schemaSource.Read();
        HeroAuthoringCreationTemplateDefinition template =
            BuildTemplates(schema).FirstOrDefault(candidate =>
                string.Equals(candidate.Key, templateKey, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"不存在创建模板“{templateKey}”。");
        if (template.Mode != HeroAuthoringCreationMode.Node)
        {
            throw new ArgumentException($"创建模板“{template.Label}”不是独立节点模板。", nameof(templateKey));
        }

        Dictionary<string, IReadOnlyList<string>> fields = PrepareInitialFields(template, schema)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach ((string key, IReadOnlyList<string> value) in initialFields ?? new Dictionary<string, IReadOnlyList<string>>())
        {
            if (!string.Equals(key, "Id", StringComparison.OrdinalIgnoreCase))
            {
                fields[key] = value;
            }
        }
        HeroAuthoringCommandResult command = commands.CreateAsset(
            template.TargetNamespace,
            fields,
            expectedRevision);
        return new HeroAuthoringCreationTemplateResult(template, command);
    }

    public HeroAuthoringCreationTemplateResult CreateStandaloneGroupFromTemplate(
        string templateKey,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? initialFields = null,
        string? expectedRevision = null)
    {
        HeroAuthoringSemanticSchema schema = schemaSource.Read();
        HeroAuthoringCreationTemplateDefinition template =
            BuildTemplates(schema).FirstOrDefault(candidate =>
                string.Equals(candidate.Key, templateKey, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"不存在创建模板“{templateKey}”。");
        if (template.Mode != HeroAuthoringCreationMode.GroupMember
            || template.ParentNamespace is not ("EffectGroup" or "ConditionGroup"))
        {
            throw new ArgumentException($"创建模板“{template.Label}”不是独立组模板。", nameof(templateKey));
        }

        Dictionary<string, IReadOnlyList<string>> fields = PrepareInitialFields(template, schema)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach ((string key, IReadOnlyList<string> value) in initialFields ?? new Dictionary<string, IReadOnlyList<string>>())
        {
            if (!string.Equals(key, "Id", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(key, "group_id", StringComparison.OrdinalIgnoreCase))
            {
                fields[key] = value;
            }
        }
        HeroAuthoringCommandResult command = commands.CreateStandaloneGroup(
            template.ParentNamespace,
            fields,
            expectedRevision);
        return new HeroAuthoringCreationTemplateResult(template, command);
    }

    private HeroAuthoringCreationTemplateDefinition[] BuildTemplates(HeroAuthoringSemanticSchema schema)
    {
        GameDataCatalog? catalog = store.ReadGameDataCatalog();
        return (schema.CreationTemplates ?? [])
            .Concat(ActionTemplates(schema.Effects, "effect", "EffectGroup", "TbEffect"))
            .Concat(ActionTemplates(schema.Conditions, "condition", "ConditionGroup", "TbCondition"))
            .Concat(ConditionBranchTemplates(schema.Conditions, "success", "success_conds_group_id"))
            .Concat(ConditionBranchTemplates(schema.Conditions, "failure", "failure_conds_group_id"))
            .Concat(ParameterNodeTemplates(schema.Effects, "effect", "TbEffect"))
            .Concat(ParameterNodeTemplates(schema.Conditions, "condition", "TbCondition"))
            .Select(template => WithActionValue(template, schema, catalog))
            .ToArray();
    }

    private static IEnumerable<HeroAuthoringCreationTemplateDefinition> ConditionBranchTemplates(
        IReadOnlyList<HeroAuthoringActionDefinition> actions,
        string branchKey,
        string parentField) =>
        actions.Select(action => new HeroAuthoringCreationTemplateDefinition(
            $"effect-{branchKey}-condition-{action.Key}",
            action.Label,
            "TbEffect",
            parentField,
            "ConditionGroup",
            HeroAuthoringCreationMode.Group,
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["action_type"] = [action.Key],
                ["action_param"] = Enumerable.Repeat("0", action.MinParameterCount).ToArray(),
            },
            action.Key));

    private static IEnumerable<HeroAuthoringCreationTemplateDefinition> ActionTemplates(
        IReadOnlyList<HeroAuthoringActionDefinition> actions,
        string keyPrefix,
        string parentNamespace,
        string targetNamespace) =>
        actions.Select(action => new HeroAuthoringCreationTemplateDefinition(
            $"{keyPrefix}-group-{action.Key}",
            action.Label,
            parentNamespace,
            "group_id",
            targetNamespace,
            HeroAuthoringCreationMode.GroupMember,
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["action_type"] = [action.Key],
                ["action_param"] = Enumerable.Repeat("0", action.MinParameterCount).ToArray(),
            },
            action.Key));

    private static IEnumerable<HeroAuthoringCreationTemplateDefinition> ParameterNodeTemplates(
        IReadOnlyList<HeroAuthoringActionDefinition> actions,
        string keyPrefix,
        string parentNamespace) =>
        actions.SelectMany(action => action.Parameters
            .Where(parameter => parameter.Kind == HeroAuthoringParameterKind.Reference
                                && parameter.ReferenceTarget is "TbBuff" or "TbBullet" or "TbTrap"
                                    or "TbSearch" or "TbSkillResource" or "ConditionGroup" or "EffectGroup")
            .Select(parameter => new HeroAuthoringCreationTemplateDefinition(
                $"{keyPrefix}-{action.Key}-parameter-{parameter.Index}",
                $"新增并引用{parameter.Label}",
                parentNamespace,
                "action_param",
                parameter.ReferenceTarget!,
                parameter.ReferenceTarget is "ConditionGroup" or "EffectGroup"
                    ? HeroAuthoringCreationMode.ParameterGroup
                    : HeroAuthoringCreationMode.ParameterNode,
                new Dictionary<string, IReadOnlyList<string>>(),
                action.Key,
                parameter.Index)));

    private Dictionary<string, IReadOnlyList<string>> PrepareInitialFields(
        HeroAuthoringCreationTemplateDefinition template,
        HeroAuthoringSemanticSchema schema)
    {
        var fields = template.InitialFields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (template.Mode is HeroAuthoringCreationMode.Node or HeroAuthoringCreationMode.ParameterNode)
        {
            ApplyParameterNodeDefaults(template, fields);
            if (template.Mode == HeroAuthoringCreationMode.ParameterNode) return fields;
        }
        if (template.Mode == HeroAuthoringCreationMode.ParameterGroup) return fields;
        if (template.Mode is not (HeroAuthoringCreationMode.Node
                or HeroAuthoringCreationMode.GroupMember
                or HeroAuthoringCreationMode.Group)
            || string.IsNullOrWhiteSpace(template.ActionKey))
        {
            return fields;
        }

        GameDataCatalog catalog = store.ReadGameDataCatalog()
                                  ?? throw new InvalidOperationException("尚未加载英雄配置数据。");
        string tableKey = template.TargetNamespace is "TbEffect" or "EffectGroup" ? "effect" : "condition";
        GameDataTable table = catalog.Table(tableKey);
        HeroAuthoringActionDefinition action = ActionsFor(template, schema).Single(
            candidate => string.Equals(candidate.Key, template.ActionKey, StringComparison.Ordinal));
        GameDataFieldDefinition typeField = table.Fields.Single(field => field.Key == "action_type");
        GameDataOption? option = typeField.Options.FirstOrDefault(candidate =>
            string.Equals(candidate.Code, action.Key, StringComparison.Ordinal)
            || candidate.LegacyValue == action.LegacyValue);
        if (option is null)
        {
            throw new InvalidDataException($"{table.DisplayName}的操作枚举中找不到 {action.Key}（{action.LegacyValue}）。");
        }
        fields["action_type"] = [option.Value];
        if (table.Fields.Any(field => field.Key == "action_param") && !fields.ContainsKey("action_param"))
        {
            fields["action_param"] = Enumerable.Repeat("0", action.MinParameterCount).ToArray();
        }
        if (table.Fields.Any(field => field.Key == "name")) fields["name"] = [$"新{action.Label}"];
        return fields;
    }

    private void ApplyParameterNodeDefaults(
        HeroAuthoringCreationTemplateDefinition template,
        IDictionary<string, IReadOnlyList<string>> fields)
    {
        GameDataCatalog catalog = store.ReadGameDataCatalog()
                                  ?? throw new InvalidOperationException("尚未加载英雄配置数据。");
        if (!HeroAuthoringGraphProjector.TryGetTableKey(template.TargetNamespace, out string tableKey)) return;
        GameDataTable table = catalog.Table(tableKey);
        Dictionary<string, IReadOnlyList<string>> defaults = template.TargetNamespace switch
        {
            "TbBuff" => new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["name"] = ["新Buff"],
                ["duration"] = ["0"],
                ["delay"] = ["0"],
                ["interval"] = ["0"],
                ["max_layer"] = ["1"],
            },
            "TbBullet" => new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["name"] = ["新子弹"],
                ["hor_speed"] = ["100000"],
                ["control_factor"] = ["5000"],
                ["control_height"] = ["40000"],
                ["life"] = ["10000"],
            },
            "TbSearch" => new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["name"] = ["新搜索配置"],
                ["__remark_2"] = ["新搜索配置"],
            },
            "TbSkillResource" => new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["name"] = ["新技能资源"],
            },
            "TbTrap" => new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["name"] = ["新机关"],
            },
            _ => new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal),
        };
        foreach (GameDataFieldDefinition field in table.Fields.Where(field => field.Key != "Id" && field.Required))
        {
            IReadOnlyList<string> fallback = field.Kind switch
            {
                GameDataFieldKind.Boolean => ["false"],
                GameDataFieldKind.Enum when field.Options.Count > 0 => [field.Options[0].Value],
                GameDataFieldKind.List or GameDataFieldKind.Map or GameDataFieldKind.DelimitedList => [],
                GameDataFieldKind.Text => [$"新{table.DisplayName}"],
                _ => ["0"],
            };
            defaults.TryAdd(field.Key, fallback);
        }
        foreach (KeyValuePair<string, IReadOnlyList<string>> value in defaults)
        {
            if (table.Fields.Any(field => field.Key == value.Key)) fields.TryAdd(value.Key, value.Value);
        }
    }

    private static HeroAuthoringCreationTemplateDefinition WithActionValue(
        HeroAuthoringCreationTemplateDefinition template,
        HeroAuthoringSemanticSchema schema,
        GameDataCatalog? catalog)
    {
        if (string.IsNullOrWhiteSpace(template.ActionKey)) return template;
        HeroAuthoringActionDefinition? action = ActionsFor(template, schema).FirstOrDefault(candidate =>
            string.Equals(candidate.Key, template.ActionKey, StringComparison.Ordinal));
        if (action is null) return template;
        string tableKey = UsesEffectActions(template) ? "effect" : "condition";
        GameDataFieldDefinition? typeField = catalog?.Tables.FirstOrDefault(table =>
                string.Equals(table.Key, tableKey, StringComparison.OrdinalIgnoreCase))
            ?.Fields.FirstOrDefault(field => field.Key == "action_type");
        GameDataOption? option = typeField?.Options.FirstOrDefault(candidate =>
            string.Equals(candidate.Code, action.Key, StringComparison.Ordinal)
            || candidate.LegacyValue == action.LegacyValue);
        return template with { ActionValue = option?.Value ?? action.Key };
    }

    private void ValidateParentAction(
        HeroAuthoringCreationTemplateDefinition template,
        HeroAuthoringSemanticSchema schema,
        int parentId)
    {
        if (template.Mode is not (HeroAuthoringCreationMode.ParameterNode or HeroAuthoringCreationMode.ParameterGroup)
            || string.IsNullOrWhiteSpace(template.ActionKey)) return;
        GameDataCatalog catalog = store.ReadGameDataCatalog()
                                  ?? throw new InvalidOperationException("尚未加载英雄配置数据。");
        string parentTableKey = template.ParentNamespace == "TbEffect" ? "effect" : "condition";
        GameDataTable table = catalog.Table(parentTableKey);
        GameDataRecord parent = table.Record(parentId);
        IReadOnlyList<string> typeValues = parent.Fields.GetValueOrDefault("action_type", []);
        string rawType = typeValues.Count > 0 ? typeValues[0] : string.Empty;
        GameDataFieldDefinition typeField = table.Fields.Single(field => field.Key == "action_type");
        GameDataOption? option = typeField.Options.FirstOrDefault(candidate =>
            string.Equals(candidate.Value, rawType, StringComparison.Ordinal)
            || string.Equals(candidate.Code, rawType, StringComparison.Ordinal));
        HeroAuthoringActionDefinition? actual = ActionsFor(template, schema).FirstOrDefault(action =>
            string.Equals(action.Key, rawType, StringComparison.Ordinal)
            || string.Equals(action.Label, rawType, StringComparison.Ordinal)
            || option?.Code == action.Key
            || option?.LegacyValue == action.LegacyValue);
        if (!string.Equals(actual?.Key, template.ActionKey, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"创建模板“{template.Label}”只适用于 {template.ActionKey}，当前操作是“{rawType}”。",
                nameof(parentId));
        }
    }

    private int ResolveParameterIndex(
        HeroAuthoringCreationTemplateDefinition template,
        HeroAuthoringSemanticSchema schema,
        int parentId)
    {
        int baseIndex = template.ParameterIndex
                        ?? throw new InvalidDataException($"创建模板“{template.Label}”缺少参数位置。");
        HeroAuthoringActionDefinition action = ActionsFor(template, schema).Single(candidate =>
            string.Equals(candidate.Key, template.ActionKey, StringComparison.Ordinal));
        HeroAuthoringParameterDefinition parameter = action.Parameters.Single(candidate => candidate.Index == baseIndex);
        GameDataCatalog catalog = store.ReadGameDataCatalog()
                                  ?? throw new InvalidOperationException("尚未加载英雄配置数据。");
        string parentTableKey = template.ParentNamespace == "TbEffect" ? "effect" : "condition";
        GameDataRecord parent = catalog.Table(parentTableKey).Record(parentId);
        IReadOnlyList<string> values = parent.Fields.GetValueOrDefault(template.ParentField, []);

        if (!parameter.Repeating)
        {
            if (HasConfiguredReference(values, baseIndex))
            {
                throw new InvalidOperationException(
                    $"“{action.Label}”的{parameter.Label}已经引用 {values[baseIndex]}；如需更换，请在参数表单中明确选择新配置，不能通过“新增”覆盖。");
            }
            return baseIndex;
        }

        int step = Math.Max(1, parameter.RepeatStep);
        int index = baseIndex;
        while (HasConfiguredReference(values, index))
        {
            index += step;
            if (action.MaxParameterCount is int maxCount && index >= maxCount)
            {
                throw new InvalidOperationException($"“{action.Label}”的{parameter.Label}已达到最多 {maxCount} 个参数，无法继续新增。");
            }
        }
        return index;
    }

    private static bool HasConfiguredReference(IReadOnlyList<string> values, int index) =>
        index < values.Count
        && !string.IsNullOrWhiteSpace(values[index])
        && !string.Equals(values[index], "0", StringComparison.Ordinal);

    private static IReadOnlyList<HeroAuthoringActionDefinition> ActionsFor(
        HeroAuthoringCreationTemplateDefinition template,
        HeroAuthoringSemanticSchema schema) =>
        UsesEffectActions(template) ? schema.Effects : schema.Conditions;

    private static bool UsesEffectActions(HeroAuthoringCreationTemplateDefinition template) =>
        (template.Mode is HeroAuthoringCreationMode.ParameterNode or HeroAuthoringCreationMode.ParameterGroup)
        && template.ParentNamespace == "TbEffect"
        || template.TargetNamespace is "TbEffect" or "EffectGroup"
        || template.ParentNamespace == "EffectGroup";

    private static int FirstId(GameDataRecord record, string field) =>
        record.Fields.TryGetValue(field, out IReadOnlyList<string>? values)
        && values.Count > 0
        && int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
            ? id
            : 0;
}
