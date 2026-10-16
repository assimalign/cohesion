# Assimalign.Cohesion.Content.Bmff

BMFF provides ISO base media file boxes, forward-only reading/writing, and an editable document
beneath MP4 and HEIF profiles. It references Content.Media and uses Content.Binary for bounded
streams, exact reads, FourCC, fixed-point numbers and lazy BinarySegment payloads.

## Inspect a large source

```csharp
using var reader = BmffReader.Create(input);
while (reader.Read())
{
    if (reader.Header.BoxType == BmffBoxType.Movie)
    {
        using var children = reader.ReadChildren();
        while (children.Read())
        {
            Inspect(children.Header);
        }
    }
}
```

Reading a header never materializes media. On a seekable source, advancing skips media by seeking;
on a network stream, advancing drains unread bytes through a fixed transfer buffer. Payload exposes
the current bounded stream. Consume it before advancing. Current materializes supported metadata;
media and opaque Current nodes need a seekable source for their lazy segments.

## Traverse and inspect a tree

```csharp
using Assimalign.Cohesion.Content.Binary;
using Assimalign.Cohesion.Content.Bmff;

using var document = BmffDocument.Load(input);
var movie = document.First<MovieBox>();
var tracks = movie.Children<TrackBox>();
var headers = document.Descendants<TrackHeaderBox>();
var media = document.FindAll(BmffBoxType.MediaData);
var unknown = document.FirstOrDefault(FourCC.Parse("zzzz"));
BmffTreeFormatter.Write(document, Console.Out);

BmffBoxWalker.Walk(document, (box, depth, path) =>
{
    Console.WriteLine($"{depth}: {string.Join('/', path)}");
    return box.BoxType == BmffBoxType.UserData
        ? BmffWalkAction.SkipSubtree : BmffWalkAction.Continue;
});
```

An excerpt from the progressive fixture's movie subtree:

```text
moov @0x201A size=982 children=3
  mvhd @0x2022 size=108 version=0 flags=0x000000 timescale=1000 duration=4000 next-track-id=3
  trak @0x208E size=459 children=2
    tkhd @0x2096 size=92 version=0 flags=0x000003 track-id=1 duration=4000 dimensions=0x0
```

The walker and typed queries are the primary traversal. They work for BMFF, profile and unknown
boxes without extending a visitor. Accept and both visitor interfaces remain for compatibility;
the interfaces' overload set is closed to profiles because BMFF cannot depend on those packages.
A profile box therefore uses Visit(BmffBox), while generic callbacks see its actual type.

Traversal is iterative, detects cycles, and defaults to depth 64. Enter and optional leave
callbacks receive the node, depth and borrowed FourCC path. SkipSubtree skips children but still
calls leave; Stop ends immediately. Copy the path if retaining it, and do not mutate during a walk.
`Children<T>` visits only direct children. `Descendants<T>`, First/FirstOrDefault and FindAll use
preorder; composite queries exclude the receiver, while document queries include top-level boxes.
First and FirstOrDefault also accept BmffBoxType or FourCC; FindAll accepts either raw identity.
First throws InvalidOperationException on absence; FirstOrDefault returns null. Query depth starts
at each direct child: depth zero rejects visited grandchildren; use `Children<T>` for direct children.

BmffTreeFormatter.Write accepts a TextWriter and leaves it open. Format returns LF-terminated
text. Both escape control characters and use invariant numbers. No payload bytes are read: opaque
segments appear only as lengths, even after the source has been closed. A loaded line combines
original header coordinates and total size with current field values. Unloaded boxes are marked;
inspection never calls GetPayloadSize or computes a new output layout.

Offset keeps its existing name: it is the source read position and does not follow editing,
relocation or saving. Limit is the original total byte count, not an end position. Before loading,
constructor-supplied values are retained (zero if omitted). The base derives both from Header once
loaded, and derives IsLeaf/IsComposite from the base type, including empty composites. All package
boxes inherit that state. Existing custom overrides remain supported by the virtual properties.

The inherited debugger display has the form "moov @0x20 size 1,204 [3 children]" or
"mdat @0x0 size 40 GiB (lazy)". A composite proxy exposes its existing Children list. The display
uses header and shape only, without field descriptions, write plans or payload I/O.

## Edit and save

```csharp
using var document = BmffDocument.Load(input);
var fileType = document.First<FileTypeBox>();
fileType.CompatibleBrands = new[] { FourCC.Parse("isom").Value };

var movieIndex = -1;
for (var index = 0; index < document.Boxes.Count; index++)
{
    if (document.Boxes[index] is MovieBox)
    {
        movieIndex = index;
        break;
    }
}
if (movieIndex > 1)
{
    var movie = document.Boxes[movieIndex];
    document.Boxes.RemoveAt(movieIndex);
    document.Boxes.Insert(1, movie);
}

// Optional explicit planning; SaveAsync also relocates supported references.
document.RelocateMediaOffsets();
await document.SaveAsync(output, cancellationToken);
```

The example assumes an ftyp-first progressive file with its moov after mdat. Save recomputes sizes
and relocates in-file chunk/item/fragment, sidx and saio references. It promotes stco to co64 when
needed and plans
again until the layout stabilizes. Repeated saves do not apply the shift twice. The exact supported
references and fragment-base restrictions are in [DESIGN](DESIGN.md#relocation-and-mutation).

Load requires seekable input and borrows it by default; leaveOpen: false transfers disposal to the
document. Keep that source open and byte-stable through save. Use a separate output. Media, codec
configuration and unknown payloads copy through without loading them. Calls on shared sources,
readers, writers and models are sequential. A new document's first save establishes its layout;
its caller supplies initial offsets.

## Work with fields

Every standard box remains its own sealed model. Containers expose ordered Children. For example,
SampleDescriptionBox and DataReferenceBox expose EntryCount through that collection. stsd entries
resolve through RegisterSampleEntry factories in the active registry. Ordinary box registrations
do not apply in that context; unregistered entries remain lazy and opaque, including in strict mode.
The MP4 profile supplies the codec entry grammars.

MovieHeaderBox, TrackHeaderBox and MediaHeaderBox own their timing fields. CreationTime and
ModificationTime are raw unsigned seconds since 1904-01-01 UTC. Version zero selects 32-bit time
fields and version one selects 64-bit fields; callers choose the version explicitly. Fixed-point
rate/volume/dimensions preserve their raw bits through Content.Binary types.

Large sample tables use primitive arrays or arrays of nested Entry structs. For ctts/trun,
CompositionTimeOffset/SampleOffset uses long to represent unsigned v0 and signed v1 values.
For tfhd and trun, set both Flags and the corresponding nullable optional fields. Fields outside
the selected version/width or inconsistent with flags fail with ContentFormatException.

FileTypeBox.CompatibleBrands replaces the old CompatableBrands spelling without an alias.
TrackBox's misplaced header/rate properties were removed; use the owning mvhd/tkhd/mdhd box.
MovieHeaderBox now uses the inherited byte Version and mutable raw/fixed-point properties.

## Stream output or extend a profile

```csharp
using var writer = BmffWriter.Create(output);
writer.BeginContainer(BmffBoxType.MovieFragment, payloadSize: knownChildBytes);
writer.Write(fragmentChild);
writer.EndContainer();
```

Supply a known payload size to stream directly, or omit it for bounded buffering. WritePayload
copies raw segments. Both modes support non-seekable outputs.

```csharp
var registry = BmffBoxRegistry.CreateBuilder()
    .Register(profileType, header => new ProfileBox(header))
    .Build();
using var reader = BmffReader.Create(input, new BmffReaderOptions(), registry);
```

A profile supplies Read, Write and GetPayloadSize on a box model. Factories receive validated
headers; registry snapshots are immutable. Composite extension bytes precede ordered children,
and their size is included in relocation. New standard models use the existing Visit(BmffBox)
fallback so visitor interfaces remain unchanged.

## The three profile seams

| Need | Profile uses | Contract |
| --- | --- | --- |
| Opaque configuration or item data | Derive a sealed box from BmffBoxLazyLeaf and register its factory | Payload is a borrowed BinarySegment. ReadPayloadBytes is the protected, capped materialization path. Typed accessors belong to the subclass. |
| Typed stsd entries | RegisterSampleEntry(type, factory) | The same bounded reader materializes entries and descendants with the enclosing depth budget. Unknown entries remain preserved. |
| Private offset-bearing boxes | RegisterRelocation(type, capture) | Capture returns apply/restore callbacks; apply uses IBmffRelocationContext to map checkpoint offsets and box positions. Failed planning restores fields. |

Load the document with the completed registry so its relocation rules also apply on Save.
A rule captures its original fields once and uses those same values on every planning pass.
It must restore every field it changes. This includes version/width promotion if the profile
format needs it. A registered rule replaces the built-in rule for its FourCC.

sidx relocates the end-relative first offset and referenced spans, including references to other
indexes. saio relocates absolute sample-table addresses and supported tfhd-relative fragment
addresses, including auxiliary bytes in opaque senc boxes. Version-zero fields promote when
necessary. These two field implementations are internal; inspect their generic box identity
or emitted bytes. Existing public visitor interfaces are unchanged.

Profiles compacting a payload call RelocateMediaOffsets(mapPayloadOffset, outputOffset) after
replacing it. The pure mapper translates payload-relative byte addresses and exclusive ends;
a negative result rejects a deleted target. HEIF uses this to erase removed metadata while
relocating retained iloc extents and other supported references. See the complete
[relocation contract](DESIGN.md#relocation-and-mutation) for rollback and unsupported bases.

## Limits and scope

Defaults are a 16 MiB buffered/decoded leaf budget, 1,000,000 table entries and depth 64.
Counts and payload bounds are validated before allocations. Unknown/unimplemented types and
unsupported versions are preserved in lenient mode; strict mode rejects them, except deliberately
opaque stsd sample entries. Malformed supported fields report ContentFormatException with a code
and absolute position.

The strategic set covers movie, track/media, sample-table, fragment, item metadata and protection
signalling fields. The [design table](DESIGN.md#field-encodings-and-validation) records versions and
flags, the [preserved set](DESIGN.md#strategic-field-models) names deferred models, and the
[byte-preservation rule](DESIGN.md#parsed-byte-preservation) defines round-trip behavior.
Codec interpretation, decryption and sample indexing remain outside this layer. Profile-specific
references require explicit registered relocation rules.
