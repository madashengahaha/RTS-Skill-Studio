using Microsoft.Data.Sqlite;
using RtsSkillStudio.Agent.Llm;
using RtsSkillStudio.Agent.Workspaces;
using System.Text.Json;

namespace RtsSkillStudio.Api.Workspaces;

public sealed class StudioConversationStore
{
    private readonly string _databasePath;
    private readonly ILogger<StudioConversationStore> _logger;
    private readonly SemaphoreSlim _initializeGate = new(1, 1);
    private bool _initialized;

    public StudioConversationStore(
        SkillWorkspaceOptions options,
        IHostEnvironment environment,
        ILogger<StudioConversationStore> logger
    )
    {
        _databasePath = Path.IsPathRooted(options.ConversationDatabasePath)
            ? Path.GetFullPath(options.ConversationDatabasePath)
            : Path.GetFullPath(
                Path.Combine(
                    environment.ContentRootPath,
                    options.ConversationDatabasePath
                )
            );
        _logger = logger;
    }

    public async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializeGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            string? directory = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using SqliteConnection connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode = WAL;
                PRAGMA foreign_keys = ON;

                CREATE TABLE IF NOT EXISTS conversations (
                    id TEXT PRIMARY KEY,
                    title TEXT NOT NULL,
                    selected_skill_id INTEGER NULL,
                    selected_asset_key TEXT NULL,
                    mentioned_assets_json TEXT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS messages (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    conversation_id TEXT NOT NULL,
                    role TEXT NOT NULL,
                    content TEXT NOT NULL,
                    provider TEXT NULL,
                    model TEXT NULL,
                    latency_ms INTEGER NULL,
                    created_at TEXT NOT NULL,
                    FOREIGN KEY (conversation_id) REFERENCES conversations(id)
                        ON DELETE CASCADE
                );

                CREATE INDEX IF NOT EXISTS ix_messages_conversation_id
                    ON messages(conversation_id, id);

                CREATE TABLE IF NOT EXISTS conversation_plans (
                    conversation_id TEXT PRIMARY KEY,
                    plan_json TEXT NOT NULL,
                    validation_json TEXT NOT NULL,
                    expected_plan INTEGER NOT NULL DEFAULT 0,
                    plan_disposition TEXT NOT NULL DEFAULT 'Expected',
                    updated_at TEXT NOT NULL,
                    FOREIGN KEY (conversation_id) REFERENCES conversations(id)
                        ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS plan_revisions (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    conversation_id TEXT NOT NULL,
                    plan_json TEXT NOT NULL,
                    validation_json TEXT NOT NULL,
                    expected_plan INTEGER NOT NULL,
                    plan_disposition TEXT NOT NULL,
                    created_at TEXT NOT NULL,
                    FOREIGN KEY (conversation_id) REFERENCES conversations(id)
                        ON DELETE CASCADE
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await EnsureColumnAsync(
                connection,
                "conversation_plans",
                "expected_plan",
                "INTEGER NOT NULL DEFAULT 0",
                cancellationToken
            );
            await EnsureColumnAsync(
                connection,
                "conversations",
                "selected_asset_key",
                "TEXT NULL",
                cancellationToken
            );
            await EnsureColumnAsync(
                connection,
                "conversations",
                "mentioned_assets_json",
                "TEXT NULL",
                cancellationToken
            );
            await using SqliteCommand migrateAssetKey = connection.CreateCommand();
            migrateAssetKey.CommandText = """
                UPDATE conversations
                SET selected_asset_key = 'TbSkill:' || selected_skill_id
                WHERE selected_asset_key IS NULL
                  AND selected_skill_id IS NOT NULL;
                """;
            await migrateAssetKey.ExecuteNonQueryAsync(cancellationToken);
            await EnsureColumnAsync(
                connection,
                "conversation_plans",
                "plan_disposition",
                "TEXT NOT NULL DEFAULT 'Expected'",
                cancellationToken
            );
            await SanitizeLegacyPlanMessagesAsync(
                connection,
                cancellationToken
            );
            _initialized = true;
            _logger.LogInformation(
                "Initialized Studio conversation database at {DatabasePath}.",
                _databasePath
            );
        }
        finally
        {
            _initializeGate.Release();
        }
    }

    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(
        int limit,
        CancellationToken cancellationToken
    )
    {
        if (limit is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                "会话列表数量必须在 1 到 10 之间。"
            );
        }

        await EnsureInitializedAsync(cancellationToken);
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                c.id,
                c.title,
                c.selected_skill_id,
                c.selected_asset_key,
                c.created_at,
                c.updated_at,
                COUNT(m.id)
            FROM conversations c
            LEFT JOIN messages m ON m.conversation_id = c.id
            GROUP BY c.id
            ORDER BY c.created_at DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<ConversationSummary>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            cancellationToken
        );
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(
                new ConversationSummary(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetInt32(2),
                    ParseAssetKey(
                        reader.IsDBNull(3) ? null : reader.GetString(3)
                    ),
                    DateTimeOffset.Parse(reader.GetString(4)),
                    DateTimeOffset.Parse(reader.GetString(5)),
                    reader.GetInt32(6)
                )
            );
        }

        return results;
    }

    public async Task<bool> DeleteAsync(
        string conversationId,
        CancellationToken cancellationToken
    )
    {
        await EnsureInitializedAsync(cancellationToken);
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken
            );

        foreach (string table in new[]
        {
            "messages",
            "conversation_plans",
            "plan_revisions"
        })
        {
            await using SqliteCommand cleanup = connection.CreateCommand();
            cleanup.Transaction = transaction;
            cleanup.CommandText =
                $"DELETE FROM {table} WHERE conversation_id = $conversationId;";
            cleanup.Parameters.AddWithValue(
                "$conversationId",
                conversationId
            );
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }

        await using SqliteCommand delete = connection.CreateCommand();
        delete.Transaction = transaction;
        delete.CommandText = """
            DELETE FROM conversations
            WHERE id = $conversationId;
            """;
        delete.Parameters.AddWithValue("$conversationId", conversationId);
        int affected = await delete.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return affected > 0;
    }

    public async Task<ConversationDetail> CreateAsync(
        string? title,
        CancellationToken cancellationToken
    )
    {
        await EnsureInitializedAsync(cancellationToken);
        string id = Guid.NewGuid().ToString("N");
        string now = DateTimeOffset.UtcNow.ToString("O");
        string resolvedTitle = string.IsNullOrWhiteSpace(title)
            ? "新会话"
            : title.Trim();

        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO conversations (id, title, created_at, updated_at)
            VALUES ($id, $title, $createdAt, $updatedAt);
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$title", resolvedTitle);
        command.Parameters.AddWithValue("$createdAt", now);
        command.Parameters.AddWithValue("$updatedAt", now);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return new ConversationDetail(
            id,
            resolvedTitle,
            null,
            null,
            [],
            DateTimeOffset.Parse(now),
            DateTimeOffset.Parse(now),
            [],
            null,
            [],
            false,
            "None"
        );
    }

    public async Task<ConversationDetail?> GetAsync(
        string id,
        CancellationToken cancellationToken
    )
    {
        await EnsureInitializedAsync(cancellationToken);
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                title,
                selected_skill_id,
                selected_asset_key,
                mentioned_assets_json,
                created_at,
                updated_at
            FROM conversations
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", id);

        string? title;
        int? selectedSkillId;
        StudioAssetRef? selectedAsset;
        IReadOnlyList<StudioAssetRef> mentionedAssets;
        DateTimeOffset createdAt;
        DateTimeOffset updatedAt;
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(
            cancellationToken
        ))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            title = reader.GetString(1);
            selectedSkillId = reader.IsDBNull(2)
                ? null
                : reader.GetInt32(2);
            selectedAsset = ParseAssetKey(
                reader.IsDBNull(3) ? null : reader.GetString(3)
            );
            mentionedAssets = reader.IsDBNull(4)
                ? []
                : JsonSerializer.Deserialize<StudioAssetRef[]>(
                    reader.GetString(4)
                ) ?? [];
            createdAt = DateTimeOffset.Parse(reader.GetString(5));
            updatedAt = DateTimeOffset.Parse(reader.GetString(6));
        }

        var messages = new List<ConversationMessage>();
        await using SqliteCommand messageCommand = connection.CreateCommand();
        messageCommand.CommandText = """
            SELECT id, role, content, provider, model, latency_ms, created_at
            FROM messages
            WHERE conversation_id = $conversationId
            ORDER BY id;
            """;
        messageCommand.Parameters.AddWithValue("$conversationId", id);
        await using SqliteDataReader messageReader =
            await messageCommand.ExecuteReaderAsync(cancellationToken);
        while (await messageReader.ReadAsync(cancellationToken))
        {
            messages.Add(
                new ConversationMessage(
                    messageReader.GetInt64(0),
                    messageReader.GetString(1),
                    messageReader.GetString(2),
                    messageReader.IsDBNull(3)
                        ? null
                        : messageReader.GetString(3),
                    messageReader.IsDBNull(4)
                        ? null
                        : messageReader.GetString(4),
                    messageReader.IsDBNull(5)
                        ? null
                        : messageReader.GetInt64(5),
                    DateTimeOffset.Parse(messageReader.GetString(6))
                )
            );
        }

        string? planJson = null;
        IReadOnlyList<string> planErrors = [];
        bool expectedPlan = false;
        string planDisposition = "None";
        await using SqliteCommand planCommand = connection.CreateCommand();
        planCommand.CommandText = """
            SELECT
                plan_json,
                validation_json,
                expected_plan,
                plan_disposition
            FROM conversation_plans
            WHERE conversation_id = $conversationId;
            """;
        planCommand.Parameters.AddWithValue("$conversationId", id);
        await using SqliteDataReader planReader =
            await planCommand.ExecuteReaderAsync(cancellationToken);
        if (await planReader.ReadAsync(cancellationToken))
        {
            planJson = planReader.GetString(0);
            planErrors = JsonSerializer.Deserialize<string[]>(
                planReader.GetString(1)
            ) ?? [];
            expectedPlan = planReader.GetInt32(2) != 0;
            planDisposition = planReader.GetString(3);
        }

        return new ConversationDetail(
            id,
            title,
            selectedSkillId,
            selectedAsset,
            mentionedAssets,
            createdAt,
            updatedAt,
            messages,
            planJson,
            planErrors,
            expectedPlan,
            planDisposition
        );
    }

    public async Task UpdateSelectedSkillAsync(
        string conversationId,
        int? skillId,
        CancellationToken cancellationToken
    )
    {
        await UpdateSelectedAssetAsync(
            conversationId,
            skillId is null
                ? null
                : new StudioAssetRef("TbSkill", skillId.Value),
            cancellationToken
        );
    }

    public async Task UpdateSelectedAssetAsync(
        string conversationId,
        StudioAssetRef? asset,
        CancellationToken cancellationToken
    )
    {
        await EnsureInitializedAsync(cancellationToken);
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE conversations
            SET selected_skill_id = $skillId,
                selected_asset_key = $assetKey,
                updated_at = $updatedAt
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue(
            "$skillId",
            asset is not null
            && string.Equals(
                asset.Namespace,
                "TbSkill",
                StringComparison.OrdinalIgnoreCase
            )
                ? asset.Id
                : DBNull.Value
        );
        command.Parameters.AddWithValue(
            "$assetKey",
            asset is null ? DBNull.Value : $"{asset.Namespace}:{asset.Id}"
        );
        command.Parameters.AddWithValue(
            "$updatedAt",
            DateTimeOffset.UtcNow.ToString("O")
        );
        command.Parameters.AddWithValue("$id", conversationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpdateMentionedAssetsAsync(
        string conversationId,
        IReadOnlyList<StudioAssetRef> assets,
        CancellationToken cancellationToken
    )
    {
        await EnsureInitializedAsync(cancellationToken);
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE conversations
            SET mentioned_assets_json = $mentionedAssets,
                updated_at = $updatedAt
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue(
            "$mentionedAssets",
            JsonSerializer.Serialize(assets)
        );
        command.Parameters.AddWithValue(
            "$updatedAt",
            DateTimeOffset.UtcNow.ToString("O")
        );
        command.Parameters.AddWithValue("$id", conversationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static StudioAssetRef? ParseAssetKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        int separator = key.LastIndexOf(':');
        return separator > 0
            && int.TryParse(key[(separator + 1)..], out int id)
            ? new StudioAssetRef(key[..separator], id)
            : null;
    }

    public async Task AppendMessageAsync(
        string conversationId,
        string role,
        string content,
        string? provider,
        string? model,
        long? latencyMs,
        CancellationToken cancellationToken
    )
    {
        await EnsureInitializedAsync(cancellationToken);
        string now = DateTimeOffset.UtcNow.ToString("O");

        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken
            );

        await using SqliteCommand insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO messages (
                conversation_id,
                role,
                content,
                provider,
                model,
                latency_ms,
                created_at
            )
            VALUES (
                $conversationId,
                $role,
                $content,
                $provider,
                $model,
                $latencyMs,
                $createdAt
            );
            """;
        insert.Parameters.AddWithValue("$conversationId", conversationId);
        insert.Parameters.AddWithValue("$role", role);
        insert.Parameters.AddWithValue("$content", content);
        insert.Parameters.AddWithValue(
            "$provider",
            provider is null ? DBNull.Value : provider
        );
        insert.Parameters.AddWithValue(
            "$model",
            model is null ? DBNull.Value : model
        );
        insert.Parameters.AddWithValue(
            "$latencyMs",
            latencyMs is null ? DBNull.Value : latencyMs.Value
        );
        insert.Parameters.AddWithValue("$createdAt", now);
        await insert.ExecuteNonQueryAsync(cancellationToken);

        await using SqliteCommand update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE conversations
            SET
                title = CASE
                    WHEN title = '新会话' AND $role = 'user'
                        THEN substr($content, 1, 36)
                    ELSE title
                END,
                updated_at = $updatedAt
            WHERE id = $conversationId;
            """;
        update.Parameters.AddWithValue("$role", role);
        update.Parameters.AddWithValue("$content", content);
        update.Parameters.AddWithValue("$updatedAt", now);
        update.Parameters.AddWithValue("$conversationId", conversationId);
        await update.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task SavePlanAsync(
        string conversationId,
        string planJson,
        IReadOnlyList<string> errors,
        bool expectedPlan,
        string planDisposition,
        CancellationToken cancellationToken
    )
    {
        await EnsureInitializedAsync(cancellationToken);
        string now = DateTimeOffset.UtcNow.ToString("O");
        string validationJson = JsonSerializer.Serialize(errors);
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken
            );

        await using SqliteCommand revision = connection.CreateCommand();
        revision.Transaction = transaction;
        revision.CommandText = """
            INSERT INTO plan_revisions (
                conversation_id,
                plan_json,
                validation_json,
                expected_plan,
                plan_disposition,
                created_at
            )
            VALUES (
                $conversationId,
                $planJson,
                $validationJson,
                $expectedPlan,
                $planDisposition,
                $createdAt
            );
            """;
        revision.Parameters.AddWithValue("$conversationId", conversationId);
        revision.Parameters.AddWithValue("$planJson", planJson);
        revision.Parameters.AddWithValue("$validationJson", validationJson);
        revision.Parameters.AddWithValue("$expectedPlan", expectedPlan ? 1 : 0);
        revision.Parameters.AddWithValue(
            "$planDisposition",
            planDisposition
        );
        revision.Parameters.AddWithValue("$createdAt", now);
        await revision.ExecuteNonQueryAsync(cancellationToken);

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO conversation_plans (
                conversation_id,
                plan_json,
                validation_json,
                expected_plan,
                plan_disposition,
                updated_at
            )
            VALUES (
                $conversationId,
                $planJson,
                $validationJson,
                $expectedPlan,
                $planDisposition,
                $createdAt
            )
            ON CONFLICT(conversation_id) DO UPDATE SET
                plan_json = excluded.plan_json,
                validation_json = excluded.validation_json,
                expected_plan = excluded.expected_plan,
                plan_disposition = excluded.plan_disposition,
                updated_at = excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);
        command.Parameters.AddWithValue("$planJson", planJson);
        command.Parameters.AddWithValue("$validationJson", validationJson);
        command.Parameters.AddWithValue("$expectedPlan", expectedPlan ? 1 : 0);
        command.Parameters.AddWithValue(
            "$planDisposition",
            planDisposition
        );
        command.Parameters.AddWithValue("$createdAt", now);
        await command.ExecuteNonQueryAsync(cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ConversationPlanRevision>> ListPlanRevisionsAsync(
        string conversationId,
        CancellationToken cancellationToken
    )
    {
        await EnsureInitializedAsync(cancellationToken);
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                plan_json,
                validation_json,
                expected_plan,
                plan_disposition,
                created_at
            FROM plan_revisions
            WHERE conversation_id = $conversationId
            ORDER BY id DESC;
            """;
        command.Parameters.AddWithValue("$conversationId", conversationId);

        var revisions = new List<ConversationPlanRevision>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(
            cancellationToken
        );
        while (await reader.ReadAsync(cancellationToken))
        {
            revisions.Add(
                new ConversationPlanRevision(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    JsonSerializer.Deserialize<string[]>(
                        reader.GetString(2)
                    ) ?? [],
                    reader.GetInt32(3) != 0,
                    reader.GetString(4),
                    DateTimeOffset.Parse(reader.GetString(5))
                )
            );
        }

        return revisions;
    }

    private SqliteConnection CreateConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            ForeignKeys = true
        };
        return new SqliteConnection(builder.ToString());
    }

    private static async Task EnsureColumnAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string definition,
        CancellationToken cancellationToken
    )
    {
        await using SqliteCommand info = connection.CreateCommand();
        info.CommandText = $"PRAGMA table_info({tableName});";
        await using SqliteDataReader reader = await info.ExecuteReaderAsync(
            cancellationToken
        );
        while (await reader.ReadAsync(cancellationToken))
        {
            if (
                string.Equals(
                    reader.GetString(1),
                    columnName,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return;
            }
        }

        await reader.DisposeAsync();
        await using SqliteCommand alter = connection.CreateCommand();
        alter.CommandText =
            $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};";
        await alter.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task SanitizeLegacyPlanMessagesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken
    )
    {
        var updates = new List<(long Id, string Content)>();
        await using SqliteCommand select = connection.CreateCommand();
        select.CommandText = """
            SELECT id, content
            FROM messages
            WHERE role = 'assistant'
              AND instr(content, '```json') > 0;
            """;
        await using (SqliteDataReader reader = await select.ExecuteReaderAsync(
            cancellationToken
        ))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                updates.Add(
                    (
                        reader.GetInt64(0),
                        SkillConfigPlanParser.RemovePlanBlock(reader.GetString(1))
                    )
                );
            }
        }

        foreach ((long id, string content) in updates)
        {
            await using SqliteCommand update = connection.CreateCommand();
            update.CommandText = """
                UPDATE messages
                SET content = $content
                WHERE id = $id;
                """;
            update.Parameters.AddWithValue("$content", content);
            update.Parameters.AddWithValue("$id", id);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}

public sealed record ConversationSummary(
    string Id,
    string Title,
    int? SelectedSkillId,
    StudioAssetRef? SelectedAsset,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int MessageCount
);

public sealed record ConversationDetail(
    string Id,
    string Title,
    int? SelectedSkillId,
    StudioAssetRef? SelectedAsset,
    IReadOnlyList<StudioAssetRef> MentionedAssets,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ConversationMessage> Messages,
    string? PlanJson,
    IReadOnlyList<string> PlanErrors,
    bool ExpectedPlan,
    string PlanDisposition
);

public sealed record ConversationMessage(
    long Id,
    string Role,
    string Content,
    string? Provider,
    string? Model,
    long? LatencyMs,
    DateTimeOffset CreatedAt
);

public sealed record CreateConversationRequest(string? Title);

public sealed record UpdateConversationSkillRequest(int? SkillId);

public sealed record UpdateConversationAssetRequest(
    string? AssetNamespace,
    int? AssetId,
    int? SkillId = null
);

public sealed record ConversationChatRequest(
    string? Provider,
    string Message,
    string? Model = null,
    int? SkillId = null,
    string? ReasoningEffort = null,
    string? AssetNamespace = null,
    int? AssetId = null
);

public sealed record ConversationChatResponse(
    string ConversationId,
    int? SelectedSkillId,
    StudioAssetRef? SelectedAsset,
    string Provider,
    string Model,
    string Text,
    long LatencyMs,
    string? PlanJson,
    IReadOnlyList<string> PlanErrors,
    bool ExpectedPlan,
    string PlanDisposition,
    IReadOnlyList<StudioAssetRef> MentionedAssets
);

public sealed record ConversationPlanRevision(
    long Id,
    string PlanJson,
    IReadOnlyList<string> Errors,
    bool ExpectedPlan,
    string PlanDisposition,
    DateTimeOffset CreatedAt
);
