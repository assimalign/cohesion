namespace Assimalign.Cohesion.ObjectPool;

using Internal;

/// <summary>
/// A base type for <see cref="ObjectPoolPolicy{T}"/>.
/// </summary>
/// <typeparam name="T">The type of object which is being pooled.</typeparam>
public abstract class ObjectPoolPolicy<T> 
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="instance"></param>
    /// <returns></returns>
    public abstract bool CanReturn(T instance);

    /// <summary>
    /// The default object pool policy.
    /// </summary>
    public static ObjectPoolPolicy<T> Default { get; } = new DefaultObjectPoolPolicy<T>(); 
}
