namespace CrabDesk.Core;

/// <summary>
/// AI connection settings are a local trust decision. Layout imports must not
/// replace the endpoint, credentials, model, or prompt profile of this device.
/// </summary>
public static class AiClassificationImportPolicy
{
    public static AiClassificationSettings PreserveLocalProfile(
        AiClassificationSettings localProfile,
        AiClassificationSettings importedProfile)
    {
        ArgumentNullException.ThrowIfNull(localProfile);
        ArgumentNullException.ThrowIfNull(importedProfile);
        return new AiClassificationSettings
        {
            BaseUrl = localProfile.BaseUrl,
            ApiKey = localProfile.ApiKey,
            WebSearchEnabled = localProfile.WebSearchEnabled,
            WebSearchApiKey = localProfile.WebSearchApiKey,
            Model = localProfile.Model,
            CategoryLabels = localProfile.CategoryLabels,
            CustomPrompt = localProfile.CustomPrompt,
            ReassignExistingItems = localProfile.ReassignExistingItems
        };
    }
}
