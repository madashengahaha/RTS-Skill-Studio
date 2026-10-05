using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using TianshuDM.Application.Quests;
using TianshuDM.Domain.Quests;
using TianshuDM.Domain.Workspaces;

namespace TianshuDM.Infrastructure.Excel;

public sealed class RecurringQuestWorkbookWriter : IRecurringQuestWorkbookWriter
{
    public void Write(
        string dailyQuestWorkbookPath,
        string weeklyQuestWorkbookPath,
        string questChestRewardWorkbookPath,
        WorkspaceImportResult workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        WriteQuests(
            dailyQuestWorkbookPath,
            workspace.DailyQuests.Select(
                quest => QuestRecord(
                    quest.Id,
                    quest.Type,
                    quest.TargetValue,
                    quest.Rewards,
                    quest.SortOrder,
                    quest.Description)));
        WriteQuests(
            weeklyQuestWorkbookPath,
            workspace.WeeklyQuests.Select(
                quest => QuestRecord(
                    quest.Id,
                    quest.Type,
                    quest.TargetValue,
                    quest.Rewards,
                    quest.SortOrder,
                    quest.Description)));
        LubanTableWriter.Write(
            questChestRewardWorkbookPath,
            workspace.QuestChestRewards
                .OrderBy(chest => chest.ChestLevel)
                .ThenBy(chest => chest.Id)
                .Select(
                    chest => new Dictionary<string, object>
                    {
                        ["Id"] = chest.Id,
                        ["RequiredCount"] = chest.RequiredCount,
                        ["Reward"] = PackRewards(chest.Rewards),
                        ["ChestLevel"] = chest.ChestLevel,
                    }));
    }

    private static void WriteQuests(
        string path,
        IEnumerable<IReadOnlyDictionary<string, object>> records)
    {
        LubanTableWriter.Write(
            path,
            records
                .OrderBy(record => (int)record["SortOrder"])
                .ThenBy(record => (int)record["Id"]));
    }

    private static Dictionary<string, object> QuestRecord(
        int id,
        QuestTypeValue type,
        int targetValue,
        IReadOnlyList<QuestReward> rewards,
        int sortOrder,
        string description)
    {
        return new Dictionary<string, object>
        {
            ["Id"] = id,
            ["Type"] = type.Code,
            ["TargetValue"] = targetValue,
            ["Reward"] = PackRewards(rewards),
            ["SortOrder"] = sortOrder,
            ["Desc"] = description,
        };
    }

    private static string PackRewards(IEnumerable<QuestReward> rewards)
    {
        return string.Join(
            '|',
            rewards.Select(
                reward => string.Join(
                    ',',
                    reward.Type,
                    reward.ConfigId.ToString(CultureInfo.InvariantCulture),
                    reward.Amount.ToString(CultureInfo.InvariantCulture))));
    }

    private static class LubanTableWriter
    {
        public static void Write(
            string path,
            IEnumerable<IReadOnlyDictionary<string, object>> records)
        {
            using SpreadsheetDocument document = SpreadsheetDocument.Open(path, true);
            WorkbookPart workbookPart = document.WorkbookPart
                ?? throw new InvalidDataException($"{path} 没有工作簿数据。");
            Workbook workbook = workbookPart.Workbook
                ?? throw new InvalidDataException($"{path} 没有工作簿定义。");
            WorksheetPart worksheetPart = WorkbookWorksheetResolver.FirstInWorkbookOrder(
                workbookPart,
                path);
            Worksheet worksheet = worksheetPart.Worksheet
                ?? throw new InvalidDataException($"{path} 没有工作表数据。");
            SheetData sheetData = worksheet.GetFirstChild<SheetData>()
                ?? throw new InvalidDataException($"{path} 没有数据区域。");
            SharedStringTable? sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;
            Row[] rows = sheetData.Elements<Row>().ToArray();
            Row[] metadataRows = rows
                .Where(row => ReadFirstCell(row, sharedStrings).StartsWith("##", StringComparison.Ordinal))
                .ToArray();
            Row header = metadataRows.FirstOrDefault(
                row => StringComparer.Ordinal.Equals(ReadFirstCell(row, sharedStrings), "##var"))
                ?? throw new InvalidDataException($"{path} 缺少 ##var 表头。");
            Dictionary<string, int> columns = ReadColumns(header, sharedStrings);
            foreach (Row row in rows.Except(metadataRows))
            {
                row.Remove();
            }

            uint rowIndex = metadataRows
                .Select(row => row.RowIndex?.Value ?? 0)
                .DefaultIfEmpty(0u)
                .Max() + 1;
            foreach (IReadOnlyDictionary<string, object> record in records)
            {
                var row = new Row { RowIndex = rowIndex };
                foreach ((string name, object value) in record.OrderBy(pair => Column(columns, pair.Key)))
                {
                    row.Append(CreateCell(value, Column(columns, name), rowIndex));
                }

                sheetData.Append(row);
                rowIndex++;
            }

            worksheet.Save();
            workbook.Save();
        }

        private static Dictionary<string, int> ReadColumns(Row row, SharedStringTable? sharedStrings)
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
                : throw new InvalidDataException($"缺少周期任务字段 '{name}'。");
        }

        private static string ReadFirstCell(Row row, SharedStringTable? sharedStrings)
        {
            Cell? cell = row.Elements<Cell>().FirstOrDefault();
            return cell is null ? string.Empty : ReadCell(cell, sharedStrings);
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
                _ => new Cell
                {
                    CellReference = Reference(column, row),
                    DataType = CellValues.InlineString,
                    InlineString = new InlineString(
                        new Text(value?.ToString() ?? string.Empty)
                        {
                            Space = SpaceProcessingModeValues.Preserve,
                        }),
                },
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
    }
}
