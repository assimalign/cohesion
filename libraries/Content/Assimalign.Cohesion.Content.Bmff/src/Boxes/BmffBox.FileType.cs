using System;
using System.Diagnostics;
using static System.Text.Encoding;
using static System.Buffers.Binary.BinaryPrimitives;

using Assimalign.Cohesion.Content.Media;
using Assimalign.IO;

namespace Assimalign.Cohesion.Files.Bmff;


[DebuggerDisplay("Bmff Box: File Type (ftyp)")]
public sealed class FileTypeBox : BmffBox
{
    private uint[] _compatibleBrands;
    private uint _majorBrand;
    private uint _minorBrand;

    public FileTypeBox(long offset)
    {
        this.Offset = offset;
    }
    public FileTypeBox(long offset, long limit)
    {
        this.Offset = offset;
        this.Limit = limit;
    }


    /// <inheritdoc />
    public override bool IsLeaf => true;

    /// <inheritdoc />
    public override bool IsComposite => false;

    /// <inheritdoc />
    public override long Limit { get; }

    /// <inheritdoc />
    public override long Offset { get; }

    /// <inheritdoc />
    public override BmffBoxType BoxType => BmffBoxType.FileType;
    /// <summary>
    /// 
    /// </summary>
    public uint MajorBrand
    {
        get => _majorBrand;
        init => _majorBrand = value;
    }
    /// <summary>
    /// 
    /// </summary>
    public uint MinorBrand
    {
        get => _minorBrand;
        init => _minorBrand = value;
    }
    /// <summary>
    /// Represents a collection of other ISO BMFF Types this file is compatible with.
    /// </summary>
    public uint[] CompatableBrands
    {
        get => _compatibleBrands; 
        init => _compatibleBrands = value ?? Array.Empty<uint>();
    }

   
    public string GetMajorBrand()
    {
        var span = new Span<byte>(new byte[4]);
        WriteInt32BigEndian(span, (int)MajorBrand);
        return UTF8.GetString(span.ToArray());
    }
    public string GetMinorBrand()
    {
        var span = new Span<byte>(new byte[4]);
        WriteInt32BigEndian(span, (int)MinorBrand);
        return UTF8.GetString(span.ToArray());
    }
    public string[] GetCompatibleBrands()
    {
        var values = new string[_compatibleBrands.Length];

        for (int i = 0; i < values.Length; i++)
        {
            var span = new Span<byte>(new byte[4]);
            WriteInt32BigEndian(span, (int)_compatibleBrands[i]);
            values[i] = UTF8.GetString(span.ToArray());
        }

        return values;
    }

    public override void Read(BmffStream stream)
    {
        _majorBrand = (uint)ReadInt32BigEndian(stream.ReadBytes(4));
        _minorBrand = (uint)ReadInt32BigEndian(stream.ReadBytes(4));

        var remaining = stream.Remaining / 4;

        _compatibleBrands = new uint[remaining];

        for (int i = 0; i < remaining; i++) 
        {
            _compatibleBrands[i] = (uint)ReadInt32BigEndian(stream.ReadBytes(4));
        }
    }

    public override void Write(BmffStream stream)
    {
        
    }



    public override T Accept<T>(IBmffBoxVisitor<T> visitor)
    {
        return visitor.Visit(this);
    }
}
