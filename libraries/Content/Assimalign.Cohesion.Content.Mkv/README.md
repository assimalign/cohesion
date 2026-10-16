# Assimalign.Cohesion.Content.Mkv

Matroska and WebM container semantics over Content.Ebml, with Content.Media tracks, lazy sample
queries, block/lace framing, and metadata editing that preserves unchanged cluster bytes.
Codec identifiers and private bytes remain opaque; this package does not decode media.

The current EBML core rejects RFC 9559's ChapterDisplay ID `0x80`. Until that prerequisite is
integrated, Chapters is preserved as an opaque lazy payload; typed chapter editing is unavailable.

- [Overview and usage](docs/OVERVIEW.md)
- [Design, retained scope, limits and escalations](docs/DESIGN.md)
- [EBML foundation](../Assimalign.Cohesion.Content.Ebml/docs/DESIGN.md)
- [Shared media contracts](../Assimalign.Cohesion.Content.Media/docs/DESIGN.md)

## Build and test

From the repository root:

```powershell
dotnet build libraries/Content/Assimalign.Cohesion.Content.Mkv/src/Assimalign.Cohesion.Content.Mkv.csproj
dotnet test libraries/Content/Assimalign.Cohesion.Content.Mkv/tests/Assimalign.Cohesion.Content.Mkv.Tests.csproj
```

A fresh worktree needs the repository's build tasks first:
`dotnet build build/Tasks/Assimalign.Cohesion.Build.Tasks.csproj`.
Tests use local writer-generated fixtures and require no media downloads, player, network service,
developer-specific media directory, or external codec installation.
