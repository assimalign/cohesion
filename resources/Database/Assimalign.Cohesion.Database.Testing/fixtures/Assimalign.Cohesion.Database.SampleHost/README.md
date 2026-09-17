# Assimalign.Cohesion.Database.SampleHost

This non-packable sample is the executable acceptance fixture for
`Assimalign.Cohesion.Database.Testing`. It is deliberately a normal
`Assimalign.Cohesion.Sdk.Database` consumer with `CohesionApplicationModel=enabled`:
the SDK generates its resource manifest and default Database control-plane registration,
while `Program.cs` composes the SQL engine, code-first schema provisioning, and TCP server
through the public Database builder surface.

The testing project references this project with `ReferenceOutputAssembly=false`. That
keeps the sample out of the test assembly's compile/runtime dependency graph while ensuring
its apphost and generated manifest exist for the real-process `LocalGateway` tests.
The versionless `resources/Database/global.template.json` is materialized as an ignored
area-level `global.json` by `Install-Local.ps1`, so the sample, its referencing tests, and
the Database solution all resolve the exact package-backed SDK version produced by that run.
