using TianshuDM.Application.GameData;
using TianshuDM.Domain.GameData;
using System.Xml.Linq;

namespace TianshuDM.Infrastructure.Excel;

public sealed class UnitGameDataCatalogReader(IGameDataWorkbookReader workbookReader)
    : IGameDataCatalogReader
{
    private static readonly GameDataSourceDefinition[] Sources =
    [
        new("hero", "英雄", "英雄管理", "Unit/Hero.xlsx"),
        new("hero-skin", "英雄皮肤", "英雄管理", "Unit/HeroSkin.xlsx"),
        new("hero-upgrade", "英雄升级", "英雄管理", "Unit/HeroUpgrade.xlsx"),
        new("soldier", "士兵", "单位管理", "Unit/Soldier.xlsx"),
        new("soldier-upgrade", "士兵升级", "单位管理", "Unit/SoldierUpgrade.xlsx"),
        new("building", "建筑", "单位管理", "Unit/Building.xlsx"),
        new("block", "地块", "单位管理", "Unit/Block.xlsx"),
        new("trap", "机关", "单位管理", "Unit/Trap.xlsx"),
        new("item", "物品", "单位管理", "Unit/Item.xlsx"),
        new("battle-hero-shop", "英雄商店", "战斗配置", "Battle/BattleHeroShop.xlsx"),
        new("battle-soldier-level-up", "士兵战斗升级", "战斗配置", "Battle/BattleSoldierLevelUp.xlsx"),
        new("card", "卡牌", "单位管理", "Unit/Card.xlsx"),
        new("inventory-item", "通用道具", "任务引用数据", "Inventory/InventoryItem.xlsx"),
        new("skill", "技能", "英雄配置图谱", "Skill/Skill.xlsx"),
        new("effect", "效果", "英雄配置图谱", "Skill/Effect.xlsx"),
        new("buff", "Buff", "英雄配置图谱", "Skill/Buff.xlsx"),
        new("condition", "条件", "英雄配置图谱", "Skill/Condition.xlsx"),
        new("search", "目标搜索", "英雄配置图谱", "Skill/Search.xlsx"),
        new("bullet", "子弹", "英雄配置图谱", "Skill/Bullet.xlsx"),
        new("damage-pipeline", "伤害管线", "英雄配置图谱", "Skill/DamagePipeline.xlsx"),
        new("skill-resource", "技能资源", "英雄配置图谱", "Skill/SkillResource.xlsx"),
        new("hero-skill-description", "英雄技能说明", "英雄配置图谱", "Skill/HeroSkillDes.xlsx"),
        new("resource", "资源", "英雄配置图谱", "Resource.xlsx"),
        new("equipment", "装备", "英雄配置图谱", "Equipment/Equipment.xlsx"),
        new("equipment-upgrade", "装备升级", "英雄配置图谱", "Equipment/EquipmentUpgrade.xlsx"),
        new("random-bag", "随机包", "英雄配置图谱", "Random/RandomBag.xlsx"),
        new("random-set", "随机集合", "英雄配置图谱", "Random/RandomSet.xlsx"),
        new("random-card", "随机卡牌", "英雄配置图谱", "Random/RandomCard.xlsx"),
        new("view-function-component", "表现组件", "英雄配置图谱", "ViewFunctionComponent.xlsx"),
    ];

    private static readonly Dictionary<string, string> ManagedReferenceKeys =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TbHero"] = "hero",
            ["TbHeroSkin"] = "hero-skin",
            ["TbHeroUpgrade"] = "hero-upgrade",
            ["TbSoldier"] = "soldier",
            ["TbSoldierUpgrade"] = "soldier-upgrade",
            ["TbBuilding"] = "building",
            ["TbBlock"] = "block",
            ["TbTrap"] = "trap",
            ["TbItem"] = "item",
            ["TbBattleHeroShop"] = "battle-hero-shop",
            ["TbBattleSoldierLevelUp"] = "battle-soldier-level-up",
            ["TbCard"] = "card",
            ["TbInventoryItem"] = "inventory-item",
            ["TbSkill"] = "skill",
            ["TbEffect"] = "effect",
            ["TbBuff"] = "buff",
            ["TbCondition"] = "condition",
            ["TbSearch"] = "search",
            ["TbBullet"] = "bullet",
            ["TbDamagePipeline"] = "damage-pipeline",
            ["TbSkillResource"] = "skill-resource",
            ["TbHeroSkillDes"] = "hero-skill-description",
            ["TbResource"] = "resource",
            ["TbEquipment"] = "equipment",
            ["TbEquipmentUpgrade"] = "equipment-upgrade",
            ["TbRandomBag"] = "random-bag",
            ["TbRandomSet"] = "random-set",
            ["TbRandomCard"] = "random-card",
            ["TbViewFunctionComponent"] = "view-function-component",
        };

    private static readonly Dictionary<string, string> ImplicitManagedReferenceFields =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["skill:effect_id"] = "skill-resource",
            ["buff:effect_id"] = "skill-resource",
        };

    private static readonly HashSet<string> RuntimeMultiSelectEnumFields =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "search:type",
        };

    public GameDataCatalog Read(string excelDataRoot)
    {
        return ReadSources(excelDataRoot, Sources);
    }

    public GameDataCatalog Read(
        string excelDataRoot,
        IReadOnlyCollection<string> tableKeys
    )
    {
        HashSet<string> selected = tableKeys.ToHashSet(
            StringComparer.OrdinalIgnoreCase
        );
        return ReadSources(
            excelDataRoot,
            Sources.Where(source => selected.Contains(source.Key))
        );
    }

    private GameDataCatalog ReadSources(
        string excelDataRoot,
        IEnumerable<GameDataSourceDefinition> sources
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(excelDataRoot);
        string dataRoot = Path.GetFullPath(excelDataRoot);
        HashSet<string> flagsEnums = ReadFlagsEnums(dataRoot);
        var runtimeEnumCache = new Dictionary<string, IReadOnlyList<GameDataOption>?>(
            StringComparer.Ordinal);
        GameDataTable[] tables = sources.Select(
                source =>
                {
                    string workbookPath = Path.Combine(
                        dataRoot,
                        source.RelativeWorkbookPath.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(workbookPath))
                    {
                        throw new FileNotFoundException(
                            $"缺少{source.DisplayName}工作簿。",
                            workbookPath);
                    }

                    GameDataTable table = workbookReader.Read(
                        new GameDataTableSource(
                            source.Key,
                            source.DisplayName,
                            source.Category,
                            source.RelativeWorkbookPath,
                            workbookPath));
                    return table with
                    {
                        Fields = table.Fields.Select(field =>
                            NormalizeField(
                                source.Key,
                                field,
                                flagsEnums,
                                dataRoot,
                                runtimeEnumCache)).ToArray(),
                    };
                })
            .ToArray();
        return new GameDataCatalog(tables);
    }

    private static GameDataFieldDefinition NormalizeField(
        string tableKey,
        GameDataFieldDefinition field,
        HashSet<string> flagsEnums,
        string dataRoot,
        Dictionary<string, IReadOnlyList<GameDataOption>?> runtimeEnumCache)
    {
        GameDataFieldDefinition normalized = field;
        if (normalized.ReferenceTable is not null
            && ManagedReferenceKeys.TryGetValue(normalized.ReferenceTable, out string? declaredKey))
        {
            normalized = normalized with { ReferenceTable = declaredKey };
        }

        if (ImplicitManagedReferenceFields.TryGetValue($"{tableKey}:{field.Key}", out string? inferredKey))
        {
            normalized = normalized with { ReferenceTable = inferredKey };
        }

        string? enumName = RuntimeEnumName(normalized);
        if (enumName is not null)
        {
            if (!runtimeEnumCache.TryGetValue(enumName, out IReadOnlyList<GameDataOption>? generatedOptions))
            {
                generatedOptions = UnityGeneratedEnumOptionReader.Read(dataRoot, enumName);
                runtimeEnumCache[enumName] = generatedOptions;
            }

            if (generatedOptions is not null)
            {
                normalized = normalized with
                {
                    Options = UnityGeneratedEnumOptionReader.Merge(normalized.Options, generatedOptions),
                };
            }
        }

        return normalized.Kind == GameDataFieldKind.Enum
               && flagsEnums.Contains(normalized.RawType)
               && RuntimeMultiSelectEnumFields.Contains($"{tableKey}:{normalized.Key}")
            ? normalized with { AllowsMultipleEnumValues = true }
            : normalized;
    }

    private static string? RuntimeEnumName(GameDataFieldDefinition field)
    {
        if (field.Kind is not (GameDataFieldKind.Enum or GameDataFieldKind.List))
        {
            return null;
        }

        string[] parts = field.RawType.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        string candidate = parts[^1].Trim();
        return candidate.Length > 0 && candidate.All(character => char.IsLetterOrDigit(character) || character == '_')
            ? candidate
            : null;
    }

    private static HashSet<string> ReadFlagsEnums(string dataRoot)
    {
        string? excelRoot = Path.GetDirectoryName(dataRoot);
        if (string.IsNullOrWhiteSpace(excelRoot))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        string definitionPath = Path.Combine(excelRoot, "Defines", "builtin.xml");
        if (!File.Exists(definitionPath))
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        XDocument document = XDocument.Load(definitionPath, LoadOptions.None);
        return document.Descendants("enum")
            .Where(element => string.Equals(
                element.Attribute("flags")?.Value,
                "TRUE",
                StringComparison.OrdinalIgnoreCase))
            .Select(element => element.Attribute("name")?.Value)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
    }

    private sealed record GameDataSourceDefinition(
        string Key,
        string DisplayName,
        string Category,
        string RelativeWorkbookPath);
}
