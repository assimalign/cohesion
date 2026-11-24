# Assimalign.Cohesion.Templates

The package supplies the Cohesion .NET CLI templates. Every project has an authored `Program.cs`,
and every template pins the complete 19-SDK inventory plus the repository's .NET SDK version.

```bash
dotnet new install Assimalign.Cohesion.Templates
dotnet new cohesion-app -n Acme
```

The standalone resource names are `cohesion-web`, `cohesion-spa`, `cohesion-database`,
`cohesion-secretstore`, `cohesion-configurationstore`, `cohesion-identityhub` and `cohesion-rezolvr`.
Use `-n` to name a project.

`cohesion-app` generates two executable projects, `Acme.Api` and `Acme.Database`. The API reads
its database endpoint from `Database:Endpoint`. See the [design](DESIGN.md) for the template layout.

`cohesion-spa` serves `wwwroot` and falls back to `index.html` for GET/HEAD client-side routes
such as `/settings`. Missing assets whose last path segment names a file remain 404 responses.

Generated root files include `Directory.Build.props`, `global.json`, `nuget.config`, `.gitignore`
and a credential guard workflow. Replace the organization feed placeholder before using your own
packages. Authentication belongs outside tracked files. Each project also includes
`Properties/launchSettings.json` with a project-named profile; resource profiles set
`COHESION_ENVIRONMENT=Local`. For an explicit shell environment, use `dotnet run --no-launch-profile`
so launch settings do not override it. Development is a strict deployed environment; the
framework's unset default remains Production.

The template package contains content only, with no runtime dependency or public assembly API.
Generated applications consume Cohesion SDK and framework packages.

Build and verify from the repository root:

```powershell
dotnet build build/Tasks
dotnet build tooling/templates/Assimalign.Cohesion.Templates/src/Assimalign.Cohesion.Templates.csproj
dotnet test tooling/templates/Assimalign.Cohesion.Templates/tests/Assimalign.Cohesion.Templates.Tests.csproj
dotnet pack tooling/templates/Assimalign.Cohesion.Templates/src/Assimalign.Cohesion.Templates.csproj -o _out/verify-39
```

Feed-free tests always install and instantiate the full roster. Set `COHESION_TEMPLATES_TEST_FEED`
and `COHESION_TEMPLATES_TEST_PACKAGE_VERSION` to exercise builds against a prepared package feed.
Defaults are `_out/packages` and the canonical version with `.local` appended. Test discovery
reports exact missing packages or an SDK pack without executable defaults as skips. With the feed
present, one test also starts the generated `cohesion-database`: it builds it self-contained for
the host RID, so that run restores the matching `Microsoft.NETCore.App` runtime pack from nuget.org,
then writes and reads one full customer row over the wire.

The standalone Database template and the app's Database import `Assimalign.Cohesion.Database` for
the shared `Listen(Uri)` extension. `App.Database` already carries `Database.Tcp`; generated
projects need no extra package reference.
