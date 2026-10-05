using System.Globalization;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using TianshuDM.Application.Quests;
using TianshuDM.Domain.Quests;

namespace TianshuDM.Infrastructure.Excel;

public sealed class StoryQuestWorkbookReader : IStoryQuestWorkbookReader
{
    public StoryQuestDataset Read(
        string questWorkbookPath,
        string stepWorkbookPath,
        string turnWorkbookPath)
    {
        return new StoryQuestDataset(
            ReadRows(
                questWorkbookPath,
                (cells, columns, row) => new StoryQuestDefinition(
                    ParseInt(Value(cells, columns, "Id"), row, "Id"),
                    Value(cells, columns, "Name"),
                    Value(cells, columns, "Description"),
                    ParseBool(Value(cells, columns, "IsRepeatable"), row, "IsRepeatable"),
                    ParseBool(Value(cells, columns, "StepAutoAccept"), row, "StepAutoAccept"),
                    ParsePrerequisites(
                        Value(cells, columns, "Prerequisites", "PrerequisiteQuestIds"),
                        row,
                        columns.ContainsKey("Prerequisites")))),
            ReadRows(
                stepWorkbookPath,
                MapStep),
            ReadRows(
                turnWorkbookPath,
                (cells, columns, row) => new StoryConversationTurnDefinition(
                    ParseInt(Value(cells, columns, "Id"), row, "Id"),
                    ParseInt(Value(cells, columns, "DialogueId"), row, "DialogueId"),
                    ParseInt(Value(cells, columns, "OrderIndex"), row, "OrderIndex"),
                    ParseInt(Value(cells, columns, "SpeakerType"), row, "SpeakerType"),
                    Value(cells, columns, "SpeakerName"),
                    Value(cells, columns, "SpeakerImage"),
                    Value(cells, columns, "Text"))));
    }

    private static StoryQuestStepDefinition MapStep(
        IReadOnlyDictionary<int, string> cells,
        IReadOnlyDictionary<string, int> columns,
        uint row)
    {
        StoryQuestConditionType condition = ParseCondition(
            Value(cells, columns, "ConditionType"),
            row);
        return new StoryQuestStepDefinition(
            ParseInt(Value(cells, columns, "Id"), row, "Id"),
            ParseInt(Value(cells, columns, "QuestId"), row, "QuestId"),
            ParseInt(Value(cells, columns, "StepOrder"), row, "StepOrder"),
            ParseInt(Value(cells, columns, "NPCId"), row, "NPCId"),
            ParseInt(Value(cells, columns, "AcceptDialogueId"), row, "AcceptDialogueId"),
            ParseInt(Value(cells, columns, "SubmitDialogueId"), row, "SubmitDialogueId"),
            ParseInt(Value(cells, columns, "ProcessingDialogueId"), row, "ProcessingDialogueId"),
            condition,
            ParseInt(Value(cells, columns, "ConditionValue"), row, "ConditionValue"),
            ParseRewards(Value(cells, columns, "Reward"), row),
            OptionalValue(cells, columns, "Description"),
            ParseConditionParams(
                OptionalValue(cells, columns, "ConditionParams"),
                condition,
                row));
    }

    private static List<T> ReadRows<T>(
        string path,
        Func<IReadOnlyDictionary<int, string>, IReadOnlyDictionary<string, int>, uint, T> map)
    {
        using SpreadsheetDocument document = SpreadsheetDocument.Open(path, false);
        WorkbookPart workbookPart = document.WorkbookPart
            ?? throw new InvalidDataException($"{path} has no workbook part.");
        WorksheetPart worksheetPart = WorkbookWorksheetResolver.FirstInWorkbookOrder(workbookPart, path);
        SharedStringTable? sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;
        Worksheet worksheet = worksheetPart.Worksheet
            ?? throw new InvalidDataException($"{path} has no worksheet data.");
        Row[] rows = worksheet.GetFirstChild<SheetData>()?
            .Elements<Row>()
            .ToArray() ?? [];
        if (rows.Length == 0)
        {
            return [];
        }

        Dictionary<string, int> columns = ReadCells(rows[0], sharedStrings)
            .Where(pair => pair.Key > 0 && !string.IsNullOrWhiteSpace(pair.Value))
            .ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);
        var result = new List<T>();
        foreach (Row row in rows.Skip(1))
        {
            Dictionary<int, string> cells = ReadCells(row, sharedStrings);
            string marker = cells.GetValueOrDefault(0, string.Empty);
            string id = cells.GetValueOrDefault(columns.GetValueOrDefault("Id"), string.Empty);
            if (marker.StartsWith("##", StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            result.Add(map(cells, columns, row.RowIndex?.Value ?? 0));
        }

        return result;
    }

    private static Dictionary<int, string> ReadCells(Row row, SharedStringTable? sharedStrings)
    {
        var cells = new Dictionary<int, string>();
        int fallbackIndex = 0;
        foreach (Cell cell in row.Elements<Cell>())
        {
            int columnIndex = cell.CellReference?.Value is { } reference
                ? ColumnIndex(reference)
                : fallbackIndex;
            cells[columnIndex] = ReadCell(cell, sharedStrings);
            fallbackIndex = columnIndex + 1;
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

        if (cell.DataType?.Value == CellValues.InlineString)
        {
            return cell.InlineString?.InnerText ?? string.Empty;
        }

        if (cell.DataType?.Value == CellValues.Boolean)
        {
            return cell.CellValue?.InnerText == "1" ? "true" : "false";
        }

        return cell.CellValue?.InnerText ?? string.Empty;
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

    private static string Value(
        IReadOnlyDictionary<int, string> cells,
        IReadOnlyDictionary<string, int> columns,
        params string[] names)
    {
        foreach (string name in names)
        {
            if (columns.TryGetValue(name, out int index))
            {
                return cells.GetValueOrDefault(index, string.Empty);
            }
        }

        throw new InvalidDataException(
            $"Missing required StoryQuest column '{string.Join("' or '", names)}'.");
    }

    private static int ParseInt(string value, uint row, string column)
    {
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result))
        {
            return result;
        }

        throw new InvalidDataException($"Row {row} has invalid integer '{value}' in {column}.");
    }

    private static string OptionalValue(
        IReadOnlyDictionary<int, string> cells,
        IReadOnlyDictionary<string, int> columns,
        string name)
    {
        return columns.TryGetValue(name, out int index)
            ? cells.GetValueOrDefault(index, string.Empty)
            : string.Empty;
    }

    private static bool ParseBool(string value, uint row, string column)
    {
        if (bool.TryParse(value, out bool result))
        {
            return result;
        }

        if (value == "1" || value == "0")
        {
            return value == "1";
        }

        throw new InvalidDataException($"Row {row} has invalid boolean '{value}' in {column}.");
    }

    private static StoryQuestConditionType ParseCondition(string value, uint row)
    {
        if (Enum.TryParse(value, out StoryQuestConditionType result)
            && Enum.IsDefined(result))
        {
            return result;
        }

        throw new InvalidDataException($"Row {row} has unknown StoryQuest condition '{value}'.");
    }

    private static List<StoryQuestPrerequisite> ParsePrerequisites(
        string value,
        uint row,
        bool typedFormat)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var prerequisites = new List<StoryQuestPrerequisite>();
        foreach (string part in value.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = part.Trim();
            if (!typedFormat)
            {
                prerequisites.Add(
                    new StoryQuestPrerequisite(
                        StoryQuestPrerequisiteType.CompleteQuest,
                        ParseInt(candidate, row, "PrerequisiteQuestIds")));
                continue;
            }

            int separator = candidate.IndexOf(':');
            if (separator <= 0
                || !Enum.TryParse(candidate[..separator], out StoryQuestPrerequisiteType type)
                || !Enum.IsDefined(type))
            {
                throw new InvalidDataException($"Row {row} has invalid prerequisite '{candidate}'.");
            }

            prerequisites.Add(
                new StoryQuestPrerequisite(
                    type,
                    ParseInt(candidate[(separator + 1)..], row, "Prerequisites")));
        }

        return prerequisites;
    }

    private static List<int> ParseOptionalIds(string value, uint row, string column)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value.Split('|', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => ParseInt(part.Trim(), row, column))
            .ToList();
    }

    private static List<int> ParseConditionParams(
        string value,
        StoryQuestConditionType condition,
        uint row)
    {
        if (condition is not StoryQuestConditionType.MoveToArea
            and not StoryQuestConditionType.TimelinePlayed)
        {
            return ParseOptionalIds(value, row, "ConditionParams");
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value.Split([',', '|'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => ParseInt(part.Trim(), row, "ConditionParams"))
            .ToList();
    }

    private static List<QuestReward> ParseRewards(string value, uint row)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var rewards = new List<QuestReward>();
        foreach (string packedReward in value.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = packedReward.Split(',');
            if (parts.Length != 3)
            {
                throw new InvalidDataException($"Row {row} has invalid reward '{packedReward}'.");
            }

            rewards.Add(
                new QuestReward(
                    parts[0],
                    ParseInt(parts[1], row, "Reward.ConfigId"),
                    ParseInt(parts[2], row, "Reward.Amount")));
        }

        return rewards;
    }
}
