using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Http;

public interface IHttpHeaderCollection : IEnumerable<KeyValuePair<HttpHeaderKey, HttpHeaderValue>>
{
    /// <summary>
    /// The number of headers in the collection.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// 
    /// </summary>
    bool IsReadOnly { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    HttpHeaderValue this[HttpHeaderKey key] { get; }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    bool ContainsKey(HttpHeaderKey key);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <param name="value"></param>
    /// <returns></returns>
    bool TryGetValue(HttpHeaderKey key, out HttpHeaderValue value);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <param name="value"></param>
    void Add(HttpHeaderKey key, HttpHeaderValue value);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    void Remove(HttpHeaderKey key);

    /// <summary>
    /// 
    /// </summary>
    void Clear();
}
