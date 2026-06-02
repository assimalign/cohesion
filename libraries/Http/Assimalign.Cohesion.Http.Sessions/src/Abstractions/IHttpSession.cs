using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using System.Threading;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Represents the session state associated with an HTTP exchange.
/// </summary>
public interface IHttpSession
{
    /// <summary>
    /// Gets a value indicating whether the session has been loaded and is
    /// ready for use. Backing stores that require an asynchronous load set
    /// this to <see langword="true"/> only after <see cref="LoadAsync"/>
    /// completes.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Gets the unique session identifier.
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Gets the keys currently present in the session.
    /// </summary>
    IEnumerable<string> Keys { get; }

    /// <summary>
    /// Loads the session state from the backing store.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for the load operation.</param>
    /// <returns>A task that completes when the session has been loaded.</returns>
    Task LoadAsync(CancellationToken cancellationToken = default(CancellationToken));

    /// <summary>
    /// Persists the session state to the backing store.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token for the commit operation.</param>
    /// <returns>A task that completes when the session has been committed.</returns>
    Task CommitAsync(CancellationToken cancellationToken = default(CancellationToken));

    /// <summary>
    /// Attempts to retrieve the value associated with the supplied key.
    /// </summary>
    /// <param name="key">The session key to resolve.</param>
    /// <param name="value">The resolved binary value when found.</param>
    /// <returns><see langword="true"/> when the value was found; otherwise <see langword="false"/>.</returns>
    bool TryGetValue(string key, [NotNullWhen(true)] out byte[]? value);

    /// <summary>
    /// Sets the supplied key and value in the current session.
    /// </summary>
    /// <param name="key">The session key to store.</param>
    /// <param name="value">The binary value to store.</param>
    void Set(string key, byte[] value);

    /// <summary>
    /// Removes the supplied key from the current session if present.
    /// </summary>
    /// <param name="key">The session key to remove.</param>
    void Remove(string key);

    /// <summary>
    /// Removes all entries from the current session.
    /// </summary>
    void Clear();
}
