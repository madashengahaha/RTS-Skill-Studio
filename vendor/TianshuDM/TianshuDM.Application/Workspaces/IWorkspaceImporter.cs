using TianshuDM.Domain.Workspaces;

namespace TianshuDM.Application.Workspaces;

public interface IWorkspaceImporter
{
    WorkspaceImportResult Import(string unityProjectRoot);
}
