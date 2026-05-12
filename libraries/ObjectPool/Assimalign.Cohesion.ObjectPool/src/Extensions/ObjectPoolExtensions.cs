using Assimalign.Cohesion.ObjectPool.Internal;
using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Assimalign.Cohesion.ObjectPool;

public static class ObjectPoolExtensions
{
    extension<T>(ObjectPool<T> pool) where T : class
    {
        public static ObjectPool<T> Create(ObjectPoolFactory<T> factory)
        {
            return new DefaultObjectPool<T>(factory);
        }

        public static ObjectPool<T> Create(ObjectPoolFactory<T> factory, ObjectPoolPolicy<T> policy)
        {
            return new DefaultObjectPool<T>(factory, policy);
        }

        public static ObjectPool<T> Create(ObjectPoolFactory<T> factory, ObjectPoolPolicy<T> policy, int maximumRetained)
        {
            return new DefaultObjectPool<T>(factory, policy, maximumRetained);
        }

        public static ObjectPool<T> Create(ObjectPoolFactory<T> factory, int maximumRetained)
        {
            return new DefaultObjectPool<T>(factory, maximumRetained);
        }
    }

    extension<T>(ObjectPool<T> pool) where T : class, new()
    {
        /// <summary>
        /// Creates a new object pool for instances of type <typeparamref name="T"/> using the default pooling policy.
        /// </summary>
        /// <remarks>The returned object pool uses the default object pooling strategy for the specified
        /// type. This method is suitable for most scenarios where custom pooling behavior is not required.</remarks>
        /// <returns>An <see cref="ObjectPool{T}"/> instance that manages pooled objects of type <typeparamref name="T"/>.</returns>
        public static ObjectPool<T> Create() 
        {
            return new DefaultObjectPool<T>(new DefaultObjectPoolFactory<T>());
        }

        /// <summary>
        /// Creates a new object pool that uses the specified policy for managing pooled objects.
        /// </summary>
        /// <param name="policy">The policy that defines how objects are created, returned, and reused within the pool. Cannot be null.</param>
        /// <returns>An ObjectPool<T> instance that manages objects according to the specified policy.</returns>
        public static ObjectPool<T> Create(ObjectPoolPolicy<T> policy)
        {
            return new DefaultObjectPool<T>(new DefaultObjectPoolFactory<T>(), policy);
        }

        /// <summary>
        /// Creates a new object pool with a specified maximum number of objects to retain for reuse.
        /// </summary>
        /// <remarks>If the number of returned objects exceeds the specified maximum, excess objects may
        /// be discarded rather than retained for reuse. This method is thread-safe and suitable for use in
        /// multi-threaded scenarios.</remarks>
        /// <param name="maximumRetained">The maximum number of objects that the pool will retain for reuse. Must be greater than or equal to 0.</param>
        /// <returns>An ObjectPool<T> instance that can be used to rent and return objects of type T.</returns>
        public static ObjectPool<T> Create(int maximumRetained)
        {
            return new DefaultObjectPool<T>(new DefaultObjectPoolFactory<T>(), maximumRetained);
        }
    }


    extension<T, TArgs>(ObjectPool<T, TArgs> pool) where T : class
    {
        public static ObjectPool<T, TArgs> Create(ObjectPoolFactory<T, TArgs> factory)
        {
            return new DefaultObjectPool<T, TArgs>(factory);
        }

        public static ObjectPool<T, TArgs> Create(ObjectPoolFactory<T, TArgs> factory, ObjectPoolPolicy<T> policy)
        {
            return new DefaultObjectPool<T, TArgs>(factory, policy);
        }

        public static ObjectPool<T, TArgs> Create(ObjectPoolFactory<T, TArgs> factory, ObjectPoolPolicy<T> policy, int maximumRetained)
        {
            return new DefaultObjectPool<T, TArgs>(factory, policy, maximumRetained);
        }

        public static ObjectPool<T, TArgs> Create(ObjectPoolFactory<T, TArgs> factory, int maximumRetained)
        {
            return new DefaultObjectPool<T, TArgs>(factory, maximumRetained);
        }
    }


    //extension(ObjectPoolProvider provider)
    //{
    //    /// <summary>
    //    /// Creates an <see cref="ObjectPool"/>.
    //    /// </summary>
    //    /// <typeparam name="T">The type to create a pool for.</typeparam>
    //    public ObjectPool<T> Create<T>() where T : class, new()
    //    {
    //        return provider.Create<T>(new DefaultObjectPoolPolicy<T>());
    //    }
    //}

    ///// <summary>
    ///// Creates an <see cref="ObjectPool{T}"/> that pools <see cref="StringBuilder"/> instances.
    ///// </summary>
    ///// <param name="provider">The <see cref="ObjectPoolProvider"/>.</param>
    ///// <returns>The <see cref="ObjectPool{T}"/>.</returns>
    //public static ObjectPool<StringBuilder> CreateStringBuilderPool(this ObjectPoolProvider provider)
    //{
    //    return provider.Create<StringBuilder>(new PooledObjectPolicyStringBuilder());
    //}

    ///// <summary>
    ///// Creates an <see cref="ObjectPool{T}"/> that pools <see cref="StringBuilder"/> instances.
    ///// </summary>
    ///// <param name="provider">The <see cref="ObjectPoolProvider"/>.</param>
    ///// <param name="initialCapacity">The initial capacity to initialize <see cref="StringBuilder"/> instances with.</param>
    ///// <param name="maximumRetainedCapacity">The maximum value for <see cref="StringBuilder.Capacity"/> that is allowed to be
    ///// retained, when an instance is returned.</param>
    ///// <returns>The <see cref="ObjectPool{T}"/>.</returns>
    //public static ObjectPool<StringBuilder> CreateStringBuilderPool(
    //    this ObjectPoolProvider provider,
    //    int initialCapacity,
    //    int maximumRetainedCapacity)
    //{
    //    var policy = new PooledObjectPolicyStringBuilder()
    //    {
    //        InitialCapacity = initialCapacity,
    //        MaximumRetainedCapacity = maximumRetainedCapacity,
    //    };

    //    return provider.Create<StringBuilder>(policy);
    //}
}
