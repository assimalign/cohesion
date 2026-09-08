# Sdk.Gateway smoke sample

This sample has one Web resource, one Database resource, and a Gateway that references
both manifests. Prepare the exact-version package feed by following the
`gateway-packages` job in `.github/workflows/sdk-smoke.yml`, then describe the model:

```powershell
dotnet run --project Gateway/Gateway.csproj -- --mode describe --gateway local
```

The generated gateway surface supplies `Gateway.CreateBuilder(args)`, typed `Add*`
verbs, `AddAllResources()`, manifest constants, and provider selection. The Web manifest
references the Database manifest, so the described model contains the inferred edge.
