namespace Assimalign.Cohesion.Content.Mpeg.Internal;

internal interface IMp4SampleEntry
{
    ushort DataReferenceIndex { get; }
    Mp4SampleEntryFields Fields { get; }
}
