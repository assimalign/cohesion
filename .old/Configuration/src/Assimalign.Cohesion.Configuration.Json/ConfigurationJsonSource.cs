namespace  Assimalign.Cohesion.Configuration.Providers
{
    using  Assimalign.Cohesion.Configuration;

    /// <summary>
    /// Represents a JSON file as an <see cref="IConfigurationSource"/>.
    /// </summary>
    public class ConfigurationJsonSource : ConfigurationFileSource
    {
        /// <summary>
        /// Builds the <see cref="ConfigurationJsonProvider"/> for this source.
        /// </summary>
        /// <param name="builder">The <see cref="IConfigurationBuilder"/>.</param>
        /// <returns>A <see cref="ConfigurationJsonProvider"/></returns>
        public override IConfigurationProvider Build(IConfigurationBuilder builder)
        {
            EnsureDefaults(builder);
            return new ConfigurationJsonProvider(this);
        }
    }
}
