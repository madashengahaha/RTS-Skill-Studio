using System.Globalization;
using TianshuDM.Domain.GameData;

namespace TianshuDM.Application.GameData;

public sealed class GameDataDraftService(IGameDataDraftStore store)
{
    public GameDataCatalog GetCatalog()
    {
        return store.ReadGameDataCatalog()
               ?? throw new InvalidOperationException("尚未加载英雄和单位数据。");
    }

    public GameDataRecord Update(
        string tableKey,
        int recordId,
        IReadOnlyDictionary<string, IReadOnlyList<string>> fields)
    {
        GameDataCatalog catalog = GetCatalog();
        GameDataTable table = catalog.Table(tableKey);
        GameDataRecord current = table.Record(recordId);
        GameDataRecord replacement = ValidateRecord(catalog, table, recordId, fields) with
        {
            SourceOrder = current.SourceOrder,
            SourceRow = current.SourceRow,
        };
        IReadOnlyList<GameDataRecord> records = table.Records
            .Select(record => record.Id == recordId ? replacement : record)
            .ToArray();
        store.ReplaceGameDataTable(table.Key, records, Scope(table.Key));
        return replacement;
    }

    public GameDataRecord Create(
        string tableKey,
        int recordId,
        IReadOnlyDictionary<string, IReadOnlyList<string>> fields)
    {
        GameDataCatalog catalog = GetCatalog();
        GameDataTable table = catalog.Table(tableKey);
        if (table.Records.Any(record => record.Id == recordId))
        {
            throw new ArgumentException($"{table.DisplayName}中已存在编号 {recordId}。");
        }

        GameDataRecord created = ValidateRecord(catalog, table, recordId, fields) with
        {
            SourceOrder = table.Records.Select(record => record.SourceOrder).DefaultIfEmpty(-1).Max() + 1,
        };
        store.ReplaceGameDataTable(
            table.Key,
            table.Records.Append(created).OrderBy(record => record.Id).ToArray(),
            Scope(table.Key));
        return created;
    }

    public void Delete(string tableKey, int recordId)
    {
        GameDataCatalog catalog = GetCatalog();
        GameDataTable table = catalog.Table(tableKey);
        table.Record(recordId);
        string target = recordId.ToString(CultureInfo.InvariantCulture);
        foreach (GameDataTable candidate in catalog.Tables)
        {
            foreach (GameDataFieldDefinition field in candidate.Fields.Where(
                         field => StringComparer.OrdinalIgnoreCase.Equals(
                             field.ReferenceTable,
                             table.Key)))
            {
                GameDataRecord? owner = candidate.Records.FirstOrDefault(
                    record => record.Fields.TryGetValue(field.Key, out IReadOnlyList<string>? values)
                              && values.Contains(target, StringComparer.Ordinal));
                if (owner is not null)
                {
                    throw new InvalidOperationException(
                        $"无法删除{table.DisplayName} {recordId}，{candidate.DisplayName} {owner.Id}仍在引用它。");
                }
            }
        }

        store.ReplaceGameDataTable(
            table.Key,
            table.Records.Where(record => record.Id != recordId).ToArray(),
            Scope(table.Key));
    }

    internal static GameDataRecord ValidateRecord(
        GameDataCatalog catalog,
        GameDataTable table,
        int recordId,
        IReadOnlyDictionary<string, IReadOnlyList<string>> fields)
    {
        string[] unknown = fields.Keys
            .Where(key => table.Fields.All(field => !StringComparer.Ordinal.Equals(field.Key, key)))
            .ToArray();
        if (unknown.Length > 0)
        {
            throw new ArgumentException($"包含未知字段：{string.Join('、', unknown)}。");
        }

        var normalized = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (GameDataFieldDefinition definition in table.Fields)
        {
            IReadOnlyList<string> values = fields.TryGetValue(definition.Key, out IReadOnlyList<string>? supplied)
                ? supplied.Select(value => value?.Trim() ?? string.Empty).ToArray()
                : [];
            if (definition.Required && values.All(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException($"“{definition.Label}”不能为空。");
            }

            if (definition.Kind == GameDataFieldKind.Map && values.Count % 2 != 0)
            {
                throw new ArgumentException($"“{definition.Label}”必须按键和值成对填写。");
            }

            ValidateValues(catalog, definition, values);
            normalized[definition.Key] = values.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        }

        if (!normalized.TryGetValue("Id", out IReadOnlyList<string>? idValues)
            || idValues.Count != 1
            || !int.TryParse(idValues[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int fieldId)
            || fieldId != recordId)
        {
            throw new ArgumentException("编号必须与当前记录一致。");
        }

        return new GameDataRecord(recordId, normalized);
    }

    private static void ValidateValues(
        GameDataCatalog catalog,
        GameDataFieldDefinition definition,
        IReadOnlyList<string> values)
    {
        IEnumerable<string> integerValues = definition.Kind switch
        {
            GameDataFieldKind.Integer or GameDataFieldKind.Reference => values,
            GameDataFieldKind.List when CollectionValueIsInteger(definition.RawType) => values,
            GameDataFieldKind.Map when MapValueIsInteger(definition.RawType) => values.Where((_, index) => index % 2 == 1),
            _ => [],
        };
        foreach (string value in integerValues.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
            {
                throw new ArgumentException($"“{definition.Label}”的数值只能填写整数。");
            }
        }

        if ((definition.Kind is GameDataFieldKind.Enum or GameDataFieldKind.List)
            && definition.Options.Count > 0)
        {
            foreach (string value in values.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                IEnumerable<string> enumValues = definition.Kind == GameDataFieldKind.Enum
                                                  && definition.AllowsMultipleEnumValues
                    ? value.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                    : [value];
                if (enumValues.Any(enumValue =>
                        definition.Options.All(option =>
                            !StringComparer.Ordinal.Equals(option.Value, enumValue)
                            && !StringComparer.Ordinal.Equals(option.Code, enumValue)
                            && !StringComparer.Ordinal.Equals(option.Label, enumValue))))
                {
                    throw new ArgumentException($"“{definition.Label}”包含未知选项“{value}”。");
                }
            }
        }

        if (definition.Kind == GameDataFieldKind.List
            && StringComparer.OrdinalIgnoreCase.Equals(CollectionValueType(definition.RawType), "ValueSource"))
        {
            foreach (string value in values.Where(value => !string.IsNullOrWhiteSpace(value)))
            {
                string[] parts = value.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length != 3
                    || parts.Any(part => !int.TryParse(
                        part,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out _)))
                {
                    throw new ArgumentException($"“{definition.Label}”每项必须按 PropId,Scale,Fix 填写三个整数。");
                }
            }
        }

        if (definition.ReferenceTable is not null)
        {
            GameDataTable? referenced = catalog.Tables.FirstOrDefault(
                table => StringComparer.OrdinalIgnoreCase.Equals(table.Key, definition.ReferenceTable));
            if (referenced is null)
            {
                return;
            }

            foreach (string value in values.Where(value => !string.IsNullOrWhiteSpace(value) && value != "0"))
            {
                int id = int.Parse(value, CultureInfo.InvariantCulture);
                if (referenced.Records.All(record => record.Id != id))
                {
                    throw new ArgumentException(
                        $"“{definition.Label}”引用了不存在的{referenced.DisplayName} {id}。");
                }
            }
        }
    }

    private static bool CollectionValueIsInteger(string rawType)
    {
        return IsIntegerType(CollectionValueType(rawType));
    }

    private static bool MapValueIsInteger(string rawType)
    {
        string[] parts = rawType.Split(',', StringSplitOptions.TrimEntries);
        return parts.Length >= 3 && IsIntegerType(parts[^1]);
    }

    private static bool IsIntegerType(string rawType)
    {
        string valueType = rawType.Split('#', 2)[0].Trim().TrimEnd('?');
        return StringComparer.OrdinalIgnoreCase.Equals(valueType, "int")
               || StringComparer.OrdinalIgnoreCase.Equals(valueType, "long");
    }

    private static string CollectionValueType(string rawType)
    {
        string[] parts = rawType.Split(',', StringSplitOptions.TrimEntries);
        return parts.Length >= 2 ? parts[^1] : string.Empty;
    }

    private static string Scope(string tableKey) => $"game-data:{tableKey}";
}
