using System;

namespace Assimalign.Cohesion.ObjectPool;

/// <summary>
/// 
/// </summary>
/// <typeparam name="T"></typeparam>
/// <typeparam name="TArgs"></typeparam>
public abstract class ObjectPool<T, TArgs> where T : class
{
    private protected readonly ObjectPoolPolicy<T> _policy = ObjectPoolPolicy<T>.Default;
    private protected readonly ObjectPoolFactory<T, TArgs> _factory;


    /// <summary>
    /// 
    /// </summary>
    /// <param name="factory"></param>
    protected ObjectPool(ObjectPoolFactory<T, TArgs> factory)
    {
        _factory = ArgumentNullException.ThrowIfNull<ObjectPoolFactory<T, TArgs>>(factory);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="policy"></param>
    /// <param name="factory"></param>
    protected ObjectPool(ObjectPoolFactory<T, TArgs> factory, ObjectPoolPolicy<T> policy) : this(factory)
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
    protected virtual ObjectPoolFactory<T, TArgs> Factory => _factory;

    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public abstract T Rent(TArgs args);

    /// <summary>
    /// 
    /// </summary>
    /// <param name="instance"></param>
    public abstract void Return(T instance);
}
