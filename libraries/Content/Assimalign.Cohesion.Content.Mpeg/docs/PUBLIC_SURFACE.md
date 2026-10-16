# Content.Mpeg public surface

All declarations are new in Stage 4, in namespace `Assimalign.Cohesion.Content.Mpeg`.
No existing shipped public type or interface was modified or removed. Overrides declared on
new box classes are listed; unchanged inherited BMFF members are not repeated. Internal track
and sample-index implementations are intentionally absent. Each bullet is one declaration.

These are concrete names for the profile, boxes, brands and limits sanctioned by the brief;
the brief did not prescribe their CLR names, so this is also the complete name-level review inventory.

## Ac3SampleEntryBox

- `public Ac3SampleEntryBox()`
- `public double SampleRate { get; set; }`
- `public override BmffBoxType BoxType { get; }`
- `public override long GetPayloadSize()`
- `public override long Limit { get; }`
- `public override long Offset { get; }`
- `public override void Read(BmffStream stream)`
- `public override void Write(BmffStream stream)`
- `public sealed class Ac3SampleEntryBox : BmffBoxComposite`
- `public ushort ChannelCount { get; set; }`
- `public ushort DataReferenceIndex { get; set; }`

## Ac3SpecificBox

- `public Ac3SpecificBox()`
- `public BinarySegment Data { get; set; }`
- `public override BmffBoxType BoxType { get; }`
- `public override bool IsComposite { get; }`
- `public override bool IsLeaf { get; }`
- `public override IList<BmffBox> Children { get; }`
- `public override long GetPayloadSize()`
- `public override long Limit { get; }`
- `public override long Offset { get; }`
- `public override void Read(BmffStream stream)`
- `public override void Write(BmffStream stream)`
- `public sealed class Ac3SpecificBox : BmffBoxComposite`

## Av01SampleEntryBox

- `public Av01SampleEntryBox()`
- `public override BmffBoxType BoxType { get; }`
- `public override long GetPayloadSize()`
- `public override long Limit { get; }`
- `public override long Offset { get; }`
- `public override void Read(BmffStream stream)`
- `public override void Write(BmffStream stream)`
- `public sealed class Av01SampleEntryBox : BmffBoxComposite`
- `public string CompressorName { get; set; }`
- `public ushort DataReferenceIndex { get; set; }`
- `public ushort Height { get; set; }`
- `public ushort Width { get; set; }`

## Av1ConfigurationBox

- `public Av1ConfigurationBox()`
- `public BinarySegment Data { get; set; }`
- `public override BmffBoxType BoxType { get; }`
- `public override bool IsComposite { get; }`
- `public override bool IsLeaf { get; }`
- `public override IList<BmffBox> Children { get; }`
- `public override long GetPayloadSize()`
- `public override long Limit { get; }`
- `public override long Offset { get; }`
- `public override void Read(BmffStream stream)`
- `public override void Write(BmffStream stream)`
- `public sealed class Av1ConfigurationBox : BmffBoxComposite`

## Avc1SampleEntryBox

- `public Avc1SampleEntryBox()`
- `public override BmffBoxType BoxType { get; }`
- `public override long GetPayloadSize()`
- `public override long Limit { get; }`
- `public override long Offset { get; }`
- `public override void Read(BmffStream stream)`
- `public override void Write(BmffStream stream)`
- `public sealed class Avc1SampleEntryBox : BmffBoxComposite`
- `public string CompressorName { get; set; }`
- `public ushort DataReferenceIndex { get; set; }`
- `public ushort Height { get; set; }`
- `public ushort Width { get; set; }`

## Avc3SampleEntryBox

- `public Avc3SampleEntryBox()`
- `public override BmffBoxType BoxType { get; }`
- `public override long GetPayloadSize()`
- `public override long Limit { get; }`
- `public override long Offset { get; }`
- `public override void Read(BmffStream stream)`
- `public override void Write(BmffStream stream)`
- `public sealed class Avc3SampleEntryBox : BmffBoxComposite`
- `public string CompressorName { get; set; }`
- `public ushort DataReferenceIndex { get; set; }`
- `public ushort Height { get; set; }`
- `public ushort Width { get; set; }`

## AvcConfigurationBox

- `public AvcConfigurationBox()`
- `public BinarySegment Data { get; set; }`
- `public override BmffBoxType BoxType { get; }`
- `public override bool IsComposite { get; }`
- `public override bool IsLeaf { get; }`
- `public override IList<BmffBox> Children { get; }`
- `public override long GetPayloadSize()`
- `public override long Limit { get; }`
- `public override long Offset { get; }`
- `public override void Read(BmffStream stream)`
- `public override void Write(BmffStream stream)`
- `public sealed class AvcConfigurationBox : BmffBoxComposite`

## BitRateBox

- `public BinarySegment Data { get; set; }`
- `public BitRateBox()`
- `public override BmffBoxType BoxType { get; }`
- `public override bool IsComposite { get; }`
- `public override bool IsLeaf { get; }`
- `public override IList<BmffBox> Children { get; }`
- `public override long GetPayloadSize()`
- `public override long Limit { get; }`
- `public override long Offset { get; }`
- `public override void Read(BmffStream stream)`
- `public override void Write(BmffStream stream)`
- `public sealed class BitRateBox : BmffBoxComposite`

## ColourInformationBox

- `public BinarySegment Data { get; set; }`
- `public ColourInformationBox()`
- `public override BmffBoxType BoxType { get; }`
- `public override bool IsComposite { get; }`
- `public override bool IsLeaf { get; }`
- `public override IList<BmffBox> Children { get; }`
- `public override long GetPayloadSize()`
- `public override long Limit { get; }`
- `public override long Offset { get; }`
- `public override void Read(BmffStream stream)`
- `public override void Write(BmffStream stream)`
- `public sealed class ColourInformationBox : BmffBoxComposite`

## Ec3SampleEntryBox

- `public double SampleRate { get; set; }`
- `public Ec3SampleEntryBox()`
- `public override BmffBoxType BoxType { get; }`
- `public override long GetPayloadSize()`
- `public override long Limit { get; }`
- `public override long Offset { get; }`
- `public override void Read(BmffStream stream)`
- `public override void Write(BmffStream stream)`
- `public sealed class Ec3SampleEntryBox : BmffBoxComposite`
- `public ushort ChannelCount { get; set; }`
- `public ushort DataReferenceIndex { get; set; }`

## Ec3SpecificBox

- `public BinarySegment Data { get; set; }`
- `public Ec3SpecificBox()`
- `public override BmffBoxType BoxType { get; }`
- `public override bool IsComposite { get; }`
- `public override bool IsLeaf { get; }`
- `public override IList<BmffBox> Children { get; }`
- `public override long GetPayloadSize()`
- `public override long Limit { get; }`
- `public override long Offset { get; }`
- `public override void Read(BmffStream stream)`
- `public override void Write(BmffStream stream)`
- `public sealed class Ec3SpecificBox : BmffBoxComposite`

## ElementaryStreamDescriptorBox

- `public BinarySegment Data { get; set; }`
- `public ElementaryStreamDescriptorBox()`
- `public override BmffBoxType BoxType { get; }`
- `public override bool IsComposite { get; }`
- `public override bool IsLeaf { get; }`
- `public override IList<BmffBox> Children { get; }`
- `public override long GetPayloadSize()`
- `public override long Limit { get; }`
- `public override long Offset { get; }`
- `public override void Read(BmffStream stream)`
- `public override void Write(BmffStream stream)`
- `public sealed class ElementaryStreamDescriptorBox : BmffBoxComposite`

## Hev1SampleEntryBox

- `public Hev1SampleEntryBox()`
- `public override BmffBoxType BoxType { get; }`
- `public override long GetPayloadSize()`
- `public override long Limit { get; }`
- `public override long Offset { get; }`
- `public override void Read(BmffStream stream)`
- `public override void Write(BmffStream stream)`
- `public sealed class Hev1SampleEntryBox : BmffBoxComposite`
- `public string CompressorName { get; set; }`
- `public ushort DataReferenceIndex { get; set; }`
- `public ushort Height { get; set; }`
- `public ushort Width { get; set; }`

## HevcConfigurationBox

- `public BinarySegment Data { get; set; }`
- `public HevcConfigurationBox()`
- `public override BmffBoxType BoxType { get; }`
- `public override bool IsComposite { get; }`
- `public override bool IsLeaf { get; }`
- `public override IList<BmffBox> Children { get; }`
- `public override long GetPayloadSize()`
- `public override long Limit { get; }`
- `public override long Offset { get; }`
- `public override void Read(BmffStream stream)`
- `public override void Write(BmffStream stream)`
- `public sealed class HevcConfigurationBox : BmffBoxComposite`

## Hvc1SampleEntryBox

- `public Hvc1SampleEntryBox()`
- `public override BmffBoxType BoxType { get; }`
- `public override long GetPayloadSize()`
- `public override long Limit { get; }`
- `public override long Offset { get; }`
- `public override void Read(BmffStream stream)`
- `public override void Write(BmffStream stream)`
- `public sealed class Hvc1SampleEntryBox : BmffBoxComposite`
- `public string CompressorName { get; set; }`
- `public ushort DataReferenceIndex { get; set; }`
- `public ushort Height { get; set; }`
- `public ushort Width { get; set; }`

## Mp4aSampleEntryBox

- `public double SampleRate { get; set; }`
- `public Mp4aSampleEntryBox()`
- `public override BmffBoxType BoxType { get; }`
- `public override long GetPayloadSize()`
- `public override long Limit { get; }`
- `public override long Offset { get; }`
- `public override void Read(BmffStream stream)`
- `public override void Write(BmffStream stream)`
- `public sealed class Mp4aSampleEntryBox : BmffBoxComposite`
- `public ushort ChannelCount { get; set; }`
- `public ushort DataReferenceIndex { get; set; }`

## Mp4BrandFeatures

- `Mp4BrandFeatures.AudioOnly = 16`
- `Mp4BrandFeatures.Avc = 8`
- `Mp4BrandFeatures.Cmaf = 256`
- `Mp4BrandFeatures.CmafChunk = 2048`
- `Mp4BrandFeatures.CmafSegment = 1024`
- `Mp4BrandFeatures.CmafVersion2 = 512`
- `Mp4BrandFeatures.Dash = 64`
- `Mp4BrandFeatures.Fragmented = 128`
- `Mp4BrandFeatures.IsoBaseMedia = 1`
- `Mp4BrandFeatures.Mp4Version1 = 2`
- `Mp4BrandFeatures.Mp4Version2 = 4`
- `Mp4BrandFeatures.None = 0`
- `Mp4BrandFeatures.Video = 32`
- `public enum Mp4BrandFeatures`

## Mp4File

- `public BmffDocument Document { get; }`
- `public bool CanReopen { get; }`
- `public bool IsFragmented { get; }`
- `public bool IsLive { get; }`
- `public bool IsReadOnly { get; }`
- `public ContentFormat Format { get; }`
- `public FourCC MajorBrand { get; }`
- `public IReadOnlyList<ContentFormatException> Diagnostics { get; }`
- `public IReadOnlyList<FourCC> CompatibleBrands { get; }`
- `public IReadOnlyList<IMediaTrack> Tracks { get; }`
- `public long? Length { get; }`
- `public MediaTimestamp? Duration { get; }`
- `public Mp4BrandFeatures BrandFeatures { get; }`
- `public sealed class Mp4File : IMediaFile`
- `public static Mp4File Load(Stream source, Mp4ReaderOptions? options = null, bool leaveOpen = true, CancellationToken cancellationToken = default)`
- `public static ValueTask<Mp4File> LoadAsync(Stream source, Mp4ReaderOptions? options = null, bool leaveOpen = true, CancellationToken cancellationToken = default)`
- `public Stream OpenRead()`
- `public string? MediaType { get; }`
- `public string? Name { get; }`
- `public ValueTask DisposeAsync()`
- `public ValueTask<Stream> OpenReadAsync(CancellationToken cancellationToken = default)`
- `public void Dispose()`

## Mp4Profile

- `public static BmffBoxRegistry CreateRegistry(BmffReaderOptions? options = null)`
- `public static BmffDocument LoadDocument(Stream source, BmffReaderOptions? options = null, bool leaveOpen = true, CancellationToken cancellationToken = default)`
- `public static bool IsFragmented(BmffDocument document)`
- `public static Mp4BrandFeatures InterpretBrand(FourCC brand)`
- `public static partial class Mp4Profile`
- `public static void FastStart(BmffDocument document)`

## Mp4ReaderOptions

- `public BmffReaderOptions ContainerOptions { get; init; }`
- `public bool Strict { get; init; }`
- `public int MaxFragmentRunCount { get; init; }`
- `public int MaxTrackCount { get; init; }`
- `public Mp4ReaderOptions()`
- `public sealed class Mp4ReaderOptions`

## OpusSampleEntryBox

- `public double SampleRate { get; set; }`
- `public OpusSampleEntryBox()`
- `public override BmffBoxType BoxType { get; }`
- `public override long GetPayloadSize()`
- `public override long Limit { get; }`
- `public override long Offset { get; }`
- `public override void Read(BmffStream stream)`
- `public override void Write(BmffStream stream)`
- `public sealed class OpusSampleEntryBox : BmffBoxComposite`
- `public ushort ChannelCount { get; set; }`
- `public ushort DataReferenceIndex { get; set; }`

## OpusSpecificBox

- `public BinarySegment Data { get; set; }`
- `public OpusSpecificBox()`
- `public override BmffBoxType BoxType { get; }`
- `public override bool IsComposite { get; }`
- `public override bool IsLeaf { get; }`
- `public override IList<BmffBox> Children { get; }`
- `public override long GetPayloadSize()`
- `public override long Limit { get; }`
- `public override long Offset { get; }`
- `public override void Read(BmffStream stream)`
- `public override void Write(BmffStream stream)`
- `public sealed class OpusSpecificBox : BmffBoxComposite`

## PixelAspectRatioBox

- `public BinarySegment Data { get; set; }`
- `public override BmffBoxType BoxType { get; }`
- `public override bool IsComposite { get; }`
- `public override bool IsLeaf { get; }`
- `public override IList<BmffBox> Children { get; }`
- `public override long GetPayloadSize()`
- `public override long Limit { get; }`
- `public override long Offset { get; }`
- `public override void Read(BmffStream stream)`
- `public override void Write(BmffStream stream)`
- `public PixelAspectRatioBox()`
- `public sealed class PixelAspectRatioBox : BmffBoxComposite`

## StppSampleEntryBox

- `public override BmffBoxType BoxType { get; }`
- `public override long GetPayloadSize()`
- `public override long Limit { get; }`
- `public override long Offset { get; }`
- `public override void Read(BmffStream stream)`
- `public override void Write(BmffStream stream)`
- `public sealed class StppSampleEntryBox : BmffBoxComposite`
- `public StppSampleEntryBox()`
- `public ushort DataReferenceIndex { get; set; }`

## Tx3gSampleEntryBox

- `public override BmffBoxType BoxType { get; }`
- `public override long GetPayloadSize()`
- `public override long Limit { get; }`
- `public override long Offset { get; }`
- `public override void Read(BmffStream stream)`
- `public override void Write(BmffStream stream)`
- `public sealed class Tx3gSampleEntryBox : BmffBoxComposite`
- `public Tx3gSampleEntryBox()`
- `public ushort DataReferenceIndex { get; set; }`

## Vp09SampleEntryBox

- `public override BmffBoxType BoxType { get; }`
- `public override long GetPayloadSize()`
- `public override long Limit { get; }`
- `public override long Offset { get; }`
- `public override void Read(BmffStream stream)`
- `public override void Write(BmffStream stream)`
- `public sealed class Vp09SampleEntryBox : BmffBoxComposite`
- `public string CompressorName { get; set; }`
- `public ushort DataReferenceIndex { get; set; }`
- `public ushort Height { get; set; }`
- `public ushort Width { get; set; }`
- `public Vp09SampleEntryBox()`

## VpConfigurationBox

- `public BinarySegment Data { get; set; }`
- `public override BmffBoxType BoxType { get; }`
- `public override bool IsComposite { get; }`
- `public override bool IsLeaf { get; }`
- `public override IList<BmffBox> Children { get; }`
- `public override long GetPayloadSize()`
- `public override long Limit { get; }`
- `public override long Offset { get; }`
- `public override void Read(BmffStream stream)`
- `public override void Write(BmffStream stream)`
- `public sealed class VpConfigurationBox : BmffBoxComposite`
- `public VpConfigurationBox()`

## WvttSampleEntryBox

- `public override BmffBoxType BoxType { get; }`
- `public override long GetPayloadSize()`
- `public override long Limit { get; }`
- `public override long Offset { get; }`
- `public override void Read(BmffStream stream)`
- `public override void Write(BmffStream stream)`
- `public sealed class WvttSampleEntryBox : BmffBoxComposite`
- `public ushort DataReferenceIndex { get; set; }`
- `public WvttSampleEntryBox()`
