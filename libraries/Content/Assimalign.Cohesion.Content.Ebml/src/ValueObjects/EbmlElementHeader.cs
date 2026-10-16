namespace Assimalign.Cohesion.Content.Ebml;

/// <summary>The immutable wire header of the current element.</summary>
public readonly struct EbmlElementHeader
{
    internal EbmlElementHeader(VInt identifier, VInt size, long offset, EbmlElementType type)
    {
        Identifier = identifier;
        DataSize = size.IsReserved ? null : (long)size.Value;
        Offset = offset;
        HeaderLength = identifier.Length + size.Length;
        SizeLength = size.Length;
        Type = type;
    }
    /// <summary>Gets the encoded element ID.</summary>
    public VInt Identifier { get; }
    /// <summary>Gets the data length, or null for unknown size.</summary>
    public long? DataSize { get; }
    /// <summary>Gets whether the size is unknown.</summary>
    public bool IsUnknownSize => DataSize is null;
    /// <summary>Gets the absolute header offset.</summary>
    public long Offset { get; }
    /// <summary>Gets the combined ID and size width.</summary>
    public int HeaderLength { get; }
    /// <summary>Gets the encoded size width.</summary>
    public int SizeLength { get; }
    /// <summary>Gets the absolute data offset.</summary>
    public long DataOffset => Offset + HeaderLength;
    /// <summary>Gets the schema-resolved type, or binary for an unknown ID.</summary>
    public EbmlElementType Type { get; }
}
