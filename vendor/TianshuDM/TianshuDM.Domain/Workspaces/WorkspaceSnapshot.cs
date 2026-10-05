namespace TianshuDM.Domain.Workspaces;

public sealed class WorkspaceSnapshot
{
    public WorkspaceSnapshot(string sourceHash)
    {
        SourceHash = sourceHash;
    }

    public string SourceHash { get; }

    public bool HasSourceConflict(string currentSourceHash)
    {
        return !StringComparer.Ordinal.Equals(SourceHash, currentSourceHash);
    }
}
