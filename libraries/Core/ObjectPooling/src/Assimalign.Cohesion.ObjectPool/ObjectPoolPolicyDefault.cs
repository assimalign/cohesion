﻿namespace Assimalign.Cohesion.ObjectPool;

/// <summary>
/// Default implementation for <see cref="ObjectPoolPolicy{T}"/>.
/// </summary>
/// <typeparam name="T">The type of object which is being pooled.</typeparam>
public class ObjectPoolPolicyDefault<T> : ObjectPoolPolicy<T> where T : class, new()
{
    /// <inheritdoc />
    public override T Create()
    {
        return new T();
    }

    /// <inheritdoc />
    public override bool Return(T instance)
    {
        // DefaultObjectPool<T> doesn't call 'Return' for the default policy.
        // So take care adding any logic to this method, as it might require changes elsewhere.
        return true;
    }
}
