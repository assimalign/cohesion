namespace Assimalign.Cohesion.ObjectPool;

/// <summary>
/// A pool of objects.
/// </summary>
/// <typeparam name="T">The type of objects to pool.</typeparam>
public abstract class ObjectPool<T> where T : class
{
    /// <summary>
    /// Gets an object from the pool if one is available, otherwise creates one.
    /// </summary>
    /// <returns>A <typeparamref name="T"/>.</returns>
    public abstract T Rent();

    /// <summary>
    /// Return an object to the pool.
    /// </summary>
    /// <param name="instance">The object to add to the pool.</param>
    public abstract void Return(T instance);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="policy"></param>
    /// <returns></returns>
    public static ObjectPool<T> Create(IObjectPoolPolicy<T> policy)
    {
        var provider = new DefaultObjectPoolProvider();
        return provider.Create(policy);
    }
}
