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

Composing the two as one application, and deploying it, moves to the Application Model's resource
manager and fabric, which are being redesigned; this scaffold will gain that project when they land.

Set the organization feed owner in `nuget.config`.
