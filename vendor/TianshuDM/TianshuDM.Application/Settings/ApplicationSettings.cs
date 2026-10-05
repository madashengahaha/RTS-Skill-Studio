namespace TianshuDM.Application.Settings;

public sealed record ApplicationSettings(bool AllowLubanFailureConfirmation)
{
    public static ApplicationSettings Default { get; } = new(true);
}

public interface IApplicationSettingsStore
{
    ApplicationSettings ReadSettings();

    void SaveSettings(ApplicationSettings settings);
}
