using TianshuDM.Domain.Workspaces;

namespace TianshuDM.Application.Workspaces;

public sealed class OpenWorkspaceService(
    IWorkspaceImporter importer,
    IWorkspaceStore store)
{
    public WorkspaceImportResult Open(string unityProjectRoot)
    {
        WorkspaceImportResult import = importer.Import(unityProjectRoot);
        store.ReplaceWorkspace(import);
        return import;
    }
}
