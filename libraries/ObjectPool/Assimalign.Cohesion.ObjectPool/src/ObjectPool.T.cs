using System;

namespace Assimalign.Cohesion.ObjectPool;

/// <summary>
/// A pool of objects.
/// </summary>
/// <typeparam name="T">The type of objects to pool.</typeparam>
public abstract class ObjectPool<T> where T : class
{
    private protected readonly ObjectPoolPolicy<T> _policy = ObjectPoolPolicy<T>.Default;
    private protected readonly ObjectPoolFactory<T> _factory;

    /// <summary>
    /// 
    /// </summary>
    /// <param name="factory"></param>
    protected ObjectPool(ObjectPoolFactory<T> factory)
    {
        _factory = ArgumentNullException.ThrowIfNull<ObjectPoolFactory<T>>(factory);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="factory"></param>
    /// <param name="policy"></param>
    protected ObjectPool(ObjectPoolFactory<T> factory, ObjectPoolPolicy<T> policy) : this(factory)
    {
        _policy = ArgumentNullException.ThrowIfNull<ObjectPoolPolicy<T>>(policy);
    }

    /// <summary>
    /// 
    /// </summary>
    protected virtual ObjectPoolPolicy<T> Policy => _policy;

    /// <summary>
    /// 
    /// </summary>
    protected virtual ObjectPoolFactory<T> Factory => _factory;

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public abstract T Rent();

    /// <summary>
    /// 
    /// </summary>
    /// <param name="instance"></param>
    public abstract void Return(T instance);
}