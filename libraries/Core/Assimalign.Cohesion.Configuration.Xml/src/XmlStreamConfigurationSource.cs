

namespace Assimalign.Extensions.Configuration.Providers;
/// <summary>
/// Represents a XML file as an <see cref="IConfigurationSource"/>.
/// </summary>
public class XmlStreamConfigurationSource : StreamConfigurationSource
{
    /// <summary>
    /// Builds the <see cref="XmlStreamConfigurationProvider"/> for this source.
    /// </summary>
    /// <param name="builder">The <see cref="IConfigurationBuilder"/>.</param>
    /// <returns>An <see cref="XmlStreamConfigurationProvider"/></returns>
    public override IConfigurationProvider Build(IConfigurationBuilder builder)
        => new XmlStreamConfigurationProvider(this);
}
