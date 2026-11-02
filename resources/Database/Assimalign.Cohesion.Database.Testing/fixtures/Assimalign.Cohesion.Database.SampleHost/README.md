# Assimalign.Cohesion.Database.SampleHost

This non-packable sample is the executable acceptance fixture for
`Assimalign.Cohesion.Database.Testing`. It is deliberately a plain
`Assimalign.Cohesion.Sdk.Database` consumer: `Program.cs` composes the SQL engine, code-first
schema provisioning, and a TCP server at `cohesion-db://127.0.0.1:5740` through the public
Database builder surface, with its data path from the `Sample:DataPath` setting. The SDK's
enabled-resource mode, which generated its manifest and control-plane registration, was turned
off on 2026-10-31 with the application-model restart (assimalign/cohesion#1759).

The testing project references this project with `ReferenceOutputAssembly=false`. That
keeps the sample out of the test assembly's compile/runtime dependency graph while ensuring
its apphost exists for the real-process tests.
The versionless `resources/Database/global.template.json` is materialized as an ignored
area-level `global.json` by `Install-Local.ps1`, so the sample, its referencing tests, and
the Database solution all resolve the exact package-backed SDK version produced by that run.
