using System.IO;

namespace  Assimalign.Cohesion.Configuration.Providers;

/// <summary>
/// Loads configuration key/values from a json stream into a provider.
/// </summary>
public class ConfigurationJsonStreamProvider : StreamConfigurationProvider
{
    /// <summary>
    /// Constructor.
    /// </summary>
    /// <param name="source">The <see cref="ConfigurationJsonStreamSource"/>.</param>
    public ConfigurationJsonStreamProvider(ConfigurationJsonStreamSource source) : base(source) { }

    /// <summary>
    /// Loads json configuration key/values from a stream into a provider.
    /// </summary>
    /// <param name="stream">The json <see cref="Stream"/> to load configuration data from.</param>
    public override void Load(Stream stream)
    {
        Data = ConfigurationJsonProvider.JsonConfigurationFileParser.Parse(stream);
    }
}
