namespace  Assimalign.Cohesion.Configuration.Providers
{
    using  Assimalign.Cohesion.Configuration;

    /// <summary>
    /// Represents a JSON file as an <see cref="IConfigurationSource"/>.
    /// </summary>
    public class ConfigurationJsonStreamSource : StreamConfigurationSource
    {
        /// <summary>
        /// Builds the <see cref="ConfigurationJsonStreamProvider"/> for this source.
        /// </summary>
        /// <param name="builder">The <see cref="IConfigurationBuilder"/>.</param>
        /// <returns>An <see cref="ConfigurationJsonStreamProvider"/></returns>
        public override IConfigurationProvider Build(IConfigurationBuilder builder)
            => new ConfigurationJsonStreamProvider(this);
    }
}
