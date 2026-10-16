using System;

namespace Assimalign.Cohesion.Content.Bmff.Tests;

internal sealed class ProfileBox : BmffFullBox
{
    private readonly BmffBoxHeader _header;
    internal ProfileBox(BmffBoxHeader header) { _header = header; }
    public override long Offset => _header.Offset;
    public override long Limit => _header.TotalSize!.Value;
    public override BmffBoxType BoxType => _header.BoxType;
    public override bool IsLeaf => true;
    public override bool IsComposite => false;
    public override void Read(BmffStream stream)
    {
        ReadFullBoxHeader(stream);
        if (Version != 0 || Flags != 0)
        {
            throw new ContentFormatException("Unsupported fixture version.", ContentFormatErrorCode.Unsupported, Offset);
        }
    }
    public override void Write(BmffStream stream) => WriteFullBoxHeader(stream);
    public override long GetPayloadSize() => 4;
}

internal sealed class CountingVisitor : BmffBoxVisitor<int>
{
    internal int Count { get; private set; }
    protected override int VisitDefault(BmffBox box)
    {
        Count++;
        return base.VisitDefault(box);
    }
}

internal sealed class ProfileContainer : BmffBoxComposite
{
    private readonly BmffBoxHeader _header;
    internal ProfileContainer(BmffBoxHeader header) { _header = header; }
    internal int WriteCalls { get; private set; }
    public override long Offset => _header.Offset;
    public override long Limit => _header.TotalSize!.Value;
    public override BmffBoxType BoxType => _header.BoxType;
    public override void Read(BmffStream stream)
    {
        if (stream.ReadByte() != 42)
        {
            throw new ContentFormatException("Invalid profile prefix.", ContentFormatErrorCode.InvalidValue, stream.Offset);
        }
        base.Read(stream);
    }
    public override void Write(BmffStream stream)
    {
        WriteCalls++;
        stream.WriteByte(42);
        base.Write(stream);
    }
    public override long GetPayloadSize() => 1 + base.GetPayloadSize();
}
