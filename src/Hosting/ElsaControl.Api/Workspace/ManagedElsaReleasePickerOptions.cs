namespace ElsaControl.Api.Workspace;

public static class ManagedElsaReleasePickerDefaults
{
    public const string ConfigurationSection = "ManagedElsa:ReleasePicker";
}

/// <summary>
/// Picker-only cap for onboarding-options and Apply-release lists. The resolver
/// still accepts any admitted Supported row.
/// </summary>
public sealed class ManagedElsaReleasePickerOptions
{
    public const string ConfigurationSection = ManagedElsaReleasePickerDefaults.ConfigurationSection;

    public int MaxChoices { get; init; } = ElsaControl.RuntimeBuilder.Abstractions.ReleaseCatalog.ManagedElsaReleasePicker.DefaultMaxChoices;

    public int EffectiveMaxChoices =>
        ElsaControl.RuntimeBuilder.Abstractions.ReleaseCatalog.ManagedElsaReleasePicker.NormalizeMaxChoices(MaxChoices);
}
