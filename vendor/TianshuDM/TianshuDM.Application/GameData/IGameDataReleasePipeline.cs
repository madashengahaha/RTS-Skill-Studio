using TianshuDM.Domain.GameData;
using TianshuDM.Domain.Workspaces;

namespace TianshuDM.Application.GameData;

public interface IGameDataWorkbookWriter
{
    void Write(string workbookPath, GameDataTable table);
}

public interface IGameDataReleasePipeline
{
    GameDataReleaseResult Execute(
        WorkspaceImportResult workspace,
        GameDataCatalog catalog,
        IReadOnlyList<string> dirtyTableKeys,
        bool publish,
        bool confirmLubanFailure = false);
}

public interface IGameDataSourceIntegrityValidator
{
    void Verify(GameDataCatalog catalog);
}

public sealed record GameDataReleaseResult(
    string Status,
    string BackupPath,
    string JournalPath,
    string ValidationOutput)
{
    public bool ValidationOverridden { get; init; }

    public string ValidationStatus { get; init; } = "passed";
}
