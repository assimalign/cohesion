using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http;

public abstract class HttpSession
{
    /// <summary>
    /// Indicates whether the current session loaded successfully. Accessing this property before the session is loaded will cause it to be loaded inline.
    /// </summary>
    public virtual bool IsAvailable { get; }

    /// <summary>
    /// A unique identifier for the current session. This is not the same as the session cookie
    /// since the cookie lifetime may not be the same as the session entry lifetime in the data store.
    /// </summary>
    public virtual string Id { get; }

    /// <summary>
    /// Enumerates all the keys, if any.
    /// </summary>
    public virtual IEnumerable<string> Keys { get; }

    /// <summary>
    /// Load the session from the data store. This may throw if the data store is unavailable.
    /// </summary>
    /// <returns></returns>
    //public virtual Task LoadAsync(CancellationToken cancellationToken = default(CancellationToken))
    //{

    //}

    ///// <summary>
    ///// store the session in the data store. This may throw if the data store is unavailable.
    ///// </summary>
    ///// <returns></returns>
    //Task CommitAsync(CancellationToken cancellationToken = default(CancellationToken));

    ///// <summary>
    ///// Retrieve the value of the given key, if present.
    ///// </summary>
    ///// <param name="key"></param>
    ///// <param name="value"></param>
    ///// <returns>The retrieved value.</returns>
    //bool TryGetValue(string key, [NotNullWhen(true)] out byte[]? value);

    ///// <summary>
    ///// Set the given key and value in the current session. This will throw if the session
    ///// was not established prior to sending the response.
    ///// </summary>
    ///// <param name="key"></param>
    ///// <param name="value"></param>
    //void Set(string key, byte[] value);

    ///// <summary>
    ///// Remove the given key from the session if present.
    ///// </summary>
    ///// <param name="key"></param>
    //void Remove(string key);

    ///// <summary>
    ///// Remove all entries from the current session, if any.
    ///// The session cookie is not removed.
    ///// </summary>
    //void Clear();
}
