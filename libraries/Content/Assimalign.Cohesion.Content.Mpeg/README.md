# Assimalign.Cohesion.Content.Mpeg

MP4 profile support over the shared BMFF container and media contracts: editable sample entries,
opaque decoder configuration, track metadata, lazy progressive/fragmented sample indexes, seek
ranges, edit-list timing, fast start and retained CMAF structure checks.

- [Overview and usage](docs/OVERVIEW.md)
- [Design, supported scope and extension gaps](docs/DESIGN.md)
- [Public surface](docs/PUBLIC_SURFACE.md)

Build and test from the repository root:

```powershell
dotnet build libraries/Content/Assimalign.Cohesion.Content.Mpeg/src/Assimalign.Cohesion.Content.Mpeg.csproj
dotnet test libraries/Content/Assimalign.Cohesion.Content.Mpeg/tests/Assimalign.Cohesion.Content.Mpeg.Tests.csproj
```

A fresh worktree requires the shared build task:
`dotnet build build/Tasks/Assimalign.Cohesion.Build.Tasks.csproj`.
Tests need no downloaded media or installed muxer. Checked-in fixtures are produced by
`tests/TestObjects/Mp4GoldenFixtures.cs` using the BMFF writer and placeholder codec bytes.
