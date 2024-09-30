using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Configuration.Providers
{
    using Assimalign.Cohesion.Configuration;

    /// <summary>
    /// Represents command line arguments as an <see cref="IConfigurationSource"/>.
    /// </summary>
    public class ConfigurationCommandLineSource : IConfigurationSource
    {
        /// <summary>
        /// Gets or sets the switch mappings.
        /// </summary>
        public IDictionary<string, string> SwitchMappings { get; set; }

        /// <summary>
        /// Gets or sets the command line args.
        /// </summary>
        public IEnumerable<string> Args { get; set; }

        /// <summary>
        /// Builds the <see cref="ConfigurationCommandLineProvider"/> for this source.
        /// </summary>
        /// <param name="builder">The <see cref="IConfigurationBuilder"/>.</param>
        /// <returns>A <see cref="ConfigurationCommandLineProvider"/></returns>
        public IConfigurationProvider Build(IConfigurationBuilder builder)
        {
            return new ConfigurationCommandLineProvider(Args, SwitchMappings);
        }
    }
}
