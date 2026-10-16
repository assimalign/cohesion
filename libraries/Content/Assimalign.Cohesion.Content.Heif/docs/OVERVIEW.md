# Content.Heif overview

Content.Heif supplies HEIF item boxes and AVIF properties on the shared BMFF reader, writer and
composite tree. It references Content.Bmff only. The registry is explicit and NativeAOT-safe.

Use HeifBoxRegistry.Default with BmffReader.Create or BmffDocument.Load. For custom limits, call
HeifBoxRegistry.Register(BmffBoxRegistry.CreateBuilder(), options).Build(). Write the resulting
boxes through BmffWriter. Keep borrowed sources open until all segment reads and saves finish.

See [design](DESIGN.md) for limits, scope and BMFF extension constraints.

HeifDocument.Load(source, new HeifReaderOptions { Strict = true }) creates the still-image view.
PrimaryImage.Grid.Tiles exposes grid inputs; GetThumbnails and GetAlphaImages follow references.
ExifItems and XmpItems locate metadata. RemoveExif/RemoveXmp remove metadata items; ReplaceExif
accepts the complete Exif item including its four-byte offset. SetRotation uses counter-clockwise
quarter turns; SetMirror accepts a vertical/horizontal axis or null to remove mirroring.
AddThumbnail accepts already encoded bytes and their properties. Save streams through BMFF and
relocates iloc while keeping the original image extents lazy.

Removal is logical: residual unreferenced bytes in shared data boxes are retained. See DESIGN.md
for this limit, method-2/external locations, and BMFF relocation constraints.
