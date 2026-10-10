using System.Text.Json;
using System.Text.Json.Nodes;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RtsSkillStudio.Agent.Patch;
using RtsSkillStudio.Api.Workspaces;
using TianshuDM.Application.GameData;
using TianshuDM.Application.HeroAuthoring;
using TianshuDM.Domain.GameData;
using TianshuDM.Infrastructure.Excel;
using Xunit;

namespace RtsSkillStudio.Tests;

public sealed class WorkbookPatchPhaseB3Tests
{
    private const string SourceHashA =
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string RevisionA = "revision-a";

    [Fact]
    public async Task MultipleOperationsCompileDeterministicallyAndValidateAgainstOneBase()
    {
        WorkbookPatchRegistry registry = LoadRegistry();
        WorkbookPatchWorkspace workspace = CreateWorkspace();
        WorkbookPatchTable skill = workspace.Tables.First(table => table.Namespace == "TbSkill");
        workspace = workspace with
        {
            Tables = workspace.Tables.Select(table => table == skill
                ? table with { Records = [.. table.Records, table.Records[0] with { Id = 100102 }] }
                : table).ToArray()
        };
        JsonObject plan = JsonNode.Parse(CreateLinkPlan(workspace, registry))!.AsObject();
        JsonArray operations = plan["operations"]!.AsArray();
        JsonObject second = operations[0]!.DeepClone().AsObject();
        second["operationId"] = "op-2";
        second["skill"]!["id"] = 100102;
        operations.Add(second);
        var compiler = new WorkbookPatchCompiler(registry);
        WorkbookPatchCompileResult result = compiler.Compile(plan.ToJsonString(), workspace);
        Assert.Equal("Compiled", result.Status);
        Assert.Equal(2, result.Patch!.FieldChanges.Count);
        Assert.Equal(new[] { 0, 1 }, result.Patch.Commands.Select(command => command.Sequence));
        Assert.Equal(result.PatchJson, compiler.Compile(plan.ToJsonString(), workspace).PatchJson);
        Assert.Equal(WorkbookPatchJsonUtilities.ComputePlanHash(plan.ToJsonString()), result.Patch.SourcePlanHash);
        var validator = new WorkbookPatchValidator(FindContractRoot(), registry);
        Assert.Equal("Valid", (await validator.ValidateAsync(result.PatchJson!, workspace, CancellationToken.None)).Status);

        WorkbookPatchDocument overlapping = result.Patch with
        {
            FieldChanges = [result.Patch.FieldChanges[0], result.Patch.FieldChanges[0] with { OperationId = "op-2" }]
        };
        overlapping = overlapping with { PatchId = WorkbookPatchJsonUtilities.ComputePatchId(overlapping) };
        Assert.Equal("Invalid", (await validator.ValidateAsync(WorkbookPatchJson.Serialize(overlapping), workspace, CancellationToken.None)).Status);

        // An unchanged operation is omitted while other changes still compile.
        WorkbookPatchWorkspace partlyUnchanged = workspace with
        {
            Tables = workspace.Tables.Select(table => table.Namespace == "TbSkill"
                ? table with { Records = table.Records.Select(record => record.Id == 100102
                    ? record with { Fields = new Dictionary<string, IReadOnlyList<string>>(record.Fields) { ["search_target"] = ["300101"] } }
                    : record).ToArray() }
                : table).ToArray()
        };
        second["fields"]!["search_target"]!["value"]!["id"] = 300101;
        Assert.Single(compiler.Compile(plan.ToJsonString(), partlyUnchanged).Patch!.Commands);

        second["fields"]!["search_target"]!["value"]!["id"] = 999999;
        WorkbookPatchCompileResult invalid = compiler.Compile(plan.ToJsonString(), workspace);
        Assert.Equal("Invalid", invalid.Status);
        Assert.Null(invalid.Patch);
        Assert.Contains(invalid.Errors, error => error.Code == "compiler.reference_target_missing");
    }

    [Fact]
    public void MultipleOperationsRejectDuplicateIdsAndOverlappingWrites()
    {
        WorkbookPatchRegistry registry = LoadRegistry();
        WorkbookPatchWorkspace workspace = CreateWorkspace();
        JsonObject plan = JsonNode.Parse(CreateLinkPlan(workspace, registry))!.AsObject();
        JsonArray operations = plan["operations"]!.AsArray();
        JsonObject second = operations[0]!.DeepClone().AsObject();
        operations.Add(second);
        var compiler = new WorkbookPatchCompiler(registry);
        Assert.Contains(compiler.Compile(plan.ToJsonString(), workspace).Errors,
            error => error.Code == "compiler.duplicate_operation_id");
        second["operationId"] = "op-2";
        WorkbookPatchCompileResult conflict = compiler.Compile(plan.ToJsonString(), workspace);
        Assert.Null(conflict.Patch);
        Assert.Contains(conflict.Errors, error => error.Code == "compiler.conflicting_field_write");
    }

    [Fact]
    public void CompilerResolvesScalarReferenceFromRegistryMetadata()
    {
        Assert.Equal(
            "Reference",
            Assert.Single(
                LoadRegistry().EntityFields,
                field => field.Path == "Skill.search_target"
            ).Kind
        );
        Assert.Equal(
            "Zero",
            Assert.Single(
                LoadRegistry().EntityFields,
                field => field.Path == "Skill.search_target"
            ).ReferenceRemoval
        );

        WorkbookPatchRegistry registry = LoadRegistry();
        var compiler = new WorkbookPatchCompiler(registry);
        WorkbookPatchWorkspace workspace = CreateWorkspace();

        WorkbookPatchCompileResult first = compiler.Compile(
            CreateLinkPlan(workspace, registry),
            workspace
        );
        WorkbookPatchCompileResult second = compiler.Compile(
            CreateLinkPlan(workspace, registry),
            workspace
        );

        Assert.Equal("Compiled", first.Status);
        Assert.NotNull(first.PatchJson);
        Assert.Equal(first.PatchJson, second.PatchJson);
        WorkbookFieldChange change = Assert.Single(first.Patch!.FieldChanges);
        Assert.Equal("search_target", change.Field);
        Assert.Equal("search_target", change.SemanticField);
        Assert.Equal("0", WorkbookFieldChangeJson.RawText(change.Before));
        Assert.Equal("300101", WorkbookFieldChangeJson.RawText(change.After));
        Assert.Equal(
            "300101",
            WorkbookFieldChangeJson.SemanticText(change.SemanticValue)
        );
        Assert.Contains("ExistingConfig:TbSearch:300101", change.Evidence);
    }

    [Fact]
    public void CompilerRejectsMissingMismatchedAndUndeclaredReferenceTargets()
    {
        WorkbookPatchRegistry registry = LoadRegistry();
        var compiler = new WorkbookPatchCompiler(registry);
        WorkbookPatchWorkspace workspace = CreateWorkspace();

        WorkbookPatchCompileResult missing = compiler.Compile(
            CreateFieldPlan(
                workspace,
                registry,
                "search_target",
                new JsonObject
                {
                    ["binding"] = "Existing",
                    ["namespace"] = "TbSearch",
                    ["id"] = 999999
                }
            ),
            workspace
        );
        WorkbookPatchCompileResult wrongNamespace = compiler.Compile(
            CreateFieldPlan(
                workspace,
                registry,
                "search_target",
                new JsonObject
                {
                    ["binding"] = "Existing",
                    ["namespace"] = "TbBuff",
                    ["id"] = 300101
                }
            ),
            workspace
        );
        WorkbookPatchCompileResult incompleteTarget = compiler.Compile(
            CreateFieldPlan(
                workspace,
                registry,
                "search_target",
                JsonSerializer.SerializeToNode(
                    new Dictionary<string, object>
                    {
                        ["binding"] = "Existing",
                        ["namespace"] = "TbSearch"
                    }
                )!
            ),
            workspace
        );
        WorkbookPatchCompileResult referenceIntoScalarField = compiler.Compile(
            CreateFieldPlan(
                workspace,
                registry,
                "cooldown",
                new JsonObject
                {
                    ["binding"] = "Existing",
                    ["namespace"] = "TbSearch",
                    ["id"] = 300101
                }
            ),
            workspace
        );
        WorkbookPatchCompileResult undeclared = compiler.Compile(
            CreateFieldPlan(
                workspace,
                registry,
                "broken_target",
                new JsonObject
                {
                    ["binding"] = "Existing",
                    ["namespace"] = "TbBuff",
                    ["id"] = 400101
                }
            ),
            workspace
        );

        Assert.Equal(
            "compiler.reference_target_missing",
            Assert.Single(missing.Errors).Code
        );
        Assert.Equal(
            "compiler.reference_namespace_mismatch",
            Assert.Single(wrongNamespace.Errors).Code
        );
        Assert.Equal(
            "compiler.invalid_reference_value",
            Assert.Single(incompleteTarget.Errors).Code
        );
        Assert.Equal(
            "compiler.invalid_value_type",
            Assert.Single(referenceIntoScalarField.Errors).Code
        );
        Assert.Equal(
            "compiler.reference_target_undeclared",
            Assert.Single(undeclared.Errors).Code
        );
    }

    [Fact]
    public void CompilerRejectsRegistryContractThatDisagreesWithWorkbookAnnotation()
    {
        WorkbookPatchRegistry registry = LoadRegistry();
        var compiler = new WorkbookPatchCompiler(registry);
        WorkbookPatchWorkspace workspace = CreateWorkspace(
            searchTargetReferenceNamespace: "TbBuff"
        );

        WorkbookPatchCompileResult compiled = compiler.Compile(
            CreateLinkPlan(workspace, registry),
            workspace
        );

        Assert.Equal("Invalid", compiled.Status);
        Assert.Equal(
            "compiler.reference_contract_mismatch",
            Assert.Single(compiled.Errors).Code
        );
    }

    [Fact]
    public void CompilerReportsNoChangeWhenLinkAlreadyPointsAtTarget()
    {
        WorkbookPatchRegistry registry = LoadRegistry();
        var compiler = new WorkbookPatchCompiler(registry);
        WorkbookPatchWorkspace workspace = CreateWorkspace(searchTarget: "300101");

        WorkbookPatchCompileResult compiled = compiler.Compile(
            CreateLinkPlan(workspace, registry),
            workspace
        );

        Assert.Equal("NoChange", compiled.Status);
        Assert.Null(compiled.Patch);
        Assert.Equal(
            "compiler.no_change",
            Assert.Single(compiled.Errors).Code
        );
    }

    [Fact]
    public void RemoveLinkUsesDeclaredRemovalEncoding()
    {
        WorkbookPatchRegistry registry = LoadRegistry();
        var compiler = new WorkbookPatchCompiler(registry);
        WorkbookPatchWorkspace workspace = CreateWorkspace(
            searchTarget: "300101"
        );

        WorkbookPatchCompileResult compiled = compiler.Compile(
            CreateRemoveLinkPlan(workspace, registry, "search_target", 300101),
            workspace
        );

        Assert.Equal("Compiled", compiled.Status);
        WorkbookFieldChange change = Assert.Single(compiled.Patch!.FieldChanges);
        Assert.Equal("search_target", change.Field);
        Assert.Equal("300101", WorkbookFieldChangeJson.RawText(change.Before));
        Assert.Equal("0", WorkbookFieldChangeJson.RawText(change.After));
        Assert.Equal("Compiler", change.Source);
        Assert.Contains("ReferenceRemoval:Zero", change.Evidence);
        Assert.Contains("ExistingConfig:TbSearch:300101", change.Evidence);

        WorkbookPatchDiffRow row = Assert.Single(
            WorkbookPatchDiffProjector.Project(compiled.Patch)
        );
        Assert.Equal("0", row.After);
        Assert.Equal("0", row.SemanticValue);
        Assert.False(row.IsNoOp);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RemoveLinkValidatesAndRoundTripsThroughApplyAndUndo(bool multipleOperations)
    {
        string testRoot = CreateTestRoot();
        try
        {
            string sourceDataRoot = Path.Combine(testRoot, "source", "Datas");
            CreateSkillWorkbook(
                Path.Combine(sourceDataRoot, "Skill", "Skill.xlsx"),
                searchTarget: 300101,
                secondRecord: multipleOperations
            );
            CreateSearchWorkbook(
                Path.Combine(sourceDataRoot, "Search", "Search.xlsx")
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
            var catalogReader = new SkillAndSearchCatalogReader();
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
            WorkbookPatchWorkspace workspace = BuildWorkspaceFromCatalog(
                catalogReader.Read(sourceDataRoot),
                registry,
                sourceHashBefore
            );
            var compiler = new WorkbookPatchCompiler(registry);
            JsonObject transactionPlan = JsonNode.Parse(CreateRemoveLinkPlan(workspace, registry, "search_target", 300101))!.AsObject();
            if (multipleOperations)
            {
                JsonObject second = transactionPlan["operations"]![0]!.DeepClone().AsObject();
                second["operationId"] = "op-remove-2";
                second["parent"]!["id"] = 100102;
                transactionPlan["operations"]!.AsArray().Add(second);
            }
            WorkbookPatchCompileResult compiled = compiler.Compile(transactionPlan.ToJsonString(), workspace);
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
            Assert.True(
                validation.Status == "Valid",
                string.Join(
                    "; ",
                    validation.Checks
                        .Where(check => check.Status == "Failed")
                        .Select(check => check.Code + ":" + check.Message)
                )
            );
            Assert.Contains(
                validation.Checks,
                check =>
                    check.Code == "validation.reference"
                    && check.Status == "Passed"
            );

            TemporaryWorkbookPatchApplyResult temporary =
                await temporaryService.ApplyAsync(
                    compiled.PatchJson!,
                    validation,
                    CancellationToken.None
                );
            Assert.Equal("Verified", temporary.Status);
            Assert.True(temporary.SourceUnchanged);
            Assert.Empty(temporary.Mismatches);

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
            Assert.Equal(multipleOperations ? 2 : 1, applied.AppliedFieldCount);
            if (multipleOperations)
                Assert.Equal("0", catalogReader.Read(sourceDataRoot).Table("skill").Record(100102).Fields["search_target"].Single());
            Assert.Equal(
                "0",
                catalogReader
                    .Read(sourceDataRoot)
                    .Table("skill")
                    .Record(100101)
                    .Fields["search_target"]
                    .Single()
            );

            Assert.True(applied.UndoAvailable);
            FinalWorkbookPatchApplyResult undone = await finalService.UndoAsync(
                applied.TransactionId!,
                CancellationToken.None
            );
            Assert.Equal("Undone", undone.Status);
            if (multipleOperations)
                Assert.Equal("300101", catalogReader.Read(sourceDataRoot).Table("skill").Record(100102).Fields["search_target"].Single());
            Assert.Equal(
                "300101",
                catalogReader
                    .Read(sourceDataRoot)
                    .Table("skill")
                    .Record(100101)
                    .Fields["search_target"]
                    .Single()
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
    public void RemoveLinkRejectsMismatchedAndMissingTargets()
    {
        WorkbookPatchRegistry registry = LoadRegistry();
        var compiler = new WorkbookPatchCompiler(registry);
        WorkbookPatchWorkspace workspace = CreateWorkspace(
            searchTarget: "300102"
        );

        WorkbookPatchCompileResult mismatch = compiler.Compile(
            CreateRemoveLinkPlan(workspace, registry, "search_target", 300101),
            workspace
        );
        Assert.Equal("Invalid", mismatch.Status);
        Assert.Equal(
            "compiler.remove_link_target_mismatch",
            Assert.Single(mismatch.Errors).Code
        );

        WorkbookPatchWorkspace linked = CreateWorkspace(
            searchTarget: "300101"
        );
        WorkbookPatchCompileResult missing = compiler.Compile(
            CreateRemoveLinkPlan(linked, registry, "search_target", 999999),
            linked
        );
        Assert.Equal("Invalid", missing.Status);
        Assert.Equal(
            "compiler.reference_target_missing",
            Assert.Single(missing.Errors).Code
        );
    }

    [Fact]
    public void RemoveLinkIsNoChangeWhenFieldAlreadyUnlinked()
    {
        WorkbookPatchRegistry registry = LoadRegistry();
        var compiler = new WorkbookPatchCompiler(registry);
        WorkbookPatchWorkspace workspace = CreateWorkspace(searchTarget: "0");

        WorkbookPatchCompileResult compiled = compiler.Compile(
            CreateRemoveLinkPlan(workspace, registry, "search_target", 300101),
            workspace
        );

        Assert.Equal("NoChange", compiled.Status);
        Assert.Null(compiled.Patch);
        Assert.Equal("compiler.no_change", Assert.Single(compiled.Errors).Code);
    }

    [Fact]
    public void RemoveLinkSupportsListsAndFailsClosedForUndeclaredScalarFields()
    {
        WorkbookPatchRegistry registry = LoadRegistry();
        var compiler = new WorkbookPatchCompiler(registry);

        WorkbookPatchWorkspace undeclaredRemoval = CreateWorkspace(
            searchTarget: "300101",
            searchTargetRemoval: null
        );
        WorkbookPatchWorkspace listValued = CreateWorkspace(
            searchTarget: "300101",
            searchTargetKind: GameDataFieldKind.List
        );
        WorkbookPatchWorkspace actionParameter = CreateWorkspace(
            searchTarget: "300101",
            searchTargetBinding: WorkbookPatchFieldBindingKind.ActionParameter,
            searchTargetParameterIndex: 0
        );
        WorkbookPatchWorkspace nonReference = CreateWorkspace(
            searchTarget: "300101",
            searchTargetKind: GameDataFieldKind.Integer
        );
        WorkbookPatchWorkspace undeclaredTarget = CreateWorkspace(
            searchTarget: "300101",
            searchTargetReferenceTarget: null
        );

        Assert.Equal(
            "compiler.reference_removal_undeclared",
            Assert.Single(
                compiler.Compile(
                    CreateRemoveLinkPlan(
                        undeclaredRemoval,
                        registry,
                        "search_target",
                        300101
                    ),
                    undeclaredRemoval
                ).Errors
            ).Code
        );
        Assert.Equal("Compiled", compiler.Compile(
            CreateRemoveLinkPlan(listValued, registry, "search_target", 300101), listValued).Status);
        Assert.Equal(
            "compiler.unsupported_action_parameter_link",
            Assert.Single(
                compiler.Compile(
                    CreateRemoveLinkPlan(
                        actionParameter,
                        registry,
                        "search_target",
                        300101
                    ),
                    actionParameter
                ).Errors
            ).Code
        );
        Assert.Equal(
            "compiler.remove_link_not_reference",
            Assert.Single(
                compiler.Compile(
                    CreateRemoveLinkPlan(
                        nonReference,
                        registry,
                        "search_target",
                        300101
                    ),
                    nonReference
                ).Errors
            ).Code
        );
        Assert.Equal(
            "compiler.reference_target_undeclared",
            Assert.Single(
                compiler.Compile(
                    CreateRemoveLinkPlan(
                        undeclaredTarget,
                        registry,
                        "search_target",
                        300101
                    ),
                    undeclaredTarget
                ).Errors
            ).Code
        );
    }

    [Fact]
    public void CompilerErrorCodesAreDeclaredInVersionedContract()
    {
        string contractRoot = FindContractRoot();
        string repoRoot = Directory
            .GetParent(Directory.GetParent(contractRoot)!.FullName)!
            .FullName;
        string snapshot = File.ReadAllText(
            Path.Combine(contractRoot, "config", "workbook-patch-errors.v0.json")
        );
        string factory = File.ReadAllText(
            Path.Combine(
                repoRoot,
                "contract-factory",
                "config",
                "workbook-patch-errors.v0.json"
            )
        );
        Assert.Equal(factory, snapshot);

        string source = File.ReadAllText(
            Path.Combine(
                repoRoot,
                "src",
                "RtsSkillStudio.Agent",
                "Patch",
                "WorkbookPatchCompiler.cs"
            )
        );
        var declared = JsonSerializer.Deserialize<JsonElement>(snapshot)
            .GetProperty("compile")
            .EnumerateArray()
            .Select(item => item.GetProperty("code").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        var emitted = System.Text.RegularExpressions.Regex
            .Matches(source, "\"(compiler\\.[a-z_]+)\"")
            .Select(match => match.Groups[1].Value)
            .Distinct(StringComparer.Ordinal);
        Assert.DoesNotContain(
            emitted,
            code => !declared.Contains(code)
        );
    }

    [Fact]
    public async Task ReferenceChangeValidatesAndProjectsDiffRows()
    {
        WorkbookPatchRegistry registry = LoadRegistry();
        var compiler = new WorkbookPatchCompiler(registry);
        WorkbookPatchWorkspace workspace = CreateWorkspace();
        WorkbookPatchCompileResult compiled = compiler.Compile(
            CreateLinkPlan(workspace, registry),
            workspace
        );
        Assert.NotNull(compiled.PatchJson);

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
        Assert.Contains(
            validation.Checks,
            check =>
                check.Code == "validation.reference"
                && check.Status == "Passed"
        );

        WorkbookPatchDiffRow row = Assert.Single(
            WorkbookPatchDiffProjector.Project(compiled.Patch!)
        );
        Assert.Equal("skill", row.TableKey);
        Assert.Equal("search_target", row.Field);
        Assert.Equal("0", row.Before);
        Assert.Equal("300101", row.After);
        Assert.Equal("300101", row.SemanticValue);
        Assert.False(row.IsNoOp);
    }

    [Fact]
    public async Task ValidatorRejectsPatchWhoseReferenceTargetIsMissing()
    {
        WorkbookPatchRegistry registry = LoadRegistry();
        var compiler = new WorkbookPatchCompiler(registry);
        WorkbookPatchWorkspace workspace = CreateWorkspace();
        WorkbookPatchCompileResult compiled = compiler.Compile(
            CreateLinkPlan(workspace, registry),
            workspace
        );
        Assert.NotNull(compiled.Patch);

        // Remove the linked target from the workspace after compilation.
        WorkbookPatchWorkspace shrunk = workspace with
        {
            Tables = workspace.Tables
                .Select(
                    table =>
                        table.Namespace == "TbSearch"
                            ? table with { Records = [] }
                            : table
                )
                .ToArray()
        };
        var validator = new WorkbookPatchValidator(
            FindContractRoot(),
            registry
        );
        WorkbookPatchValidationReport validation =
            await validator.ValidateAsync(
                compiled.PatchJson!,
                shrunk,
                CancellationToken.None
            );

        Assert.Equal("Invalid", validation.Status);
        Assert.Contains(
            validation.Checks,
            check =>
                check.Code == "validation.reference"
                && check.Status == "Failed"
        );
    }

    [Fact]
    public async Task ReferenceLinkRoundTripsThroughApplyAndUndo()
    {
        string testRoot = CreateTestRoot();
        try
        {
            string sourceDataRoot = Path.Combine(
                testRoot,
                "source",
                "Datas"
            );
            CreateSkillWorkbook(
                Path.Combine(sourceDataRoot, "Skill", "Skill.xlsx"),
                searchTarget: 0
            );
            CreateSearchWorkbook(
                Path.Combine(sourceDataRoot, "Search", "Search.xlsx")
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
            var catalogReader = new SkillAndSearchCatalogReader();
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
            WorkbookPatchWorkspace workspace = BuildWorkspaceFromCatalog(
                catalogReader.Read(sourceDataRoot),
                registry,
                sourceHashBefore
            );
            var compiler = new WorkbookPatchCompiler(registry);
            WorkbookPatchCompileResult compiled = compiler.Compile(
                CreateLinkPlan(workspace, registry),
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

            TemporaryWorkbookPatchApplyResult temporary =
                await temporaryService.ApplyAsync(
                    compiled.PatchJson!,
                    validation,
                    CancellationToken.None
                );
            Assert.Equal("Verified", temporary.Status);
            Assert.True(temporary.SourceUnchanged);
            Assert.Empty(temporary.Mismatches);
            Assert.Equal(
                sourceHashBefore,
                SkillWorkspaceService.ComputeSourceTreeHash(sourceDataRoot)
            );

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
            Assert.Equal(1, applied.AppliedFieldCount);
            Assert.Equal(
                "300101",
                catalogReader
                    .Read(sourceDataRoot)
                    .Table("skill")
                    .Record(100101)
                    .Fields["search_target"]
                    .Single()
            );

            Assert.True(applied.UndoAvailable);
            FinalWorkbookPatchApplyResult undone = await finalService.UndoAsync(
                applied.TransactionId!,
                CancellationToken.None
            );
            Assert.Equal("Undone", undone.Status);
            Assert.Equal(
                "0",
                catalogReader
                    .Read(sourceDataRoot)
                    .Table("skill")
                    .Record(100101)
                    .Fields["search_target"]
                    .Single()
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

    private static WorkbookPatchWorkspace BuildWorkspaceFromCatalog(
        GameDataCatalog catalog,
        WorkbookPatchRegistry registry,
        string sourceHash
    )
    {
        GameDataTable skillTable = catalog.Table("skill");
        var fields = new List<WorkbookPatchField>();
        foreach (GameDataFieldDefinition field in skillTable.Fields)
        {
            WorkbookPatchRegistryField? declared = registry.EntityFields
                .FirstOrDefault(
                    item =>
                        string.Equals(
                            item.Path,
                            $"Skill.{field.Key}",
                            StringComparison.OrdinalIgnoreCase
                        )
                );
            fields.Add(
                new WorkbookPatchField(
                    field.Key,
                    $"Skill.{field.Key}",
                    declared?.SemanticName,
                    field.Kind,
                    field.RawType,
                    field.Required,
                    declared?.Scale ?? 1,
                    declared?.Unit,
                    declared?.Minimum,
                    declared?.Maximum,
                    field.Options,
                    ResolveNamespace(field.ReferenceTable),
                    ReferenceTarget: ResolveNamespace(declared?.ReferenceTarget),
                    ReferenceRemoval: declared?.ReferenceRemoval
                )
            );
        }

        var tables = new List<WorkbookPatchTable>
        {
            new(
                "Skill",
                "TbSkill",
                "skill",
                fields,
                skillTable.Records
                    .Select(record => new WorkbookPatchRecord(record.Id, record.Fields))
                    .ToArray()
            )
        };
        if (catalog.Tables.Any(table => table.Key == "search"))
        {
            GameDataTable searchTable = catalog.Table("search");
            tables.Add(
                new WorkbookPatchTable(
                    "Search",
                    "TbSearch",
                    "search",
                    [
                        new WorkbookPatchField(
                            "Id",
                            "Search.Id",
                            null,
                            GameDataFieldKind.Integer,
                            "int",
                            true,
                            1,
                            null,
                            null,
                            null,
                            [],
                            null
                        )
                    ],
                    searchTable.Records
                        .Select(record => new WorkbookPatchRecord(record.Id, record.Fields))
                        .ToArray()
                )
            );
        }

        return new WorkbookPatchWorkspace(
            "test-workspace",
            "test-revision",
            sourceHash,
            tables
        );
    }

    private static string? ResolveNamespace(string? tableKey)
    {
        if (string.IsNullOrWhiteSpace(tableKey))
        {
            return null;
        }

        return HeroAuthoringGraphProjector.TryGetNamespace(
            tableKey,
            out string nodeNamespace
        )
            ? nodeNamespace
            : tableKey;
    }

    private static WorkbookPatchWorkspace CreateWorkspace(
        string searchTarget = "0",
        string searchTargetReferenceNamespace = "TbSearch",
        string? searchTargetReferenceTarget = "TbSearch",
        string? searchTargetRemoval = "Zero",
        GameDataFieldKind searchTargetKind = GameDataFieldKind.Reference,
        WorkbookPatchFieldBindingKind searchTargetBinding =
            WorkbookPatchFieldBindingKind.Scalar,
        int? searchTargetParameterIndex = null
    )
    {
        var skillFields = new[]
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
                "search_target",
                "Skill.search_target",
                "search_target",
                searchTargetKind,
                "int#ref=search",
                false,
                1,
                null,
                null,
                null,
                [],
                searchTargetReferenceNamespace,
                searchTargetBinding,
                RecordId: null,
                ActionKey: searchTargetBinding
                    == WorkbookPatchFieldBindingKind.ActionParameter
                        ? "TestAction"
                        : null,
                ParameterIndex: searchTargetParameterIndex,
                ReferenceTarget: searchTargetReferenceTarget,
                ReferenceRemoval: searchTargetRemoval
            ),
            new WorkbookPatchField(
                "broken_target",
                "Skill.broken_target",
                "broken_target",
                GameDataFieldKind.Reference,
                "int#ref=buff",
                false,
                1,
                null,
                null,
                null,
                [],
                null
            )
        };
        return new WorkbookPatchWorkspace(
            "studio-workspace",
            RevisionA,
            SourceHashA,
            [
                new WorkbookPatchTable(
                    "Skill",
                    "TbSkill",
                    "skill",
                    skillFields,
                    [
                        new WorkbookPatchRecord(
                            100101,
                            new Dictionary<string, IReadOnlyList<string>>
                            {
                                ["Id"] = ["100101"],
                                ["cd_time"] = ["5000"],
                                ["search_target"] = [searchTarget]
                            }
                        )
                    ]
                ),
                new WorkbookPatchTable(
                    "Search",
                    "TbSearch",
                    "search",
                    [
                        new WorkbookPatchField(
                            "Id",
                            "Search.Id",
                            null,
                            GameDataFieldKind.Integer,
                            "int",
                            true,
                            1,
                            null,
                            null,
                            null,
                            [],
                            null
                        )
                    ],
                    [
                        new WorkbookPatchRecord(
                            300101,
                            new Dictionary<string, IReadOnlyList<string>>
                            {
                                ["Id"] = ["300101"]
                            }
                        ),
                        new WorkbookPatchRecord(
                            300102,
                            new Dictionary<string, IReadOnlyList<string>>
                            {
                                ["Id"] = ["300102"]
                            }
                        )
                    ]
                )
            ]
        );
    }

    private static string CreateLinkPlan(
        WorkbookPatchWorkspace workspace,
        WorkbookPatchRegistry registry
    )
    {
        return CreateFieldPlan(
            workspace,
            registry,
            "search_target",
            new JsonObject
            {
                ["binding"] = "Existing",
                ["namespace"] = "TbSearch",
                ["id"] = 300101
            }
        );
    }

    private static string CreateFieldPlan(
        WorkbookPatchWorkspace workspace,
        WorkbookPatchRegistry registry,
        string fieldName,
        JsonNode value
    )
    {
        var plan = new JsonObject
        {
            ["schemaVersion"] = 0,
            ["planId"] = "plan-b3-link",
            ["base"] = new JsonObject
            {
                ["workspaceId"] = workspace.WorkspaceId,
                ["revision"] = workspace.Revision,
                ["sourceHash"] = workspace.SourceHash,
                ["capabilityRegistryVersion"] =
                    registry.CapabilityRegistryVersion,
                ["defaultValueContractVersion"] =
                    registry.DefaultValueContractVersion,
                ["defaultMechanismContractVersion"] =
                    registry.DefaultMechanismContractVersion
            },
            ["request"] = new JsonObject
            {
                ["text"] = "把技能的目标搜索指向 300101"
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
                    ["fields"] = new JsonObject
                    {
                        [fieldName] = new JsonObject
                        {
                            ["value"] = value.DeepClone(),
                            ["source"] = "UserEdited",
                            ["evidence"] = new JsonArray()
                        }
                    }
                }
            }
        };
        return plan.ToJsonString();
    }

    private static string CreateRemoveLinkPlan(
        WorkbookPatchWorkspace workspace,
        WorkbookPatchRegistry registry,
        string field,
        int targetId
    )
    {
        var plan = new JsonObject
        {
            ["schemaVersion"] = 0,
            ["planId"] = "plan-b3-remove-link",
            ["base"] = new JsonObject
            {
                ["workspaceId"] = workspace.WorkspaceId,
                ["revision"] = workspace.Revision,
                ["sourceHash"] = workspace.SourceHash,
                ["capabilityRegistryVersion"] =
                    registry.CapabilityRegistryVersion,
                ["defaultValueContractVersion"] =
                    registry.DefaultValueContractVersion,
                ["defaultMechanismContractVersion"] =
                    registry.DefaultMechanismContractVersion
            },
            ["request"] = new JsonObject
            {
                ["text"] = "解除技能的目标搜索链接"
            },
            ["status"] = "Ready",
            ["operations"] = new JsonArray
            {
                new JsonObject
                {
                    ["operationId"] = "op-remove-1",
                    ["kind"] = "RemoveLink",
                    ["parent"] = new JsonObject
                    {
                        ["binding"] = "Existing",
                        ["namespace"] = "TbSkill",
                        ["id"] = 100101
                    },
                    ["field"] = field,
                    ["target"] = new JsonObject
                    {
                        ["binding"] = "Existing",
                        ["namespace"] = "TbSearch",
                        ["id"] = targetId
                    }
                }
            }
        };
        return plan.ToJsonString();
    }

    private static WorkbookPatchRegistry LoadRegistry()
    {
        return WorkbookPatchRegistryLoader.Load(FindContractRoot());
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

    private static void CreateSkillWorkbook(string path, int searchTarget, bool secondRecord = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
            Row("##var", "Id", "search_target"),
            Row("##type", "int", "int#ref=search"),
            Row("##", "编号", "目标搜索"),
            Row("", "100101", searchTarget.ToString())
        );
        if (secondRecord)
            sheetData.Append(Row("", "100102", searchTarget.ToString()));
        worksheetPart.Worksheet.Save();
        workbookPart.Workbook.Save();
    }

    private static void CreateSearchWorkbook(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
                Name = "Search"
            }
        );
        SheetData sheetData = worksheetPart.Worksheet.GetFirstChild<SheetData>()!;
        sheetData.Append(
            Row("##var", "Id", "name"),
            Row("##type", "int", "string"),
            Row("##", "编号", "名称"),
            Row("", "300101", "目标搜索A"),
            Row("", "300102", "目标搜索B")
        );
        worksheetPart.Worksheet.Save();
        workbookPart.Workbook.Save();
    }

    private static Row Row(params string[] values)
    {
        var row = new Row();
        for (int index = 0; index < values.Length; index++)
        {
            row.Append(
                new Cell
                {
                    CellReference = $"{(char)('A' + index)}1",
                    DataType = CellValues.InlineString,
                    InlineString = new InlineString(new Text(values[index]))
                }
            );
        }

        return row;
    }

    private sealed class SkillAndSearchCatalogReader : IGameDataCatalogReader
    {
        public GameDataCatalog Read(string excelDataRoot)
        {
            var reader = new GameDataWorkbookReader();
            return new GameDataCatalog(
                [
                    reader.Read(
                        new GameDataTableSource(
                            "skill",
                            "技能",
                            "英雄配置图谱",
                            "Skill/Skill.xlsx",
                            Path.Combine(excelDataRoot, "Skill", "Skill.xlsx")
                        )
                    ),
                    reader.Read(
                        new GameDataTableSource(
                            "search",
                            "搜索",
                            "英雄配置图谱",
                            "Search/Search.xlsx",
                            Path.Combine(
                                excelDataRoot,
                                "Search",
                                "Search.xlsx"
                            )
                        )
                    )
                ]
            );
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
}
