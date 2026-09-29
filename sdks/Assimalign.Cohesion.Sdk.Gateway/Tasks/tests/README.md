# Gateway SDK tests

These tests consume packed SDKs and libraries from `_out/packages` in isolated NuGet caches.
They default to the canonical version in `build/Targets/Build.Version.props`. Prepare the SDKs
with `installer/scripts/Install-Local.ps1 -UseCanonicalVersion -SkipLibraries -SkipFramework`
and the canonical gateway library dependency closure described in the `gateway-packages` job
of `.github/workflows/sdk-smoke.yml`. A libraries-only local install prunes the canonical
library archives; prepare the canonical closure after that install.

The gateway closure no longer includes any area client package: `Sdk.Gateway` injects only the
orchestration libraries, the selected provider packages, and the `<Area>.ApplicationModel`
package of each referenced resource project's area. `ConsumerWorkspace` checks for the required
archives before a test runs and names every missing one. It cannot tell a stale archive from a
current one: the command-target test also asserts that no area client reaches the gateway's
`ReferencePath`, so an `Assimalign.Cohesion.ApplicationModel.Gateway` archive packed before the
area clients were removed fails it. Repack the gateway library closure from the current tree.

Run the project explicitly:

```powershell
dotnet test sdks/Assimalign.Cohesion.Sdk.Gateway/Tasks/tests/Assimalign.Cohesion.Sdk.Gateway.Tests.csproj
```

The fixtures under `TestProjects/` compose resources the way a consumer gateway does: the
generated surface supplies `Manifests`, and each `Program.cs` calls the area's hand-written verb
over it (`builder.AddWeb(Manifests.GatewaySmokeWeb)`, `builder.AddDatabase(...)`), or
`builder.AddResource(Manifests.X)` for a resource whose area ApplicationModel the gateway does
not reference.

`CommandGateway` declares a `secretstore.add-secret` command, and the SecretStore manifest flags
that kind `requiresInputResolver`, so `Build()` fails unless the gateway registers a resolver for
it. A delivering gateway calls `builder.UseSecretStore(...)` from
`Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration`, but that package brings
`SecretStore.Client`, which the command-target test forbids in the gateway's `ReferencePath`. The
fixture registers its own pass-through `DescribeOnlyCommandInputResolver` in
`builder.Providers.CommandInputs` instead. It returns the declared payload unchanged and never
runs: the test starts the gateway only with `--mode=describe`, which builds the model and delivers
nothing. The test also asserts the flag itself, reading the object form for
`secretstore.add-secret` and the plain string for `secretstore.issue-certificate` from the
`CommandSecretStore` fixture's `resource.json`. Do not copy the pass-through into a gateway that
delivers commands; the store would reject the unresolved source.

The real image-gather tests require canonical `linux-arm64` runtime packs for
`Assimalign.Cohesion.App`, `App.Web`, and `App.Database`, plus their targeting and host runtime
packs. The fixtures disable transitive framework downloads so unrelated area runtime packs are
not required. Debug publishing produces an OCI archive without a daemon and verifies both the
member/index `linux/arm64` platform and the published apphost's ELF AArch64 machine header.
The Microsoft runtime/apphost packs still need to be available through NuGet or a NuGet fallback
package folder. Provider-selection tests execute generated code with isolated child-process
environment variables; they do not modify the test runner's environment.
