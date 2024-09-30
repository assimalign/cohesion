﻿using System;
using System.Text;

namespace Assimalign.Cohesion.ObjectPool;

public static class ObjectPoolProviderExtensions
{
    /// <summary>
    /// Creates an <see cref="ObjectPool{T}"/> that pools <see cref="StringBuilder"/> instances.
    /// </summary>
    /// <param name="provider">The <see cref="ObjectPoolProvider"/>.</param>
    /// <returns>The <see cref="ObjectPool{T}"/>.</returns>
    public static ObjectPool<StringBuilder> CreateStringBuilderPool(this ObjectPoolProvider provider)
    {
        return provider.Create<StringBuilder>(new PooledObjectPolicyStringBuilder());
    }

    /// <summary>
    /// Creates an <see cref="ObjectPool{T}"/> that pools <see cref="StringBuilder"/> instances.
    /// </summary>
    /// <param name="provider">The <see cref="ObjectPoolProvider"/>.</param>
    /// <param name="initialCapacity">The initial capacity to initialize <see cref="StringBuilder"/> instances with.</param>
    /// <param name="maximumRetainedCapacity">The maximum value for <see cref="StringBuilder.Capacity"/> that is allowed to be
    /// retained, when an instance is returned.</param>
    /// <returns>The <see cref="ObjectPool{T}"/>.</returns>
    public static ObjectPool<StringBuilder> CreateStringBuilderPool(
        this ObjectPoolProvider provider,
        int initialCapacity,
        int maximumRetainedCapacity)
    {
        var policy = new PooledObjectPolicyStringBuilder()
        {
            InitialCapacity = initialCapacity,
            MaximumRetainedCapacity = maximumRetainedCapacity,
        };

        return provider.Create<StringBuilder>(policy);
    }
}
