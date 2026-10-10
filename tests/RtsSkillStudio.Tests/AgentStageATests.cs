using System.Text.Json.Nodes;
using RtsSkillStudio.Agent.Llm;
using Xunit;

namespace RtsSkillStudio.Tests;

public sealed class AgentStageATests
{
    [Theory]
    [InlineData("分析技能血源诅咒的配置可行性，仅分析不创建Plan")]
    [InlineData("评估某项需求的机制支持情况")]
    public void CapabilityAssessmentQueriesContractsWithoutAnExistingAsset(string message)
    {
        Assert.True(AgentIntentRouter.IsCapabilityAssessment(message));
        Assert.Equal(AgentIntentKind.Query, AgentIntentRouter.Route(message).Kind);
        Assert.False(AgentIntentRouter.Route(message).ExpectsPlan);
        Assert.False(AgentIntentRouter.IsCapabilityAssessment("TbSkill:100101 配置可行性分析"));
        Assert.False(AgentIntentRouter.IsCapabilityAssessment("从零创建技能并检查配置可行性"));
    }

    [Fact]
    public void IntentRouterSeparatesQueryConfigurationCreateAndUnsupported()
    {
        Assert.Equal(
            AgentIntentKind.Query,
            AgentIntentRouter.Route("这个技能怎么生效").Kind
        );
        Assert.Equal(
            AgentIntentKind.Configuration,
            AgentIntentRouter.Route("把冷却改成 8 秒").Kind
        );
        Assert.Equal(
            AgentIntentKind.Create,
            AgentIntentRouter.Route("从零创建一个范围伤害技能").Kind
        );
        Assert.Equal(
            AgentIntentKind.Unsupported,
            AgentIntentRouter.Route("直接写 Excel 第 10 行第 5 列").Kind
        );
        Assert.Equal(
            AgentIntentKind.Configuration,
            AgentIntentRouter
                .Route("把冷却改成 8 秒，不要直接写 Excel")
                .Kind
        );
        Assert.Equal(
            AgentIntentKind.Ambiguous,
            AgentIntentRouter.Route("修改").Kind
        );
        Assert.Equal(
            AgentIntentKind.Ambiguous,
            AgentIntentRouter.Route("把技能改一下").Kind
        );
        Assert.Equal(
            AgentIntentKind.Query,
            AgentIntentRouter
                .Route(
                    "`[\"1004\", \"1003\", \"1000\", \"10000\"]`这些参数分别代表什么？难道不是改成20000吗？"
                )
                .Kind
        );
    }

    [Fact]
    public void CapabilityQuestionsDoNotRequireASelectedAsset()
    {
        Assert.True(
            AgentIntentRouter.IsCapabilityQuestion(
                "search 是怎么定义搜索范围的"
            )
        );
        Assert.True(
            AgentIntentRouter.IsCapabilityQuestion(
                "Search 配表有哪些字段"
            )
        );
        Assert.True(
            AgentIntentRouter.IsCapabilityQuestion(
                "shape_param 是什么"
            )
        );
        Assert.False(
            AgentIntentRouter.IsCapabilityQuestion(
                "TbSkill:100101 的 search 是怎么定义的"
            )
        );
        Assert.False(
            AgentIntentRouter.IsCapabilityQuestion(
                "把 search 范围改成 500"
            )
        );
    }

    [Fact]
    public void ToolProtocolParsesAndRemovesItsEnvelope()
    {
        const string response = """
            需要读取技能链路。
            ```json
            {"type":"skill_studio_tool_calls","calls":[{"name":"get_graph","arguments":{"root":"TbSkill:100101","depth":32}}]}
            ```
            """;

        IReadOnlyList<AgentToolCall> calls = AgentToolProtocol.Parse(response);

        Assert.Single(calls);
        Assert.Equal("get_graph", calls[0].Name);
        Assert.Equal(
            "TbSkill:100101",
            calls[0].Arguments?["root"]?.GetValue<string>()
        );
        Assert.Equal(
            "需要读取技能链路。",
            AgentToolProtocol.RemoveToolCallBlock(response)
        );
    }

    [Fact]
    public void ToolProtocolParsesDeepSeekDsmlToolCalls()
    {
        string response = """
            <\uFF5C\uFF5CDSML\uFF5C\uFF5C calls>
            <\uFF5C\uFF5CDSML\uFF5C\uFF5C invoke name="get_capability_context">
            <\uFF5C\uFF5CDSML\uFF5C\uFF5C parameter name="arguments" string="false">{"query":"AddSkill","limit":50}</\uFF5C\uFF5CDSML\uFF5C\uFF5C parameter>
            <\uFF5C\uFF5CDSML\uFF5C\uFF5C parameter name="name" string="true">get_capability_context</\uFF5C\uFF5CDSML\uFF5C\uFF5C parameter>
            </\uFF5C\uFF5CDSML\uFF5C\uFF5C invoke>
            </\uFF5C\uFF5CDSML\uFF5C\uFF5C calls>
            """.Replace(
            @"\uFF5C",
            "\uFF5C",
            StringComparison.Ordinal
        );

        IReadOnlyList<AgentToolCall> calls = AgentToolProtocol.Parse(response);

        Assert.Single(calls);
        Assert.Equal("get_capability_context", calls[0].Name);
        Assert.Equal(
            "AddSkill",
            calls[0].Arguments?["query"]?.GetValue<string>()
        );
        Assert.Equal(
            "",
            AgentToolProtocol.RemoveToolCallBlock(response)
        );
    }

    [Fact]
    public void PlanParserIgnoresJsonThatIsNotASkillConfigPlan()
    {
        const string response = """
            查询结果如下。
            ```json
            {"name":"damage","value":4}
            ```
            """;

        SkillConfigPlanExtraction extraction =
            SkillConfigPlanParser.Extract(response);

        Assert.Null(extraction.PlanJson);
        Assert.Empty(extraction.Errors);
        Assert.Equal(
            response,
            SkillConfigPlanParser.RemovePlanBlock(response)
        );
    }

    [Fact]
    public void ToolProtocolExtractsAndGuardsTheAuthoritativeAssetName()
    {
        const string graphResult = """
            {
              "Nodes": [
                {
                  "Key": "TbSkill:100402",
                  "Label": "英雄-幻影刺客-窒碍短匕",
                  "IsFocus": true,
                  "Fields": {
                    "__remark_2": ["英雄-幻影刺客-窒碍短匕"],
                    "__executor": [
                      "造成伤害 · Damage · 枚举值 4"
                    ]
                  }
                },
                {
                  "Key": "TbEntity:9001",
                  "Label": "权威实体名",
                  "IsFocus": false,
                  "Fields": {
                    "__remark_2": ["权威实体名"]
                  }
                }
              ]
            }
            """;
        AgentAssetIdentity? identity =
            AgentToolProtocol.FindAuthoritativeIdentity(
                [
                    new AgentToolExecution(
                        "get_graph",
                        "{}",
                        graphResult,
                        false
                    )
                ]
            );

        Assert.NotNull(identity);
        Assert.Equal("TbSkill:100402", identity.Key);
        Assert.Equal("英雄-幻影刺客-窒碍短匕", identity.CanonicalName);
        Assert.Equal(
            ["Damage"],
            AgentToolProtocol.FindExecutorActionKeys(
                [
                    new AgentToolExecution(
                        "get_graph",
                        "{}",
                        graphResult,
                        false
                    )
                ]
            )
        );
        Assert.True(
            AgentToolProtocol.HasParameterContractEvidence(
                [
                    new AgentToolExecution(
                        "get_capability_context",
                        """{"actionKey":"Damage"}""",
                        """
                        {
                          "Effects": [
                            {
                              "Key": "Damage",
                              "Parameters": [
                                {
                                  "Index": 3,
                                  "Key": "attackScale",
                                  "Label": "攻击倍率",
                                  "Scale": 10000
                                }
                              ]
                            }
                          ]
                        }
                        """,
                        false
                    )
                ]
            )
        );
        Assert.True(
            AgentToolProtocol.TextContainsAuthoritativeName(
                "### 技能概览\n名称：英雄 - 幻影刺客 - 窒碍短匕",
                identity
            )
        );
        Assert.False(
            AgentToolProtocol.TextContainsAuthoritativeName(
                "### 技能概览\n名称：英桀 - 暗影刺客 - 瞬翼短弹",
                identity
            )
        );
        Assert.Contains(
            "英雄-幻影刺客-窒碍短匕",
            AgentToolProtocol.BuildIdentityRepairInstructions(identity)
        );

        const string modelText = """
            这是技能 **"英桀 - 暗影刺客 - 瞬影短浴" (TbSkill:100402)**。

            ### 技能详情分析：英桀 - 霆光刺客 - 瞬斩短弹
            ### 一、技能本体（TbSkill:100402）
            * **技能名称（逐字引用）**：`英雄-幻影刺客-窒息短匕`
            *   **名称**: 英雄 - 幻影刺客 - 窒碍短匕
                * `name`: 英雄 - 幻影刺客 - 窒碍短匕首

            | 字段 | 值 |
            |---|---|
            | __remark_2（名称） | \u82F1\u96C4-\u5E7F\u5F79\u523A\u5BA2-\u7A92\u788D\u77ED\u5315 |
            | **技能名称** | 英豪 - 雷火枪炮 - 榴霰弹 |
            """;
        string normalized = AgentToolProtocol.NormalizeAssetIdentityText(
            modelText,
            identity
        );

        Assert.Contains("英雄-幻影刺客-窒碍短匕", normalized);
        Assert.DoesNotContain("瞬影短浴", normalized);
        Assert.DoesNotContain("瞬斩短弹", normalized);
        Assert.DoesNotContain("窒碍短匕首", normalized);
        Assert.DoesNotContain("雷火枪炮", normalized);
        Assert.DoesNotContain(@"\u82F1", normalized);
        Assert.Contains("### 一、技能本体（TbSkill:100402）", normalized);

        string related = AgentToolProtocol.NormalizeReferencedIdentityText(
            "引用实体：**错误名称** (TbEntity: 9001)",
            AgentToolProtocol.FindReferencedIdentities(
                [
                    new AgentToolExecution(
                        "get_graph",
                        "{}",
                        graphResult,
                        false
                    )
                ]
            )
        );
        Assert.Contains("权威实体名 (TbEntity: 9001)", related);
        Assert.DoesNotContain("错误名称", related);
    }

    [Fact]
    public void ToolCallBlockRemovalHandlesNestedJson()
    {
        const string modelText = """
            最终回答内容
            ```json
            {"type":"skill_studio_tool_calls","calls":[{"name":"get_graph","arguments":{"root":"TbSkill:100502","depth":32}}]}
            ```
            """;

        string normalized = AgentToolProtocol.RemoveToolCallBlock(
            modelText
        );

        Assert.Equal("最终回答内容", normalized);
    }

    [Fact]
    public void DamageChangeSummaryUsesContractScaleAndExactEnumName()
    {
        const string graphResult = """
            {
              "Nodes": [
                {
                  "Key": "TbEffect:100400041",
                  "Id": 100400041,
                  "TableKey": "effect",
                  "SourceRow": 165,
                  "Fields": {
                    "action_type": ["伤害"],
                    "__executor": ["造成伤害 · Damage · 枚举值 4"],
                    "action_param": ["1004", "1003", "1000", "10000"]
                  }
                }
              ]
            }
            """;
        const string capabilityResult = """
            {
              "Effects": [
                {
                  "Key": "Damage",
                  "Parameters": [
                    {
                      "Index": 2,
                      "Key": "fixedDamage",
                      "Scale": 10000
                    }
                  ]
                }
              ],
              "Enums": [
                {
                  "Name": "NumericType",
                  "Values": [
                    {"Name": "BaseStat", "Value": 1003},
                    {
                      "Name": "ElementDamage",
                      "Value": 2222,
                      "Alias": "雷电"
                    }
                  ]
                }
              ]
            }
            """;

        DamageChangeSummary? summary = DamageChangeSummaryBuilder.TryBuild(
            "我想把伤害改成固定12345雷电伤害，先改这个",
            [
                new AgentToolExecution(
                    "get_graph",
                    "{}",
                    graphResult,
                    false
                ),
                new AgentToolExecution(
                    "get_capability_context",
                    "{}",
                    capabilityResult,
                    false
                )
            ]
        );

        Assert.NotNull(summary);
        Assert.Contains("ElementDamage", summary.Text);
        Assert.Contains("2222", summary.Text);
        Assert.Contains("123450000", summary.Text);
        Assert.Contains(
            "[\"1004\", \"2222\", \"123450000\", \"10000\"]",
            summary.Text
        );
        Assert.NotNull(summary.ConfirmationQuestion);
    }

    [Fact]
    public async Task PlanValidatorUsesTheGeneratedJsonSchema()
    {
        var validator = new SkillConfigPlanValidator(FindContractRoot());
        var valid = new JsonObject
        {
            ["schemaVersion"] = 0,
            ["planId"] = "test-plan",
            ["base"] = new JsonObject
            {
                ["workspaceId"] = "studio-workspace",
                ["revision"] = "r1",
                ["sourceHash"] =
                    "0000000000000000000000000000000000000000000000000000000000000000",
                ["capabilityRegistryVersion"] = "0.1.0",
                ["defaultValueContractVersion"] = "0.1.0",
                ["defaultMechanismContractVersion"] = "0.1.0"
            },
            ["request"] = new JsonObject { ["text"] = "修改物品效果组" },
            ["status"] = "Ready",
            ["operations"] = new JsonArray
            {
                new JsonObject
                {
                    ["operationId"] = "op-1",
                    ["kind"] = "ModifyAsset",
                    ["asset"] = new JsonObject
                    {
                        ["binding"] = "Existing",
                        ["namespace"] = "TbItem",
                        ["id"] = 100604
                    },
                    ["fields"] = new JsonObject
                    {
                        ["effect_group_id"] = new JsonObject
                        {
                            ["value"] = 10060000,
                            ["source"] = "UserEdited",
                            ["evidence"] = new JsonArray()
                        }
                    }
                }
            }
        };

        SkillConfigPlanValidationResult result = await validator.ValidateAsync(
            valid.ToJsonString(),
            CancellationToken.None
        );
        SkillConfigPlanValidationResult invalid = await validator.ValidateAsync(
            """{"schemaVersion":0,"planId":"bad","status":"Ready","operations":[]}""",
            CancellationToken.None
        );

        Assert.True(result.IsValid, string.Join("; ", result.Errors));
        Assert.False(invalid.IsValid);
        Assert.NotEmpty(invalid.Errors);
    }

    private static string FindContractRoot(
        [System.Runtime.CompilerServices.CallerFilePath] string sourceFile = ""
    )
    {
        DirectoryInfo? current = new(
            Path.GetDirectoryName(sourceFile) ?? Environment.CurrentDirectory
        );
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "RtsSkillStudio.sln")))
            {
                return Path.Combine(
                    current.FullName,
                    "contracts",
                    "rts-skill-agent"
                );
            }
            current = current.Parent;
        }

        current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "RtsSkillStudio.sln")))
            {
                return Path.Combine(
                    current.FullName,
                    "contracts",
                    "rts-skill-agent"
                );
            }
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
