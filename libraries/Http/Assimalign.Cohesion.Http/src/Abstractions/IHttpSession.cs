using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Tasks;
using System.Threading;

namespace Assimalign.Cohesion.Http;

public interface IHttpSession
{
    /// <summary>
    /// 
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Load the session from the data store. This may throw if the data store is unavailable.
    /// </summary>
    /// <returns></returns>
    Task LoadAsync(CancellationToken cancellationToken = default(CancellationToken));

    /// <summary>
    /// Store the session in the data store. This may throw if the data store is unavailable.
    /// </summary>
    /// <returns></returns>
    Task CommitAsync(CancellationToken cancellationToken = default(CancellationToken));

    /// <summary>
    /// Retrieve the value of the given key, if present.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="value"></param>
    /// <returns>The retrieved value.</returns>
    bool TryGetValue(string key, [NotNullWhen(true)] out byte[]? value);

    /// <summary>
    /// Set the given key and value in the current session. This will throw if the session
    /// was not established prior to sending the response.
    /// </summary>
    /// <param name="key"></param>
    /// <param name="value"></param>
    void Set(string key, byte[] value);

    /// <summary>
    /// Remove the given key from the session if present.
    /// </summary>
    /// <param name="key"></param>
    void Remove(string key);

    /// <summary>
    /// Remove all entries from the current session, if any.
    /// The session cookie is not removed.
    /// </summary>
    void Clear();
}
