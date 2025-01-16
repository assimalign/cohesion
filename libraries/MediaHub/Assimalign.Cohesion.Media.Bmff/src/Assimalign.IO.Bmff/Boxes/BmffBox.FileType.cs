using System;
using System.Diagnostics;
using static System.Text.Encoding;
using static System.Buffers.Binary.BinaryPrimitives;

namespace Assimalign.IO.Bmff;


[DebuggerDisplay("Bmff Box: File Type (ftyp)")]
public sealed class FileTypeBox : BmffBox
{
    private uint[] compatibleBrands;
    private uint majorBrand;
    private uint minorBrand;

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
        get => majorBrand;
        init => majorBrand = value;
    }
    /// <summary>
    /// 
    /// </summary>
    public uint MinorBrand
    {
        get => minorBrand;
        init => minorBrand = value;
    }
    /// <summary>
    /// Represents a collection of other ISO BMFF Types this file is compatible with.
    /// </summary>
    public uint[] CompatableBrands
    {
        get => compatibleBrands; 
        init => compatibleBrands = value ?? Array.Empty<uint>();
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
        var values = new string[compatibleBrands.Length];

        for (int i = 0; i < values.Length; i++)
        {
            var span = new Span<byte>(new byte[4]);
            WriteInt32BigEndian(span, (int)compatibleBrands[i]);
            values[i] = UTF8.GetString(span.ToArray());
        }

        return values;
    }

    public override void Read(BmffStream stream)
    {
        majorBrand = (uint)ReadInt32BigEndian(stream.ReadBytes(4));
        minorBrand = (uint)ReadInt32BigEndian(stream.ReadBytes(4));

        var remaining = stream.Remaining / 4;

        compatibleBrands = new uint[remaining];

        for (int i = 0; i < remaining; i++) 
        {
            compatibleBrands[i] = (uint)ReadInt32BigEndian(stream.ReadBytes(4));
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
