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
    IGameDataWorkbookWriter workbookWriter,
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
                    SkillWorkspaceService.CopyTree(
                        sourceDataRoot,
                        outputDataRoot
                    );
                    string copiedHash =
                        SkillWorkspaceService.ComputeSourceTreeHash(
                            outputDataRoot
                        );
                    ApplyAndVerify(
                        patch,
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
                        && verifiedFieldCount == patch.FieldChanges.Count;
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
        string outputDataRoot,
        out string writtenHash,
        out int verifiedFieldCount,
        out IReadOnlyList<string> mismatches
    )
    {
        GameDataCatalog catalog = catalogReader.Read(outputDataRoot);
        var mismatchesList = new List<string>();
        foreach (
            IGrouping<string, WorkbookFieldChange> tableChanges in patch
                .FieldChanges.GroupBy(
                    item => item.LogicalAddress.TableKey,
                    StringComparer.OrdinalIgnoreCase
                )
                .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
        )
        {
            GameDataTable? table = catalog.Tables.FirstOrDefault(
                item => string.Equals(
                    item.Key,
                    tableChanges.Key,
                    StringComparison.OrdinalIgnoreCase
                )
            );
            if (table is null)
            {
                mismatchesList.Add($"缺少表 {tableChanges.Key}。");
                continue;
            }

            var records = table.Records.ToDictionary(item => item.Id);
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
                    fields[change.Field] =
                    [
                        WorkbookFieldChangeJson.RawText(change.After)
                    ];
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
            workbookWriter.Write(updated.WorkbookPath, updated);
        }

        GameDataCatalog rereadCatalog = catalogReader.Read(outputDataRoot);
        int verified = 0;
        foreach (WorkbookFieldChange change in patch.FieldChanges)
        {
            GameDataTable? table = rereadCatalog.Tables.FirstOrDefault(
                item => string.Equals(
                    item.Key,
                    change.LogicalAddress.TableKey,
                    StringComparison.OrdinalIgnoreCase
                )
            );
            GameDataRecord? record = table?.Records.FirstOrDefault(
                item => item.Id == change.Id
            );
            string actual = record?.Fields.TryGetValue(
                change.Field,
                out IReadOnlyList<string>? values
            ) == true
                ? values.FirstOrDefault() ?? ""
                : "";
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

    private string ResolveContentRootPath(string path)
    {
        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(environment.ContentRootPath, path));
    }
}
