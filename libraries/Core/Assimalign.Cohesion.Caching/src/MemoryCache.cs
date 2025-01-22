using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Caching;

public class MemoryCache : ICache
{
    public void Add(object key, object value)
    {
        throw new NotImplementedException();
    }

    public void Clear()
    {
        throw new NotImplementedException();
    }

    public object GetOrAdd(object key, Func<object, object> func)
    {
        throw new NotImplementedException();
    }

    public void Remove(object key)
    {
        throw new NotImplementedException();
    }

    public bool TryGetValue(object key, out object value)
    {
        throw new NotImplementedException();
    }


    public void Dispose()
    {
        throw new NotImplementedException();
    }
}