using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

/// <summary>
/// Provides configuration key/values for an application.
/// </summary>
public interface IConfigurationProvider : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// Returns the provider name.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Determines whether the given path exists in the available configurations.
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    bool Exists(Path path);

    /// <summary>
    /// Attempts to retrieve the value associated with the specified path.
    /// </summary>
    /// <param name="path">The path to the value being retrieved. This parameter cannot be null.</param>
    /// <param name="value">When this method returns <see langword="true"/>, contains the value associated with the specified path; otherwise, <see langword="null"/>.</param>
    /// <returns><see langword="true"/> if the value was found; otherwise, <see langword="false"/>.</returns>
    bool TryGet(Path path, [NotNullWhen(true)] out string? value);

    /// <summary>
    /// Attempts to set the specified value at the given path within the configuration provider.
    /// </summary>
    /// <param name="path">The path at which to set the value. This parameter cannot be null.</param>
    /// <param name="value">The value to assign at the specified path. If null, the value at the path will be removed.</param>
    /// <returns><see langword="true"/> if the value was successfully set; otherwise, <see langword="false"/>.</returns>
    bool TrySet(Path path, string? value);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    IConfigurationEntry? GetEntry(Path path);

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    IEnumerable<IConfigurationEntry> GetEntries();

    /// <summary>
    /// Synchronously loads the configuration values.
    /// </summary>
    void Load();

    /// <summary>
    /// Asynchronously loads the configuration values.
    /// </summary>
    /// <param name="cancellationToken"></param>
    /// <returns></returns>
    Task LoadAsync(CancellationToken cancellationToken = default);
}