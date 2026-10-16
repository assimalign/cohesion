# Stage 5 verification

Verified in `C:\cw\ct\s5-heif`, branch `codex/content-s5-heif`, based on `808a62f92`, on 2026-10-16.
All thirteen Content source projects build without warnings or errors. No tests were weakened or removed.
All implemented test suites pass. The existing Mkv placeholder has one skipped test; Mpeg has no test project at this base.

## Commands and results

| Command | Result |
| --- | --- |
| `dotnet build libraries/Content/Assimalign.Cohesion.Content.Binary/src/Assimalign.Cohesion.Content.Binary.csproj` | Exit 0; 0 Warning(s); 0 Error(s) |
| `dotnet test libraries/Content/Assimalign.Cohesion.Content.Binary/tests/Assimalign.Cohesion.Content.Binary.Tests.csproj` | Exit 0; 201 passed, 0 failed, 0 skipped |
| `dotnet build libraries/Content/Assimalign.Cohesion.Content.Bmff/src/Assimalign.Cohesion.Content.Bmff.csproj` | Exit 0; 0 Warning(s); 0 Error(s) |
| `dotnet test libraries/Content/Assimalign.Cohesion.Content.Bmff/tests/Assimalign.Cohesion.Content.Bmff.Tests.csproj` | Exit 0; 397 passed, 0 failed, 0 skipped |
| `dotnet build libraries/Content/Assimalign.Cohesion.Content.Ebml/src/Assimalign.Cohesion.Content.Ebml.csproj` | Exit 0; 0 Warning(s); 0 Error(s) |
| `dotnet test libraries/Content/Assimalign.Cohesion.Content.Ebml/tests/Assimalign.Cohesion.Content.Ebml.Tests.csproj` | Exit 0; 235 passed, 0 failed, 0 skipped |
| `dotnet build libraries/Content/Assimalign.Cohesion.Content.Exe/src/Assimalign.Cohesion.Content.Exe.csproj` | Exit 0; 0 Warning(s); 0 Error(s) |
| `dotnet test libraries/Content/Assimalign.Cohesion.Content.Exe/tests/Assimalign.Cohesion.Content.Exe.Tests.csproj` | Exit 0; 107 passed, 0 failed, 0 skipped |
| `dotnet build libraries/Content/Assimalign.Cohesion.Content.Heif/src/Assimalign.Cohesion.Content.Heif.csproj` | Exit 0; 0 Warning(s); 0 Error(s) |
| `dotnet test libraries/Content/Assimalign.Cohesion.Content.Heif/tests/Assimalign.Cohesion.Content.Heif.Tests.csproj` | Exit 0; 71 passed, 0 failed, 0 skipped |
| `dotnet build libraries/Content/Assimalign.Cohesion.Content.Markdown/src/Assimalign.Cohesion.Content.Markdown.csproj` | Exit 0; 0 Warning(s); 0 Error(s) |
| `dotnet test libraries/Content/Assimalign.Cohesion.Content.Markdown/tests/Assimalign.Cohesion.Content.Markdown.Tests.csproj` | Exit 0; 1500 passed, 0 failed, 0 skipped |
| `dotnet build libraries/Content/Assimalign.Cohesion.Content.Media/src/Assimalign.Cohesion.Content.Media.csproj` | Exit 0; 0 Warning(s); 0 Error(s) |
| `dotnet test libraries/Content/Assimalign.Cohesion.Content.Media/tests/Assimalign.Cohesion.Content.Media.Tests.csproj` | Exit 0; 93 passed, 0 failed, 0 skipped |
| `dotnet build libraries/Content/Assimalign.Cohesion.Content.Mkv/src/Assimalign.Cohesion.Content.Mkv.csproj` | Exit 0; 0 Warning(s); 0 Error(s) |
| `dotnet test libraries/Content/Assimalign.Cohesion.Content.Mkv/tests/Assimalign.Cohesion.Content.Mkv.Tests.csproj` | Exit 0; 0 passed, 0 failed, 1 skipped |
| `dotnet build libraries/Content/Assimalign.Cohesion.Content.Mpeg/src/Assimalign.Cohesion.Content.Mpeg.csproj` | Exit 0; 0 Warning(s); 0 Error(s) |
| `dotnet build libraries/Content/Assimalign.Cohesion.Content.Pdf/src/Assimalign.Cohesion.Content.Pdf.csproj` | Exit 0; 0 Warning(s); 0 Error(s) |
| `dotnet test libraries/Content/Assimalign.Cohesion.Content.Pdf/tests/Assimalign.Cohesion.Content.Pdf.Tests.csproj` | Exit 0; 256 passed, 0 failed, 0 skipped |
| `dotnet build libraries/Content/Assimalign.Cohesion.Content.Text/src/Assimalign.Cohesion.Content.Text.csproj` | Exit 0; 0 Warning(s); 0 Error(s) |
| `dotnet test libraries/Content/Assimalign.Cohesion.Content.Text/tests/Assimalign.Cohesion.Content.Text.Tests.csproj` | Exit 0; 493 passed, 0 failed, 0 skipped |
| `dotnet build libraries/Content/Assimalign.Cohesion.Content.Yaml/src/Assimalign.Cohesion.Content.Yaml.csproj` | Exit 0; 0 Warning(s); 0 Error(s) |
| `dotnet test libraries/Content/Assimalign.Cohesion.Content.Yaml/tests/Assimalign.Cohesion.Content.Yaml.Tests.csproj` | Exit 0; 416 passed, 0 failed, 0 skipped |
| `dotnet build libraries/Content/Assimalign.Cohesion.Content/src/Assimalign.Cohesion.Content.csproj` | Exit 0; 0 Warning(s); 0 Error(s) |
| `dotnet test libraries/Content/Assimalign.Cohesion.Content/tests/Assimalign.Cohesion.Content.Tests.csproj` | Exit 0; 22 passed, 0 failed, 0 skipped |
| `dotnet build libraries/Content/Assimalign.Cohesion.Content.Heif/src/Assimalign.Cohesion.Content.Heif.csproj -p:GenerateDocumentationFile=true -p:BuildProjectReferences=false` | Passed, zero warnings/errors; XML public API documentation generated. |
| `pwsh build/scripts/Update-CohesionDependencyGraph.ps1` | Passed, 693 projects / 39 areas; generated graph committed with the new reference and regenerated during onboarding. |
| `pwsh -NoProfile -Command "Import-Module ./installer/scripts/modules/CohesionPackaging.psm1 -Force; Assert-CohesionReleaseInventory"` | Passed, 229 libraries/resources, 21 SDK packs, 19 framework families x 7 RIDs. |
| `pwsh build/scripts/Update-CohesionDependencyGraph.ps1 -Check` | Passed; dependency graph current. |
| `pwsh -NoProfile -Command "Invoke-Pester ./installer/scripts/modules/tests -CI"` | 55 passed, 0 failed, 0 skipped, 0 inconclusive, 0 not run. |
| `git diff --check` | Passed; no whitespace errors. |

Repeated HEIF build/test invocations during implementation used the same explicit source/test csproj paths above. Initial compiler/style diagnostics (IDE0011, CS8122, CS8602 and one temporary fixture-edit syntax error) were fixed. A malformed-extent test initially failed because the writer correctly refused to generate its invalid offset; the fixture now corrupts a valid writer output. Subsequent test checkpoints passed at 20, 27, 65, 70 and finally 71 tests. No final failure needs baseline reproduction.

The isolated fixture generator command `dotnet run --project .heif-corpusgen/Heif.CorpusGen.csproj` first reported NETSDK1013 because its scratch location did not inherit the library target framework. Setting net10.0/Preview corrected it and all four checked-in fixtures were generated. The temporary project, outputs and logs were then moved under the session TEMP directory.

`dotnet format style libraries/Content/Assimalign.Cohesion.Content.Heif/src/Assimalign.Cohesion.Content.Heif.csproj --diagnostics IDE0011 --no-restore` corrected brace diagnostics. Earlier attempts with scoped `--include` arguments completed without selecting files. The formatter changed only the new HEIF package.

All three Mermaid diagrams were rendered with the Mermaid CLI and visually inspected: the area family map, Content README project map (11 nodes), and HEIF dependency map. System Edge failed to start for the first two preview attempts; the official Puppeteer headless shell completed all renders. Renderer dependencies, browser profile and PNG previews stayed in TEMP. The temporary local preview server was stopped.

## Pre-completion checklist

- [x] Read repository rules, shared D1-D6 design, BMFF extension design and issues #454-#457 / #1553; confirmed base and ownership boundaries.
- [x] New project uses the exact assembly/root namespace, net10.0 and Preview inherited build settings, and one direct reference to Content.Bmff.
- [x] Public box types are sealed and parameterless; internal helpers stay in Internal. No public interfaces or shipped-assembly InternalsVisibleTo were added. Box interface-first deviations are annotated at each entry point as directed by the brief.
- [x] Public APIs have XML documentation; the separate public-surface inventory enumerates each type/member, including BMFF overrides.
- [x] Custom exceptions use generated ErrorMessages resources. Parsing checks counts, arithmetic, bounds and depth; malformed wire input is reported through ContentFormatException with family codes and absolute positions.
- [x] Payloads stay borrowed BinarySegments; descriptors are bounded. No reflection, dynamic code generation, parsing-loop LINQ, downloaded fixtures or codecs were added.
- [x] Async save takes CancellationToken and returns ValueTask. Test conventions, AAA arrangement, Shouldly, test-object placement and standard clause citations were checked.
- [x] All 44 new C#/csproj/resx files pass the CRLF scan. Shared-file diffs were inspected for narrow scope and no line-ending rewrites.
- [x] All three solution XML files parse and their eight HEIF entries resolve. Overview/design/provenance and verification docs use uppercase names; projects and docs appear in all three solutions. Diagrams render and arrows match their documented current or target dependency graph.
- [x] CI/release entries and generated dependency graph are complete. Every Content source/test project and all required packaging guards ran successfully, with the existing placeholder skip reported above.
- [x] No edits to BMFF, Mpeg, Mkv, resources, program plans or another worktree; no GitHub writes, push, shared cache rewrite, build-server shutdown or unrelated process termination.
- N/A: extension members, value-object generation, new exception classes, event sources, new framework/SDK plumbing, file renames and public type documentation pages that did not already exist. Installer/local pack is expressly prohibited by the session brief.

## Deviations and escalation record

The generated dependency graph was committed with the package reference in item 2 to obey the reference-change convention, then regenerated for item 5. Tests identify normative clause titles where ISO editions renumber sections. Concrete sealed box models follow the explicit brief instead of the repository default preference for interfaces. Nested association/reference/layer value types group the sanctioned item-model data.

BMFF extension gaps and the smallest local workarounds are recorded in DESIGN.md: lazy wire leaves currently use the composite dispatch path; visitor dispatch uses the existing generic fallback; header-only iref traversal must consume its four-byte full-box prefix; external-box asynchronous save may perform synchronous I/O; edited method-0 extents outside mdat are rejected because BMFF cannot relocate them. No BMFF public interface was widened.

Metadata removal is logical item removal. Old, unreferenced bytes in shared mdat/idat remain. Owner review should confirm that scope for media-service use; physical compaction/secure erasure needs a separate feature. Sequences, construction method 2, external data references, codec/profile certification and TIFF payload parsing (#1553) remain explicit non-goals. The first three are reported Unsupported where encountered, while codec/TIFF bytes remain opaque.

Supporting public API names HeifReaderOptions, HeifItem.Property, HeifItem.Reference, HeifOverlayImage.Layer and ItemPropertyAssociationBox.Entry/Association implement the brief's limits and ordered item/box fields. They add no unrelated feature surface; the owner can review their naming in PUBLIC_SURFACE.md.
