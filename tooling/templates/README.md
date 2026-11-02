# Cohesion templates

This L1 tooling area ships the `Assimalign.Cohesion.Templates` package for `dotnet new`.
Its scaffolds consume the SDKs and runtime families without depending on repository build files.
Every generated project includes `Properties/launchSettings.json` with a project-named
launch profile; resource profiles set `COHESION_ENVIRONMENT=Local` for the developer machine.
Development uses the same strict posture as other deployed environments; an unset framework
environment still defaults to Production.

| Project | Purpose |
| --- | --- |
| [Assimalign.Cohesion.Templates](Assimalign.Cohesion.Templates/docs/OVERVIEW.md) | Application, gateway and standalone resource templates. |

See the [design](Assimalign.Cohesion.Templates/docs/DESIGN.md) for the package lifecycle,
template roster, version substitution and consumer acceptance tests.

The template run tests require a packed SDK/framework feed. To exercise the standalone SPA
with the same canonical package identity as CI, run from the repository root (set `$rid` to
the RID reported by `dotnet --info`):

```powershell
dotnet build build/Tasks -c Debug
$rid = 'win-arm64'
./scripts/Install-Local.ps1 -Configuration Release -Rids $rid -SkipLibraries -UseCanonicalVersion
$env:COHESION_TEMPLATES_TEST_FEED = '_out/packages'
$env:COHESION_TEMPLATES_TEST_PACKAGE_VERSION = ./scripts/Get-CohesionVersion.ps1
dotnet test tooling/templates/Assimalign.Cohesion.Templates/tests/Assimalign.Cohesion.Templates.Tests.csproj -c Release --filter FullyQualifiedName~Build_Spa
```

The test installs the packed template into a private hive, builds the generated project,
then checks client-route and missing-asset responses from its apphost. It stops the server even
when an assertion fails. The full roster additionally needs the library packages listed in
`.github/workflows/tooling-templates.yml`. Missing feed prerequisites are reported as skips;
a skipped run does not verify the SPA behavior.
