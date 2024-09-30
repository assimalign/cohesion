using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace  Assimalign.Cohesion.Configuration.Providers
{
    using  Assimalign.Cohesion.Configuration;

    /// <summary>
    /// Represents environment variables as an <see cref="IConfigurationSource"/>.
    /// </summary>
    public class ConfigurationEnvironmentVariablesSource : IConfigurationSource
    {
        /// <summary>
        /// A prefix used to filter environment variables.
        /// </summary>
        public string Prefix { get; set; }

        /// <summary>
        /// Builds the <see cref="ConfigurationEnvironmentVariablesProvider"/> for this source.
        /// </summary>
        /// <param name="builder">The <see cref="IConfigurationBuilder"/>.</param>
        /// <returns>A <see cref="ConfigurationEnvironmentVariablesProvider"/></returns>
        public IConfigurationProvider Build(IConfigurationBuilder builder)
        {
            return new ConfigurationEnvironmentVariablesProvider(Prefix);
        }
    }
}
