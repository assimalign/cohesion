# Cohesion templates

This L1 tooling area ships the `Assimalign.Cohesion.Templates` package for `dotnet new`.
Its scaffolds consume the SDKs and runtime families without depending on repository build files.
Every generated project includes `Properties/launchSettings.json` with a project-named
launch profile setting `COHESION_ENVIRONMENT=Local` for the developer machine. Development
uses the same strict posture as other deployed environments; an unset framework environment
still defaults to Production. The landing-zone APIs use `appsettings.Local.json` for local settings.

| Project | Purpose |
| --- | --- |
| [Assimalign.Cohesion.Templates](Assimalign.Cohesion.Templates/docs/OVERVIEW.md) | Application, landing-zone, gateway, composite and standalone resource templates. |

See the [design](Assimalign.Cohesion.Templates/docs/DESIGN.md) for the package lifecycle,
template roster, version substitution and consumer acceptance tests.
