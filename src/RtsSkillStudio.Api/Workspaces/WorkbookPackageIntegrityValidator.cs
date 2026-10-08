using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

namespace RtsSkillStudio.Api.Workspaces;

public static class WorkbookPackageIntegrityValidator
{
    public static IReadOnlyList<string> FindNewErrors(
        string sourceWorkbookPath,
        string stagedWorkbookPath
    )
    {
        HashSet<string> baseline = Validate(sourceWorkbookPath)
            .ToHashSet(StringComparer.Ordinal);
        return Validate(stagedWorkbookPath)
            .Where(error => !baseline.Contains(error))
            .ToArray();
    }

    private static IReadOnlyList<string> Validate(string workbookPath)
    {
        using SpreadsheetDocument document =
            SpreadsheetDocument.Open(workbookPath, false);
        var validator = new OpenXmlValidator();
        return validator
            .Validate(document)
            .Select(
                error =>
                    $"{error.ErrorType}|{error.Path?.XPath}|{error.Description}"
            )
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
