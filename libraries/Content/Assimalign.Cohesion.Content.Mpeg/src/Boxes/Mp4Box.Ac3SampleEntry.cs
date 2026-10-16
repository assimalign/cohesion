using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Mpeg.Internal;

namespace Assimalign.Cohesion.Content.Mpeg;

/// <summary>The ac-3 sample entry, preserving its prefix and ordered child boxes; ISO/IEC 14496-12 8.5.2.</summary>
// Deviates from the repo interface-first rule per the Stage 4 brief's sealed composite box model.
public sealed class Ac3SampleEntryBox : BmffBoxComposite, IMp4SampleEntry
{
    private readonly Mp4SampleEntryFields _fields;
    /// <summary>Creates an empty sample entry for editing and writing.</summary>
    public Ac3SampleEntryBox() : this(default, 16 * 1024 * 1024) { }
    internal Ac3SampleEntryBox(BmffBoxHeader header, int limit)
    {
        Offset = header.Offset;
        Limit = header.TotalSize ?? 0;
        _fields = new Mp4SampleEntryFields("Audio", 28, limit);
    }
    Mp4SampleEntryFields IMp4SampleEntry.Fields => _fields;
    /// <summary>Gets or sets the one-based data-reference index.</summary>
    public ushort DataReferenceIndex { get => _fields.DataReferenceIndex; set => _fields.DataReferenceIndex = value; }
    /// <summary>Gets or sets the encoded channel count.</summary>
    public ushort ChannelCount { get => _fields.ChannelCount; set => _fields.ChannelCount = value; }
    /// <summary>Gets or sets the unsigned 16.16 sampling frequency in hertz.</summary>
    /// <exception cref="System.ArgumentOutOfRangeException">The value is outside unsigned 16.16 range.</exception>
    public double SampleRate { get => _fields.SampleRate; set => _fields.SampleRate = value; }
    /// <inheritdoc/>
    public override long Offset { get; }
    /// <inheritdoc/>
    public override long Limit { get; }
    /// <inheritdoc/>
    public override BmffBoxType BoxType => (BmffBoxType)0x61632d33;
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
