using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Ebml.Internal;

namespace Assimalign.Cohesion.Content.Ebml;

/// <summary>An editable typed leaf or master with ordered children.</summary>
/// <remarks>Leaf values use ulong, long, double, string, or BinarySegment. Dates use signed nanoseconds
/// since 2001-01-01T00:00:00 UTC, preserving precision beyond DateTime ticks.</remarks>
[global::System.Diagnostics.DebuggerDisplay("EbmlElement")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class EbmlElement
{
    private object? _value;
    private bool _unknownSize;
    private int _sizeLength;
    private readonly List<EbmlElement> _children = new();

    private EbmlElement(VInt identifier, EbmlElementType type, object? value)
    {
        Identifier = identifier;
        Type = type;
        Value = value;
    }

    /// <summary>Creates a typed leaf or an empty master.</summary>
    /// <param name="identifier">An initialized encoded VINT; schema validation and writing check ID eligibility.</param>
    /// <param name="type">The payload type.</param>
    /// <param name="value">The typed leaf value; null for masters.</param>
    /// <returns>The editable element.</returns>
    /// <exception cref="ArgumentException">The ID or value type is invalid.</exception>
    public static EbmlElement Create(VInt identifier, EbmlElementType type, object? value = null)
    {
        if (identifier.Length == 0)
        {
            throw new ArgumentException(ErrorMessages.InvalidVInt, nameof(identifier));
        }

        return new EbmlElement(identifier, type, value);
    }

    /// <summary>Gets the ID.</summary>
    public VInt Identifier { get; }
    /// <summary>Gets the payload type.</summary>
    public EbmlElementType Type { get; }
    /// <summary>Gets or replaces the typed leaf value; replacement discards preserved wire bytes.</summary>
    /// <exception cref="ArgumentException">The value does not match Type.</exception>
    public object? Value
    {
        get => _value;
        set
        {
            if (!EbmlValueCodec.Matches(Type, value))
            {
                throw new ArgumentException(ErrorMessages.InvalidType, nameof(value));
            }

            _value = value;
            RawPayload = null;
        }
    }
    /// <summary>Gets ordered children of a master.</summary>
    /// <exception cref="InvalidOperationException">This is a leaf.</exception>
    public IList<EbmlElement> Children => Type == EbmlElementType.MasterElement
        ? _children : throw new InvalidOperationException(ErrorMessages.InvalidType);
    /// <summary>Gets or sets whether a master is written with unknown size.</summary>
    /// <exception cref="ArgumentException">Unknown size was requested for a leaf.</exception>
    public bool IsUnknownSize
    {
        get => _unknownSize;
        set
        {
            if (value && Type != EbmlElementType.MasterElement)
            {
                throw new ArgumentException(ErrorMessages.UnknownSize);
            }

            _unknownSize = value;
        }
    }
    /// <summary>Gets or sets the size VINT width; zero selects the smallest width.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The width is outside zero through eight.</exception>
    public int SizeLength
    {
        get => _sizeLength;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 8);
            _sizeLength = value;
        }
    }
    /// <summary>Gets the original absolute header offset, or zero for an element built in code.</summary>
    public long Offset { get; internal set; }

    internal BinarySegment? RawPayload { get; set; }

    private sealed class DebugView
    {
        private readonly EbmlElement _target;

        public DebugView(EbmlElement value)
        {
            _target = value;
        }

        public VInt Identifier => _target.Identifier;
        public EbmlElementType Type => _target.Type;
        public object? Value => _target.Value;
        public bool IsUnknownSize => _target.IsUnknownSize;
        public int SizeLength => _target.SizeLength;
        public long Offset => _target.Offset;
    }
}
