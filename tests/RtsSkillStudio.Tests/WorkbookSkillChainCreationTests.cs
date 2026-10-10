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

public sealed class WorkbookSkillChainCreationTests
{
    [Fact]
    public async Task ExtendReorderAndDeleteMembersRoundTripAndUndoRestoresBytes()
    {
        using var fixture = new Fixture();
        JsonObject plan = fixture.Plan();
        JsonObject extra = plan["operations"]![0]!["nodes"]![2]!.DeepClone().AsObject();
        extra["localKey"] = "second-damage";
        plan["operations"]![0]!["nodes"]!.AsArray().Add(extra);
        var history = new List<(string Transaction, string Before)>();
        async Task<WorkbookPatchDocument> Apply(JsonObject proposal)
        {
            WorkbookPatchCompileResult result = fixture.Compile(proposal);
            Assert.True(result.Status == "Compiled", string.Join(";", result.Errors.Select(error => error.Message)));
            WorkbookPatchValidationReport report = await fixture.Validator.ValidateAsync(result.PatchJson!, fixture.Workspace(), CancellationToken.None);
            Assert.Equal("Valid", report.Status);
            string before = SkillWorkspaceService.ComputeSourceTreeHash(fixture.Source);
            FinalWorkbookPatchApplyResult applied = await fixture.Final.ApplyAsync(result.PatchJson!, report, CancellationToken.None);
            Assert.True(applied.Status == "Applied", applied.Message + string.Join(";", applied.Mismatches));
            history.Add((applied.TransactionId!, before));
            return result.Patch!;
        }
        WorkbookPatchDocument created = await Apply(plan);
        int group = created.Allocations.Single(value => value.Kind == "GroupId").Id;
        JsonObject member = extra.DeepClone().AsObject();
        member["localKey"] = "extension";
        member["group"] = new JsonObject { ["binding"] = "Existing", ["namespace"] = "EffectGroup", ["groupKey"] = group };
        JsonObject extension = fixture.Plan();
        extension["operations"] = new JsonArray(new JsonObject
        {
            ["operationId"] = "extend", ["kind"] = "ExtendAssetChain", ["entry"] = "extension",
            ["parent"] = new JsonObject { ["binding"] = "Existing", ["namespace"] = "EffectGroup", ["groupKey"] = group },
            ["nodes"] = new JsonArray(member)
        });
        Assert.True((await new SkillConfigPlanValidator(ContractRoot()).ValidateAsync(extension.ToJsonString(), CancellationToken.None)).IsValid);
        WorkbookPatchDocument extended = await Apply(extension);
        int addedId = extended.Allocations.Single().Id;
        int[] ids = created.Allocations.Where(value => value.Namespace == "TbEffect").Select(value => value.Id).Prepend(addedId).ToArray();
        JsonObject reorder = fixture.Plan();
        reorder["operations"] = new JsonArray(new JsonObject
        {
            ["operationId"] = "reorder", ["kind"] = "ReorderMembers",
            ["group"] = new JsonObject { ["binding"] = "Existing", ["namespace"] = "EffectGroup", ["groupKey"] = group },
            ["orderedMembers"] = new JsonArray(ids.Select(id => (JsonNode?)new JsonObject { ["binding"] = "Existing", ["namespace"] = "TbEffect", ["id"] = id }).ToArray())
        });
        await Apply(reorder);
        Assert.Equal(ids, fixture.Reader.Read(fixture.Source).Tables.Single(table => table.Key == "effect").Records.Where(record => ids.Contains(record.Id)).Select(record => record.Id));
        JsonObject deletion = fixture.Plan();
        deletion["operations"] = new JsonArray(new JsonObject
        {
            ["operationId"] = "delete", ["kind"] = "DeleteAsset",
            ["asset"] = new JsonObject { ["binding"] = "Existing", ["namespace"] = "TbEffect", ["id"] = addedId }
        });
        await Apply(deletion);
        Assert.DoesNotContain(fixture.Reader.Read(fixture.Source).Tables.Single(table => table.Key == "effect").Records, record => record.Id == addedId);
        foreach (var entry in history.AsEnumerable().Reverse())
        {
            Assert.Equal("Undone", (await fixture.Final.UndoAsync(entry.Transaction, CancellationToken.None)).Status);
            Assert.Equal(entry.Before, SkillWorkspaceService.ComputeSourceTreeHash(fixture.Source));
        }
    }

    [Fact]
    public void MapCreationEncodesTypedKeysAndRejectsNormalizedDuplicates()
    {
        using var fixture = new Fixture();
        JsonObject plan = fixture.Plan();
        JsonObject operation = plan["operations"]![0]!.AsObject();
        operation["groups"]!.AsArray().Add(new JsonObject { ["localKey"] = "ticks", ["namespace"] = "EffectGroup" });
        operation["nodes"]![2]!["group"] = Local("EffectGroup", "ticks");
        operation["nodes"]!.AsArray().Add(new JsonObject
        {
            ["localKey"] = "buff", ["namespace"] = "TbBuff", ["fields"] = new JsonObject
            {
                ["interval_effect"] = Value(Local("EffectGroup", "ticks")),
                ["param"] = Value(new JsonObject { ["alpha"] = 10, ["beta"] = 20 })
            }
        });
        operation["nodes"]!.AsArray().Add(new JsonObject
        {
            ["localKey"] = "apply", ["namespace"] = "TbEffect", ["actionKey"] = "AddBuff", ["group"] = Local("EffectGroup", "main"),
            ["fields"] = new JsonObject(), ["parameters"] = new JsonObject
            { ["buffId"] = Value(Local("TbBuff", "buff")), ["applyTarget"] = Value(JsonValue.Create("Target")) }
        });
        WorkbookPatchCompileResult result = fixture.Compile(plan);
        Assert.True(result.Status == "Compiled", string.Join(";", result.Errors.Select(error => error.Message)));
        Assert.Equal(new[] { "alpha", "10", "beta", "20" }, result.Patch!.FieldChanges.Where(change => change.Field.StartsWith("param[")).Select(change => change.After.GetString()));
    }

    [Fact]
    public async Task BatchCreationReservesIdsAndRollsBackAsOneTransaction()
    {
        using var fixture = new Fixture();
        JsonObject plan = fixture.Plan();
        JsonObject second = plan["operations"]![0]!.DeepClone().AsObject();
        second["operationId"] = "second";
        plan["operations"]!.AsArray().Add(second);
        WorkbookPatchCompileResult result = fixture.Compile(plan);
        Assert.True(result.Status == "Compiled", string.Join(";", result.Errors.Select(error => error.Message)));
        Assert.Equal(result.PatchJson, fixture.Compile(plan).PatchJson);
        Assert.Equal(8, result.Patch!.Allocations.Count);
        Assert.Equal(8, result.Patch.Allocations.Select(value => (value.Namespace, value.Id)).Distinct().Count());
        WorkbookPatchValidationReport report = await fixture.Validator.ValidateAsync(result.PatchJson!, fixture.Workspace(), CancellationToken.None);
        Assert.Equal("Valid", report.Status);
        FinalWorkbookPatchApplyResult applied = await fixture.Final.ApplyAsync(result.PatchJson!, report, CancellationToken.None);
        Assert.Equal("Applied", applied.Status);
        Assert.Equal(3, fixture.Reader.Read(fixture.Source).Tables.Single(table => table.Key == "skill").Records.Count);
        Assert.Equal("Undone", (await fixture.Final.UndoAsync(applied.TransactionId!, CancellationToken.None)).Status);
        Assert.Equal(fixture.OriginalHash, SkillWorkspaceService.ComputeSourceTreeHash(fixture.Source));
        second["nodes"]![2]!["parameters"]!.AsObject().Remove("fixedDamage");
        Assert.Null(fixture.Compile(plan).Patch);
    }

    [Fact]
    public async Task RepeatedParametersCompileWithContractStrideAndRejectMismatchedPairs()
    {
        using var fixture = new Fixture();
        JsonObject plan = fixture.Plan();
        JsonObject node = plan["operations"]![0]!["nodes"]![2]!.AsObject();
        node["actionKey"] = "AddProperty";
        WorkbookPatchAction action = fixture.Registry.Actions.Single(action => action.Key == "AddProperty");
        string enumValue = action.Parameters[0].Options.First().Value;
        node["parameters"] = new JsonObject
        {
            ["numericType"] = Value(new JsonArray(enumValue, enumValue)),
            ["value"] = Value(new JsonArray(1, 2))
        };
        WorkbookPatchCompileResult result = fixture.Compile(plan);
        Assert.True(result.Status == "Compiled", string.Join(";", result.Errors.Select(error => error.Message)));
        Assert.Equal("10000", result.Patch!.FieldChanges.Single(change => change.Field == "action_param[1]").After.GetString());
        Assert.Equal("20000", result.Patch.FieldChanges.Single(change => change.Field == "action_param[3]").After.GetString());
        WorkbookPatchValidationReport report = await fixture.Validator.ValidateAsync(result.PatchJson!, fixture.Workspace(), CancellationToken.None);
        Assert.Equal("Valid", report.Status);
        Assert.Equal("Verified", (await fixture.Temporary.ApplyAsync(result.PatchJson!, report, CancellationToken.None)).Status);
        node["parameters"]!["value"]!["value"]!.AsArray().RemoveAt(1);
        Assert.Null(fixture.Compile(plan).Patch);
    }

    [Fact]
    public async Task ListEditingClearsTailAndNestedValuesRoundTripWithoutPrematureScaling()
    {
        using var fixture = new Fixture();
        JsonObject plan = fixture.Plan();
        plan["operations"] = new JsonArray(new JsonObject
        {
            ["operationId"] = "list", ["kind"] = "ModifySkill",
            ["skill"] = new JsonObject { ["binding"] = "Existing", ["namespace"] = "TbSkill", ["id"] = 100 },
            ["fields"] = new JsonObject { ["trigger_array"] = Value(new JsonArray(4, 8)) }
        });
        WorkbookPatchCompileResult result = fixture.Compile(plan);
        Assert.True(result.Status == "Compiled", string.Join(";", result.Errors.Select(error => error.Message)));
        WorkbookPatchValidationReport report = await fixture.Validator.ValidateAsync(result.PatchJson!, fixture.Workspace(), CancellationToken.None);
        Assert.Equal("Valid", report.Status);
        Assert.Equal("Verified", (await fixture.Temporary.ApplyAsync(result.PatchJson!, report, CancellationToken.None)).Status);
        WorkbookPatchWorkspace context = fixture.Workspace();
        WorkbookPatchTable table = context.Tables.Single(table => table.Namespace == "TbSkill");
        var fields = table.Records[0].Fields.ToDictionary(pair => pair.Key, pair => pair.Value);
        fields["trigger_array"] = new[] { "1", "2", "3" };
        context = context with { Tables = context.Tables.Select(candidate => candidate == table ? candidate with
            { Records = new[] { candidate.Records[0] with { Fields = fields } } } : candidate).ToArray() };
        WorkbookPatchCompileResult shortened = new WorkbookPatchCompiler(fixture.Registry).Compile(plan.ToJsonString(), context);
        Assert.Equal("", shortened.Patch!.FieldChanges.Single(change => change.Field == "trigger_array[2]").After.GetString());
        JsonObject tampered = JsonNode.Parse(result.PatchJson!)!.AsObject();
        tampered["fieldChanges"]![0]!["after"] = "999";
        WorkbookPatchDocument forged = WorkbookPatchJson.Deserialize(tampered.ToJsonString())!;
        forged = forged with { PatchId = WorkbookPatchJsonUtilities.ComputePatchId(forged) };
        Assert.Equal("Invalid", (await fixture.Validator.ValidateAsync(WorkbookPatchJson.Serialize(forged), fixture.Workspace(), CancellationToken.None)).Status);
    }

    [Fact]
    public void RebaseRetainsProposalSourcesAndRejectsConcurrentFieldChanges()
    {
        using var fixture = new Fixture();
        JsonObject plan = fixture.Plan();
        plan["operations"] = new JsonArray(new JsonObject
        {
            ["operationId"] = "edit", ["kind"] = "ModifySkill",
            ["skill"] = new JsonObject { ["binding"] = "Existing", ["namespace"] = "TbSkill", ["id"] = 100 },
            ["fields"] = new JsonObject { ["cooldown"] = Value(JsonValue.Create(9), "s") }
        });
        WorkbookPatchCompileResult result = fixture.Compile(plan);
        WorkbookPatchWorkspace context = fixture.Workspace() with { Revision = "new", SourceHash = new string('b', 64) };
        WorkbookPlanRebaseResult rebased = WorkbookPlanRebaser.Rebase(plan.ToJsonString(), result.PatchJson!, context, fixture.Registry);
        Assert.Empty(rebased.Errors);
        Assert.Equal("Compiled", new WorkbookPatchCompiler(fixture.Registry).Compile(rebased.PlanJson!, context).Status);
        WorkbookPatchTable table = context.Tables.Single(table => table.Namespace == "TbSkill");
        var fields = table.Records[0].Fields.ToDictionary(pair => pair.Key, pair => pair.Value);
        fields["cd_time"] = new[] { "5000" };
        context = context with { Tables = context.Tables.Select(candidate => candidate == table ? candidate with
            { Records = new[] { candidate.Records[0] with { Fields = fields } } } : candidate).ToArray() };
        Assert.Null(WorkbookPlanRebaser.Rebase(plan.ToJsonString(), result.PatchJson!, context, fixture.Registry).PlanJson);
    }

    [Fact]
    public void MultiStageBuffChainUsesTheSameGenericCompilerAndRejectsCycles()
    {
        using var fixture = new Fixture();
        JsonObject plan = fixture.Plan();
        JsonObject operation = plan["operations"]![0]!.AsObject();
        operation["groups"]!.AsArray().Add(new JsonObject { ["localKey"] = "ticks", ["namespace"] = "EffectGroup" });
        operation["nodes"]![2]!["group"] = Local("EffectGroup", "ticks");
        operation["nodes"]!.AsArray().Add(new JsonObject
        {
            ["localKey"] = "buff", ["namespace"] = "TbBuff", ["fields"] = new JsonObject
            {
                ["duration"] = Value(JsonValue.Create(6000)), ["interval"] = Value(JsonValue.Create(1000)),
                ["interval_effect"] = Value(Local("EffectGroup", "ticks"))
            }
        });
        operation["nodes"]!.AsArray().Add(new JsonObject
        {
            ["localKey"] = "apply", ["namespace"] = "TbEffect", ["actionKey"] = "AddBuff", ["group"] = Local("EffectGroup", "main"),
            ["fields"] = new JsonObject(), ["parameters"] = new JsonObject
            {
                ["buffId"] = Value(Local("TbBuff", "buff")), ["applyTarget"] = Value(JsonValue.Create("Target"))
            }
        });
        WorkbookPatchCompileResult result = fixture.Compile(plan);
        Assert.True(result.Status == "Compiled", string.Join(";", result.Errors.Select(error => error.Message)));
        Assert.Equal(7, result.Patch!.Allocations.Count);
        operation["nodes"]![3]!["fields"]!["interval_effect"]!["value"] = Local("EffectGroup", "main");
        WorkbookPatchCompileResult cycle = fixture.Compile(plan);
        Assert.Null(cycle.Patch);
        Assert.Contains(cycle.Errors, error => error.Message.Contains("循环"));
    }

    [Fact]
    public async Task CompleteSkillSearchDamageChainRoundTripsAndUndoRestoresEveryWorkbook()
    {
        using var fixture = new Fixture();
        JsonObject plan = fixture.Plan();
        SkillConfigPlanValidationResult planValidation = await new SkillConfigPlanValidator(ContractRoot()).ValidateAsync(plan.ToJsonString(), CancellationToken.None);
        Assert.True(planValidation.IsValid, string.Join(";", planValidation.Errors));
        string repairSchema = await new SkillConfigPlanValidator(ContractRoot()).ReadSchemaAsync(CancellationToken.None, "CreateSkillChainOperation");
        Assert.DoesNotContain("ModifySkillOperation", repairSchema);
        var focusedSchema = await NJsonSchema.JsonSchema.FromJsonAsync(repairSchema
            .Replace("\"$defs\"", "\"definitions\"").Replace("#/$defs/", "#/definitions/"));
        Assert.Empty(focusedSchema.Validate(plan.ToJsonString()));
        JsonObject metadata = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(ContractRoot(), "config", "default-value-contract.v0.json")))!.AsObject();
        JsonObject registryJson = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(ContractRoot(), "config", "capability-registry.v0.json")))!.AsObject();
        metadata["creation"]!["nestedTypes"] = registryJson["nestedTypes"]!.DeepClone();
        string context = new JsonObject { ["creationContract"] = metadata["creation"]!.DeepClone(),
            ["effects"] = new JsonArray(registryJson["effects"]!.AsArray().OfType<JsonObject>().Single(action => action["key"]!.GetValue<string>() == "Damage").DeepClone()) }.ToJsonString();
        string bound = AgentToolLoop.BindCreationRepairSchema(repairSchema, [new("get_capability_context", "{}", context, false)]);
        var boundSchema = await NJsonSchema.JsonSchema.FromJsonAsync(bound.Replace("\"$defs\"", "\"definitions\"").Replace("#/$defs/", "#/definitions/"));
        Assert.Empty(boundSchema.Validate(plan.ToJsonString()));
        JsonObject wrongNamespace = plan.DeepClone().AsObject();
        wrongNamespace["operations"]![0]!["nodes"]![0]!["namespace"] = "invented";
        Assert.NotEmpty(boundSchema.Validate(wrongNamespace.ToJsonString()));
        WorkbookPatchCompileResult result = fixture.Compile(plan);
        Assert.True(result.Status == "Compiled", string.Join(";", result.Errors.Select(error => error.Message)));
        Assert.Equal(result.PatchJson, fixture.Compile(plan).PatchJson);
        WorkbookPatchDocument patch = result.Patch!;
        Assert.Equal(4, patch.Allocations.Count);
        Assert.Equal(3, patch.Commands.Count);
        Assert.All(patch.Commands, command => Assert.Equal("CreateNode", command.Kind));
        WorkbookPatchAllocation skill = patch.Allocations.Single(value => value.LocalKey == "skill");
        WorkbookPatchAllocation search = patch.Allocations.Single(value => value.LocalKey == "search");
        WorkbookPatchAllocation effect = patch.Allocations.Single(value => value.LocalKey == "damage");
        WorkbookPatchAllocation group = patch.Allocations.Single(value => value.LocalKey == "main");
        Assert.Equal("8000", patch.FieldChanges.Single(change => change.Namespace == "TbSkill" && change.Field == "cd_time").After.GetString());
        Assert.Equal("500000", patch.FieldChanges.Single(change => change.Field == "action_param[2]").After.GetString());
        Assert.Equal("0,0,30000", patch.FieldChanges.Single(change => change.Field == "shape_param[0]").After.GetString());
        WorkbookPatchValidationReport report = await fixture.Validator.ValidateAsync(result.PatchJson!, fixture.Workspace(), CancellationToken.None);
        Assert.Equal("Valid", report.Status);
        TemporaryWorkbookPatchApplyResult temporary = await fixture.Temporary.ApplyAsync(result.PatchJson!, report, CancellationToken.None);
        Assert.Equal("Verified", temporary.Status);
        Assert.True(temporary.SourceUnchanged);
        Assert.Equal(fixture.OriginalHash, SkillWorkspaceService.ComputeSourceTreeHash(fixture.Source));
        FinalWorkbookPatchApplyResult applied = await fixture.Final.ApplyAsync(result.PatchJson!, report, CancellationToken.None);
        Assert.True(applied.Status == "Applied", applied.Message + string.Join(";", applied.Mismatches));
        GameDataCatalog catalog = fixture.Reader.Read(fixture.Source);
        Assert.Equal(search.Id.ToString(), catalog.Table("skill").Record(skill.Id).Fields["search_target"].Single());
        Assert.Equal(group.Id.ToString(), catalog.Table("skill").Record(skill.Id).Fields["effect_group_id"].Single());
        Assert.Equal(group.Id.ToString(), catalog.Table("effect").Record(effect.Id).Fields["group_id"].Single());
        Assert.Equal("500000", catalog.Table("effect").Record(effect.Id).Fields["action_param"][2]);
        Assert.Equal("0,0,30000", catalog.Table("search").Record(search.Id).Fields["shape_param"][0]);
        Assert.Equal(fixture.SentinelRow, ReadRow(Path.Combine(fixture.Source, "Skill.xlsx"), 5));
        using (SpreadsheetDocument written = SpreadsheetDocument.Open(Path.Combine(fixture.Source, "Skill.xlsx"), false))
        {
            Worksheet sheet = written.WorkbookPart!.WorksheetParts.First().Worksheet!;
            string extent = sheet.GetFirstChild<SheetDimension>()!.Reference!.Value!;
            Assert.EndsWith("6", extent);
        }
        Assert.Equal(2, catalog.Table("skill").Records.Count);
        FinalWorkbookPatchApplyResult undo = await fixture.Final.UndoAsync(applied.TransactionId!, CancellationToken.None);
        Assert.Equal("Undone", undo.Status);
        Assert.Equal(fixture.OriginalHash, SkillWorkspaceService.ComputeSourceTreeHash(fixture.Source));
        Assert.Single(fixture.Reader.Read(fixture.Source).Table("skill").Records);
    }

    [Theory]
    [InlineData("missing-reference")]
    [InlineData("wrong-namespace")]
    [InlineData("missing-parameter")]
    [InlineData("orphan")]
    [InlineData("duplicate-key")]
    [InlineData("no-entry")]
    [InlineData("invalid-enum")]
    [InlineData("overflow")]
    [InlineData("schema-drift")]
    public void InvalidCreationNeverEmitsAPartialPatch(string scenario)
    {
        using var fixture = new Fixture();
        JsonObject plan = fixture.Plan();
        JsonObject op = plan["operations"]![0]!.AsObject();
        JsonObject skill = op["nodes"]![0]!.AsObject();
        JsonObject damage = op["nodes"]![2]!.AsObject();
        WorkbookPatchWorkspace workspace = fixture.Workspace();
        switch (scenario)
        {
            case "missing-reference": damage["parameters"]!["pipeline"]!["value"]!["id"] = 999999; break;
            case "wrong-namespace": damage["parameters"]!["pipeline"]!["value"]!["namespace"] = "TbSkill"; break;
            case "missing-parameter": damage["parameters"]!.AsObject().Remove("fixedDamage"); break;
            case "orphan":
                JsonObject extra = op["nodes"]![1]!.DeepClone().AsObject(); extra["localKey"] = "unused"; op["nodes"]!.AsArray().Add(extra); break;
            case "duplicate-key": damage["localKey"] = "skill"; break;
            case "no-entry": skill["fields"]!.AsObject().Remove("effect_group_id"); break;
            case "invalid-enum": damage["parameters"]!["attackType"]!["value"] = "MadeUpProperty"; break;
            case "overflow": workspace = workspace with { Tables = workspace.Tables.Select(table => table.Namespace == "TbSkill"
                ? table with { Records = [new WorkbookPatchRecord(int.MaxValue, new Dictionary<string, IReadOnlyList<string>>())] } : table).ToArray() }; break;
            case "schema-drift": workspace = workspace with { Tables = workspace.Tables.Select(table => table.Namespace == "TbSkill"
                ? table with { Fields = table.Fields.Select(field => field.Key == "cd_time" ? field with { RawType = "string" } : field).ToArray() } : table).ToArray() }; break;
        }
        WorkbookPatchCompileResult result = new WorkbookPatchCompiler(fixture.Registry).Compile(plan.ToJsonString(), workspace);
        Assert.Equal("Invalid", result.Status);
        Assert.Null(result.Patch);
        Assert.Equal(fixture.OriginalHash, SkillWorkspaceService.ComputeSourceTreeHash(fixture.Source));
    }

    [Fact]
    public async Task ValidatorRejectsTamperedCreationAndStaleSource()
    {
        using var fixture = new Fixture();
        WorkbookPatchCompileResult compiled = fixture.Compile(fixture.Plan());
        Assert.True(compiled.Status == "Compiled", string.Join(";", compiled.Errors.Select(error => error.Message)));
        WorkbookPatchDocument patch = compiled.Patch!;
        WorkbookPatchDocument forged = patch with { FieldChanges = patch.FieldChanges.Select(change => change.Field == "action_param[2]"
            ? change with { After = JsonSerializer.SerializeToElement("1") } : change).ToArray() };
        forged = forged with { PatchId = WorkbookPatchJsonUtilities.ComputePatchId(forged) };
        Assert.Equal("Invalid", (await fixture.Validator.ValidateAsync(WorkbookPatchJson.Serialize(forged), fixture.Workspace(), CancellationToken.None)).Status);
        Assert.Equal("Invalid", (await fixture.Validator.ValidateAsync(WorkbookPatchJson.Serialize(patch), fixture.Workspace() with { SourceHash = new string('b', 64) }, CancellationToken.None)).Status);
    }

    private static JsonObject Value(JsonNode? value, string? unit = null)
    {
        var result = new JsonObject { ["value"] = value, ["source"] = "UserEdited" };
        if (unit is not null) result["unit"] = unit;
        return result;
    }
    private static JsonObject Local(string ns, string key) => new() { ["binding"] = "Local", ["namespace"] = ns, ["localKey"] = key };
    private static string ContractRoot([System.Runtime.CompilerServices.CallerFilePath] string file = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "..", "contracts", "rts-skill-agent"));
    private static string ReadRow(string path, uint index)
    {
        using SpreadsheetDocument doc = SpreadsheetDocument.Open(path, false);
        Sheets sheets = doc.WorkbookPart!.Workbook!.GetFirstChild<Sheets>()!;
        WorksheetPart part = (WorksheetPart)doc.WorkbookPart!.GetPartById(sheets.Elements<Sheet>().First().Id!.Value!);
        return part.Worksheet!.GetFirstChild<SheetData>()!.Elements<Row>().Single(row => row.RowIndex?.Value == index).OuterXml;
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "RtsSkillCreationTests", Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Root, "source");
        public WorkbookPatchRegistry Registry { get; } = WorkbookPatchRegistryLoader.Load(ContractRoot());
        public CatalogReader Reader { get; } = new();
        public string OriginalHash { get; }
        public string SentinelRow { get; }
        public WorkbookPatchValidator Validator { get; }
        public TemporaryWorkbookPatchApplyService Temporary { get; }
        public FinalWorkbookPatchApplyService Final { get; }
        public Fixture()
        {
            Directory.CreateDirectory(Source);
            foreach (string ns in new[] { "TbSkill", "TbSearch", "TbEffect", "TbBuff" })
            {
                JsonObject definition = Registry.Creation!["entities"]!.AsArray().OfType<JsonObject>().Single(value => value["namespace"]!.GetValue<string>() == ns);
                WriteWorkbook(Path.Combine(Source, definition["entityKey"]!.GetValue<string>() + ".xlsx"), definition, Registry);
            }
            WriteWorkbook(Path.Combine(Source, "DamagePipeline.xlsx"), new JsonObject { ["fields"] = new JsonArray(new JsonObject { ["key"] = "Id", ["rawType"] = "int" }) }, Registry);
            OriginalHash = SkillWorkspaceService.ComputeSourceTreeHash(Source);
            SentinelRow = ReadRow(Path.Combine(Source, "Skill.xlsx"), 5);
            var options = new SkillWorkspaceOptions { ExcelDataRoot = Source, WriteTestRoot = Path.Combine(Root, "work"), TransactionRoot = Path.Combine(Root, "transactions") };
            var environment = new TestEnvironment { ContentRootPath = Root };
            Temporary = new(options, environment, Reader, new GameDataWorkbookReader(), NullLogger<TemporaryWorkbookPatchApplyService>.Instance);
            Final = new(options, Temporary, Reader, new WorkbookPatchTransactionStore(options, environment, NullLogger<WorkbookPatchTransactionStore>.Instance), NullLogger<FinalWorkbookPatchApplyService>.Instance);
            Validator = new(ContractRoot(), Registry);
        }
        public WorkbookPatchWorkspace Workspace()
        {
            return new("creation-test", "revision-a", SkillWorkspaceService.ComputeSourceTreeHash(Source), Reader.Read(Source).Tables.Select(table =>
                new WorkbookPatchTable(table.DisplayName, "Tb" + table.DisplayName, table.Key,
                    table.Fields.Select(field =>
                    {
                        WorkbookPatchRegistryField? metadata = Registry.EntityFields.FirstOrDefault(item => item.Path == table.DisplayName + "." + field.Key);
                        return new WorkbookPatchField(field.Key, table.DisplayName + "." + field.Key, metadata?.SemanticName ?? field.Key, field.Kind, field.RawType,
                            field.Required, metadata?.Scale ?? 1, metadata?.Unit, metadata?.Minimum, metadata?.Maximum, field.Options,
                            field.ReferenceTable, ReferenceTarget: metadata?.ReferenceTarget, ColumnCount: field.ColumnCount, PackingSeparator: field.PackingSeparator);
                    }).ToArray(), table.Records.Select(record => new WorkbookPatchRecord(record.Id, record.Fields, record.SourceOrder)).ToArray())).ToArray());
        }
        public WorkbookPatchCompileResult Compile(JsonObject plan) => new WorkbookPatchCompiler(Registry).Compile(plan.ToJsonString(), Workspace());
        public JsonObject Plan()
        {
            WorkbookPatchWorkspace workspace = Workspace();
            return new JsonObject
            {
                ["schemaVersion"] = 0, ["planId"] = "create-skill-chain", ["status"] = "Ready",
                ["base"] = JsonSerializer.SerializeToNode(new WorkbookPatchBase(workspace.WorkspaceId, workspace.Revision, workspace.SourceHash, Registry.CapabilityRegistryVersion, Registry.DefaultValueContractVersion, Registry.DefaultMechanismContractVersion), WorkbookPatchJson.Options),
                ["request"] = new JsonObject { ["text"] = "创建冷却8秒、半径3、对敌方英雄造成50点固定伤害的自动技能" },
                ["operations"] = new JsonArray(new JsonObject
                {
                    ["operationId"] = "create-chain", ["kind"] = "CreateSkillChain", ["root"] = "skill",
                    ["groups"] = new JsonArray(new JsonObject { ["localKey"] = "main", ["namespace"] = "EffectGroup" }),
                    ["nodes"] = new JsonArray(
                        new JsonObject { ["localKey"] = "skill", ["namespace"] = "TbSkill", ["fields"] = new JsonObject { ["cooldown"] = Value(JsonValue.Create(8), "s"), ["effect_group_id"] = Value(Local("EffectGroup", "main")), ["search_target"] = Value(Local("TbSearch", "search")) } },
                        new JsonObject { ["localKey"] = "search", ["namespace"] = "TbSearch", ["fields"] = new JsonObject { ["shape"] = Value(JsonValue.Create("Circle")), ["shape_param"] = Value(new JsonArray(new JsonObject { ["PropId"] = "None", ["Scale"] = 0, ["Fix"] = 3 })), ["team"] = Value(JsonValue.Create("Enemy")), ["type"] = Value(JsonValue.Create("Hero")), ["count"] = Value(JsonValue.Create(1)) } },
                        new JsonObject { ["localKey"] = "damage", ["namespace"] = "TbEffect", ["group"] = Local("EffectGroup", "main"), ["actionKey"] = "Damage", ["fields"] = new JsonObject { ["name"] = Value(JsonValue.Create("创建链路测试伤害")) }, ["parameters"] = new JsonObject { ["pipeline"] = Value(new JsonObject { ["binding"] = "Existing", ["namespace"] = "TbDamagePipeline", ["id"] = 100 }), ["attackType"] = Value(JsonValue.Create("None")), ["fixedDamage"] = Value(JsonValue.Create(50)), ["attackScale"] = Value(JsonValue.Create(0)) } })
                })
            };
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }

    public sealed class CatalogReader : IGameDataCatalogReader
    {
        public GameDataCatalog Read(string root) => new(Directory.GetFiles(root, "*.xlsx").OrderBy(path => path).Select(path =>
            new GameDataWorkbookReader().Read(new GameDataTableSource(Path.GetFileNameWithoutExtension(path).ToLowerInvariant(), Path.GetFileNameWithoutExtension(path), "Test", Path.GetFileName(path), path))).ToArray());
    }
    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "CreationTests";
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    private static void WriteWorkbook(string path, JsonObject definition, WorkbookPatchRegistry registry)
    {
        using SpreadsheetDocument doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        WorkbookPart workbook = doc.AddWorkbookPart(); workbook.Workbook = new Workbook(new Sheets());
        JsonObject[] fields = definition["fields"]!.AsArray().OfType<JsonObject>().ToArray();
        var headers = new List<string> { "##var" }; var types = new List<string> { "##type" }; var labels = new List<string> { "##" }; var data = new List<string> { "" };
        foreach (JsonObject field in fields)
        {
            string key = field["key"]!.GetValue<string>(); string rawType = field["rawType"]!.GetValue<string>();
            int count = rawType.StartsWith("array,") || rawType.StartsWith("map,") ? 8 : 1;
            for (int i = 0; i < count; i++) { headers.Add(i == 0 ? key : ""); types.Add(i == 0 ? rawType : ""); labels.Add(i == 0 ? key : ""); data.Add(key == "Id" ? "100" : ""); }
        }
        AddSheet(Path.GetFileNameWithoutExtension(path), [MakeRow(1, headers), MakeRow(2, types), MakeRow(3, labels), MakeRow(5, data)]);
        foreach (JsonObject e in registry.Creation!["enums"]!.AsArray().OfType<JsonObject>())
        {
            string name = e["name"]!.GetValue<string>();
            var rows = new List<Row> { MakeRow(1, ["序号", "名字", "中文", "数值"]) };
            uint index = 2;
            foreach (JsonObject value in e["values"]!.AsArray().OfType<JsonObject>())
                rows.Add(MakeRow(index++, ["", value["name"]!.GetValue<string>(), value["alias"]?.GetValue<string>() ?? "", value["value"]!.ToJsonString()]));
            AddSheet("(" + name + ")", rows);
        }
        foreach ((string name, string category) in new[] { ("EffectActionType", "effect"), ("EConditionType", "condition") })
            AddSheet("(" + name + ")", new[] { MakeRow(1, ["序号", "名字", "中文", "数值"]) }.Concat(registry.Actions.Where(action => action.Category == category).Select((action, index) => MakeRow((uint)index + 2, ["", action.Key, "", action.LegacyValue.ToString()!]))));
        workbook.Workbook.Save();
        void AddSheet(string name, IEnumerable<Row> rows)
        {
            Row[] data = rows.ToArray();
            WorksheetPart part = workbook.AddNewPart<WorksheetPart>();
            part.Worksheet = new Worksheet(new SheetDimension { Reference = "A1:Z" + data.Max(row => row.RowIndex!.Value) }, new SheetData(data));
            workbook.Workbook.GetFirstChild<Sheets>()!.Append(new Sheet { Id = workbook.GetIdOfPart(part), SheetId = (uint)workbook.Workbook.GetFirstChild<Sheets>()!.ChildElements.Count + 1, Name = name }); part.Worksheet.Save();
        }
    }
    private static Row MakeRow(uint index, IReadOnlyList<string> values)
    {
        var row = new Row { RowIndex = index };
        for (int column = 0; column < values.Count; column++)
        {
            int number = column + 1; string label = "";
            while (number > 0) { number--; label = (char)('A' + number % 26) + label; number /= 26; }
            row.Append(new Cell { CellReference = label + index, DataType = CellValues.InlineString, InlineString = new InlineString(new Text(values[column])) });
        }
        return row;
    }
}
