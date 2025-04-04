using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// 
/// </summary>
public interface IHttpQueryCollection : IEnumerable<KeyValuePair<HttpQueryKey, HttpQueryValue>>
{
    /// <summary>
    /// The count 
    /// </summary>
    int Count { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    HttpQueryValue this[HttpQueryKey key] { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    bool ContainsKey(HttpQueryKey key);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <param name="value"></param>
    /// <returns></returns>
    bool TryGetValue(HttpQueryKey key, out HttpQueryValue value);
}
