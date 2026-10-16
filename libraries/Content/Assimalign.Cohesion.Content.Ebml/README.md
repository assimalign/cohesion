# Assimalign.Cohesion.Content.Ebml

A generic [RFC 8794](https://www.rfc-editor.org/rfc/rfc8794) container layer for reading,
streaming and editing EBML. It depends on Content.Binary and has no media-format semantics.

- **EbmlReader** scans seekable or non-seekable input with bounded typed reads.
- **EbmlWriter** emits typed elements and precomputed, streaming or back-patched masters.
- **EbmlDocument** edits an ordered tree and copies lazy binary payloads through on save.
- **EbmlSchema** and **EbmlSchemaRegistry** let derived formats compose hand-written definitions.

See [the overview](docs/OVERVIEW.md) for usage and [the design](docs/DESIGN.md) for contracts,
limits, ownership, validation and extension boundaries.

Build and test from the repository root:

```powershell
dotnet build libraries/Content/Assimalign.Cohesion.Content.Ebml/src/Assimalign.Cohesion.Content.Ebml.csproj
dotnet test libraries/Content/Assimalign.Cohesion.Content.Ebml/tests/Assimalign.Cohesion.Content.Ebml.Tests.csproj
```

A fresh worktree needs the repository build tasks built first:
`dotnet build build/Tasks/Assimalign.Cohesion.Build.Tasks.csproj`.
Fixtures are generated in code; tests require no downloaded content.
