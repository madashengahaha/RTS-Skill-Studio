namespace RtsSkillStudio.Api.Workspaces;

public static class WorkbookFileLockChecker
{
    public static IReadOnlyList<string> FindLockedFiles(
        IEnumerable<string> paths
    )
    {
        var locked = new List<string>();
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using FileStream stream = new(
                    path,
                    FileMode.Open,
                    FileAccess.ReadWrite,
                    FileShare.None
                );
            }
            catch (IOException)
            {
                locked.Add(path);
            }
            catch (UnauthorizedAccessException)
            {
                locked.Add(path);
            }
        }

        return locked;
    }
}
