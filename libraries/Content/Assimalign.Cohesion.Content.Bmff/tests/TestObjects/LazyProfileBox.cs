namespace Assimalign.Cohesion.Content.Bmff.Tests;

// Original ISO/IEC 14496-12 extension fixture; registered as mdat to reuse the sparse source.
internal sealed class LazyProfileBox : BmffBoxLazyLeaf
{
    public override long Offset => Header.Offset;
    public override long Limit => Header.TotalSize ?? 0;
    public override BmffBoxType BoxType => BmffBoxType.MediaData;
    internal byte[] Materialize() => ReadPayloadBytes();
}
