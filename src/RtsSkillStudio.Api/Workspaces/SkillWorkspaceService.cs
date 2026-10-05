using RtsSkillStudio.Agent.Workspaces;
using TianshuDM.Application.HeroAuthoring;
using TianshuDM.Domain.GameData;
using TianshuDM.Domain.HeroAuthoring;
using TianshuDM.Infrastructure.Excel;
using TianshuDM.Infrastructure.Excel.HeroAuthoring;

namespace RtsSkillStudio.Api.Workspaces;

public sealed class SkillWorkspaceService(
    SkillWorkspaceOptions options,
    ILogger<SkillWorkspaceService> logger
)
{
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private WorkspaceSnapshot? _snapshot;

    public async Task<SkillWorkspaceStatus> GetStatusAsync(
        CancellationToken cancellationToken
    )
    {
        var errors = new List<string>();
        bool rootExists = Directory.Exists(options.ExcelDataRoot);
        bool schemaExists = File.Exists(options.HeroAuthoringSchemaPath);

        if (!rootExists)
        {
            errors.Add($"Excel 数据目录不存在：{options.ExcelDataRoot}");
        }

        if (!schemaExists)
        {
            errors.Add($"语义 Schema 不存在：{options.HeroAuthoringSchemaPath}");
        }

        if (errors.Count > 0)
        {
            return new SkillWorkspaceStatus(
                false,
                options.ExcelDataRoot,
                options.HeroAuthoringSchemaPath,
                rootExists,
                schemaExists,
                null,
                0,
                0,
                0,
                0,
                errors
            );
        }

        try
        {
            var snapshot = await LoadAsync(cancellationToken);
            return new SkillWorkspaceStatus(
                true,
                options.ExcelDataRoot,
                options.HeroAuthoringSchemaPath,
                true,
                true,
                snapshot.Revision,
                snapshot.Catalog.Tables.Count,
                snapshot.Graph.Nodes.Count,
                snapshot.Graph.Edges.Count,
                snapshot.Catalog.Table("skill").Records.Count,
                []
            );
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to load skill workspace.");
            return new SkillWorkspaceStatus(
                false,
                options.ExcelDataRoot,
                options.HeroAuthoringSchemaPath,
                true,
                true,
                null,
                0,
                0,
                0,
                0,
                [exception.Message]
            );
        }
    }

    public async Task<IReadOnlyList<SkillSummary>> ListSkillsAsync(
        string? query,
        int limit,
        CancellationToken cancellationToken
    )
    {
        if (limit is < 1 or > 500)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                "技能数量必须在 1 到 500 之间。"
            );
        }

        var snapshot = await LoadAsync(cancellationToken);
        GameDataTable skills = snapshot.Catalog.Table("skill");
        Dictionary<string, int> incoming = snapshot
            .Graph.Edges.GroupBy(edge => edge.Target, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        Dictionary<string, int> outgoing = snapshot
            .Graph.Edges.GroupBy(edge => edge.Source, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        string term = query?.Trim() ?? "";

        return skills
            .Records.Select(record =>
            {
                string key = $"TbSkill:{record.Id}";
                string label = Title(skills, record);
                return new SkillSummary(
                    record.Id,
                    label,
                    BuildSkillSummary(record),
                    record.SourceRow,
                    incoming.GetValueOrDefault(key),
                    outgoing.GetValueOrDefault(key)
                );
            })
            .Where(item =>
                term.Length == 0
                || item.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    .Contains(term, StringComparison.OrdinalIgnoreCase)
                || item.Label.Contains(term, StringComparison.OrdinalIgnoreCase)
                || item.Summary.Contains(term, StringComparison.OrdinalIgnoreCase)
            )
            .OrderBy(item => item.Id)
            .Take(limit)
            .ToArray();
    }

    public async Task<SkillChainSnapshot> GetSkillChainAsync(
        int skillId,
        int depth,
        CancellationToken cancellationToken
    )
    {
        if (depth is < 1 or > 12)
        {
            throw new ArgumentOutOfRangeException(
                nameof(depth),
                "链路深度必须在 1 到 12 之间。"
            );
        }

        var snapshot = await LoadAsync(cancellationToken);
        HeroAuthoringGraph graph = snapshot.Projector.ProjectSkillBehavior(
            snapshot.Catalog,
            skillId,
            depth
        );

        return new SkillChainSnapshot(
            snapshot.Revision,
            skillId,
            graph.FocusKey,
            graph.Nodes.Select(node => new SkillChainNode(
                node.Key,
                node.Namespace,
                node.LegacyId,
                node.Label,
                node.Kind,
                node.TableKey,
                node.IsVirtual,
                node.IsCode,
                node.IsMissing,
                node.IsFocus,
                node.SourceRow,
                node.Fields
            )).ToArray(),
            graph.Edges.Select(edge => new SkillChainEdge(
                edge.Id,
                edge.Source,
                edge.Target,
                edge.Role,
                edge.Label,
                edge.Detail,
                edge.Derived,
                edge.SourceField,
                edge.ParameterIndex
            )).ToArray()
        );
    }

    public async Task<WriteSmokeTestResult> RunWriteSmokeTestAsync(
        CancellationToken cancellationToken
    )
    {
        string sourceDataRoot = Path.GetFullPath(options.ExcelDataRoot);
        string outputRoot = Path.Combine(
            Path.GetFullPath(options.WriteTestRoot),
            $"write-smoke-{DateTime.UtcNow:yyyyMMdd-HHmmss}"
        );
        string outputDataRoot = Path.Combine(
            outputRoot,
            "Unity",
            "Assets",
            "Config",
            "Excel",
            "Datas"
        );

        return await Task.Run(
            () =>
            {
                Directory.CreateDirectory(outputDataRoot);
                CopyTree(sourceDataRoot, outputDataRoot);

                var workbookReader = new GameDataWorkbookReader();
                var catalogReader = new UnitGameDataCatalogReader(workbookReader);
                GameDataCatalog originalCatalog = catalogReader.Read(outputDataRoot);
                GameDataTable originalSkills = originalCatalog.Table("skill");
                var writer = new GameDataWorkbookWriter();
                writer.Write(originalSkills.WorkbookPath, originalSkills);

                GameDataTable rereadSkills = workbookReader.Read(
                    new GameDataTableSource(
                        originalSkills.Key,
                        originalSkills.DisplayName,
                        originalSkills.Category,
                        "Skill/Skill.xlsx",
                        originalSkills.WorkbookPath
                    )
                );
                bool fieldsMatch = TablesMatch(originalSkills, rereadSkills);

                logger.LogInformation(
                    "Write smoke test completed for {Table}: {Records} records, fieldsMatch={FieldsMatch}, output={Output}.",
                    originalSkills.Key,
                    originalSkills.Records.Count,
                    fieldsMatch,
                    originalSkills.WorkbookPath
                );

                return new WriteSmokeTestResult(
                    "completed",
                    originalSkills.Key,
                    originalSkills.WorkbookPath,
                    originalSkills.Records.Count,
                    rereadSkills.Records.Count,
                    fieldsMatch,
                    originalSkills.SourceHash,
                    rereadSkills.SourceHash
                );
            },
            cancellationToken
        );
    }

    private async Task<WorkspaceSnapshot> LoadAsync(
        CancellationToken cancellationToken
    )
    {
        WorkspaceSnapshot? current = _snapshot;
        if (current is not null)
        {
            return current;
        }

        await _loadGate.WaitAsync(cancellationToken);
        try
        {
            current = _snapshot;
            if (current is not null)
            {
                return current;
            }

            current = await Task.Run(
                () =>
                {
                    return CreateSnapshotFromSharedCopy();
                },
                cancellationToken
            );
            _snapshot = current;
            logger.LogInformation(
                "Loaded skill workspace: {Tables} tables, {Nodes} nodes, {Edges} edges, revision {Revision}.",
                current.Catalog.Tables.Count,
                current.Graph.Nodes.Count,
                current.Graph.Edges.Count,
                current.Revision
            );
            return current;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    private WorkspaceSnapshot CreateSnapshotFromSharedCopy()
    {
        string sourceDataRoot = Path.GetFullPath(options.ExcelDataRoot);
        DirectoryInfo? excelRoot = Directory.GetParent(sourceDataRoot);
        DirectoryInfo? configRoot = excelRoot?.Parent;
        DirectoryInfo? assetsRoot = configRoot?.Parent;
        if (
            excelRoot is null
            || configRoot is null
            || assetsRoot is null
        )
        {
            throw new InvalidOperationException(
                $"无法推导 Excel 工作区父目录：{sourceDataRoot}"
            );
        }

        string snapshotRoot = Path.Combine(
            Path.GetTempPath(),
            "RtsSkillStudio",
            Guid.NewGuid().ToString("N")
        );
        string snapshotAssetsRoot = Path.Combine(snapshotRoot, "Unity", "Assets");
        string snapshotConfigRoot = Path.Combine(snapshotAssetsRoot, "Config");
        string snapshotExcelRoot = Path.Combine(snapshotConfigRoot, "Excel");
        string snapshotDataRoot = Path.Combine(snapshotExcelRoot, "Datas");

        try
        {
            CopyTree(sourceDataRoot, snapshotDataRoot);

            string sourceBuiltin = Path.Combine(excelRoot.FullName, "Defines");
            if (Directory.Exists(sourceBuiltin))
            {
                CopyTree(
                    sourceBuiltin,
                    Path.Combine(snapshotExcelRoot, "Defines")
                );
            }

            string sourceGeneratedEnumRoot = Path.Combine(
                assetsRoot.FullName,
                "Scripts",
                "Model",
                "Generate",
                "Client",
                "Config"
            );
            if (Directory.Exists(sourceGeneratedEnumRoot))
            {
                CopyTree(
                    sourceGeneratedEnumRoot,
                    Path.Combine(
                        snapshotAssetsRoot,
                        "Scripts",
                        "Model",
                        "Generate",
                        "Client",
                        "Config"
                    )
                );
            }

            var workbookReader = new GameDataWorkbookReader();
            var catalogReader = new UnitGameDataCatalogReader(workbookReader);
            GameDataCatalog catalog = catalogReader.Read(snapshotDataRoot);
            var schemaSource = new HeroAuthoringSchemaJsonReader(
                options.HeroAuthoringSchemaPath
            );
            var projector = new HeroAuthoringGraphProjector(schemaSource);
            HeroAuthoringGraph graph = projector.ProjectCatalog(catalog);
            string revision = HeroAuthoringCatalogRevision.Compute(catalog);
            return new WorkspaceSnapshot(catalog, revision, projector, graph);
        }
        finally
        {
            DeleteTemporaryDirectory(snapshotRoot);
        }
    }

    private static void CopyTree(string sourceRoot, string destinationRoot)
    {
        Directory.CreateDirectory(destinationRoot);
        foreach (string sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            string relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
            string destinationPath = Path.Combine(destinationRoot, relativePath);
            string? destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            using var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete
            );
            using var destination = new FileStream(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None
            );
            source.CopyTo(destination);
        }
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string tempRoot = Path.GetFullPath(Path.GetTempPath());
        if (
            !fullPath.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase)
            || !fullPath.Contains(
                $"{Path.DirectorySeparatorChar}RtsSkillStudio{Path.DirectorySeparatorChar}",
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            throw new InvalidOperationException(
                $"拒绝清理非 Studio 临时目录：{fullPath}"
            );
        }

        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, true);
        }
    }

    private static bool TablesMatch(GameDataTable left, GameDataTable right)
    {
        if (left.Records.Count != right.Records.Count)
        {
            return false;
        }

        Dictionary<int, GameDataRecord> rightById = right.Records.ToDictionary(
            record => record.Id
        );
        string[] comparedFields = left
            .Fields.Select(field => field.Key)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        foreach (GameDataRecord leftRecord in left.Records)
        {
            if (!rightById.TryGetValue(leftRecord.Id, out GameDataRecord? rightRecord))
            {
                return false;
            }

            foreach (string field in comparedFields)
            {
                IReadOnlyList<string> leftValues =
                    leftRecord.Fields.GetValueOrDefault(field) ?? [];
                IReadOnlyList<string> rightValues =
                    rightRecord.Fields.GetValueOrDefault(field) ?? [];
                if (!leftValues.SequenceEqual(rightValues, StringComparer.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static string Title(GameDataTable table, GameDataRecord record)
    {
        foreach (string key in new[] { "name", "Name", "remark", "desc", "Desc" })
        {
            string? value = Values(record, key).FirstOrDefault(Configured);
            if (value is not null)
            {
                return value;
            }
        }

        return $"{table.DisplayName} {record.Id}";
    }

    private static string BuildSkillSummary(GameDataRecord record)
    {
        var parts = new List<string>();
        AddPrefixedInteger(parts, Values(record, "effect_group_id"), "主要效果组");
        AddPrefixedInteger(parts, Values(record, "search_target"), "目标搜索");
        AddPrefixedMilliseconds(parts, Values(record, "cooldown", "cooldown_ms", "cd"), "冷却");
        AddPrefixedMilliseconds(parts, Values(record, "duration", "duration_ms"), "持续");
        return parts.Count > 0 ? string.Join(" · ", parts) : "未提取到摘要字段";
    }

    private static void AddPrefixedInteger(
        ICollection<string> parts,
        IReadOnlyList<string> values,
        string label
    )
    {
        string? value = values.FirstOrDefault(Configured);
        if (value is not null)
        {
            parts.Add($"{label} {value}");
        }
    }

    private static void AddPrefixedMilliseconds(
        ICollection<string> parts,
        IReadOnlyList<string> values,
        string label
    )
    {
        string? raw = values.FirstOrDefault(Configured);
        if (raw is null)
        {
            return;
        }

        if (
            decimal.TryParse(
                raw,
                System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture,
                out decimal milliseconds
            )
        )
        {
            parts.Add($"{label} {milliseconds / 1000m:0.###} 秒");
        }
    }

    private static IReadOnlyList<string> Values(
        GameDataRecord record,
        params string[] keys
    )
    {
        foreach (string key in keys)
        {
            IReadOnlyList<string> values =
                record.Fields.FirstOrDefault(
                    pair => string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase)
                ).Value ?? [];
            if (values.Any(Configured))
            {
                return values;
            }
        }

        return [];
    }

    private static bool Configured(string value) =>
        !string.IsNullOrWhiteSpace(value) && value != "0";

    private sealed record WorkspaceSnapshot(
        GameDataCatalog Catalog,
        string Revision,
        HeroAuthoringGraphProjector Projector,
        HeroAuthoringGraph Graph
    );
}
