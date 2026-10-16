# Content.Heif overview

Content.Heif supplies HEIF item boxes and AVIF properties on the shared BMFF reader, writer and
composite tree. It references Content.Bmff only. The registry is explicit and NativeAOT-safe.

Use HeifBoxRegistry.Default with BmffReader.Create or BmffDocument.Load. For custom limits, call
HeifBoxRegistry.Register(BmffBoxRegistry.CreateBuilder(), options).Build(). Write the resulting
boxes through BmffWriter. Keep borrowed sources open until all segment reads and saves finish.

See [design](DESIGN.md) for limits, scope and BMFF extension contracts.

HeifDocument.Load(source, new HeifReaderOptions { Strict = true }) creates the still-image view.
PrimaryImage.Grid.Tiles exposes grid inputs; GetThumbnails and GetAlphaImages follow references.
ExifItems and XmpItems locate metadata. RemoveExif/RemoveXmp remove metadata items; ReplaceExif
accepts the complete Exif item including its four-byte offset. SetRotation uses counter-clockwise
quarter turns; SetMirror accepts a vertical/horizontal axis or null to remove mirroring.
AddThumbnail accepts already encoded bytes and their properties. Save streams through BMFF and
relocates iloc while keeping the original image extents lazy.

RemoveItem(id), RemoveExif, RemoveXmp and RemoveThumbnail physically remove their unshared
payload bytes from mdat/idat when saved. Replacement retires the old payload too. Any bytes
shared with a retained item are kept, including partial overlaps. Every remaining image retains
its bytes, and its iloc extents are relocated. There is no structural-only removal mode.

    using var source = File.OpenRead("photo.heic");
    using var document = HeifDocument.Load(source);
    document.RemoveExif();
    document.RemoveXmp();
    using var output = File.Create("photo-without-metadata.heic");
    await document.SaveAsync(output, cancellationToken);

This changes the saved file; the borrowed original is unchanged. Removal fails when external,
method-2 or unresolved extents make sharing unknowable. Primary items and retained derivation
inputs cannot be deleted. See [the removal contract](DESIGN.md#item-view-validation-and-editing).

HEIF's opaque configuration, ICC and item-data leaves use BmffBoxLazyLeaf. Profiles adding timed
sample entries use RegisterSampleEntry; profiles adding private references use RegisterRelocation
and IBmffRelocationContext. HEIF compaction supplies the BMFF payload-offset map so iloc and
other supported references follow retained bytes. The [BMFF design](../../Assimalign.Cohesion.Content.Bmff/docs/DESIGN.md)
documents all three extension seams and the complete relocation rule set.

## Traverse and inspect the native tree

```csharp
using Assimalign.Cohesion.Content.Bmff;
using Assimalign.Cohesion.Content.Heif;

using var document = BmffDocument.Load(source, registry: HeifBoxRegistry.Default);
var properties = document.First<ItemPropertyContainerBox>();
var dimensions = properties.Children<ImageSpatialExtentsBox>();
var items = document.Descendants<ItemInfoEntryBox>();
BmffTreeFormatter.Write(document, Console.Out);
```

An excerpt from the AVIF fixture's property-container subtree:

```text
ipco @0xD7 size=145 children=6
  ispe @0xDF size=20 version=0 flags=0x000000 dimensions=64x64
  av1C @0xF3 size=12 lazy-payload=4
  colr @0xFF size=19 lazy-payload=11 colour-type=nclx icc-bytes=0
```

Descriptions include item identities, dimensions, property/association counts, reference source
IDs, rotation/mirroring and colour representation. ICC and item-data lengths remain lazy. Reference
counts come from the retained memory prefix without decoding arrays or changing the model.

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
