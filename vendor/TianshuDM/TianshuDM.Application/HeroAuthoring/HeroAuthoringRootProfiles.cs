namespace TianshuDM.Application.HeroAuthoring;

public sealed record HeroAuthoringRootProfile(
    string Namespace,
    string Key,
    string DisplayName,
    string Kind,
    bool CanBeRoot,
    bool CanCreate,
    bool ShowInSourceBrowser,
    string HistoryScopePrefix,
    string? LayoutScopePrefix);

public static class HeroAuthoringRootProfiles
{
    private static readonly HeroAuthoringRootProfile[] Profiles =
    [
        new(
            "TbSkill",
            "skill",
            "Skill",
            "Behavior",
            true,
            true,
            true,
            "skill",
            null),
        new(
            "TbBuff",
            "buff",
            "Buff",
            "Behavior",
            true,
            false,
            true,
            "buff",
            "buff"),
        new(
            "TbEffect",
            "effect",
            "Effect",
            "Behavior",
            true,
            false,
            true,
            "effect",
            null),
        new(
            "TbItem",
            "item",
            "Item",
            "Behavior",
            true,
            false,
            true,
            "item",
            null),
        new(
            "TbBullet",
            "bullet",
            "Bullet",
            "Behavior",
            true,
            false,
            true,
            "bullet",
            null),
        new(
            "TbTrap",
            "trap",
            "Trap",
            "Behavior",
            true,
            false,
            true,
            "trap",
            null),
        new(
            "EffectGroup",
            "effectGroup",
            "Effect Group",
            "SharedContainer",
            true,
            true,
            true,
            "effectGroup",
            "effectGroup"),
        new(
            "ConditionGroup",
            "conditionGroup",
            "Condition Group",
            "SharedContainer",
            true,
            true,
            true,
            "conditionGroup",
            "conditionGroup"),
    ];

    public static IReadOnlyList<HeroAuthoringRootProfile> All => Profiles;

    public static bool TryNormalize(
        string? rootNamespace,
        out HeroAuthoringRootProfile profile)
    {
        profile = Profiles.FirstOrDefault(candidate =>
            candidate.CanBeRoot
            && string.Equals(candidate.Namespace, rootNamespace?.Trim(), StringComparison.OrdinalIgnoreCase))!;
        return profile is not null;
    }

    public static string HistoryScope(string rootNamespace, int rootId)
    {
        if (!TryNormalize(rootNamespace, out HeroAuthoringRootProfile profile))
        {
            throw new ArgumentException("未知行为根类型。", nameof(rootNamespace));
        }

        return $"{profile.HistoryScopePrefix}:{rootId}";
    }

    public static string LayoutStorageMode(string rootNamespace, string projectionMode)
    {
        if (!TryNormalize(rootNamespace, out HeroAuthoringRootProfile profile))
        {
            throw new ArgumentException("未知行为根类型。", nameof(rootNamespace));
        }

        return profile.LayoutScopePrefix is null
            ? projectionMode
            : $"{profile.LayoutScopePrefix}:{projectionMode}";
    }

    public static string SupportedNames =>
        string.Join("、", Profiles.Where(profile => profile.CanBeRoot).Select(profile => profile.DisplayName));
}
