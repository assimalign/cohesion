# Matroska and WebM overview

Content.Mkv supplies the retained Matroska schema, block framing and media semantics over the
generic EBML engine. It directly references Content.Ebml and Content.Media. The dependency graph,
wire scope, ownership, query costs and streaming contracts are in [DESIGN.md](DESIGN.md).

## Read tracks and seek

```csharp
using System.IO;
using System.Threading;

using Assimalign.Cohesion.Content.Media;
using Assimalign.Cohesion.Content.Mkv;

using var source = File.OpenRead("input.webm");
using var document = MkvDocument.Load(source);
IMediaFile media = document;
var track = media.Tracks[0];
var result = await track.Samples.SeekAsync(
    new MediaTimestamp(1750, 1000), CancellationToken.None);
```

The result identifies a preceding keyframe, a target frame and a contiguous source range.
Initialization metadata is assumed available. A range is not a standalone muxed file. The first
cue-based seek scans framing to retain source ordinals and timing bounds per occupied cluster.
Subsequent seeks use cues and those bounds to read only candidate clusters, including under
presentation reordering. Without cues, each seek scans framing. References and overlapping timing
bounds can increase this cost. `Count` stays unknown rather than triggering a scan. Laced frames
after the first require `DefaultDuration`.

Tracks implement `IMediaVideoTrack` or `IMediaAudioTrack` where appropriate. `CodecId` is the native
Matroska string. `CodecPrivateData` and block frames borrow the source through `BinarySegment`.
Nanosecond PTS includes `CodecDelay`; missing DTS remains null. No bitstream is interpreted.

## Inspect forward-only input

Use the EBML reader with the registry, and open SimpleBlock/Block frames through MkvBlockReader.

```csharp
using Assimalign.Cohesion.Content.Ebml;
using Assimalign.Cohesion.Content.Mkv;

using var reader = EbmlReader.Create(networkStream, registry: MkvSchema.Registry);
Scan();

void Scan()
{
    while (reader.ReadNext())
    {
        var header = reader.Current; // Absolute offset, ID, type, nullable size.
        if (header.Identifier.EncodedValue is 0xA3 or 0xA1)
        {
            using var block = MkvBlockReader.Create(reader);
            while (block.ReadFrame() is { } frame)
            {
                using (frame)
                {
                    frame.CopyTo(frameDestination); // Encoded bytes; no payload-sized buffer.
                }
            }
        }
        else if (header.Type == EbmlElementType.MasterElement)
        {
            reader.EnterMaster();
            Scan();
            reader.LeaveMaster();
        }
        else
        {
            reader.Skip(); // Drains block/attachment bytes without buffering them.
        }
    }
}
```

Unknown-size Clusters terminate through schema-aware sibling lookahead. Each frame is a bounded
forward-only ContentSliceStream and supports asynchronous reads with cancellation. ReadFrame drains
an unread previous frame and invalidates it. Disposing the block reader leaves the enclosing EBML
reader to drain the block remainder on ReadNext/Skip. Neither operation closes borrowed input.
Consume views sequentially; never retain a frame while advancing the enclosing reader.

The block reader exposes track number, relative timestamp, flags, lacing, frame count and absolute
FrameOffsets. Track lookup, cluster timestamps and BlockGroup references remain the enclosing scan's
responsibility. It shares all lacing checks with MkvBlock.Parse, which remains the lazy BinarySegment
API for seekable storage. The EBML buffered-value cap does not apply to streamed frames.
A loaded live document remains a fixed seekable snapshot of available bytes, with no refresh.

## Emit forward-only output

`EbmlWriter.Create(output, MkvSchema.Matroska)` writes Matroska elements without seeking when
sizes are supplied or when Segment/Cluster sizes are unknown. `WriteElement` precomputes master
sizes without materializing binary leaves. EbmlWriter.GetEncodedLength measures complete elements
or values without writing, and WriteRawElement copies a complete original BinarySegment verbatim.
On seekable output, `BeginMaster(..., backPatch: true)`
reserves a width and `EndMaster` fills it. Always explicitly complete each opened master.

Use `MkvSchema.WebM` for the WebM retained element restrictions. Supply a DocType of `webm`, a
compatible codec ID, and the appropriate track metadata; the document adapter checks the codec
subset on load/save. The writer does not decode or validate codec bitstreams. For nonzero numeric
schema defaults, specify a nonempty payload width (for example `length: 1` for FlagEnabled=1),
as required by RFC 9559 §4.4. Omit defaulted fields when the default is intended.

## Edit and save metadata

The native EBML tree is exposed through `Ebml` and `Segment`. Values use `ulong`, `long`, `double`,
`string` or `BinarySegment`; masters have ordered editable `Children`. Find an element by its
encoded ID and replace `Value`, add a typed element, or remove a child. Title, tags, track names,
languages and flags, and attachment metadata/data can be changed this way. Chapters also have typed
editable views; ChapterDisplay's 0x80 is authorized by the active schema while VInt retains its
generic identifier rule.

```csharp
using var destination = File.Create("edited.webm");
document.Save(destination);
```

Save recalculates SeekHead/Cues cluster positions and removes CRCs from edited masters. Original
clusters and unchanged unknown elements copy through exactly. Call **MkvDocument.Save**, not
`document.Ebml.Save`, when index positions must remain valid. Never save over the source or an alias
of it. Reload the output for relocated sample coordinates. `OpenRead` always returns the original
source snapshot, and `IsReadOnly` refers to that serialized source, not the editable metadata tree.

## Edit typed chapters

MkvDocument.Chapters contains editable editions. Each edition has Chapters; each chapter has
recursive Chapters and localized Displays. Reading these live native-tree views does not insert
default fields or change an unmodified save.

~~~csharp
var edition = document.Chapters.Count == 0 ? new MkvChapterEdition() : document.Chapters[0];
if (document.Chapters.Count == 0)
{
    document.Chapters.Add(edition);
}
var chapter = MkvChapter.Create(42, 1_000_000_000); // Nanoseconds, not scaled segment ticks.
chapter.TimeEnd = 2_000_000_000;
chapter.Displays.Add(MkvChapterDisplay.Create("Next chapter"));
edition.Chapters.Add(chapter);

chapter.Displays[0].Text = "Opening";
chapter.TimeStart = 123;
document.Save(destination);
~~~

Edition Uid, IsDefault and IsOrdered, and chapter Uid, StringUid, IsHidden and IsEnabled are editable.
TimeEnd and StringUid may be removed by assigning null. Display Languages, LanguagesBCP47 and
Countries are ordered explicit wire occurrences; an empty Languages list means the implicit eng
default. Ordered-chapter flags are preserved metadata; playback programs and linked segments are
outside this API. Save rejects missing mandatory fields, a chapter end before its start, and an
ordered leaf without an end.

The first added edition creates Chapters. Removing the final edition removes that entire optional
container. Unknown extensions inside retained chapters survive ordinary field edits. Metadata size
changes still pass through Matroska's SeekHead/Cues position maintenance.

Use the attachment's FileData `BinarySegment` for lazy access. `ReadAttachmentData` is the explicit
bounded convenience operation; its default materialization cap is 16 MiB.
