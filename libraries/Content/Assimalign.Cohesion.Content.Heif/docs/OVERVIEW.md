# Content.Heif overview

Content.Heif supplies HEIF item boxes and AVIF properties on the shared BMFF reader, writer and
composite tree. It references Content.Bmff only. The registry is explicit and NativeAOT-safe.

Use HeifBoxRegistry.Default with BmffReader.Create or BmffDocument.Load. For custom limits, call
HeifBoxRegistry.Register(BmffBoxRegistry.CreateBuilder(), options).Build(). Write the resulting
boxes through BmffWriter. Keep borrowed sources open until all segment reads and saves finish.

See [design](DESIGN.md) for limits, scope and BMFF extension constraints.
