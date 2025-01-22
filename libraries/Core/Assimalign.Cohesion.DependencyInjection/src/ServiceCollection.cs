using Assimalign.Cohesion.DependencyInjection.Properties;
using System;
using System.Collections;
using System.Collections.Generic;

namespace Assimalign.Cohesion.DependencyInjection;

/// <summary>
/// Default implementation of <see cref="IServiceCollection"/>.
/// </summary>
public sealed class ServiceCollection : IServiceCollection
{
    private bool isReadOnly;
    private readonly List<ServiceDescriptor> descriptors = new List<ServiceDescriptor>();

    public ServiceCollection()
    {
            
    }
    public ServiceCollection(IEnumerable<ServiceDescriptor> descriptors)
    {
        this.descriptors.AddRange(descriptors);
    }


    /// <inheritdoc />
    public int Count => descriptors.Count;

    /// <inheritdoc />
    public bool IsReadOnly => isReadOnly;

    /// <inheritdoc />
    public ServiceDescriptor this[int index]
    {
        get
        {
            return descriptors[index];
        }
        set
        {
            CheckReadOnly();
            descriptors[index] = value;
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        CheckReadOnly();
        descriptors.Clear();
    }

    /// <inheritdoc />
    public bool Contains(ServiceDescriptor item)
    {
        return descriptors.Contains(item);
    }

    /// <inheritdoc />
    public void CopyTo(ServiceDescriptor[] array, int arrayIndex)
    {
        descriptors.CopyTo(array, arrayIndex);
    }

    /// <inheritdoc />
    public bool Remove(ServiceDescriptor item)
    {
        CheckReadOnly();
        return descriptors.Remove(item);
    }

    /// <inheritdoc />
    public IEnumerator<ServiceDescriptor> GetEnumerator()
    {
        return descriptors.GetEnumerator();
    }

    void ICollection<ServiceDescriptor>.Add(ServiceDescriptor item)
    {
        CheckReadOnly();
        descriptors.Add(item);
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    /// <inheritdoc />
    public int IndexOf(ServiceDescriptor item)
    {
        return descriptors.IndexOf(item);
    }

    /// <inheritdoc />
    public void Insert(int index, ServiceDescriptor item)
    {
        CheckReadOnly();
        descriptors.Insert(index, item);
    }

    /// <inheritdoc />
    public void RemoveAt(int index)
    {
        CheckReadOnly();
        descriptors.RemoveAt(index);
    }


    /// <summary>
    /// Makes this collection read-only.
    /// </summary>
    /// <remarks>
    /// After the collection is marked as read-only, any further attempt to modify it throws an <see cref="InvalidOperationException" />.
    /// </remarks>
    public void MakeReadOnly()
    {
        isReadOnly = true;
    }

    private void CheckReadOnly()
    {
        if (isReadOnly)
        {
            ThrowReadOnlyException();
        }
    }

    private static void ThrowReadOnlyException() =>
        throw new InvalidOperationException(Resources.ServiceCollectionReadOnly);
}
