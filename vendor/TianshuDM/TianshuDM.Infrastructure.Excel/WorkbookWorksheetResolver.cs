using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace TianshuDM.Infrastructure.Excel;

internal static class WorkbookWorksheetResolver
{
    public static WorksheetPart FirstInWorkbookOrder(WorkbookPart workbookPart, string path)
    {
        Workbook workbook = workbookPart.Workbook
            ?? throw new InvalidDataException($"{path} has no workbook data.");
        Sheet sheet = workbook.GetFirstChild<Sheets>()?.Elements<Sheet>().FirstOrDefault()
            ?? throw new InvalidDataException($"{path} has no worksheet.");
        string relationshipId = sheet.Id?.Value
            ?? throw new InvalidDataException($"{path} has a worksheet without a relationship id.");

        return workbookPart.GetPartById(relationshipId) as WorksheetPart
            ?? throw new InvalidDataException($"{path} has an invalid worksheet relationship.");
    }

    public static string FirstSheetName(WorkbookPart workbookPart, string path)
    {
        Workbook workbook = workbookPart.Workbook
            ?? throw new InvalidDataException($"{path} has no workbook data.");
        return workbook.Sheets?.Elements<Sheet>().FirstOrDefault()?.Name?.Value
               ?? throw new InvalidDataException($"{path} has no worksheet.");
    }
}
