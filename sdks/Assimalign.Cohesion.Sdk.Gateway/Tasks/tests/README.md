# Gateway SDK tests

These tests consume packed SDKs and libraries from `_out/packages` in isolated NuGet caches.
They default to the canonical version in `build/Targets/Build.Version.props`. Prepare the SDKs
with `installer/scripts/Install-Local.ps1 -UseCanonicalVersion -SkipLibraries -SkipFramework`
and the canonical gateway library dependency closure described in the `gateway-packages` job
of `.github/workflows/sdk-smoke.yml`. Include public project dependencies such as FileSystem
and FileSystem.Physical. A libraries-only local install prunes the canonical library archives;
prepare the canonical closure after that install.

Run the project explicitly:

```powershell
dotnet test sdks/Assimalign.Cohesion.Sdk.Gateway/Tasks/tests/Assimalign.Cohesion.Sdk.Gateway.Tests.csproj
```

The real image-gather tests require canonical `linux-arm64` runtime packs for
`Assimalign.Cohesion.App`, `App.Web`, and `App.Database`, plus their targeting and host runtime
packs. The fixtures disable transitive framework downloads so unrelated area runtime packs are
not required. Debug publishing produces an OCI archive without a daemon and verifies both the
member/index `linux/arm64` platform and the published apphost's ELF AArch64 machine header.
The Microsoft runtime/apphost packs still need to be available through NuGet or a NuGet fallback
package folder. Provider-selection tests execute generated code with isolated child-process
environment variables; they do not modify the test runner's environment.
