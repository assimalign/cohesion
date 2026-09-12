# Assimalign.Cohesion.Templates

The package supplies the .NET CLI templates defined by developer-experience design §8.
Every project has an authored `Program.cs`; every template writes explicit application identity
and pins the complete 20-SDK inventory plus the repository's .NET SDK version.

```bash
dotnet new install Assimalign.Cohesion.Templates
dotnet new cohesion-app -n Acme
dotnet new cohesion-landing-zone -n Example --topology single
dotnet new cohesion-landing-zone -n Example --topology federated
dotnet new cohesion-gateway -n Acme.Gateway --applicationName acme
dotnet new cohesion-composite -n Acme.Workers
```

The standalone resource names are `cohesion-web`, `cohesion-spa`, `cohesion-database`,
`cohesion-secretstore`, `cohesion-configurationstore`, `cohesion-identityhub` and `cohesion-rezolvr`.
Use `-n` to name a project. For gateway, composite and standalone templates the application
defaults to the lowercased first name segment and can be supplied with `--applicationName`.

Applications and landing zones enable orchestration on each referenced resource. Standalone
resources explicitly disable it and explain the one-line opt-in in their project files.
Gateways inherit their always-enabled behavior from `Sdk.Gateway`.

Generated root files include `Directory.Build.props`, `global.json`, `nuget.config`, `.gitignore`
and a credential guard workflow. Replace the organization feed placeholder and registry before
using your own packages or publishing images. Authentication belongs outside tracked files.

The template package contains content only, with no runtime dependency or public assembly API.
Generated applications consume Cohesion SDK/framework packages. Local and InProcess gateway
providers are selected; Docker and Kubernetes provider integration remains a separate delivery.

Build and verify from the repository root:

```powershell
dotnet build build/Tasks
dotnet build tooling/templates/Assimalign.Cohesion.Templates/src/Assimalign.Cohesion.Templates.csproj
dotnet test tooling/templates/Assimalign.Cohesion.Templates/tests/
dotnet pack tooling/templates/Assimalign.Cohesion.Templates/src/Assimalign.Cohesion.Templates.csproj -o _out/verify-39
```

Feed-free tests always install and instantiate the full roster. Set `COHESION_TEMPLATES_TEST_FEED`
and `COHESION_TEMPLATES_TEST_PACKAGE_VERSION` to exercise builds against a prepared package feed.
Defaults are `_out/packages` and the canonical version with `.local` appended. Test discovery
reports exact missing packages or an SDK pack without executable defaults as skips.
