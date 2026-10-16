# Stage 4 MP4 verification

Run from `C:\cw\ct\s4-mp4` on `codex/content-s4-mp4`, based on `6c412fff7`.
No failing check remains. All twelve Content builds exit zero. Content tests total **3,818 passed,
0 failed, 1 pre-existing skipped**. The skipped MKV fixture is unchanged and belongs to s4-mkv.
No external media was downloaded. No server started for this work remains running.

## Full Content pass

Every command used the explicit project path, not a directory. Build commands have no test counts.

| Command | Result |
| --- | --- |
| `dotnet build libraries/Content/Assimalign.Cohesion.Content/src/Assimalign.Cohesion.Content.csproj` | PASS (exit 0) |
| `dotnet test libraries/Content/Assimalign.Cohesion.Content/tests/Assimalign.Cohesion.Content.Tests.csproj` | 22 passed / 0 failed / 0 skipped |
| `dotnet build libraries/Content/Assimalign.Cohesion.Content.Binary/src/Assimalign.Cohesion.Content.Binary.csproj` | PASS (exit 0) |
| `dotnet test libraries/Content/Assimalign.Cohesion.Content.Binary/tests/Assimalign.Cohesion.Content.Binary.Tests.csproj` | 201 passed / 0 failed / 0 skipped |
| `dotnet build libraries/Content/Assimalign.Cohesion.Content.Bmff/src/Assimalign.Cohesion.Content.Bmff.csproj` | PASS (exit 0) |
| `dotnet test libraries/Content/Assimalign.Cohesion.Content.Bmff/tests/Assimalign.Cohesion.Content.Bmff.Tests.csproj` | 397 passed / 0 failed / 0 skipped |
| `dotnet build libraries/Content/Assimalign.Cohesion.Content.Ebml/src/Assimalign.Cohesion.Content.Ebml.csproj` | PASS (exit 0) |
| `dotnet test libraries/Content/Assimalign.Cohesion.Content.Ebml/tests/Assimalign.Cohesion.Content.Ebml.Tests.csproj` | 235 passed / 0 failed / 0 skipped |
| `dotnet build libraries/Content/Assimalign.Cohesion.Content.Exe/src/Assimalign.Cohesion.Content.Exe.csproj` | PASS (exit 0) |
| `dotnet test libraries/Content/Assimalign.Cohesion.Content.Exe/tests/Assimalign.Cohesion.Content.Exe.Tests.csproj` | 107 passed / 0 failed / 0 skipped |
| `dotnet build libraries/Content/Assimalign.Cohesion.Content.Markdown/src/Assimalign.Cohesion.Content.Markdown.csproj` | PASS (exit 0) |
| `dotnet test libraries/Content/Assimalign.Cohesion.Content.Markdown/tests/Assimalign.Cohesion.Content.Markdown.Tests.csproj` | 1500 passed / 0 failed / 0 skipped |
| `dotnet build libraries/Content/Assimalign.Cohesion.Content.Media/src/Assimalign.Cohesion.Content.Media.csproj` | PASS (exit 0) |
| `dotnet test libraries/Content/Assimalign.Cohesion.Content.Media/tests/Assimalign.Cohesion.Content.Media.Tests.csproj` | 93 passed / 0 failed / 0 skipped |
| `dotnet build libraries/Content/Assimalign.Cohesion.Content.Mkv/src/Assimalign.Cohesion.Content.Mkv.csproj` | PASS (exit 0) |
| `dotnet test libraries/Content/Assimalign.Cohesion.Content.Mkv/tests/Assimalign.Cohesion.Content.Mkv.Tests.csproj` | 0 passed / 0 failed / 1 skipped |
| `dotnet build libraries/Content/Assimalign.Cohesion.Content.Mpeg/src/Assimalign.Cohesion.Content.Mpeg.csproj` | PASS (exit 0) |
| `dotnet test libraries/Content/Assimalign.Cohesion.Content.Mpeg/tests/Assimalign.Cohesion.Content.Mpeg.Tests.csproj` | 98 passed / 0 failed / 0 skipped |
| `dotnet build libraries/Content/Assimalign.Cohesion.Content.Pdf/src/Assimalign.Cohesion.Content.Pdf.csproj` | PASS (exit 0) |
| `dotnet test libraries/Content/Assimalign.Cohesion.Content.Pdf/tests/Assimalign.Cohesion.Content.Pdf.Tests.csproj` | 256 passed / 0 failed / 0 skipped |
| `dotnet build libraries/Content/Assimalign.Cohesion.Content.Text/src/Assimalign.Cohesion.Content.Text.csproj` | PASS (exit 0) |
| `dotnet test libraries/Content/Assimalign.Cohesion.Content.Text/tests/Assimalign.Cohesion.Content.Text.Tests.csproj` | 493 passed / 0 failed / 0 skipped |
| `dotnet build libraries/Content/Assimalign.Cohesion.Content.Yaml/src/Assimalign.Cohesion.Content.Yaml.csproj` | PASS (exit 0) |
| `dotnet test libraries/Content/Assimalign.Cohesion.Content.Yaml/tests/Assimalign.Cohesion.Content.Yaml.Tests.csproj` | 416 passed / 0 failed / 0 skipped |

## Required guards and supporting checks

| Command | Result |
| --- | --- |
| `pwsh -NoProfile -Command "Import-Module ./installer/scripts/modules/CohesionPackaging.psm1 -Force; Assert-CohesionReleaseInventory"` | PASS; 229 libraries/resources, 21 SDK packs, 19 framework families × 7 RIDs. Mpeg is included; it has no exclusion entry. |
| `pwsh build/scripts/Update-CohesionDependencyGraph.ps1` | PASS; regenerated 692 projects / 39 areas with the new references and test project in item 1. |
| `pwsh build/scripts/Update-CohesionDependencyGraph.ps1 -Check` | PASS; graph matches project files. |
| `pwsh -NoProfile -Command "Invoke-Pester ./installer/scripts/modules/tests -CI"` | 55 passed / 0 failed / 0 skipped. |
| `git diff --check`, `git diff --cached --check`, `git diff --check 6c412fff7..HEAD` | PASS after removing extra EOF blank lines found by the staged and final base comparisons. |
| `npx --yes --package @mermaid-js/mermaid-cli mmdc -i <TEMP>/cohesion-s4-mp4/profile.mmd -o <TEMP>/cohesion-s4-mp4/profile.svg` | PASS; diagram rendered. |
| Same Mermaid command with `profile.png` output | PASS; image visually inspected. |
| `dotnet run --project <TEMP>/cohesion-s4-mp4/goldengen/GoldenGen.csproj -- C:/cw/ct/s4-mp4/libraries/Content/Assimalign.Cohesion.Content.Mpeg/tests/TestObjects/Goldens` | PASS; scratch harness linked the three fixture sources and called Mp4GoldenFixtures.Create for the five filenames. Normal tests regenerate and compare every checked-in byte. |

`<TEMP>` denotes the session's Windows temporary directory. The Mermaid diagram source is the
unchanged dependency diagram extracted from DESIGN.md.

## Earlier implementation runs

These are the other build/test invocations, including failures resolved before completion.
The final all-Content pass above supersedes them; no test was weakened, removed or skipped.

| Command | Outcomes in execution order |
| --- | --- |
| `dotnet build libraries/Content/Assimalign.Cohesion.Content.Mpeg/src/Assimalign.Cohesion.Content.Mpeg.csproj` | Initial FAIL: 11 brace-style errors and one stackalloc warning, fixed. Then PASS (0 warnings/errors), then PASS (0 warnings/errors). |
| `dotnet test libraries/Content/Assimalign.Cohesion.Content.Mpeg/tests/Assimalign.Cohesion.Content.Mpeg.Tests.csproj` | Initial compile FAIL: missing required BMFF options argument, fixed. Then 37/0/0; 50/0/0 (two blocking-test warnings subsequently removed); 50/0/0; 75/0/0; 81/1/0; 82/0/0; 83/0/0; 95/0/0; 97/0/0; 98/0/0 (passed/failed/skipped). |

The allocation failure came from another parallel parser test consuming shared ArrayPool buckets.
The measuring collection is now isolated; its original 4 KiB construction allowance and zero-byte
query threshold are unchanged. The presentation-tail, missing-CMAF-default and unhandled-offset
regressions in item 3 include their corresponding local fixes.

## Pre-completion checklist

- ✅ Name-only references, explicit namespaces, inherited .NET 10 / Preview / AOT settings; no new package versions or Microsoft.Extensions dependencies.
- ✅ Public sealed box exception is explicit and scoped; other public implementations use the required construction pattern; native track/index implementations stay internal.
- ✅ File placement, file-scoped namespaces, private-field naming, explicit usings and no shipped InternalsVisibleTo.
- ✅ Public XML documentation and the complete public declaration inventory.
- ✅ Resource-backed shipped exception text, absolute malformed-input positions, checked sizes and bounded discovery.
- ✅ Span-based fixed prefixes, lazy payload segments, cancellable ValueTask queries; no parsing-loop LINQ, reflection or runtime generation.
- ✅ xUnit/Shouldly, named clause-linked tests, AAA structure, cancellation tokens and original fixture provenance.
- ✅ DESIGN/OVERVIEW/README, one rendered Mermaid diagram, relative documentation links and all three solution entries.
- ✅ CRLF C#/project/resource files; shared-file diffs limited to Mpeg entries; no generated outputs or scratch files staged.
- ✅ Dependency graph, all Content builds/tests, release inventory and all packaging Pester checks.
- N/A: framework/SDK creation, assembly-level attributes, source sharing, EventSource, extension members, document/file moves, local installer/pack feed changes.
- The installer was not run, as required by the brief; the existing MKV skip was left to its owning session.
