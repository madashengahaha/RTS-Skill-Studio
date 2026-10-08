using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using TianshuDM.Domain.GameData;
using TianshuDM.Infrastructure.Excel;

namespace RtsSkillStudio.Api.Workspaces;

public static class GameDataWorkbookCellUpdater
{
    public static void Apply(
        string workbookPath,
        GameDataTable table,
        IEnumerable<WorkbookPatchChange> changes
    )
    {
        using SpreadsheetDocument document =
            SpreadsheetDocument.Open(workbookPath, true);
        WorkbookPart workbookPart = document.WorkbookPart
            ?? throw new InvalidDataException(
                $"{workbookPath} 没有工作簿数据。"
            );
        Workbook workbook = workbookPart.Workbook
            ?? throw new InvalidDataException(
                $"{workbookPath} 没有工作簿数据。"
            );
        Sheet sheet = workbook.GetFirstChild<Sheets>()
            ?.Elements<Sheet>()
            .FirstOrDefault()
            ?? throw new InvalidDataException(
                $"{workbookPath} 没有工作表。"
            );
        string relationshipId = sheet.Id?.Value
            ?? throw new InvalidDataException(
                $"{workbookPath} 的工作表缺少关联 ID。"
            );
        WorksheetPart worksheetPart =
            workbookPart.GetPartById(relationshipId) as WorksheetPart
            ?? throw new InvalidDataException(
                $"{workbookPath} 的工作表关联无效。"
            );
        SheetData sheetData = worksheetPart.Worksheet
            ?.GetFirstChild<SheetData>()
            ?? throw new InvalidDataException(
                $"{workbookPath} 没有数据区域。"
            );
        SharedStringTable? sharedStrings =
            workbookPart.SharedStringTablePart?.SharedStringTable;
        GameDataFieldDefinition idField = table.Fields.First(
            field => field.Key == "Id"
        );
        var worksheetRows = sheetData
            .Elements<Row>()
            .Select(
                (row, index) => new
                {
                    Row = row,
                    RowNumber =
                        row.RowIndex?.Value ?? (uint)(index + 1)
                }
            )
            .ToArray();
        Dictionary<int, (Row Row, uint RowNumber)> rows = table
            .Records.Select(
                record =>
                {
                    (Row Row, uint RowNumber) row = worksheetRows
                        .FirstOrDefault(
                            item =>
                                item.RowNumber == (uint)record.SourceRow
                                && TryReadId(
                                    item.Row,
                                    idField.StartColumn,
                                    sharedStrings
                                ) == record.Id
                        ) is { Row: not null } exact
                            ? (exact.Row, exact.RowNumber)
                            : worksheetRows
                                .Where(
                                    item =>
                                        TryReadId(
                                            item.Row,
                                            idField.StartColumn,
                                            sharedStrings
                                        ) == record.Id
                                )
                                .Select(
                                    item =>
                                        (item.Row, item.RowNumber)
                                )
                                .FirstOrDefault();
                    return (RecordId: record.Id, row);
                }
            )
            .Where(item => item.row.Row is not null)
            .ToDictionary(
                item => item.RecordId,
                item => item.row
            );
        var modifiedRows = new HashSet<Row>();

        foreach (WorkbookPatchChange change in changes)
        {
            if (
                !rows.TryGetValue(
                    change.RecordId,
                    out (Row Row, uint RowNumber) rowEntry
                )
            )
            {
                throw new KeyNotFoundException(
                    $"{table.Key}:{change.RecordId} 不存在。"
                );
            }
            Row row = rowEntry.Row;

            GameDataFieldDefinition field = table.Fields.FirstOrDefault(
                item => string.Equals(
                    item.Key,
                    change.BaseFieldKey,
                    StringComparison.OrdinalIgnoreCase
                )
            ) ?? throw new InvalidDataException(
                $"{table.Key} 不存在字段 {change.BaseFieldKey}。"
            );
            int column =
                field.Kind == GameDataFieldKind.DelimitedList
                    ? field.StartColumn
                    : field.StartColumn + change.IndexOffset;
            string reference = CellReference(
                column,
                rowEntry.RowNumber
            );
            Cell? cell = row.Elements<Cell>().FirstOrDefault(
                item => string.Equals(
                    item.CellReference?.Value,
                    reference,
                    StringComparison.OrdinalIgnoreCase
                )
            );
            if (cell is null)
            {
                cell = new Cell { CellReference = reference };
                row.Append(cell);
            }

            string value = change.Value;
            if (field.Kind == GameDataFieldKind.DelimitedList)
            {
                string separator = field.PackingSeparator ?? ",";
                List<string> values = ReadCell(cell, sharedStrings)
                    .Split(
                        separator,
                        StringSplitOptions.TrimEntries
                    )
                    .ToList();
                while (values.Count <= change.IndexOffset)
                {
                    values.Add("");
                }

                values[change.IndexOffset] = change.Value;
                value = string.Join(separator, values);
            }

            bool writeNumber =
                field.Kind != GameDataFieldKind.DelimitedList
                && double.TryParse(
                    value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out double numericValue
                )
                && double.IsFinite(numericValue)
                && (
                    IsNumericField(field)
                    || cell.DataType?.Value == CellValues.Number
                    || (cell.DataType is null && cell.CellValue is not null)
                );
            UInt32Value? style = cell.StyleIndex;
            cell.RemoveAllChildren();
            if (writeNumber)
            {
                cell.DataType = CellValues.Number;
                cell.CellValue = new CellValue(value);
            }
            else
            {
                cell.DataType = CellValues.InlineString;
                cell.InlineString = new InlineString(
                    new Text(value)
                    {
                        Space = SpaceProcessingModeValues.Preserve
                    }
                );
            }
            if (style is not null)
            {
                cell.StyleIndex = style;
            }
            modifiedRows.Add(row);
        }

        foreach (Row row in modifiedRows)
        {
            Cell[] cells = row.Elements<Cell>()
                .OrderBy(
                    cell => ColumnIndex(cell.CellReference?.Value ?? "")
                )
                .ToArray();
            row.RemoveAllChildren<Cell>();
            foreach (Cell cell in cells)
            {
                row.Append(cell);
            }
        }

        worksheetPart.Worksheet.Save();
        workbookPart.Workbook.Save();
    }

    private static bool IsNumericField(GameDataFieldDefinition field)
    {
        if (field.Kind == GameDataFieldKind.Integer)
        {
            return true;
        }

        if (field.Kind != GameDataFieldKind.List)
        {
            return false;
        }

        string itemType = field.RawType.Split(',').Last().Trim().TrimEnd('?');
        return itemType.Equals("int", StringComparison.OrdinalIgnoreCase)
            || itemType.Equals("long", StringComparison.OrdinalIgnoreCase)
            || itemType.Equals("float", StringComparison.OrdinalIgnoreCase)
            || itemType.Equals("double", StringComparison.OrdinalIgnoreCase)
            || itemType.Equals("decimal", StringComparison.OrdinalIgnoreCase);
    }

    private static int? TryReadId(
        Row row,
        int idColumn,
        SharedStringTable? sharedStrings
    )
    {
        Cell? cell = row.Elements<Cell>().FirstOrDefault(
            item =>
                ColumnIndex(item.CellReference?.Value ?? "")
                == idColumn
        );
        if (cell is null)
        {
            return null;
        }

        string value = ReadCell(cell, sharedStrings).Trim();
        return int.TryParse(
            value,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int id
        )
            ? id
            : null;
    }

    private static string ReadCell(
        Cell cell,
        SharedStringTable? sharedStrings
    )
    {
        if (
            cell.DataType?.Value == CellValues.SharedString
            && int.TryParse(
                cell.CellValue?.InnerText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int index
            )
        )
        {
            SharedStringItem? item = sharedStrings
                ?.Elements<SharedStringItem>()
                .ElementAtOrDefault(index);
            return item?.InnerText ?? string.Empty;
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

            result =
                (result * 26)
                + (char.ToUpperInvariant(character) - 'A' + 1);
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

public sealed record WorkbookPatchChange(
    int RecordId,
    string BaseFieldKey,
    int IndexOffset,
    string Value
);
