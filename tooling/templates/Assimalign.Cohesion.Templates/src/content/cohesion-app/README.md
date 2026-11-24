# `cohesion-app` — one repo, an API and a database

`Acme.Api` and `Acme.Database` are ordinary executables with a `Program.cs`. `Acme.Api` reads its
database wire endpoint from `Database:Endpoint`.

Each project's `Properties/launchSettings.json` selects environment `Local` for developer-machine
runs. Development uses strict deployed security. Use `dotnet run --no-launch-profile` when selecting
an environment through shell variables so launch settings do not override them.

```bash
dotnet run --project Acme.Database   # the database
dotnet run --project Acme.Api        # the API
```

Set the organization feed owner in `nuget.config`.
