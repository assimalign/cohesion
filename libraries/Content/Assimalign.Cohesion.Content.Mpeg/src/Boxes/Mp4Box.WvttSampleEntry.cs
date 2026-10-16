using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Mpeg.Internal;

namespace Assimalign.Cohesion.Content.Mpeg;

/// <summary>The wvtt sample entry, preserving its prefix and ordered child boxes; ISO/IEC 14496-12 8.5.2.</summary>
// Deviates from the repo interface-first rule per the Stage 4 brief's sealed composite box model.
public sealed class WvttSampleEntryBox : BmffBoxComposite, IMp4SampleEntry
{
    private readonly Mp4SampleEntryFields _fields;
    /// <summary>Creates an empty sample entry for editing and writing.</summary>
    public WvttSampleEntryBox() : this(default, 16 * 1024 * 1024) { }
    internal WvttSampleEntryBox(BmffBoxHeader header, int limit)
    {
        _fields = new Mp4SampleEntryFields("Text", 8, limit);
    }
    Mp4SampleEntryFields IMp4SampleEntry.Fields => _fields;
    /// <summary>Gets or sets the one-based data-reference index.</summary>
    public ushort DataReferenceIndex { get => _fields.DataReferenceIndex; set => _fields.DataReferenceIndex = value; }
    /// <inheritdoc/>
    public override BmffBoxType BoxType => (BmffBoxType)0x77767474;
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
