namespace RtsSkillStudio.Agent.Patch;

public static class WorkbookPatchDiffProjector
{
    public static IReadOnlyList<WorkbookPatchDiffRow> Project(
        WorkbookPatchDocument patch
    )
    {
        return patch.FieldChanges
            .Select(Project)
            .OrderBy(row => row.TableKey, StringComparer.Ordinal)
            .ThenBy(row => row.RecordId)
            .ThenBy(row => row.Field, StringComparer.Ordinal)
            .ToArray();
    }

    private static WorkbookPatchDiffRow Project(WorkbookFieldChange change)
    {
        string before = WorkbookFieldChangeJson.RawText(change.Before);
        string after = WorkbookFieldChangeJson.RawText(change.After);
        return new WorkbookPatchDiffRow(
            change.OperationId,
            $"{change.LogicalAddress.TableKey}.{change.Id}.{change.Field}",
            change.LogicalAddress.TableKey,
            change.LogicalAddress.RecordId,
            change.Field,
            change.SemanticField,
            WorkbookFieldChangeJson.SemanticText(change.SemanticValue),
            change.SemanticUnit,
            before,
            after,
            change.Source,
            change.Evidence,
            string.Equals(before, after, StringComparison.Ordinal)
        );
    }
}
