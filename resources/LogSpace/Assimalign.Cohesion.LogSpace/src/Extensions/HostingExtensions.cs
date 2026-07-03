using System;

namespace Assimalign.Cohesion.LogSpace;

using Assimalign.Cohesion.Hosting;

public static class HostingExtensions
{
    extension(IHostBuilder builder)
    {
        /// <summary>
        /// Adds the LogSpace resource to the host.
        /// </summary>
        /// <returns>The host builder for chaining.</returns>
        public IHostBuilder AddLogSpace()
        {
            return builder;
        }
    }
}
