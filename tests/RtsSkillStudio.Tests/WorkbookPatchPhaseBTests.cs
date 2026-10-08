using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RtsSkillStudio.Agent.Llm;
using RtsSkillStudio.Agent.Patch;
using RtsSkillStudio.Api.Workspaces;
using TianshuDM.Application.GameData;
using TianshuDM.Domain.GameData;
using TianshuDM.Infrastructure.Excel;
using Xunit;

namespace RtsSkillStudio.Tests;

public sealed class WorkbookPatchPhaseBTests
{
    private const string SourceHashA =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string SourceHashB =
        "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string RevisionA = "revision-a";
    private const string RevisionB = "revision-b";

    [Fact]
    public void CompilerProducesExpectedCooldownPatchAndIsDeterministic()
    {
        WorkbookPatchRegistry registry = LoadRegistry();
        var compiler = new WorkbookPatchCompiler(registry);
        WorkbookPatchWorkspace workspace = CreateWorkspace(
            cooldown: "5000"
        );
        string plan = CreateCooldownPlan(
            workspace,
            registry,
            cooldown: 8,
            unit: "s"
        );

        WorkbookPatchCompileResult first = compiler.Compile(plan, workspace);
        WorkbookPatchCompileResult second = compiler.Compile(plan, workspace);

        Assert.Equal("Compiled", first.Status);
        Assert.NotNull(first.Patch);
        Assert.NotNull(first.PatchJson);
        Assert.Equal(first.PatchJson, second.PatchJson);
        Assert.Single(first.Patch.Commands);
        Assert.Equal("UpdateNode", first.Patch.Commands[0].Kind);
        Assert.Single(first.Patch.FieldChanges);
        Assert.Equal("cd_time", first.Patch.FieldChanges[0].Field);
        Assert.Equal(
            "8000",
            WorkbookFieldChangeJson.RawText(
                first.Patch.FieldChanges[0].After
            )
        );
        Assert.Equal(
            first.Patch.PatchId,
            WorkbookPatchJsonUtilities.ComputePatchId(first.Patch)
        );
    }

    [Fact]
    public void CompilerRejectsUnsupportedFieldValueAndUnitBeforeApply()
    {
        WorkbookPatchRegistry registry = LoadRegistry();
        var compiler = new WorkbookPatchCompiler(registry);
        WorkbookPatchWorkspace workspace = CreateWorkspace(
            cooldown: "5000"
        );

        WorkbookPatchCompileResult unknown = compiler.Compile(
            CreateModifySkillPlan(
                workspace,
                registry,
                new JsonObject
                {
                    ["unknownField"] = new JsonObject
                    {
                        ["value"] = 1,
                        ["unit"] = "s",
                        ["source"] = "UserEdited"
                    }
                }
            ),
            workspace
        );
        WorkbookPatchCompileResult badType = compiler.Compile(
            CreateCooldownPlan(
                workspace,
                registry,
                new JsonObject { ["not"] = "a number" }
            ),
            workspace
        );
        WorkbookPatchCompileResult badUnit = compiler.Compile(
            CreateCooldownPlan(
                workspace,
                registry,
                cooldown: 8,
                unit: "minute"
            ),
            workspace
        );
        WorkbookPatchCompileResult badEnum = compiler.Compile(
            CreateModifySkillPlan(
                workspace,
                registry,
                new JsonObject
                {
                    ["skill_type"] = new JsonObject
                    {
                        ["value"] = "NotAnEnum",
                        ["source"] = "UserEdited"
                    }
                }
            ),
            workspace
        );

        Assert.Equal(
            "compiler.unknown_semantic_field",
            Assert.Single(unknown.Errors).Code
        );
        Assert.Equal(
            "compiler.invalid_value_type",
            Assert.Single(badType.Errors).Code
        );
        Assert.Equal(
            "compiler.invalid_unit",
            Assert.Single(badUnit.Errors).Code
        );
        Assert.Equal(
            "compiler.invalid_value_type",
            Assert.Single(badEnum.Errors).Code
        );
        Assert.Contains("skill_type", Assert.Single(badEnum.Errors).Message);
    }

    [Fact]
    public async Task ValidatorRejectsRevisionAndSourceHashMismatch()
    {
        WorkbookPatchRegistry registry = LoadRegistry();
        var compiler = new WorkbookPatchCompiler(registry);
        WorkbookPatchWorkspace workspace = CreateWorkspace(
            cooldown: "5000"
        );
        WorkbookPatchCompileResult compiled = compiler.Compile(
            CreateCooldownPlan(workspace, registry, cooldown: 8, unit: "s"),
            workspace
        );
        Assert.NotNull(compiled.PatchJson);

        var validator = new WorkbookPatchValidator(
            FindContractRoot(),
            registry
        );
        WorkbookPatchValidationReport stale =
            await validator.ValidateAsync(
                compiled.PatchJson,
                workspace with
                {
                    Revision = RevisionB,
                    SourceHash = SourceHashB
                },
                CancellationToken.None
            );

        Assert.Equal("Invalid", stale.Status);
        Assert.Contains(
            stale.Checks,
            check =>
                check.Code == "validation.revision"
                && check.Status == "Failed"
        );
        Assert.Contains(
            stale.Checks,
            check =>
                check.Code == "validation.source_hash"
                && check.Status == "Failed"
        );
    }

    [Fact]
    public void NoChangeReturnsStructuredResultAndZeroChanges()
    {
        WorkbookPatchRegistry registry = LoadRegistry();
        var compiler = new WorkbookPatchCompiler(registry);
        WorkbookPatchWorkspace workspace = CreateWorkspace(
            cooldown: "8000"
        );

        WorkbookPatchCompileResult result = compiler.Compile(
            CreateCooldownPlan(workspace, registry, cooldown: 8, unit: "s"),
            workspace
        );

        Assert.Equal("NoChange", result.Status);
        Assert.Null(result.Patch);
        Assert.Equal("compiler.no_change", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public void DiffRowsMatchPatchFieldChanges()
    {
        WorkbookPatchRegistry registry = LoadRegistry();
        var compiler = new WorkbookPatchCompiler(registry);
        WorkbookPatchWorkspace workspace = CreateWorkspace(
            cooldown: "5000"
        );
        WorkbookPatchCompileResult compiled = compiler.Compile(
            CreateCooldownPlan(workspace, registry, cooldown: 8, unit: "s"),
            workspace
        );

        IReadOnlyList<WorkbookPatchDiffRow> rows =
            WorkbookPatchDiffProjector.Project(compiled.Patch!);

        Assert.Single(rows);
        Assert.Equal("skill", rows[0].TableKey);
        Assert.Equal("cd_time", rows[0].Field);
        Assert.Equal("5000", rows[0].Before);
        Assert.Equal("8000", rows[0].After);
        Assert.False(rows[0].IsNoOp);
    }

    [Fact]
    public async Task CompilerModifiesEffectActionParametersByContractIndex()
    {
        WorkbookPatchRegistry registry = LoadRegistry();
        WorkbookPatchAction damage = Assert.Single(
            registry.Actions,
            action => action.Key == "Damage"
        );
        WorkbookPatchField[] actionFields = damage.Parameters
            .Select(
                parameter => new WorkbookPatchField(
                    $"action_param[{parameter.Index}]",
                    $"Effect.action_param[{parameter.Index}]",
                    parameter.Key,
                    parameter.FieldKind,
                    parameter.RawType,
                    parameter.Required,
                    parameter.Scale,
                    parameter.Unit,
                    parameter.Minimum,
                    parameter.Maximum,
                    parameter.Options,
                    parameter.ReferenceTarget,
                    WorkbookPatchFieldBindingKind.ActionParameter,
                    100600180,
                    damage.Key,
                    parameter.Index,
                    parameter.Repeating
                )
            )
            .ToArray();
        var workspace = new WorkbookPatchWorkspace(
            "studio-workspace",
            RevisionA,
            SourceHashA,
            [
                new WorkbookPatchTable(
                    "Effect",
                    "TbEffect",
                    "effect",
                    actionFields,
                    [
                        new WorkbookPatchRecord(
                            100600180,
                            new Dictionary<string, IReadOnlyList<string>>
                            {
                                ["Id"] = ["100600180"],
                                ["action_type"] = ["伤害"],
                                ["action_param"] =
                                [
                                    "1006",
                                    "1020",
                                    "50000",
                                    "10000"
                                ]
                            }
                        )
                    ]
                )
            ]
        );
        var plan = new JsonObject
        {
            ["schemaVersion"] = 0,
            ["planId"] = "plan-effect-params",
            ["base"] = new JsonObject
            {
                ["workspaceId"] = "studio-workspace",
                ["revision"] = RevisionA,
                ["sourceHash"] = SourceHashA,
                ["capabilityRegistryVersion"] =
                    registry.CapabilityRegistryVersion,
                ["defaultValueContractVersion"] =
                    registry.DefaultValueContractVersion,
                ["defaultMechanismContractVersion"] =
                    registry.DefaultMechanismContractVersion
            },
            ["request"] = new JsonObject
            {
                ["text"] = "改成火焰，去掉固定伤害，倍率改成10"
            },
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
                        ["namespace"] = "TbEffect",
                        ["id"] = 100600180
                    },
                    ["fields"] = new JsonObject
                    {
                        ["attackType"] = new JsonObject
                        {
                            ["value"] = 1019,
                            ["source"] = "UserEdited",
                            ["evidence"] = new JsonArray()
                        },
                        ["fixedDamage"] = new JsonObject
                        {
                            ["value"] = 0,
                            ["source"] = "UserEdited",
                            ["evidence"] = new JsonArray()
                        },
                        ["attackScale"] = new JsonObject
                        {
                            ["value"] = 10,
                            ["source"] = "UserEdited",
                            ["evidence"] = new JsonArray()
                        }
                    }
                }
            }
        };
        var compiler = new WorkbookPatchCompiler(registry);

        WorkbookPatchCompileResult compiled = compiler.Compile(
            plan.ToJsonString(),
            workspace
        );

        Assert.Equal("Compiled", compiled.Status);
        Assert.NotNull(compiled.Patch);
        Assert.Collection(
            compiled.Patch!.FieldChanges.OrderBy(change => change.Field),
            change =>
            {
                Assert.Equal("action_param[1]", change.Field);
                Assert.Equal("1019", WorkbookFieldChangeJson.RawText(change.After));
            },
            change =>
            {
                Assert.Equal("action_param[2]", change.Field);
                Assert.Equal("0", WorkbookFieldChangeJson.RawText(change.After));
            },
            change =>
            {
                Assert.Equal("action_param[3]", change.Field);
                Assert.Equal("100000", WorkbookFieldChangeJson.RawText(change.After));
            }
        );

        var validator = new WorkbookPatchValidator(
            FindContractRoot(),
            registry
        );
        WorkbookPatchValidationReport validation =
            await validator.ValidateAsync(
                compiled.PatchJson!,
                workspace,
                CancellationToken.None
            );
        Assert.Equal("Valid", validation.Status);
    }

    [Fact]
    public async Task PlanNormalizerMapsRegistryAliasesInfersUnitsAndRepairsBase()
    {
        WorkbookPatchRegistry registry = LoadRegistry();
        var normalizer = new SkillConfigPlanNormalizer(registry);
        var plan = new JsonObject
        {
            ["schemaVersion"] = 0,
            ["planId"] = "model-plan",
            ["base"] = new JsonObject
            {
                ["workspaceId"] = "studio-workspace",
                ["revision"] = "v0",
                ["sourceHash"] = SourceHashA,
                ["capabilityRegistryVersion"] = "v0",
                ["defaultValueContractVersion"] = "v0",
                ["defaultMechanismContractVersion"] = "v0"
            },
            ["request"] = new JsonObject
            {
                ["text"] = "把冷却改成8秒"
            },
            ["status"] = "Ready",
            ["assumptions"] = new JsonArray
            {
                new JsonObject
                {
                    ["key"] = "cd_time_unit",
                    ["value"] = new JsonObject
                    {
                        ["value"] = 8,
                        ["source"] = "ModelProposed",
                        ["evidence"] = new JsonArray(),
                        ["reason"] = "legacy model field"
                    },
                    ["reason"] = "用户明确说明秒",
                    ["requiresConfirmation"] = true
                }
            },
            ["operations"] = new JsonArray
            {
                new JsonObject
                {
                    ["operationId"] = "op-1",
                    ["kind"] = "ModifySkill",
                    ["skill"] = new JsonObject
                    {
                        ["binding"] = "Existing",
                        ["namespace"] = "TbSkill",
                        ["id"] = 100101
                    },
                    ["fields"] = new JsonObject
                    {
                        ["cd_time"] = new JsonObject
                        {
                            ["value"] = 8,
                            ["source"] = "ModelProposed",
                            ["evidence"] = new JsonArray()
                        }
                    }
                }
            }
        };

        string normalized = normalizer.Normalize(
            plan.ToJsonString(),
            "把冷却改成8秒",
            new WorkbookPatchWorkspaceSnapshot(
                "studio-workspace",
                RevisionA,
                SourceHashA,
                GameDataCatalog.Empty
            )
        );
        JsonObject normalizedPlan = JsonNode.Parse(normalized)!.AsObject();
        JsonObject fields = normalizedPlan["operations"]![0]!["fields"]!
            .AsObject();
        Assert.True(fields.ContainsKey("cooldown"));
        Assert.False(fields.ContainsKey("cd_time"));
        Assert.Equal("秒", fields["cooldown"]!["unit"]!.GetValue<string>());
        Assert.Equal(
            "0.1.0",
            normalizedPlan["base"]!["capabilityRegistryVersion"]!
                .GetValue<string>()
        );
        Assert.False(
            normalizedPlan["assumptions"]![0]!["value"]!
                .AsObject()
                .ContainsKey("reason")
        );

        var validator = new SkillConfigPlanValidator(FindContractRoot());
        SkillConfigPlanValidationResult validation =
            await validator.ValidateAsync(normalized, CancellationToken.None);
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors));
    }

    [Fact]
    public async Task TemporaryApplyWritesCopyReReadsAndLeavesSourceUntouched()
    {
        string testRoot = CreateTestRoot();
        try
        {
            string sourceDataRoot = Path.Combine(
                testRoot,
                "source",
                "Datas"
            );
            string workbookPath = Path.Combine(
                sourceDataRoot,
                "Skill",
                "Skill.xlsx"
            );
            Directory.CreateDirectory(Path.GetDirectoryName(workbookPath)!);
            CreateSkillWorkbook(workbookPath, cooldown: 5000);
            string sourceHashBefore =
                SkillWorkspaceService.ComputeSourceTreeHash(sourceDataRoot);

            var options = new SkillWorkspaceOptions
            {
                ExcelDataRoot = sourceDataRoot,
                WriteTestRoot = Path.Combine(testRoot, "work"),
                WriteTestRetentionCount = 2
            };
            var environment = new TestHostEnvironment
            {
                ContentRootPath = testRoot
            };
            var catalogReader = new SingleSkillCatalogReader();
            var service = new TemporaryWorkbookPatchApplyService(
                options,
                environment,
                catalogReader,
                new GameDataWorkbookReader(),
                NullLogger<TemporaryWorkbookPatchApplyService>.Instance
            );

            WorkbookPatchRegistry registry = LoadRegistry();
            var compiler = new WorkbookPatchCompiler(registry);
            WorkbookPatchWorkspace workspace = CreateWorkspace(
                cooldown: "5000",
                workspaceId: "test-workspace",
                revision: "test-revision",
                sourceHash: sourceHashBefore
            );
            WorkbookPatchCompileResult compiled = compiler.Compile(
                CreateCooldownPlan(
                    workspace,
                    registry,
                    cooldown: 8,
                    unit: "s",
                    workspaceId: "test-workspace",
                    revision: "test-revision",
                    sourceHash: sourceHashBefore
                ),
                workspace
            );
            Assert.NotNull(compiled.Patch);

            WorkbookPatchValidationReport validation =
                new(
                    0,
                    compiled.Patch.PatchId,
                    "Valid",
                    [
                        new WorkbookPatchValidationCheck(
                            "test",
                            "Passed",
                            "Info",
                            true,
                            "test validation"
                        )
                    ]
                );
            TemporaryWorkbookPatchApplyResult applied = await service.ApplyAsync(
                compiled.PatchJson!,
                validation,
                CancellationToken.None
            );

            Assert.Equal("Verified", applied.Status);
            Assert.True(applied.SourceUnchanged);
            Assert.Empty(applied.Mismatches);
            Assert.Equal(
                sourceHashBefore,
                SkillWorkspaceService.ComputeSourceTreeHash(sourceDataRoot)
            );

            string copiedWorkbook = Path.Combine(
                applied.OutputRoot,
                "Unity",
                "Assets",
                "Config",
                "Excel",
                "Datas",
                "Skill",
                "Skill.xlsx"
            );
            GameDataTable reread = new GameDataWorkbookReader().Read(
                new GameDataTableSource(
                    "skill",
                    "技能",
                    "英雄配置图谱",
                    "Skill/Skill.xlsx",
                    copiedWorkbook
                )
            );
            Assert.Equal(
                "8000",
                reread.Record(100101).Fields["cd_time"].Single()
            );
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, true);
            }
        }
    }

    [Fact]
    public async Task InvalidPatchPerformsZeroWrites()
    {
        string testRoot = CreateTestRoot();
        try
        {
            string sourceDataRoot = Path.Combine(
                testRoot,
                "source",
                "Datas"
            );
            string workbookPath = Path.Combine(
                sourceDataRoot,
                "Skill",
                "Skill.xlsx"
            );
            Directory.CreateDirectory(Path.GetDirectoryName(workbookPath)!);
            CreateSkillWorkbook(workbookPath, cooldown: 5000);

            string writeRoot = Path.Combine(testRoot, "work");
            var options = new SkillWorkspaceOptions
            {
                ExcelDataRoot = sourceDataRoot,
                WriteTestRoot = writeRoot,
                WriteTestRetentionCount = 2
            };
            var service = new TemporaryWorkbookPatchApplyService(
                options,
                new TestHostEnvironment { ContentRootPath = testRoot },
                new SingleSkillCatalogReader(),
                new GameDataWorkbookReader(),
                NullLogger<TemporaryWorkbookPatchApplyService>.Instance
            );
            var patch = new WorkbookPatchDocument(
                0,
                new string('1', 64),
                new string('2', 64),
                new WorkbookPatchBase(
                    "test-workspace",
                    "test-revision",
                    SourceHashA,
                    "0.1.0",
                    "0.1.0",
                    "0.1.0"
                ),
                [],
                [
                    new WorkbookPatchCommand(
                        0,
                        "op-1",
                        "UpdateNode",
                        new WorkbookPatchAssetIdentity("TbSkill", 100101),
                        new JsonObject()
                    )
                ],
                [
                    new WorkbookFieldChange(
                        "op-1",
                        new WorkbookPatchLogicalAddress(
                            "skill",
                            100101,
                            "cd_time"
                        ),
                        "TbSkill",
                        100101,
                        "cd_time",
                        "cooldown",
                        JsonSerializer.SerializeToElement(8),
                        "s",
                        JsonSerializer.SerializeToElement("5000"),
                        JsonSerializer.SerializeToElement("8000"),
                        "UserEdited",
                        []
                    )
                ]
            );
            string patchJson = WorkbookPatchJson.Serialize(patch);

            TemporaryWorkbookPatchApplyResult result = await service.ApplyAsync(
                patchJson,
                new WorkbookPatchValidationReport(
                    0,
                    patch.PatchId,
                    "Invalid",
                    [
                        new WorkbookPatchValidationCheck(
                            "validation.source_hash",
                            "Failed",
                            "Error",
                            true,
                            "stale"
                        )
                    ]
                ),
                CancellationToken.None
            );

            Assert.Equal("Blocked", result.Status);
            Assert.False(Directory.Exists(writeRoot));
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, true);
            }
        }
    }

    [Fact]
    public async Task FinalApplyWritesSourceAndReReadsIt()
    {
        string testRoot = CreateTestRoot();
        try
        {
            string sourceDataRoot = Path.Combine(
                testRoot,
                "source",
                "Datas"
            );
            string workbookPath = Path.Combine(
                sourceDataRoot,
                "Skill",
                "Skill.xlsx"
            );
            Directory.CreateDirectory(Path.GetDirectoryName(workbookPath)!);
            CreateSkillWorkbook(workbookPath, cooldown: 5000);
            string sourceHashBefore =
                SkillWorkspaceService.ComputeSourceTreeHash(sourceDataRoot);

            var options = new SkillWorkspaceOptions
            {
                ExcelDataRoot = sourceDataRoot,
                WriteTestRoot = Path.Combine(testRoot, "work"),
                WriteTestRetentionCount = 2
            };
            var environment = new TestHostEnvironment
            {
                ContentRootPath = testRoot
            };
            var catalogReader = new SingleSkillCatalogReader();
            var temporaryService = new TemporaryWorkbookPatchApplyService(
                options,
                environment,
                catalogReader,
                new GameDataWorkbookReader(),
                NullLogger<TemporaryWorkbookPatchApplyService>.Instance
            );
            var finalService = new FinalWorkbookPatchApplyService(
                options,
                temporaryService,
                catalogReader,
                new WorkbookPatchTransactionStore(
                    options,
                    environment,
                    NullLogger<WorkbookPatchTransactionStore>.Instance
                ),
                NullLogger<FinalWorkbookPatchApplyService>.Instance
            );

            WorkbookPatchRegistry registry = LoadRegistry();
            var compiler = new WorkbookPatchCompiler(registry);
            WorkbookPatchWorkspace workspace = CreateWorkspace(
                cooldown: "5000",
                workspaceId: "test-workspace",
                revision: "test-revision",
                sourceHash: sourceHashBefore
            );
            WorkbookPatchCompileResult compiled = compiler.Compile(
                CreateCooldownPlan(
                    workspace,
                    registry,
                    cooldown: 8,
                    unit: "s",
                    workspaceId: "test-workspace",
                    revision: "test-revision",
                    sourceHash: sourceHashBefore
                ),
                workspace
            );
            Assert.NotNull(compiled.Patch);

            FinalWorkbookPatchApplyResult applied =
                await finalService.ApplyAsync(
                    compiled.PatchJson!,
                    new WorkbookPatchValidationReport(
                        0,
                        compiled.Patch.PatchId,
                        "Valid",
                        [
                            new WorkbookPatchValidationCheck(
                                "test",
                                "Passed",
                                "Info",
                                true,
                                "test validation"
                            )
                        ]
                    ),
                    CancellationToken.None
                );

            Assert.True(
                applied.Status == "Applied",
                $"{applied.Message} | {string.Join("; ", applied.Mismatches)}"
            );
            Assert.Equal(1, applied.AppliedFieldCount);
            Assert.NotEqual(
                sourceHashBefore,
                SkillWorkspaceService.ComputeSourceTreeHash(sourceDataRoot)
            );

            GameDataTable reread = new GameDataWorkbookReader().Read(
                new GameDataTableSource(
                    "skill",
                    "技能",
                    "英雄配置图谱",
                    "Skill/Skill.xlsx",
                    workbookPath
                )
            );
            Assert.Equal(
                "8000",
                reread.Record(100101).Fields["cd_time"].Single()
            );
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, true);
            }
        }
    }

    [Fact]
    public async Task FinalApplyWritesEffectActionParametersToSource()
    {
        string testRoot = CreateTestRoot();
        try
        {
            string sourceDataRoot = Path.Combine(
                testRoot,
                "source",
                "Datas"
            );
            string workbookPath = Path.Combine(
                sourceDataRoot,
                "Skill",
                "Effect.xlsx"
            );
            Directory.CreateDirectory(Path.GetDirectoryName(workbookPath)!);
            CreateEffectWorkbook(
                workbookPath,
                ["1006", "1020", "50000", "10000"]
            );
            string sourceHashBefore =
                SkillWorkspaceService.ComputeSourceTreeHash(sourceDataRoot);

            var options = new SkillWorkspaceOptions
            {
                ExcelDataRoot = sourceDataRoot,
                WriteTestRoot = Path.Combine(testRoot, "work"),
                WriteTestRetentionCount = 2
            };
            var environment = new TestHostEnvironment
            {
                ContentRootPath = testRoot
            };
            var catalogReader = new SingleEffectCatalogReader();
            var temporaryService = new TemporaryWorkbookPatchApplyService(
                options,
                environment,
                catalogReader,
                new GameDataWorkbookReader(),
                NullLogger<TemporaryWorkbookPatchApplyService>.Instance
            );
            var finalService = new FinalWorkbookPatchApplyService(
                options,
                temporaryService,
                catalogReader,
                new WorkbookPatchTransactionStore(
                    options,
                    environment,
                    NullLogger<WorkbookPatchTransactionStore>.Instance
                ),
                NullLogger<FinalWorkbookPatchApplyService>.Instance
            );

            WorkbookPatchRegistry registry = LoadRegistry();
            WorkbookPatchAction damage = Assert.Single(
                registry.Actions,
                action => action.Key == "Damage"
            );
            GameDataTable effectTable = catalogReader
                .Read(sourceDataRoot)
                .Table("effect");
            WorkbookPatchField[] actionFields = damage.Parameters
                .Select(
                    parameter => new WorkbookPatchField(
                        $"action_param[{parameter.Index}]",
                        $"Effect.action_param[{parameter.Index}]",
                        parameter.Key,
                        parameter.FieldKind,
                        parameter.RawType,
                        parameter.Required,
                        parameter.Scale,
                        parameter.Unit,
                        parameter.Minimum,
                        parameter.Maximum,
                        parameter.Options,
                        parameter.ReferenceTarget,
                        WorkbookPatchFieldBindingKind.ActionParameter,
                        100600180,
                        damage.Key,
                        parameter.Index,
                        parameter.Repeating
                    )
                )
                .ToArray();
            var workspace = new WorkbookPatchWorkspace(
                "test-workspace",
                "test-revision",
                sourceHashBefore,
                [
                    new WorkbookPatchTable(
                        "Effect",
                        "TbEffect",
                        "effect",
                        actionFields,
                        effectTable.Records
                            .Select(
                                record => new WorkbookPatchRecord(
                                    record.Id,
                                    record.Fields
                                )
                            )
                            .ToArray()
                    )
                ]
            );
            var compiler = new WorkbookPatchCompiler(registry);
            WorkbookPatchCompileResult compiled = compiler.Compile(
                CreateEffectActionPlan(
                    workspace,
                    registry,
                    workspaceId: "test-workspace",
                    revision: "test-revision",
                    sourceHash: sourceHashBefore
                ),
                workspace
            );
            Assert.Equal("Compiled", compiled.Status);
            Assert.NotNull(compiled.Patch);

            var validator = new WorkbookPatchValidator(
                FindContractRoot(),
                registry
            );
            WorkbookPatchValidationReport validation =
                await validator.ValidateAsync(
                    compiled.PatchJson!,
                    workspace,
                    CancellationToken.None
                );
            Assert.Equal("Valid", validation.Status);

            FinalWorkbookPatchApplyResult applied =
                await finalService.ApplyAsync(
                    compiled.PatchJson!,
                    validation,
                    CancellationToken.None
                );

            Assert.True(
                applied.Status == "Applied",
                $"{applied.Message} | {string.Join("; ", applied.Mismatches)}"
            );
            Assert.Equal(3, applied.AppliedFieldCount);
            Assert.Empty(applied.Mismatches);

            GameDataTable reread = catalogReader.Read(sourceDataRoot)
                .Table("effect");
            Assert.Equal(
                ["1006", "1019", "0", "100000"],
                reread.Record(100600180).Fields["action_param"]
            );

            Assert.True(applied.UndoAvailable);
            Assert.False(string.IsNullOrWhiteSpace(applied.TransactionId));
            FinalWorkbookPatchApplyResult undone =
                await finalService.UndoAsync(
                    applied.TransactionId!,
                    CancellationToken.None
                );
            Assert.Equal("Undone", undone.Status);

            GameDataTable restored = catalogReader.Read(sourceDataRoot)
                .Table("effect");
            Assert.Equal(
                ["1006", "1020", "50000", "10000"],
                restored.Record(100600180).Fields["action_param"]
            );
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, true);
            }
        }
    }

    [Fact]
    public void CellUpdaterUsesFirstSheetInWorkbookOrder()
    {
        string testRoot = CreateTestRoot();
        try
        {
            string sourceDataRoot = Path.Combine(testRoot, "Datas");
            string workbookPath = Path.Combine(
                sourceDataRoot,
                "Skill",
                "Effect.xlsx"
            );
            Directory.CreateDirectory(Path.GetDirectoryName(workbookPath)!);
            CreateEffectWorkbook(
                workbookPath,
                ["1006", "1020", "50000", "10000"],
                addEarlierDecoyPart: true
            );
            GameDataTable table = new SingleEffectCatalogReader()
                .Read(sourceDataRoot)
                .Table("effect");

            GameDataWorkbookCellUpdater.Apply(
                workbookPath,
                table,
                [new WorkbookPatchChange(100600180, "action_param", 1, "1019")]
            );

            GameDataTable reread = new SingleEffectCatalogReader()
                .Read(sourceDataRoot)
                .Table("effect");
            Assert.Equal(
                "1019",
                reread.Record(100600180).Fields["action_param"][1]
            );
            using SpreadsheetDocument written = SpreadsheetDocument.Open(
                workbookPath,
                false
            );
            WorkbookPart workbookPart = written.WorkbookPart!;
            Sheet firstSheet = workbookPart.Workbook!
                .GetFirstChild<Sheets>()!
                .Elements<Sheet>()
                .First();
            WorksheetPart firstPart = (WorksheetPart)workbookPart.GetPartById(
                firstSheet.Id!.Value!
            );
            Cell numericCell = firstPart.Worksheet!
                .GetFirstChild<SheetData>()!
                .Descendants<Cell>()
                .First(cell => cell.CellReference?.Value == "E4");
            Assert.Equal(CellValues.Number, numericCell.DataType?.Value);
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, true);
            }
        }
    }

    [Fact]
    public void CellUpdaterFindsRecordByIdWhenSourceRowIsNotPhysicalRow()
    {
        string testRoot = CreateTestRoot();
        try
        {
            string sourceDataRoot = Path.Combine(testRoot, "Datas");
            string workbookPath = Path.Combine(
                sourceDataRoot,
                "Skill",
                "Effect.xlsx"
            );
            Directory.CreateDirectory(Path.GetDirectoryName(workbookPath)!);
            CreateEffectWorkbook(
                workbookPath,
                ["1006", "1020", "50000", "10000"]
            );
            using (SpreadsheetDocument document =
                SpreadsheetDocument.Open(workbookPath, true))
            {
                Worksheet worksheet = document.WorkbookPart!
                    .WorksheetParts.First()
                    .Worksheet
                    ?? throw new InvalidDataException(
                        "测试工作簿缺少工作表。"
                    );
                SheetData data = worksheet.GetFirstChild<SheetData>()!;
                Row target = data.Elements<Row>().Last();
                Row decoy = (Row)target.CloneNode(true);
                SetRowNumber(target, 10);
                SetRowNumber(decoy, 5);
                Cell id = decoy.Elements<Cell>().First(
                    cell => cell.CellReference?.Value == "B5"
                );
                id.InlineString = new InlineString(new Text("999999"));
                data.InsertBefore(decoy, target);
                worksheet.Save();
            }

            var reader = new SingleEffectCatalogReader();
            GameDataTable table = reader.Read(sourceDataRoot).Table("effect");
            Assert.Equal(5, table.Record(100600180).SourceRow);
            GameDataWorkbookCellUpdater.Apply(
                workbookPath,
                table,
                [new WorkbookPatchChange(100600180, "action_param", 1, "1019")]
            );

            GameDataTable reread = reader.Read(sourceDataRoot).Table("effect");
            Assert.Equal(
                "1019",
                reread.Record(100600180).Fields["action_param"][1]
            );
            Assert.Equal(
                "1020",
                reread.Record(999999).Fields["action_param"][1]
            );
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, true);
            }
        }
    }

    private static void SetRowNumber(Row row, uint rowNumber)
    {
        row.RowIndex = rowNumber;
        foreach (Cell cell in row.Elements<Cell>())
        {
            string reference = cell.CellReference?.Value ?? "";
            cell.CellReference = new string(
                reference.TakeWhile(char.IsLetter).ToArray()
            ) + rowNumber;
        }
    }

    [Fact]
    public void LockCheckerDetectsWorkbookHeldByAnotherWriter()
    {
        string testRoot = CreateTestRoot();
        try
        {
            string path = Path.Combine(testRoot, "Locked.xlsx");
            File.WriteAllText(path, "locked");
            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None
            );

            IReadOnlyList<string> locked =
                WorkbookFileLockChecker.FindLockedFiles([path]);

            Assert.Equal([path], locked);
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, true);
            }
        }
    }

    [Fact]
    public void SourceHashIgnoresExcelTemporaryLockFiles()
    {
        string testRoot = CreateTestRoot();
        try
        {
            File.WriteAllText(
                Path.Combine(testRoot, "Effect.xlsx"),
                "workbook"
            );
            string before =
                SkillWorkspaceService.ComputeSourceTreeHash(testRoot);

            File.WriteAllText(
                Path.Combine(testRoot, "~$Effect.xlsx"),
                "excel-lock"
            );
            File.WriteAllText(
                Path.Combine(testRoot, "~$Effect.xlsx.meta"),
                "unity-meta"
            );
            string after =
                SkillWorkspaceService.ComputeSourceTreeHash(testRoot);

            Assert.Equal(before, after);
        }
        finally
        {
            if (Directory.Exists(testRoot))
            {
                Directory.Delete(testRoot, true);
            }
        }
    }

    private static WorkbookPatchRegistry LoadRegistry()
    {
        return WorkbookPatchRegistryLoader.Load(FindContractRoot());
    }

    private static WorkbookPatchWorkspace CreateWorkspace(
        string cooldown,
        string workspaceId = "studio-workspace",
        string revision = RevisionA,
        string sourceHash = SourceHashA
    )
    {
        var fields = new[]
        {
            new WorkbookPatchField(
                "cd_time",
                "Skill.cd_time",
                "cooldown",
                GameDataFieldKind.Integer,
                "int",
                false,
                1,
                "ms",
                0,
                null,
                [],
                null
            ),
            new WorkbookPatchField(
                "duration",
                "Skill.duration",
                "duration",
                GameDataFieldKind.Integer,
                "int",
                false,
                1,
                "ms",
                0,
                null,
                [],
                null
            ),
            new WorkbookPatchField(
                "skill_type",
                "Skill.skill_type",
                "skill_type",
                GameDataFieldKind.Enum,
                "ESkillType",
                true,
                1,
                null,
                null,
                null,
                [
                    new GameDataOption(
                        "自动",
                        "自动",
                        "Auto",
                        5
                    )
                ],
                null
            )
        };
        var record = new WorkbookPatchRecord(
            100101,
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["Id"] = ["100101"],
                ["cd_time"] = [cooldown],
                ["duration"] = ["1000"],
                ["skill_type"] = ["自动"]
            }
        );
        return new WorkbookPatchWorkspace(
            workspaceId,
            revision,
            sourceHash,
            [
                new WorkbookPatchTable(
                    "Skill",
                    "TbSkill",
                    "skill",
                    fields,
                    [record]
                )
            ]
        );
    }

    private static string CreateCooldownPlan(
        WorkbookPatchWorkspace workspace,
        WorkbookPatchRegistry registry,
        int cooldown,
        string unit,
        string workspaceId = "studio-workspace",
        string revision = RevisionA,
        string sourceHash = SourceHashA
    )
    {
        return CreateModifySkillPlan(
            workspace,
            registry,
            new JsonObject
            {
                ["cooldown"] = new JsonObject
                {
                    ["value"] = cooldown,
                    ["unit"] = unit,
                    ["source"] = "ModelProposed",
                    ["evidence"] = new JsonArray()
                }
            },
            workspaceId,
            revision,
            sourceHash
        );
    }

    private static string CreateCooldownPlan(
        WorkbookPatchWorkspace workspace,
        WorkbookPatchRegistry registry,
        JsonNode value,
        string unit = "s"
    )
    {
        return CreateModifySkillPlan(
            workspace,
            registry,
            new JsonObject
            {
                ["cooldown"] = new JsonObject
                {
                    ["value"] = value.DeepClone(),
                    ["unit"] = unit,
                    ["source"] = "ModelProposed",
                    ["evidence"] = new JsonArray()
                }
            }
        );
    }

    private static string CreateModifySkillPlan(
        WorkbookPatchWorkspace workspace,
        WorkbookPatchRegistry registry,
        JsonObject fields,
        string workspaceId = "studio-workspace",
        string revision = RevisionA,
        string sourceHash = SourceHashA
    )
    {
        var plan = new JsonObject
        {
            ["schemaVersion"] = 0,
            ["planId"] = "plan-phase-b",
            ["base"] = new JsonObject
            {
                ["workspaceId"] = workspaceId,
                ["revision"] = revision,
                ["sourceHash"] = sourceHash,
                ["capabilityRegistryVersion"] =
                    registry.CapabilityRegistryVersion,
                ["defaultValueContractVersion"] =
                    registry.DefaultValueContractVersion,
                ["defaultMechanismContractVersion"] =
                    registry.DefaultMechanismContractVersion
            },
            ["request"] = new JsonObject
            {
                ["text"] = "把冷却改成8秒"
            },
            ["status"] = "Ready",
            ["operations"] = new JsonArray
            {
                new JsonObject
                {
                    ["operationId"] = "op-1",
                    ["kind"] = "ModifySkill",
                    ["skill"] = new JsonObject
                    {
                        ["binding"] = "Existing",
                        ["namespace"] = "TbSkill",
                        ["id"] = 100101
                    },
                    ["fields"] = fields.DeepClone()
                }
            }
        };
        return plan.ToJsonString();
    }

    private static string CreateEffectActionPlan(
        WorkbookPatchWorkspace workspace,
        WorkbookPatchRegistry registry,
        string workspaceId,
        string revision,
        string sourceHash
    )
    {
        var plan = new JsonObject
        {
            ["schemaVersion"] = 0,
            ["planId"] = "plan-effect-parameters",
            ["base"] = new JsonObject
            {
                ["workspaceId"] = workspaceId,
                ["revision"] = revision,
                ["sourceHash"] = sourceHash,
                ["capabilityRegistryVersion"] =
                    registry.CapabilityRegistryVersion,
                ["defaultValueContractVersion"] =
                    registry.DefaultValueContractVersion,
                ["defaultMechanismContractVersion"] =
                    registry.DefaultMechanismContractVersion
            },
            ["request"] = new JsonObject
            {
                ["text"] = "改成火焰，去掉固定伤害，倍率改成10"
            },
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
                        ["namespace"] = "TbEffect",
                        ["id"] = 100600180
                    },
                    ["fields"] = new JsonObject
                    {
                        ["attackType"] = new JsonObject
                        {
                            ["value"] = 1019,
                            ["source"] = "UserEdited",
                            ["evidence"] = new JsonArray()
                        },
                        ["fixedDamage"] = new JsonObject
                        {
                            ["value"] = 0,
                            ["source"] = "UserEdited",
                            ["evidence"] = new JsonArray()
                        },
                        ["attackScale"] = new JsonObject
                        {
                            ["value"] = 10,
                            ["source"] = "UserEdited",
                            ["evidence"] = new JsonArray()
                        }
                    }
                }
            }
        };
        return plan.ToJsonString();
    }

    private static string CreateTestRoot()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "RtsSkillStudioTests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(path);
        return path;
    }

    private static void CreateSkillWorkbook(string path, int cooldown)
    {
        using SpreadsheetDocument document = SpreadsheetDocument.Create(
            path,
            SpreadsheetDocumentType.Workbook
        );
        WorkbookPart workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        WorksheetPart worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        worksheetPart.Worksheet = new Worksheet(new SheetData());
        Sheets sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(
            new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "Skill"
            }
        );
        SheetData sheetData = worksheetPart.Worksheet.GetFirstChild<SheetData>()!;
        sheetData.Append(
            Row("##var", "Id", "cd_time", "duration"),
            Row("##type", "int", "int", "int"),
            Row("##", "编号", "冷却", "持续时间"),
            Row("", "100101", cooldown.ToString(), "1000")
        );
        worksheetPart.Worksheet.Save();
        workbookPart.Workbook.Save();
    }

    private static void CreateEffectWorkbook(
        string path,
        IReadOnlyList<string> actionParameters,
        bool addEarlierDecoyPart = false
    )
    {
        using SpreadsheetDocument document = SpreadsheetDocument.Create(
            path,
            SpreadsheetDocumentType.Workbook
        );
        WorkbookPart workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new Workbook();
        WorksheetPart? decoyPart = null;
        if (addEarlierDecoyPart)
        {
            decoyPart = workbookPart.AddNewPart<WorksheetPart>();
            decoyPart.Worksheet = new Worksheet(new SheetData());
        }
        WorksheetPart worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
        worksheetPart.Worksheet = new Worksheet(new SheetData());
        Sheets sheets = workbookPart.Workbook.AppendChild(new Sheets());
        sheets.Append(
            new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "Effect"
            }
        );
        if (decoyPart is not null)
        {
            sheets.Append(
                new Sheet
                {
                    Id = workbookPart.GetIdOfPart(decoyPart),
                    SheetId = 2,
                    Name = "Decoy"
                }
            );
            decoyPart.Worksheet!.Save();
        }
        SheetData sheetData = worksheetPart.Worksheet.GetFirstChild<SheetData>()!;
        sheetData.Append(
            Row("##var", "Id", "action_type", "action_param", "", "", ""),
            Row("##type", "int", "string", "array,int", "", "", ""),
            Row("##", "编号", "动作", "参数", "", "", ""),
            Row(
                "",
                "100600180",
                "伤害",
                actionParameters.ElementAtOrDefault(0) ?? "",
                actionParameters.ElementAtOrDefault(1) ?? "",
                actionParameters.ElementAtOrDefault(2) ?? "",
                actionParameters.ElementAtOrDefault(3) ?? "",
                ""
            )
        );
        worksheetPart.Worksheet.Save();
        workbookPart.Workbook.Save();
    }

    private static Row Row(params string[] values)
    {
        var row = new Row();
        for (int index = 0; index < values.Length; index++)
        {
            string reference = $"{(char)('A' + index)}1";
            row.Append(
                new Cell
                {
                    CellReference = reference,
                    DataType = CellValues.InlineString,
                    InlineString = new InlineString(
                        new Text(values[index])
                    )
                }
            );
        }

        return row;
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

        throw new DirectoryNotFoundException("Repository root was not found.");
    }

    private sealed class SingleSkillCatalogReader : IGameDataCatalogReader
    {
        public GameDataCatalog Read(string excelDataRoot)
        {
            var reader = new GameDataWorkbookReader();
            GameDataTable table = reader.Read(
                new GameDataTableSource(
                    "skill",
                    "技能",
                    "英雄配置图谱",
                    "Skill/Skill.xlsx",
                    Path.Combine(excelDataRoot, "Skill", "Skill.xlsx")
                )
            );
            return new GameDataCatalog([table]);
        }
    }

    private sealed class SingleEffectCatalogReader : IGameDataCatalogReader
    {
        public GameDataCatalog Read(string excelDataRoot)
        {
            var reader = new GameDataWorkbookReader();
            GameDataTable table = reader.Read(
                new GameDataTableSource(
                    "effect",
                    "效果",
                    "英雄配置图谱",
                    "Skill/Effect.xlsx",
                    Path.Combine(excelDataRoot, "Skill", "Effect.xlsx")
                )
            );
            return new GameDataCatalog([table]);
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";

        public string ApplicationName { get; set; } = "RtsSkillStudio.Tests";

        public string ContentRootPath { get; set; } = "";

        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
