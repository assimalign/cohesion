using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Caching;

public interface ICache : IDisposable
{
    /// <summary>
    /// Clears all cache entries
    /// </summary>
    void Clear();
    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    void Remove(object key);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <param name="value"></param>
    void Add(object key, object value);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <param name="value"></param>
    /// <returns></returns>
    bool TryGetValue(object key, out object value);
    /// <summary>
    /// 
    /// </summary>
    /// <param name="key"></param>
    /// <param name="func"></param>
    /// <returns></returns>
    object GetOrAdd(object key, Func<object, object> func);
}
