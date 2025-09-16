using System;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

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
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <param name="value"></param>
    /// <returns></returns>
    bool TryGet(Path path, out string? value);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="path"></param>
    /// <param name="value"></param>
    /// <returns></returns>
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