using TianshuDM.Domain.Workspaces;

namespace TianshuDM.Application.Workspaces;

public interface IWorkspaceSourceValidator
{
    void Validate(WorkspaceImportResult workspace);
}
