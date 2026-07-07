using System;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.Database.ApplicationModel;

/// <summary>
/// Composition extensions for adding a database resource to an application model.
/// </summary>
public static class DatabaseResourceExtensions
{
    extension(IApplicationBuilder builder)
    {
        /// <summary>
        /// Adds a Cohesion database resource to the application.
        /// </summary>
        /// <param name="name">The resource name, unique within the application.</param>
        /// <returns>The resource descriptor, for chaining dependency edges.</returns>
        public IApplicationResourceDescriptor AddDatabase(string name)
        {
            return builder.AddResource(new DatabaseResource(name));
        }

        /// <summary>
        /// Adds a Cohesion database resource to the application with configured options.
        /// </summary>
        /// <param name="name">The resource name, unique within the application.</param>
        /// <param name="configure">Configures the resource's declared endpoint, mounts, and environment.</param>
        /// <returns>The resource descriptor, for chaining dependency edges.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="configure"/> is null.</exception>
        public IApplicationResourceDescriptor AddDatabase(string name, Action<DatabaseResourceOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);
            var options = new DatabaseResourceOptions();
            configure(options);
            return builder.AddResource(new DatabaseResource(name, options));
        }
    }
}
