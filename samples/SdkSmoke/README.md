# SDK consumer smoke

These package-only consumers exercise the base, Web, and Database SDKs. The two base-SDK
executables opt into the App hosting kernel explicitly; the analyzer consumer also references the
ordinary ObjectMapping package, which delivers its own generator. Each project is non-packable
and leaves `TargetFramework` to its SDK. The Analyzer project emits compiler-generated files in
Release and suppresses `CA2252` for its preview API usage.

From the repository root, prepare the host-RID feed and run the smoke:

```powershell
$version = (& ./installer/scripts/Get-CohesionVersion.ps1).Trim()
$commit = (git rev-parse HEAD).Trim()
$rid = (dotnet --info | Select-String '^\s*RID:\s*(\S+)').Matches[0].Groups[1].Value
./installer/scripts/Pack-Release.ps1 -Version $version -RepositoryCommit $commit -RuntimeIdentifier $rid -SkipLibraries
dotnet pack ./libraries/Core/Assimalign.Cohesion.Core/src/Assimalign.Cohesion.Core.csproj -c Release -p:PackageOutputPath=./_out/release/packages
dotnet pack ./libraries/ObjectMapping/Assimalign.Cohesion.ObjectMapping/src/Assimalign.Cohesion.ObjectMapping.csproj -c Release -p:PackageOutputPath=./_out/release/packages
./.github/scripts/Invoke-SdkConsumerSmoke.ps1 -PackageDirectory ./_out/release/packages -Version $version -RuntimeIdentifier $rid
```

The harness copies this sample into an isolated workspace, excluding `bin` and `obj`, rewrites
the `cohesion-smoke` NuGet source to the supplied feed, and generates `global.json` with the
repository's .NET SDK settings and the three Cohesion SDKs pinned to the supplied version.
`-SampleDirectory` accepts an alternative source directory; `-WorkingDirectory` selects the
workspace parent. The checked-in NuGet source resolves to `_out/release/packages` in this repo.

All four consumers build in Release, publish self-contained for the host RID, and run with exact
stdout assertions. The Analyzer consumer must produce exactly one `*.MapperProfile.g.cs`
containing `TryConfigureGenerated`. `sdk-smoke.yml` runs this harness on Windows, Linux, and macOS;
`release.yml` validates consumers against the release artifact using the same harness.

These projects stay outside repository solutions because their SDKs resolve from the packed feed.
They preserve the harness's original inline consumers and do not depend on `dotnet new` templates.
