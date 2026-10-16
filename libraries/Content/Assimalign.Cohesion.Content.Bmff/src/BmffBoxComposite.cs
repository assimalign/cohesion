using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>A BMFF node with an editable ordered list of children.</summary>
public abstract class BmffBoxComposite : BmffBox
{
    /// <summary>Creates an empty composite box.</summary>
    public BmffBoxComposite()
    {
    }

    private long? _plannedChildrenSize;
    internal virtual bool SupportsAsyncPlan => false;

    internal long MeasurePayload(long childrenSize)
    {
        _plannedChildrenSize = childrenSize;
        try
        {
            return GetPayloadSize();
        }
        finally
        {
            _plannedChildrenSize = null;
        }
    }

    /// <inheritdoc/>
    public override bool IsLeaf => false;
    /// <inheritdoc/>
    public override bool IsComposite => true;
    /// <summary>Gets the mutable ordered child list.</summary>
    public virtual IList<BmffBox> Children { get; } = new List<BmffBox>();

    internal virtual int PrefixSize => 0;
    internal virtual int? ReadPrefix(BmffStream stream) => null;
    internal virtual void WritePrefix(BmffStream stream) { }
    internal virtual void ValidateChildren(int? expectedCount)
    {
        if (expectedCount.HasValue && expectedCount.Value != Children.Count)
        {
            throw new ContentFormatException(ErrorMessages.ChildCountMismatch, ContentFormatErrorCode.InvalidStructure, Offset);
        }
    }

    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        Children.Clear();
        var count = ReadPrefix(stream);
        using var reader = stream.ReadChildren();
        while (reader.Read())
        {
            Children.Add(reader.Current);
        }
        ValidateChildren(count);
    }

    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        WritePrefix(stream);
        if (stream.WritePlan is not null)
        {
            stream.WritePlan.WriteChildren(stream);
            return;
        }
        using var writer = BmffWriter.Create(stream);
        foreach (var child in Children)
        {
            writer.Write(child);
        }
    }

    /// <inheritdoc/>
    public override long GetPayloadSize()
    {
        if (_plannedChildrenSize.HasValue)
        {
            if (_plannedChildrenSize.Value > long.MaxValue - PrefixSize)
            {
                throw new ContentFormatException(ErrorMessages.SizeLimit, ContentFormatErrorCode.LimitExceeded, Offset);
            }
            return PrefixSize + _plannedChildrenSize.Value;
        }
        long size = PrefixSize;
        foreach (var child in Children)
        {
            var childSize = BmffWritePlan.Create(child, 0, 64, false).Size;
            if (childSize > long.MaxValue - size)
            {
                throw new ContentFormatException(ErrorMessages.SizeLimit, ContentFormatErrorCode.LimitExceeded, Offset);
            }
            size += childSize;
        }
        return size;
    }
}
