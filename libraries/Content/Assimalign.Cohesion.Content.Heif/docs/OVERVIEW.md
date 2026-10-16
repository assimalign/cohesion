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
