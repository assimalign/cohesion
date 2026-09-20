# Database.Blob.Client

The typed Blob wire client supports bounded uploads and downloads, properties,
deletion and prefix listing over the shared database connection pool. See
[overview](docs/OVERVIEW.md) and [design](docs/DESIGN.md) for connection ownership,
stream lifetimes and failure behavior.

Run the co-located tests by project path:

```powershell
dotnet test resources/Database/Assimalign.Cohesion.Database.Blob.Client/tests/Assimalign.Cohesion.Database.Blob.Client.Tests.csproj
```

No external service or local package feed is required. The build automatically
builds and copies the streaming fixture. Its process test launches `dotnet` with a
64 MiB heap cap and round-trips 256 MiB + 123 bytes through a file-backed engine,
the generic server, and the typed client over Connections.InMemory. The test needs
a writable temporary directory and disk space for the object and storage journal;
it removes the directory afterward. The child validates the effective memory cap,
length, SHA-256 digest, and bounded source reads.
