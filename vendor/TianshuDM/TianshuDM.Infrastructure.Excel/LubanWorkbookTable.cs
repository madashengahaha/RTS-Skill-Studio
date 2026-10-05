using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using TianshuDM.Domain.Quests;

namespace TianshuDM.Infrastructure.Excel;

internal sealed record LubanWorkbookRow(
    uint? RowIndex,
    IReadOnlyDictionary<string, string> Values);

internal static class LubanWorkbookTable
{
    public static IReadOnlyList<LubanWorkbookRow> Read(
        string workbookPath,
        string logicalName,
        IReadOnlyCollection<string> requiredColumns)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workbookPath);

        using SpreadsheetDocument document = SpreadsheetDocument.Open(workbookPath, false);
        WorkbookPart workbookPart = document.WorkbookPart
            ?? throw new InvalidDataException("The workbook has no workbook part.");
        Workbook workbook = workbookPart.Workbook
            ?? throw new InvalidDataException("The workbook part has no workbook.");
        Sheets sheets = workbook.Sheets
            ?? throw new InvalidDataException("The workbook has no sheets collection.");
        Sheet sheet = sheets.Elements<Sheet>()
                          .FirstOrDefault(candidate => candidate.Name?.Value == "Sheet1")
                      ?? throw new InvalidDataException("The workbook has no Sheet1 worksheet.");
        WorksheetPart worksheetPart = (WorksheetPart)workbookPart.GetPartById(
            sheet.Id?.Value ?? throw new InvalidDataException("Sheet1 has no relationship id."));
        SharedStringTable? sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;
        Worksheet worksheet = worksheetPart.Worksheet
            ?? throw new InvalidDataException("Sheet1 has no worksheet data.");
        Row[] rows = worksheet
            .GetFirstChild<SheetData>()?
            .Elements<Row>()
            .ToArray() ?? [];
        if (rows.Length == 0)
        {
            return [];
        }

        Dictionary<int, string> headerCells = ReadCells(rows[0], sharedStrings);
        Dictionary<string, int> columns = headerCells
            .Where(pair => pair.Key > 0 && !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);
        string[] missingColumns = requiredColumns
            .Where(column => !columns.ContainsKey(column))
            .ToArray();
        if (missingColumns.Length > 0)
        {
            throw new InvalidDataException(
                $"{logicalName} is missing required columns: {string.Join(", ", missingColumns)}.");
        }

        var result = new List<LubanWorkbookRow>();
        foreach (Row row in rows.Skip(1))
        {
            Dictionary<int, string> cells = ReadCells(row, sharedStrings);
            if (cells.GetValueOrDefault(0, string.Empty)
                .StartsWith("##", StringComparison.Ordinal))
            {
                continue;
            }

            var values = columns.ToDictionary(
                pair => pair.Key,
                pair => cells.GetValueOrDefault(pair.Value, string.Empty),
                StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(values["Id"]))
            {
                continue;
            }

            result.Add(new LubanWorkbookRow(row.RowIndex?.Value, values));
        }

        return result;
    }

    private static Dictionary<int, string> ReadCells(
        Row row,
        SharedStringTable? sharedStrings)
    {
        var cells = new Dictionary<int, string>();
        int fallbackIndex = 0;
        foreach (Cell cell in row.Elements<Cell>())
        {
            int columnIndex = cell.CellReference?.Value is { } reference
                ? GetColumnIndex(reference)
                : fallbackIndex;
            cells[columnIndex] = ReadCellValue(cell, sharedStrings);
            fallbackIndex = columnIndex + 1;
        }

        return cells;
    }

    private static string ReadCellValue(Cell cell, SharedStringTable? sharedStrings)
    {
        if (cell.DataType?.Value == CellValues.SharedString
            && int.TryParse(cell.CellValue?.InnerText, out int sharedStringIndex))
        {
            return sharedStrings?.ElementAt(sharedStringIndex).InnerText ?? string.Empty;
        }

        if (cell.DataType?.Value == CellValues.InlineString)
        {
            return cell.InlineString?.InnerText ?? string.Empty;
        }

        return cell.CellValue?.InnerText ?? string.Empty;
    }

    private static int GetColumnIndex(string cellReference)
    {
        int index = 0;
        foreach (char character in cellReference)
        {
            if (!char.IsLetter(character))
            {
                break;
            }

            index = (index * 26) + (char.ToUpperInvariant(character) - 'A' + 1);
        }

        return index - 1;
    }
}

internal static class LubanValueParser
{
    public static int ParseInt(
        LubanWorkbookRow row,
        string columnName,
        string logicalName)
    {
        string value = row.Values[columnName];
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
        {
            return result;
        }

        throw new InvalidDataException(
            $"{logicalName} row {row.RowIndex} has invalid integer '{value}' in {columnName}.");
    }

    public static IReadOnlyList<QuestReward> ParseRewards(
        LubanWorkbookRow row,
        string columnName,
        string logicalName)
    {
        string value = row.Values[columnName];
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var rewards = new List<QuestReward>();
        foreach (string rewardText in value.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = rewardText.Split(',');
            if (parts.Length != 3
                || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int configId)
                || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int amount))
            {
                throw new InvalidDataException(
                    $"{logicalName} row {row.RowIndex} has invalid reward '{rewardText}'.");
            }

            rewards.Add(new QuestReward(parts[0], configId, amount));
        }

        return rewards;
    }
}
