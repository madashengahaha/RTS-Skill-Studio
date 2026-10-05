using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using TianshuDM.Application.GameData;
using TianshuDM.Domain.GameData;

namespace TianshuDM.Infrastructure.Excel;

public sealed partial class GameDataWorkbookReader : IGameDataWorkbookReader
{
    private static readonly Dictionary<string, string> ChineseLabels =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = "编号",
            ["skin"] = "默认皮肤",
            ["profession"] = "职业",
            ["quality"] = "品质",
            ["is_show"] = "是否展示",
            ["radius"] = "半径",
            ["props"] = "基础属性",
            ["propsBonus"] = "等级属性加成",
            ["normal_skills"] = "普通技能",
            ["active_skills"] = "主动技能",
            ["skills"] = "技能",
            ["buffs"] = "Buff",
            ["ai_name"] = "AI方案",
            ["hero_id"] = "英雄编号",
            ["group_id"] = "分组编号",
            ["name"] = "名称",
            ["Name"] = "名称",
            ["desc"] = "描述",
            ["Desc"] = "描述",
            ["model"] = "模型资源",
            ["icon_path"] = "图标路径",
            ["Icon"] = "图标",
            ["CardImage"] = "卡牌图片",
            ["level"] = "等级",
            ["FragmentsNeeded"] = "升级所需碎片",
            ["UnlockItem"] = "解锁物品",
            ["Population"] = "人口占用",
            ["random_bag_id"] = "死亡掉落包",
            ["KillSilver"] = "击杀获得银币",
            ["KillMagicValue"] = "击杀获得魔法值",
            ["range_indicator"] = "攻击范围指示器",
            ["placed_layer"] = "放置目标层",
            ["shape"] = "放置形状",
            ["can_be_attack"] = "是否可被攻击",
            ["price_placement"] = "放置消耗",
            ["product_skill"] = "生产技能",
            ["function_component"] = "功能组件",
            ["viewfunction_component"] = "表现组件",
            ["icon"] = "图标",
            ["effect_group_id"] = "效果组编号",
            ["search_target"] = "目标搜索配置",
            ["UseType"] = "使用方式",
            ["UnitType"] = "单位类型",
            ["UnitId"] = "单位编号",
            ["Count"] = "放置数量",
        };

    public GameDataTable Read(GameDataTableSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        using SpreadsheetDocument document = SpreadsheetDocument.Open(source.WorkbookPath, false);
        WorkbookPart workbookPart = document.WorkbookPart
            ?? throw new InvalidDataException($"{source.WorkbookPath} 没有工作簿数据。");
        WorksheetPart worksheetPart = WorkbookWorksheetResolver.FirstInWorkbookOrder(
            workbookPart,
            source.WorkbookPath);
        string worksheetName = WorkbookWorksheetResolver.FirstSheetName(workbookPart, source.WorkbookPath);
        SharedStringTable? sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;
        Worksheet worksheet = worksheetPart.Worksheet
            ?? throw new InvalidDataException($"{source.WorkbookPath} 没有工作表数据。");
        Row[] rows = worksheet.GetFirstChild<SheetData>()?.Elements<Row>().ToArray() ?? [];
        Row header = FindMetadataRow(rows, "##var", sharedStrings, source.WorkbookPath);
        Row types = FindMetadataRow(rows, "##type", sharedStrings, source.WorkbookPath);
        Row labels = FindMetadataRow(rows, "##", sharedStrings, source.WorkbookPath);
        Dictionary<int, string> headerCells = ReadCells(header, sharedStrings);
        Dictionary<int, string> typeCells = ReadCells(types, sharedStrings);
        Dictionary<int, string> labelCells = ReadCells(labels, sharedStrings);
        IReadOnlyDictionary<string, IReadOnlyList<GameDataOption>> enumOptions = ReadEnumOptions(
            workbookPart,
            sharedStrings);
        GameDataFieldDefinition[] fields = BuildFields(
            source,
            headerCells,
            typeCells,
            labelCells,
            enumOptions);

        var records = new List<GameDataRecord>();
        for (int rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        {
            Row row = rows[rowIndex];
            Dictionary<int, string> cells = ReadCells(row, sharedStrings);
            if (cells.GetValueOrDefault(0, string.Empty).StartsWith("##", StringComparison.Ordinal))
            {
                continue;
            }

            GameDataFieldDefinition idField = fields.First(field => field.Key == "Id");
            string idText = cells.GetValueOrDefault(idField.StartColumn, string.Empty);
            if (!int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            {
                continue;
            }

            var values = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (GameDataFieldDefinition field in fields)
            {
                string[] raw = Enumerable.Range(field.StartColumn, field.ColumnCount)
                    .Select(column => cells.GetValueOrDefault(column, string.Empty).Trim())
                    .ToArray();
                values[field.Key] = field.Kind == GameDataFieldKind.DelimitedList
                    ? Split(raw.FirstOrDefault(), field.PackingSeparator)
                    : raw.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
            }

            records.Add(new GameDataRecord(id, values, records.Count, rowIndex + 1));
        }

        return new GameDataTable(
            source.Key,
            source.DisplayName,
            source.Category,
            source.WorkbookPath,
            HashFile(source.WorkbookPath),
            worksheetName,
            fields,
            records);
    }

    private static GameDataFieldDefinition[] BuildFields(
        GameDataTableSource source,
        IReadOnlyDictionary<int, string> headers,
        IReadOnlyDictionary<int, string> types,
        IReadOnlyDictionary<int, string> labels,
        IReadOnlyDictionary<string, IReadOnlyList<GameDataOption>> enumOptions)
    {
        int[] starts = headers
            .Where(pair => pair.Key > 0 && !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => pair.Key)
            .Order()
            .ToArray();
        var result = new List<GameDataFieldDefinition>();
        for (int index = 0; index < starts.Length; index++)
        {
            int start = starts[index];
            int next = index + 1 < starts.Length ? starts[index + 1] : MaxColumn(headers, types, labels) + 1;
            string sourceKey = headers[start];
            string key = string.Equals(sourceKey, "Id", StringComparison.OrdinalIgnoreCase)
                ? "Id"
                : sourceKey;
            string rawType = types.GetValueOrDefault(start, "string");
            GameDataFieldKind kind = Kind(rawType, enumOptions);
            int columnCount = kind is GameDataFieldKind.List or GameDataFieldKind.Map
                ? Math.Max(1, next - start)
                : 1;
            string? enumName = EnumName(rawType, kind, enumOptions);
            result.Add(
                new GameDataFieldDefinition(
                    key,
                    Label(key, labels.GetValueOrDefault(start, string.Empty)),
                    kind,
                    rawType,
                    start,
                    columnCount,
                    key == "Id",
                    ReferenceTable(rawType),
                    enumName is not null && enumOptions.TryGetValue(enumName, out IReadOnlyList<GameDataOption>? options)
                        ? options
                        : [],
                    kind == GameDataFieldKind.DelimitedList ? Delimiter(rawType) : null));

            if (kind is GameDataFieldKind.List or GameDataFieldKind.Map)
            {
                continue;
            }

            for (int column = start + 1; column < next; column++)
            {
                string annotationLabel = labels.GetValueOrDefault(column, string.Empty);
                if (string.IsNullOrWhiteSpace(annotationLabel))
                {
                    continue;
                }

                result.Add(
                    new GameDataFieldDefinition(
                        $"__remark_{column}",
                        annotationLabel,
                        GameDataFieldKind.Text,
                        "string",
                        column,
                        1,
                        false,
                        null,
                        [],
                        null));
            }
        }

        if (result.All(field => field.Key != "Id"))
        {
            throw new InvalidDataException($"{source.DisplayName}缺少 Id 字段。");
        }

        return result.OrderBy(field => field.StartColumn).ToArray();
    }

    private static Dictionary<string, IReadOnlyList<GameDataOption>> ReadEnumOptions(
        WorkbookPart workbookPart,
        SharedStringTable? sharedStrings)
    {
        var result = new Dictionary<string, IReadOnlyList<GameDataOption>>(StringComparer.Ordinal);
        Workbook workbook = workbookPart.Workbook
            ?? throw new InvalidDataException("工作簿缺少枚举定义。");
        foreach (Sheet sheet in workbook.Sheets?.Elements<Sheet>() ?? [])
        {
            Match match = EnumSheetName().Match(sheet.Name?.Value ?? string.Empty);
            if (!match.Success || sheet.Id?.Value is not { } relationshipId)
            {
                continue;
            }

            WorksheetPart part = (WorksheetPart)workbookPart.GetPartById(relationshipId);
            Worksheet worksheet = part.Worksheet
                ?? throw new InvalidDataException($"枚举工作表 {sheet.Name?.Value} 缺少数据。");
            Row[] rows = worksheet.GetFirstChild<SheetData>()?.Elements<Row>().ToArray() ?? [];
            int heading = Array.FindIndex(
                rows,
                row => ReadCells(row, sharedStrings).GetValueOrDefault(0, string.Empty) == "序号");
            if (heading < 0)
            {
                continue;
            }

            GameDataOption[] options = rows.Skip(heading + 1)
                .Select(row => ReadCells(row, sharedStrings))
                .Where(cells => !string.IsNullOrWhiteSpace(cells.GetValueOrDefault(1, string.Empty)))
                .Select(
                    cells =>
                    {
                        string code = cells.GetValueOrDefault(1, string.Empty);
                        string chinese = cells.GetValueOrDefault(2, string.Empty);
                        string value = string.IsNullOrWhiteSpace(chinese) ? code : chinese;
                        int? legacyValue = int.TryParse(
                            cells.GetValueOrDefault(3, string.Empty),
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out int parsed)
                            ? parsed
                            : null;
                        return new GameDataOption(value, value, code, legacyValue);
                    })
                .ToArray();
            result[match.Groups[1].Value] = options;
        }

        return result;
    }

    private static Row FindMetadataRow(
        IEnumerable<Row> rows,
        string marker,
        SharedStringTable? sharedStrings,
        string path)
    {
        return rows.FirstOrDefault(
                   row => ReadCells(row, sharedStrings).GetValueOrDefault(0, string.Empty) == marker)
               ?? throw new InvalidDataException($"{path}缺少 {marker} 元数据行。");
    }

    private static Dictionary<int, string> ReadCells(Row row, SharedStringTable? sharedStrings)
    {
        var cells = new Dictionary<int, string>();
        int fallback = 0;
        foreach (Cell cell in row.Elements<Cell>())
        {
            int column = cell.CellReference?.Value is { } reference
                ? ColumnIndex(reference)
                : fallback;
            cells[column] = ReadCell(cell, sharedStrings);
            fallback = column + 1;
        }

        return cells;
    }

    private static string ReadCell(Cell cell, SharedStringTable? sharedStrings)
    {
        if (cell.DataType?.Value == CellValues.SharedString
            && int.TryParse(cell.CellValue?.InnerText, out int index))
        {
            return sharedStrings?.ElementAt(index).InnerText ?? string.Empty;
        }

        return cell.DataType?.Value == CellValues.InlineString
            ? cell.InlineString?.InnerText ?? string.Empty
            : cell.CellValue?.InnerText ?? string.Empty;
    }

    private static int ColumnIndex(string reference)
    {
        int index = 0;
        foreach (char character in reference)
        {
            if (!char.IsLetter(character))
            {
                break;
            }

            index = (index * 26) + (char.ToUpperInvariant(character) - 'A' + 1);
        }

        return index - 1;
    }

    private static int MaxColumn(params IReadOnlyDictionary<int, string>[] rows)
    {
        return rows.SelectMany(row => row.Keys).DefaultIfEmpty(0).Max();
    }

    private static GameDataFieldKind Kind(
        string rawType,
        IReadOnlyDictionary<string, IReadOnlyList<GameDataOption>> enumOptions)
    {
        string normalized = rawType.Trim();
        if (normalized.StartsWith("map,", StringComparison.OrdinalIgnoreCase))
        {
            return GameDataFieldKind.Map;
        }

        if (normalized.StartsWith("array,", StringComparison.OrdinalIgnoreCase))
        {
            return GameDataFieldKind.List;
        }

        if (normalized.StartsWith("(array#sep=", StringComparison.OrdinalIgnoreCase))
        {
            return GameDataFieldKind.DelimitedList;
        }

        if (normalized.Contains("#ref=", StringComparison.OrdinalIgnoreCase))
        {
            return GameDataFieldKind.Reference;
        }

        if (normalized.StartsWith('E')
            || enumOptions.ContainsKey(normalized.TrimEnd('?')))
        {
            return GameDataFieldKind.Enum;
        }

        if (normalized.StartsWith("bool", StringComparison.OrdinalIgnoreCase))
        {
            return GameDataFieldKind.Boolean;
        }

        if (normalized.Contains("int", StringComparison.OrdinalIgnoreCase))
        {
            return GameDataFieldKind.Integer;
        }

        return GameDataFieldKind.Text;
    }

    private static string? EnumName(
        string rawType,
        GameDataFieldKind kind,
        IReadOnlyDictionary<string, IReadOnlyList<GameDataOption>> enumOptions)
    {
        string candidate = rawType.Trim().TrimEnd('?');
        if (kind == GameDataFieldKind.List)
        {
            int comma = candidate.IndexOf(',');
            if (comma < 0) return null;
            candidate = candidate[(comma + 1)..].Trim();
        }
        else if (kind != GameDataFieldKind.Enum)
        {
            return null;
        }

        return enumOptions.ContainsKey(candidate) ? candidate : null;
    }

    private static string Label(string key, string sourceLabel)
    {
        if (ChineseLabels.TryGetValue(key, out string? translated))
        {
            return translated;
        }

        return string.IsNullOrWhiteSpace(sourceLabel) ? "配置值" : sourceLabel.Trim();
    }

    private static string? ReferenceTable(string rawType)
    {
        Match match = ReferenceType().Match(rawType);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string Delimiter(string rawType)
    {
        Match match = ArraySeparator().Match(rawType);
        return match.Success && match.Groups[1].Value.Length > 0 ? match.Groups[1].Value : ",";
    }

    private static string[] Split(string? value, string? separator)
    {
        return string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(separator ?? ",", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string HashFile(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    [GeneratedRegex(@"\(([^)]+)\)$")]
    private static partial Regex EnumSheetName();

    [GeneratedRegex(@"#ref=([A-Za-z0-9_]+)")]
    private static partial Regex ReferenceType();

    [GeneratedRegex(@"#sep=(.*?)\)")]
    private static partial Regex ArraySeparator();
}
