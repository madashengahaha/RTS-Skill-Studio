using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TianshuDM.Application.GameData;
using TianshuDM.Application.HeroAuthoring;
using TianshuDM.Application.Quests;
using TianshuDM.Application.Settings;
using TianshuDM.Application.Workspaces;
using TianshuDM.Domain.GameData;
using TianshuDM.Domain.Quests;
using TianshuDM.Domain.Workspaces;

namespace TianshuDM.Infrastructure.Sqlite;

public sealed class WorkspaceDatabase
    : IWorkspaceStore,
      IRecurringQuestImportStore,
      IRecurringQuestReleaseStore,
      IRecurringQuestMutationStore,
      IDailyQuestDraftStore,
      IWeeklyQuestDraftStore,
      IQuestChestRewardDraftStore,
      IStoryQuestDraftStore,
      IGameDataAtomicDraftStore,
      IApplicationSettingsStore,
      IHeroAuthoringLayoutStore
{
    private readonly string _databasePath;

    public WorkspaceDatabase(string databasePath)
    {
        _databasePath = databasePath;
    }

    public void Initialize()
    {
        string? directory = Path.GetDirectoryName(_databasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using SqliteConnection connection = CreateConnection();
        connection.Open();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            PRAGMA journal_mode = WAL;
            PRAGMA foreign_keys = ON;
            PRAGMA busy_timeout = 5000;

            CREATE TABLE IF NOT EXISTS workspace_snapshot (
                id INTEGER PRIMARY KEY CHECK (id = 1),
                source_hash TEXT NOT NULL,
                schema_version TEXT NOT NULL,
                imported_at_utc TEXT NOT NULL,
                unity_project_root TEXT NOT NULL DEFAULT '',
                daily_quest_workbook_path TEXT NOT NULL DEFAULT '',
                weekly_quest_workbook_path TEXT NOT NULL DEFAULT '',
                weekly_quest_source_hash TEXT NOT NULL DEFAULT '',
                quest_chest_reward_workbook_path TEXT NOT NULL DEFAULT '',
                quest_chest_reward_source_hash TEXT NOT NULL DEFAULT '',
                story_quest_workbook_path TEXT NOT NULL DEFAULT '',
                story_step_workbook_path TEXT NOT NULL DEFAULT '',
                story_turn_workbook_path TEXT NOT NULL DEFAULT '',
                story_quest_source_hash TEXT NOT NULL DEFAULT '',
                story_step_source_hash TEXT NOT NULL DEFAULT '',
                story_turn_source_hash TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS change_set (
                id TEXT PRIMARY KEY,
                base_source_hash TEXT NOT NULL,
                status TEXT NOT NULL,
                created_at_utc TEXT NOT NULL,
                updated_at_utc TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS application_setting (
                setting_key TEXT PRIMARY KEY,
                setting_value TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS daily_quest (
                id INTEGER PRIMARY KEY,
                type_code TEXT NOT NULL,
                type_value INTEGER NOT NULL,
                target_value INTEGER NOT NULL,
                sort_order INTEGER NOT NULL,
                description TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS daily_quest_reward (
                quest_id INTEGER NOT NULL,
                reward_order INTEGER NOT NULL,
                reward_type TEXT NOT NULL,
                config_id INTEGER NOT NULL,
                amount INTEGER NOT NULL,
                PRIMARY KEY (quest_id, reward_order),
                FOREIGN KEY (quest_id) REFERENCES daily_quest(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS weekly_quest (
                id INTEGER PRIMARY KEY,
                type_code TEXT NOT NULL,
                type_value INTEGER NOT NULL,
                target_value INTEGER NOT NULL,
                sort_order INTEGER NOT NULL,
                description TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS weekly_quest_reward (
                quest_id INTEGER NOT NULL,
                reward_order INTEGER NOT NULL,
                reward_type TEXT NOT NULL,
                config_id INTEGER NOT NULL,
                amount INTEGER NOT NULL,
                PRIMARY KEY (quest_id, reward_order),
                FOREIGN KEY (quest_id) REFERENCES weekly_quest(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS quest_chest_reward (
                id INTEGER PRIMARY KEY,
                required_count INTEGER NOT NULL,
                chest_level INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS quest_chest_reward_item (
                chest_id INTEGER NOT NULL,
                reward_order INTEGER NOT NULL,
                reward_type TEXT NOT NULL,
                config_id INTEGER NOT NULL,
                amount INTEGER NOT NULL,
                PRIMARY KEY (chest_id, reward_order),
                FOREIGN KEY (chest_id) REFERENCES quest_chest_reward(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS story_quest (
                id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                description TEXT NOT NULL,
                is_repeatable INTEGER NOT NULL,
                step_auto_accept INTEGER NOT NULL
            );

            CREATE TABLE IF NOT EXISTS story_quest_prerequisite (
                quest_id INTEGER NOT NULL,
                prerequisite_order INTEGER NOT NULL,
                prerequisite_type TEXT NOT NULL DEFAULT 'CompleteQuest',
                prerequisite_quest_id INTEGER NOT NULL,
                PRIMARY KEY (quest_id, prerequisite_order),
                FOREIGN KEY (quest_id) REFERENCES story_quest(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS story_quest_step (
                id INTEGER PRIMARY KEY,
                quest_id INTEGER NOT NULL,
                step_order INTEGER NOT NULL,
                npc_id INTEGER NOT NULL,
                accept_dialogue_id INTEGER NOT NULL,
                submit_dialogue_id INTEGER NOT NULL,
                processing_dialogue_id INTEGER NOT NULL,
                condition_type INTEGER NOT NULL,
                condition_value INTEGER NOT NULL,
                description TEXT NOT NULL DEFAULT '',
                condition_params TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS story_quest_step_reward (
                step_id INTEGER NOT NULL,
                reward_order INTEGER NOT NULL,
                reward_type TEXT NOT NULL,
                config_id INTEGER NOT NULL,
                amount INTEGER NOT NULL,
                PRIMARY KEY (step_id, reward_order),
                FOREIGN KEY (step_id) REFERENCES story_quest_step(id) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS story_conversation_turn (
                id INTEGER PRIMARY KEY,
                dialogue_id INTEGER NOT NULL,
                order_index INTEGER NOT NULL,
                speaker_type INTEGER NOT NULL,
                speaker_name TEXT NOT NULL,
                speaker_image TEXT NOT NULL,
                text TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS game_data_table (
                table_key TEXT PRIMARY KEY,
                display_name TEXT NOT NULL,
                category TEXT NOT NULL,
                workbook_path TEXT NOT NULL,
                source_hash TEXT NOT NULL,
                worksheet_name TEXT NOT NULL,
                fields_json TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS game_data_record (
                table_key TEXT NOT NULL,
                record_id INTEGER NOT NULL,
                fields_json TEXT NOT NULL,
                source_order INTEGER NOT NULL DEFAULT 0,
                source_row INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (table_key, record_id),
                FOREIGN KEY (table_key) REFERENCES game_data_table(table_key) ON DELETE CASCADE
            );

            CREATE TABLE IF NOT EXISTS hero_authoring_layout (
                project_root TEXT NOT NULL,
                skill_id INTEGER NOT NULL,
                projection_mode TEXT NOT NULL,
                instance_key TEXT NOT NULL,
                position_x REAL NOT NULL,
                position_y REAL NOT NULL,
                PRIMARY KEY (project_root, skill_id, projection_mode, instance_key)
            );
            """;
        command.ExecuteNonQuery();

        EnsureColumn(
            connection,
            "workspace_snapshot",
            "unity_project_root",
            "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(
            connection,
            "workspace_snapshot",
            "daily_quest_workbook_path",
            "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "workspace_snapshot", "weekly_quest_workbook_path", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "workspace_snapshot", "weekly_quest_source_hash", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "workspace_snapshot", "quest_chest_reward_workbook_path", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "workspace_snapshot", "quest_chest_reward_source_hash", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "game_data_record", "source_order", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "game_data_record", "source_row", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(connection, "workspace_snapshot", "story_quest_workbook_path", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "workspace_snapshot", "story_step_workbook_path", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "workspace_snapshot", "story_turn_workbook_path", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "workspace_snapshot", "story_quest_source_hash", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "workspace_snapshot", "story_step_source_hash", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "workspace_snapshot", "story_turn_source_hash", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(
            connection,
            "story_quest_prerequisite",
            "prerequisite_type",
            "TEXT NOT NULL DEFAULT 'CompleteQuest'");
        EnsureColumn(connection, "story_quest_step", "description", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(connection, "story_quest_step", "condition_params", "TEXT NOT NULL DEFAULT ''");
        RemoveStoryQuestStepQuestForeignKey(connection);
    }

    public IReadOnlyList<HeroAuthoringNodeLayout> ReadHeroAuthoringLayout(
        string projectRoot,
        int skillId,
        string projectionMode)
    {
        using SqliteConnection connection = CreateConnection();
        connection.Open();
        EnableForeignKeys(connection);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT instance_key, position_x, position_y
            FROM hero_authoring_layout
            WHERE project_root = $projectRoot
              AND skill_id = $skillId
              AND projection_mode = $projectionMode
            ORDER BY instance_key;
            """;
        command.Parameters.AddWithValue("$projectRoot", projectRoot);
        command.Parameters.AddWithValue("$skillId", skillId);
        command.Parameters.AddWithValue("$projectionMode", projectionMode);
        using SqliteDataReader reader = command.ExecuteReader();
        var result = new List<HeroAuthoringNodeLayout>();
        while (reader.Read())
        {
            result.Add(new HeroAuthoringNodeLayout(reader.GetString(0), reader.GetDouble(1), reader.GetDouble(2)));
        }

        return result;
    }

    public void ReplaceHeroAuthoringLayout(
        string projectRoot,
        int skillId,
        string projectionMode,
        IReadOnlyList<HeroAuthoringNodeLayout> positions)
    {
        using SqliteConnection connection = CreateConnection();
        connection.Open();
        EnableForeignKeys(connection);
        using SqliteTransaction transaction = connection.BeginTransaction();
        ExecuteParameterized(
            connection,
            transaction,
            """
            DELETE FROM hero_authoring_layout
            WHERE project_root = $projectRoot
              AND skill_id = $skillId
              AND projection_mode = $projectionMode;
            """,
            ("$projectRoot", projectRoot),
            ("$skillId", skillId),
            ("$projectionMode", projectionMode));
        foreach (HeroAuthoringNodeLayout position in positions)
        {
            ExecuteParameterized(
                connection,
                transaction,
                """
                INSERT INTO hero_authoring_layout (
                    project_root, skill_id, projection_mode, instance_key, position_x, position_y)
                VALUES ($projectRoot, $skillId, $projectionMode, $instanceKey, $positionX, $positionY);
                """,
                ("$projectRoot", projectRoot),
                ("$skillId", skillId),
                ("$projectionMode", projectionMode),
                ("$instanceKey", position.InstanceKey),
                ("$positionX", position.X),
                ("$positionY", position.Y));
        }

        transaction.Commit();
    }

    private static void RemoveStoryQuestStepQuestForeignKey(SqliteConnection connection)
    {
        bool hasQuestForeignKey;
        using (SqliteCommand inspect = connection.CreateCommand())
        {
            inspect.CommandText = "PRAGMA foreign_key_list(story_quest_step);";
            using SqliteDataReader reader = inspect.ExecuteReader();
            hasQuestForeignKey = false;
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(2), "story_quest", StringComparison.Ordinal))
                {
                    hasQuestForeignKey = true;
                    break;
                }
            }
        }

        if (!hasQuestForeignKey)
        {
            return;
        }

        using (SqliteCommand disableForeignKeys = connection.CreateCommand())
        {
            disableForeignKeys.CommandText = "PRAGMA foreign_keys = OFF;";
            disableForeignKeys.ExecuteNonQuery();
        }

        using (SqliteTransaction transaction = connection.BeginTransaction())
        using (SqliteCommand migrate = connection.CreateCommand())
        {
            migrate.Transaction = transaction;
            migrate.CommandText =
                """
                CREATE TABLE story_quest_step_new (
                    id INTEGER PRIMARY KEY,
                    quest_id INTEGER NOT NULL,
                    step_order INTEGER NOT NULL,
                    npc_id INTEGER NOT NULL,
                    accept_dialogue_id INTEGER NOT NULL,
                    submit_dialogue_id INTEGER NOT NULL,
                    processing_dialogue_id INTEGER NOT NULL,
                    condition_type INTEGER NOT NULL,
                    condition_value INTEGER NOT NULL,
                    description TEXT NOT NULL DEFAULT '',
                    condition_params TEXT NOT NULL DEFAULT ''
                );
                INSERT INTO story_quest_step_new
                SELECT id, quest_id, step_order, npc_id, accept_dialogue_id,
                       submit_dialogue_id, processing_dialogue_id, condition_type,
                       condition_value, description, condition_params
                FROM story_quest_step;

                CREATE TABLE story_quest_step_reward_new (
                    step_id INTEGER NOT NULL,
                    reward_order INTEGER NOT NULL,
                    reward_type TEXT NOT NULL,
                    config_id INTEGER NOT NULL,
                    amount INTEGER NOT NULL,
                    PRIMARY KEY (step_id, reward_order),
                    FOREIGN KEY (step_id) REFERENCES story_quest_step_new(id) ON DELETE CASCADE
                );
                INSERT INTO story_quest_step_reward_new
                SELECT step_id, reward_order, reward_type, config_id, amount
                FROM story_quest_step_reward;

                DROP TABLE story_quest_step_reward;
                DROP TABLE story_quest_step;
                ALTER TABLE story_quest_step_new RENAME TO story_quest_step;
                ALTER TABLE story_quest_step_reward_new RENAME TO story_quest_step_reward;
                """;
            migrate.ExecuteNonQuery();
            transaction.Commit();
        }

        using SqliteCommand enableForeignKeys = connection.CreateCommand();
        enableForeignKeys.CommandText = "PRAGMA foreign_keys = ON;";
        enableForeignKeys.ExecuteNonQuery();
    }

    public ApplicationSettings ReadSettings()
    {
        using SqliteConnection connection = CreateConnection();
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT setting_value FROM application_setting WHERE setting_key = 'allow_luban_failure_confirmation';";
        object? value = command.ExecuteScalar();
        return value is string text && bool.TryParse(text, out bool enabled)
            ? new ApplicationSettings(enabled)
            : ApplicationSettings.Default;
    }

    public void SaveSettings(ApplicationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        using SqliteConnection connection = CreateConnection();
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO application_setting (setting_key, setting_value)
            VALUES ('allow_luban_failure_confirmation', $value)
            ON CONFLICT(setting_key) DO UPDATE SET setting_value = excluded.setting_value;
            """;
        command.Parameters.AddWithValue(
            "$value",
            settings.AllowLubanFailureConfirmation.ToString());
        command.ExecuteNonQuery();
    }

    public void ReplaceWorkspace(WorkspaceImportResult import)
    {
        ArgumentNullException.ThrowIfNull(import);

        using SqliteConnection connection = CreateConnection();
        connection.Open();
        EnableForeignKeys(connection);
        using SqliteTransaction transaction = connection.BeginTransaction();

        Execute(connection, transaction, "DELETE FROM daily_quest;");
        Execute(connection, transaction, "DELETE FROM weekly_quest;");
        Execute(connection, transaction, "DELETE FROM quest_chest_reward;");
        ClearStoryQuest(connection, transaction);
        Execute(connection, transaction, "DELETE FROM game_data_table;");
        Execute(connection, transaction, "DELETE FROM change_set;");

        foreach (DailyQuestDefinition quest in import.DailyQuests)
        {
            using SqliteCommand questCommand = connection.CreateCommand();
            questCommand.Transaction = transaction;
            questCommand.CommandText =
                """
                INSERT INTO daily_quest (
                    id,
                    type_code,
                    type_value,
                    target_value,
                    sort_order,
                    description
                )
                VALUES (
                    $id,
                    $typeCode,
                    $typeValue,
                    $targetValue,
                    $sortOrder,
                    $description
                );
                """;
            questCommand.Parameters.AddWithValue("$id", quest.Id);
            questCommand.Parameters.AddWithValue("$typeCode", quest.Type.Code);
            questCommand.Parameters.AddWithValue("$typeValue", quest.Type.LegacyValue);
            questCommand.Parameters.AddWithValue("$targetValue", quest.TargetValue);
            questCommand.Parameters.AddWithValue("$sortOrder", quest.SortOrder);
            questCommand.Parameters.AddWithValue("$description", quest.Description);
            questCommand.ExecuteNonQuery();

            for (int index = 0; index < quest.Rewards.Count; index++)
            {
                QuestReward reward = quest.Rewards[index];
                using SqliteCommand rewardCommand = connection.CreateCommand();
                rewardCommand.Transaction = transaction;
                rewardCommand.CommandText =
                    """
                    INSERT INTO daily_quest_reward (
                        quest_id,
                        reward_order,
                        reward_type,
                        config_id,
                        amount
                    )
                    VALUES ($questId, $rewardOrder, $rewardType, $configId, $amount);
                    """;
                rewardCommand.Parameters.AddWithValue("$questId", quest.Id);
                rewardCommand.Parameters.AddWithValue("$rewardOrder", index);
                rewardCommand.Parameters.AddWithValue("$rewardType", reward.Type);
                rewardCommand.Parameters.AddWithValue("$configId", reward.ConfigId);
                rewardCommand.Parameters.AddWithValue("$amount", reward.Amount);
                rewardCommand.ExecuteNonQuery();
            }
        }

        foreach (WeeklyQuestDefinition quest in import.WeeklyQuests)
        {
            InsertRecurringQuest(
                connection,
                transaction,
                "weekly_quest",
                "weekly_quest_reward",
                quest.Id,
                quest.Type,
                quest.TargetValue,
                quest.Rewards,
                quest.SortOrder,
                quest.Description);
        }

        foreach (QuestChestRewardDefinition chest in import.QuestChestRewards)
        {
            ExecuteParameterized(
                connection,
                transaction,
                "INSERT INTO quest_chest_reward (id, required_count, chest_level) VALUES ($id, $requiredCount, $chestLevel);",
                ("$id", chest.Id),
                ("$requiredCount", chest.RequiredCount),
                ("$chestLevel", chest.ChestLevel));
            InsertRewards(
                connection,
                transaction,
                "quest_chest_reward_item",
                "chest_id",
                chest.Id,
                chest.Rewards);
        }

        if (import.StoryQuest is not null)
        {
            ReplaceStoryQuest(connection, transaction, import.StoryQuest.Dataset);
        }

        InsertGameDataCatalog(connection, transaction, import.GameData);

        using SqliteCommand snapshotCommand = connection.CreateCommand();
        snapshotCommand.Transaction = transaction;
        snapshotCommand.CommandText =
            """
            INSERT INTO workspace_snapshot (
                id,
                source_hash,
                schema_version,
                imported_at_utc,
                unity_project_root,
                daily_quest_workbook_path,
                weekly_quest_workbook_path,
                weekly_quest_source_hash,
                quest_chest_reward_workbook_path,
                quest_chest_reward_source_hash,
                story_quest_workbook_path,
                story_step_workbook_path,
                story_turn_workbook_path,
                story_quest_source_hash,
                story_step_source_hash,
                story_turn_source_hash
            )
            VALUES (
                1, $sourceHash, '1', $importedAtUtc, $projectRoot, $workbookPath,
                $weeklyQuestPath, $weeklyQuestHash, $chestRewardPath, $chestRewardHash,
                $storyQuestPath, $storyStepPath, $storyTurnPath,
                $storyQuestHash, $storyStepHash, $storyTurnHash
            )
            ON CONFLICT(id) DO UPDATE SET
                source_hash = excluded.source_hash,
                schema_version = excluded.schema_version,
                imported_at_utc = excluded.imported_at_utc,
                unity_project_root = excluded.unity_project_root,
                daily_quest_workbook_path = excluded.daily_quest_workbook_path,
                weekly_quest_workbook_path = excluded.weekly_quest_workbook_path,
                weekly_quest_source_hash = excluded.weekly_quest_source_hash,
                quest_chest_reward_workbook_path = excluded.quest_chest_reward_workbook_path,
                quest_chest_reward_source_hash = excluded.quest_chest_reward_source_hash,
                story_quest_workbook_path = excluded.story_quest_workbook_path,
                story_step_workbook_path = excluded.story_step_workbook_path,
                story_turn_workbook_path = excluded.story_turn_workbook_path,
                story_quest_source_hash = excluded.story_quest_source_hash,
                story_step_source_hash = excluded.story_step_source_hash,
                story_turn_source_hash = excluded.story_turn_source_hash;
            """;
        snapshotCommand.Parameters.AddWithValue("$sourceHash", import.SourceHash);
        snapshotCommand.Parameters.AddWithValue(
            "$importedAtUtc",
            DateTimeOffset.UtcNow.ToString("O"));
        snapshotCommand.Parameters.AddWithValue("$projectRoot", import.UnityProjectRoot);
        snapshotCommand.Parameters.AddWithValue(
            "$workbookPath",
            import.DailyQuestWorkbookPath);
        snapshotCommand.Parameters.AddWithValue("$weeklyQuestPath", import.WeeklyQuestWorkbookPath);
        snapshotCommand.Parameters.AddWithValue("$weeklyQuestHash", import.WeeklyQuestSourceHash);
        snapshotCommand.Parameters.AddWithValue("$chestRewardPath", import.QuestChestRewardWorkbookPath);
        snapshotCommand.Parameters.AddWithValue("$chestRewardHash", import.QuestChestRewardSourceHash);
        StoryQuestImportData? story = import.StoryQuest;
        snapshotCommand.Parameters.AddWithValue("$storyQuestPath", story?.QuestWorkbookPath ?? string.Empty);
        snapshotCommand.Parameters.AddWithValue("$storyStepPath", story?.StepWorkbookPath ?? string.Empty);
        snapshotCommand.Parameters.AddWithValue("$storyTurnPath", story?.TurnWorkbookPath ?? string.Empty);
        snapshotCommand.Parameters.AddWithValue("$storyQuestHash", story?.QuestSourceHash ?? string.Empty);
        snapshotCommand.Parameters.AddWithValue("$storyStepHash", story?.StepSourceHash ?? string.Empty);
        snapshotCommand.Parameters.AddWithValue("$storyTurnHash", story?.TurnSourceHash ?? string.Empty);
        snapshotCommand.ExecuteNonQuery();

        transaction.Commit();
    }

    public GameDataCatalog? ReadGameDataCatalog()
    {
        using SqliteConnection connection = CreateConnection();
        connection.Open();
        return ReadGameDataCatalog(connection);
    }

    public void ReplaceGameDataImport(GameDataCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        using SqliteConnection connection = CreateConnection();
        connection.Open();
        EnableForeignKeys(connection);
        using SqliteTransaction transaction = connection.BeginTransaction();
        Execute(connection, transaction, "DELETE FROM game_data_table;");
        InsertGameDataCatalog(connection, transaction, catalog);
        Execute(connection, transaction, "DELETE FROM change_set WHERE id LIKE 'game-data:%';");
        transaction.Commit();
    }

    public void ReplaceGameDataTable(
        string tableKey,
        IReadOnlyList<GameDataRecord> records,
        string dirtyScope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tableKey);
        ArgumentNullException.ThrowIfNull(records);
        ArgumentException.ThrowIfNullOrWhiteSpace(dirtyScope);

        using SqliteConnection connection = CreateConnection();
        connection.Open();
        EnableForeignKeys(connection);
        using SqliteTransaction transaction = connection.BeginTransaction();

        using (SqliteCommand existsCommand = connection.CreateCommand())
        {
            existsCommand.Transaction = transaction;
            existsCommand.CommandText = "SELECT COUNT(*) FROM game_data_table WHERE table_key = $tableKey;";
            existsCommand.Parameters.AddWithValue("$tableKey", tableKey);
            if (Convert.ToInt32(existsCommand.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
            {
                throw new KeyNotFoundException($"不存在数据表“{tableKey}”。");
            }
        }

        ExecuteParameterized(
            connection,
            transaction,
            "DELETE FROM game_data_record WHERE table_key = $tableKey;",
            ("$tableKey", tableKey));
        InsertGameDataRecords(connection, transaction, tableKey, records);
        MarkDirty(connection, transaction, dirtyScope);
        transaction.Commit();
    }

    public void ReplaceGameDataTables(
        IReadOnlyDictionary<string, IReadOnlyList<GameDataRecord>> replacements)
    {
        ArgumentNullException.ThrowIfNull(replacements);
        if (replacements.Count == 0)
        {
            throw new ArgumentException("跨表草稿批次不能为空。", nameof(replacements));
        }

        using SqliteConnection connection = CreateConnection();
        connection.Open();
        EnableForeignKeys(connection);
        using SqliteTransaction transaction = connection.BeginTransaction();
        foreach ((string tableKey, IReadOnlyList<GameDataRecord> records) in replacements.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            using var existsCommand = connection.CreateCommand();
            existsCommand.Transaction = transaction;
            existsCommand.CommandText = "SELECT COUNT(*) FROM game_data_table WHERE table_key = $tableKey;";
            existsCommand.Parameters.AddWithValue("$tableKey", tableKey);
            if (Convert.ToInt32(existsCommand.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
            {
                throw new KeyNotFoundException($"不存在数据表“{tableKey}”。");
            }

            ExecuteParameterized(
                connection,
                transaction,
                "DELETE FROM game_data_record WHERE table_key = $tableKey;",
                ("$tableKey", tableKey));
            InsertGameDataRecords(connection, transaction, tableKey, records);
            MarkDirty(connection, transaction, $"game-data:{tableKey}");
        }
        transaction.Commit();
    }

    public IReadOnlyList<string> ReadDirtyGameDataTableKeys()
    {
        using SqliteConnection connection = CreateConnection();
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT substr(id, length('game-data:') + 1) FROM change_set WHERE status = 'dirty' AND id LIKE 'game-data:%' ORDER BY id;";
        using SqliteDataReader reader = command.ExecuteReader();
        var result = new List<string>();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    public void ReplaceRecurringQuestImport(WorkspaceImportResult import)
    {
        ArgumentNullException.ThrowIfNull(import);

        using SqliteConnection connection = CreateConnection();
        connection.Open();
        EnableForeignKeys(connection);
        using SqliteTransaction transaction = connection.BeginTransaction();

        Execute(connection, transaction, "DELETE FROM weekly_quest;");
        Execute(connection, transaction, "DELETE FROM quest_chest_reward;");

        foreach (WeeklyQuestDefinition quest in import.WeeklyQuests)
        {
            InsertRecurringQuest(
                connection,
                transaction,
                "weekly_quest",
                "weekly_quest_reward",
                quest.Id,
                quest.Type,
                quest.TargetValue,
                quest.Rewards,
                quest.SortOrder,
                quest.Description);
        }

        foreach (QuestChestRewardDefinition chest in import.QuestChestRewards)
        {
            ExecuteParameterized(
                connection,
                transaction,
                "INSERT INTO quest_chest_reward (id, required_count, chest_level) VALUES ($id, $requiredCount, $chestLevel);",
                ("$id", chest.Id),
                ("$requiredCount", chest.RequiredCount),
                ("$chestLevel", chest.ChestLevel));
            InsertRewards(
                connection,
                transaction,
                "quest_chest_reward_item",
                "chest_id",
                chest.Id,
                chest.Rewards);
        }

        ExecuteParameterized(
            connection,
            transaction,
            """
            UPDATE workspace_snapshot
            SET weekly_quest_workbook_path = $weeklyQuestPath,
                weekly_quest_source_hash = $weeklyQuestHash,
                quest_chest_reward_workbook_path = $chestRewardPath,
                quest_chest_reward_source_hash = $chestRewardHash,
                imported_at_utc = $importedAtUtc
            WHERE id = 1;
            """,
            ("$weeklyQuestPath", import.WeeklyQuestWorkbookPath),
            ("$weeklyQuestHash", import.WeeklyQuestSourceHash),
            ("$chestRewardPath", import.QuestChestRewardWorkbookPath),
            ("$chestRewardHash", import.QuestChestRewardSourceHash),
            ("$importedAtUtc", DateTimeOffset.UtcNow.ToString("O")));

        transaction.Commit();
    }

    public void ReplaceDailyQuestImport(WorkspaceImportResult import)
    {
        ArgumentNullException.ThrowIfNull(import);

        using SqliteConnection connection = CreateConnection();
        connection.Open();
        EnableForeignKeys(connection);
        using SqliteTransaction transaction = connection.BeginTransaction();

        Execute(connection, transaction, "DELETE FROM daily_quest;");
        foreach (DailyQuestDefinition quest in import.DailyQuests)
        {
            InsertRecurringQuest(
                connection,
                transaction,
                "daily_quest",
                "daily_quest_reward",
                quest.Id,
                quest.Type,
                quest.TargetValue,
                quest.Rewards,
                quest.SortOrder,
                quest.Description);
        }

        ExecuteParameterized(
            connection,
            transaction,
            """
            UPDATE workspace_snapshot
            SET daily_quest_workbook_path = $dailyQuestPath,
                source_hash = $dailyQuestHash,
                imported_at_utc = $importedAtUtc
            WHERE id = 1;
            """,
            ("$dailyQuestPath", import.DailyQuestWorkbookPath),
            ("$dailyQuestHash", import.SourceHash),
            ("$importedAtUtc", DateTimeOffset.UtcNow.ToString("O")));
        Execute(connection, transaction, "DELETE FROM change_set WHERE id = 'daily';");
        transaction.Commit();
    }

    public void ReplaceWeeklyQuestImport(WorkspaceImportResult import)
    {
        ArgumentNullException.ThrowIfNull(import);

        using SqliteConnection connection = CreateConnection();
        connection.Open();
        EnableForeignKeys(connection);
        using SqliteTransaction transaction = connection.BeginTransaction();

        Execute(connection, transaction, "DELETE FROM weekly_quest;");
        foreach (WeeklyQuestDefinition quest in import.WeeklyQuests)
        {
            InsertRecurringQuest(
                connection,
                transaction,
                "weekly_quest",
                "weekly_quest_reward",
                quest.Id,
                quest.Type,
                quest.TargetValue,
                quest.Rewards,
                quest.SortOrder,
                quest.Description);
        }

        ExecuteParameterized(
            connection,
            transaction,
            """
            UPDATE workspace_snapshot
            SET weekly_quest_workbook_path = $weeklyQuestPath,
                weekly_quest_source_hash = $weeklyQuestHash,
                imported_at_utc = $importedAtUtc
            WHERE id = 1;
            """,
            ("$weeklyQuestPath", import.WeeklyQuestWorkbookPath),
            ("$weeklyQuestHash", import.WeeklyQuestSourceHash),
            ("$importedAtUtc", DateTimeOffset.UtcNow.ToString("O")));
        Execute(connection, transaction, "DELETE FROM change_set WHERE id = 'weekly';");
        transaction.Commit();
    }

    public void ReplaceQuestChestRewardImport(WorkspaceImportResult import)
    {
        ArgumentNullException.ThrowIfNull(import);

        using SqliteConnection connection = CreateConnection();
        connection.Open();
        EnableForeignKeys(connection);
        using SqliteTransaction transaction = connection.BeginTransaction();

        Execute(connection, transaction, "DELETE FROM quest_chest_reward;");
        foreach (QuestChestRewardDefinition chest in import.QuestChestRewards)
        {
            ExecuteParameterized(
                connection,
                transaction,
                "INSERT INTO quest_chest_reward (id, required_count, chest_level) VALUES ($id, $requiredCount, $chestLevel);",
                ("$id", chest.Id),
                ("$requiredCount", chest.RequiredCount),
                ("$chestLevel", chest.ChestLevel));
            InsertRewards(
                connection,
                transaction,
                "quest_chest_reward_item",
                "chest_id",
                chest.Id,
                chest.Rewards);
        }

        ExecuteParameterized(
            connection,
            transaction,
            """
            UPDATE workspace_snapshot
            SET quest_chest_reward_workbook_path = $chestRewardPath,
                quest_chest_reward_source_hash = $chestRewardHash,
                imported_at_utc = $importedAtUtc
            WHERE id = 1;
            """,
            ("$chestRewardPath", import.QuestChestRewardWorkbookPath),
            ("$chestRewardHash", import.QuestChestRewardSourceHash),
            ("$importedAtUtc", DateTimeOffset.UtcNow.ToString("O")));
        Execute(connection, transaction, "DELETE FROM change_set WHERE id = 'chest';");
        transaction.Commit();
    }

    public void ReplaceRecurringQuestRelease(WorkspaceImportResult import)
    {
        ArgumentNullException.ThrowIfNull(import);

        using SqliteConnection connection = CreateConnection();
        connection.Open();
        EnableForeignKeys(connection);
        using SqliteTransaction transaction = connection.BeginTransaction();

        Execute(connection, transaction, "DELETE FROM daily_quest;");
        Execute(connection, transaction, "DELETE FROM weekly_quest;");
        Execute(connection, transaction, "DELETE FROM quest_chest_reward;");
        foreach (DailyQuestDefinition quest in import.DailyQuests)
        {
            InsertRecurringQuest(
                connection,
                transaction,
                "daily_quest",
                "daily_quest_reward",
                quest.Id,
                quest.Type,
                quest.TargetValue,
                quest.Rewards,
                quest.SortOrder,
                quest.Description);
        }

        foreach (WeeklyQuestDefinition quest in import.WeeklyQuests)
        {
            InsertRecurringQuest(
                connection,
                transaction,
                "weekly_quest",
                "weekly_quest_reward",
                quest.Id,
                quest.Type,
                quest.TargetValue,
                quest.Rewards,
                quest.SortOrder,
                quest.Description);
        }

        foreach (QuestChestRewardDefinition chest in import.QuestChestRewards)
        {
            ExecuteParameterized(
                connection,
                transaction,
                "INSERT INTO quest_chest_reward (id, required_count, chest_level) VALUES ($id, $requiredCount, $chestLevel);",
                ("$id", chest.Id),
                ("$requiredCount", chest.RequiredCount),
                ("$chestLevel", chest.ChestLevel));
            InsertRewards(
                connection,
                transaction,
                "quest_chest_reward_item",
                "chest_id",
                chest.Id,
                chest.Rewards);
        }

        ExecuteParameterized(
            connection,
            transaction,
            """
            UPDATE workspace_snapshot
            SET source_hash = $dailyQuestHash,
                daily_quest_workbook_path = $dailyQuestPath,
                weekly_quest_workbook_path = $weeklyQuestPath,
                weekly_quest_source_hash = $weeklyQuestHash,
                quest_chest_reward_workbook_path = $chestRewardPath,
                quest_chest_reward_source_hash = $chestRewardHash,
                imported_at_utc = $importedAtUtc
            WHERE id = 1;
            """,
            ("$dailyQuestPath", import.DailyQuestWorkbookPath),
            ("$dailyQuestHash", import.SourceHash),
            ("$weeklyQuestPath", import.WeeklyQuestWorkbookPath),
            ("$weeklyQuestHash", import.WeeklyQuestSourceHash),
            ("$chestRewardPath", import.QuestChestRewardWorkbookPath),
            ("$chestRewardHash", import.QuestChestRewardSourceHash),
            ("$importedAtUtc", DateTimeOffset.UtcNow.ToString("O")));
        Execute(
            connection,
            transaction,
            "DELETE FROM change_set WHERE id IN ('daily', 'weekly', 'chest');");
        transaction.Commit();
    }

    public bool TrySaveDailyQuestDraft(DailyQuestDefinition quest)
    {
        ArgumentNullException.ThrowIfNull(quest);

        using SqliteConnection connection = CreateConnection();
        connection.Open();
        EnableForeignKeys(connection);
        using SqliteTransaction transaction = connection.BeginTransaction();

        using SqliteCommand questCommand = connection.CreateCommand();
        questCommand.Transaction = transaction;
        questCommand.CommandText =
            """
            UPDATE daily_quest
            SET type_code = $typeCode,
                type_value = $typeValue,
                target_value = $targetValue,
                sort_order = $sortOrder,
                description = $description
            WHERE id = $id;
            """;
        questCommand.Parameters.AddWithValue("$id", quest.Id);
        questCommand.Parameters.AddWithValue("$typeCode", quest.Type.Code);
        questCommand.Parameters.AddWithValue("$typeValue", quest.Type.LegacyValue);
        questCommand.Parameters.AddWithValue("$targetValue", quest.TargetValue);
        questCommand.Parameters.AddWithValue("$sortOrder", quest.SortOrder);
        questCommand.Parameters.AddWithValue("$description", quest.Description);
        if (questCommand.ExecuteNonQuery() == 0)
        {
            transaction.Rollback();
            return false;
        }

        using SqliteCommand deleteRewardsCommand = connection.CreateCommand();
        deleteRewardsCommand.Transaction = transaction;
        deleteRewardsCommand.CommandText =
            "DELETE FROM daily_quest_reward WHERE quest_id = $questId;";
        deleteRewardsCommand.Parameters.AddWithValue("$questId", quest.Id);
        deleteRewardsCommand.ExecuteNonQuery();

        for (int index = 0; index < quest.Rewards.Count; index++)
        {
            QuestReward reward = quest.Rewards[index];
            using SqliteCommand rewardCommand = connection.CreateCommand();
            rewardCommand.Transaction = transaction;
            rewardCommand.CommandText =
                """
                INSERT INTO daily_quest_reward (
                    quest_id,
                    reward_order,
                    reward_type,
                    config_id,
                    amount
                )
                VALUES ($questId, $rewardOrder, $rewardType, $configId, $amount);
                """;
            rewardCommand.Parameters.AddWithValue("$questId", quest.Id);
            rewardCommand.Parameters.AddWithValue("$rewardOrder", index);
            rewardCommand.Parameters.AddWithValue("$rewardType", reward.Type);
            rewardCommand.Parameters.AddWithValue("$configId", reward.ConfigId);
            rewardCommand.Parameters.AddWithValue("$amount", reward.Amount);
            rewardCommand.ExecuteNonQuery();
        }

        string now = DateTimeOffset.UtcNow.ToString("O");
        using SqliteCommand changeSetCommand = connection.CreateCommand();
        changeSetCommand.Transaction = transaction;
        changeSetCommand.CommandText =
            """
            INSERT INTO change_set (
                id,
                base_source_hash,
                status,
                created_at_utc,
                updated_at_utc
            )
            SELECT 'daily', source_hash, 'dirty', $now, $now
            FROM workspace_snapshot
            WHERE id = 1
            ON CONFLICT(id) DO UPDATE SET
                status = excluded.status,
                updated_at_utc = excluded.updated_at_utc;
            """;
        changeSetCommand.Parameters.AddWithValue("$now", now);
        changeSetCommand.ExecuteNonQuery();

        transaction.Commit();
        return true;
    }

    public bool TryCreateDailyQuestDraft(DailyQuestDefinition quest)
    {
        return TryCreateRecurringQuestDraft(
            "daily_quest",
            "daily_quest_reward",
            "daily",
            quest.Id,
            quest.Type,
            quest.TargetValue,
            quest.Rewards,
            quest.SortOrder,
            quest.Description);
    }

    public bool TryDeleteDailyQuestDraft(int questId)
    {
        return TryDeleteRecurringQuestDraft("daily_quest", "daily", questId);
    }

    public bool TrySaveWeeklyQuestDraft(WeeklyQuestDefinition quest)
    {
        ArgumentNullException.ThrowIfNull(quest);

        using SqliteConnection connection = CreateConnection();
        connection.Open();
        EnableForeignKeys(connection);
        using SqliteTransaction transaction = connection.BeginTransaction();

        int changed = ExecuteParameterizedWithResult(
            connection,
            transaction,
            """
            UPDATE weekly_quest
            SET type_code = $typeCode,
                type_value = $typeValue,
                target_value = $targetValue,
                sort_order = $sortOrder,
                description = $description
            WHERE id = $id;
            """,
            ("$id", quest.Id),
            ("$typeCode", quest.Type.Code),
            ("$typeValue", quest.Type.LegacyValue),
            ("$targetValue", quest.TargetValue),
            ("$sortOrder", quest.SortOrder),
            ("$description", quest.Description));
        if (changed == 0)
        {
            transaction.Rollback();
            return false;
        }

        ExecuteParameterized(
            connection,
            transaction,
            "DELETE FROM weekly_quest_reward WHERE quest_id = $id;",
            ("$id", quest.Id));
        InsertRewards(
            connection,
            transaction,
            "weekly_quest_reward",
            "quest_id",
            quest.Id,
            quest.Rewards);
        MarkDirty(connection, transaction, "weekly");
        transaction.Commit();
        return true;
    }

    public bool TryCreateWeeklyQuestDraft(WeeklyQuestDefinition quest)
    {
        return TryCreateRecurringQuestDraft(
            "weekly_quest",
            "weekly_quest_reward",
            "weekly",
            quest.Id,
            quest.Type,
            quest.TargetValue,
            quest.Rewards,
            quest.SortOrder,
            quest.Description);
    }

    public bool TryDeleteWeeklyQuestDraft(int questId)
    {
        return TryDeleteRecurringQuestDraft("weekly_quest", "weekly", questId);
    }

    public bool TrySaveQuestChestRewardDraft(QuestChestRewardDefinition chest)
    {
        ArgumentNullException.ThrowIfNull(chest);

        using SqliteConnection connection = CreateConnection();
        connection.Open();
        EnableForeignKeys(connection);
        using SqliteTransaction transaction = connection.BeginTransaction();

        int changed = ExecuteParameterizedWithResult(
            connection,
            transaction,
            """
            UPDATE quest_chest_reward
            SET required_count = $requiredCount,
                chest_level = $chestLevel
            WHERE id = $id;
            """,
            ("$id", chest.Id),
            ("$requiredCount", chest.RequiredCount),
            ("$chestLevel", chest.ChestLevel));
        if (changed == 0)
        {
            transaction.Rollback();
            return false;
        }

        ExecuteParameterized(
            connection,
            transaction,
            "DELETE FROM quest_chest_reward_item WHERE chest_id = $id;",
            ("$id", chest.Id));
        InsertRewards(
            connection,
            transaction,
            "quest_chest_reward_item",
            "chest_id",
            chest.Id,
            chest.Rewards);
        MarkDirty(connection, transaction, "chest");
        transaction.Commit();
        return true;
    }

    public bool TryCreateQuestChestRewardDraft(QuestChestRewardDefinition chest)
    {
        ArgumentNullException.ThrowIfNull(chest);

        using SqliteConnection connection = CreateConnection();
        connection.Open();
        EnableForeignKeys(connection);
        using SqliteTransaction transaction = connection.BeginTransaction();
        int changed = ExecuteParameterizedWithResult(
            connection,
            transaction,
            "INSERT OR IGNORE INTO quest_chest_reward (id, required_count, chest_level) VALUES ($id, $requiredCount, $chestLevel);",
            ("$id", chest.Id),
            ("$requiredCount", chest.RequiredCount),
            ("$chestLevel", chest.ChestLevel));
        if (changed == 0)
        {
            transaction.Rollback();
            return false;
        }

        InsertRewards(
            connection,
            transaction,
            "quest_chest_reward_item",
            "chest_id",
            chest.Id,
            chest.Rewards);
        MarkDirty(connection, transaction, "chest");
        transaction.Commit();
        return true;
    }

    public bool TryDeleteQuestChestRewardDraft(int chestId)
    {
        return TryDeleteRecurringQuestDraft("quest_chest_reward", "chest", chestId);
    }

    public string ReadDraftStatus()
    {
        using SqliteConnection connection = CreateConnection();
        connection.Open();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT status FROM change_set WHERE status = 'dirty' LIMIT 1;";
        return command.ExecuteScalar() as string ?? "clean";
    }

    public StoryQuestDataset? ReadStoryQuestDataset()
    {
        return ReadWorkspace()?.StoryQuest?.Dataset;
    }

    public void ReplaceStoryQuestDataset(StoryQuestDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);

        using SqliteConnection connection = CreateConnection();
        connection.Open();
        EnableForeignKeys(connection);
        using SqliteTransaction transaction = connection.BeginTransaction();
        ClearStoryQuest(connection, transaction);
        ReplaceStoryQuest(connection, transaction, dataset);
        string now = DateTimeOffset.UtcNow.ToString("O");
        ExecuteParameterized(
            connection,
            transaction,
            """
            INSERT INTO change_set (id, base_source_hash, status, created_at_utc, updated_at_utc)
            SELECT 'story', source_hash, 'dirty', $now, $now
            FROM workspace_snapshot
            WHERE id = 1
            ON CONFLICT(id) DO UPDATE SET
                status = excluded.status,
                updated_at_utc = excluded.updated_at_utc;
            """,
            ("$now", now));
        transaction.Commit();
    }

    public void ReplaceStoryQuestImport(StoryQuestImportData import)
    {
        ArgumentNullException.ThrowIfNull(import);

        using SqliteConnection connection = CreateConnection();
        connection.Open();
        EnableForeignKeys(connection);
        using SqliteTransaction transaction = connection.BeginTransaction();
        ClearStoryQuest(connection, transaction);
        ReplaceStoryQuest(connection, transaction, import.Dataset);
        ExecuteParameterized(
            connection,
            transaction,
            """
            UPDATE workspace_snapshot
            SET story_quest_workbook_path = $questPath,
                story_step_workbook_path = $stepPath,
                story_turn_workbook_path = $turnPath,
                story_quest_source_hash = $questHash,
                story_step_source_hash = $stepHash,
                story_turn_source_hash = $turnHash,
                imported_at_utc = $importedAtUtc
            WHERE id = 1;
            """,
            ("$questPath", import.QuestWorkbookPath),
            ("$stepPath", import.StepWorkbookPath),
            ("$turnPath", import.TurnWorkbookPath),
            ("$questHash", import.QuestSourceHash),
            ("$stepHash", import.StepSourceHash),
            ("$turnHash", import.TurnSourceHash),
            ("$importedAtUtc", DateTimeOffset.UtcNow.ToString("O")));
        ExecuteParameterized(
            connection,
            transaction,
            "DELETE FROM change_set WHERE id = $id;",
            ("$id", "story"));
        transaction.Commit();
    }

    public WorkspaceImportResult? ReadWorkspace()
    {
        using SqliteConnection connection = CreateConnection();
        connection.Open();

        using SqliteCommand snapshotCommand = connection.CreateCommand();
        snapshotCommand.CommandText =
            """
            SELECT
                unity_project_root,
                daily_quest_workbook_path,
                source_hash,
                weekly_quest_workbook_path,
                weekly_quest_source_hash,
                quest_chest_reward_workbook_path,
                quest_chest_reward_source_hash,
                story_quest_workbook_path,
                story_step_workbook_path,
                story_turn_workbook_path,
                story_quest_source_hash,
                story_step_source_hash,
                story_turn_source_hash
            FROM workspace_snapshot
            WHERE id = 1;
            """;
        using SqliteDataReader snapshotReader = snapshotCommand.ExecuteReader();
        if (!snapshotReader.Read())
        {
            return null;
        }

        string projectRoot = snapshotReader.GetString(0);
        string workbookPath = snapshotReader.GetString(1);
        string sourceHash = snapshotReader.GetString(2);
        string weeklyQuestPath = snapshotReader.GetString(3);
        string weeklyQuestHash = snapshotReader.GetString(4);
        string chestRewardPath = snapshotReader.GetString(5);
        string chestRewardHash = snapshotReader.GetString(6);
        string storyQuestPath = snapshotReader.GetString(7);
        string storyStepPath = snapshotReader.GetString(8);
        string storyTurnPath = snapshotReader.GetString(9);
        string storyQuestHash = snapshotReader.GetString(10);
        string storyStepHash = snapshotReader.GetString(11);
        string storyTurnHash = snapshotReader.GetString(12);
        snapshotReader.Close();

        var quests = new List<DailyQuestDefinition>();
        using SqliteCommand questCommand = connection.CreateCommand();
        questCommand.CommandText =
            """
            SELECT id, type_code, type_value, target_value, sort_order, description
            FROM daily_quest
            ORDER BY sort_order, id;
            """;
        using SqliteDataReader questReader = questCommand.ExecuteReader();
        while (questReader.Read())
        {
            int questId = questReader.GetInt32(0);
            quests.Add(
                new DailyQuestDefinition(
                    questId,
                    new QuestTypeValue(questReader.GetString(1), questReader.GetInt32(2)),
                    questReader.GetInt32(3),
                    ReadRewards(connection, "daily_quest_reward", "quest_id", questId),
                    questReader.GetInt32(4),
                    questReader.GetString(5)));
        }
        questReader.Close();

        var weeklyQuests = new List<WeeklyQuestDefinition>();
        using (SqliteCommand weeklyCommand = connection.CreateCommand())
        {
            weeklyCommand.CommandText =
                "SELECT id, type_code, type_value, target_value, sort_order, description FROM weekly_quest ORDER BY sort_order, id;";
            using SqliteDataReader weeklyReader = weeklyCommand.ExecuteReader();
            while (weeklyReader.Read())
            {
                int questId = weeklyReader.GetInt32(0);
                weeklyQuests.Add(
                    new WeeklyQuestDefinition(
                        questId,
                        new QuestTypeValue(weeklyReader.GetString(1), weeklyReader.GetInt32(2)),
                        weeklyReader.GetInt32(3),
                        ReadRewards(connection, "weekly_quest_reward", "quest_id", questId),
                        weeklyReader.GetInt32(4),
                        weeklyReader.GetString(5)));
            }
        }

        var chestRewards = new List<QuestChestRewardDefinition>();
        using (SqliteCommand chestCommand = connection.CreateCommand())
        {
            chestCommand.CommandText =
                "SELECT id, required_count, chest_level FROM quest_chest_reward ORDER BY chest_level, id;";
            using SqliteDataReader chestReader = chestCommand.ExecuteReader();
            while (chestReader.Read())
            {
                int chestId = chestReader.GetInt32(0);
                chestRewards.Add(
                    new QuestChestRewardDefinition(
                        chestId,
                        chestReader.GetInt32(1),
                        ReadRewards(
                            connection,
                            "quest_chest_reward_item",
                            "chest_id",
                            chestId),
                        chestReader.GetInt32(2)));
            }
        }

        var result = new WorkspaceImportResult(projectRoot, workbookPath, sourceHash, quests)
        {
            WeeklyQuestWorkbookPath = weeklyQuestPath,
            WeeklyQuestSourceHash = weeklyQuestHash,
            WeeklyQuests = weeklyQuests,
            QuestChestRewardWorkbookPath = chestRewardPath,
            QuestChestRewardSourceHash = chestRewardHash,
            QuestChestRewards = chestRewards,
            GameData = ReadGameDataCatalog(connection),
        };
        if (!string.IsNullOrEmpty(storyQuestPath))
        {
            result = result with
            {
                StoryQuest = new StoryQuestImportData(
                    storyQuestPath,
                    storyStepPath,
                    storyTurnPath,
                    storyQuestHash,
                    storyStepHash,
                    storyTurnHash,
                    ReadStoryQuest(connection)),
            };
        }

        return result;
    }

    private static GameDataCatalog ReadGameDataCatalog(SqliteConnection connection)
    {
        var tables = new List<GameDataTable>();
        using SqliteCommand tableCommand = connection.CreateCommand();
        tableCommand.CommandText =
            "SELECT table_key, display_name, category, workbook_path, source_hash, worksheet_name, fields_json FROM game_data_table ORDER BY category, table_key;";
        using SqliteDataReader tableReader = tableCommand.ExecuteReader();
        while (tableReader.Read())
        {
            string tableKey = tableReader.GetString(0);
            GameDataFieldDefinition[] fields = JsonSerializer.Deserialize<GameDataFieldDefinition[]>(
                    tableReader.GetString(6))
                ?? [];
            tables.Add(
                new GameDataTable(
                    tableKey,
                    tableReader.GetString(1),
                    tableReader.GetString(2),
                    tableReader.GetString(3),
                    tableReader.GetString(4),
                    tableReader.GetString(5),
                    fields,
                    ReadGameDataRecords(connection, tableKey)));
        }

        return new GameDataCatalog(tables);
    }

    private static List<GameDataRecord> ReadGameDataRecords(
        SqliteConnection connection,
        string tableKey)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT record_id, fields_json, source_order, source_row FROM game_data_record WHERE table_key = $tableKey ORDER BY source_order, record_id;";
        command.Parameters.AddWithValue("$tableKey", tableKey);
        using SqliteDataReader reader = command.ExecuteReader();
        var records = new List<GameDataRecord>();
        while (reader.Read())
        {
            Dictionary<string, string[]> serializedFields =
                JsonSerializer.Deserialize<Dictionary<string, string[]>>(reader.GetString(1))
                ?? [];
            records.Add(
                new GameDataRecord(
                    reader.GetInt32(0),
                    serializedFields.ToDictionary(
                        field => field.Key,
                        field => (IReadOnlyList<string>)field.Value),
                    reader.GetInt32(2),
                    reader.GetInt32(3)));
        }

        return records;
    }

    private static void InsertGameDataCatalog(
        SqliteConnection connection,
        SqliteTransaction transaction,
        GameDataCatalog catalog)
    {
        foreach (GameDataTable table in catalog.Tables)
        {
            ExecuteParameterized(
                connection,
                transaction,
                "INSERT INTO game_data_table (table_key, display_name, category, workbook_path, source_hash, worksheet_name, fields_json) VALUES ($key, $name, $category, $path, $hash, $sheet, $fields);",
                ("$key", table.Key),
                ("$name", table.DisplayName),
                ("$category", table.Category),
                ("$path", table.WorkbookPath),
                ("$hash", table.SourceHash),
                ("$sheet", table.WorksheetName),
                ("$fields", JsonSerializer.Serialize(table.Fields)));
            InsertGameDataRecords(connection, transaction, table.Key, table.Records);
        }
    }

    private static void InsertGameDataRecords(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableKey,
        IReadOnlyList<GameDataRecord> records)
    {
        foreach (GameDataRecord record in records)
        {
            ExecuteParameterized(
                connection,
                transaction,
                "INSERT INTO game_data_record (table_key, record_id, fields_json, source_order, source_row) VALUES ($tableKey, $recordId, $fields, $sourceOrder, $sourceRow);",
                ("$tableKey", tableKey),
                ("$recordId", record.Id),
                ("$fields", JsonSerializer.Serialize(record.Fields)),
                ("$sourceOrder", record.SourceOrder),
                ("$sourceRow", record.SourceRow));
        }
    }

    private static void InsertRecurringQuest(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string questTable,
        string rewardTable,
        int id,
        QuestTypeValue type,
        int targetValue,
        IReadOnlyList<QuestReward> rewards,
        int sortOrder,
        string description)
    {
        ExecuteParameterized(
            connection,
            transaction,
            $"INSERT INTO {questTable} (id, type_code, type_value, target_value, sort_order, description) VALUES ($id, $typeCode, $typeValue, $targetValue, $sortOrder, $description);",
            ("$id", id),
            ("$typeCode", type.Code),
            ("$typeValue", type.LegacyValue),
            ("$targetValue", targetValue),
            ("$sortOrder", sortOrder),
            ("$description", description));
        InsertRewards(connection, transaction, rewardTable, "quest_id", id, rewards);
    }

    private bool TryCreateRecurringQuestDraft(
        string questTable,
        string rewardTable,
        string changeSetId,
        int id,
        QuestTypeValue type,
        int targetValue,
        IReadOnlyList<QuestReward> rewards,
        int sortOrder,
        string description)
    {
        using SqliteConnection connection = CreateConnection();
        connection.Open();
        EnableForeignKeys(connection);
        using SqliteTransaction transaction = connection.BeginTransaction();
        int changed = ExecuteParameterizedWithResult(
            connection,
            transaction,
            $"INSERT OR IGNORE INTO {questTable} (id, type_code, type_value, target_value, sort_order, description) VALUES ($id, $typeCode, $typeValue, $targetValue, $sortOrder, $description);",
            ("$id", id),
            ("$typeCode", type.Code),
            ("$typeValue", type.LegacyValue),
            ("$targetValue", targetValue),
            ("$sortOrder", sortOrder),
            ("$description", description));
        if (changed == 0)
        {
            transaction.Rollback();
            return false;
        }

        InsertRewards(connection, transaction, rewardTable, "quest_id", id, rewards);
        MarkDirty(connection, transaction, changeSetId);
        transaction.Commit();
        return true;
    }

    private bool TryDeleteRecurringQuestDraft(string table, string changeSetId, int id)
    {
        using SqliteConnection connection = CreateConnection();
        connection.Open();
        EnableForeignKeys(connection);
        using SqliteTransaction transaction = connection.BeginTransaction();
        int changed = ExecuteParameterizedWithResult(
            connection,
            transaction,
            $"DELETE FROM {table} WHERE id = $id;",
            ("$id", id));
        if (changed == 0)
        {
            transaction.Rollback();
            return false;
        }

        MarkDirty(connection, transaction, changeSetId);
        transaction.Commit();
        return true;
    }

    private static void InsertRewards(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string rewardTable,
        string ownerColumn,
        int ownerId,
        IReadOnlyList<QuestReward> rewards)
    {
        for (int index = 0; index < rewards.Count; index++)
        {
            QuestReward reward = rewards[index];
            ExecuteParameterized(
                connection,
                transaction,
                $"INSERT INTO {rewardTable} ({ownerColumn}, reward_order, reward_type, config_id, amount) VALUES ($ownerId, $order, $type, $configId, $amount);",
                ("$ownerId", ownerId),
                ("$order", index),
                ("$type", reward.Type),
                ("$configId", reward.ConfigId),
                ("$amount", reward.Amount));
        }
    }

    private static void MarkDirty(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string changeSetId)
    {
        string now = DateTimeOffset.UtcNow.ToString("O");
        ExecuteParameterized(
            connection,
            transaction,
            """
            INSERT INTO change_set (id, base_source_hash, status, created_at_utc, updated_at_utc)
            SELECT $id, source_hash, 'dirty', $now, $now
            FROM workspace_snapshot
            WHERE id = 1
            ON CONFLICT(id) DO UPDATE SET
                status = excluded.status,
                updated_at_utc = excluded.updated_at_utc;
            """,
            ("$id", changeSetId),
            ("$now", now));
    }

    private static void ReplaceStoryQuest(
        SqliteConnection connection,
        SqliteTransaction transaction,
        StoryQuestDataset dataset)
    {
        foreach (StoryQuestDefinition quest in dataset.Quests)
        {
            ExecuteParameterized(
                connection,
                transaction,
                "INSERT INTO story_quest (id, name, description, is_repeatable, step_auto_accept) VALUES ($id, $name, $description, $repeatable, $autoAccept);",
                ("$id", quest.Id),
                ("$name", quest.Name),
                ("$description", quest.Description),
                ("$repeatable", quest.IsRepeatable ? 1 : 0),
                ("$autoAccept", quest.StepAutoAccept ? 1 : 0));
            for (int index = 0; index < quest.Prerequisites.Count; index++)
            {
                StoryQuestPrerequisite prerequisite = quest.Prerequisites[index];
                ExecuteParameterized(
                    connection,
                    transaction,
                    "INSERT INTO story_quest_prerequisite (quest_id, prerequisite_order, prerequisite_type, prerequisite_quest_id) VALUES ($questId, $order, $type, $value);",
                    ("$questId", quest.Id),
                    ("$order", index),
                    ("$type", prerequisite.Type.ToString()),
                    ("$value", prerequisite.Value));
            }
        }

        foreach (StoryQuestStepDefinition step in dataset.Steps)
        {
            ExecuteParameterized(
                connection,
                transaction,
                """
                INSERT INTO story_quest_step (
                    id, quest_id, step_order, npc_id, accept_dialogue_id,
                    submit_dialogue_id, processing_dialogue_id, condition_type,
                    condition_value, description, condition_params
                ) VALUES (
                    $id, $questId, $stepOrder, $npcId, $acceptDialogueId,
                    $submitDialogueId, $processingDialogueId, $conditionType,
                    $conditionValue, $description, $conditionParams
                );
                """,
                ("$id", step.Id),
                ("$questId", step.QuestId),
                ("$stepOrder", step.StepOrder),
                ("$npcId", step.NpcId),
                ("$acceptDialogueId", step.AcceptDialogueId),
                ("$submitDialogueId", step.SubmitDialogueId),
                ("$processingDialogueId", step.ProcessingDialogueId),
                ("$conditionType", (int)step.ConditionType),
                ("$conditionValue", step.ConditionValue),
                ("$description", step.Description),
                ("$conditionParams", string.Join('|', step.ConditionParams ?? [])));
            for (int index = 0; index < step.Rewards.Count; index++)
            {
                QuestReward reward = step.Rewards[index];
                ExecuteParameterized(
                    connection,
                    transaction,
                    "INSERT INTO story_quest_step_reward (step_id, reward_order, reward_type, config_id, amount) VALUES ($stepId, $order, $type, $configId, $amount);",
                    ("$stepId", step.Id),
                    ("$order", index),
                    ("$type", reward.Type),
                    ("$configId", reward.ConfigId),
                    ("$amount", reward.Amount));
            }
        }

        foreach (StoryConversationTurnDefinition turn in dataset.Turns)
        {
            ExecuteParameterized(
                connection,
                transaction,
                "INSERT INTO story_conversation_turn (id, dialogue_id, order_index, speaker_type, speaker_name, speaker_image, text) VALUES ($id, $dialogueId, $orderIndex, $speakerType, $speakerName, $speakerImage, $text);",
                ("$id", turn.Id),
                ("$dialogueId", turn.DialogueId),
                ("$orderIndex", turn.OrderIndex),
                ("$speakerType", turn.SpeakerType),
                ("$speakerName", turn.SpeakerName),
                ("$speakerImage", turn.SpeakerImage),
                ("$text", turn.Text));
        }
    }

    private static void ClearStoryQuest(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        Execute(connection, transaction, "DELETE FROM story_conversation_turn;");
        Execute(connection, transaction, "DELETE FROM story_quest_step_reward;");
        Execute(connection, transaction, "DELETE FROM story_quest_step;");
        Execute(connection, transaction, "DELETE FROM story_quest;");
    }

    private static StoryQuestDataset ReadStoryQuest(SqliteConnection connection)
    {
        var quests = new List<StoryQuestDefinition>();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, name, description, is_repeatable, step_auto_accept FROM story_quest ORDER BY id;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                int questId = reader.GetInt32(0);
                quests.Add(
                    new StoryQuestDefinition(
                        questId,
                        reader.GetString(1),
                        reader.GetString(2),
                        reader.GetInt32(3) != 0,
                        reader.GetInt32(4) != 0,
                        ReadStoryPrerequisites(
                            connection,
                            questId)));
            }
        }

        var steps = new List<StoryQuestStepDefinition>();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, quest_id, step_order, npc_id, accept_dialogue_id, submit_dialogue_id, processing_dialogue_id, condition_type, condition_value, description, condition_params FROM story_quest_step ORDER BY quest_id, step_order, id;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                int stepId = reader.GetInt32(0);
                steps.Add(
                    new StoryQuestStepDefinition(
                        stepId,
                        reader.GetInt32(1),
                        reader.GetInt32(2),
                        reader.GetInt32(3),
                        reader.GetInt32(4),
                        reader.GetInt32(5),
                        reader.GetInt32(6),
                        (StoryQuestConditionType)reader.GetInt32(7),
                        reader.GetInt32(8),
                        ReadStoryRewards(connection, stepId),
                        reader.GetString(9),
                        ParsePackedInts(reader.GetString(10))));
            }
        }

        var turns = new List<StoryConversationTurnDefinition>();
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, dialogue_id, order_index, speaker_type, speaker_name, speaker_image, text FROM story_conversation_turn ORDER BY dialogue_id, order_index, id;";
            using SqliteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                turns.Add(
                    new StoryConversationTurnDefinition(
                        reader.GetInt32(0),
                        reader.GetInt32(1),
                        reader.GetInt32(2),
                        reader.GetInt32(3),
                        reader.GetString(4),
                        reader.GetString(5),
                        reader.GetString(6)));
            }
        }

        return new StoryQuestDataset(quests, steps, turns);
    }

    private static List<int> ReadInts(SqliteConnection connection, string sql, int id)
    {
        var result = new List<int>();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", id);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(reader.GetInt32(0));
        }

        return result;
    }

    private static List<StoryQuestPrerequisite> ReadStoryPrerequisites(
        SqliteConnection connection,
        int questId)
    {
        var result = new List<StoryQuestPrerequisite>();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT prerequisite_type, prerequisite_quest_id FROM story_quest_prerequisite WHERE quest_id = $id ORDER BY prerequisite_order;";
        command.Parameters.AddWithValue("$id", questId);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!Enum.TryParse(reader.GetString(0), out StoryQuestPrerequisiteType type)
                || !Enum.IsDefined(type))
            {
                throw new InvalidDataException($"未知剧情任务前置条件类型：{reader.GetString(0)}。");
            }

            result.Add(new StoryQuestPrerequisite(type, reader.GetInt32(1)));
        }

        return result;
    }

    private static List<int> ParsePackedInts(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value.Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.Parse(part, CultureInfo.InvariantCulture))
            .ToList();
    }

    private static List<QuestReward> ReadStoryRewards(SqliteConnection connection, int stepId)
    {
        var result = new List<QuestReward>();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT reward_type, config_id, amount FROM story_quest_step_reward WHERE step_id = $id ORDER BY reward_order;";
        command.Parameters.AddWithValue("$id", stepId);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new QuestReward(reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2)));
        }

        return result;
    }

    private SqliteConnection CreateConnection()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
        }.ToString();
        return new SqliteConnection(connectionString);
    }

    private static List<QuestReward> ReadRewards(
        SqliteConnection connection,
        string rewardTable,
        string ownerColumn,
        int ownerId)
    {
        var rewards = new List<QuestReward>();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT reward_type, config_id, amount
            FROM {{rewardTable}}
            WHERE {{ownerColumn}} = $ownerId
            ORDER BY reward_order;
            """;
        command.CommandText = command.CommandText
            .Replace("{{rewardTable}}", rewardTable, StringComparison.Ordinal)
            .Replace("{{ownerColumn}}", ownerColumn, StringComparison.Ordinal);
        command.Parameters.AddWithValue("$ownerId", ownerId);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rewards.Add(
                new QuestReward(
                    reader.GetString(0),
                    reader.GetInt32(1),
                    reader.GetInt32(2)));
        }

        return rewards;
    }

    private static void EnableForeignKeys(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();
    }

    private static void Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void ExecuteParameterized(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }

    private static int ExecuteParameterizedWithResult(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command.ExecuteNonQuery();
    }

    private static void EnsureColumn(
        SqliteConnection connection,
        string tableName,
        string columnName,
        string definition)
    {
        using SqliteCommand infoCommand = connection.CreateCommand();
        infoCommand.CommandText = $"PRAGMA table_info({tableName});";
        using SqliteDataReader reader = infoCommand.ExecuteReader();
        bool exists = false;
        while (reader.Read())
        {
            if (StringComparer.Ordinal.Equals(reader.GetString(1), columnName))
            {
                exists = true;
                break;
            }
        }

        reader.Close();
        if (exists)
        {
            return;
        }

        using SqliteCommand alterCommand = connection.CreateCommand();
        alterCommand.CommandText =
            $"ALTER TABLE {tableName} ADD COLUMN {columnName} {definition};";
        alterCommand.ExecuteNonQuery();
    }
}
