using System;

namespace Assimalign.Cohesion.ConfigurationStore;

/// <summary>
/// Defines the contract-only composition seam for a configuration store application.
/// </summary>
public interface IConfigurationStoreApplicationBuilder
{
    /// <summary>
    /// Declares a named configuration namespace and its first-start values.
    /// </summary>
    /// <remarks>
    /// Declared values seed a namespace only when no durable namespace document exists.
    /// Later command mutations therefore survive application restarts.
    /// </remarks>
    /// <param name="name">The namespace name.</param>
    /// <param name="configure">The callback that declares initial key/value entries.</param>
    /// <returns>The same builder instance for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or whitespace.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A namespace with the same name is already declared.</exception>
    IConfigurationStoreApplicationBuilder AddNamespace(
        string name,
        Action<IConfigurationNamespaceBuilder> configure);

    /// <summary>
    /// Builds the configuration store application.
    /// </summary>
    /// <returns>The configured configuration store application.</returns>
    /// <exception cref="InvalidOperationException">
    /// The builder has already built an application, or a registered host service factory returns
    /// <see langword="null"/>.
    /// </exception>
    IConfigurationStoreApplication Build();
}
