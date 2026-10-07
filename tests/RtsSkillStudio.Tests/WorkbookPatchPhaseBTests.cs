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
                new GameDataWorkbookWriter(),
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
                new GameDataWorkbookWriter(),
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

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";

        public string ApplicationName { get; set; } = "RtsSkillStudio.Tests";

        public string ContentRootPath { get; set; } = "";

        public IFileProvider ContentRootFileProvider { get; set; } =
            new NullFileProvider();
    }
}
