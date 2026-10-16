# Matroska and WebM overview

Content.Mkv supplies the retained Matroska schema, block framing and media semantics over the
generic EBML engine. It directly references Content.Ebml and Content.Media. The dependency graph,
wire scope, ownership, query costs and current core prerequisites are in [DESIGN.md](DESIGN.md).

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

Use the existing EBML reader with the registry; no additional Matroska framing parser is needed.

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
        if (header.Type == EbmlElementType.MasterElement)
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

Unknown-size Clusters terminate through the schema-aware sibling lookahead. Non-seekable
`ReadBinary` would materialize within EBML's cap; use `Skip` for large payloads. The EBML core
does not yet expose a forward-only payload stream, so lazy frame segments require the document
mode's seekable input. A live document is a fixed snapshot of available bytes, with no refresh.

## Emit forward-only output

`EbmlWriter.Create(output, MkvSchema.Matroska)` writes Matroska elements without seeking when
sizes are supplied or when Segment/Cluster sizes are unknown. `WriteElement` precomputes master
sizes without materializing binary leaves. On seekable output, `BeginMaster(..., backPatch: true)`
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
languages and flags, and attachment metadata/data can be changed this way. Chapters currently
remain opaque because of the upstream `0x80` prerequisite; they can be preserved or replaced as
opaque bytes, not edited as chapter fields.

```csharp
using var destination = File.Create("edited.webm");
document.Save(destination);
```

Save recalculates SeekHead/Cues cluster positions and removes CRCs from edited masters. Original
clusters and unchanged unknown elements copy through exactly. Call **MkvDocument.Save**, not
`document.Ebml.Save`, when index positions must remain valid. Never save over the source or an alias
of it. Reload the output for relocated sample coordinates. `OpenRead` always returns the original
source snapshot, and `IsReadOnly` refers to that serialized source, not the editable metadata tree.

Use the attachment's FileData `BinarySegment` for lazy access. `ReadAttachmentData` is the explicit
bounded convenience operation; its default materialization cap is 16 MiB.
