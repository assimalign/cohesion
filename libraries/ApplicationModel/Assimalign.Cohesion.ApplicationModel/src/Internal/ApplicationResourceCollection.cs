using System;
using System.Collections.ObjectModel;

namespace Assimalign.Cohesion.ApplicationModel;

/// <summary>
/// The default <see cref="IApplicationResourceCollection"/>: an ordered list of resources
/// that enforces resource-name uniqueness on insert and replace.
/// </summary>
internal sealed class ApplicationResourceCollection : Collection<IApplicationResource>, IApplicationResourceCollection
{
    protected override void InsertItem(int index, IApplicationResource item)
    {
        ArgumentNullException.ThrowIfNull(item);
        EnsureUniqueName(item, ignoreIndex: -1);
        base.InsertItem(index, item);
    }

    protected override void SetItem(int index, IApplicationResource item)
    {
        ArgumentNullException.ThrowIfNull(item);
        EnsureUniqueName(item, ignoreIndex: index);
        base.SetItem(index, item);
    }

    private void EnsureUniqueName(IApplicationResource item, int ignoreIndex)
    {
        for (int i = 0; i < Count; i++)
        {
            if (i == ignoreIndex)
            {
                continue;
            }

            if (this[i].Name == item.Name)
            {
                throw new InvalidOperationException(
                    $"A resource named '{item.Name}' has already been added; resource names must be unique within an application.");
            }
        }
    }
}
