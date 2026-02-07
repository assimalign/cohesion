using System;
using System.Threading;
using System.Collections.Concurrent;

namespace Assimalign.Cohesion.ObjectPool;

public class DefaultObjectPool<T> : ObjectPool<T> where T : class
{
    private protected readonly ConcurrentQueue<T> _objects;
    private protected T? _firstElement;

    private readonly int _maxCapacity = (Environment.ProcessorCount * 2) - 1;
    private int _numItems;


    public DefaultObjectPool(ObjectPoolFactory<T> factory)
        : this(factory, ObjectPoolPolicy<T>.Default) { }
    public DefaultObjectPool(ObjectPoolFactory<T> factory, ObjectPoolPolicy<T> policy)
        : this(factory, policy, Environment.ProcessorCount * 2) { }

    public DefaultObjectPool(ObjectPoolFactory<T> factory, int maximumRetained) 
        : this(factory, ObjectPoolPolicy<T>.Default, maximumRetained) { }

    public DefaultObjectPool(ObjectPoolFactory<T> factory, ObjectPoolPolicy<T> policy, int maximumRetained) 
        : base(factory, policy)
    {
        _objects = new ConcurrentQueue<T>();
        _maxCapacity = maximumRetained - 1;
    }

    /// <summary>
    /// Gets an object from the pool if one is available, otherwise creates one.
    /// </summary>
    /// <returns>A <typeparamref name="T"/>.</returns>
    public override T Rent()
    {
        var item = _firstElement;
        if (item == null || Interlocked.CompareExchange(ref _firstElement, null, item) != item)
        {
            if (_objects.TryDequeue(out item))
            {
                Interlocked.Decrement(ref _numItems);
                return item;
            }

            item = _factory.Create();
        }

        return item;
    }

    /// <summary>
    /// Return an object to the pool.
    /// </summary>
    /// <param name="instance">The object to add to the pool.</param>
    public override void Return(T instance)
    {
        if (_policy.CanReturn(instance))
        {
            if (_firstElement != null || Interlocked.CompareExchange(ref _firstElement, instance, null) != null)
            {
                if (Interlocked.Increment(ref _numItems) <= _maxCapacity)
                {
                    _objects.Enqueue(instance);
                }
                else
                {
                    // no room, clean up the count and drop the object on the floor
                    Interlocked.Decrement(ref _numItems);
                }
            }
        }
    }
}
