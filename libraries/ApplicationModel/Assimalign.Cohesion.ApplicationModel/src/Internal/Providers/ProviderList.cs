using System;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.ApplicationModel.Internal;

/// <summary>
/// A mutable, ordered provider list that rejects <see langword="null"/> entries. It backs
/// <see cref="ApplicationProviders.CommandInputs"/> and <see cref="ApplicationProviders.Callers"/>
/// until the registrations are frozen.
/// </summary>
/// <typeparam name="T">The provider seam.</typeparam>
internal sealed class ProviderList<T> : Collection<T>
    where T : class
{
    protected override void InsertItem(int index, T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        base.InsertItem(index, item);
    }

    protected override void SetItem(int index, T item)
    {
        ArgumentNullException.ThrowIfNull(item);
        base.SetItem(index, item);
    }
}
