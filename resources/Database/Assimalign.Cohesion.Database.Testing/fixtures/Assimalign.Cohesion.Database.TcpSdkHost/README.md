# Assimalign.Cohesion.Database.TcpSdkHost

This non-packable `Sdk.Database` consumer proves that its public framework exposes the shared
`Listen(Uri)` extension to `AddKeyValue`, `AddGraph` and `AddBlob`. It has no project or package
references. The SDK supplies App and App.Database; Connections.Tcp stays runtime-private.

Run `pwsh scripts/Install-Local.ps1 -Configuration Release -SkipLibraries` from the repository
root before building the parent Database.Testing tests. The area's generated `global.json` pins
the locally packed SDK and framework version. The test project builds this fixture without
referencing its output assembly, then launches the copied self-contained apphost.

The process prints `READY|key-value-port|graph-port|blob-port` only after application startup has
completed for every server. Its parent authenticates and sends Ping with each model family,
then writes `STOP` on standard input. The fixture stops the application and prints `STOPPED`.
The lifetime and shutdown budgets are hang guards; no elapsed-time comparison decides success.

See [DESIGN.md](docs/DESIGN.md) and [OVERVIEW.md](docs/OVERVIEW.md).
