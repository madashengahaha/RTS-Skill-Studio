namespace TianshuDM.Application.HeroAuthoring;

public sealed record HeroAuthoringNodeLayout(string InstanceKey, double X, double Y);

public interface IHeroAuthoringLayoutStore
{
    IReadOnlyList<HeroAuthoringNodeLayout> ReadHeroAuthoringLayout(
        string projectRoot,
        int skillId,
        string projectionMode);

    void ReplaceHeroAuthoringLayout(
        string projectRoot,
        int skillId,
        string projectionMode,
        IReadOnlyList<HeroAuthoringNodeLayout> positions);
}
