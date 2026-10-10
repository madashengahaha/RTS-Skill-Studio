using RtsSkillStudio.Agent.Patch;
using TianshuDM.Application.GameData;
using TianshuDM.Domain.GameData;

namespace RtsSkillStudio.Api.Workspaces;

public sealed record TemporaryWorkbookPatchApplyResult(
    string Status,
    string PatchId,
    string OutputRoot,
    string SourceHash,
    string CopiedHash,
    string WrittenHash,
    bool SourceUnchanged,
    int VerifiedFieldCount,
    IReadOnlyList<string> Mismatches,
    string Message
);

public sealed class TemporaryWorkbookPatchApplyService(
    SkillWorkspaceOptions options,
    IHostEnvironment environment,
    IGameDataCatalogReader catalogReader,
    IGameDataWorkbookReader workbookReader,
    ILogger<TemporaryWorkbookPatchApplyService> logger
)
{
    public async Task<TemporaryWorkbookPatchApplyResult> ApplyAsync(
        string patchJson,
        WorkbookPatchValidationReport validation,
        CancellationToken cancellationToken
    )
    {
        WorkbookPatchDocument patch = WorkbookPatchJson.Deserialize(patchJson)
            ?? throw new InvalidDataException(
                "WorkbookPatch JSON 无法反序列化。"
            );
        if (
            validation.Status != "Valid"
            || !string.Equals(
                validation.PatchId,
                patch.PatchId,
                StringComparison.Ordinal
            )
        )
        {
            return new TemporaryWorkbookPatchApplyResult(
                "Blocked",
                patch.PatchId,
                "",
                "",
                "",
                "",
                true,
                0,
                ["Validation must be Valid for the same patchId before apply."],
                "校验未通过，未创建临时工作区。"
            );
        }

        string sourceDataRoot = Path.GetFullPath(options.ExcelDataRoot);
        string sourceHash = SkillWorkspaceService.ComputeSourceTreeHash(
            sourceDataRoot
        );
        string outputRoot = Path.Combine(
            ResolveContentRootPath(options.WriteTestRoot),
            $"workbook-patch-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{patch.PatchId[..8]}-{Guid.NewGuid():N}"
        );
        string outputDataRoot = Path.Combine(
            outputRoot,
            "Unity",
            "Assets",
            "Config",
            "Excel",
            "Datas"
        );

        try
        {
            TemporaryWorkbookPatchApplyResult result = await Task.Run(
                () =>
                {
                    Directory.CreateDirectory(outputDataRoot);
                    IReadOnlyCollection<string> tableKeys = WorkbookPatchImpact.TableKeys(patch).ToArray();
                    GameDataCatalog sourceCatalog =
                        catalogReader.Read(sourceDataRoot, tableKeys);
                    string copiedHash =
                        SkillWorkspaceService.ComputeSourceTreeHash(
                            outputDataRoot
                        );
                    ApplyAndVerify(
                        patch,
                        sourceCatalog,
                        sourceDataRoot,
                        outputDataRoot,
                        out string writtenHash,
                        out int verifiedFieldCount,
                        out IReadOnlyList<string> mismatches
                    );
                    string sourceHashAfter =
                        SkillWorkspaceService.ComputeSourceTreeHash(
                            sourceDataRoot
                        );
                    bool sourceUnchanged = string.Equals(
                        sourceHash,
                        sourceHashAfter,
                        StringComparison.Ordinal
                    );
                    bool verified =
                        sourceUnchanged
                        && mismatches.Count == 0
                        && verifiedFieldCount == WorkbookPatchImpact.ChangeCount(patch);
                    return new TemporaryWorkbookPatchApplyResult(
                        verified ? "Verified" : "Failed",
                        patch.PatchId,
                        outputRoot,
                        sourceHash,
                        copiedHash,
                        writtenHash,
                        sourceUnchanged,
                        verifiedFieldCount,
                        mismatches,
                        verified
                            ? "临时工作区写入并重读验证通过；源工作区未修改。"
                            : "临时工作区写入或重读验证失败；源工作区未修改。"
                    );
                },
                cancellationToken
            );
            PrunePatchRuns(ResolveContentRootPath(options.WriteTestRoot));
            logger.LogInformation(
                "Temporary WorkbookPatch apply finished with {Status} for {PatchId}.",
                result.Status,
                patch.PatchId
            );
            return result;
        }
        catch
        {
            PrunePatchRuns(ResolveContentRootPath(options.WriteTestRoot));
            throw;
        }
    }

    private void ApplyAndVerify(
        WorkbookPatchDocument patch,
        GameDataCatalog sourceCatalog,
        string sourceDataRoot,
        string outputDataRoot,
        out string writtenHash,
        out int verifiedFieldCount,
        out IReadOnlyList<string> mismatches
    )
    {
        GameDataCatalog catalog = sourceCatalog;
        var mismatchesList = new List<string>();
        var stagedTables = new List<(string RelativePath, GameDataTable Table)>();
        foreach (string tableKey in WorkbookPatchImpact.TableKeys(patch).OrderBy(key => key, StringComparer.Ordinal))
        {
            WorkbookFieldChange[] tableChanges = patch.FieldChanges.Where(change => change.LogicalAddress.TableKey == tableKey).ToArray();
            GameDataTable? table = catalog.Tables.FirstOrDefault(
                item => string.Equals(
                    item.Key,
                    tableKey,
                    StringComparison.OrdinalIgnoreCase
                )
            );
            if (table is null)
            {
                mismatchesList.Add($"缺少表 {tableKey}。");
                continue;
            }

            string relativePath = Path.GetRelativePath(
                sourceDataRoot,
                table.WorkbookPath
            );
            string stagedWorkbookPath = Path.Combine(
                outputDataRoot,
                relativePath
            );
            string? stagedDirectory = Path.GetDirectoryName(
                stagedWorkbookPath
            );
            if (!string.IsNullOrWhiteSpace(stagedDirectory))
            {
                Directory.CreateDirectory(stagedDirectory);
            }
            File.Copy(
                table.WorkbookPath,
                stagedWorkbookPath,
                overwrite: true
            );

            var records = table.Records.ToDictionary(item => item.Id);
            int[] createdIds = patch.Commands.Where(command => command.Kind == "CreateNode"
                && patch.FieldChanges.Any(change => change.LogicalAddress.TableKey == tableKey && change.Namespace == command.Target.Namespace && change.Id == command.Target.Id))
                .Select(command => command.Target.Id ?? throw new InvalidDataException("创建命令缺少 ID。"))
                .ToArray();
            foreach (int id in createdIds)
            {
                if (records.ContainsKey(id)) throw new InvalidDataException($"新增 ID {table.Key}:{id} 已存在。");
                records.Add(id, new GameDataRecord(id, new Dictionary<string, IReadOnlyList<string>>(),
                    records.Values.Select(record => record.SourceOrder).DefaultIfEmpty(0).Max() + 1));
            }
            foreach (
                IGrouping<int, WorkbookFieldChange> recordChanges in tableChanges
                    .GroupBy(item => item.Id)
                    .OrderBy(item => item.Key)
            )
            {
                if (!records.TryGetValue(recordChanges.Key, out GameDataRecord? record))
                {
                    mismatchesList.Add(
                        $"{table.Key}:{recordChanges.Key} 不存在。"
                    );
                    continue;
                }

                var fields = record.Fields.ToDictionary(
                    item => item.Key,
                    item => item.Value,
                    StringComparer.Ordinal
                );
                foreach (WorkbookFieldChange change in recordChanges)
                {
                    WorkbookPatchFieldAccessor.Apply(
                        fields,
                        change.Field,
                        WorkbookFieldChangeJson.RawText(change.After)
                    );
                }

                records[record.Id] = record with
                {
                    Fields = fields
                };
            }

            GameDataTable updated = table with
            {
                Records = records.Values
                    .OrderBy(item => item.SourceOrder)
                    .ThenBy(item => item.Id)
                    .ToArray()
            };
            catalog = catalog.ReplaceTable(updated);
            GameDataWorkbookCellUpdater.Apply(
                stagedWorkbookPath,
                updated,
                tableChanges
                    .Select(
                        change =>
                        {
                            (string BaseField, int IndexOffset) =
                                ParseChangeField(change.Field);
                            return new WorkbookPatchChange(
                                change.Id,
                                BaseField,
                                IndexOffset,
                                WorkbookFieldChangeJson.RawText(
                                    change.After
                                )
                            );
                        }
                    ),
                createdIds,
                WorkbookPatchImpact.RowCommands(patch).Where(command => command.Arguments["tableKey"]!.GetValue<string>() == tableKey).ToArray()
            );
            IReadOnlyList<string> packageErrors =
                WorkbookPackageIntegrityValidator.FindNewErrors(
                    table.WorkbookPath,
                    stagedWorkbookPath
                );
            if (packageErrors.Count > 0)
            {
                throw new InvalidDataException(
                    $"写入后的 {Path.GetFileName(stagedWorkbookPath)} 出现新的 Excel 包校验错误："
                        + string.Join("；", packageErrors)
                );
            }
            stagedTables.Add((relativePath, updated));
        }

        int verified = 0;
        foreach (WorkbookFieldChange change in patch.FieldChanges)
        {
            (string RelativePath, GameDataTable Table) staged =
                stagedTables.FirstOrDefault(
                    item => string.Equals(
                        item.Table.Key,
                        change.LogicalAddress.TableKey,
                        StringComparison.OrdinalIgnoreCase
                    )
                );
            if (staged.Table is null)
            {
                mismatchesList.Add(
                    $"临时副本缺少表 {change.LogicalAddress.TableKey}。"
                );
                continue;
            }

            GameDataTable rereadTable = workbookReader.Read(
                new GameDataTableSource(
                    staged.Table.Key,
                    staged.Table.DisplayName,
                    staged.Table.Category,
                    staged.RelativePath,
                    Path.Combine(outputDataRoot, staged.RelativePath)
                )
            );
            GameDataRecord? record = rereadTable.Records.FirstOrDefault(
                item => item.Id == change.Id
            );
            string actual = record is null
                ? ""
                : WorkbookPatchFieldAccessor.Read(
                    record.Fields,
                    change.Field
                );
            string expected = WorkbookFieldChangeJson.RawText(change.After);
            if (string.Equals(actual, expected, StringComparison.Ordinal))
            {
                verified++;
            }
            else
            {
                mismatchesList.Add(
                    $"{change.LogicalAddress.TableKey}.{change.Id}.{change.Field} expected={expected} actual={actual}"
                );
            }
        }

        foreach (string tableKey in WorkbookPatchImpact.TableKeys(patch))
        {
            var staged = stagedTables.Single(item => item.Table.Key == tableKey);
            GameDataTable reread = workbookReader.Read(new GameDataTableSource(staged.Table.Key, staged.Table.DisplayName,
                staged.Table.Category, staged.RelativePath, Path.Combine(outputDataRoot, staged.RelativePath)));
            IReadOnlyList<string> errors = WorkbookPatchImpact.VerifyRows(patch, tableKey,
                reread.Records.Select(record => (record.Id, record.SourceOrder)).ToArray());
            mismatchesList.AddRange(errors);
            if (errors.Count == 0) verified += WorkbookPatchImpact.RowCommands(patch).Count(command => command.Arguments["tableKey"]!.GetValue<string>() == tableKey);
        }

        writtenHash = SkillWorkspaceService.ComputeSourceTreeHash(
            outputDataRoot
        );
        verifiedFieldCount = verified;
        mismatches = mismatchesList;
    }

    private void PrunePatchRuns(string writeTestRoot)
    {
        if (options.WriteTestRetentionCount <= 0)
        {
            return;
        }

        string root = Path.GetFullPath(writeTestRoot);
        if (!Directory.Exists(root))
        {
            return;
        }

        string rootPrefix =
            root.EndsWith(Path.DirectorySeparatorChar)
                ? root
                : root + Path.DirectorySeparatorChar;
        foreach (string pattern in new[] { "workbook-patch-*", "write-smoke-*" })
        {
            foreach (
                string directory in Directory
                    .EnumerateDirectories(root, pattern)
                    .OrderByDescending(path => path, StringComparer.Ordinal)
                    .Skip(options.WriteTestRetentionCount)
            )
            {
                string fullPath = Path.GetFullPath(directory);
                if (
                    !fullPath.StartsWith(
                        rootPrefix,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                {
                    continue;
                }

                try
                {
                    Directory.Delete(fullPath, true);
                }
                catch (Exception exception)
                    when (exception is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(
                        exception,
                        "Failed to prune old patch run {Directory}.",
                        fullPath
                    );
                }
            }
        }
    }

    private static (string BaseField, int IndexOffset) ParseChangeField(
        string field
    )
    {
        if (WorkbookPatchFieldAccessor.TryGetIndex(field, out string baseField, out int index))
        {
            return (baseField, index);
        }

        return (field, 0);
    }

    private string ResolveContentRootPath(string path)
    {
        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, path));
    }
}
