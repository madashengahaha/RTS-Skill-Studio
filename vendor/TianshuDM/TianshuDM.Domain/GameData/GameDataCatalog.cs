using System.Diagnostics.CodeAnalysis;

namespace TianshuDM.Domain.GameData;

[SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "Integer is part of the stable API field-kind contract.")]
public enum GameDataFieldKind
{
    Text,
    Integer,
    Boolean,
    Enum,
    Reference,
    List,
    Map,
    DelimitedList,
}

public sealed record GameDataOption(
    string Value,
    string Label,
    string? Code = null,
    int? LegacyValue = null);

public sealed record GameDataFieldDefinition(
    string Key,
    string Label,
    GameDataFieldKind Kind,
    string RawType,
    int StartColumn,
    int ColumnCount,
    bool Required,
    string? ReferenceTable,
    IReadOnlyList<GameDataOption> Options,
    string? PackingSeparator,
    bool AllowsMultipleEnumValues = false);

public sealed record GameDataRecord(
    int Id,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Fields,
    int SourceOrder = 0,
    int SourceRow = 0);

public sealed record GameDataTable(
    string Key,
    string DisplayName,
    string Category,
    string WorkbookPath,
    string SourceHash,
    string WorksheetName,
    IReadOnlyList<GameDataFieldDefinition> Fields,
    IReadOnlyList<GameDataRecord> Records)
{
    public GameDataRecord Record(int id)
    {
        return Records.FirstOrDefault(record => record.Id == id)
               ?? throw new KeyNotFoundException($"{DisplayName}中不存在编号 {id}。");
    }
}

public sealed record GameDataCatalog(IReadOnlyList<GameDataTable> Tables)
{
    public static GameDataCatalog Empty { get; } = new([]);

    public GameDataTable Table(string key)
    {
        return Tables.FirstOrDefault(
                   table => StringComparer.OrdinalIgnoreCase.Equals(table.Key, key))
               ?? throw new KeyNotFoundException($"不存在数据表“{key}”。");
    }

    public GameDataCatalog ReplaceTable(GameDataTable replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (!Tables.Any(table => StringComparer.OrdinalIgnoreCase.Equals(table.Key, replacement.Key)))
        {
            throw new KeyNotFoundException($"不存在数据表“{replacement.Key}”。");
        }

        return new GameDataCatalog(
            Tables.Select(
                    table => StringComparer.OrdinalIgnoreCase.Equals(table.Key, replacement.Key)
                        ? replacement
                        : table)
                .ToArray());
    }
}

public sealed record GameDataTableSource(
    string Key,
    string DisplayName,
    string Category,
    string RelativeWorkbookPath,
    string WorkbookPath);
