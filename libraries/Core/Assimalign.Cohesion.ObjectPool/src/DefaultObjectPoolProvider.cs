using System;

namespace Assimalign.Cohesion.ObjectPool;

/// <summary>
/// The default <see cref="ObjectPoolProvider"/>.
/// </summary>
public class DefaultObjectPoolProvider : ObjectPoolProvider
{
    /// <summary>
    /// The maximum number of objects to retain in the pool.
    /// </summary>
    public int MaximumRetained { get; set; } = Environment.ProcessorCount * 2;

    /// <inheritdoc/>
    public override ObjectPool<T> Create<T>(IObjectPoolPolicy<T> policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
       

        if (typeof(IDisposable).IsAssignableFrom(typeof(T)))
        {
            return new ObjectPoolDisposable<T>(policy, MaximumRetained);
        }

        return new DefaultObjectPool<T>(policy, MaximumRetained);
    }
}
