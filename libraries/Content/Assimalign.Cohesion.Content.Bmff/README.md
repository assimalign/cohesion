# Assimalign.Cohesion.Content.Bmff

ISO BMFF container machinery for reading, streaming and editing large files:

- Header-first reading on seekable and non-seekable streams.
- The retained per-box composite model and visitor interfaces.
- Immutable delegate registries for MP4/HEIF extensions.
- Forward-only writing with precomputed sizes or bounded streaming containers.
- Lazy media and unknown-box payloads, with byte-exact unchanged document saves.

See [OVERVIEW](docs/OVERVIEW.md) for usage and [DESIGN](docs/DESIGN.md) for ownership, limits,
wire rules, registry extension and the field models deferred to #447.
Content.Media and Content.Binary supply the shared layers beneath BMFF.

From the repository root:

```powershell
dotnet build libraries/Content/Assimalign.Cohesion.Content.Bmff/src/Assimalign.Cohesion.Content.Bmff.csproj
dotnet test libraries/Content/Assimalign.Cohesion.Content.Bmff/tests/Assimalign.Cohesion.Content.Bmff.Tests.csproj
```

The tests build fixtures in code; no media downloads or developer-local files are required.
In a fresh worktree, build build/Tasks/Assimalign.Cohesion.Build.Tasks.csproj first if MSB4062 occurs.
