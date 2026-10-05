using System.Globalization;
using TianshuDM.Application.GameData;
using TianshuDM.Application.Workspaces;
using TianshuDM.Domain.GameData;
using TianshuDM.Domain.HeroAuthoring;

namespace TianshuDM.Application.HeroAuthoring;

public sealed record HeroAuthoringCommandResult(
    string Revision,
    IReadOnlyList<string> ChangedNodeKeys,
    int? CreatedId = null,
    int? CreatedGroupId = null,
    IReadOnlyList<string>? PreservedNodeKeys = null);

public sealed record HeroAuthoringCommandAuditEntry(
    DateTimeOffset CreatedAtUtc,
    string Command,
    IReadOnlyList<string> ChangedNodeKeys,
    string BeforeRevision,
    string AfterRevision);

public sealed record HeroAuthoringHistoryStatus(
    string Revision,
    bool CanUndo,
    bool CanRedo,
    int UndoDepth,
    int RedoDepth);

public sealed class HeroAuthoringCommandService(
    IGameDataAtomicDraftStore store,
    HeroAuthoringGraphProjector projector,
    IHeroAuthoringLayoutStore? layoutStore = null,
    IWorkspaceStore? workspaceStore = null)
{
    public const string HistoryScopeHeader = "X-Hero-Authoring-History-Scope";

    private const string GlobalHistoryScope = "global";

    private readonly Dictionary<string, Stack<HistoryEntry>> _undoByScope =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, Stack<HistoryEntry>> _redoByScope =
        new(StringComparer.Ordinal);
    private readonly List<HeroAuthoringCommandAuditEntry> _audit = [];
    private readonly object _gate = new();
    private readonly AsyncLocal<string?> _historyScope = new();

    public IDisposable BeginHistoryScope(string? scopeKey)
    {
        string? previous = _historyScope.Value;
        _historyScope.Value = string.IsNullOrWhiteSpace(scopeKey)
            ? GlobalHistoryScope
            : scopeKey.Trim();
        return new HistoryScopeHandle(this, previous);
    }

    public HeroAuthoringCommandResult CreateAsset(
        string nodeNamespace,
        IReadOnlyDictionary<string, IReadOnlyList<string>> initialFields,
        string? expectedRevision = null)
    {
        lock (_gate)
        {
            GameDataCatalog catalog = RequireCatalog(expectedRevision);
            GameDataTable table = ResolveTable(catalog, nodeNamespace);
            int id = NextId(table);
            Dictionary<string, IReadOnlyList<string>> fields = initialFields.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
            fields["Id"] = [id.ToString(CultureInfo.InvariantCulture)];
            GameDataRecord created = GameDataDraftService.ValidateRecord(catalog, table, id, fields) with
            {
                SourceOrder = table.Records.Select(record => record.SourceOrder).DefaultIfEmpty(-1).Max() + 1,
                SourceRow = 0,
            };
            return Commit(
                catalog,
                new Dictionary<string, IReadOnlyList<GameDataRecord>>
                {
                    [table.Key] = table.Records.Append(created).OrderBy(record => record.SourceOrder).ToArray(),
                },
                [$"{nodeNamespace}:{id}"],
                id,
                command: "create-asset");
        }
    }

    public HeroAuthoringCommandResult UpdateNode(
        string nodeNamespace,
        int id,
        IReadOnlyDictionary<string, IReadOnlyList<string>> fields,
        string? expectedRevision = null)
    {
        lock (_gate)
        {
            GameDataCatalog catalog = RequireCatalog(expectedRevision);
            GameDataTable table = ResolveTable(catalog, nodeNamespace);
            GameDataRecord current = table.Record(id);
            GameDataRecord updated = GameDataDraftService.ValidateRecord(catalog, table, id, fields) with
            {
                SourceOrder = current.SourceOrder,
                SourceRow = current.SourceRow,
            };
            return Commit(
                catalog,
                new Dictionary<string, IReadOnlyList<GameDataRecord>>
                {
                    [table.Key] = table.Records.Select(record => record.Id == id ? updated : record).ToArray(),
                },
                [$"{nodeNamespace}:{id}"],
                command: "update-node");
        }
    }

    public HeroAuthoringCommandResult CreateAndLink(
        string parentNamespace,
        int parentId,
        string parentField,
        string targetNamespace,
        IReadOnlyDictionary<string, IReadOnlyList<string>> initialFields,
        string? expectedRevision = null)
    {
        lock (_gate)
        {
            GameDataCatalog catalog = RequireCatalog(expectedRevision);
            GameDataTable parentTable = ResolveTable(catalog, parentNamespace);
            GameDataTable targetTable = ResolveTable(catalog, targetNamespace);
            GameDataRecord parent = parentTable.Record(parentId);
            GameDataFieldDefinition link = RequireLinkField(parentTable, parentField, targetTable.Key);
            int id = NextId(targetTable);
            Dictionary<string, IReadOnlyList<string>> targetFields = initialFields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            targetFields["Id"] = [id.ToString(CultureInfo.InvariantCulture)];
            GameDataRecord created = GameDataDraftService.ValidateRecord(catalog, targetTable, id, targetFields) with
            {
                SourceOrder = targetTable.Records.Select(record => record.SourceOrder).DefaultIfEmpty(-1).Max() + 1,
            };
            GameDataTable targetWithCreated = targetTable with
            {
                Records = targetTable.Records.Append(created).OrderBy(record => record.SourceOrder).ToArray(),
            };
            GameDataCatalog catalogWithCreated = catalog.ReplaceTable(targetWithCreated);
            GameDataRecord linkedParent = ReplaceLink(catalogWithCreated, parentTable, parent, link, id, add: true);
            var replacements = new Dictionary<string, IReadOnlyList<GameDataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                [targetTable.Key] = targetWithCreated.Records,
            };
            replacements[parentTable.Key] = ReplaceRecord(
                replacements.TryGetValue(parentTable.Key, out IReadOnlyList<GameDataRecord>? sameTable)
                    ? sameTable
                    : parentTable.Records,
                linkedParent);
            return Commit(
                catalog,
                replacements,
                [$"{parentNamespace}:{parentId}", $"{targetNamespace}:{id}"],
                id,
                command: "create-and-link");
        }
    }

    public HeroAuthoringCommandResult CreateUpgradeSkill(
        int heroId,
        int unlockLevel,
        IReadOnlyDictionary<string, IReadOnlyList<string>> initialSkillFields,
        string? expectedRevision = null)
    {
        lock (_gate)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(unlockLevel);
            GameDataCatalog catalog = RequireCatalog(expectedRevision);
            GameDataTable heroTable = catalog.Table("hero");
            heroTable.Record(heroId);
            GameDataTable skillTable = catalog.Table("skill");
            GameDataTable upgradeTable = catalog.Table("hero-upgrade");
            GameDataTable itemTable = catalog.Table("item");
            GameDataTable effectTable = catalog.Table("effect");
            GameDataRecord[] matchingUpgrades = upgradeTable.Records.Where(record =>
                FirstId(record, "group_id") == heroId
                && FirstId(record, "level") == unlockLevel).ToArray();
            if (matchingUpgrades.Length == 0)
            {
                throw new InvalidOperationException(
                    $"找不到英雄 {heroId} 的 {unlockLevel} 级升级配置，无法创建升级解锁技能。");
            }
            if (matchingUpgrades.Length > 1)
            {
                throw new InvalidDataException(
                    $"英雄 {heroId} 存在多条 {unlockLevel} 级升级配置，请先修复 HeroUpgrade.xlsx。");
            }

            GameDataRecord upgrade = matchingUpgrades[0];
            int occupiedItemId = FirstId(upgrade, "UnlockItem");
            if (occupiedItemId > 0)
            {
                throw new InvalidOperationException(
                    $"英雄 {heroId} 的 {unlockLevel} 级已经配置了解锁物品 {occupiedItemId}，请选择其他等级。");
            }

            int skillId = NextId(skillTable);
            Dictionary<string, IReadOnlyList<string>> skillFields = initialSkillFields.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
            skillFields["Id"] = [skillId.ToString(CultureInfo.InvariantCulture)];
            GameDataRecord skill = GameDataDraftService.ValidateRecord(
                catalog,
                skillTable,
                skillId,
                skillFields) with
            {
                SourceOrder = skillTable.Records.Select(record => record.SourceOrder).DefaultIfEmpty(-1).Max() + 1,
                SourceRow = 0,
            };
            GameDataTable skillWithCreated = skillTable with
            {
                Records = skillTable.Records.Append(skill).OrderBy(record => record.SourceOrder).ToArray(),
            };
            GameDataCatalog catalogWithSkill = catalog.ReplaceTable(skillWithCreated);

            int effectGroupId = effectTable.Records
                .Select(record => FirstId(record, "group_id"))
                .DefaultIfEmpty(0)
                .Max() + 1;
            int effectId = NextId(effectTable);
            GameDataFieldDefinition actionTypeField = effectTable.Fields.FirstOrDefault(field =>
                string.Equals(field.Key, "action_type", StringComparison.Ordinal))
                ?? throw new InvalidDataException("Effect.xlsx 缺少 action_type 字段。");
            GameDataOption addSkill = actionTypeField.Options.FirstOrDefault(option =>
                string.Equals(option.Code, "AddSkill", StringComparison.Ordinal))
                ?? throw new InvalidDataException("EffectActionType 中缺少 AddSkill（增加技能）操作。");
            var effectFields = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["Id"] = [effectId.ToString(CultureInfo.InvariantCulture)],
                ["group_id"] = [effectGroupId.ToString(CultureInfo.InvariantCulture)],
                ["action_type"] = [addSkill.Value],
                ["action_param"] = [skillId.ToString(CultureInfo.InvariantCulture)],
            };
            if (effectTable.Fields.Any(field => string.Equals(field.Key, "name", StringComparison.Ordinal)))
            {
                effectFields["name"] = [$"解锁技能 {skillId}"];
            }
            GameDataRecord effect = GameDataDraftService.ValidateRecord(
                catalogWithSkill,
                effectTable,
                effectId,
                effectFields) with
            {
                SourceOrder = effectTable.Records.Select(record => record.SourceOrder).DefaultIfEmpty(-1).Max() + 1,
                SourceRow = 0,
            };
            GameDataTable effectWithCreated = effectTable with
            {
                Records = effectTable.Records.Append(effect).OrderBy(record => record.SourceOrder).ToArray(),
            };
            GameDataCatalog catalogWithEffect = catalogWithSkill.ReplaceTable(effectWithCreated);

            int itemId = NextId(itemTable);
            var itemFields = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["Id"] = [itemId.ToString(CultureInfo.InvariantCulture)],
                ["effect_group_id"] = [effectGroupId.ToString(CultureInfo.InvariantCulture)],
            };
            if (itemTable.Fields.Any(field => string.Equals(field.Key, "desc", StringComparison.Ordinal)))
            {
                itemFields["desc"] = [$"解锁技能 {skillId}"];
            }
            GameDataRecord item = GameDataDraftService.ValidateRecord(
                catalogWithEffect,
                itemTable,
                itemId,
                itemFields) with
            {
                SourceOrder = itemTable.Records.Select(record => record.SourceOrder).DefaultIfEmpty(-1).Max() + 1,
                SourceRow = 0,
            };
            GameDataTable itemWithCreated = itemTable with
            {
                Records = itemTable.Records.Append(item).OrderBy(record => record.SourceOrder).ToArray(),
            };
            GameDataCatalog catalogWithItem = catalogWithEffect.ReplaceTable(itemWithCreated);

            var upgradeFields = upgrade.Fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            upgradeFields["UnlockItem"] = [itemId.ToString(CultureInfo.InvariantCulture)];
            GameDataRecord updatedUpgrade = GameDataDraftService.ValidateRecord(
                catalogWithItem,
                upgradeTable,
                upgrade.Id,
                upgradeFields) with
            {
                SourceOrder = upgrade.SourceOrder,
                SourceRow = upgrade.SourceRow,
            };

            return Commit(
                catalog,
                new Dictionary<string, IReadOnlyList<GameDataRecord>>(StringComparer.OrdinalIgnoreCase)
                {
                    [skillTable.Key] = skillWithCreated.Records,
                    [effectTable.Key] = effectWithCreated.Records,
                    [itemTable.Key] = itemWithCreated.Records,
                    [upgradeTable.Key] = ReplaceRecord(upgradeTable.Records, updatedUpgrade),
                },
                [
                    $"TbHero:{heroId}",
                    $"TbHeroUpgrade:{upgrade.Id}",
                    $"TbItem:{itemId}",
                    $"EffectGroup:{effectGroupId}",
                    $"TbEffect:{effectId}",
                    $"TbSkill:{skillId}",
                ],
                skillId,
                effectGroupId,
                "create-upgrade-skill");
        }
    }

    public HeroAuthoringCommandResult LinkExisting(
        string parentNamespace,
        int parentId,
        string parentField,
        string targetNamespace,
        int targetId,
        string? expectedRevision = null,
        int? parameterIndex = null) =>
        ChangeLink(parentNamespace, parentId, parentField, targetNamespace, targetId, true, expectedRevision, parameterIndex);

    public HeroAuthoringCommandResult CloneAndLink(
        string parentNamespace,
        int parentId,
        string parentField,
        string targetNamespace,
        int sourceId,
        string? expectedRevision = null)
    {
        lock (_gate)
        {
            GameDataCatalog catalog = RequireCatalog(expectedRevision);
            GameDataTable parentTable = ResolveTable(catalog, parentNamespace);
            GameDataTable targetTable = ResolveTable(catalog, targetNamespace);
            GameDataRecord source = targetTable.Record(sourceId);
            GameDataRecord parent = parentTable.Record(parentId);
            GameDataFieldDefinition link = RequireLinkField(parentTable, parentField, targetTable.Key);
            int id = NextId(targetTable);
            var clonedFields = source.Fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            clonedFields["Id"] = [id.ToString(CultureInfo.InvariantCulture)];
            GameDataRecord clone = GameDataDraftService.ValidateRecord(catalog, targetTable, id, clonedFields) with
            {
                SourceOrder = targetTable.Records.Select(record => record.SourceOrder).DefaultIfEmpty(-1).Max() + 1,
                SourceRow = 0,
            };
            GameDataTable targetWithClone = targetTable with
            {
                Records = targetTable.Records.Append(clone).OrderBy(record => record.SourceOrder).ToArray(),
            };
            GameDataCatalog catalogWithClone = catalog.ReplaceTable(targetWithClone);
            GameDataRecord linkedParent = ReplaceLink(catalogWithClone, parentTable, parent, link, id, add: true);
            var replacements = new Dictionary<string, IReadOnlyList<GameDataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                [targetTable.Key] = targetWithClone.Records,
            };
            replacements[parentTable.Key] = ReplaceRecord(
                replacements.TryGetValue(parentTable.Key, out IReadOnlyList<GameDataRecord>? sameTable)
                    ? sameTable
                    : parentTable.Records,
                linkedParent);
            return Commit(
                catalog,
                replacements,
                [$"{parentNamespace}:{parentId}", $"{targetNamespace}:{id}"],
                id,
                command: "clone-and-link");
        }
    }

    public HeroAuthoringCommandResult CreateGroupAndMember(
        string parentNamespace,
        int parentId,
        string parentField,
        string groupNamespace,
        IReadOnlyDictionary<string, IReadOnlyList<string>> initialMemberFields,
        string? expectedRevision = null)
    {
        lock (_gate)
        {
            GameDataCatalog catalog = RequireCatalog(expectedRevision);
            GameDataTable parentTable = ResolveTable(catalog, parentNamespace);
            (GameDataTable memberTable, GameDataFieldDefinition groupField) = ResolveGroup(catalog, groupNamespace);
            GameDataFieldDefinition parentGroupField = RequireGroupField(parentTable, parentField, groupNamespace);
            GameDataRecord parent = parentTable.Record(parentId);
            int groupId = memberTable.Records.Select(record => FirstId(record, groupField.Key)).DefaultIfEmpty(0).Max() + 1;
            int memberId = NextId(memberTable);
            Dictionary<string, IReadOnlyList<string>> memberFields = initialMemberFields.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
            memberFields["Id"] = [memberId.ToString(CultureInfo.InvariantCulture)];
            memberFields[groupField.Key] = [groupId.ToString(CultureInfo.InvariantCulture)];
            GameDataRecord member = GameDataDraftService.ValidateRecord(catalog, memberTable, memberId, memberFields) with
            {
                SourceOrder = memberTable.Records.Select(record => record.SourceOrder).DefaultIfEmpty(-1).Max() + 1,
            };
            GameDataRecord linkedParent = ReplaceGroupLink(catalog, parentTable, parent, parentGroupField, groupId, add: true);
            var replacements = new Dictionary<string, IReadOnlyList<GameDataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                [memberTable.Key] = memberTable.Records.Append(member).OrderBy(record => record.SourceOrder).ToArray(),
                [parentTable.Key] = ReplaceRecord(parentTable.Records, linkedParent),
            };
            return Commit(
                catalog,
                replacements,
                [$"{parentNamespace}:{parentId}", $"{groupNamespace}:{groupId}", $"{(memberTable.Key == "effect" ? "TbEffect" : "TbCondition")}:{memberId}"],
                memberId,
                groupId,
                "create-group");
        }
    }

    public HeroAuthoringCommandResult CreateStandaloneGroup(
        string groupNamespace,
        IReadOnlyDictionary<string, IReadOnlyList<string>> initialMemberFields,
        string? expectedRevision = null)
    {
        lock (_gate)
        {
            GameDataCatalog catalog = RequireCatalog(expectedRevision);
            (GameDataTable memberTable, GameDataFieldDefinition groupField) = ResolveGroup(catalog, groupNamespace);
            int groupId = memberTable.Records
                .Select(record => FirstId(record, groupField.Key))
                .DefaultIfEmpty(0)
                .Max() + 1;
            int memberId = NextId(memberTable);
            Dictionary<string, IReadOnlyList<string>> fields = initialMemberFields.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
            fields["Id"] = [memberId.ToString(CultureInfo.InvariantCulture)];
            fields[groupField.Key] = [groupId.ToString(CultureInfo.InvariantCulture)];
            GameDataRecord member = GameDataDraftService.ValidateRecord(catalog, memberTable, memberId, fields) with
            {
                SourceOrder = memberTable.Records.Select(record => record.SourceOrder).DefaultIfEmpty(-1).Max() + 1,
                SourceRow = 0,
            };
            return Commit(
                catalog,
                new Dictionary<string, IReadOnlyList<GameDataRecord>>
                {
                    [memberTable.Key] = memberTable.Records.Append(member).OrderBy(record => record.SourceOrder).ToArray(),
                },
                [$"{groupNamespace}:{groupId}", $"{(memberTable.Key == "effect" ? "TbEffect" : "TbCondition")}:{memberId}"],
                memberId,
                groupId,
                "create-standalone-group");
        }
    }

    public HeroAuthoringCommandResult CreateGroupMember(
        string groupNamespace,
        int groupId,
        string targetNamespace,
        IReadOnlyDictionary<string, IReadOnlyList<string>> initialFields,
        string? expectedRevision = null)
    {
        lock (_gate)
        {
            GameDataCatalog catalog = RequireCatalog(expectedRevision);
            (GameDataTable memberTable, GameDataFieldDefinition groupField) = ResolveGroup(catalog, groupNamespace);
            GameDataTable targetTable = ResolveTable(catalog, targetNamespace);
            if (!string.Equals(memberTable.Key, targetTable.Key, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"{groupNamespace} 不能添加 {targetNamespace} 类型的成员。", nameof(targetNamespace));
            }
            string groupKey = $"{groupNamespace}:{groupId}";
            if (projector.ProjectCatalog(catalog).Nodes.All(node => node.Key != groupKey))
            {
                throw new KeyNotFoundException($"不存在分组 {groupKey}。");
            }

            int memberId = NextId(memberTable);
            Dictionary<string, IReadOnlyList<string>> fields = initialFields.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
            fields["Id"] = [memberId.ToString(CultureInfo.InvariantCulture)];
            fields[groupField.Key] = [groupId.ToString(CultureInfo.InvariantCulture)];
            int insertionOrder = memberTable.Records
                .Where(record => FirstId(record, groupField.Key) == groupId)
                .Select(record => record.SourceOrder)
                .DefaultIfEmpty(memberTable.Records.Select(record => record.SourceOrder).DefaultIfEmpty(-1).Max())
                .Max() + 1;
            GameDataRecord member = GameDataDraftService.ValidateRecord(catalog, memberTable, memberId, fields) with
            {
                SourceOrder = insertionOrder,
            };
            GameDataRecord[] records = memberTable.Records
                .Select(record => record.SourceOrder >= insertionOrder
                    ? record with { SourceOrder = record.SourceOrder + 1 }
                    : record)
                .Append(member)
                .OrderBy(record => record.SourceOrder)
                .ThenBy(record => record.Id)
                .ToArray();
            return Commit(
                catalog,
                new Dictionary<string, IReadOnlyList<GameDataRecord>>
                {
                    [memberTable.Key] = records,
                },
                [$"{groupNamespace}:{groupId}", $"{targetNamespace}:{memberId}"],
                memberId,
                command: "create-group-member");
        }
    }

    public HeroAuthoringCommandResult CreateParameterNode(
        string parentNamespace,
        int parentId,
        string parentField,
        int parameterIndex,
        string targetNamespace,
        IReadOnlyDictionary<string, IReadOnlyList<string>> initialFields,
        string? expectedRevision = null)
    {
        lock (_gate)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(parameterIndex);
            GameDataCatalog catalog = RequireCatalog(expectedRevision);
            GameDataTable parentTable = ResolveTable(catalog, parentNamespace);
            GameDataTable targetTable = ResolveTable(catalog, targetNamespace);
            GameDataFieldDefinition packedField = parentTable.Fields.FirstOrDefault(
                field => string.Equals(field.Key, parentField, StringComparison.Ordinal)
                         && field.Kind == GameDataFieldKind.List)
                ?? throw new ArgumentException($"{parentTable.DisplayName}不存在可写入的参数数组“{parentField}”。", nameof(parentField));
            GameDataRecord parent = parentTable.Record(parentId);
            int targetId = NextId(targetTable);
            Dictionary<string, IReadOnlyList<string>> targetFields = initialFields.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal);
            targetFields["Id"] = [targetId.ToString(CultureInfo.InvariantCulture)];
            GameDataRecord created = GameDataDraftService.ValidateRecord(catalog, targetTable, targetId, targetFields) with
            {
                SourceOrder = targetTable.Records.Select(record => record.SourceOrder).DefaultIfEmpty(-1).Max() + 1,
            };
            GameDataTable targetWithCreated = targetTable with
            {
                Records = targetTable.Records.Append(created).OrderBy(record => record.SourceOrder).ToArray(),
            };
            GameDataCatalog catalogWithCreated = catalog.ReplaceTable(targetWithCreated);
            var parentFields = parent.Fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var parameters = parentFields.GetValueOrDefault(packedField.Key, []).ToList();
            while (parameters.Count <= parameterIndex) parameters.Add("0");
            parameters[parameterIndex] = targetId.ToString(CultureInfo.InvariantCulture);
            parentFields[packedField.Key] = parameters;
            GameDataRecord linkedParent = GameDataDraftService.ValidateRecord(
                catalogWithCreated,
                parentTable,
                parentId,
                parentFields) with
            {
                SourceOrder = parent.SourceOrder,
                SourceRow = parent.SourceRow,
            };
            var replacements = new Dictionary<string, IReadOnlyList<GameDataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                [targetTable.Key] = targetWithCreated.Records,
                [parentTable.Key] = ReplaceRecord(parentTable.Records, linkedParent),
            };
            return Commit(
                catalog,
                replacements,
                [$"{parentNamespace}:{parentId}", $"{targetNamespace}:{targetId}"],
                targetId,
                command: "create-parameter-node");
        }
    }

    public HeroAuthoringCommandResult CreateParameterGroup(
        string parentNamespace,
        int parentId,
        string parentField,
        int parameterIndex,
        string groupNamespace,
        string? expectedRevision = null)
    {
        lock (_gate)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(parameterIndex);
            GameDataCatalog catalog = RequireCatalog(expectedRevision);
            GameDataTable parentTable = ResolveTable(catalog, parentNamespace);
            GameDataFieldDefinition packedField = parentTable.Fields.FirstOrDefault(
                field => string.Equals(field.Key, parentField, StringComparison.Ordinal)
                         && field.Kind == GameDataFieldKind.List)
                ?? throw new ArgumentException($"{parentTable.DisplayName}不存在可写入的参数数组“{parentField}”。", nameof(parentField));
            (GameDataTable memberTable, GameDataFieldDefinition groupField) = ResolveGroup(catalog, groupNamespace);
            int groupId = projector.ProjectCatalog(catalog).Nodes
                .Where(node => string.Equals(node.Namespace, groupNamespace, StringComparison.Ordinal))
                .Select(node => node.LegacyId)
                .DefaultIfEmpty(0)
                .Max() + 1;
            GameDataRecord parent = parentTable.Record(parentId);
            var fields = parent.Fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            var parameters = fields.GetValueOrDefault(packedField.Key, []).ToList();
            while (parameters.Count <= parameterIndex) parameters.Add("0");
            parameters[parameterIndex] = groupId.ToString(CultureInfo.InvariantCulture);
            fields[packedField.Key] = parameters;
            GameDataRecord linkedParent = GameDataDraftService.ValidateRecord(catalog, parentTable, parentId, fields) with
            {
                SourceOrder = parent.SourceOrder,
                SourceRow = parent.SourceRow,
            };
            int? createdMemberId = null;
            var replacements = new Dictionary<string, IReadOnlyList<GameDataRecord>>(StringComparer.OrdinalIgnoreCase)
            {
                [parentTable.Key] = ReplaceRecord(parentTable.Records, linkedParent),
            };
            if (string.Equals(groupNamespace, "EffectGroup", StringComparison.Ordinal))
            {
                createdMemberId = NextId(memberTable);
                var memberFields = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["Id"] = [createdMemberId.Value.ToString(CultureInfo.InvariantCulture)],
                    [groupField.Key] = [groupId.ToString(CultureInfo.InvariantCulture)],
                };
                if (memberTable.Fields.Any(field => string.Equals(field.Key, "name", StringComparison.Ordinal)))
                {
                    memberFields["name"] = ["新效果"];
                }
                GameDataFieldDefinition? typeField = memberTable.Fields.FirstOrDefault(field =>
                    string.Equals(field.Key, "action_type", StringComparison.Ordinal));
                if (typeField is not null)
                {
                    GameDataOption? none = typeField.Options.FirstOrDefault(option =>
                        option.LegacyValue == 0
                        || string.Equals(option.Code, "None", StringComparison.OrdinalIgnoreCase));
                    memberFields["action_type"] = [none?.Value ?? "None"];
                }
                if (memberTable.Fields.Any(field => string.Equals(field.Key, "action_param", StringComparison.Ordinal)))
                {
                    memberFields["action_param"] = [];
                }
                GameDataRecord member = GameDataDraftService.ValidateRecord(
                    catalog,
                    memberTable,
                    createdMemberId.Value,
                    memberFields) with
                {
                    SourceOrder = memberTable.Records.Select(record => record.SourceOrder).DefaultIfEmpty(-1).Max() + 1,
                    SourceRow = 0,
                };
                GameDataRecord[] memberRecords = memberTable.Records.Append(member)
                    .OrderBy(record => record.SourceOrder)
                    .ToArray();
                if (string.Equals(parentTable.Key, memberTable.Key, StringComparison.OrdinalIgnoreCase))
                {
                    memberRecords = ReplaceRecord(memberRecords, linkedParent);
                }
                replacements[memberTable.Key] = memberRecords;
            }
            if (!string.Equals(parentTable.Key, memberTable.Key, StringComparison.OrdinalIgnoreCase))
            {
                replacements[parentTable.Key] = ReplaceRecord(parentTable.Records, linkedParent);
            }
            string[] changedNodes =
            [
                $"{parentNamespace}:{parentId}",
                $"{groupNamespace}:{groupId}",
                ..(createdMemberId is int id ? [$"TbEffect:{id}"] : Array.Empty<string>()),
            ];
            return Commit(
                catalog,
                replacements,
                changedNodes,
                createdMemberId,
                createdGroupId: groupId,
                command: "create-parameter-group");
        }
    }

    public HeroAuthoringCommandResult Unlink(
        string parentNamespace,
        int parentId,
        string parentField,
        string targetNamespace,
        int targetId,
        string? expectedRevision = null,
        int? parameterIndex = null) =>
        ChangeLink(parentNamespace, parentId, parentField, targetNamespace, targetId, false, expectedRevision, parameterIndex);

    public HeroAuthoringCommandResult DeleteNode(
        string nodeNamespace,
        int id,
        string? expectedRevision = null)
    {
        lock (_gate)
        {
            GameDataCatalog catalog = RequireCatalog(expectedRevision);
            GameDataTable table = ResolveTable(catalog, nodeNamespace);
            table.Record(id);
            string key = $"{nodeNamespace}:{id}";
            HeroAuthoringGraph impact = projector.Project(catalog, nodeNamespace, id, 1);
            HeroAuthoringGraphEdge? incoming = impact.Edges.FirstOrDefault(edge => edge.Target == key);
            if (incoming is not null)
            {
                throw new InvalidOperationException($"无法删除 {key}，节点 {incoming.Source} 仍在引用它；请先解除引用。");
            }

            return Commit(
                catalog,
                new Dictionary<string, IReadOnlyList<GameDataRecord>>
                {
                    [table.Key] = table.Records.Where(record => record.Id != id).ToArray(),
                },
                [key],
                command: "delete-node");
        }
    }

    public HeroAuthoringCommandResult DeleteGroupMember(
        string groupNamespace,
        int groupId,
        string memberNamespace,
        int memberId,
        string? expectedRevision = null)
    {
        lock (_gate)
        {
            GameDataCatalog catalog = RequireCatalog(expectedRevision);
            (GameDataTable memberTable, GameDataFieldDefinition groupField) = ResolveGroup(catalog, groupNamespace);
            GameDataTable requestedTable = ResolveTable(catalog, memberNamespace);
            if (!string.Equals(memberTable.Key, requestedTable.Key, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"{groupNamespace} 不包含 {memberNamespace} 类型的成员。", nameof(memberNamespace));
            }
            GameDataRecord member = memberTable.Record(memberId);
            if (FirstId(member, groupField.Key) != groupId)
            {
                throw new ArgumentException($"{memberNamespace}:{memberId} 不属于 {groupNamespace}:{groupId}。", nameof(memberId));
            }

            string memberKey = $"{memberNamespace}:{memberId}";
            string groupKey = $"{groupNamespace}:{groupId}";
            HeroAuthoringGraph memberImpact = projector.Project(catalog, memberNamespace, memberId, 1);
            HeroAuthoringGraphEdge? sharedReference = memberImpact.Edges.FirstOrDefault(edge =>
                edge.Target == memberKey && edge.Source != groupKey);
            if (sharedReference is not null)
            {
                throw new InvalidOperationException(
                    $"无法删除 {memberKey}，节点 {sharedReference.Source} 仍在引用它；请先解除引用。");
            }

            return Commit(
                catalog,
                new Dictionary<string, IReadOnlyList<GameDataRecord>>
                {
                    [memberTable.Key] = memberTable.Records.Where(record => record.Id != memberId).ToArray(),
                },
                [groupKey, memberKey],
                command: "delete-group-member");
        }
    }

    public HeroAuthoringCommandResult DeleteBranch(
        string parentNamespace,
        int parentId,
        string parentField,
        string targetNamespace,
        int targetId,
        string? expectedRevision = null,
        int? parameterIndex = null)
    {
        lock (_gate)
        {
            GameDataCatalog catalog = RequireCatalog(expectedRevision);
            string effectiveParentNamespace = parentNamespace == "SkillConditionGate" ? "TbSkill" : parentNamespace;
            string parentKey = $"{effectiveParentNamespace}:{parentId}";
            string targetKey = $"{targetNamespace}:{targetId}";
            bool groupMembership = effectiveParentNamespace is "EffectGroup" or "ConditionGroup";
            GameDataCatalog detachedCatalog = catalog;
            var changedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (groupMembership)
            {
                (GameDataTable memberTable, GameDataFieldDefinition groupField) = ResolveGroup(catalog, effectiveParentNamespace);
                GameDataTable requestedTable = ResolveTable(catalog, targetNamespace);
                if (!string.Equals(memberTable.Key, requestedTable.Key, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException($"{effectiveParentNamespace} 不包含 {targetNamespace} 类型的成员。", nameof(targetNamespace));
                }
                GameDataRecord member = memberTable.Record(targetId);
                if (FirstId(member, groupField.Key) != parentId)
                {
                    throw new ArgumentException($"{targetKey} 不属于 {parentKey}。", nameof(targetId));
                }
            }
            else
            {
                GameDataTable parentTable = ResolveTable(catalog, effectiveParentNamespace);
                GameDataRecord parent = parentTable.Record(parentId);
                GameDataRecord detachedParent;
                if (string.Equals(parentField, "action_param", StringComparison.Ordinal))
                {
                    detachedParent = ClearActionParameterReference(
                        catalog,
                        parentTable,
                        parent,
                        targetNamespace,
                        targetId,
                        parameterIndex);
                }
                else if (targetNamespace is "EffectGroup" or "ConditionGroup")
                {
                    GameDataFieldDefinition groupLink = RequireGroupField(parentTable, parentField, targetNamespace);
                    detachedParent = ReplaceGroupLink(catalog, parentTable, parent, groupLink, targetId, add: false);
                }
                else
                {
                    GameDataTable targetTable = ResolveTable(catalog, targetNamespace);
                    targetTable.Record(targetId);
                    GameDataFieldDefinition link = RequireLinkField(parentTable, parentField, targetTable.Key);
                    detachedParent = ReplaceLink(catalog, parentTable, parent, link, targetId, add: false);
                }
                detachedCatalog = catalog.ReplaceTable(parentTable with
                {
                    Records = ReplaceRecord(parentTable.Records, detachedParent),
                });
                changedTables.Add(parentTable.Key);
            }

            return DeleteBranchCore(
                catalog,
                detachedCatalog,
                parentKey,
                targetNamespace,
                targetId,
                groupMembership,
                changedTables);
        }
    }

    public HeroAuthoringCommandResult DeleteRootBranch(
        string rootNamespace,
        int rootId,
        string? expectedRevision = null)
    {
        lock (_gate)
        {
            GameDataCatalog catalog = RequireCatalog(expectedRevision);
            string targetKey = $"{rootNamespace}:{rootId}";
            if (projector.ProjectCatalog(catalog).Nodes.All(node => node.Key != targetKey))
            {
                throw new KeyNotFoundException($"英雄配置图谱中不存在节点 {targetKey}。");
            }

            var changedTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var detachedNodes = new HashSet<string>(StringComparer.Ordinal);
            GameDataCatalog detachedCatalog = DetachIncomingReferences(
                catalog,
                targetKey,
                changedTables,
                detachedNodes);
            HeroAuthoringCommandResult result = DeleteBranchCore(
                catalog,
                detachedCatalog,
                null,
                rootNamespace,
                rootId,
                groupMembership: false,
                changedTables);
            return result with
            {
                ChangedNodeKeys = detachedNodes.Concat(result.ChangedNodeKeys)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray(),
            };
        }
    }

    private HeroAuthoringCommandResult DeleteBranchCore(
        GameDataCatalog catalog,
        GameDataCatalog detachedCatalog,
        string? parentKey,
        string targetNamespace,
        int targetId,
        bool groupMembership,
        HashSet<string> changedTables)
    {
        string targetKey = $"{targetNamespace}:{targetId}";
        HeroAuthoringGraph graph = projector.ProjectCatalog(detachedCatalog);
        var nodesByKey = graph.Nodes.ToDictionary(node => node.Key, StringComparer.Ordinal);
        HeroAuthoringGraphEdge[] realEdges = graph.Edges
            .Where(edge => !edge.Derived
                           && !edge.Source.StartsWith("SkillConditionGate:", StringComparison.Ordinal))
            .ToArray();
        bool detachedEmptyGroup = !groupMembership
                                  && (targetNamespace is "EffectGroup" or "ConditionGroup")
                                  && !nodesByKey.ContainsKey(targetKey);
        if (!nodesByKey.ContainsKey(targetKey) && !detachedEmptyGroup)
        {
            throw new KeyNotFoundException($"英雄配置图谱中不存在节点 {targetKey}。");
        }

        var deleted = new HashSet<string>(StringComparer.Ordinal);
        var preserved = new HashSet<string>(StringComparer.Ordinal);
        var pending = detachedEmptyGroup ? new Queue<string>() : new Queue<string>([targetKey]);
        while (pending.TryDequeue(out string? candidateKey))
        {
            if (deleted.Contains(candidateKey) || preserved.Contains(candidateKey)) continue;
            HeroAuthoringGraphEdge[] externalIncoming = realEdges.Where(edge =>
                edge.Target == candidateKey
                && !deleted.Contains(edge.Source)
                && !(groupMembership && candidateKey == targetKey && edge.Source == parentKey))
                .ToArray();
            if (externalIncoming.Length > 0)
            {
                if (groupMembership && candidateKey == targetKey)
                {
                    throw new InvalidOperationException(
                        $"无法从 {parentKey} 删除 {targetKey}，节点 {externalIncoming[0].Source} 仍在引用它；组内成员不能在保留编号的同时脱离所属组。");
                }
                if (parentKey is null && candidateKey == targetKey)
                {
                    throw new InvalidOperationException(
                        $"无法删除根节点 {targetKey}，节点 {externalIncoming[0].Source} 仍通过不可编辑的派生关系引用它。");
                }
                preserved.Add(candidateKey);
                continue;
            }

            deleted.Add(candidateKey);
            foreach (HeroAuthoringGraphEdge outgoing in realEdges.Where(edge => edge.Source == candidateKey))
            {
                pending.Enqueue(outgoing.Target);
            }
        }
        foreach (string virtualGroup in deleted.Where(key =>
                     key.StartsWith("EffectGroup:", StringComparison.Ordinal)
                     || key.StartsWith("ConditionGroup:", StringComparison.Ordinal)).ToArray())
        {
            if (realEdges.Any(edge => edge.Source == virtualGroup && preserved.Contains(edge.Target)))
            {
                deleted.Remove(virtualGroup);
                preserved.Add(virtualGroup);
            }
        }

        var replacements = new Dictionary<string, IReadOnlyList<GameDataRecord>>(StringComparer.OrdinalIgnoreCase);
        foreach (string key in deleted)
        {
            if (!TryNodeIdentity(key, out string nodeNamespace, out int nodeId)
                || nodeNamespace is "EffectGroup" or "ConditionGroup"
                || !HeroAuthoringGraphProjector.TryGetTableKey(nodeNamespace, out string tableKey))
            {
                continue;
            }
            changedTables.Add(tableKey);
            GameDataTable table = detachedCatalog.Table(tableKey);
            IReadOnlyList<GameDataRecord> records = replacements.GetValueOrDefault(tableKey, table.Records);
            replacements[tableKey] = records.Where(record => record.Id != nodeId).ToArray();
        }
        foreach (string tableKey in changedTables)
        {
            replacements.TryAdd(tableKey, detachedCatalog.Table(tableKey).Records);
        }

        IReadOnlyList<string> changedNodes = new string?[] { parentKey, targetKey }
            .OfType<string>()
            .Concat(deleted)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        HeroAuthoringCommandResult committed = Commit(
            catalog,
            replacements,
            changedNodes,
            command: "delete-branch");
        return committed with { PreservedNodeKeys = preserved.Order(StringComparer.Ordinal).ToArray() };
    }

    private GameDataCatalog DetachIncomingReferences(
        GameDataCatalog catalog,
        string targetKey,
        HashSet<string> changedTables,
        HashSet<string> changedNodes)
    {
        HeroAuthoringGraph graph = projector.ProjectCatalog(catalog);
        HeroAuthoringGraphEdge[] incoming = graph.Edges
            .Where(edge => edge.Target == targetKey
                           && !edge.Derived
                           && !string.IsNullOrWhiteSpace(edge.SourceField))
            .ToArray();
        GameDataCatalog result = catalog;
        foreach (HeroAuthoringGraphEdge edge in incoming)
        {
            if (!TryNodeIdentity(edge.Source, out string sourceNamespace, out int sourceId)
                || !TryNodeIdentity(edge.Target, out string targetNamespace, out int targetId))
            {
                continue;
            }
            GameDataTable sourceTable = ResolveTable(result, sourceNamespace);
            GameDataRecord source = sourceTable.Record(sourceId);
            GameDataRecord detached;
            if (string.Equals(edge.SourceField, "action_param", StringComparison.Ordinal))
            {
                detached = ClearActionParameterReference(
                    result,
                    sourceTable,
                    source,
                    targetNamespace,
                    targetId,
                    edge.ParameterIndex);
            }
            else if (targetNamespace is "EffectGroup" or "ConditionGroup")
            {
                GameDataFieldDefinition groupField = RequireGroupField(
                    sourceTable,
                    edge.SourceField!,
                    targetNamespace);
                detached = ReplaceGroupLink(result, sourceTable, source, groupField, targetId, add: false);
            }
            else
            {
                GameDataTable targetTable = ResolveTable(result, targetNamespace);
                targetTable.Record(targetId);
                GameDataFieldDefinition link = RequireLinkField(sourceTable, edge.SourceField!, targetTable.Key);
                detached = ReplaceLink(result, sourceTable, source, link, targetId, add: false);
            }
            result = result.ReplaceTable(sourceTable with
            {
                Records = ReplaceRecord(sourceTable.Records, detached),
            });
            changedTables.Add(sourceTable.Key);
            changedNodes.Add(edge.Source);
        }
        return result;
    }

    public HeroAuthoringCommandResult ReorderGroup(
        string groupNamespace,
        int groupId,
        IReadOnlyList<int> orderedMemberIds,
        string? expectedRevision = null)
    {
        lock (_gate)
        {
            GameDataCatalog catalog = RequireCatalog(expectedRevision);
            string tableKey = groupNamespace switch
            {
                "EffectGroup" => "effect",
                "ConditionGroup" => "condition",
                _ => throw new ArgumentException("仅效果组和条件组支持拖拽排序。", nameof(groupNamespace)),
            };
            GameDataTable table = catalog.Table(tableKey);
            GameDataRecord[] members = table.Records.Where(record => FirstId(record, "group_id") == groupId).ToArray();
            if (members.Select(record => record.Id).Order().SequenceEqual(orderedMemberIds.Order()) is false)
            {
                throw new ArgumentException("排序列表必须完整包含当前组内的全部配置，且不能包含其他记录。", nameof(orderedMemberIds));
            }

            int[] orderSlots = members.Select(record => record.SourceOrder).Order().ToArray();
            Dictionary<int, int> orderById = orderedMemberIds.Select((id, index) => (id, orderSlots[index])).ToDictionary(value => value.id, value => value.Item2);
            GameDataRecord[] records = table.Records
                .Select(record => orderById.TryGetValue(record.Id, out int order) ? record with { SourceOrder = order } : record)
                .OrderBy(record => record.SourceOrder)
                .ThenBy(record => record.Id)
                .ToArray();
            return Commit(
                catalog,
                new Dictionary<string, IReadOnlyList<GameDataRecord>> { [table.Key] = records },
                orderedMemberIds.Select(id => $"{(tableKey == "effect" ? "TbEffect" : "TbCondition")}:{id}").ToArray(),
                command: "reorder-group");
        }
    }

    public HeroAuthoringCommandResult SaveLayout(
        int skillId,
        string projectionMode,
        IReadOnlyList<HeroAuthoringNodeLayout> positions) =>
        SaveLayout("TbSkill", skillId, projectionMode, positions);

    public HeroAuthoringCommandResult SaveLayout(
        string rootNamespace,
        int rootId,
        string projectionMode,
        IReadOnlyList<HeroAuthoringNodeLayout> positions)
    {
        lock (_gate)
        {
            if (layoutStore == null || workspaceStore == null)
            {
                throw new InvalidOperationException("当前服务未配置英雄技能布局存储。");
            }

            string projectRoot = workspaceStore.ReadWorkspace()?.UnityProjectRoot ?? string.Empty;
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                throw new InvalidOperationException("尚未打开项目工作区。");
            }

            string mode = string.Equals(projectionMode, "audit", StringComparison.OrdinalIgnoreCase)
                ? "audit"
                : "compose";
            string storageMode = LayoutStorageMode(rootNamespace, mode);
            IReadOnlyList<HeroAuthoringNodeLayout> before = NormalizeLayout(
                layoutStore.ReadHeroAuthoringLayout(projectRoot, rootId, storageMode));
            IReadOnlyList<HeroAuthoringNodeLayout> after = NormalizeLayout(positions);
            if (LayoutEquals(before, after))
            {
                return new HeroAuthoringCommandResult(CurrentRevision(), []);
            }

            layoutStore.ReplaceHeroAuthoringLayout(projectRoot, rootId, storageMode, after);
            string revision = CurrentRevision();
            string scopeKey = string.IsNullOrWhiteSpace(CurrentHistoryScope)
                || string.Equals(CurrentHistoryScope, GlobalHistoryScope, StringComparison.Ordinal)
                ? LayoutHistoryScope(rootNamespace, rootId)
                : CurrentHistoryScope;
            string[] changedKeys = before
                .Select(position => position.InstanceKey)
                .Concat(after.Select(position => position.InstanceKey))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var entry = new LayoutHistoryEntry(
                "save-layout",
                scopeKey,
                changedKeys,
                revision,
                revision,
                projectRoot,
                rootNamespace,
                rootId,
                storageMode,
                before,
                after);
            UndoStack(scopeKey).Push(entry);
            _redoByScope.Remove(scopeKey);
            return new HeroAuthoringCommandResult(revision, changedKeys);
        }
    }

    public HeroAuthoringCommandResult Undo()
    {
        lock (_gate)
        {
            string scopeKey = CurrentHistoryScope ?? GlobalHistoryScope;
            Stack<HistoryEntry> undo = UndoStack(scopeKey);
            if (undo.Count == 0) throw new InvalidOperationException("没有可撤销的英雄配置操作。");
            HistoryEntry entry = undo.Peek();
            EnsureCanUndo(entry);
            undo.Pop();
            string revision = ApplyBeforeState(entry);
            RedoStack(scopeKey).Push(entry);
            if (entry is ConfigHistoryEntry)
            {
                _audit.Add(new HeroAuthoringCommandAuditEntry(DateTimeOffset.UtcNow, "undo", entry.ChangedNodeKeys, entry.AfterRevision, revision));
            }
            return new HeroAuthoringCommandResult(revision, entry.ChangedNodeKeys);
        }
    }

    public HeroAuthoringCommandResult Redo()
    {
        lock (_gate)
        {
            string scopeKey = CurrentHistoryScope ?? GlobalHistoryScope;
            Stack<HistoryEntry> redo = RedoStack(scopeKey);
            if (redo.Count == 0) throw new InvalidOperationException("没有可重做的英雄配置操作。");
            HistoryEntry entry = redo.Peek();
            EnsureCanRedo(entry);
            redo.Pop();
            string revision = ApplyAfterState(entry);
            UndoStack(scopeKey).Push(entry);
            if (entry is ConfigHistoryEntry)
            {
                _audit.Add(new HeroAuthoringCommandAuditEntry(DateTimeOffset.UtcNow, "redo", entry.ChangedNodeKeys, entry.BeforeRevision, revision));
            }
            return new HeroAuthoringCommandResult(revision, entry.ChangedNodeKeys);
        }
    }

    public string CurrentRevision() => HeroAuthoringCatalogRevision.Compute(RequireCatalog());

    public HeroAuthoringHistoryStatus ReadHistoryStatus()
    {
        lock (_gate)
        {
            string revision = CurrentRevision();
            string scopeKey = CurrentHistoryScope ?? GlobalHistoryScope;
            Stack<HistoryEntry> undo = UndoStack(scopeKey);
            Stack<HistoryEntry> redo = RedoStack(scopeKey);
            bool canUndo = undo.TryPeek(out HistoryEntry? undoEntry) && CanApply(undoEntry, undo: true);
            bool canRedo = redo.TryPeek(out HistoryEntry? redoEntry) && CanApply(redoEntry, undo: false);
            return new HeroAuthoringHistoryStatus(
                revision,
                canUndo,
                canRedo,
                canUndo ? undo.Count : 0,
                canRedo ? redo.Count : 0);
        }
    }

    public IReadOnlyList<HeroAuthoringCommandAuditEntry> ReadPendingAudit()
    {
        lock (_gate) return _audit.ToArray();
    }

    public void ClearPendingAudit()
    {
        lock (_gate) _audit.Clear();
    }

    private HeroAuthoringCommandResult ChangeLink(
        string parentNamespace,
        int parentId,
        string parentField,
        string targetNamespace,
        int targetId,
        bool add,
        string? expectedRevision,
        int? parameterIndex)
    {
        lock (_gate)
        {
            GameDataCatalog catalog = RequireCatalog(expectedRevision);
            GameDataTable parentTable = ResolveTable(catalog, parentNamespace);
            GameDataRecord parent = parentTable.Record(parentId);
            if (string.Equals(parentField, "action_param", StringComparison.Ordinal))
            {
                if (parameterIndex is null or < 0)
                {
                    throw new ArgumentException("修改动作参数引用时必须提供有效的参数位置。", nameof(parameterIndex));
                }
                EnsureTargetNodeExists(catalog, targetNamespace, targetId);
                GameDataFieldDefinition packedField = parentTable.Fields.FirstOrDefault(field =>
                    string.Equals(field.Key, parentField, StringComparison.Ordinal)
                    && field.Kind == GameDataFieldKind.List)
                    ?? throw new ArgumentException($"{parentTable.DisplayName}不存在动作参数数组。", nameof(parentField));
                var fields = parent.Fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
                var parameters = fields.GetValueOrDefault(packedField.Key, []).ToList();
                if (!add && (parameterIndex >= parameters.Count
                    || !string.Equals(parameters[parameterIndex.Value], targetId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)))
                {
                    throw new ArgumentException($"动作参数第 {parameterIndex.Value + 1} 项当前没有引用 {targetNamespace}:{targetId}。", nameof(targetId));
                }
                while (parameters.Count <= parameterIndex.Value) parameters.Add("0");
                parameters[parameterIndex.Value] = add
                    ? targetId.ToString(CultureInfo.InvariantCulture)
                    : "0";
                fields[packedField.Key] = parameters;
                GameDataRecord parameterReplacement = GameDataDraftService.ValidateRecord(catalog, parentTable, parentId, fields) with
                {
                    SourceOrder = parent.SourceOrder,
                    SourceRow = parent.SourceRow,
                };
                return Commit(
                    catalog,
                    new Dictionary<string, IReadOnlyList<GameDataRecord>>
                    {
                        [parentTable.Key] = ReplaceRecord(parentTable.Records, parameterReplacement),
                    },
                    [$"{parentNamespace}:{parentId}", $"{targetNamespace}:{targetId}"],
                    command: add ? "link-action-parameter" : "unlink-action-parameter");
            }
            if (targetNamespace is "EffectGroup" or "ConditionGroup")
            {
                _ = ResolveGroup(catalog, targetNamespace);
                string groupKey = $"{targetNamespace}:{targetId}";
                if (add && projector.ProjectCatalog(catalog).Nodes.All(node => node.Key != groupKey))
                {
                    throw new KeyNotFoundException($"不存在分组 {groupKey}。");
                }
                GameDataFieldDefinition parentGroupField = RequireGroupField(parentTable, parentField, targetNamespace);
                GameDataRecord groupReplacement = ReplaceGroupLink(
                    catalog,
                    parentTable,
                    parent,
                    parentGroupField,
                    targetId,
                    add);
                return Commit(
                    catalog,
                    new Dictionary<string, IReadOnlyList<GameDataRecord>>
                    {
                        [parentTable.Key] = ReplaceRecord(parentTable.Records, groupReplacement),
                    },
                    [$"{parentNamespace}:{parentId}", $"{targetNamespace}:{targetId}"],
                    command: add ? "link-group" : "unlink-group");
            }
            GameDataTable targetTable = ResolveTable(catalog, targetNamespace);
            targetTable.Record(targetId);
            GameDataFieldDefinition link = RequireLinkField(parentTable, parentField, targetTable.Key);
            GameDataRecord replacement = ReplaceLink(catalog, parentTable, parent, link, targetId, add);
            return Commit(
                catalog,
                new Dictionary<string, IReadOnlyList<GameDataRecord>>
                {
                    [parentTable.Key] = ReplaceRecord(parentTable.Records, replacement),
                },
                [$"{parentNamespace}:{parentId}", $"{targetNamespace}:{targetId}"],
                command: add ? "link-existing" : "unlink");
        }
    }

    private GameDataRecord ClearActionParameterReference(
        GameDataCatalog catalog,
        GameDataTable parentTable,
        GameDataRecord parent,
        string targetNamespace,
        int targetId,
        int? parameterIndex)
    {
        if (parameterIndex is null or < 0)
        {
            throw new ArgumentException("修改动作参数引用时必须提供有效的参数位置。", nameof(parameterIndex));
        }
        EnsureTargetNodeExists(catalog, targetNamespace, targetId);
        GameDataFieldDefinition packedField = parentTable.Fields.FirstOrDefault(field =>
            string.Equals(field.Key, "action_param", StringComparison.Ordinal)
            && field.Kind == GameDataFieldKind.List)
            ?? throw new ArgumentException($"{parentTable.DisplayName}不存在动作参数数组。", nameof(parentTable));
        var fields = parent.Fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var parameters = fields.GetValueOrDefault(packedField.Key, []).ToList();
        if (parameterIndex >= parameters.Count
            || !string.Equals(parameters[parameterIndex.Value], targetId.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            throw new ArgumentException($"动作参数第 {parameterIndex.Value + 1} 项当前没有引用 {targetNamespace}:{targetId}。", nameof(targetId));
        }
        parameters[parameterIndex.Value] = "0";
        fields[packedField.Key] = parameters;
        return GameDataDraftService.ValidateRecord(catalog, parentTable, parent.Id, fields) with
        {
            SourceOrder = parent.SourceOrder,
            SourceRow = parent.SourceRow,
        };
    }

    private void EnsureTargetNodeExists(GameDataCatalog catalog, string targetNamespace, int targetId)
    {
        if (targetNamespace is not ("EffectGroup" or "ConditionGroup"))
        {
            ResolveTable(catalog, targetNamespace).Record(targetId);
            return;
        }

        _ = ResolveGroup(catalog, targetNamespace);
        string targetKey = $"{targetNamespace}:{targetId}";
        if (projector.ProjectCatalog(catalog).Nodes.All(node => node.Key != targetKey))
        {
            throw new KeyNotFoundException($"不存在分组 {targetKey}。");
        }
    }

    private HeroAuthoringCommandResult Commit(
        GameDataCatalog beforeCatalog,
        IReadOnlyDictionary<string, IReadOnlyList<GameDataRecord>> after,
        IReadOnlyList<string> changedNodes,
        int? createdId = null,
        int? createdGroupId = null,
        string command = "edit")
    {
        var before = after.Keys.ToDictionary(
            key => key,
            key => (IReadOnlyList<GameDataRecord>)beforeCatalog.Table(key).Records.ToArray(),
            StringComparer.OrdinalIgnoreCase);
        string beforeRevision = HeroAuthoringCatalogRevision.Compute(beforeCatalog);
        string scopeKey = CurrentHistoryScope ?? GlobalHistoryScope;
        Stack<HistoryEntry> undo = UndoStack(scopeKey);
        if (undo.TryPeek(out HistoryEntry? latest)
            && !string.Equals(latest.AfterRevision, beforeRevision, StringComparison.Ordinal))
        {
            undo.Clear();
            _redoByScope.Remove(scopeKey);
            _audit.Clear();
        }
        store.ReplaceGameDataTables(after);
        string afterRevision = HeroAuthoringCatalogRevision.Compute(RequireCatalog());
        undo.Push(new ConfigHistoryEntry(
            command,
            scopeKey,
            changedNodes,
            beforeRevision,
            afterRevision,
            before,
            after));
        _redoByScope.Remove(scopeKey);
        _audit.Add(new HeroAuthoringCommandAuditEntry(DateTimeOffset.UtcNow, command, changedNodes, beforeRevision, afterRevision));
        return new HeroAuthoringCommandResult(afterRevision, changedNodes, createdId, createdGroupId);
    }

    private string? CurrentHistoryScope => _historyScope.Value;

    private Stack<HistoryEntry> UndoStack(string scopeKey) =>
        GetHistoryStack(_undoByScope, scopeKey);

    private Stack<HistoryEntry> RedoStack(string scopeKey) =>
        GetHistoryStack(_redoByScope, scopeKey);

    private static Stack<HistoryEntry> GetHistoryStack(
        Dictionary<string, Stack<HistoryEntry>> stacks,
        string scopeKey)
    {
        if (!stacks.TryGetValue(scopeKey, out Stack<HistoryEntry>? stack))
        {
            stack = new Stack<HistoryEntry>();
            stacks[scopeKey] = stack;
        }

        return stack;
    }

    private void EnsureCanUndo(HistoryEntry entry)
    {
        if (!CanApply(entry, undo: true))
        {
            throw new InvalidOperationException(
                entry is LayoutHistoryEntry
                    ? "技能布局已变化，无法安全撤销；请刷新工作区后继续。"
                    : "英雄配置工作副本已变化，无法安全撤销；请刷新图谱后继续。");
        }
    }

    private void EnsureCanRedo(HistoryEntry entry)
    {
        if (!CanApply(entry, undo: false))
        {
            throw new InvalidOperationException(
                entry is LayoutHistoryEntry
                    ? "技能布局已变化，无法安全重做；请刷新工作区后继续。"
                    : "英雄配置工作副本已变化，无法安全重做；请刷新图谱后继续。");
        }
    }

    private bool CanApply(HistoryEntry entry, bool undo)
    {
        if (entry is not LayoutHistoryEntry layout)
        {
            string expectedRevision = undo ? entry.AfterRevision : entry.BeforeRevision;
            return string.Equals(CurrentRevision(), expectedRevision, StringComparison.Ordinal);
        }

        if (layoutStore == null)
        {
            return false;
        }

        IReadOnlyList<HeroAuthoringNodeLayout> current = NormalizeLayout(
            layoutStore.ReadHeroAuthoringLayout(layout.ProjectRoot, layout.RootId, layout.ProjectionMode));
        return LayoutEquals(current, undo ? layout.After : layout.Before);
    }

    private string ApplyBeforeState(HistoryEntry entry)
    {
        switch (entry)
        {
            case ConfigHistoryEntry config:
                store.ReplaceGameDataTables(config.BeforeTables);
                return HeroAuthoringCatalogRevision.Compute(RequireCatalog());
            case LayoutHistoryEntry layout:
                RequireLayoutStore().ReplaceHeroAuthoringLayout(
                    layout.ProjectRoot,
                    layout.RootId,
                    layout.ProjectionMode,
                    layout.Before);
                return CurrentRevision();
            default:
                throw new InvalidOperationException("未知的英雄配置历史记录。");
        }
    }

    private string ApplyAfterState(HistoryEntry entry)
    {
        switch (entry)
        {
            case ConfigHistoryEntry config:
                store.ReplaceGameDataTables(config.AfterTables);
                return HeroAuthoringCatalogRevision.Compute(RequireCatalog());
            case LayoutHistoryEntry layout:
                RequireLayoutStore().ReplaceHeroAuthoringLayout(
                    layout.ProjectRoot,
                    layout.RootId,
                    layout.ProjectionMode,
                    layout.After);
                return CurrentRevision();
            default:
                throw new InvalidOperationException("未知的英雄配置历史记录。");
        }
    }

    private IHeroAuthoringLayoutStore RequireLayoutStore() =>
        layoutStore ?? throw new InvalidOperationException("当前服务未配置英雄技能布局存储。");

    private static string LayoutHistoryScope(string rootNamespace, int rootId)
        => HeroAuthoringRootProfiles.HistoryScope(rootNamespace, rootId);

    private static string LayoutStorageMode(string rootNamespace, string projectionMode) =>
        HeroAuthoringRootProfiles.LayoutStorageMode(rootNamespace, projectionMode);

    private static HeroAuthoringNodeLayout[] NormalizeLayout(
        IEnumerable<HeroAuthoringNodeLayout> positions) =>
        positions
            .GroupBy(position => position.InstanceKey, StringComparer.Ordinal)
            .Select(group => group.Last())
            .OrderBy(position => position.InstanceKey, StringComparer.Ordinal)
            .ToArray();

    private static bool LayoutEquals(
        IReadOnlyList<HeroAuthoringNodeLayout> left,
        IReadOnlyList<HeroAuthoringNodeLayout> right) =>
        left.Count == right.Count
        && left.Zip(right).All(pair =>
            string.Equals(pair.First.InstanceKey, pair.Second.InstanceKey, StringComparison.Ordinal)
            && pair.First.X.Equals(pair.Second.X)
            && pair.First.Y.Equals(pair.Second.Y));

    private GameDataCatalog RequireCatalog(string? expectedRevision = null)
    {
        GameDataCatalog catalog = store.ReadGameDataCatalog()
                                  ?? throw new InvalidOperationException("尚未加载英雄配置数据。");
        string revision = HeroAuthoringCatalogRevision.Compute(catalog);
        if (!string.IsNullOrWhiteSpace(expectedRevision)
            && !string.Equals(revision, expectedRevision, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("英雄配置工作副本已变化，请刷新图谱后重试。");
        }
        return catalog;
    }

    private static GameDataTable ResolveTable(GameDataCatalog catalog, string nodeNamespace)
    {
        if (!HeroAuthoringGraphProjector.TryGetTableKey(nodeNamespace, out string tableKey))
        {
            throw new ArgumentException($"节点类型 {nodeNamespace} 不是可编辑表格节点。", nameof(nodeNamespace));
        }
        return catalog.Table(tableKey);
    }

    private static GameDataFieldDefinition RequireLinkField(
        GameDataTable parent,
        string fieldKey,
        string targetTableKey)
    {
        GameDataFieldDefinition field = parent.Fields.FirstOrDefault(
            candidate => string.Equals(candidate.Key, fieldKey, StringComparison.Ordinal))
            ?? throw new ArgumentException($"{parent.DisplayName}不存在字段“{fieldKey}”。", nameof(fieldKey));
        bool declaredReference = string.Equals(field.ReferenceTable, targetTableKey, StringComparison.OrdinalIgnoreCase);
        bool skillReleaseCondition = string.Equals(parent.Key, "skill", StringComparison.OrdinalIgnoreCase)
                                     && string.Equals(field.Key, "condition_id_array", StringComparison.Ordinal)
                                     && string.Equals(targetTableKey, "condition", StringComparison.OrdinalIgnoreCase)
                                     && field.Kind is GameDataFieldKind.List or GameDataFieldKind.DelimitedList;
        if (!declaredReference && !skillReleaseCondition)
        {
            throw new ArgumentException($"“{field.Label}”不能引用目标表 {targetTableKey}。", nameof(fieldKey));
        }
        return field;
    }

    private static (GameDataTable Table, GameDataFieldDefinition GroupField) ResolveGroup(
        GameDataCatalog catalog,
        string groupNamespace)
    {
        string tableKey = groupNamespace switch
        {
            "EffectGroup" => "effect",
            "ConditionGroup" => "condition",
            _ => throw new ArgumentException($"{groupNamespace} 不是支持的配置分组。", nameof(groupNamespace)),
        };
        GameDataTable table = catalog.Table(tableKey);
        GameDataFieldDefinition groupField = table.Fields.FirstOrDefault(
            field => string.Equals(field.Key, "group_id", StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"{table.DisplayName}缺少分组编号字段 group_id。");
        return (table, groupField);
    }

    private static GameDataFieldDefinition RequireGroupField(
        GameDataTable parent,
        string fieldKey,
        string groupNamespace)
    {
        GameDataFieldDefinition field = parent.Fields.FirstOrDefault(
            candidate => string.Equals(candidate.Key, fieldKey, StringComparison.Ordinal))
            ?? throw new ArgumentException($"{parent.DisplayName}不存在字段“{fieldKey}”。", nameof(fieldKey));
        string[] allowed = groupNamespace == "EffectGroup"
            ? ["pre_effect_group_id", "effect_group_id", "post_effect_group_id", "enter_effect", "interval_effect", "finish_effect", "finish_effect_id"]
            : ["success_conds_group_id", "failure_conds_group_id"];
        if (!allowed.Contains(field.Key, StringComparer.Ordinal))
        {
            throw new ArgumentException($"“{field.Label}”不能引用 {groupNamespace}。", nameof(fieldKey));
        }
        return field;
    }

    private static GameDataRecord ReplaceGroupLink(
        GameDataCatalog catalog,
        GameDataTable table,
        GameDataRecord owner,
        GameDataFieldDefinition field,
        int groupId,
        bool add)
    {
        string value = groupId.ToString(CultureInfo.InvariantCulture);
        var fields = owner.Fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        IReadOnlyList<string> current = fields.GetValueOrDefault(field.Key, []);
        if (field.Kind == GameDataFieldKind.List)
        {
            var values = current.ToList();
            if (add)
            {
                if (!values.Contains(value, StringComparer.Ordinal)) values.Add(value);
            }
            else if (!values.Remove(value))
            {
                throw new ArgumentException($"“{field.Label}”当前没有引用分组 {groupId}。", nameof(groupId));
            }
            fields[field.Key] = values;
        }
        else
        {
            if (!add && !current.Contains(value, StringComparer.Ordinal))
            {
                throw new ArgumentException($"“{field.Label}”当前没有引用分组 {groupId}。", nameof(groupId));
            }
            fields[field.Key] = add ? [value] : field.Required ? ["0"] : [];
        }
        return GameDataDraftService.ValidateRecord(catalog, table, owner.Id, fields) with
        {
            SourceOrder = owner.SourceOrder,
            SourceRow = owner.SourceRow,
        };
    }

    private static GameDataRecord ReplaceLink(
        GameDataCatalog catalog,
        GameDataTable table,
        GameDataRecord owner,
        GameDataFieldDefinition field,
        int targetId,
        bool add)
    {
        string value = targetId.ToString(CultureInfo.InvariantCulture);
        var fields = owner.Fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var values = fields.GetValueOrDefault(field.Key, []).ToList();
        bool collection = field.Kind is GameDataFieldKind.List or GameDataFieldKind.DelimitedList;
        if (add && collection)
        {
            if (!values.Contains(value, StringComparer.Ordinal)) values.Add(value);
        }
        else if (add)
        {
            values = [value];
        }
        else if (!values.Remove(value))
        {
            throw new ArgumentException($"“{field.Label}”当前没有引用 {targetId}。", nameof(targetId));
        }
        if (!add && !collection && field.Required) values = ["0"];
        fields[field.Key] = values;
        return GameDataDraftService.ValidateRecord(catalog, table, owner.Id, fields) with
        {
            SourceOrder = owner.SourceOrder,
            SourceRow = owner.SourceRow,
        };
    }

    private static GameDataRecord[] ReplaceRecord(
        IReadOnlyList<GameDataRecord> records,
        GameDataRecord replacement) =>
        records.Select(record => record.Id == replacement.Id ? replacement : record).ToArray();

    private static int NextId(GameDataTable table)
    {
        int next = table.Records.Select(record => record.Id).DefaultIfEmpty(0).Max() + 1;
        if (next <= 0) throw new InvalidOperationException($"{table.DisplayName}没有可分配的有效编号。");
        return next;
    }

    private static int FirstId(GameDataRecord record, string field) =>
        record.Fields.TryGetValue(field, out IReadOnlyList<string>? values)
        && values.Count > 0
        && int.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int id)
            ? id
            : 0;

    private static bool TryNodeIdentity(string key, out string nodeNamespace, out int id)
    {
        int separator = key.LastIndexOf(':');
        nodeNamespace = separator > 0 ? key[..separator] : string.Empty;
        id = 0;
        return separator > 0
               && int.TryParse(key[(separator + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
    }

    private abstract record HistoryEntry(
        string Command,
        string ScopeKey,
        IReadOnlyList<string> ChangedNodeKeys,
        string BeforeRevision,
        string AfterRevision);

    private sealed record ConfigHistoryEntry(
        string Command,
        string ScopeKey,
        IReadOnlyList<string> ChangedNodeKeys,
        string BeforeRevision,
        string AfterRevision,
        IReadOnlyDictionary<string, IReadOnlyList<GameDataRecord>> BeforeTables,
        IReadOnlyDictionary<string, IReadOnlyList<GameDataRecord>> AfterTables)
        : HistoryEntry(Command, ScopeKey, ChangedNodeKeys, BeforeRevision, AfterRevision);

    private sealed record LayoutHistoryEntry(
        string Command,
        string ScopeKey,
        IReadOnlyList<string> ChangedNodeKeys,
        string BeforeRevision,
        string AfterRevision,
        string ProjectRoot,
        string RootNamespace,
        int RootId,
        string ProjectionMode,
        IReadOnlyList<HeroAuthoringNodeLayout> Before,
        IReadOnlyList<HeroAuthoringNodeLayout> After)
        : HistoryEntry(Command, ScopeKey, ChangedNodeKeys, BeforeRevision, AfterRevision);

    private sealed class HistoryScopeHandle(
        HeroAuthoringCommandService owner,
        string? previousScope) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            owner._historyScope.Value = previousScope;
            _disposed = true;
        }
    }
}
