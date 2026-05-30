using System.Collections.Generic;

namespace Assimalign.Cohesion.ObjectMapping.Tests;

/// <summary>Source carrying a list of elements, mapped into various target collection shapes.</summary>
public sealed class ItemsSource
{
    public List<LineItemSource>? Items { get; set; }
}

/// <summary>Target whose collection member is a concrete <see cref="List{T}"/>.</summary>
public sealed class ListTarget
{
    public List<LineItemTarget>? Items { get; set; }
}

/// <summary>Target whose collection member is an array.</summary>
public sealed class ArrayTarget
{
    public LineItemTarget[]? Items { get; set; }
}

/// <summary>Target whose collection member is exposed as <see cref="IEnumerable{T}"/>.</summary>
public sealed class EnumerableTarget
{
    public IEnumerable<LineItemTarget>? Items { get; set; }
}

/// <summary>Target whose collection member is exposed as <see cref="IList{T}"/>.</summary>
public sealed class IListTarget
{
    public IList<LineItemTarget>? Items { get; set; }
}

/// <summary>Target whose collection member is a <see cref="HashSet{T}"/>.</summary>
public sealed class SetTarget
{
    public HashSet<LineItemTarget>? Items { get; set; }
}

/// <summary>Target whose collection member is a <see cref="Queue{T}"/>.</summary>
public sealed class QueueTarget
{
    public Queue<LineItemTarget>? Items { get; set; }
}

/// <summary>Target whose collection member is a <see cref="Stack{T}"/>.</summary>
public sealed class StackTarget
{
    public Stack<LineItemTarget>? Items { get; set; }
}
