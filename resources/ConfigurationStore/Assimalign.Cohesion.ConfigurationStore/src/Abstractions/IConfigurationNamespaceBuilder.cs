using System;

namespace Assimalign.Cohesion.ConfigurationStore;

/// <summary>
/// Declares the first-start key/value entries in one configuration namespace.
/// </summary>
public interface IConfigurationNamespaceBuilder
{
    /// <summary>
    /// Sets an initial configuration entry.
    /// </summary>
    /// <param name="key">The configuration key.</param>
    /// <param name="value">The string value, or <see langword="null"/>.</param>
    /// <returns>The same namespace builder for chaining.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="key"/> is empty, whitespace, or contains <c>/</c>.
    /// </exception>
    IConfigurationNamespaceBuilder Set(string key, string? value);
}
