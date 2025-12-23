using System;
using System.Threading;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.ObjectPool;

/// <summary>
/// Default implementation of <see cref="ObjectPool{T}"/>.
/// </summary>
/// <typeparam name="T">The type to pool objects for.</typeparam>
/// <remarks>This implementation keeps a cache of retained objects. This means that if objects are returned when the pool has already reached "maximumRetained" objects they will be available to be Garbage Collected.</remarks>
public class DefaultObjectPool<T> : ObjectPool<T> where T : class
{
    private protected readonly ObjectWrapper[] _poolElements;
    private protected readonly IObjectPoolPolicy<T> _poolPolicy;
    private protected readonly bool _isDefaultPolicy;
    private protected T? firstElement;
    private protected readonly ObjectPoolPolicy<T>? _fastPolicy; // This class was introduced in 2.1 to avoid the interface call where possible


    /// <summary>
    /// Creates an instance of <see cref="DefaultObjectPool{T}{T}"/>.
    /// </summary>
    /// <param name="policy">The pooling policy to use.</param>
    public DefaultObjectPool(IObjectPoolPolicy<T> policy)
        : this(policy, Environment.ProcessorCount * 2)
    {
    }

    /// <summary>
    /// Creates an instance of <see cref="DefaultObjectPool{T}"/>.
    /// </summary>
    /// <param name="policy">The pooling policy to use.</param>
    /// <param name="maximumRetained">The maximum number of objects to retain in the pool.</param>
    public DefaultObjectPool(IObjectPoolPolicy<T> policy, int maximumRetained)
    {
        _poolPolicy = policy ?? throw new ArgumentNullException(nameof(policy));
        _fastPolicy = policy as ObjectPoolPolicy<T>;
        _isDefaultPolicy = IsDefaultPolicy();

        // -1 due to _firstItem
        _poolElements = new ObjectWrapper[maximumRetained - 1];

        bool IsDefaultPolicy()
        {
            var type = policy.GetType();

            return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(DefaultObjectPoolPolicy<>);
        }
    }

    /// <inheritdoc />
    public override T Rent()
    {
        var item = firstElement;
        if (item == null || Interlocked.CompareExchange(ref firstElement, null, item) != item)
        {
            var items = _poolElements;
            for (var i = 0; i < items.Length; i++)
            {
                item = items[i].Element;
                if (item != null && Interlocked.CompareExchange(ref items[i].Element, null, item) == item)
                {
                    return item;
                }
            }

            item = Create();
        }

        return item;
    }

    // Non-inline to improve its code quality as uncommon path
    [MethodImpl(MethodImplOptions.NoInlining)]
    private T Create() => _fastPolicy?.Create() ?? _poolPolicy.Create();

    /// <inheritdoc />
    public override void Return(T instance)
    {
        if (_isDefaultPolicy || (_fastPolicy?.Return(instance) ?? _poolPolicy.Return(instance)))
        {
            if (firstElement != null || Interlocked.CompareExchange(ref firstElement, instance, null) != null)
            {
                var items = _poolElements;

                for (var i = 0; i < items.Length && Interlocked.CompareExchange(ref items[i].Element, instance, null) != null; ++i)
                {
                }
            }
        }
    }

    // PERF: the struct wrapper avoids array-covariance-checks from the runtime when assigning to elements of the array.
    [DebuggerDisplay("{Element}")]
    private protected struct ObjectWrapper
    {
        public T? Element;
    }
}
