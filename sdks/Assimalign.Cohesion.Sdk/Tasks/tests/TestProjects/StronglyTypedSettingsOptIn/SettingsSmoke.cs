using Assimalign.Cohesion.Configuration;

namespace StronglyTypedSettingsOptIn;

internal static class SettingsSmoke
{
    internal static CatalogSettings Bind(IConfiguration configuration)
    {
        var settings = new CatalogSettings();

        settings.Bind(configuration);

        return settings;
    }
}
