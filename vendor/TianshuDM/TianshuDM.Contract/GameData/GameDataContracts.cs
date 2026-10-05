namespace TianshuDM.Contract.GameData;

public sealed record GameDataTableSummaryResponse(
    string Key,
    string DisplayName,
    string Category,
    int RecordCount,
    bool HasDraft);

public sealed record GameDataTableResponse(
    string Key,
    string DisplayName,
    string Category,
    string WorkbookPath,
    string WorksheetName,
    IReadOnlyList<GameDataFieldResponse> Fields,
    IReadOnlyList<GameDataRecordResponse> Records,
    bool HasDraft);

public sealed record GameDataFieldResponse(
    string Key,
    string Label,
    string Kind,
    string KindLabel,
    string RawType,
    bool Required,
    string? ReferenceTable,
    IReadOnlyList<GameDataOptionResponse> Options,
    string? PackingSeparator,
    int StartColumn,
    int ColumnCount,
    string ColumnRange,
    bool AllowsMultipleEnumValues);

public sealed record GameDataOptionResponse(
    string Value,
    string Label,
    string? Code = null,
    int? LegacyValue = null);

public sealed record GameDataRecordResponse(
    int Id,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Fields,
    int SourceOrder,
    int SourceRow);

public sealed record SaveGameDataRecordRequest(
    IReadOnlyDictionary<string, string[]> Fields);

public sealed record CreateGameDataRecordRequest(
    int Id,
    IReadOnlyDictionary<string, string[]> Fields);
