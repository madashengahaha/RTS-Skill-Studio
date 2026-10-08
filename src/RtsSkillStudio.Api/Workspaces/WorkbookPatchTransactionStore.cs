using System.Text.Json;

namespace RtsSkillStudio.Api.Workspaces;

public sealed record WorkbookPatchTransactionFile(
    string SourcePath,
    string BackupPath,
    bool Replaced
);

public sealed record WorkbookPatchTransaction(
    string TransactionId,
    string PatchId,
    string Status,
    string SourceRoot,
    string SourceHashBefore,
    string? SourceHashAfter,
    IReadOnlyList<WorkbookPatchTransactionFile> Files,
    IReadOnlyList<string> AppliedFields,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Message
);

public sealed class WorkbookPatchTransactionStore(
    SkillWorkspaceOptions options,
    IHostEnvironment environment,
    ILogger<WorkbookPatchTransactionStore> logger
)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public async Task SaveAsync(
        WorkbookPatchTransaction transaction,
        CancellationToken cancellationToken
    )
    {
        string root = TransactionRoot();
        Directory.CreateDirectory(root);
        string path = Path.Combine(
            root,
            $"{transaction.TransactionId}.json"
        );
        string temporary = path + ".tmp";
        string json = JsonSerializer.Serialize(transaction, JsonOptions);
        await File.WriteAllTextAsync(temporary, json, cancellationToken);
        File.Move(temporary, path, overwrite: true);
        logger.LogInformation(
            "Saved WorkbookPatch transaction {TransactionId} with status {Status}.",
            transaction.TransactionId,
            transaction.Status
        );
    }

    public async Task<WorkbookPatchTransaction?> GetAsync(
        string transactionId,
        CancellationToken cancellationToken
    )
    {
        string path = Path.Combine(
            TransactionRoot(),
            $"{transactionId}.json"
        );
        if (!File.Exists(path))
        {
            return null;
        }

        string json = await File.ReadAllTextAsync(path, cancellationToken);
        return JsonSerializer.Deserialize<WorkbookPatchTransaction>(
            json,
            JsonOptions
        );
    }

    public string TransactionRoot()
    {
        return Path.IsPathRooted(options.TransactionRoot)
            ? Path.GetFullPath(options.TransactionRoot)
            : Path.GetFullPath(
                Path.Combine(
                    environment.ContentRootPath,
                    options.TransactionRoot
                )
            );
    }
}
