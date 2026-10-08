using RtsSkillStudio.Agent.Patch;
using TianshuDM.Application.GameData;
using TianshuDM.Domain.GameData;

namespace RtsSkillStudio.Api.Workspaces;

public sealed record FinalWorkbookPatchApplyResult(
    string Status,
    string PatchId,
    string SourceRoot,
    string SourceHashBefore,
    string SourceHashAfter,
    int AppliedFieldCount,
    IReadOnlyList<string> Mismatches,
    string Message,
    string? TransactionId = null,
    bool UndoAvailable = false
);

public sealed class FinalWorkbookPatchApplyService(
    SkillWorkspaceOptions options,
    TemporaryWorkbookPatchApplyService temporaryApplyService,
    IGameDataCatalogReader catalogReader,
    WorkbookPatchTransactionStore transactionStore,
    ILogger<FinalWorkbookPatchApplyService> logger
)
{
    public async Task<FinalWorkbookPatchApplyResult> ApplyAsync(
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
            return Blocked(
                patch,
                "",
                "",
                "校验未通过，未写入正式工作区。"
            );
        }

        string sourceRoot = Path.GetFullPath(options.ExcelDataRoot);
        string sourceHashBefore =
            SkillWorkspaceService.ComputeSourceTreeHash(sourceRoot);
        if (
            !string.Equals(
                sourceHashBefore,
                patch.Base.SourceHash,
                StringComparison.Ordinal
            )
        )
        {
            return Blocked(
                patch,
                sourceRoot,
                sourceHashBefore,
                "当前源工作区哈希与 Patch 基线不一致，请重新生成修改方案。"
            );
        }

        string transactionId = Guid.NewGuid().ToString("N");
        string transactionRoot = Path.Combine(
            transactionStore.TransactionRoot(),
            transactionId
        );
        IReadOnlyCollection<string> tableKeys = patch.FieldChanges
            .Select(change => change.LogicalAddress.TableKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        GameDataCatalog catalog;
        IReadOnlyList<FileReplacement> replacements;
        try
        {
            catalog = catalogReader.Read(sourceRoot, tableKeys);
            replacements = BuildReplacements(
                patch,
                catalog,
                sourceRoot,
                transactionRoot
            );
        }
        catch (Exception exception) when (
            exception is InvalidDataException
                or FileNotFoundException
                or KeyNotFoundException
        )
        {
            return new FinalWorkbookPatchApplyResult(
                "Failed",
                patch.PatchId,
                sourceRoot,
                sourceHashBefore,
                sourceHashBefore,
                0,
                [exception.Message],
                exception.Message
            );
        }
        if (replacements.Count == 0)
        {
            return new FinalWorkbookPatchApplyResult(
                "Failed",
                patch.PatchId,
                sourceRoot,
                sourceHashBefore,
                sourceHashBefore,
                0,
                [],
                "没有解析到需要写入的工作簿。"
            );
        }

        IReadOnlyList<string> lockedBefore = WorkbookFileLockChecker
            .FindLockedFiles(
                replacements.Select(replacement => replacement.SourcePath)
            );
        if (lockedBefore.Count > 0)
        {
            return Blocked(
                patch,
                sourceRoot,
                sourceHashBefore,
                BuildLockedFileMessage(lockedBefore)
            );
        }

        TemporaryWorkbookPatchApplyResult staged;
        try
        {
            staged = await temporaryApplyService.ApplyAsync(
                patchJson,
                validation,
                cancellationToken
            );
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or KeyNotFoundException
        )
        {
            logger.LogError(
                exception,
                "Temporary WorkbookPatch apply failed for {PatchId}.",
                patch.PatchId
            );
            IReadOnlyList<string> lockedAtFailure =
                WorkbookFileLockChecker.FindLockedFiles(
                    replacements.Select(
                        replacement => replacement.SourcePath
                    )
                );
            string message = lockedAtFailure.Count > 0
                ? BuildLockedFileMessage(lockedAtFailure)
                : $"临时副本验证失败：{DescribeWriteFailure(exception)}";
            return new FinalWorkbookPatchApplyResult(
                lockedAtFailure.Count > 0 ? "Blocked" : "Failed",
                patch.PatchId,
                sourceRoot,
                sourceHashBefore,
                sourceHashBefore,
                0,
                [message],
                message,
                null,
                false
            );
        }
        if (staged.Status != "Verified")
        {
            return new FinalWorkbookPatchApplyResult(
                "Failed",
                patch.PatchId,
                sourceRoot,
                sourceHashBefore,
                sourceHashBefore,
                0,
                staged.Mismatches,
                staged.Mismatches.Count > 0
                    ? "临时副本验证未通过，正式工作区未修改："
                        + string.Join("；", staged.Mismatches.Take(3))
                    : "临时副本验证未通过，正式工作区未修改。"
            );
        }

        string stagedRoot = Path.Combine(
            staged.OutputRoot,
            "Unity",
            "Assets",
            "Config",
            "Excel",
            "Datas"
        );
        replacements = replacements
            .Select(
                replacement => replacement with
                {
                    StagedPath = Path.Combine(
                        stagedRoot,
                        Path.GetRelativePath(
                            sourceRoot,
                            replacement.SourcePath
                        )
                    )
                }
            )
            .ToArray();
        IReadOnlyList<string> missingStaged = replacements
            .Where(replacement => !File.Exists(replacement.StagedPath))
            .Select(replacement => replacement.StagedPath)
            .ToArray();
        if (missingStaged.Count > 0)
        {
            return new FinalWorkbookPatchApplyResult(
                "Failed",
                patch.PatchId,
                sourceRoot,
                sourceHashBefore,
                sourceHashBefore,
                0,
                missingStaged,
                "临时工作区缺少需要提交的工作簿。"
            );
        }

        IReadOnlyList<string> lockedBeforeCommit = WorkbookFileLockChecker
            .FindLockedFiles(
                replacements.Select(replacement => replacement.SourcePath)
            );
        if (lockedBeforeCommit.Count > 0)
        {
            return Blocked(
                patch,
                sourceRoot,
                sourceHashBefore,
                BuildLockedFileMessage(lockedBeforeCommit)
            );
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var transaction = new WorkbookPatchTransaction(
            transactionId,
            patch.PatchId,
            "Prepared",
            sourceRoot,
            sourceHashBefore,
            null,
            replacements
                .Select(
                    replacement => new WorkbookPatchTransactionFile(
                        replacement.SourcePath,
                        replacement.BackupPath,
                        false
                    )
                )
                .ToArray(),
            patch.FieldChanges
                .Select(
                    change =>
                        $"{change.LogicalAddress.TableKey}.{change.Id}.{change.Field}"
                )
                .ToArray(),
            now,
            now,
            "事务已准备。"
        );
        await transactionStore.SaveAsync(transaction, cancellationToken);

        var completed = new List<FileReplacement>();
        try
        {
            await transactionStore.SaveAsync(
                transaction with
                {
                    Status = "Committing",
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Message = "正在提交工作簿。"
                },
                cancellationToken
            );
            foreach (FileReplacement replacement in replacements)
            {
                Directory.CreateDirectory(
                    Path.GetDirectoryName(replacement.BackupPath)!
                );
                File.Copy(
                    replacement.SourcePath,
                    replacement.BackupPath,
                    overwrite: true
                );
                File.Copy(
                    replacement.StagedPath,
                    replacement.SourcePath,
                    overwrite: true
                );
                completed.Add(replacement);
            }

            VerificationResult verification = Verify(
                patch,
                catalogReader.Read(sourceRoot, tableKeys)
            );
            string sourceHashAfter =
                SkillWorkspaceService.ComputeSourceTreeHash(sourceRoot);
            if (
                verification.Mismatches.Count > 0
                || verification.VerifiedFieldCount != patch.FieldChanges.Count
            )
            {
                Rollback(completed);
                string rolledBackHash =
                    SkillWorkspaceService.ComputeSourceTreeHash(sourceRoot);
                await transactionStore.SaveAsync(
                    transaction with
                    {
                        Status = "RolledBack",
                        SourceHashAfter = rolledBackHash,
                        Files = replacements
                            .Select(
                                replacement =>
                                    new WorkbookPatchTransactionFile(
                                        replacement.SourcePath,
                                        replacement.BackupPath,
                                        completed.Contains(replacement)
                                    )
                            )
                            .ToArray(),
                        UpdatedAt = DateTimeOffset.UtcNow,
                        Message = "正式工作区重读验证失败，已回滚。"
                    },
                    cancellationToken
                );
                return new FinalWorkbookPatchApplyResult(
                    "Failed",
                    patch.PatchId,
                    sourceRoot,
                    sourceHashBefore,
                    rolledBackHash,
                    verification.VerifiedFieldCount,
                    verification.Mismatches,
                    "正式工作区重读验证失败，已回滚写入。",
                    transactionId,
                    false
                );
            }

            logger.LogInformation(
                "Final WorkbookPatch apply succeeded for {PatchId}.",
                patch.PatchId
            );
            await transactionStore.SaveAsync(
                transaction with
                {
                    Status = "Committed",
                    SourceHashAfter = sourceHashAfter,
                    Files = replacements
                        .Select(
                            replacement =>
                                new WorkbookPatchTransactionFile(
                                    replacement.SourcePath,
                                    replacement.BackupPath,
                                    true
                                )
                        )
                        .ToArray(),
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Message = "正式工作区写入并重读验证通过。"
                },
                cancellationToken
            );
            return new FinalWorkbookPatchApplyResult(
                "Applied",
                patch.PatchId,
                sourceRoot,
                sourceHashBefore,
                sourceHashAfter,
                verification.VerifiedFieldCount,
                [],
                "正式工作区写入并重读验证通过。",
                transactionId,
                true
            );
        }
        catch (Exception exception)
        {
            Rollback(completed);
            string rolledBackHash =
                SkillWorkspaceService.ComputeSourceTreeHash(sourceRoot);
            logger.LogError(
                exception,
                "Final WorkbookPatch apply failed for {PatchId}.",
                patch.PatchId
            );
            await transactionStore.SaveAsync(
                transaction with
                {
                    Status = "RolledBack",
                    SourceHashAfter = rolledBackHash,
                    Files = replacements
                        .Select(
                            replacement =>
                                new WorkbookPatchTransactionFile(
                                    replacement.SourcePath,
                                    replacement.BackupPath,
                                    completed.Contains(replacement)
                                )
                        )
                        .ToArray(),
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Message = "正式工作区写入失败，已尝试回滚。"
                },
                cancellationToken
            );
            return new FinalWorkbookPatchApplyResult(
                "Failed",
                patch.PatchId,
                sourceRoot,
                sourceHashBefore,
                rolledBackHash,
                completed.Count,
                [DescribeWriteFailure(exception)],
                "正式工作区写入失败，已尝试回滚。",
                transactionId,
                false
            );
        }
    }

    public async Task<FinalWorkbookPatchApplyResult> UndoAsync(
        string transactionId,
        CancellationToken cancellationToken
    )
    {
        WorkbookPatchTransaction? transaction =
            await transactionStore.GetAsync(
                transactionId,
                cancellationToken
            );
        if (transaction is null)
        {
            return new FinalWorkbookPatchApplyResult(
                "NotFound",
                "",
                "",
                "",
                "",
                0,
                [],
                $"事务 {transactionId} 不存在。",
                transactionId,
                false
            );
        }
        if (transaction.Status != "Committed")
        {
            return new FinalWorkbookPatchApplyResult(
                "Blocked",
                transaction.PatchId,
                transaction.SourceRoot,
                transaction.SourceHashBefore,
                transaction.SourceHashAfter ?? "",
                0,
                [],
                $"事务 {transactionId} 当前状态为 {transaction.Status}，不能撤销。",
                transactionId,
                false
            );
        }

        IReadOnlyList<WorkbookPatchTransactionFile> replaced =
            transaction.Files.Where(file => file.Replaced).ToArray();
        IReadOnlyList<string> locked = WorkbookFileLockChecker.FindLockedFiles(
            replaced.Select(file => file.SourcePath)
        );
        if (locked.Count > 0)
        {
            return new FinalWorkbookPatchApplyResult(
                "Blocked",
                transaction.PatchId,
                transaction.SourceRoot,
                transaction.SourceHashBefore,
                transaction.SourceHashAfter ?? "",
                0,
                locked,
                BuildLockedFileMessage(locked),
                transactionId,
                false
            );
        }

        try
        {
            foreach (
                WorkbookPatchTransactionFile file in replaced.Reverse()
            )
            {
                if (!File.Exists(file.BackupPath))
                {
                    throw new FileNotFoundException(
                        $"事务备份不存在：{file.BackupPath}",
                        file.BackupPath
                    );
                }

                File.Copy(
                    file.BackupPath,
                    file.SourcePath,
                    overwrite: true
                );
            }

            string sourceHashAfter =
                SkillWorkspaceService.ComputeSourceTreeHash(
                    transaction.SourceRoot
                );
            await transactionStore.SaveAsync(
                transaction with
                {
                    Status = "Undone",
                    SourceHashAfter = sourceHashAfter,
                    UpdatedAt = DateTimeOffset.UtcNow,
                    Message = "已撤销正式工作区写入。"
                },
                cancellationToken
            );
            return new FinalWorkbookPatchApplyResult(
                "Undone",
                transaction.PatchId,
                transaction.SourceRoot,
                transaction.SourceHashBefore,
                sourceHashAfter,
                transaction.AppliedFields.Count,
                [],
                "已撤销正式工作区写入。",
                transactionId,
                false
            );
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Failed to undo WorkbookPatch transaction {TransactionId}.",
                transactionId
            );
            return new FinalWorkbookPatchApplyResult(
                "Failed",
                transaction.PatchId,
                transaction.SourceRoot,
                transaction.SourceHashBefore,
                SkillWorkspaceService.ComputeSourceTreeHash(
                    transaction.SourceRoot
                ),
                0,
                [DescribeWriteFailure(exception)],
                "撤销写入失败。",
                transactionId,
                true
            );
        }
    }

    private static IReadOnlyList<FileReplacement> BuildReplacements(
        WorkbookPatchDocument patch,
        GameDataCatalog catalog,
        string sourceRoot,
        string transactionRoot
    )
    {
        var replacements = new List<FileReplacement>();
        foreach (
            string tableKey in patch.FieldChanges
                .Select(change => change.LogicalAddress.TableKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
        )
        {
            GameDataTable? table = catalog.Tables.FirstOrDefault(
                candidate => string.Equals(
                    candidate.Key,
                    tableKey,
                    StringComparison.OrdinalIgnoreCase
                )
            );
            if (table is null)
            {
                throw new InvalidDataException(
                    $"正式工作区不存在表 {tableKey}。"
                );
            }

            string relativePath = Path.GetRelativePath(
                sourceRoot,
                table.WorkbookPath
            );
            replacements.Add(
                new FileReplacement(
                    SourcePath: table.WorkbookPath,
                    StagedPath: "",
                    BackupPath: Path.Combine(
                        transactionRoot,
                        "backups",
                        relativePath + ".bak"
                    )
                )
            );
        }

        return replacements;
    }

    private static VerificationResult Verify(
        WorkbookPatchDocument patch,
        GameDataCatalog catalog
    )
    {
        var mismatches = new List<string>();
        int verified = 0;
        foreach (WorkbookFieldChange change in patch.FieldChanges)
        {
            GameDataTable? table = catalog.Tables.FirstOrDefault(
                item => string.Equals(
                    item.Key,
                    change.LogicalAddress.TableKey,
                    StringComparison.OrdinalIgnoreCase
                )
            );
            GameDataRecord? record = table?.Records.FirstOrDefault(
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
                mismatches.Add(
                    $"{change.LogicalAddress.TableKey}.{change.Id}.{change.Field} expected={expected} actual={actual}"
                );
            }
        }

        return new VerificationResult(verified, mismatches);
    }

    private static void Rollback(
        IReadOnlyList<FileReplacement> completed
    )
    {
        foreach (FileReplacement replacement in completed.Reverse())
        {
            if (File.Exists(replacement.BackupPath))
            {
                File.Copy(
                    replacement.BackupPath,
                    replacement.SourcePath,
                    overwrite: true
                );
            }
        }
    }

    private static FinalWorkbookPatchApplyResult Blocked(
        WorkbookPatchDocument patch,
        string sourceRoot,
        string sourceHash,
        string message
    )
    {
        return new FinalWorkbookPatchApplyResult(
            "Blocked",
            patch.PatchId,
            sourceRoot,
            sourceHash,
            sourceHash,
            0,
            [],
            message
        );
    }

    private string BuildLockedFileMessage(
        IReadOnlyList<string> lockedFiles
    )
    {
        logger.LogWarning(
            "Workbook apply is blocked by locked files: {LockedFiles}",
            string.Join(", ", lockedFiles)
        );
        return "以下被修改的 Excel 文件正在被占用，请先关闭对应 Excel 后重试："
            + string.Join(
                "、",
                lockedFiles.Select(Path.GetFileName)
            );
    }

    private static string DescribeWriteFailure(Exception exception)
    {
        return exception switch
        {
            InvalidDataException => exception.Message,
            KeyNotFoundException => exception.Message,
            UnauthorizedAccessException =>
                $"没有权限访问文件：{exception.Message}",
            IOException => exception.Message,
            _ => exception.Message
        };
    }

    private sealed record FileReplacement(
        string SourcePath,
        string StagedPath,
        string BackupPath
    );

    private sealed record VerificationResult(
        int VerifiedFieldCount,
        IReadOnlyList<string> Mismatches
    );
}
