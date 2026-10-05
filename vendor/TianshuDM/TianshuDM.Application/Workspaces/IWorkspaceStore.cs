using TianshuDM.Domain.Workspaces;

namespace TianshuDM.Application.Workspaces;

public interface IWorkspaceStore
{
    void ReplaceWorkspace(WorkspaceImportResult import);

    WorkspaceImportResult? ReadWorkspace();

    string ReadDraftStatus();
}
