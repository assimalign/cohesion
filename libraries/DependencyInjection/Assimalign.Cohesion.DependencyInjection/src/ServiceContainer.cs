using Assimalign.Cohesion.DependencyInjection.Properties;
using System;
using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.DependencyInjection;

/// <summary>
/// Default implementation of <see cref="IServiceContainer"/>.
/// </summary>
public sealed class ServiceContainer : IServiceContainer
{
    private readonly List<ServiceDescriptor> _descriptors;
    private bool _isReadOnly;

    public ServiceContainer()
    {
        _descriptors = new List<ServiceDescriptor>();
    }

    public ServiceContainer(IEnumerable<ServiceDescriptor> descriptors) : this()
    {
        _descriptors.AddRange(descriptors);
    }


    /// <inheritdoc />
    public int Count => _descriptors.Count;

    /// <inheritdoc />
    public bool IsReadOnly => _isReadOnly;

    /// <inheritdoc />
    public ServiceDescriptor this[int index]
    {
        get
        {
            return _descriptors[index];
        }
        set
        {
            CheckReadOnly();
            _descriptors[index] = value;
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        CheckReadOnly();
        _descriptors.Clear();
    }

    ///// <inheritdoc />
    //public bool Contains(ServiceDescriptor item)
    //{
    //    return _descriptors.Contains(item);
    //}

    /// <inheritdoc />
    public void CopyTo(ServiceDescriptor[] array, int arrayIndex)
    {
        _descriptors.CopyTo(array, arrayIndex);
    }

    ///// <inheritdoc />
    //public bool Remove(ServiceDescriptor item)
    //{
    //    CheckReadOnly();
    //    return _descriptors.Remove(item);
    //}

    /// <inheritdoc />
    public IEnumerator<ServiceDescriptor> GetEnumerator()
    {
        return _descriptors.GetEnumerator();
    }

    //void ICollection<ServiceDescriptor>.Add(ServiceDescriptor item)
    //{
    //    CheckReadOnly();
    //    _descriptors.Add(item);
    //}

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    ///// <inheritdoc />
    //public int IndexOf(ServiceDescriptor item)
    //{
    //    return _descriptors.IndexOf(item);
    //}

    ///// <inheritdoc />
    //public void Insert(int index, ServiceDescriptor item)
    //{
    //    CheckReadOnly();
    //    _descriptors.Insert(index, item);
    //}

    ///// <inheritdoc />
    //public void RemoveAt(int index)
    //{
    //    CheckReadOnly();
    //    _descriptors.RemoveAt(index);
    //}


    /// <summary>
    /// Makes this collection read-only.
    /// </summary>
    /// <remarks>
    /// After the collection is marked as read-only, any further attempt to modify it throws an <see cref="InvalidOperationException" />.
    /// </remarks>
    public void MakeReadOnly()
    {
        _isReadOnly = true;
    }

    private void CheckReadOnly()
    {
        if (_isReadOnly)
        {
            ThrowReadOnlyException();
        }
    }

    private static void ThrowReadOnlyException() =>
        throw new InvalidOperationException(Resources.ServiceCollectionReadOnly);

    public void Register(ServiceDescriptor descriptor)
    {
        CheckReadOnly();
        _descriptors.Add(descriptor);
    }

    public bool Unregister(ServiceDescriptor descriptor)
    {
        CheckReadOnly();
        return _descriptors.Remove(descriptor);
    }

    /// <inheritdoc />
    public void UnregisterAt(int index)
    {
        CheckReadOnly();
        _descriptors.RemoveAt(index);
    }
}
