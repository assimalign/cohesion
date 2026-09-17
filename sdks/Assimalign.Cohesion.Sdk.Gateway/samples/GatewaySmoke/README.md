# Sdk.Gateway smoke sample

This sample has one Web resource, one Database resource, and a Gateway that directly
references only Web; Web's resource reference brings Database into the transitive manifest
and in-process project closure. Prepare the exact-version package feed by following the
`gateway-packages` job in `.github/workflows/sdk-smoke.yml`, then describe the model:

```powershell
dotnet run --project Gateway/Gateway.csproj -- --mode describe --gateway local
```

The generated gateway surface supplies `Gateway.CreateBuilder(args)`, typed `Add*`
verbs, `AddAllResources()`, manifest constants, and provider selection. The Web manifest
references the Database manifest, so the described model contains the inferred edge.

Run the bounded in-process smoke path with:

```powershell
dotnet run --project Gateway/Gateway.csproj -- --gateway inprocess --smoke
```

The smoke path waits until both member hosts complete their started lifecycle phase and report
the gateway process ID. The Web member returns healthy for its first two startup/readiness
checks, then deliberately returns exactly three unhealthy first-generation liveness results.
That reaches the InProcess restart threshold; the smoke succeeds only after a second Web host
generation reaches its started lifecycle phase in the same process. The health counter is
process-static and is never reset between generations. The Web member also resolves
`Resource.References.GatewaySmokeDatabase.Db.Url` and requires its observed Database endpoint
to be loopback before it builds its host. Package-boundary CI runs this path from both the
self-contained publish and the NativeAOT-published Composite on Linux.
