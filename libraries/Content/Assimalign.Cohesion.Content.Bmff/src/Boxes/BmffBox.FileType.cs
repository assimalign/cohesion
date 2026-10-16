using System.Buffers.Binary;
using System;

using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff.Internal;

namespace Assimalign.Cohesion.Content.Bmff;

/// <summary>The file brand and compatible-brand declarations.</summary>
public sealed class FileTypeBox : BmffBox
{
    private uint[] _compatibleBrands = Array.Empty<uint>();
    /// <summary>Creates a file-type box at an original offset.</summary>
    /// <param name="offset">The original header offset.</param>
    public FileTypeBox(long offset) { Offset = offset; }
    /// <summary>Creates a file-type box with its original location.</summary>
    /// <param name="offset">The original header offset.</param>
    /// <param name="limit">The original total size.</param>
    public FileTypeBox(long offset, long limit) { Offset = offset; Limit = limit; }
    /// <inheritdoc/>
    public override bool IsLeaf => true;
    /// <inheritdoc/>
    public override bool IsComposite => false;
    /// <inheritdoc/>
    public override long Limit { get; }
    /// <inheritdoc/>
    public override long Offset { get; }
    /// <inheritdoc/>
    public override BmffBoxType BoxType => BmffBoxType.FileType;
    /// <summary>Gets or sets the major brand FourCC.</summary>
    public uint MajorBrand { get; set; }
    /// <summary>Gets or sets the minor version integer (the legacy property name is retained).</summary>
    public uint MinorBrand { get; set; }
    /// <summary>Gets or sets compatible-brand FourCCs.</summary>
    public uint[] CompatibleBrands { get => _compatibleBrands; set => _compatibleBrands = value ?? Array.Empty<uint>(); }
    /// <summary>Formats the major brand as four ISO-8859-1 characters.</summary>
    /// <returns>The brand text.</returns>
    public string GetMajorBrand() => FourCC.FromUInt32(MajorBrand).ToString();
    /// <summary>Formats the legacy minor-brand value as four ISO-8859-1 characters.</summary>
    /// <returns>The raw text, not the numeric minor version.</returns>
    public string GetMinorBrand() => FourCC.FromUInt32(MinorBrand).ToString();
    /// <summary>Formats each compatible brand.</summary>
    /// <returns>The brand strings.</returns>
    public string[] GetCompatibleBrands()
    {
        var result = new string[_compatibleBrands.Length];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = FourCC.FromUInt32(_compatibleBrands[index]).ToString();
        }
        return result;
    }
    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        if (stream.Remaining < 8)
        {
            throw new ContentFormatException(ErrorMessages.TruncatedHeader, ContentFormatErrorCode.Truncated, stream.Offset + stream.Length);
        }
        if ((stream.Remaining - 8) % 4 != 0)
        {
            throw new ContentFormatException(ErrorMessages.InvalidBrands, ContentFormatErrorCode.InvalidStructure, stream.Offset);
        }
        if (stream.Remaining > stream.Options.MaxBufferedPayloadSize)
        {
            throw new ContentFormatException(ErrorMessages.BufferLimit, ContentFormatErrorCode.LimitExceeded, stream.Offset);
        }
        Span<byte> bytes = stackalloc byte[4];
        MajorBrand = BmffBinary.ReadField(stream, bytes).ReadUInt32BigEndian();
        MinorBrand = BmffBinary.ReadField(stream, bytes).ReadUInt32BigEndian();
        _compatibleBrands = new uint[(int)(stream.Remaining / 4)];
        for (var index = 0; index < _compatibleBrands.Length; index++)
        {
            _compatibleBrands[index] = BmffBinary.ReadField(stream, bytes).ReadUInt32BigEndian();
        }
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, MajorBrand);
        stream.Write(bytes);
        BinaryPrimitives.WriteUInt32BigEndian(bytes, MinorBrand);
        stream.Write(bytes);
        foreach (var brand in _compatibleBrands)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes, brand);
            stream.Write(bytes);
        }
    }
    /// <inheritdoc/>
    public override long GetPayloadSize() => 8L + 4L * _compatibleBrands.Length;
    /// <inheritdoc/>
    public override T Accept<T>(IBmffBoxVisitor<T> visitor) => visitor.Visit(this);
    /// <inheritdoc/>
    public override void Accept(IBmffBoxVisitor visitor) => visitor.Visit(this);
}
