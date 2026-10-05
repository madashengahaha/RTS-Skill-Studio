using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using TianshuDM.Application.Quests;
using TianshuDM.Domain.Quests;

namespace TianshuDM.Infrastructure.Excel;

public sealed class StoryQuestWorkbookWriter : IStoryQuestWorkbookWriter
{
    private static readonly HashSet<string> OptionalStepColumns =
        new(StringComparer.Ordinal) { "Description", "ConditionParams" };

    public void Write(
        string questWorkbookPath,
        string stepWorkbookPath,
        string turnWorkbookPath,
        StoryQuestDataset dataset)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        string prerequisiteColumn = HasColumn(questWorkbookPath, "Prerequisites")
            ? "Prerequisites"
            : "PrerequisiteQuestIds";
        if (prerequisiteColumn == "PrerequisiteQuestIds"
            && dataset.Quests.SelectMany(quest => quest.Prerequisites)
                .Any(item => item.Type != StoryQuestPrerequisiteType.CompleteQuest))
        {
            throw new InvalidDataException(
                "当前 StoryQuest 表只有旧版 PrerequisiteQuestIds 列，无法保存等级或地图依赖；请先更新为 Prerequisites 列。");
        }

        WriteTable(
            questWorkbookPath,
            dataset.Quests.OrderBy(quest => quest.Id).Select(
                quest => new Dictionary<string, object>
                {
                    ["Id"] = quest.Id,
                    ["Name"] = quest.Name,
                    ["Description"] = quest.Description,
                    ["IsRepeatable"] = quest.IsRepeatable,
                    ["StepAutoAccept"] = quest.StepAutoAccept,
                    [prerequisiteColumn] = prerequisiteColumn == "Prerequisites"
                        ? string.Join('|', quest.Prerequisites.Select(PackPrerequisite))
                        : string.Join(
                            '|',
                            quest.Prerequisites
                                .Where(item => item.Type == StoryQuestPrerequisiteType.CompleteQuest)
                                .Select(item => item.Value)),
                }));
        WriteTable(
            stepWorkbookPath,
            dataset.Steps.OrderBy(step => step.QuestId).ThenBy(step => step.StepOrder).ThenBy(step => step.Id).Select(
                step => new Dictionary<string, object>
                {
                    ["Id"] = step.Id,
                    ["QuestId"] = step.QuestId,
                    ["StepOrder"] = step.StepOrder,
                    ["NPCId"] = step.NpcId,
                    ["AcceptDialogueId"] = step.AcceptDialogueId,
                    ["SubmitDialogueId"] = step.SubmitDialogueId,
                    ["ProcessingDialogueId"] = step.ProcessingDialogueId,
                    ["ConditionType"] = (int)step.ConditionType,
                    ["ConditionValue"] = step.ConditionValue,
                    ["Reward"] = string.Join('|', step.Rewards.Select(PackReward)),
                    ["Description"] = step.Description,
                    ["ConditionParams"] = PackConditionParams(step),
                }));
        WriteTable(
            turnWorkbookPath,
            dataset.Turns.OrderBy(turn => turn.DialogueId).ThenBy(turn => turn.OrderIndex).ThenBy(turn => turn.Id).Select(
                turn => new Dictionary<string, object>
                {
                    ["Id"] = turn.Id,
                    ["DialogueId"] = turn.DialogueId,
                    ["OrderIndex"] = turn.OrderIndex,
                    ["SpeakerType"] = turn.SpeakerType,
                    ["SpeakerName"] = turn.SpeakerName,
                    ["SpeakerImage"] = turn.SpeakerImage,
                    ["Text"] = turn.Text,
                }));
    }

    private static void WriteTable(
        string path,
        IEnumerable<IReadOnlyDictionary<string, object>> records)
    {
        IReadOnlyDictionary<string, object>[] materializedRecords = records.ToArray();
        using SpreadsheetDocument document = SpreadsheetDocument.Open(path, true);
        WorkbookPart workbookPart = document.WorkbookPart
            ?? throw new InvalidDataException($"{path} has no workbook part.");
        Workbook workbook = workbookPart.Workbook
            ?? throw new InvalidDataException($"{path} has no workbook data.");
        WorksheetPart worksheetPart = WorkbookWorksheetResolver.FirstInWorkbookOrder(workbookPart, path);
        Worksheet worksheet = worksheetPart.Worksheet
            ?? throw new InvalidDataException($"{path} has no worksheet data.");
        SheetData sheetData = worksheet.GetFirstChild<SheetData>()
            ?? throw new InvalidDataException($"{path} has no sheet data.");
        SharedStringTable? sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;
        Row[] rows = sheetData.Elements<Row>().ToArray();
        Row[] metadataRows = rows
            .Where(row => ReadFirstCell(row, sharedStrings).StartsWith("##", StringComparison.Ordinal))
            .ToArray();
        Row header = metadataRows.FirstOrDefault(
            row => StringComparer.Ordinal.Equals(ReadFirstCell(row, sharedStrings), "##var"))
            ?? throw new InvalidDataException($"{path} has no ##var header row.");
        Dictionary<string, int> columns = ReadColumns(header, sharedStrings);
        Dictionary<string, Row> sourceRowsById = ReadSourceRowsById(
            rows.Except(metadataRows),
            columns,
            sharedStrings);
        HashSet<int> declaredColumns = columns.Values.ToHashSet();
        foreach (Row row in rows.Except(metadataRows))
        {
            row.Remove();
        }

        uint rowIndex = metadataRows.Select(row => row.RowIndex?.Value ?? 0).DefaultIfEmpty(0u).Max() + 1;
        foreach (IReadOnlyDictionary<string, object> record in materializedRecords)
        {
            var row = new Row { RowIndex = rowIndex };
            var cells = new Dictionary<int, Cell>();
            foreach ((string name, object value) in record
                         .Where(pair => columns.ContainsKey(pair.Key) || !OptionalStepColumns.Contains(pair.Key)))
            {
                int column = Column(columns, name);
                cells[column] = CreateCell(value, column, rowIndex);
            }

            string id = record.TryGetValue("Id", out object? idValue)
                ? Convert.ToString(idValue, CultureInfo.InvariantCulture) ?? string.Empty
                : string.Empty;
            if (sourceRowsById.TryGetValue(id, out Row? sourceRow))
            {
                foreach ((Cell sourceCell, int column) in sourceRow.Elements<Cell>()
                             .Select((cell, index) => (cell, CellColumn(cell, index + 1))))
                {
                    if (column <= 0 || !declaredColumns.Contains(column) || cells.ContainsKey(column))
                    {
                        continue;
                    }

                    var preserved = (Cell)sourceCell.CloneNode(true);
                    preserved.CellReference = Reference(column, rowIndex);
                    cells[column] = preserved;
                }
            }

            foreach (Cell cell in cells.OrderBy(pair => pair.Key).Select(pair => pair.Value))
            {
                row.Append(cell);
            }

            sheetData.Append(row);
            rowIndex++;
        }

        worksheet.Save();
        workbook.Save();
    }

    private static Dictionary<string, Row> ReadSourceRowsById(
        IEnumerable<Row> rows,
        Dictionary<string, int> columns,
        SharedStringTable? sharedStrings)
    {
        if (!columns.TryGetValue("Id", out int idColumn))
        {
            return new Dictionary<string, Row>(StringComparer.Ordinal);
        }

        return rows.Select(
                row => new
                {
                    Row = row,
                    Id = row.Elements<Cell>()
                        .Select((cell, index) => (cell, column: CellColumn(cell, index + 1)))
                        .Where(pair => pair.column == idColumn)
                        .Select(pair => ReadCell(pair.cell, sharedStrings))
                        .FirstOrDefault() ?? string.Empty,
                })
            .Where(item => !string.IsNullOrWhiteSpace(item.Id))
            .ToDictionary(item => item.Id, item => item.Row, StringComparer.Ordinal);
    }

    private static int CellColumn(Cell cell, int fallback)
    {
        int column = ColumnNumber(cell.CellReference?.Value ?? string.Empty);
        return column == 0 ? fallback : column;
    }

    private static Dictionary<string, int> ReadColumns(
        Row row,
        SharedStringTable? sharedStrings)
    {
        var columns = new Dictionary<string, int>(StringComparer.Ordinal);
        int fallbackColumn = 1;
        foreach (Cell cell in row.Elements<Cell>())
        {
            int column = cell.CellReference?.Value is { } reference
                ? ColumnNumber(reference)
                : fallbackColumn;
            string value = ReadCell(cell, sharedStrings);
            if (column > 1 && !string.IsNullOrWhiteSpace(value))
            {
                columns[value] = column;
            }

            fallbackColumn = column + 1;
        }

        return columns;
    }

    private static int Column(Dictionary<string, int> columns, string name)
    {
        return columns.TryGetValue(name, out int column)
            ? column
            : throw new InvalidDataException($"Missing required StoryQuest column '{name}'.");
    }

    private static bool HasColumn(string path, string name)
    {
        using SpreadsheetDocument document = SpreadsheetDocument.Open(path, false);
        WorkbookPart workbookPart = document.WorkbookPart
            ?? throw new InvalidDataException($"{path} has no workbook part.");
        WorksheetPart worksheetPart = WorkbookWorksheetResolver.FirstInWorkbookOrder(workbookPart, path);
        SharedStringTable? sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;
        Worksheet worksheet = worksheetPart.Worksheet
            ?? throw new InvalidDataException($"{path} has no worksheet data.");
        Row header = worksheet.GetFirstChild<SheetData>()?.Elements<Row>()
                         .FirstOrDefault(row => ReadFirstCell(row, sharedStrings) == "##var")
                     ?? throw new InvalidDataException($"{path} has no ##var header row.");
        return ReadColumns(header, sharedStrings).ContainsKey(name);
    }

    private static string ReadFirstCell(Row row, SharedStringTable? sharedStrings)
    {
        Cell? cell = row.Elements<Cell>().FirstOrDefault();
        if (cell is null)
        {
            return string.Empty;
        }

        if (cell.DataType?.Value == CellValues.SharedString
            && int.TryParse(cell.CellValue?.InnerText, out int index))
        {
            return sharedStrings?.ElementAt(index).InnerText ?? string.Empty;
        }

        return cell.DataType?.Value == CellValues.InlineString
            ? cell.InlineString?.InnerText ?? string.Empty
            : cell.CellValue?.InnerText ?? string.Empty;
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

    private static int ColumnNumber(string reference)
    {
        int column = 0;
        foreach (char character in reference)
        {
            if (!char.IsLetter(character))
            {
                break;
            }

            column = (column * 26) + (char.ToUpperInvariant(character) - 'A' + 1);
        }

        return column;
    }

    private static Cell CreateCell(object value, int column, uint row)
    {
        return value switch
        {
            int number => new Cell
            {
                CellReference = Reference(column, row),
                DataType = CellValues.Number,
                CellValue = new CellValue(number.ToString(CultureInfo.InvariantCulture)),
            },
            bool boolean => new Cell
            {
                CellReference = Reference(column, row),
                DataType = CellValues.Boolean,
                CellValue = new CellValue(boolean ? "1" : "0"),
            },
            _ => CreateStringCell(value?.ToString() ?? string.Empty, column, row),
        };
    }

    private static Cell CreateStringCell(string value, int column, uint row)
    {
        return new Cell
        {
            CellReference = Reference(column, row),
            DataType = CellValues.InlineString,
            InlineString = new InlineString(new Text(value) { Space = SpaceProcessingModeValues.Preserve }),
        };
    }

    private static string Reference(int column, uint row)
    {
        string name = string.Empty;
        int value = column;
        while (value > 0)
        {
            value--;
            name = (char)('A' + (value % 26)) + name;
            value /= 26;
        }

        return $"{name}{row}";
    }

    private static string PackReward(QuestReward reward)
    {
        return string.Join(
            ',',
            reward.Type,
            reward.ConfigId.ToString(CultureInfo.InvariantCulture),
            reward.Amount.ToString(CultureInfo.InvariantCulture));
    }

    private static string PackConditionParams(StoryQuestStepDefinition step)
    {
        char separator = step.ConditionType is StoryQuestConditionType.MoveToArea
            or StoryQuestConditionType.TimelinePlayed
            ? ','
            : '|';
        return string.Join(separator, step.ConditionParams ?? []);
    }

    private static string PackPrerequisite(StoryQuestPrerequisite prerequisite)
    {
        return $"{prerequisite.Type}:{prerequisite.Value.ToString(CultureInfo.InvariantCulture)}";
    }
}
