using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using TianshuDM.Application.GameData;
using TianshuDM.Domain.GameData;

namespace TianshuDM.Infrastructure.Excel;

public sealed class GameDataWorkbookWriter : IGameDataWorkbookWriter
{
    public void Write(string workbookPath, GameDataTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        using SpreadsheetDocument document = SpreadsheetDocument.Open(workbookPath, true);
        WorkbookPart workbookPart = document.WorkbookPart
            ?? throw new InvalidDataException($"{workbookPath} 没有工作簿数据。");
        Workbook workbook = workbookPart.Workbook
            ?? throw new InvalidDataException($"{workbookPath} 没有工作簿定义。");
        WorksheetPart worksheetPart = WorkbookWorksheetResolver.FirstInWorkbookOrder(
            workbookPart,
            workbookPath);
        Worksheet worksheet = worksheetPart.Worksheet
            ?? throw new InvalidDataException($"{workbookPath} 没有工作表数据。");
        SheetData sheetData = worksheet.GetFirstChild<SheetData>()
            ?? throw new InvalidDataException($"{workbookPath} 没有数据区域。");
        SharedStringTable? sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;
        Row[] allRows = sheetData.Elements<Row>().ToArray();
        var sourceRows = new Dictionary<int, Row>();
        var dataRowIndexes = new List<uint>();
        Dictionary<int, UInt32Value?> templateStyles = [];
        int fallbackRow = 1;
        foreach (Row row in allRows)
        {
            uint rowIndex = row.RowIndex?.Value ?? (uint)fallbackRow;
            fallbackRow++;
            Dictionary<int, Cell> cells = CellsByColumn(row);
            if (!TryReadId(cells, table, sharedStrings, out int id))
            {
                continue;
            }

            sourceRows[id] = (Row)row.CloneNode(true);
            dataRowIndexes.Add(rowIndex);

            foreach ((int column, Cell cell) in cells)
            {
                templateStyles[column] = cell.StyleIndex;
            }
            row.Remove();
        }

        uint nextRowIndex = allRows
            .Select((row, index) => row.RowIndex?.Value ?? (uint)(index + 1))
            .DefaultIfEmpty(0u)
            .Max() + 1;
        GameDataRecord[] orderedRecords = table.Records
            .OrderBy(record => record.SourceOrder)
            .ThenBy(record => record.Id)
            .ToArray();
        for (int index = 0; index < orderedRecords.Length; index++)
        {
            GameDataRecord record = orderedRecords[index];
            uint targetRowIndex = index < dataRowIndexes.Count ? dataRowIndexes[index] : nextRowIndex++;
            Row row = sourceRows.TryGetValue(record.Id, out Row? sourceRow)
                ? (Row)sourceRow.CloneNode(true)
                : new Row { RowIndex = targetRowIndex };
            Dictionary<int, Cell> sourceCells = CellsByColumn(row);
            ReplaceRow(
                row,
                targetRowIndex,
                sourceCells,
                table,
                record,
                sharedStrings,
                sourceRows.ContainsKey(record.Id) ? null : templateStyles);
            sheetData.Append(row);
        }

        worksheet.Save();
        workbook.Save();
    }

    private static void ReplaceRow(
        Row row,
        uint rowIndex,
        IReadOnlyDictionary<int, Cell> existing,
        GameDataTable table,
        GameDataRecord record,
        SharedStringTable? sharedStrings,
        IReadOnlyDictionary<int, UInt32Value?>? fallbackStyles = null)
    {
        var values = existing.ToDictionary(
            pair => pair.Key,
            pair => ReadCell(pair.Value, sharedStrings));
        foreach (GameDataFieldDefinition field in table.Fields)
        {
            for (int offset = 0; offset < field.ColumnCount; offset++)
            {
                values[field.StartColumn + offset] = string.Empty;
            }

            IReadOnlyList<string> supplied = record.Fields.TryGetValue(
                field.Key,
                out IReadOnlyList<string>? fieldValues)
                ? fieldValues
                : [];
            if (field.Kind == GameDataFieldKind.DelimitedList)
            {
                values[field.StartColumn] = string.Join(field.PackingSeparator ?? ",", supplied);
                continue;
            }

            for (int offset = 0; offset < Math.Min(field.ColumnCount, supplied.Count); offset++)
            {
                values[field.StartColumn + offset] = supplied[offset];
            }
        }

        int maxColumn = Math.Max(
            values.Keys.DefaultIfEmpty(0).Max(),
            table.Fields.Select(field => field.StartColumn + field.ColumnCount - 1).DefaultIfEmpty(0).Max());
        row.RemoveAllChildren<Cell>();
        row.RowIndex = rowIndex;
        for (int column = 0; column <= maxColumn; column++)
        {
            string value = values.GetValueOrDefault(column, string.Empty);
            UInt32Value? style = existing.GetValueOrDefault(column)?.StyleIndex
                                 ?? fallbackStyles?.GetValueOrDefault(column);
            if (string.IsNullOrEmpty(value) && style is null)
            {
                continue;
            }

            var cell = new Cell
            {
                CellReference = CellReference(column, rowIndex),
                DataType = CellValues.InlineString,
                InlineString = new InlineString(
                    new Text(value) { Space = SpaceProcessingModeValues.Preserve }),
            };
            if (style is not null)
            {
                cell.StyleIndex = style;
            }

            row.Append(cell);
        }
    }

    private static Dictionary<int, Cell> CellsByColumn(Row row)
    {
        var result = new Dictionary<int, Cell>();
        int fallback = 0;
        foreach (Cell cell in row.Elements<Cell>())
        {
            int column = cell.CellReference?.Value is { } reference
                ? ColumnIndex(reference)
                : fallback;
            result[column] = cell;
            fallback = column + 1;
        }

        return result;
    }

    private static bool TryReadId(
        Dictionary<int, Cell> cells,
        GameDataTable table,
        SharedStringTable? sharedStrings,
        out int id)
    {
        id = 0;
        GameDataFieldDefinition idField = table.Fields.First(field => field.Key == "Id");
        return cells.TryGetValue(idField.StartColumn, out Cell? cell)
               && int.TryParse(ReadCell(cell, sharedStrings), out id);
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
        int result = 0;
        foreach (char character in reference)
        {
            if (!char.IsLetter(character))
            {
                break;
            }

            result = (result * 26) + (char.ToUpperInvariant(character) - 'A' + 1);
        }

        return result - 1;
    }

    private static string CellReference(int zeroBasedColumn, uint row)
    {
        int value = zeroBasedColumn + 1;
        string column = string.Empty;
        while (value > 0)
        {
            value--;
            column = (char)('A' + (value % 26)) + column;
            value /= 26;
        }

        return $"{column}{row}";
    }
}
