using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Mpeg.Internal;

namespace Assimalign.Cohesion.Content.Mpeg;

/// <summary>The hvc1 sample entry, preserving its prefix and ordered child boxes; ISO/IEC 14496-12 8.5.2.</summary>
// Deviates from the repo interface-first rule per the Stage 4 brief's sealed composite box model.
public sealed class Hvc1SampleEntryBox : BmffBoxComposite, IMp4SampleEntry
{
    private readonly Mp4SampleEntryFields _fields;
    /// <summary>Creates an empty sample entry for editing and writing.</summary>
    public Hvc1SampleEntryBox() : this(default, 16 * 1024 * 1024) { }
    internal Hvc1SampleEntryBox(BmffBoxHeader header, int limit)
    {
        Offset = header.Offset;
        Limit = header.TotalSize ?? 0;
        _fields = new Mp4SampleEntryFields("Visual", 78, limit);
    }
    Mp4SampleEntryFields IMp4SampleEntry.Fields => _fields;
    /// <summary>Gets or sets the one-based data-reference index.</summary>
    public ushort DataReferenceIndex { get => _fields.DataReferenceIndex; set => _fields.DataReferenceIndex = value; }
    /// <summary>Gets or sets the encoded image width in pixels.</summary>
    public ushort Width { get => _fields.Width; set => _fields.Width = value; }
    /// <summary>Gets or sets the encoded image height in pixels.</summary>
    public ushort Height { get => _fields.Height; set => _fields.Height = value; }
    /// <summary>Gets or sets the Pascal ISO-8859-1 compressor label (at most 31 bytes).</summary>
    /// <exception cref="System.ArgumentException">The label is null, too long or not ISO-8859-1.</exception>
    public string CompressorName { get => _fields.CompressorName; set => _fields.CompressorName = value; }
    /// <inheritdoc/>
    public override long Offset { get; }
    /// <inheritdoc/>
    public override long Limit { get; }
    /// <inheritdoc/>
    public override BmffBoxType BoxType => (BmffBoxType)0x68766331;
    /// <inheritdoc/>
    public override void Read(BmffStream stream)
    {
        _fields.Read(stream);
        base.Read(stream);
    }
    /// <inheritdoc/>
    public override void Write(BmffStream stream)
    {
        _fields.Write(stream);
        base.Write(stream);
    }
    /// <inheritdoc/>
    public override long GetPayloadSize() => _fields.Length + base.GetPayloadSize();
}
