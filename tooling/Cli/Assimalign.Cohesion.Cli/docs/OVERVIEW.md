# Assimalign.Cohesion.Cli

`Assimalign.Cohesion.Cli` ships the `cohesion` .NET tool on net10.0. It wraps `dotnet new`,
`dotnet run` of the one gateway project below the current directory, and `dotnet publish`, and
performs IdentityHub OIDC device login.

The [tooling guide](../../../README.md) is the command reference, template roster and
local-tool-path installation guide. The [design](DESIGN.md) records contracts and boundaries.
The local solution is [Assimalign.Cohesion.Cli.slnx](../../Assimalign.Cohesion.Cli.slnx).

The executable has no public managed API or Cohesion runtime references. Its only external
package is the centrally pinned `System.Security.Cryptography.ProtectedData` BCL facade, which
protects the stored login credential. All implementation types are internal; HTTP uses
`HttpClient`, and JSON uses source-generated metadata. No DI, configuration binding or
reflection-based prompting is involved.

Tests are co-located in `../tests/` and use xUnit v2 with Shouldly. Mapping tests use an
internal process seam, HTTP tests use deterministic handlers, and filesystem fixtures stay
inside the repository's `_out/verify-40/`. No test performs real login or installs a user module.

This implements design item 40, `L01.02.01.10`, #980. The `deploy`, `trust`, `parameter` and
`status` verbs wrapped the gateway command line and local state of the generated application
model, and were removed with it on 2026-10-31.
