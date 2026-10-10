namespace RtsSkillStudio.Agent.Patch;

public static class WorkbookPatchImpact
{
    public static IReadOnlyList<WorkbookPatchCommand> RowCommands(WorkbookPatchDocument patch) =>
        patch.Commands.Where(command => command.Kind is "DeleteOwnedMember" or "ReorderGroupMembers").ToArray();
    public static int ChangeCount(WorkbookPatchDocument patch) => patch.FieldChanges.Count + RowCommands(patch).Count;
    public static IEnumerable<string> TableKeys(WorkbookPatchDocument patch) => patch.FieldChanges.Select(change => change.LogicalAddress.TableKey)
        .Concat(RowCommands(patch).Select(command => command.Arguments["tableKey"]!.GetValue<string>())).Distinct(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> VerifyRows(WorkbookPatchDocument patch, string tableKey,
        IReadOnlyList<(int Id, int Order)> records)
    {
        var errors = new List<string>();
        foreach (WorkbookPatchCommand command in RowCommands(patch).Where(command => command.Arguments["tableKey"]!.GetValue<string>() == tableKey))
        {
            if (command.Kind == "DeleteOwnedMember")
            {
                if (records.Any(record => record.Id == command.Target.Id)) errors.Add($"{tableKey}:{command.Target.Id} 删除后仍存在。");
            }
            else
            {
                int[] expected = command.Arguments["orderedIds"]!.AsArray().Select(id => id!.GetValue<int>()).ToArray();
                int[] actual = records.Where(record => expected.Contains(record.Id)).OrderBy(record => record.Order).Select(record => record.Id).ToArray();
                if (!actual.SequenceEqual(expected)) errors.Add($"{tableKey} 分组成员排序回读不一致。");
            }
        }
        return errors;
    }
}
