using TianshuDM.Application.HeroAuthoring;
using TianshuDM.Domain.GameData;
using TianshuDM.Domain.HeroAuthoring;
using Xunit;

namespace RtsSkillStudio.Tests;

public sealed class HeroAuthoringGraphProjectorTests
{
    [Fact]
    public void SummonUnitResolvesTheDynamicUnitTable()
    {
        HeroAuthoringSemanticSchema schema = Schema(
            new HeroAuthoringActionDefinition(
                "SummonUnit",
                11,
                "召唤单位",
                2,
                6,
                [
                    Parameter(0, "unitType", HeroAuthoringParameterKind.Enum),
                    Parameter(
                        1,
                        "unitId",
                        HeroAuthoringParameterKind.Reference,
                        "UnitByType"
                    )
                ]
            )
        );
        GameDataCatalog catalog = Catalog(
            Table(
                "skill",
                "Skill",
                [
                    Field("Id"),
                    Field("effect_group_id")
                ],
                Record(1, ("effect_group_id", ["100"]))
            ),
            Table(
                "effect",
                "Effect",
                [
                    Field("Id"),
                    Field("group_id"),
                    EnumField("action_type", ("召唤单位", "SummonUnit", 11)),
                    Field("action_param")
                ],
                Record(
                    10,
                    ("group_id", ["100"]),
                    ("action_type", ["召唤单位"]),
                    ("action_param", ["8", "20"])
                )
            ),
            Table(
                "soldier",
                "Soldier",
                [
                    Field("Id"),
                    Field("skills", referenceTable: "skill")
                ],
                Record(20, ("skills", []))
            )
        );

        HeroAuthoringGraph graph = new HeroAuthoringGraphProjector(
            new FakeSchemaSource(schema)
        ).ProjectBehavior(catalog, "TbSkill", 1, 8);

        Assert.Contains(
            graph.Edges,
            edge => edge.Source == "TbEffect:10" &&
                edge.Target == "TbSoldier:20"
        );
        Assert.DoesNotContain(
            graph.Nodes,
            node => node.Key == "UnitByType:20"
        );
    }

    [Fact]
    public void SearchTableIdsResolveTheirConfiguredUnitTable()
    {
        HeroAuthoringSemanticSchema schema = Schema();
        GameDataCatalog catalog = Catalog(
            Table(
                "skill",
                "Skill",
                [
                    Field("Id"),
                    Field("effect_group_id"),
                    Field("search_target", referenceTable: "search")
                ],
                Record(
                    1,
                    ("effect_group_id", ["100"]),
                    ("search_target", ["5"])
                )
            ),
            Table(
                "effect",
                "Effect",
                [
                    Field("Id"),
                    Field("group_id"),
                    Field("action_type")
                ],
                Record(10, ("group_id", ["100"]))
            ),
            Table(
                "search",
                "Search",
                [
                    Field("Id"),
                    EnumField("type", ("士兵", "Soldier", 8)),
                    Field("table_id")
                ],
                Record(
                    5,
                    ("type", ["士兵"]),
                    ("table_id", ["20"])
                )
            ),
            Table(
                "soldier",
                "Soldier",
                [Field("Id")],
                Record(20)
            )
        );

        HeroAuthoringGraph graph = new HeroAuthoringGraphProjector(
            new FakeSchemaSource(schema)
        ).ProjectBehavior(catalog, "TbSkill", 1, 8);

        Assert.Contains(
            graph.Edges,
            edge => edge.Source == "TbSearch:5" &&
                edge.Target == "TbSoldier:20" &&
                edge.SourceField == "table_id"
        );
    }

    [Fact]
    public void UnsupportedSummonUnitTypesRemainVisibleInTheGraph()
    {
        HeroAuthoringSemanticSchema schema = Schema(
            new HeroAuthoringActionDefinition(
                "SummonUnit",
                11,
                "召唤单位",
                2,
                6,
                [
                    Parameter(0, "unitType", HeroAuthoringParameterKind.Enum),
                    Parameter(
                        1,
                        "unitId",
                        HeroAuthoringParameterKind.Reference,
                        "UnitByType"
                    )
                ]
            )
        );
        GameDataCatalog catalog = Catalog(
            Table(
                "skill",
                "Skill",
                [Field("Id"), Field("effect_group_id")],
                Record(1, ("effect_group_id", ["100"]))
            ),
            Table(
                "effect",
                "Effect",
                [
                    Field("Id"),
                    Field("group_id"),
                    EnumField("action_type", ("召唤单位", "SummonUnit", 11)),
                    Field("action_param")
                ],
                Record(
                    10,
                    ("group_id", ["100"]),
                    ("action_type", ["召唤单位"]),
                    ("action_param", ["256", "7"])
                )
            )
        );

        HeroAuthoringGraph graph = new HeroAuthoringGraphProjector(
            new FakeSchemaSource(schema)
        ).ProjectBehavior(catalog, "TbSkill", 1, 8);

        Assert.Contains(
            graph.Nodes,
            node => node.Key == "UnitType:256" &&
                node.Kind == "未支持召唤类型"
        );
        Assert.Contains(
            graph.Edges,
            edge => edge.Source == "TbEffect:10" &&
                edge.Target == "UnitType:256"
        );
    }

    [Fact]
    public void BehaviorProjectionSupportsPathsDeeperThanTwelve()
    {
        HeroAuthoringSemanticSchema schema = Schema(
            new HeroAuthoringActionDefinition(
                "Research",
                14,
                "重新索敌",
                2,
                2,
                [
                    Parameter(
                        0,
                        "searchId",
                        HeroAuthoringParameterKind.Reference,
                        "TbSearch"
                    ),
                    Parameter(
                        1,
                        "effectGroupId",
                        HeroAuthoringParameterKind.Reference,
                        "EffectGroup"
                    )
                ]
            )
        );
        var tables = new List<GameDataTable>
        {
            Table(
                "skill",
                "Skill",
                [Field("Id"), Field("effect_group_id")],
                Record(1, ("effect_group_id", ["100"]))
            ),
            Table(
                "search",
                "Search",
                [Field("Id")],
                Record(1)
            )
        };
        var effectRecords = new List<GameDataRecord>();
        for (int index = 0; index < 15; index++)
        {
            int effectId = 1000 + index;
            int groupId = 100 + index;
            int nextGroupId = groupId + 1;
            effectRecords.Add(
                Record(
                    effectId,
                    ("group_id", [groupId.ToString()]),
                    ("action_type", ["重新索敌"]),
                    (
                        "action_param",
                        ["1", nextGroupId.ToString()]
                    )
                )
            );
        }
        tables.Add(
            Table(
                "effect",
                "Effect",
                [
                    Field("Id"),
                    Field("group_id"),
                    EnumField(
                        "action_type",
                        ("重新索敌", "Research", 14)
                    ),
                    Field("action_param")
                ],
                [.. effectRecords]
            )
        );

        HeroAuthoringGraph graph = new HeroAuthoringGraphProjector(
            new FakeSchemaSource(schema)
        ).ProjectBehavior(Catalog([.. tables]), "TbSkill", 1, 32);

        Assert.Contains(
            graph.Nodes,
            node => node.Key == "EffectGroup:110"
        );
    }

    private static HeroAuthoringSemanticSchema Schema(
        params HeroAuthoringActionDefinition[] effects) =>
        new(1, effects, [], []);

    private static HeroAuthoringParameterDefinition Parameter(
        int index,
        string key,
        HeroAuthoringParameterKind kind,
        string? referenceTarget = null) =>
        new(index, key, key, kind, ReferenceTarget: referenceTarget);

    private static GameDataCatalog Catalog(params GameDataTable[] tables) =>
        new(tables);

    private static GameDataTable Table(
        string key,
        string displayName,
        IReadOnlyList<GameDataFieldDefinition> fields,
        params GameDataRecord[] records) =>
        new(
            key,
            displayName,
            "test",
            $"{displayName}.xlsx",
            "hash",
            "Sheet1",
            fields,
            records
        );

    private static GameDataRecord Record(
        int id,
        params (string Key, string[] Values)[] fields) =>
        new(
            id,
            fields.ToDictionary(
                field => field.Key,
                field => (IReadOnlyList<string>)field.Values,
                StringComparer.OrdinalIgnoreCase
            )
        );

    private static GameDataFieldDefinition Field(
        string key,
        string? referenceTable = null) =>
        new(
            key,
            key,
            referenceTable is null
                ? GameDataFieldKind.Integer
                : GameDataFieldKind.Reference,
            referenceTable is null ? "int" : $"int#ref={referenceTable}",
            0,
            1,
            false,
            referenceTable,
            [],
            null,
            false
        );

    private static GameDataFieldDefinition EnumField(
        string key,
        params (string Value, string Code, int LegacyValue)[] values) =>
        new(
            key,
            key,
            GameDataFieldKind.Enum,
            "enum",
            0,
            1,
            false,
            null,
            values.Select(
                value => new GameDataOption(
                    value.Value,
                    value.Value,
                    value.Code,
                    value.LegacyValue
                )
            ).ToArray(),
            null,
            false
        );

    private sealed class FakeSchemaSource(
        HeroAuthoringSemanticSchema schema
    ) : IHeroAuthoringSemanticSchemaSource
    {
        public HeroAuthoringSemanticSchema Read() => schema;
    }
}
